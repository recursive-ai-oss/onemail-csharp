using System.Text;
using System.Text.Unicode;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Mime.Reading;
using RecursiveAI.OneMail.Text;
using RecursiveAI.OneMail.Tnef;

namespace RecursiveAI.OneMail;

/// <summary>
/// The shared read view over a MIME message (<see cref="MimeEntity"/> tree). The tree is the truth and stays reachable through
/// <see cref="Entity"/>; the view only projects it. Items read from a store parse their bytes on first use, so enumerating a store
/// and skipping an item costs no parsing.
/// </summary>
/// <remarks>
/// Which part is the text or HTML body follows RFC 2046 §5.1.4: in <c>multipart/alternative</c> the last matching alternative
/// is the preferred one; in <c>multipart/related</c> and <c>multipart/signed</c> only the first child (the root / the signed
/// content) is searched; in any other multipart the first matching child in order wins. A text part marked
/// <c>Content-Disposition: attachment</c> is never a body. Every leaf part and every <c>message/*</c> part that is not the chosen
/// text or HTML body is an attachment, except a text part directly inside <c>multipart/alternative</c> that is not marked as an
/// attachment: that is another rendering of the body (ambiguity V1 in <c>docs/ambiguities.md</c>).
/// </remarks>
public class MimeMailItem : IMailItem
{
    private readonly MimeReaderOptions? _options;
    private readonly MailProvenance _provenance;
    private MimeEntity? _entity;
    private BodyLayout? _layout;

    internal MimeMailItem(MimeEntity entity, MailProvenance provenance)
    {
        _entity = entity;
        _provenance = provenance;
    }

    internal MimeMailItem(MimeReaderOptions? options, MailProvenance provenance)
    {
        _options = options;
        _provenance = provenance;
    }

    /// <summary>The MIME entity tree behind the view; parsed on first use for items that come from a store.</summary>
    /// <exception cref="ObjectDisposedException">The item came from a store that has been disposed and was not read yet.</exception>
    public MimeEntity Entity => _entity ??= MimeEntity.Parse(LoadBytes(), _options);

    /// <inheritdoc/>
    public MailProvenance Provenance => _provenance;

    /// <inheritdoc/>
    public MailItemKind Kind => MailItemKind.Message;

    /// <inheritdoc/>
    public string? Subject => Entity.Headers.GetFirst("Subject")?.Value;

