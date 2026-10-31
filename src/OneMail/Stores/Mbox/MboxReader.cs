using System.Buffers;
using System.Runtime.CompilerServices;
using RecursiveAI.OneMail.Sources;

namespace RecursiveAI.OneMail.Stores.Mbox;

/// <summary>
/// Splits an mbox file into messages without reading message bodies into memory: it yields <see cref="MboxEntry"/> values
/// (byte ranges plus notes) that the MIME layer parses on demand.
/// </summary>
/// <remarks>
/// <para>
/// A message starts at an envelope line (<c>From </c>, sender, date; see <see cref="MboxFromLineMode"/>) and ends at the
/// next one, or where a valid <c>Content-Length</c> says (see <see cref="MboxReaderOptions.Variant"/>). The empty line
/// writers add after a message belongs to the separator, not the message. Bytes before the first envelope line are the
/// <see cref="PreambleLength"/>. Records tile the file, so nothing is lost by reading and rewriting entries.
/// </para>
/// <para>
/// The reader holds one pooled window of <see cref="MboxReaderOptions.BufferSize"/> bytes and allocates nothing per
/// message; entries are structs. A <c>Content-Length</c>-delimited message is skipped without being scanned. The
/// reader is not thread-safe. It does not dispose the source.
/// </para>
/// </remarks>
public sealed class MboxReader : IDisposable
{
    private const int MinBuffer = 16384;
    private static ReadOnlySpan<byte> FromText => "From "u8;
    private static ReadOnlySpan<byte> FromAfterNewline => "\nFrom "u8;

    private readonly IRandomAccessSource _source;
    private readonly MboxReaderOptions _options;
    private readonly long _length;
    private readonly int _maxEnvelope;
    private readonly int _maxHeader;
    private byte[]? _buf;
    private long _winStart;
    private int _winLen;
    private long _next = -1;
    private bool _started;
    private bool _done;
    private int _index;
    private StoreDefects _cur;

    /// <summary>Creates a reader over a source. Nothing is read until <see cref="Read"/> or <see cref="ReadAsync"/>.</summary>
    /// <param name="source">The mbox bytes. The reader does not dispose it.</param>
    /// <param name="options">Options; null means the defaults.</param>
    public MboxReader(IRandomAccessSource source, MboxReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _options = options ?? new MboxReaderOptions();
        _length = source.Length;
        int size = Math.Max(MinBuffer, _options.BufferSize);
        _buf = ArrayPool<byte>.Shared.Rent(size);
        _bufLen = size;
        _maxEnvelope = Math.Clamp(_options.MaxEnvelopeLineBytes, 64, size / 2);
        _maxHeader = Math.Clamp(_options.MaxHeaderBytes, 1024, size);
    }

    private readonly int _bufLen;

    /// <summary>The message the last successful <see cref="Read"/> positioned on.</summary>
    public MboxEntry Current { get; private set; }

    /// <summary>The number of bytes before the first envelope line. Valid once the first <see cref="Read"/> has returned.</summary>
    public long PreambleLength { get; private set; }

    /// <summary>The options this reader was created with.</summary>
    public MboxReaderOptions Options => _options;

    /// <summary>Moves to the next message.</summary>
    /// <returns>False when there are no more messages.</returns>
    /// <exception cref="ObjectDisposedException">The reader was disposed.</exception>
    /// <exception cref="NotSupportedException">The source is asynchronous only; use <see cref="ReadAsync"/>.</exception>
    public bool Read() => SyncWait.Result(ReadCore<SyncMode>(default));

    /// <summary>Asynchronous twin of <see cref="Read"/>.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>False when there are no more messages.</returns>
    public ValueTask<bool> ReadAsync(CancellationToken cancellationToken = default) => ReadCore<AsyncMode>(cancellationToken);

