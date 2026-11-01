using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Mime.Transfer;

namespace RecursiveAI.OneMail.Cryptography.Smime;

/// <summary>
/// S/MIME items as Outlook stores them (MS-OXOSMIME §2.1.3): message class <c>IPM.Note.SMIME</c> (opaque-signed or encrypted) and
/// <c>IPM.Note.SMIME.MultipartSigned</c> (clear-signed), each with one attachment that holds the S/MIME content. The item is turned
/// back into the Internet-format entity and then goes through <see cref="SmimeProcessor"/>, so MSG, PST and wire messages share one path.
/// </summary>
public static class SmimeOutlook
{
    /// <summary>
    /// True when the message class is <c>IPM.Note.SMIME</c> or <c>IPM.Note.SMIME.MultipartSigned</c> or ends in <c>.SMIME</c> /
    /// <c>.SMIME.MultipartSigned</c> (MS-OXOSMIME §2.1.3.1.2.2 and §2.1.3.2.2.2).
    /// </summary>
    /// <param name="messageClass">The <c>PidTagMessageClass</c> value; may be null.</param>
    /// <returns>Whether the class marks an S/MIME item.</returns>
    public static bool IsSmimeClass(string? messageClass) =>
        messageClass is not null
        && (messageClass.EndsWith(".SMIME", StringComparison.OrdinalIgnoreCase) || messageClass.EndsWith(".SMIME.MultipartSigned", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Rebuilds the Internet-format entity of an S/MIME item: for a clear-signed item the attachment content (a <c>multipart/signed</c> with
    /// its original bytes); for an opaque or encrypted item a <c>application/pkcs7-mime</c> part whose type comes from the
    /// <c>Content-Type</c> named property (property set <c>PS_INTERNET_HEADERS</c>) or else the attachment's MIME tag and file name. A <c>From</c>
    /// header from the item's sender is added so the sender check has an address to compare.
    /// </summary>
    /// <param name="message">The Outlook message.</param>
    /// <returns>The entity, or null when the class is not an S/MIME class or no attachment holds data.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    public static MimeEntity? ToEntity(MapiMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!IsSmimeClass(message.MessageClass))
        {
            return null;
        }

        // MS-OXOSMIME wants exactly one attachment; take the first one that carries data so a stray extra attachment does not hide the content.
        MapiAttachment? attachment = message.Attachments.FirstOrDefault(static a => a.Properties.Get(MapiPid.AttachDataBinary, MapiType.Binary) is not null);
        if (attachment is null)
        {
            return null;
        }

        byte[] data;
        using (Stream s = attachment.OpenData()!)
        using (var ms = new MemoryStream())
        {
            s.CopyTo(ms);
            data = ms.ToArray();
        }

        MimeEntity entity;
        bool clearSigned = message.MessageClass!.EndsWith(".SMIME.MultipartSigned", StringComparison.OrdinalIgnoreCase)
            || string.Equals(attachment.MimeType, "multipart/signed", StringComparison.OrdinalIgnoreCase);
        if (clearSigned)
        {
            // The attachment is the whole entity with only its Content-Type header (MS-OXOSMIME §2.1.3.1): the signed bytes inside it are untouched.
            entity = MimeEntity.Parse(data);
        }
        else
        {
            string contentType = message.Properties.Get(new MapiNamedProperty(MapiPropertySets.InternetHeaders, "Content-Type"))?.AsString(message.String8Encoding) is { Length: > 0 } named
                ? named
                : FallbackContentType(attachment);
            var part = new MimePart();
            part.Headers.Add("Content-Type", contentType);
            part.SetDecodedContent(data, ContentTransferEncoding.Base64);
            entity = part;
        }

        var item = new MapiMailItem(message, new MailProvenance(MailFormat.Unknown, null, null, 0, 0, 0));
        if (item.From.Count > 0 && entity.Headers.GetFirst("From") is null)
        {
            entity.Headers.Add("From", item.From[0].Address);
        }

        return entity;
    }

    /// <summary>
    /// Verifies or decrypts an Outlook S/MIME item through <see cref="SmimeProcessor.Open(MimeEntity, SmimeOpenOptions?)"/>.
    /// An item of another class gives an empty result.
    /// </summary>
    /// <param name="message">The Outlook message.</param>
    /// <param name="options">Keys, certificates, trust and policy; null for the defaults.</param>
    /// <returns>The result; <see cref="SmimeResult.IsSmime"/> is false when the item holds no S/MIME content.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException">Policy and reference time are not given together.</exception>
    public static SmimeResult Open(MapiMessage message, SmimeOpenOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        MimeEntity entity = ToEntity(message) ?? new MimePart("application/octet-stream");
        return SmimeProcessor.Open(entity, options);
    }

    private static string FallbackContentType(MapiAttachment attachment)
    {
        // MS-OXOSMIME §2.1.3.2.2.3: the media type is the attach MIME tag, with a name parameter from the file name.
        string type = attachment.MimeType is { Length: > 0 } t ? t : "application/pkcs7-mime";
        return attachment.FileName is { Length: > 0 } n ? type + "; name=\"" + n.Replace("\"", "'", StringComparison.Ordinal) + "\"" : type;
    }
}
