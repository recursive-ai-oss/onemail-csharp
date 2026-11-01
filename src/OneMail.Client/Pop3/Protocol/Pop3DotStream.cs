using RecursiveAI.OneMail.Client.Protocol;

namespace RecursiveAI.OneMail.Client.Pop3.Protocol;

/// <summary>
/// A read-only stream over a POP3 multi-line response body (RFC 1939 §3): lines as sent, a leading period removed (byte-stuffing), ended by the
/// line that holds only a period. The bytes of each line, including its line end, are delivered unchanged, so the body can go straight to
/// <c>MimeReader</c>. It must be read to its end (or <see cref="DrainAsync"/>ed) before the next command.
/// </summary>
public sealed class Pop3DotStream : Stream
{
    private readonly ProtocolReader _reader;
    private ReadOnlyMemory<byte> _line;
    private int _linePos;
    private bool _done;

    internal Pop3DotStream(ProtocolReader reader)
    {
        _reader = reader;
    }

    /// <summary>Whether the terminating line has been read.</summary>
    public bool IsComplete => _done && _linePos >= _line.Length;

    /// <summary>The number of bytes delivered so far.</summary>
    public long BytesRead { get; private set; }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    /// <inheritdoc />
    public override void Flush() { }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0)
        {
            return 0;
        }

        while (_linePos >= _line.Length)
        {
            if (_done || !Accept(_reader.ReadRawLine()))
            {
                return 0;
            }
        }

        return Take(buffer);
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0)
        {
            return 0;
        }

        while (_linePos >= _line.Length)
        {
            if (_done || !Accept(await _reader.ReadRawLineAsync(cancellationToken).ConfigureAwait(false)))
            {
                return 0;
            }
        }

        return Take(buffer.Span);
    }

    private int Take(Span<byte> destination)
    {
        int n = Math.Min(destination.Length, _line.Length - _linePos);
        _line.Span.Slice(_linePos, n).CopyTo(destination);
        _linePos += n;
        BytesRead += n;
        return n;
    }

    // The end is a line of "." alone; a stuffed line starts with ".." (RFC 1939 §3). Returns false at the end.
    private bool Accept(ReadOnlyMemory<byte> raw)
    {
        ReadOnlySpan<byte> s = raw.Span;
        if (s.Length >= 1 && s[0] == (byte)'.' && (s.Length == 1 || s.SequenceEqual(".\r\n"u8) || s.SequenceEqual(".\n"u8)))
        {
            _done = true;
            _line = default;
            _linePos = 0;
            return false;
        }

        _line = s.Length > 1 && s[0] == (byte)'.' ? raw[1..] : raw;
        _linePos = 0;
        return true;
    }

    /// <summary>Reads and discards what is left, so the connection is ready for the next command.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A task that completes after the terminating line.</returns>
    public async ValueTask DrainAsync(CancellationToken cancellationToken = default)
    {
        byte[] scratch = System.Buffers.ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (await ReadAsync(scratch.AsMemory(), cancellationToken).ConfigureAwait(false) > 0) { }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
