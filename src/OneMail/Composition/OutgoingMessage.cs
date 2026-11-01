using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Mime.Transfer;
using RecursiveAI.OneMail.Text;

namespace RecursiveAI.OneMail.Composition;

/// <summary>A file attached to an <see cref="OutgoingMessage"/>.</summary>
/// <param name="FileName">The name shown to the recipient; non-ASCII names are written as RFC 2231 extended parameters.</param>
/// <param name="Content">The file's bytes.</param>
/// <param name="MediaType">The media type; <c>application/octet-stream</c> when null.</param>
public sealed record OutgoingAttachment(string FileName, ReadOnlyMemory<byte> Content, string? MediaType = null);

/// <summary>
/// A new message described by its parts, for sending or saving. <see cref="ToEntity"/> builds the MIME tree: addresses and the subject
/// are RFC 2047 encoded when they need it, text goes out as 7bit or quoted-printable, attachments as base64.
/// </summary>
/// <remarks>Replies and forwards come from <see cref="MessageComposer"/>.</remarks>
public sealed class OutgoingMessage
{
    /// <summary>The author: <c>addr@host</c> or <c>Display Name &lt;addr@host&gt;</c>. Required.</summary>
    public required string From { get; init; }

    /// <summary>The primary recipients, in the same syntax.</summary>
    public IReadOnlyList<string> To { get; init; } = [];

    /// <summary>Copy recipients.</summary>
    public IReadOnlyList<string> Cc { get; init; } = [];

    /// <summary>Blind copy recipients: used for delivery and not shown to others (the SMTP client takes the header out when sending).</summary>
    public IReadOnlyList<string> Bcc { get; init; } = [];

    /// <summary>Where replies should go, or null.</summary>
    public string? ReplyTo { get; init; }

    /// <summary>The subject, any Unicode text; null or empty writes none.</summary>
    public string? Subject { get; init; }

    /// <summary>The plain-text body, or null.</summary>
    public string? Text { get; init; }

    /// <summary>The HTML body, or null. With <see cref="Text"/> both go in a <c>multipart/alternative</c>.</summary>
    public string? Html { get; init; }

    /// <summary>Attached files.</summary>
    public IReadOnlyList<OutgoingAttachment> Attachments { get; init; } = [];

    /// <summary>The <c>Date</c> header. Null: the current time, with the local offset.</summary>
    public DateTimeOffset? Date { get; init; }

    /// <summary>The <c>Message-ID</c> without angle brackets. Null: a random one at the sender's domain. Empty string: none.</summary>
    public string? MessageId { get; init; }

    /// <summary>Extra header fields, written after the standard ones. Values must be ASCII or already RFC 2047 encoded and contain no line breaks.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } = [];

