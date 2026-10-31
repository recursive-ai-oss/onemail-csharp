using RecursiveAI.OneMail.Tnef;

namespace RecursiveAI.OneMail.Mime;

public abstract partial class MimeEntity
{
    /// <summary>
    /// The leaf parts of this entity (and of the messages inside it) that hold a TNEF stream, found by content: a part counts when its decoded body starts with the
    /// TNEF signature 0x223E9F78 (MS-OXTNEF 2.1.3.2), whatever its Content-Type or file name says. Only parts that could plausibly carry one are opened: those
    /// with an <c>application/*</c> media type (<c>application/ms-tnef</c>, <c>application/octet-stream</c> and so on) or a file name <c>winmail.dat</c> or ending in
    /// <c>.tnef</c>. Text, image, audio and video parts are not looked into.
    /// </summary>
    /// <returns>The parts, in document order. Nothing is cached: each call reads the first bytes of each candidate again.</returns>
    public IEnumerable<MimePart> EnumerateTnefParts()
    {
        switch (this)
        {
            case MimePart part:
                if (part.IsTnef())
                {
                    yield return part;
                }

                break;
            case MimeMultipart multipart:
                foreach (MimeEntity child in multipart.Children)
                {
                    foreach (MimePart found in child.EnumerateTnefParts())
                    {
                        yield return found;
                    }
                }

                break;
            case MimeMessagePart { Message: { } inner }:
                foreach (MimePart found in inner.EnumerateTnefParts())
                {
                    yield return found;
                }

                break;
        }
    }
}

/// <summary>Reading TNEF out of MIME parts.</summary>
public static class MimeTnefExtensions
{
    /// <summary>Whether the part's decoded body starts with the TNEF signature, for a part that could carry one (see <see cref="MimeEntity.EnumerateTnefParts"/>).</summary>
    /// <param name="part">The part.</param>
    /// <returns>True when it holds a TNEF stream. Never throws on malformed content: an undecodable body is not TNEF.</returns>
    public static bool IsTnef(this MimePart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        string? name = part.FileName;
        bool named = name is not null && (name.Equals("winmail.dat", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tnef", StringComparison.OrdinalIgnoreCase));
        if (!named && part.MediaTopLevelType != "application")
        {
            return false;
        }

        try
        {
            using Stream s = part.OpenDecodedContent();
            Span<byte> head = stackalloc byte[4];
            int n = 0;
            while (n < 4)
            {
                int r = s.Read(head[n..]);
                if (r <= 0)
                {
                    break;
                }

                n += r;
            }

            return n == 4 && TnefDocument.HasTnefSignature(head);
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>Decodes the part and reads it as a TNEF message. The decoded bytes are held in memory for as long as the message is used.</summary>
    /// <param name="part">The part; see <see cref="IsTnef"/>.</param>
    /// <param name="options">Limits, or null for the defaults.</param>
    /// <returns>The message. A body without the signature gives an empty message with a <see cref="TnefDefectKind.NotTnef"/> defect.</returns>
    public static TnefMessage ReadTnef(this MimePart part, TnefReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(part);
        return TnefMessage.Read(part.GetDecodedContent(), options);
    }
}
