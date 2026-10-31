using System.Globalization;
using System.Text;
using RecursiveAI.OneMail.Conversion.OxcMail;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Mime.Transfer;
using RecursiveAI.OneMail.Stores;
using RecursiveAI.OneMail.Stores.Olm;

namespace RecursiveAI.OneMail.Conversion.Stores;

/// <summary>
/// Builds a MIME message from the shared view, for items that have no MIME of their own and no property bag to convert (OLM mail).
/// It carries what the view carries: addresses, subject, dates, identifiers, conversation topic and index, the text and HTML
/// bodies, and the attachments; Bcc is not written. The result is deterministic: boundaries derive from the message-id and position.
/// </summary>
internal static class ViewMimeBuilder
{
    public static MimeEntity Build(IMailItem item, LossReport report)
    {
        MimePart? text = Body(item.TextBody, "plain");
        MimePart? html = Body(item.HtmlBody, "html");
        List<IMailAttachment> inline = [];
        List<IMailAttachment> other = [];
        foreach (IMailAttachment a in item.Attachments)
        {
            (a.IsInline && html is not null ? inline : other).Add(a);
        }

        string seed = item.MessageId ?? item.Subject ?? "message";
        int n = 0;
        string Boundary() => "=_onemail_" + Fnv(seed).ToString("x8", CultureInfo.InvariantCulture) + "_" + (n++).ToString(CultureInfo.InvariantCulture);

        MimeEntity body;
        if (text is not null && html is not null)
        {
            MimeEntity htmlSide = html;
            if (inline.Count > 0)
            {
                var related = new MimeMultipart("related", Boundary());
                related.Add(html);
                foreach (IMailAttachment a in inline)
                {
                    related.Add(Attachment(a, report));
                }

                htmlSide = related;
            }

            var alt = new MimeMultipart("alternative", Boundary());
            alt.Add(text);
            alt.Add(htmlSide);
            body = alt;
        }
        else
        {
            body = html ?? text ?? Empty();
            if (inline.Count > 0)
            {
                var related = new MimeMultipart("related", Boundary());
                related.Add(body);
                foreach (IMailAttachment a in inline)
                {
                    related.Add(Attachment(a, report));
                }

                body = related;
            }
        }

        MimeEntity root = body;
        if (other.Count > 0)
        {
            var mixed = new MimeMultipart("mixed", Boundary());
            mixed.Add(body);
            foreach (IMailAttachment a in other)
            {
                mixed.Add(Attachment(a, report));
            }

            root = mixed;
        }

        // Header fields go in front of the content fields the body part already has.
        List<(string, string)> h = [];
        AddAddresses(h, "From", item.From);
        if (item.Sender is { } s)
        {
            AddAddresses(h, "Sender", [s]);
        }

        AddAddresses(h, "To", item.ToRecipients);
        AddAddresses(h, "Cc", item.Cc);
        AddAddresses(h, "Reply-To", item.ReplyTo);
        if (item.Bcc.Count > 0)
        {
            report.Add("view", "Bcc", LossKind.Dropped, "BccNotWritten", "Blind-copy recipients are not written to a message file.");
        }

        h.Add(("Subject", item.Subject ?? string.Empty));
        if (item.Sent is { } d)
        {
            h.Add(("Date", MapiAddressResolver.FormatDate(d.Value)));
            if (d.Value.Offset != TimeSpan.Zero)
            {
                report.Add("view", "Date", LossKind.Approximated, "DateZoneNotStored", "The date is written in UTC; the zone it was sent in is not kept.");
            }
        }

        if (item.MessageId is { } mid)
        {
            h.Add(("Message-ID", "<" + mid + ">"));
        }

        if (item.InReplyTo.Count > 0)
        {
            h.Add(("In-Reply-To", string.Join(' ', item.InReplyTo.Select(i => "<" + i + ">"))));
        }

        if (item.References.Count > 0)
        {
            h.Add(("References", string.Join(' ', item.References.Select(i => "<" + i + ">"))));
        }

        if (item.ConversationTopic is { } topic)
        {
            h.Add(("Thread-Topic", topic));
        }

        if (!item.ConversationIndex.IsEmpty)
        {
            h.Add(("Thread-Index", Convert.ToBase64String(item.ConversationIndex.Span)));
        }

        h.Add(("MIME-Version", "1.0"));
        int at = 0;
        foreach ((string name, string value) in h)
        {
            root.Headers.Insert(at++, new MimeHeader(name, OneLine(value)));
        }

        return root;
    }