    /// <summary>Builds the MIME tree.</summary>
    /// <returns>The top-level entity, ready to write or send.</returns>
    /// <exception cref="ArgumentException">An address is not parseable, a header holds a line break, or there is no recipient.</exception>
    public MimeEntity ToEntity()
    {
        MimeEntity body;
        MimePart? text = Text is null ? null : TextPart("text/plain", Text);
        MimePart? html = Html is null ? null : TextPart("text/html", Html);
        if (text is not null && html is not null)
        {
            var alt = new MimeMultipart("alternative");
            alt.Add(text);
            alt.Add(html);
            body = alt;
        }
        else
        {
            body = text ?? html ?? TextPart("text/plain", string.Empty);
        }

        MimeEntity top = body;
        if (Attachments.Count > 0)
        {
            var mixed = new MimeMultipart("mixed");
            mixed.Add(body);
            foreach (OutgoingAttachment a in Attachments)
            {
                var part = new MimePart(a.MediaType ?? "application/octet-stream");
                part.Headers.Set("Content-Type", (a.MediaType ?? "application/octet-stream") + MimeHeaderValue.Parameter("name", a.FileName));
                part.Headers.Add("Content-Disposition", "attachment" + MimeHeaderValue.Parameter("filename", a.FileName));
                part.SetDecodedContent(a.Content.Span);
                mixed.Add(part);
            }

            top = mixed;
        }

        // Standard fields first, in the order clients write them; the body's own Content-* fields follow.
        var headers = new List<(string, string)>
        {
            ("From", Format(From)),
        };
        if (To.Count > 0) { headers.Add(("To", string.Join(", ", To.Select(Format)))); }
        if (Cc.Count > 0) { headers.Add(("Cc", string.Join(", ", Cc.Select(Format)))); }
        if (Bcc.Count > 0) { headers.Add(("Bcc", string.Join(", ", Bcc.Select(Format)))); }
        if (To.Count + Cc.Count + Bcc.Count == 0)
        {
            throw new ArgumentException("A message needs at least one recipient in To, Cc or Bcc.");
        }

        if (ReplyTo is not null) { headers.Add(("Reply-To", Format(ReplyTo))); }
        if (!string.IsNullOrEmpty(Subject)) { headers.Add(("Subject", Clean(Rfc2047.Encode(Subject)))); }
        headers.Add(("Date", MessageComposer.FormatDate(Date ?? DateTimeOffset.Now)));
        if (MessageId != string.Empty)
        {
            headers.Add(("Message-ID", "<" + (MessageId ?? NewMessageId()) + ">"));
        }

        headers.Add(("MIME-Version", "1.0"));
        foreach (KeyValuePair<string, string> h in Headers)
        {
            headers.Add((h.Key, Clean(h.Value)));
        }

        int at = 0;
        foreach ((string name, string value) in headers)
        {
            top.Headers.Insert(at++, new MimeHeader(name, value));
        }

        return top;
    }

    private string NewMessageId()
    {
        string domain = "localhost";
        List<MimeAddress> parsed = AddressParser.Parse(Encoding.UTF8.GetBytes(From));
        if (AddressParser.Flatten(parsed).FirstOrDefault() is { } m && m.Address.LastIndexOf('@') is > 0 and int at)
        {
            domain = m.Address[(at + 1)..];
        }

        return Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12)) + "@" + domain;
    }

    private static string Clean(string value)
    {
        if (value.AsSpan().IndexOfAny('\r', '\n') >= 0)
        {
            throw new ArgumentException("A header value cannot contain a line break.");
        }

        return value;
    }

    // Parses one address (or a list), then writes each mailbox with its display name quoted or RFC 2047 encoded.
    private static string Format(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        List<MimeMailbox> boxes = AddressParser.Flatten(AddressParser.Parse(Encoding.UTF8.GetBytes(Clean(address))));
        if (boxes.Count == 0 || boxes.Any(b => b.Address.Length == 0))
        {
            throw new ArgumentException("Not an address: " + address);
        }

        return string.Join(", ", boxes.Select(b =>
        {
            if (string.IsNullOrWhiteSpace(b.Name))
            {
                return b.Address;
            }

            string name = b.Name.Trim();
            string phrase = name.Any(c => c > 0x7E || c < 0x20)
                ? Rfc2047.Encode(name)
                : name.All(c => char.IsAsciiLetterOrDigit(c) || c == ' ') ? name
                : "\"" + name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
            return phrase + " <" + b.Address + ">";
        }));
    }

    private static MimePart TextPart(string type, string text)
    {
        var part = new MimePart(type + "; charset=utf-8");
        byte[] bytes = Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\r\n"));
        bool plain = bytes.All(b => b < 0x80);
        if (plain)
        {
            int run = 0;
            foreach (byte b in bytes)
            {
                run = b == (byte)'\n' ? 0 : run + 1;
                if (run > 78) { plain = false; break; }
            }
        }

        part.SetDecodedContent(bytes, plain ? ContentTransferEncoding.SevenBit : ContentTransferEncoding.QuotedPrintable);
        return part;
    }
}
