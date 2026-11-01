using System.Buffers;

namespace RecursiveAI.OneMail.Client.Protocol;

/// <summary>
/// The buffered reader every protocol parser reads from: lines, raw bytes and bounded streams over one pooled buffer.
/// </summary>
/// <remarks>
/// Memory returned by the line methods points into the buffer and is valid only until the next call on the reader. A line ends
/// at LF; a CR before it is part of the terminator. Not thread-safe.
/// </remarks>
public sealed class ProtocolReader : IDisposable
{
    private const int InitialSize = 16 * 1024;

    private readonly ProtocolConnection _connection;
    private byte[] _buf = ArrayPool<byte>.Shared.Rent(InitialSize);
    private int _start;
    private int _end;

    internal ProtocolReader(ProtocolConnection connection)
    {
        _connection = connection;
    }

    /// <summary>Bytes read from the connection but not yet consumed.</summary>
    public int Buffered => _end - _start;

    /// <summary>The limits in force.</summary>
    public ProtocolLimits Limits => _connection.Options.Limits;

    /// <summary>Total bytes returned to callers so far (lines, terminators, and raw reads). Used by parsers for offsets.</summary>
    public long Consumed { get; private set; }

    /// <summary>Reads one line without its terminator.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The line, valid until the next call. Whether the terminator was a bare LF is available from <see cref="LastLineWasBareLf"/>.</returns>
    /// <exception cref="MailConnectionClosedException">The connection ended before a line end.</exception>
    /// <exception cref="MailProtocolLimitException">The line is longer than <see cref="ProtocolLimits.MaxLineLength"/>.</exception>
    public async ValueTask<ReadOnlyMemory<byte>> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        ReadOnlyMemory<byte> raw = await ReadRawLineCore(false, cancellationToken).ConfigureAwait(false);
        return StripEnd(raw);
    }

    /// <summary>Synchronous twin of <see cref="ReadLineAsync"/>.</summary>
    /// <returns>The line without its terminator.</returns>
    public ReadOnlyMemory<byte> ReadLine() => StripEnd(SyncBridge.Run(ReadRawLineCore(true, default)));

    /// <summary>Reads one line including its terminator (CRLF, LF, or none at the end of the stream is an error).</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The line with its terminator, valid until the next call.</returns>
    public ValueTask<ReadOnlyMemory<byte>> ReadRawLineAsync(CancellationToken cancellationToken = default) => ReadRawLineCore(false, cancellationToken);

    /// <summary>Synchronous twin of <see cref="ReadRawLineAsync"/>.</summary>
    /// <returns>The line with its terminator.</returns>
    public ReadOnlyMemory<byte> ReadRawLine() => SyncBridge.Run(ReadRawLineCore(true, default));

    /// <summary>Whether the last line returned by <see cref="ReadLine"/> or <see cref="ReadLineAsync"/> ended in LF without CR.</summary>
    public bool LastLineWasBareLf { get; private set; }

    private ReadOnlyMemory<byte> StripEnd(ReadOnlyMemory<byte> raw)
    {
        int len = raw.Length - 1; // the LF
        if (len > 0 && raw.Span[len - 1] == (byte)'\r')
        {
            LastLineWasBareLf = false;
            return raw[..(len - 1)];
        }

        LastLineWasBareLf = true;
        return raw[..len];
    }

    private async ValueTask<ReadOnlyMemory<byte>> ReadRawLineCore(bool sync, CancellationToken ct)
    {
        int scanned = 0;
        int max = Limits.MaxLineLength;
        while (true)
        {
            int avail = _end - _start;
            int i = _buf.AsSpan(_start + scanned, avail - scanned).IndexOf((byte)'\n');
            if (i >= 0)
            {
                int len = scanned + i + 1;
                var mem = new ReadOnlyMemory<byte>(_buf, _start, len);
                _start += len;
                Consumed += len;
                return mem;
            }

            scanned = avail;
            if (scanned > max + 1)
            {
                throw new MailProtocolLimitException("A line is longer than " + max + " bytes.");
            }

            int n = await FillAsync(sync, ct).ConfigureAwait(false);
            if (n == 0)
            {
                throw new MailConnectionClosedException(scanned == 0 ? "The connection was closed." : "The connection was closed in the middle of a line.");
            }
        }
    }

    /// <summary>Reads up to <c>destination.Length</c> bytes; fewer when that is what is available. Zero means the end of the connection.</summary>
    /// <param name="destination">Where the bytes go.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The number of bytes read.</returns>
    public ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default) => ReadCore(destination, false, cancellationToken);

    /// <summary>Synchronous twin of <see cref="ReadAsync"/>.</summary>
    /// <param name="destination">Where the bytes go.</param>
    /// <returns>The number of bytes read.</returns>
    public int Read(Span<byte> destination)
    {
        int avail = _end - _start;
        if (avail > 0)
        {
            int n = Math.Min(avail, destination.Length);
            _buf.AsSpan(_start, n).CopyTo(destination);
            _start += n;
            Consumed += n;
            return n;
        }

        if (destination.Length == 0)
        {
            return 0;
        }

        int got = SyncBridge.Run(FillAsync(true, default));
        if (got == 0)
        {
            return 0;
        }

        return Read(destination);
    }

    private async ValueTask<int> ReadCore(Memory<byte> destination, bool sync, CancellationToken ct)
    {
        int avail = _end - _start;
        if (avail == 0 && destination.Length > 0)
        {
            if (await FillAsync(sync, ct).ConfigureAwait(false) == 0)
            {
                return 0;
            }

            avail = _end - _start;
        }

        int n = Math.Min(avail, destination.Length);
        _buf.AsMemory(_start, n).CopyTo(destination);
        _start += n;
        Consumed += n;
        return n;
    }

    /// <summary>Opens a stream over the next <paramref name="length"/> bytes. It must be read or <see cref="LiteralStream.SkipAsync"/>ped to the end before the reader is used again.</summary>
    /// <param name="length">The exact number of bytes.</param>
    /// <returns>The stream.</returns>
    public LiteralStream OpenLiteral(long length) => new(this, length);

    /// <summary>Discards bytes without copying them out.</summary>
    /// <param name="count">How many.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="MailConnectionClosedException">The connection ended first.</exception>
    public async ValueTask SkipAsync(long count, CancellationToken cancellationToken = default)
    {
        while (count > 0)
        {
            if (_end == _start && await FillAsync(false, cancellationToken).ConfigureAwait(false) == 0)
            {
                throw new MailConnectionClosedException("The connection was closed in the middle of a literal.");
            }

            int n = (int)Math.Min(count, _end - _start);
            _start += n;
            Consumed += n;
            count -= n;
        }
    }

    /// <summary>Synchronous twin of <see cref="SkipAsync"/>.</summary>
    /// <param name="count">How many.</param>
    public void Skip(long count)
    {
        while (count > 0)
        {
            if (_end == _start && SyncBridge.Run(FillAsync(true, default)) == 0)
            {
                throw new MailConnectionClosedException("The connection was closed in the middle of a literal.");
            }

            int n = (int)Math.Min(count, _end - _start);
            _start += n;
            Consumed += n;
            count -= n;
        }
    }

    private async ValueTask<int> FillAsync(bool sync, CancellationToken ct)
    {
        if (_start == _end)
        {
            _start = _end = 0;
        }

        if (_end == _buf.Length)
        {
            if (_start > 0)
            {
                Buffer.BlockCopy(_buf, _start, _buf, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }
            else
            {
                byte[] bigger = ArrayPool<byte>.Shared.Rent(Math.Min(_buf.Length * 2, Math.Max(_buf.Length + 1, Limits.MaxLineLength + 4096)));
                Buffer.BlockCopy(_buf, 0, bigger, 0, _end);
                ArrayPool<byte>.Shared.Return(_buf);
                _buf = bigger;
            }
        }

        int n;
        Stream s = _connection.Stream;
        if (sync)
        {
            _connection.SetSocketTimeouts();
            n = s.Read(_buf, _end, _buf.Length - _end);
        }
        else
        {
            using CancellationTokenSource cts = ProtocolConnection.TimeoutSource(_connection.ReadTimeout, ct);
            try
            {
                n = await s.ReadAsync(_buf.AsMemory(_end), cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("The server did not send anything within " + _connection.ReadTimeout + ".");
            }
        }

        if (n > 0)
        {
            _connection.Options.Trace?.Received(_buf.AsSpan(_end, n));
            _end += n;
        }

        return n;
    }

    /// <summary>Returns the buffer to the pool.</summary>
    public void Dispose()
    {
        byte[] b = _buf;
        _buf = [];
        _start = _end = 0;
        if (b.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(b);
        }
    }

    /// <summary>A read-only, non-seekable stream over the next N bytes of a <see cref="ProtocolReader"/> (an IMAP literal, a POP3 body).</summary>
    public sealed class LiteralStream : Stream
    {
        private readonly ProtocolReader _reader;
        private long _remaining;

        internal LiteralStream(ProtocolReader reader, long length)
        {
            _reader = reader;
            _remaining = length;
            Length = length;
        }

        /// <summary>Bytes not read yet.</summary>
        public long Remaining => _remaining;

        /// <inheritdoc />
        public override bool CanRead => true;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => false;

        /// <summary>The announced size.</summary>
        public override long Length { get; }

        /// <inheritdoc />
        public override long Position
        {
            get => Length - _remaining;
            set => throw new NotSupportedException();
        }

        /// <inheritdoc />
        public override void Flush() { }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        /// <inheritdoc />
        public override int Read(Span<byte> buffer)
        {
            if (_remaining == 0 || buffer.Length == 0)
            {
                return 0;
            }

            int n = _reader.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
            if (n == 0)
            {
                throw new MailConnectionClosedException("The connection was closed in the middle of a literal.");
            }

            _remaining -= n;
            return n;
        }

        /// <inheritdoc />
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining == 0 || buffer.Length == 0)
            {
                return 0;
            }

            int n = await _reader.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                throw new MailConnectionClosedException("The connection was closed in the middle of a literal.");
            }

            _remaining -= n;
            return n;
        }

        /// <summary>Discards what is left, leaving the reader just after the literal.</summary>
        /// <param name="cancellationToken">Cancels the read.</param>
        /// <returns>A task that completes when the reader is positioned after the literal.</returns>
        public async ValueTask SkipAsync(CancellationToken cancellationToken = default)
        {
            long n = _remaining;
            _remaining = 0;
            await _reader.SkipAsync(n, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Synchronous twin of <see cref="SkipAsync"/>.</summary>
        public void Skip()
        {
            long n = _remaining;
            _remaining = 0;
            _reader.Skip(n);
        }

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
