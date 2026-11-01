using RecursiveAI.OneMail.Mime.Reading;

namespace RecursiveAI.OneMail.Mime;

public abstract partial class MimeEntity
{
    /// <summary>
    /// Parses a message or fragment found in a span: it must start with a header block (<see cref="MimeHeaderBlock.TryParse"/>). The bytes are copied,
    /// so the span can be reused at once. Everything the lenient reader repairs is in <see cref="MimeEntity.EnumerateDefects"/> of the result.
    /// </summary>
    /// <param name="data">The candidate message, from its first header byte to wherever the caller thinks it ends. May be cut or hold trailing data.</param>
    /// <param name="entity">The root entity, or null when the span does not start with a header field.</param>
    /// <param name="options">Reader options (limits), or null for the defaults.</param>
    /// <returns>False when <paramref name="data"/> is empty or does not start with a header field. Never throws on malformed content.</returns>
    public static bool TryParse(ReadOnlySpan<byte> data, out MimeEntity? entity, MimeReaderOptions? options = null)
    {
        entity = null;
        if (!MimeHeaderBlock.TryParse(data, out _))
        {
            return false;
        }

        entity = Parse(data.ToArray(), options);
        return true;
    }
}
