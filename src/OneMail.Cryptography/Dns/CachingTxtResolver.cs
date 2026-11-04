namespace RecursiveAI.OneMail.Cryptography.Dns;

/// <summary>Settings for <see cref="CachingTxtResolver"/>.</summary>
public sealed class CachingTxtResolverOptions
{
    /// <summary>The most names kept. Default 1,024. When full, expired entries go first, then the oldest.</summary>
    public int MaxEntries { get; init; } = 1024;

    /// <summary>The longest an answer is kept, whatever its time to live says. Default 1 hour: a revoked or rotated key should not outlive that.</summary>
    public TimeSpan MaxTtl { get; init; } = TimeSpan.FromHours(1);

    /// <summary>How long an answer is kept when the resolver gave no time to live. Default 5 minutes.</summary>
    public TimeSpan DefaultTtl { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The longest a "no such name" or "no records" answer is kept (the lesser of this and its own time to live). Default 1 minute.</summary>
    public TimeSpan MaxNegativeTtl { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>The clock. Null uses <see cref="TimeProvider.System"/>; tests pass a fake one.</summary>
    public TimeProvider? TimeProvider { get; init; }
}

/// <summary>
/// Wraps another <see cref="ITxtResolver"/> and keeps its answers for as long as their DNS time to live allows, so verifying many messages from
/// one domain looks the key up once. Pass it as <c>DkimVerifierOptions.Resolver</c>.
/// </summary>
/// <remarks>
/// A temporary failure is never kept, and an answer with a time to live of zero is not kept. Lifetimes are capped by
/// <see cref="CachingTxtResolverOptions.MaxTtl"/>. Safe for concurrent use; two simultaneous misses for one name may both reach the inner resolver.
/// </remarks>
public sealed class CachingTxtResolver : ITxtResolver
{
    private readonly ITxtResolver _inner;
    private readonly CachingTxtResolverOptions _options;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, (TxtLookupResult Result, long Expires, long Stored)> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>Creates a caching resolver.</summary>
    /// <param name="inner">The resolver to ask on a miss.</param>
    /// <param name="options">Settings, or null for the defaults.</param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> is null.</exception>
    public CachingTxtResolver(ITxtResolver inner, CachingTxtResolverOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _options = options ?? new CachingTxtResolverOptions();
        _time = _options.TimeProvider ?? TimeProvider.System;
    }

    /// <summary>The number of names currently held, expired ones included until the next lookup removes them.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Forgets everything held.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    /// <inheritdoc />
    public async ValueTask<TxtLookupResult> LookupTxtAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        string key = name.TrimEnd('.');
        long now = _time.GetTimestamp();
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var e))
            {
                if (now < e.Expires)
                {
                    return e.Result;
                }

                _entries.Remove(key);
            }
        }

        TxtLookupResult r = await _inner.LookupTxtAsync(name, cancellationToken).ConfigureAwait(false);
        if (r.Status == TxtLookupStatus.TemporaryFailure)
        {
            return r;
        }

        TimeSpan ttl = r.Ttl ?? _options.DefaultTtl;
        if (ttl > _options.MaxTtl)
        {
            ttl = _options.MaxTtl;
        }

        if (r.Status != TxtLookupStatus.Success && ttl > _options.MaxNegativeTtl)
        {
            ttl = _options.MaxNegativeTtl;
        }

        if (ttl <= TimeSpan.Zero || _options.MaxEntries <= 0)
        {
            return r;
        }

        long stored = _time.GetTimestamp();
        long expires = stored + (long)(ttl.TotalSeconds * _time.TimestampFrequency);
        lock (_gate)
        {
            if (_entries.Count >= _options.MaxEntries && !_entries.ContainsKey(key))
            {
                Evict(stored);
            }

            _entries[key] = (r, expires, stored);
        }

        return r;
    }

    // Called with the gate held and the table full: expired entries go; if none was expired, the oldest one.
    private void Evict(long now)
    {
        List<string>? gone = null;
        string? oldest = null;
        long oldestStored = long.MaxValue;
        foreach (KeyValuePair<string, (TxtLookupResult Result, long Expires, long Stored)> kv in _entries)
        {
            if (kv.Value.Expires <= now)
            {
                (gone ??= []).Add(kv.Key);
            }
            else if (kv.Value.Stored < oldestStored)
            {
                oldestStored = kv.Value.Stored;
                oldest = kv.Key;
            }
        }

        if (gone is not null)
        {
            foreach (string k in gone)
            {
                _entries.Remove(k);
            }
        }
        else if (oldest is not null)
        {
            _entries.Remove(oldest);
        }
    }
}
