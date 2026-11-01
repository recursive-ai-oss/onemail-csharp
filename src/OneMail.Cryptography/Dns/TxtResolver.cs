namespace RecursiveAI.OneMail.Cryptography.Dns;

/// <summary>How a TXT lookup ended.</summary>
public enum TxtLookupStatus
{
    /// <summary>The name exists and has at least one TXT record.</summary>
    Success = 0,

    /// <summary>The name does not exist (NXDOMAIN).</summary>
    NameNotFound = 1,

    /// <summary>The name exists but has no TXT record (NODATA).</summary>
    NoRecords = 2,

    /// <summary>The lookup failed in a way that may go away on retry: timeout, server failure, refusal, truncated or unusable reply.</summary>
    TemporaryFailure = 3,
}

/// <summary>The outcome of a TXT lookup: a status and, on success, one string per TXT record.</summary>
public sealed class TxtLookupResult
{
    private static readonly string[] None = [];

    /// <summary>Creates a result.</summary>
    /// <param name="status">How the lookup ended.</param>
    /// <param name="records">The TXT records; each is the concatenation of the record's character-strings (RFC 6376 §3.6.2.2) decoded as Latin-1. Null means none.</param>
    /// <param name="detail">Optional human-readable detail for failures.</param>
    public TxtLookupResult(TxtLookupStatus status, IReadOnlyList<string>? records = null, string? detail = null)
    {
        Status = status;
        Records = records ?? None;
        Detail = detail;
    }

    /// <summary>How the lookup ended.</summary>
    public TxtLookupStatus Status { get; }

    /// <summary>The TXT records, each with its character-strings joined without separators. Empty unless <see cref="Status"/> is <see cref="TxtLookupStatus.Success"/>.</summary>
    public IReadOnlyList<string> Records { get; }

    /// <summary>Why a lookup failed, when the resolver knows.</summary>
    public string? Detail { get; }
}

/// <summary>
/// Looks up TXT records. Implement it to plug in your own resolver (a caching one, DNS over HTTPS, a test double); the library
/// ships <see cref="DnsTxtClient"/> and <see cref="InMemoryTxtResolver"/>.
/// </summary>
public interface ITxtResolver
{
    /// <summary>Looks up the TXT records of a name.</summary>
    /// <param name="name">A fully qualified ASCII domain name without a trailing dot.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The outcome. Implementations report failures through <see cref="TxtLookupStatus"/> rather than by throwing; only cancellation throws.</returns>
    ValueTask<TxtLookupResult> LookupTxtAsync(string name, CancellationToken cancellationToken = default);
}

/// <summary>A resolver over a fixed table, for tests and for callers that already hold the key records.</summary>
public sealed class InMemoryTxtResolver : ITxtResolver
{
    private readonly Dictionary<string, TxtLookupResult> _table = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Sets the records of a name. Several calls for one name replace the earlier one.</summary>
    /// <param name="name">The domain name.</param>
    /// <param name="records">The TXT records, each already concatenated.</param>
    /// <returns>This resolver.</returns>
    public InMemoryTxtResolver Add(string name, params string[] records)
    {
        ArgumentNullException.ThrowIfNull(name);
        _table[name.TrimEnd('.')] = new TxtLookupResult(TxtLookupStatus.Success, records);
        return this;
    }

    /// <summary>Makes a name fail with the given status.</summary>
    /// <param name="name">The domain name.</param>
    /// <param name="status">The status to return.</param>
    /// <returns>This resolver.</returns>
    public InMemoryTxtResolver AddFailure(string name, TxtLookupStatus status)
    {
        ArgumentNullException.ThrowIfNull(name);
        _table[name.TrimEnd('.')] = new TxtLookupResult(status);
        return this;
    }

    /// <summary>The number of lookups made so far.</summary>
    public int LookupCount { get; private set; }

    /// <inheritdoc />
    public ValueTask<TxtLookupResult> LookupTxtAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LookupCount++;
        return ValueTask.FromResult(_table.TryGetValue(name.TrimEnd('.'), out TxtLookupResult? r) ? r : new TxtLookupResult(TxtLookupStatus.NameNotFound));
    }
}
