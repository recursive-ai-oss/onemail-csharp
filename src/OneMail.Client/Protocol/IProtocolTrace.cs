namespace RecursiveAI.OneMail.Client.Protocol;

/// <summary>
/// Receives a copy of the bytes that cross the connection, after TLS and compression (so the plaintext protocol). Credentials the
/// client writes with <see cref="ProtocolWriter.WriteSecret(ReadOnlySpan{byte})"/> arrive as asterisks. Implementations must not throw
/// and must not keep the span.
/// </summary>
public interface IProtocolTrace
{
    /// <summary>Bytes the client sent, redacted.</summary>
    /// <param name="data">A chunk of the outgoing stream. Chunk boundaries are arbitrary.</param>
    void Sent(ReadOnlySpan<byte> data);

    /// <summary>Bytes the server sent, as read.</summary>
    /// <param name="data">A chunk of the incoming stream. Chunk boundaries are arbitrary.</param>
    void Received(ReadOnlySpan<byte> data);
}

/// <summary>An <see cref="IProtocolTrace"/> that writes <c>C: </c> and <c>S: </c> lines to a <see cref="TextWriter"/>.</summary>
public sealed class TextWriterProtocolTrace : IProtocolTrace
{
    private readonly TextWriter _writer;
    private readonly int _maxLine;
    private readonly object _gate = new();
    private readonly System.Text.StringBuilder _sent = new();
    private readonly System.Text.StringBuilder _received = new();

    /// <summary>Creates the trace.</summary>
    /// <param name="writer">Where lines go. Not disposed.</param>
    /// <param name="maxLineCharacters">Longer lines are cut with an ellipsis, so literals do not flood the log. Default 400.</param>
    public TextWriterProtocolTrace(TextWriter writer, int maxLineCharacters = 400)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
        _maxLine = Math.Max(16, maxLineCharacters);
    }

    /// <inheritdoc />
    public void Sent(ReadOnlySpan<byte> data) => Add("C: ", _sent, data);

    /// <inheritdoc />
    public void Received(ReadOnlySpan<byte> data) => Add("S: ", _received, data);

    private void Add(string prefix, System.Text.StringBuilder pending, ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            foreach (byte b in data)
            {
                if (b == (byte)'\n')
                {
                    _writer.WriteLine(prefix + pending.ToString().TrimEnd('\r'));
                    pending.Clear();
                }
                else if (pending.Length < _maxLine)
                {
                    pending.Append(b is >= 0x20 and < 0x7F or (byte)'\r' ? (char)b : '.');
                }
                else if (pending.Length == _maxLine)
                {
                    pending.Append('…');
                }
            }
        }
    }
}
