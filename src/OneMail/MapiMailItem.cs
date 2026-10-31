using System.Text;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Text;

namespace RecursiveAI.OneMail;

/// <summary>
/// The shared read view over an Outlook message (<see cref="MapiMessage"/>), whether it was read from an MSG, a PST or an OST, or
/// is embedded in another message. The property bag is the truth and stays reachable through <see cref="Message"/>; the view only
/// projects it, and the mapping is written down in <c>docs/shared-view.md</c>.
/// </summary>
public sealed class MapiMailItem : IMailItem
{
    private readonly MapiMessage _message;
    private readonly MailProvenance _provenance;
    private IReadOnlyList<IMailAttachment>? _attachments;

    /// <summary>Wraps a MAPI message in the shared view.</summary>
    /// <param name="message">The message; it stays the truth, the view only reads it.</param>
    /// <param name="provenance">Where the message was read from.</param>
    public MapiMailItem(MapiMessage message, MailProvenance provenance)
    {
        _message = message;
        _provenance = provenance;
    }

    /// <summary>The MAPI message behind the view.</summary>
    public MapiMessage Message => _message;

    /// <inheritdoc/>
    public MailProvenance Provenance => _provenance;

    /// <inheritdoc/>
    public MailItemKind Kind => KindOf(_message.MessageClass);

