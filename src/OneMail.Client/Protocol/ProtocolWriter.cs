using System.Buffers;
using System.Text;

namespace RecursiveAI.OneMail.Client.Protocol;

/// <summary>
/// The buffered writer every protocol command goes through. Bytes collect in a pooled buffer and cross the connection at
/// <see cref="Flush"/>, so a pipelined batch is one write.
/// </summary>
public sealed class ProtocolWriter : IDisposable
{
    private readonly ProtocolConnection _connection;
    private byte[] _buf = ArrayPool<byte>.Shared.Rent(8192);
    private int _len;
    private List<(int Start, int Length)>? _secrets;

    internal ProtocolWriter(ProtocolConnection connection)
    {
        _connection = connection;
    }

    /// <summary>Bytes written but not flushed.</summary>
    public int Pending => _len;

    private Span<byte> Reserve(int count)
    {
        if (_buf.Length - _len < count)
        {
            byte[] bigger = ArrayPool<byte>.Shared.Rent(Math.Max(_buf.Length * 2, _len + count));
            Buffer.BlockCopy(_buf, 0, bigger, 0, _len);
            ArrayPool<byte>.Shared.Return(_buf);
            _buf = bigger;
        }

        return _buf.AsSpan(_len, count);
    }

    /// <summary>Appends bytes.</summary>
    /// <param name="data">The bytes.</param>
    public void Write(ReadOnlySpan<byte> data)
    {
        data.CopyTo(Reserve(data.Length));
        _len += data.Length;
    }

    /// <summary>Appends ASCII text. Characters above 0x7F are written as <c>?</c>; use <see cref="Write(ReadOnlySpan{byte})"/> with UTF-8 for those.</summary>
    /// <param name="text">The text.</param>
    public void WriteAscii(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Span<byte> dst = Reserve(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            dst[i] = text[i] < 0x80 ? (byte)text[i] : (byte)'?';
        }

        _len += text.Length;
    }

    /// <summary>Appends UTF-8 text.</summary>
    /// <param name="text">The text.</param>
    public void WriteUtf8(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int max = Encoding.UTF8.GetMaxByteCount(text.Length);
        int n = Encoding.UTF8.GetBytes(text, Reserve(max));
        _len += n;
    }

    /// <summary>Appends CR LF.</summary>
    public void WriteCrLf()
    {
        Span<byte> d = Reserve(2);
        d[0] = (byte)'\r';
        d[1] = (byte)'\n';
        _len += 2;
    }

    /// <summary>Appends bytes that must not appear in a trace (a password, a SASL response); the trace sees asterisks.</summary>
    /// <param name="data">The secret bytes.</param>
    public void WriteSecret(ReadOnlySpan<byte> data)
    {
        (_secrets ??= []).Add((_len, data.Length));
        Write(data);
    }

    /// <summary>Appends ASCII text that must not appear in a trace.</summary>
    /// <param name="text">The secret text.</param>
    public void WriteSecretAscii(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        (_secrets ??= []).Add((_len, text.Length));
        WriteAscii(text);
    }

    /// <summary>Sends everything buffered.</summary>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the bytes are handed to the transport.</returns>
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        if (_len == 0)
        {
            return;
        }

        Trace();
        int n = _len;
        _len = 0;
        using CancellationTokenSource cts = ProtocolConnection.TimeoutSource(_connection.WriteTimeout, cancellationToken);
        try
        {
            await _connection.Stream.WriteAsync(_buf.AsMemory(0, n), cts.Token).ConfigureAwait(false);
            await _connection.Stream.FlushAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The server did not accept data within " + _connection.WriteTimeout + ".");
        }
    }

    /// <summary>Synchronous twin of <see cref="FlushAsync"/>.</summary>
    public void Flush()
    {
        if (_len == 0)
        {
            return;
        }

        Trace();
        int n = _len;
        _len = 0;
        _connection.SetSocketTimeouts();
        _connection.Stream.Write(_buf, 0, n);
        _connection.Stream.Flush();
    }

    private void Trace()
    {
        IProtocolTrace? trace = _connection.Options.Trace;
        if (trace is not null)
        {
            if (_secrets is null)
            {
                trace.Sent(_buf.AsSpan(0, _len));
            }
            else
            {
                byte[] copy = _buf.AsSpan(0, _len).ToArray();
                foreach ((int start, int length) in _secrets)
                {
                    copy.AsSpan(start, length).Fill((byte)'*');
                }

                trace.Sent(copy);
            }
        }

        _secrets = null;
    }

    /// <summary>Copies exactly <paramref name="count"/> bytes from a stream into the output, flushing as it goes (an APPEND body, a DATA payload).</summary>
    /// <param name="source">The stream.</param>
    /// <param name="count">How many bytes to copy.</param>
    /// <param name="cancellationToken">Cancels the copy.</param>
    /// <exception cref="EndOfStreamException">The stream ended early.</exception>
    public async ValueTask WriteFromAsync(Stream source, long count, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        while (count > 0)
        {
            int want = (int)Math.Min(count, 16 * 1024);
            Reserve(want);
            int n = await source.ReadAsync(_buf.AsMemory(_len, want), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                throw new EndOfStreamException("The source ended before the announced length.");
            }

            _len += n;
            count -= n;
            if (_len >= 64 * 1024)
            {
                await FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Synchronous twin of <see cref="WriteFromAsync"/>.</summary>
    /// <param name="source">The stream.</param>
    /// <param name="count">How many bytes to copy.</param>
    public void WriteFrom(Stream source, long count)
    {
        ArgumentNullException.ThrowIfNull(source);
        while (count > 0)
        {
            int want = (int)Math.Min(count, 16 * 1024);
            int n = source.Read(Reserve(want));
            if (n == 0)
            {
                throw new EndOfStreamException("The source ended before the announced length.");
            }

            _len += n;
            count -= n;
            if (_len >= 64 * 1024)
            {
                Flush();
            }
        }
    }

    /// <summary>Returns the buffer to the pool.</summary>
    public void Dispose()
    {
        byte[] b = _buf;
        _buf = [];
        _len = 0;
        if (b.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(b);
        }
    }
}