    /// <inheritdoc/>
    public MailAddress? Sender
    {
        get
        {
            MailAddress[] l = Addresses("Sender");
            return l.Length == 0 ? null : l[0];
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<MailAddress> From => Addresses("From");

    /// <inheritdoc/>
    public IReadOnlyList<MailAddress> ToRecipients => Addresses("To");

    /// <inheritdoc/>
    public IReadOnlyList<MailAddress> Cc => Addresses("Cc");

    /// <inheritdoc/>
    public IReadOnlyList<MailAddress> Bcc => Addresses("Bcc");

    /// <inheritdoc/>
    public IReadOnlyList<MailAddress> ReplyTo => Addresses("Reply-To");

    /// <inheritdoc/>
    public MailDate? Sent => Entity.GetDate() is { } d ? new MailDate(d.Value, MailDateSource.DateHeader, d.ZoneKnown) : null;

    /// <inheritdoc/>
    public bool TryGetDate(MailDateSource source, out MailDate value)
    {
        if (source == MailDateSource.DateHeader && Sent is { } d)
        {
            value = d;
            return true;
        }

        if (source == MailDateSource.Delivery && DeliveryDate() is { } v)
        {
            value = v;
            return true;
        }

        value = default;
        return false;
    }

    /// <inheritdoc/>
    public string? MessageId => Entity.GetMessageId();

    /// <inheritdoc/>
    public IReadOnlyList<string> InReplyTo => Entity.GetInReplyTo();

    /// <inheritdoc/>
    public IReadOnlyList<string> References => Entity.GetReferences();

    /// <inheritdoc/>
    public ReadOnlyMemory<byte> ConversationIndex
    {
        get
        {
            // Thread-Index is base64 that clients fold across lines; blanks are not part of the value.
            string? v = Entity.Headers.GetFirst("Thread-Index")?.Value;
            if (v is null)
            {
                return default;
            }

            Span<byte> buf = new byte[v.Length];
            string compact = string.Concat(v.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return Convert.TryFromBase64String(compact, buf, out int n) ? buf[..n].ToArray() : default;
        }
    }

    /// <inheritdoc/>
    public string? ConversationTopic => Entity.Headers.GetFirst("Thread-Topic")?.Value;

    /// <inheritdoc/>
    /// <remarks>The MIME text part; when the message has none, the plain text of the TNEF part (ambiguity V9).</remarks>
    public MailBody? TextBody => Layout.Text is { } p ? new MimeMailBody(p) : Layout.Tnef.Select(t => t.Item.TextBody).FirstOrDefault(b => b is not null);

    /// <inheritdoc/>
    /// <remarks>The MIME HTML part; when the message has none, the HTML of the TNEF part, from its HTML property or de-encapsulated from its RTF (ambiguity V9).</remarks>
    public MailBody? HtmlBody => Layout.Html is { } p ? new MimeMailBody(p) : Layout.Tnef.Select(t => t.Item.HtmlBody).FirstOrDefault(b => b is not null);

    /// <inheritdoc/>
    /// <remarks>MIME has no RTF body; a TNEF part can bring one (ambiguity V9).</remarks>
    public MailBody? RtfBody => Layout.Tnef.Select(t => t.Item.RtfBody).FirstOrDefault(b => b is not null);

    /// <summary>
    /// The TNEF parts (<c>application/ms-tnef</c>, winmail.dat) the view unpacked: their attachments are in <see cref="Attachments"/> in their place and their bodies
    /// fill <see cref="RtfBody"/> and the bodies the MIME side lacks. The parts themselves are untouched in <see cref="Entity"/>; this is the way to the raw
    /// bytes (<c>part.GetDecodedContent()</c>). A TNEF part that does not qualify (damaged, trailing CR LF, correlation key mismatch) is not listed and
    /// stays an ordinary attachment (ambiguity V9).
    /// </summary>
    public IReadOnlyList<MimePart> TnefParts => Layout.Tnef.Select(t => t.Part).ToList();

    /// <summary>The unpacked TNEF messages, in the same order as <see cref="TnefParts"/>, for callers that want their properties.</summary>
    public IReadOnlyList<TnefMessage> TnefMessages => Layout.Tnef.Select(t => t.Message).ToList();

    /// <inheritdoc/>
    public IReadOnlyList<IMailAttachment> Attachments => Layout.Attachments;

    /// <inheritdoc/>
    public IEnumerable<MailDefect> Defects
    {
        get
        {
            foreach (MailDefect d in StoreDefectsOf())
            {
                yield return d;
            }

            MimeEntity root = Entity;
            foreach (MimeDefect d in root.EnumerateDefects())
            {
                yield return new MailDefect("mime", d.Kind.ToString(), d.Offset, d.IsAmbiguity);
            }

            if (Layout.TnefCrLf)
            {
                // OX20: a TNEF stream ending in CR LF is used although MS-OXCMAIL 2.2.3.8 says to ignore it.
                yield return new MailDefect("tnef", "AmbiguousTnefTrailingCrLf", -1, true);
            }

            // Structured headers are parsed on demand, so their defects exist only once they have been read.
            List<MimeDefect> structured = [];
            foreach (string name in StructuredAddressHeaders)
            {
                _ = root.GetAddresses(name, structured);
            }

            _ = root.GetDate(structured);
            _ = root.GetMessageId(structured);
            _ = root.GetInReplyTo(structured);
            _ = root.GetReferences(structured);
            foreach (MimeDefect d in structured)
            {
                yield return new MailDefect("mime", d.Kind.ToString(), d.Offset, d.IsAmbiguity);
            }
        }
    }

    /// <summary>Parses a message held in memory. The bytes are not copied and must not change while the item is in use.</summary>
    /// <param name="data">The message bytes.</param>
    /// <param name="options">The MIME reader's own options, or null for its defaults (including the resource limits).</param>
    /// <returns>The item. Malformed input never throws; it is reported by <see cref="Defects"/>.</returns>
    public static MimeMailItem Parse(ReadOnlyMemory<byte> data, MimeReaderOptions? options = null) =>
        new(MimeEntity.Parse(data, options), new MailProvenance(MailFormat.Eml, null, null, 0, data.Length, -1));

    /// <summary>Reads a message from a stream (to its end) and parses it.</summary>
    /// <param name="stream">A readable stream; it is left open.</param>
    /// <param name="options">The MIME reader's options, or null.</param>
    /// <returns>The item.</returns>
    public static MimeMailItem Parse(Stream stream, MimeReaderOptions? options = null) =>
        new(MimeEntity.Load(stream, options), new MailProvenance(MailFormat.Eml, null, null, 0, -1, -1));

    /// <summary>The bytes of a stored message. Only store items override this.</summary>
    internal virtual ReadOnlyMemory<byte> LoadBytes() => throw new InvalidOperationException("The item has no bytes to load.");

    /// <summary>The store-supplied arrival date, if the store has one.</summary>
    internal virtual MailDate? DeliveryDate() => null;

    /// <summary>Defects the store reader recorded for this message.</summary>
    internal virtual IEnumerable<MailDefect> StoreDefectsOf() => [];

    internal static IEnumerable<MailDefect> FromStoreDefects(Stores.StoreDefects flags)
    {
        foreach (Stores.StoreDefects bit in Enum.GetValues<Stores.StoreDefects>())
        {
            if (bit != Stores.StoreDefects.None && (flags & bit) != 0)
            {
                yield return new MailDefect("mailbox", bit.ToString(), -1, bit.ToString().StartsWith("Ambiguous", StringComparison.Ordinal));
            }
        }
    }

    private static readonly string[] StructuredAddressHeaders = ["From", "Sender", "To", "Cc", "Bcc", "Reply-To"];

    private MailAddress[] Addresses(string header)
    {
        IReadOnlyList<MimeAddress> raw = Entity.GetAddresses(header);
        if (raw.Count == 0)
        {
            return [];
        }

        List<MimeMailbox> boxes = AddressParser.Flatten(raw);
        MailAddress[] result = new MailAddress[boxes.Count];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = new MailAddress(boxes[i].Name, boxes[i].Address);
        }

        return result;
    }

    private BodyLayout Layout => _layout ??= BodyLayout.Build(Entity, this);

    // ---- body and attachment selection ----

    private sealed class BodyLayout
    {
        public MimePart? Text;
        public MimePart? Html;
        public IReadOnlyList<IMailAttachment> Attachments = [];
        public readonly List<(MimePart Part, TnefMessage Message, MapiMailItem Item)> Tnef = [];
        public bool TnefCrLf;

        public static BodyLayout Build(MimeEntity root, MimeMailItem owner)
        {
            BodyLayout l = new() { Text = FindBody(root, "plain"), Html = FindBody(root, "html") };
            List<IMailAttachment> list = [];
            Collect(root, root, l, list);
            l.Attachments = list;
            _ = owner;
            return l;
        }

        // RFC 2046 §5.1.4: the last alternative is the preferred one; related and signed parts are searched from their first child.
        private static MimePart? FindBody(MimeEntity e, string subtype)
        {
            switch (e)
            {
                case MimePart p:
                    return p.MediaTopLevelType == "text" && p.MediaSubType == subtype && p.Disposition != "attachment" ? p : null;
                case MimeMultipart m:
                    string sub = m.MediaSubType;
                    if (sub == "alternative")
                    {
                        for (int i = m.Children.Count - 1; i >= 0; i--)
                        {
                            if (FindBody(m.Children[i], subtype) is { } r)
                            {
                                return r;
                            }
                        }

                        return null;
                    }

                    if (sub is "related" or "signed")
                    {
                        return m.Children.Count > 0 ? FindBody(m.Children[0], subtype) : null;
                    }

                    foreach (MimeEntity c in m.Children)
                    {
                        if (FindBody(c, subtype) is { } r)
                        {
                            return r;
                        }
                    }

                    return null;
                default:
                    return null;
            }
        }

        private static void Collect(MimeEntity message, MimeEntity e, BodyLayout l, List<IMailAttachment> into, bool inAlternative = false)
        {
            switch (e)
            {
                case MimeMultipart m:
                    foreach (MimeEntity c in m.Children)
                    {
                        Collect(message, c, l, into, m.MediaSubType == "alternative");
                    }

                    break;
                case MimePart p:
                    // RFC 2046 §5.1.4: the children of multipart/alternative are renderings of one body; a text rendering that is
                    // not marked as an attachment is an alternative of the body, not a file.
                    if (p.IsTnef() && TnefInMime.TryRead(p, message, null, out TnefMessage? tnef, out TnefInMime.Refusal note))
                    {
                        // MS-OXCMAIL 2.2.3.8 and ambiguity V9: Outlook shows what the TNEF holds in place of winmail.dat.
                        var item = new MapiMailItem(tnef!, new MailProvenance(MailFormat.Tnef, null, null, 0, tnef!.Document.Source.Length, -1));
                        l.Tnef.Add((p, tnef!, item));
                        l.TnefCrLf |= note == TnefInMime.Refusal.TrailingCrLf;
                        into.AddRange(item.Attachments);
                        break;
                    }

                    bool alternativeText = inAlternative && p.MediaTopLevelType == "text" && p.Disposition != "attachment";
                    if (!alternativeText && !ReferenceEquals(p, l.Text) && !ReferenceEquals(p, l.Html))
                    {
                        into.Add(new MimeAttachment(p));
                    }

                    break;
                case MimeMessagePart mp:
                    into.Add(new MimeAttachment(mp));
                    break;
            }
        }
    }

    private sealed class MimeMailBody(MimePart part) : MailBody
    {
        public override string ContentType => part.MediaType;

        public override string? Charset => part.GetContentTypeParameter("charset")?.ToLowerInvariant();

        public override Stream Open() => part.OpenDecodedContent();

        public override string ReadText()
        {
            byte[] bytes = part.GetDecodedContent();
            string? label = part.GetContentTypeParameter("charset");
            MailEncoding? enc = label is null ? null : MailEncoding.ForLabel(label);
            if (enc is null)
            {
                // No usable charset: modern mail is UTF-8; legacy mail that is not valid UTF-8 is overwhelmingly Windows-1252 (ambiguity N5).
                enc = Utf8.IsValid(bytes) ? MailEncoding.Utf8 : MailEncoding.Windows1252;
            }

            return enc.GetString(bytes);
        }
    }

    private sealed class MimeAttachment(MimeEntity entity) : IMailAttachment
    {
        private long _size = -1;

        public string? FileName => entity.FileName;

        public string ContentType => entity.MediaType;

        public string? ContentId => entity.GetContentId();

        public bool IsInline => entity.Disposition != "attachment" && entity.Headers.GetFirst("Content-ID") is not null && entity.GetContentId() is not null;

        public long Size
        {
            get
            {
                if (_size < 0)
                {
                    _size = Measure();
                }

                return _size;
            }
        }

        public Stream Open() => entity switch
        {
            MimePart p => p.OpenDecodedContent(),
            MimeMessagePart { Message: { } m } => new MemoryStream(m.ToArray(), writable: false),
            _ => new MemoryStream([], writable: false),
        };

        public IMailItem? Embedded => entity is MimeMessagePart { Message: { } m }
            ? new MimeMailItem(m, new MailProvenance(MailFormat.Eml, null, null, m.SourceStart, m.SourceLength, -1))
            : null;

        private long Measure()
        {
            using Stream s = Open();
            byte[] buf = new byte[16384];
            long total = 0;
            int n;
            while ((n = s.Read(buf)) > 0)
            {
                total += n;
            }

            return total;
        }
    }
}