    /// <summary>Enumerates the messages of a source. The enumeration owns a reader and releases it when it ends.</summary>
    /// <param name="source">The mbox bytes; not disposed.</param>
    /// <param name="options">Options; null means the defaults.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>The entries in file order.</returns>
    public static async IAsyncEnumerable<MboxEntry> EnumerateAsync(IRandomAccessSource source, MboxReaderOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using MboxReader reader = new(source, options);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return reader.Current;
        }
    }

    /// <summary>Synchronous enumeration of the messages of a source with a synchronous read path.</summary>
    /// <param name="source">The mbox bytes; not disposed.</param>
    /// <param name="options">Options; null means the defaults.</param>
    /// <returns>The entries in file order.</returns>
    public static IEnumerable<MboxEntry> Enumerate(IRandomAccessSource source, MboxReaderOptions? options = null)
    {
        using MboxReader reader = new(source, options);
        while (reader.Read())
        {
            yield return reader.Current;
        }
    }

    /// <summary>Releases the pooled window.</summary>
    public void Dispose()
    {
        byte[]? b = _buf;
        _buf = null;
        if (b is not null)
        {
            ArrayPool<byte>.Shared.Return(b);
        }
    }

    // ---- Reading messages and envelopes ----

    /// <summary>
    /// Opens the message bytes of an entry as a lazy, forward-only stream.
    /// </summary>
    /// <param name="entry">An entry of this file.</param>
    /// <param name="unquote">
    /// True (the default) removes the quoting the variant adds to body lines (<c>&gt;From </c> becomes <c>From </c>) so the
    /// stream carries the message as it was written. With <see cref="MboxVariant.Unknown"/> the entries delimited by a
    /// <c>Content-Length</c> are returned as stored, since whether they were quoted is not known; the others are
    /// unquoted as mboxrd. False returns the stored bytes.
    /// </param>
    /// <returns>A readable stream; dispose it when done. The source must outlive it.</returns>
    public Stream OpenMessage(in MboxEntry entry, bool unquote = true)
    {
        if (!unquote || !TryGetQuoter(entry, out MboxQuoter q))
        {
            return _source.OpenRead(entry.MessageOffset, entry.MessageLength);
        }

        return new UnquotingStream(_source, entry.MessageOffset, entry.MessageLength, q);
    }

    /// <summary>Reads a message into memory. See <see cref="OpenMessage"/> for <paramref name="unquote"/>.</summary>
    /// <param name="entry">An entry of this file.</param>
    /// <param name="unquote">Whether to remove quoting.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The message bytes.</returns>
    /// <exception cref="InvalidDataException">The message is larger than 2 GiB.</exception>
    public async ValueTask<byte[]> ReadMessageAsync(MboxEntry entry, bool unquote = true, CancellationToken cancellationToken = default)
    {
        if (entry.MessageLength > Array.MaxLength)
        {
            throw new InvalidDataException("The message is too large to hold in an array.");
        }

        await using Stream s = OpenMessage(entry, unquote);
        using MemoryStream ms = new((int)entry.MessageLength);
        await s.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        return ms.ToArray();
    }

    /// <summary>Synchronous twin of <see cref="ReadMessageAsync"/>.</summary>
    /// <param name="entry">An entry of this file.</param>
    /// <param name="unquote">Whether to remove quoting.</param>
    /// <returns>The message bytes.</returns>
    public byte[] ReadMessage(in MboxEntry entry, bool unquote = true)
    {
        if (entry.MessageLength > Array.MaxLength)
        {
            throw new InvalidDataException("The message is too large to hold in an array.");
        }

        using Stream s = OpenMessage(entry, unquote);
        using MemoryStream ms = new((int)entry.MessageLength);
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Reads and parses the envelope line of an entry.</summary>
    /// <param name="entry">An entry of this file.</param>
    /// <returns>The sender and date; the sender is empty when the line has none.</returns>
    public MboxEnvelope ReadEnvelope(in MboxEntry entry)
    {
        int n = (int)Math.Min(entry.EnvelopeLength, 8192);
        byte[] b = new byte[n];
        n = _source.ReadFully(entry.Offset, b);
        return MboxEnvelope.Parse(b.AsSpan(0, n));
    }

    /// <summary>Asynchronous twin of <see cref="ReadEnvelope"/>.</summary>
    /// <param name="entry">An entry of this file.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The sender and date.</returns>
    public async ValueTask<MboxEnvelope> ReadEnvelopeAsync(MboxEntry entry, CancellationToken cancellationToken = default)
    {
        int n = (int)Math.Min(entry.EnvelopeLength, 8192);
        byte[] b = new byte[n];
        n = await _source.ReadFullyAsync(entry.Offset, b, cancellationToken).ConfigureAwait(false);
        return MboxEnvelope.Parse(b.AsSpan(0, n));
    }

    private bool TryGetQuoter(in MboxEntry entry, out MboxQuoter q)
    {
        switch (_options.Variant)
        {
            case MboxVariant.Mboxo:
            case MboxVariant.Mboxcl:
                q = new MboxQuoter(quote: false, reversible: false);
                return true;
            case MboxVariant.Mboxcl2:
                q = default;
                return false;
            case MboxVariant.Unknown when entry.EndedByContentLength:
                q = default;
                return false;
            default:
                q = new MboxQuoter(quote: false, reversible: true);
                return true;
        }
    }

    // ---- The scanner ----
    // One implementation serves both the synchronous and the asynchronous entry point: TMode says which read to call,
    // and the JIT removes the unused branch. When the source answers synchronously nothing is allocated.

    private interface IMode
    {
        static abstract bool IsAsync { get; }
    }

    private readonly struct SyncMode : IMode
    {
        public static bool IsAsync => false;
    }

    private readonly struct AsyncMode : IMode
    {
        public static bool IsAsync => true;
    }

    private void Report(StoreDefects kind, long offset)
    {
        _cur |= kind;
        _options.DefectHandler?.Invoke(new StoreDefect(kind, offset));
    }

    private ReadOnlySpan<byte> Window(long pos)
    {
        byte[] b = _buf ?? throw new ObjectDisposedException(nameof(MboxReader));
        return b.AsSpan((int)(pos - _winStart), (int)(_winStart + _winLen - pos));
    }

    // Makes the window hold [pos, pos + need), or everything to the end of the source if that is nearer.
    private ValueTask Ensure<TMode>(long pos, int need, CancellationToken ct)
        where TMode : struct, IMode
    {
        ObjectDisposedException.ThrowIf(_buf is null, this);

        long end = _winStart + _winLen;
        if (pos >= _winStart && pos <= end && (pos + need <= end || end >= _length))
        {
            return default;
        }

        return Load<TMode>(pos, ct);
    }

    private async ValueTask Load<TMode>(long pos, CancellationToken ct)
        where TMode : struct, IMode
    {
        byte[] b = _buf ?? throw new ObjectDisposedException(nameof(MboxReader));
        int want = (int)Math.Min(_bufLen, Math.Max(0, _length - pos));
        int got = TMode.IsAsync
            ? await _source.ReadFullyAsync(pos, b.AsMemory(0, want), ct).ConfigureAwait(false)
            : _source.ReadFully(pos, b.AsSpan(0, want));
        _winStart = pos;
        _winLen = got;
    }

    private async ValueTask<bool> ReadCore<TMode>(CancellationToken ct)
        where TMode : struct, IMode
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_buf is null, this);

        if (_done)
        {
            return false;
        }

        if (!_started)
        {
            _started = true;
            _cur = StoreDefects.None;
            long first = _length == 0 ? -1 : await FindFrom<TMode>(0, ct).ConfigureAwait(false);
            if (first < 0)
            {
                PreambleLength = _length;
                if (_length > 0)
                {
                    Report(StoreDefects.PreambleBeforeFirstMessage, 0);
                }

                _done = true;
                return false;
            }

            PreambleLength = first;
            if (first > 0)
            {
                Report(StoreDefects.PreambleBeforeFirstMessage, 0);
            }

            _next = first;
        }

        long f = _next;
        _cur = StoreDefects.None;

        // The envelope line.
        await Ensure<TMode>(f, _maxEnvelope + 1, ct).ConfigureAwait(false);
        long envLen;
        {
            ReadOnlySpan<byte> w = Window(f);
            int lim = Math.Min(w.Length, _maxEnvelope + 1);
            int nl = w[..lim].IndexOf((byte)'\n');
            if (nl >= 0)
            {
                envLen = nl + 1;
            }
            else if (f + lim >= _length)
            {
                envLen = lim;
            }
            else
            {
                long le = await FindLineEnd<TMode>(f + lim, ct).ConfigureAwait(false);
                envLen = le - f;
                Report(StoreDefects.OverlongEnvelopeLine, f);
            }
        }

        long s = f + envLen;
        long next = -1;
        long clUsed = -1;

        if (_options.Variant is MboxVariant.Unknown or MboxVariant.Mboxcl or MboxVariant.Mboxcl2)
        {
            (long n, long cl) = await TryContentLength<TMode>(s, ct).ConfigureAwait(false);
            if (n >= 0)
            {
                next = n;
                clUsed = cl;
            }
        }

        if (next < 0)
        {
            next = await FindFrom<TMode>(s, ct).ConfigureAwait(false);
            if (next < 0)
            {
                next = _length;
            }
        }

        // The separator is one empty line before the next envelope line (or the end of the file), LF or CRLF.
        int sep = 0;
        long lo = Math.Max(f, next - 3);
        if (next > lo)
        {
            await Ensure<TMode>(lo, (int)(next - lo), ct).ConfigureAwait(false);
            ReadOnlySpan<byte> w = Window(lo)[..(int)(next - lo)];
            if (w[^1] == (byte)'\n')
            {
                if (w.Length >= 2 && w[^2] == (byte)'\n')
                {
                    sep = 1;
                }
                else if (w.Length >= 3 && w[^2] == (byte)'\r' && w[^3] == (byte)'\n')
                {
                    sep = 2;
                }
            }

            if (next - sep < s)
            {
                sep = 0;
            }
        }

        if (sep == 0 && next < _length)
        {
            Report(StoreDefects.MissingSeparatorLine, next);
        }

        Current = new MboxEntry(_index++, f, envLen, next - sep - s, next - f, clUsed, _cur);
        if (next >= _length)
        {
            _done = true;
        }
        else
        {
            _next = next;
        }

        return true;
    }

    // Offset after the next line feed at or after pos, or the end of the source.
    private async ValueTask<long> FindLineEnd<TMode>(long pos, CancellationToken ct)
        where TMode : struct, IMode
    {
        while (pos < _length)
        {
            await Ensure<TMode>(pos, 1, ct).ConfigureAwait(false);
            ReadOnlySpan<byte> w = Window(pos);
            if (w.Length == 0)
            {
                return _length;
            }

            int nl = w.IndexOf((byte)'\n');
            if (nl >= 0)
            {
                return pos + nl + 1;
            }

            pos += w.Length;
        }

        return _length;
    }

    // Offset of the first envelope line at or after lineStart (which must start a line), or -1.
    private async ValueTask<long> FindFrom<TMode>(long lineStart, CancellationToken ct)
        where TMode : struct, IMode
    {
        long p = lineStart;
        bool checkAtP = true;
        while (p < _length)
        {
            await Ensure<TMode>(p, FromText.Length + 1, ct).ConfigureAwait(false);
            ReadOnlySpan<byte> w = Window(p);
            if (w.Length == 0)
            {
                return -1;
            }

            long cand = -1;
            if (checkAtP)
            {
                checkAtP = false;
                if (w.StartsWith(FromText))
                {
                    cand = p;
                }
            }

            if (cand < 0)
            {
                // An envelope line starts at a line start, that is, right after a line feed.
                int idx = w.IndexOf(FromAfterNewline);
                if (idx >= 0)
                {
                    cand = p + idx + 1;
                }
                else
                {
                    long end = _winStart + _winLen;
                    if (end >= _length)
                    {
                        return -1;
                    }

                    // Keep five bytes so "\nFrom " split across two windows is still found.
                    p = end - (FromAfterNewline.Length - 1);
                    continue;
                }
            }

            if (await IsDelimiter<TMode>(cand, report: true, ct).ConfigureAwait(false))
            {
                return cand;
            }

            p = cand;
        }

        return -1;
    }

    // Whether the line at cand (known to start with "From ") begins a message under the reader's mode.
    private async ValueTask<bool> IsDelimiter<TMode>(long cand, bool report, CancellationToken ct)
        where TMode : struct, IMode
    {
        if (_options.FromLineMode == MboxFromLineMode.Any)
        {
            return true;
        }

        await Ensure<TMode>(cand, _maxEnvelope + 1, ct).ConfigureAwait(false);
        ReadOnlySpan<byte> w = Window(cand);
        int lim = Math.Min(w.Length, _maxEnvelope + 1);
        int nl = w[..lim].IndexOf((byte)'\n');
        bool ok;
        if (nl >= 0)
        {
            ok = MboxFromLine.IsEnvelope(w[..nl]);
        }
        else
        {
            ok = cand + lim >= _length && lim <= _maxEnvelope && MboxFromLine.IsEnvelope(w[..lim]);
        }

        if (!ok && report)
        {
            Report(StoreDefects.AmbiguousFromLine, cand);
        }

        return ok;
    }

    // Looks through the header block at s for Content-Length. Returns the offset of the next envelope line (or the end of
    // the source) and the length when it checks out; (-1, -1) otherwise.
    private async ValueTask<(long Next, long Length)> TryContentLength<TMode>(long s, CancellationToken ct)
        where TMode : struct, IMode
    {
        long pos = s;
        long cl = -1;
        long headerEnd = -1;
        bool bad = false;
        long scanned = 0;
        while (pos < _length)
        {
            await Ensure<TMode>(pos, 4096, ct).ConfigureAwait(false);
            ReadOnlySpan<byte> w = Window(pos);
            if (w.Length == 0)
            {
                break;
            }

            if (w[0] == (byte)'\n' || (w.Length >= 2 && w[0] == (byte)'\r' && w[1] == (byte)'\n'))
            {
                headerEnd = pos + (w[0] == (byte)'\n' ? 1 : 2);
                break;
            }

            if (w.StartsWith(FromText))
            {
                break;
            }

            int nl = w.IndexOf((byte)'\n');
            long lineLen;
            if (w.Length >= 15 && IsContentLengthName(w))
            {
                ReadOnlySpan<byte> line = nl >= 0 ? w[..nl] : w;
                if (TryParseLength(line[15..], out long v))
                {
                    if (cl < 0)
                    {
                        cl = v;
                    }
                    else if (cl != v)
                    {
                        Report(StoreDefects.AmbiguousContentLength, pos);
                    }
                }
                else
                {
                    bad = true;
                }
            }

            if (nl >= 0)
            {
                lineLen = nl + 1;
            }
            else
            {
                lineLen = await FindLineEnd<TMode>(pos, ct).ConfigureAwait(false) - pos;
            }

            pos += lineLen;
            scanned += lineLen;
            if (scanned > _maxHeader)
            {
                break;
            }
        }

        if (cl < 0 && !bad)
        {
            return (-1, -1);
        }

        if (bad || headerEnd < 0 || cl > _length - headerEnd)
        {
            Report(StoreDefects.ContentLengthMismatch, s);
            return (-1, -1);
        }

        long b = headerEnd + cl;
        if (b == _length)
        {
            return (b, cl);
        }

        await Ensure<TMode>(b, 2, ct).ConfigureAwait(false);
        ReadOnlySpan<byte> t = Window(b);
        int blank = t.Length >= 1 && t[0] == (byte)'\n' ? 1 : t.Length >= 2 && t[0] == (byte)'\r' && t[1] == (byte)'\n' ? 2 : 0;
        long b2 = b + blank;
        if (b2 == _length)
        {
            if (blank > 0)
            {
                return (b2, cl);
            }
        }
        else if (b2 < _length)
        {
            await Ensure<TMode>(b2, FromText.Length, ct).ConfigureAwait(false);
            if (Window(b2).StartsWith(FromText) && await IsDelimiter<TMode>(b2, report: false, ct).ConfigureAwait(false))
            {
                return (b2, cl);
            }
        }

        Report(StoreDefects.ContentLengthMismatch, s);
        return (-1, -1);
    }

    private static bool IsContentLengthName(ReadOnlySpan<byte> w)
    {
        ReadOnlySpan<byte> name = "content-length:"u8;
        for (int i = 0; i < name.Length; i++)
        {
            byte c = w[i];
            if (c is >= (byte)'A' and <= (byte)'Z')
            {
                c += 32;
            }

            if (c != name[i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseLength(ReadOnlySpan<byte> v, out long value)
    {
        value = 0;
        int i = 0;
        while (i < v.Length && (v[i] == (byte)' ' || v[i] == (byte)'\t'))
        {
            i++;
        }

        int digits = 0;
        while (i < v.Length && v[i] is >= (byte)'0' and <= (byte)'9')
        {
            if (digits++ >= 18)
            {
                return false;
            }

            value = (value * 10) + (v[i] - (byte)'0');
            i++;
        }

        while (i < v.Length && (v[i] == (byte)' ' || v[i] == (byte)'\t' || v[i] == (byte)'\r'))
        {
            i++;
        }

        return digits > 0 && i == v.Length;
    }

    // A forward-only stream that removes mbox quoting from a range of the source.
    private sealed class UnquotingStream : Stream
    {
        private const int Chunk = 16384;
        private readonly IRandomAccessSource _source;
        private readonly long _end;
        private long _pos;
        private MboxQuoter _quoter;
        private readonly byte[] _in = ArrayPool<byte>.Shared.Rent(Chunk);
        private readonly byte[] _out = ArrayPool<byte>.Shared.Rent(MboxQuoter.MaxOutput(Chunk));
        private int _outPos;
        private int _outLen;
        private bool _finished;
        private bool _disposed;

        public UnquotingStream(IRandomAccessSource source, long offset, long length, MboxQuoter quoter)
        {
            _source = source;
            _pos = offset;
            _end = offset + length;
            _quoter = quoter;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            while (_outPos == _outLen)
            {
                if (_finished)
                {
                    return 0;
                }

                int n = _pos < _end ? _source.Read(_pos, _in.AsSpan(0, (int)Math.Min(Chunk, _end - _pos))) : 0;
                Feed(n);
            }

            return Serve(buffer);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_outPos == _outLen)
            {
                if (_finished)
                {
                    return 0;
                }

                int n = _pos < _end ? await _source.ReadAsync(_pos, _in.AsMemory(0, (int)Math.Min(Chunk, _end - _pos)), cancellationToken).ConfigureAwait(false) : 0;
                Feed(n);
            }

            return Serve(buffer.Span);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private void Feed(int n)
        {
            _outPos = 0;
            if (n <= 0)
            {
                _outLen = _quoter.Finish(_out);
                _finished = true;
                return;
            }

            _pos += n;
            _outLen = _quoter.Process(_in.AsSpan(0, n), _out);
        }

        private int Serve(Span<byte> buffer)
        {
            int take = Math.Min(buffer.Length, _outLen - _outPos);
            _out.AsSpan(_outPos, take).CopyTo(buffer);
            _outPos += take;
            return take;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                ArrayPool<byte>.Shared.Return(_in);
                ArrayPool<byte>.Shared.Return(_out);
            }

            base.Dispose(disposing);
        }
    }
}