    private static void AddAddresses(List<(string, string)> h, string name, IReadOnlyList<MailAddress> list)
    {
        if (list.Count > 0)
        {
            h.Add((name, string.Join(", ", list.Select(a => MapiToMimeConverter.FormatMailbox(a.Name, a.Address)))));
        }
    }

    private static string OneLine(string s) => s.AsSpan().ContainsAny('\r', '\n') ? s.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ') : s;

    private static MimePart Empty()
    {
        var p = new MimePart("text/plain; charset=utf-8");
        p.SetDecodedContent([], ContentTransferEncoding.SevenBit);
        return p;
    }

    private static MimePart? Body(MailBody? body, string subtype)
    {
        if (body is null)
        {
            return null;
        }

        string text = body.ReadText().Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\n", "\r\n", StringComparison.Ordinal);
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        var part = new MimePart("text/" + subtype + "; charset=utf-8");
        bool plain = Ascii.IsValid(bytes) && !bytes.AsSpan().Contains((byte)0) && MaxLine(bytes) <= 998;
        part.SetDecodedContent(bytes, plain ? ContentTransferEncoding.SevenBit : ContentTransferEncoding.QuotedPrintable);
        return part;
    }

    private static int MaxLine(byte[] b)
    {
        int max = 0, cur = 0;
        foreach (byte x in b)
        {
            cur = x == (byte)'\n' ? 0 : cur + 1;
            max = Math.Max(max, cur);
        }

        return max;
    }

    private static MimePart Attachment(IMailAttachment a, LossReport report)
    {
        string type = a.ContentType.Contains('/', StringComparison.Ordinal) && a.ContentType.AsSpan().IndexOfAny(" ;\r\n") < 0 ? a.ContentType : "application/octet-stream";
        string? name = a.FileName is null ? null : OneLine(a.FileName);
        var part = new MimePart(type + (name is null ? string.Empty : MimeHeaderValue.Parameter("name", name)));
        using (Stream s = a.Open())
        using (MemoryStream ms = new())
        {
            s.CopyTo(ms);
            part.SetDecodedContent(ms.GetBuffer().AsSpan(0, (int)ms.Length), ContentTransferEncoding.Base64);
        }

        part.Headers.Add("Content-Disposition", (a.IsInline ? "inline" : "attachment") + (name is null ? string.Empty : MimeHeaderValue.Parameter("filename", name)));
        if (a.ContentId is { } cid)
        {
            part.Headers.Add("Content-ID", "<" + OneLine(cid) + ">");
        }

        if (a.Embedded is not null)
        {
            report.Add("view", "attachment", LossKind.Approximated, "EmbeddedMessageAsData", "An embedded message is written as an ordinary attachment.");
        }

        return part;
    }

    private static uint Fnv(string s)
    {
        uint h = 2166136261;
        foreach (char c in s)
        {
            h = (h ^ c) * 16777619;
        }

        return h;
    }

    /// <summary>The OLM elements neither the view nor a conversion reads, as loss entries.</summary>
    public static void ReportUnmapped(OlmItem item, LossReport report, params string[] carriedElsewhere)
    {
        foreach (OlmElement e in item.UnmappedElements())
        {
            if (Array.IndexOf(carriedElsewhere, e.Name) < 0)
            {
                report.Add("olm", e.Name, LossKind.Dropped, "Unmapped", "The message file has no field for this OLM element; it stays in the archive.");
            }
        }
    }
}
