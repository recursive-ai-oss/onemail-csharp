using System.Buffers;
using RecursiveAI.OneMail.Client.Smtp.Protocol;

namespace RecursiveAI.OneMail.Client.Smtp;

/// <summary>
/// A write-only stream that sends what it is given as BDAT chunks (RFC 3030) and reads the server's reply to each. The final chunk goes
/// out with LAST when <see cref="Complete"/> runs. After a chunk is refused, later writes are dropped and the refusal is the result.
/// </summary>
internal sealed class BdatSink : Stream
{
    private readonly SmtpRawConnection _raw;
    private readonly byte[] _buffer;
    private readonly bool _normalize;
    private int _filled;
    private bool _pendingCr;
    private bool _done;
    private bool _returned;

    public BdatSink(SmtpRawConnection raw, int chunkSize, bool normalizeLineEndings)
    {
        _raw = raw;
        _buffer = ArrayPool<byte>.Shared.Rent(chunkSize);
        _normalize = normalizeLineEndings;
        Capacity = chunkSize;
    }

    private int Capacity { get; }

    /// <summary>The first reply that was not 2xx, or null.</summary>
    public SmtpReply? Failure { get; private set; }

    /// <summary>The reply to the last chunk once <see cref="Complete"/> has run.</summary>
    public SmtpReply? Final { get; private set; }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_done;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    private int Free => Capacity - _filled;

    // Copies as much of src as fits (a CRLF needs two bytes of room), expanding bare LF and bare CR when normalising.
    private int Fill(ReadOnlySpan<byte> src)
    {
        if (!_normalize)
        {
            int n = Math.Min(Free, src.Length);
            src[..n].CopyTo(_buffer.AsSpan(_filled));
            _filled += n;
            return n;
        }

        int consumed = 0;
        while (consumed < src.Length && Free >= 2)
        {
            if (_pendingCr)
            {
                // The CR before this byte was not followed by LF: it is a line end by itself, so complete it. A following LF belongs to it.
                _pendingCr = false;
                _buffer[_filled++] = (byte)'\r';
                _buffer[_filled++] = (byte)'\n';
                if (src[consumed] == (byte)'\n')
                {
                    consumed++;
                }

                continue;
            }

            ReadOnlySpan<byte> rest = src[consumed..];
            int i = rest.IndexOfAny((byte)'\r', (byte)'\n');
            int run = i < 0 ? rest.Length : i;
            int copy = Math.Min(run, Free);
            rest[..copy].CopyTo(_buffer.AsSpan(_filled));
            _filled += copy;
            consumed += copy;
            if (copy < run || i < 0)
            {
                continue;
            }

            if (Free < 2)
            {
                break;
            }

            if (rest[run] == (byte)'\n')
            {
                _buffer[_filled++] = (byte)'\r';
                _buffer[_filled++] = (byte)'\n';
            }
            else
            {
                _pendingCr = true;
            }

            consumed++;
        }

        return consumed;
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (buffer.Length > 0 && !_done)
        {
            if (Failure is not null)
            {
                return;
            }

            if (Free < 2)
            {
                SendChunk(false);
                continue;
            }

            buffer = buffer[Fill(buffer)..];
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (buffer.Length > 0 && !_done)
        {
            if (Failure is not null)
            {
                return;
            }

            if (Free < 2)
            {
                await SendChunkAsync(false, cancellationToken).ConfigureAwait(false);
                continue;
            }

            buffer = buffer[Fill(buffer.Span)..];
        }
    }

    private void SendChunk(bool last)
    {
        _raw.SendBdat(_buffer.AsSpan(0, _filled), last);
        _filled = 0;
        Record(_raw.ReadReply(), last);
    }

    private async ValueTask SendChunkAsync(bool last, CancellationToken ct)
    {
        await _raw.SendBdatAsync(_buffer.AsMemory(0, _filled), last, ct).ConfigureAwait(false);
        _filled = 0;
        Record(await _raw.ReadReplyAsync(ct).ConfigureAwait(false), last);
    }

    private void Record(SmtpReply r, bool last)
    {
        if (!r.IsPositiveCompletion)
        {
            Failure ??= r;
        }

        if (last)
        {
            Final = r;
        }
    }

    private void FlushPendingCr()
    {
        if (_pendingCr)
        {
            // Room is guaranteed: every flush leaves the buffer empty and a pending CR is only set with two bytes free.
            _pendingCr = false;
            if (Free < 2) { throw new InvalidOperationException("No room for the closing line end."); }
            _buffer[_filled++] = (byte)'\r';
            _buffer[_filled++] = (byte)'\n';
        }
    }

    /// <summary>Sends the last chunk and reads its reply.</summary>
    public SmtpReply Complete()
    {
        if (Failure is null)
        {
            if (_pendingCr && Free < 2) { SendChunk(false); }
            FlushPendingCr();
            if (Failure is null)
            {
                SendChunk(true);
            }
        }

        _done = true;
        return Failure ?? Final!;
    }

    /// <summary>Asynchronous twin of <see cref="Complete"/>.</summary>
    public async ValueTask<SmtpReply> CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (Failure is null)
        {
            if (_pendingCr && Free < 2) { await SendChunkAsync(false, cancellationToken).ConfigureAwait(false); }
            FlushPendingCr();
            if (Failure is null)
            {
                await SendChunkAsync(true, cancellationToken).ConfigureAwait(false);
            }
        }

        _done = true;
        return Failure ?? Final!;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_returned)
        {
            _returned = true;
            ArrayPool<byte>.Shared.Return(_buffer);
        }

        base.Dispose(disposing);
    }
}
