using RecursiveAI.OneMail.Client.Protocol;

namespace RecursiveAI.OneMail.Client.Smtp.Protocol;

/// <summary>
/// A write-only stream for the message body after <c>DATA</c>: it dot-stuffs lines that start with a period (RFC 5321 §4.5.2) and, on
/// <see cref="CompleteAsync"/>, writes the end-of-data line and reads the server's final reply.
/// </summary>
/// <remarks>
/// The bytes are sent as given: the caller supplies CRLF line ends (RFC 5321 §2.3.8). Servers refuse or mangle a bare LF or CR in DATA,
/// so a stream whose line ends are unknown can ask for <c>normalizeLineEndings</c>, which turns a bare LF or a bare CR into CRLF; that
/// changes the bytes, which matters for signed messages, so it is off by default.
/// </remarks>
public sealed class SmtpDataStream : Stream
{
    private readonly ProtocolWriter _writer;
    private readonly SmtpReplyReader _replies;
    private readonly bool _normalize;
    private bool _lineStart = true;
    private bool _afterCr;
    private bool _wroteAny;
    private bool _endedWithCrLf;
    private bool _completed;

    internal SmtpDataStream(ProtocolConnection connection, SmtpReplyReader replies, bool normalizeLineEndings)
    {
        _writer = connection.Writer;
        _replies = replies;
        _normalize = normalizeLineEndings;
    }

    /// <inheritdoc />
    public override bool CanRead => false;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => !_completed;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    /// <inheritdoc />
    public override void Flush() => _writer.Flush();

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => _writer.FlushAsync(cancellationToken).AsTask();

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        Encode(buffer);
        if (_writer.Pending >= 64 * 1024)
        {
            _writer.Flush();
        }
    }

    /// <inheritdoc />
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        Encode(buffer.Span);
        if (_writer.Pending >= 64 * 1024)
        {
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // RFC 5321 §4.5.2: a period at the start of a line is doubled; the receiver removes it.
    private void Encode(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        _wroteAny = true;
        int runStart = 0;
        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            if (_normalize)
            {
                if (_afterCr && b != (byte)'\n')
                {
                    // A bare CR: insert the LF it lacks.
                    _writer.Write(data[runStart..i]);
                    _writer.Write("\n"u8);
                    runStart = i;
                    _lineStart = true;
                }
                else if (b == (byte)'\n' && !_afterCr)
                {
                    // A bare LF: insert the CR it lacks.
                    _writer.Write(data[runStart..i]);
                    _writer.Write("\r"u8);
                    runStart = i;
                }
            }

            if (_lineStart && b == (byte)'.')
            {
                _writer.Write(data[runStart..i]);
                _writer.Write("."u8);
                runStart = i;
            }

            _endedWithCrLf = b == (byte)'\n' && (_afterCr || _normalize && b == (byte)'\n');
            _lineStart = b == (byte)'\n';
            _afterCr = b == (byte)'\r';
        }

        _writer.Write(data[runStart..]);
    }

    /// <summary>Ends the data (adds the missing CRLF, then <c>.</c> CRLF), flushes and reads the server's reply.</summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The reply to the end of data: 250 when the message was accepted.</returns>
    public async ValueTask<SmtpReply> CompleteAsync(CancellationToken cancellationToken = default)
    {
        Terminate();
        await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        return await _replies.ReadReplyAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Synchronous twin of <see cref="CompleteAsync"/>.</summary>
    /// <returns>The reply to the end of data.</returns>
    public SmtpReply Complete()
    {
        Terminate();
        _writer.Flush();
        return _replies.ReadReply();
    }

    private void Terminate()
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        if (_afterCr && _normalize)
        {
            _writer.Write("\n"u8);
            _endedWithCrLf = true;
        }

        // RFC 5321 §4.1.1.4: the end of data is CRLF "." CRLF, so the last line of the message needs its own CRLF first.
        if (_wroteAny && !_endedWithCrLf)
        {
            _writer.WriteCrLf();
        }

        _writer.Write(".\r\n"u8);
        _completed = true;
    }
}