    /// <summary>
    /// The item kind a message class names (MS-OXOSFLD, MS-OXPROPS PidTagMessageClass). Matching is case-insensitive and by whole
    /// dot-separated segments, so <c>IPM.TaskRequest</c> (a message) is not a task. No class at all is an ordinary message.
    /// </summary>
    /// <param name="messageClass">The message class, or null.</param>
    /// <returns>The kind.</returns>
    public static MailItemKind KindOf(string? messageClass)
    {
        if (string.IsNullOrEmpty(messageClass))
        {
            return MailItemKind.Message;
        }

        static bool Is(string c, string prefix) =>
            c.Equals(prefix, StringComparison.OrdinalIgnoreCase) || (c.Length > prefix.Length && c[prefix.Length] == '.' && c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        if (Is(messageClass, "IPM.Appointment"))
        {
            return MailItemKind.Appointment;
        }

        if (Is(messageClass, "IPM.Contact"))
        {
            return MailItemKind.Contact;
        }

        if (Is(messageClass, "IPM.Task"))
        {
            return MailItemKind.Task;
        }

        if (Is(messageClass, "IPM.StickyNote"))
        {
            return MailItemKind.Note;
        }

        if (Is(messageClass, "IPM.Activity"))
        {
            return MailItemKind.Journal;
        }

        if (Is(messageClass, "IPM.DistList"))
        {
            return MailItemKind.DistributionList;
        }

        // Mail and everything delivered like mail: notes, posts, meeting messages, task requests, reports and receipts, digitally signed or encrypted mail.
        if (Is(messageClass, "IPM.Note") || Is(messageClass, "IPM.Post") || Is(messageClass, "IPM.Schedule") || Is(messageClass, "IPM.TaskRequest")
            || Is(messageClass, "IPM.Recall") || Is(messageClass, "REPORT") || Is(messageClass, "IPM.Outlook.Recall") || Is(messageClass, "IPM.Sharing"))
        {
            return MailItemKind.Message;
        }

        return MailItemKind.Other;
    }

    /// <inheritdoc/>
    public string? Subject
    {
        get
        {
            // MS-OXPROPS PidTagSubject: Outlook may store the prefix ("RE: ") apart from the rest and mark the split with a pair of
            // control characters in front: U+0001 and one character giving the prefix length. The pair is not part of the text.
            string? s = _message.GetString(MapiPid.Subject);
            if (s is null)
            {
                string? normalized = _message.GetString(0x0E1D);
                return normalized is null ? null : (_message.GetString(0x003D) ?? string.Empty) + normalized;
            }

            return s.Length >= 2 && s[0] == '\u0001' ? s[2..] : s;
        }
    }

    /// <inheritdoc/>
    public MailAddress? Sender
    {
        get
        {
            // The sender is "the actual sender when it differs from From": a message sent on behalf of someone has both.
            if (Address(_message, MapiPid.SentRepresentingName, MapiPid.SentRepresentingEmailAddress, MapiPid.SentRepresentingSmtpAddress) is null)
            {
                return null;
            }

            MailAddress? sender = Address(_message, MapiPid.SenderName, MapiPid.SenderEmailAddress, MapiPid.SenderSmtpAddress);
            IReadOnlyList<MailAddress> from = From;
            return sender is { } s && !(from.Count > 0 && string.Equals(from[0].Address, s.Address, StringComparison.OrdinalIgnoreCase)) ? s : null;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<MailAddress> From
    {
        get
        {
            // MS-OXCMAIL 2.1.3.1: From comes from PidTagSentRepresenting*, falling back to PidTagSender*.
            MailAddress? a = Address(_message, MapiPid.SentRepresentingName, MapiPid.SentRepresentingEmailAddress, MapiPid.SentRepresentingSmtpAddress)
                ?? Address(_message, MapiPid.SenderName, MapiPid.SenderEmailAddress, MapiPid.SenderSmtpAddress);
            return a is { } x ? [x] : [];
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<MailAddress> ToRecipients => Recipients(MapiRecipientType.To);

    /// <inheritdoc/>
    public IReadOnlyList<MailAddress> Cc => Recipients(MapiRecipientType.Cc);

    /// <inheritdoc/>
    public IReadOnlyList<MailAddress> Bcc => Recipients(MapiRecipientType.Bcc);

    /// <inheritdoc/>
    public IReadOnlyList<MailAddress> ReplyTo => [];

    private List<MailAddress> Recipients(MapiRecipientType type)
    {
        var list = new List<MailAddress>();
        foreach (MapiRecipient r in _message.Recipients)
        {
            if (r.RecipientType == type && Address(r, MapiPid.DisplayName, MapiPid.EmailAddress, MapiPid.SmtpAddress) is { } a)
            {
                list.Add(a);
            }
        }

        return list;
    }

    // The address is the SMTP address when the object has one, else the address it is stored under (an SMTP address, or an Exchange
    // distinguished name when the object is an internal user and has no SMTP address property). Nothing is looked up.
    private static MailAddress? Address(MapiObject o, ushort name, ushort email, ushort smtp)
    {
        string? n = o.GetString(name);
        string? a = o.GetString(smtp);
        if (string.IsNullOrEmpty(a))
        {
            a = o.GetString(email);
        }

        if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(n))
        {
            return null;
        }

        return new MailAddress(string.IsNullOrEmpty(n) ? null : n, a ?? string.Empty);
    }

    /// <inheritdoc/>
    public MailDate? Sent => Date(MapiPid.ClientSubmitTime, MailDateSource.ClientSubmit);

    /// <inheritdoc/>
    public bool TryGetDate(MailDateSource source, out MailDate value)
    {
        MailDate? d = source switch
        {
            MailDateSource.ClientSubmit => Date(MapiPid.ClientSubmitTime, MailDateSource.ClientSubmit),
            MailDateSource.Delivery => Date(MapiPid.MessageDeliveryTime, MailDateSource.Delivery),
            MailDateSource.Creation => Date(MapiPid.CreationTime, MailDateSource.Creation),
            _ => null,
        };
        value = d.GetValueOrDefault();
        return d is not null;
    }

    // MAPI times are FILETIME in UTC with no zone of their own, so ZoneKnown is false and the offset is zero.
    private MailDate? Date(ushort id, MailDateSource source) =>
        _message.GetTime(id) is { } t ? new MailDate(t, source, ZoneKnown: false) : null;

    /// <inheritdoc/>
    public string? MessageId => Ids(_message.GetString(MapiPid.InternetMessageId)) is { Count: > 0 } l ? l[0] : null;

    /// <inheritdoc/>
    public IReadOnlyList<string> InReplyTo => Ids(_message.GetString(MapiPid.InReplyToId));

    /// <inheritdoc/>
    public IReadOnlyList<string> References => Ids(_message.GetString(MapiPid.InternetReferences));

    // Message IDs are written <id>; a value without angle brackets is read as whitespace-separated IDs.
    private static List<string> Ids(string? text)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return list;
        }

        if (text.Contains('<', StringComparison.Ordinal))
        {
            int i = 0;
            while ((i = text.IndexOf('<', i)) >= 0)
            {
                int end = text.IndexOf('>', i + 1);
                if (end < 0)
                {
                    break;
                }

                if (end > i + 1)
                {
                    list.Add(text.Substring(i + 1, end - i - 1).Trim());
                }

                i = end + 1;
            }

            return list;
        }

        list.AddRange(text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return list;
    }

    /// <inheritdoc/>
    public ReadOnlyMemory<byte> ConversationIndex => _message.Properties.Get(MapiPid.ConversationIndex, MapiType.Binary)?.GetBytes() ?? default;

    /// <inheritdoc/>
    public string? ConversationTopic => _message.GetString(MapiPid.ConversationTopic);

    /// <inheritdoc/>
    public MailBody? TextBody => _message.Properties.Get(MapiPid.Body, MapiType.String8, MapiType.UnicodeString) is { } p ? new MapiMailBody(_message, p, "text/plain") : null;

    /// <inheritdoc/>
    public MailBody? HtmlBody => _message.Properties.Get(MapiPid.Html, MapiType.Binary, MapiType.String8, MapiType.UnicodeString) is { } p ? new MapiMailBody(_message, p, "text/html") : null;

    /// <summary>
    /// The compressed RTF (PidTagRtfCompressed) as stored. Until the RTF layer is wired in, <see cref="MailBody.Open"/> returns the
    /// compressed stream (MS-OXRTFCP) and <see cref="MailBody.ReadText"/> decodes those bytes as text, which is not readable RTF.
    /// </summary>
    public MailBody? RtfBody => _message.Properties.Get(MapiPid.RtfCompressed, MapiType.Binary) is { } p ? new MapiMailBody(_message, p, "application/rtf") : null;

    /// <inheritdoc/>
    public IReadOnlyList<IMailAttachment> Attachments => _attachments ??= BuildAttachments();

    private List<IMailAttachment> BuildAttachments()
    {
        var list = new List<IMailAttachment>();
        foreach (MapiAttachment a in _message.Attachments)
        {
            list.Add(new MapiMailAttachment(a, _provenance));
        }

        return list;
    }

    /// <inheritdoc/>
    public IEnumerable<MailDefect> Defects
    {
        get
        {
            foreach (MapiDefect d in AllDefects(_message))
            {
                yield return new MailDefect("mapi", d.Kind.ToString(), d.Offset, d.IsAmbiguity);
            }
        }
    }

    private static IEnumerable<MapiDefect> AllDefects(MapiMessage m)
    {
        foreach (MapiDefect d in m.Defects)
        {
            yield return d;
        }

        foreach (MapiRecipient r in m.Recipients)
        {
            foreach (MapiDefect d in r.Defects)
            {
                yield return d;
            }
        }

        foreach (MapiAttachment a in m.Attachments)
        {
            foreach (MapiDefect d in a.Defects)
            {
                yield return d;
            }

            if (a.EmbeddedMessage is { } e)
            {
                foreach (MapiDefect d in AllDefects(e))
                {
                    yield return d;
                }
            }
        }
    }
}

/// <summary>A body property of a MAPI message as a <see cref="MailBody"/>: the bytes are the property's own, decoded on request.</summary>
internal sealed class MapiMailBody(MapiMessage owner, MapiProperty property, string contentType) : MailBody
{
    public override string ContentType => contentType;

    // PtypString is UTF-16LE. 8-bit text and binary HTML name their code page: HTML by PidTagInternetCodepage (MS-OXCMAIL 2.1.3.2.1),
    // everything else by the message's own rule (message code page, locale, enclosing message; windows-1252 when nothing says).
    private MailEncoding? Encoding8() =>
        property.Type == MapiType.UnicodeString ? null : EncodingFor();

    private MailEncoding EncodingFor()
    {
        if (property.Type == MapiType.Binary && contentType == "text/html" && owner.Properties.Get(MapiPid.InternetCodepage, MapiType.Integer32)?.AsInt32() is int cp and > 0
            && cp is not (1200 or 1201) && MailEncoding.ForCodePage(cp) is { } e)
        {
            return e;
        }

        return owner.String8Encoding;
    }

    public override string? Charset => property.Type == MapiType.UnicodeString ? "utf-16le"
        : contentType == "application/rtf" ? null
        : EncodingFor().Name.ToLowerInvariant();

    public override Stream Open() => property.OpenRead();

    public override string ReadText()
    {
        if (property.Type is MapiType.UnicodeString or MapiType.String8)
        {
            return property.AsString(owner.String8Encoding) ?? string.Empty;
        }

        ReadOnlySpan<byte> b = property.GetBytes().Span;
        while (b.Length > 0 && b[^1] == 0)
        {
            b = b[..^1];
        }

        if (contentType != "application/rtf" && owner.Properties.Get(MapiPid.InternetCodepage, MapiType.Integer32)?.AsInt32() is int cp and > 0 && MapiCodePages.IsUnicodeTransformation(cp) && cp == 1200)
        {
            return Encoding.Unicode.GetString(b);
        }

        return (Encoding8() ?? MailEncoding.Windows1252).GetString(b);
    }
}

/// <summary>An attachment of a MAPI message as <see cref="IMailAttachment"/>.</summary>
internal sealed class MapiMailAttachment(MapiAttachment attachment, MailProvenance parent) : IMailAttachment
{
    private MapiMailItem? _embedded;
    private bool _embeddedTried;

    public string? FileName => attachment.FileName;

    public string ContentType
    {
        get
        {
            if (attachment.EmbeddedMessage is not null)
            {
                return "message/rfc822";
            }

            string? t = attachment.MimeType?.Trim();
            return string.IsNullOrEmpty(t) ? "application/octet-stream" : t.ToLowerInvariant();
        }
    }

    public string? ContentId
    {
        get
        {
            string? c = attachment.ContentId?.Trim();
            if (c is { Length: > 1 } && c[0] == '<' && c[^1] == '>')
            {
                c = c[1..^1];
            }

            return string.IsNullOrEmpty(c) ? null : c;
        }
    }

    // As for MIME: inline when it has a content ID and is not marked as an attachment (PidTagAttachContentDisposition 0x3716).
    public bool IsInline => ContentId is not null
        && !(attachment.GetString(0x3716) is { } disp && disp.TrimStart().StartsWith("attachment", StringComparison.OrdinalIgnoreCase));

    public long Size
    {
        get
        {
            // The data property's own length; an embedded message has no flat data, so its stated size is used.
            if (attachment.Properties.Get(MapiPid.AttachDataBinary, MapiType.Binary) is { } p)
            {
                return p.GetByteLength();
            }

            return attachment.GetInt32(MapiPid.AttachSize) ?? 0;
        }
    }

    // An embedded message has no flat bytes to give: it is read through Embedded.
    public Stream Open() =>
        attachment.EmbeddedMessage is null ? attachment.Properties.Get(MapiPid.AttachDataBinary, MapiType.Binary)?.OpenRead() ?? new MemoryStream([], writable: false) : new MemoryStream([], writable: false);

    public IMailItem? Embedded
    {
        get
        {
            if (!_embeddedTried)
            {
                _embeddedTried = true;
                if (attachment.EmbeddedMessage is { } m)
                {
                    _embedded = new MapiMailItem(m, parent with { Offset = -1, Length = -1, Index = -1 });
                }
            }

            return _embedded;
        }
    }
}
