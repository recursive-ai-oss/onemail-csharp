using System.Globalization;
using System.Text;
using System.Text.Unicode;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Rtf.Compression;
using RecursiveAI.OneMail.Text;
using RecursiveAI.OneMail.Tnef;

namespace RecursiveAI.OneMail.Conversion.OxcMail;

/// <summary>
/// Gets a first look at each leaf part while a message is converted, before the converter classifies it as a body or an attachment.
/// This is where a caller merges a TNEF part (<c>application/ms-tnef</c>, winmail.dat) into the result: read it, add its properties and
/// attachments to <paramref name="message"/>, and return true.
/// </summary>
/// <param name="part">The leaf part.</param>
/// <param name="message">The message being built; the handler may add properties, recipients and attachments.</param>
/// <param name="report">The conversion's report, for anything the handler leaves behind.</param>
/// <returns>True when the handler dealt with the part and the converter should skip it; false to let the converter handle it.</returns>
public delegate bool OxcMailPartHandler(MimePart part, MapiMessage message, LossReport report);

/// <summary>What <see cref="MimeToMapiConverter"/> does with a TNEF part (<c>application/ms-tnef</c>, winmail.dat).</summary>
public enum OxcMailTnefHandling
{
    /// <summary>
    /// MS-OXCMAIL 2.2.3.8: the part is read as TNEF and its properties, recipients and attachments become the message's, with the MIME side
    /// winning conflicts. A part that does not qualify (damaged, trailing CR LF, correlation key mismatch) stays an attachment.
    /// </summary>
    Merge = 0,

    /// <summary>The part is an ordinary attachment, as for any other binary part.</summary>
    KeepAsAttachment = 1,
}

/// <summary>Settings for <see cref="MimeToMapiConverter"/>.</summary>
public sealed class OxcMailImportOptions
{
    /// <summary>What to do with TNEF parts. <see cref="PartHandler"/> sees them first and can claim them. Default <see cref="OxcMailTnefHandling.Merge"/>.</summary>
    public OxcMailTnefHandling Tnef { get; init; } = OxcMailTnefHandling.Merge;

    /// <summary>Limits for reading TNEF parts, or null for the TNEF reader's defaults.</summary>
    public TnefReaderOptions? TnefReader { get; init; }

    /// <summary>Called for every leaf part first; see <see cref="OxcMailPartHandler"/>. Null (the default) means no part is handled by the caller.</summary>
    public OxcMailPartHandler? PartHandler { get; init; }

    /// <summary>The deepest nesting of embedded messages converted; a deeper one is left out and reported. Default 16.</summary>
    public int MaxEmbeddedDepth { get; init; } = 16;

    /// <summary>The deepest nesting of multiparts walked. Default 64.</summary>
    public int MaxPartDepth { get; init; } = 64;
}

/// <summary>
/// Converts an Internet message to an Outlook message as MS-OXCMAIL section 2.2 specifies (the "receiving" direction): the header
/// fields become properties, the text, HTML and RTF parts become the body properties, other parts become attachments and
/// <c>message/rfc822</c> parts become embedded messages. The received header block is kept whole in
/// <c>PidTagTransportMessageHeaders</c>, so what the properties cannot hold is not lost with the original headers. The entity tree
/// is not changed. What does not carry over is in the <see cref="LossReport"/>; <c>docs/conversion.md</c> has the rules.
/// </summary>
public sealed class MimeToMapiConverter : IItemConversion<MimeEntity, MapiMessage>
{
    private readonly OxcMailImportOptions _options;

    /// <summary>Creates a converter.</summary>
    /// <param name="options">Settings, or null for the defaults.</param>
    public MimeToMapiConverter(OxcMailImportOptions? options = null) => _options = options ?? new OxcMailImportOptions();

    /// <inheritdoc/>
    public ConversionResult<MapiMessage> Convert(MimeEntity source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var report = new LossReport();
        MapiMessage m = new Importer(_options, report).Import(source, null, string.Empty, 0);
        return new ConversionResult<MapiMessage>(m, report);
    }

    private static readonly MapiNamedProperty Keywords = new(MapiPropertySets.PublicStrings, "Keywords");

    // MS-OXCMAIL 2.2.3.2.29: headers that are mapped elsewhere or that must not become PS_INTERNET_HEADERS properties.
    private static readonly HashSet<string> MappedOrExcluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "Subject", "From", "Sender", "To", "Cc", "Bcc", "Reply-To", "Date", "Message-ID", "In-Reply-To", "References", "Importance", "Priority",
        "X-Priority", "X-MSMail-Priority", "Sensitivity", "Keywords", "Thread-Topic", "Thread-Index", "Disposition-Notification-To", "Return-Receipt-To",
        "Received", "Resent-From", "Resent-To", "Resent-Cc", "Resent-Bcc", "Resent-Sender", "Resent-Date", "Resent-Message-Id", "Content-Type",
        "Content-Disposition", "Content-Description", "Content-Transfer-Encoding", "Content-ID", "Content-MD5", "MIME-Version", "Return-Path",
        "Comments", "AdHoc", "Apparently-To", "Content-Location", "Content-Base",
    };

    private sealed class Importer(OxcMailImportOptions options, LossReport report)
    {
        private int _nextNamedId = 0x8000;
        private MimeEntity? _root;
        private readonly List<TnefMessage> _tnef = [];
        private readonly Dictionary<MapiNamedProperty, ushort> _namedIds = [];

        public MapiMessage Import(MimeEntity root, MapiAttachment? parent, string path, int depth)
        {
            _root = root;
            var m = new MapiMessage(parent);
            m.Properties.Add(MapiProps.Str(MapiPid.MessageClass, MessageClassOf(root, path)));
            MapHeaders(m, root, path);
            var bodies = new BodyCollector { TextPart = FindBody(root, "plain"), HtmlPart = FindBody(root, "html"), RtfPart = FindRtf(root) };
            var attachments = new List<MapiAttachment>();
            Walk(root, m, bodies, attachments, path, depth, 0, signed: false);
            ApplyBodies(m, bodies);
            foreach (TnefMessage t in _tnef)
            {
                TnefMerger.Merge(m, t, report, IdFor, path);
                TnefMerger.MergeRecipients(m, t, report, path);
            }

            for (int i = 0; i < attachments.Count; i++)
            {
                attachments[i].Properties.Set(MapiProps.Int(MapiPid.AttachNumber, i));
                m.AddAttachment(attachments[i]);
            }

            if (attachments.Count > 0)
            {
                m.Properties.Set(MapiProps.Bool(MapiPid.HasAttachments, true));
            }

            return m;
        }

        // ---- message class ------------------------------------------------------------------------------------------------

        private string MessageClassOf(MimeEntity root, string path)
        {
            string type = root.MediaType;
            if (type == "multipart/report")
            {
                string? rt = root.GetContentTypeParameter("report-type")?.ToLowerInvariant();
                if (rt == "delivery-status")
                {
                    return "REPORT.IPM.Note.NDR";
                }

                if (rt == "disposition-notification")
                {
                    return "REPORT.IPM.Note.IPNRN";
                }
            }

            if (type == "multipart/signed")
            {
                report.Add("mime", "multipart/signed", LossKind.Approximated, "SignatureNotVerifiable",
                    "The signed content and the signature become a body and an attachment; the signature cannot be checked against the converted form.", path);
                return "IPM.Note.SMIME.MultipartSigned";
            }

            if (type == "application/pkcs7-mime" || type == "application/x-pkcs7-mime")
            {
                return "IPM.Note.SMIME";
            }

            return "IPM.Note";
        }

        // ---- headers ------------------------------------------------------------------------------------------------

        private void MapHeaders(MapiMessage m, MimeEntity e, string path)
        {
            MimeHeaderList h = e.Headers;
            var bag = m.Properties;

            // MS-OXCMAIL 2.2.3.2.28: the received headers are kept whole.
            var tb = new StringBuilder();
            foreach (MimeHeader f in h)
            {
                if (f.Name.StartsWith("X-MS-Exchange-Organization-", StringComparison.OrdinalIgnoreCase) || f.Name.StartsWith("X-MS-Exchange-Forest-", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                tb.Append(Decode(f.Raw.Span));
                if (tb.Length > 0 && tb[^1] != '\n')
                {
                    tb.Append("\r\n");
                }
            }

            if (tb.Length > 0)
            {
                tb.Append("\r\n");
                bag.Add(MapiProps.Str(MapiPid.TransportMessageHeaders, tb.ToString()));
            }

            if (h.GetFirst("Subject") is { } subj)
            {
                string subject = Rfc2047.DecodeText(subj.RawValue.Span).Trim();
                bag.Add(MapiProps.Str(MapiPid.Subject, subject));
                (string prefix, string normalized) = SplitSubject(subject);
                bag.Add(MapiProps.Str(MapiPid.SubjectPrefix, prefix));

                bag.Add(MapiProps.Str(MapiPid.NormalizedSubject, normalized));
            }

            List<MimeMailbox> from = Mailboxes(e, "From", path);
            List<MimeMailbox> sender = Mailboxes(e, "Sender", path);
            MimeMailbox? representing = from.Count > 0 ? from[0] : null;
            MimeMailbox? actual = sender.Count > 0 ? sender[0] : representing;
            if (representing is not null)
            {
                Party(bag, representing, MapiPid.SentRepresentingName, MapiPid.SentRepresentingAddressType, MapiPid.SentRepresentingEmailAddress, MapiPid.SentRepresentingSmtpAddress);
            }

            if (actual is not null)
            {
                Party(bag, actual, MapiPid.SenderName, MapiPid.SenderAddressType, MapiPid.SenderEmailAddress, MapiPid.SenderSmtpAddress);
            }

            if (from.Count > 1)
            {
                report.Add("mime", "From", LossKind.Dropped, "ExtraAuthorsNotMapped", "The message has several authors; only the first is a sender in the Outlook model (all stay in the transport headers).", path);
            }

            int row = 0;
            var display = new Dictionary<MapiRecipientType, List<string>>();
            foreach ((string name, MapiRecipientType type) in new[] { ("To", MapiRecipientType.To), ("Cc", MapiRecipientType.Cc), ("Bcc", MapiRecipientType.Bcc) })
            {
                foreach (MimeMailbox mb in Mailboxes(e, name, path))
                {
                    var r = new MapiRecipient(m);
                    string shown = mb.Name ?? mb.Address;
                    r.Properties.Add(MapiProps.Int(MapiPid.RecipientType, (int)type));
                    r.Properties.Add(MapiProps.Str(MapiPid.DisplayName, shown));
                    r.Properties.Add(MapiProps.Str(MapiPid.AddressType, "SMTP"));
                    r.Properties.Add(MapiProps.Str(MapiPid.EmailAddress, mb.Address));
                    r.Properties.Add(MapiProps.Str(MapiPid.SmtpAddress, mb.Address));
                    r.Properties.Add(MapiProps.Int(MapiPid.Rowid, row++));
                    r.Properties.Add(MapiProps.Int(MapiPid.ObjectType, 6));
                    r.Properties.Add(MapiProps.Int(MapiPid.DisplayType, 0));
                    m.AddRecipient(r);
                    if (!display.TryGetValue(type, out List<string>? l))
                    {
                        display[type] = l = [];
                    }

                    l.Add(shown);
                }
            }

            foreach ((MapiRecipientType type, ushort id) in new[] { (MapiRecipientType.To, MapiPid.DisplayTo), (MapiRecipientType.Cc, MapiPid.DisplayCc), (MapiRecipientType.Bcc, MapiPid.DisplayBcc) })
            {
                if (display.TryGetValue(type, out List<string>? l))
                {
                    bag.Add(MapiProps.Str(id, string.Join("; ", l)));
                }
            }

            List<MimeMailbox> reply = Mailboxes(e, "Reply-To", path);
            if (reply.Count > 0)
            {
                var entries = new List<byte[]>();
                var names = new List<string>();
                foreach (MimeMailbox mb in reply)
                {
                    string shown = mb.Name ?? mb.Address;
                    entries.Add(OneOffEntryId.Build(shown, "SMTP", mb.Address));
                    names.Add(shown);
                }

                bag.Add(MapiProps.Bin(MapiPid.ReplyRecipientEntries, OneOffEntryId.BuildList(entries)));
                bag.Add(MapiProps.Str(MapiPid.ReplyRecipientNames, string.Join("; ", names)));
            }

            if (e.GetDate() is { } date && !MapiProps.FitsFileTime(date.Utc))
            {
                // A Date field before 1601 cannot be a FILETIME.
                report.Add("mime", "Date", LossKind.Dropped, "DateBeforeFileTimeEpoch", "The Date field is before 1601-01-01, which a time property cannot hold.", path);
            }
            else if (e.GetDate() is { } date2)
            {
                bag.Add(MapiProps.Time(MapiPid.ClientSubmitTime, date2.Utc));
                if (date2.ZoneKnown && date2.OffsetMinutes != 0)
                {
                    report.Add("mime", "Date", LossKind.Approximated, "DateZoneNotStored", "The property holds UTC; the sender's zone offset survives only in the transport headers.", path);
                }
            }

            // MS-OXCMAIL 2.2.3.2.28 has the delivery time from the first Received field; the time follows its last semicolon.
            if (h.GetFirst("Received") is { } rec)
            {
                string v = rec.Value;
                int semi = v.LastIndexOf(';');
                if (semi >= 0 && DateParser.TryParse(Encoding.UTF8.GetBytes(v[(semi + 1)..]), out MimeDate rd) && MapiProps.FitsFileTime(rd.Utc))
                {
                    bag.Add(MapiProps.Time(MapiPid.MessageDeliveryTime, rd.Utc));
                }
            }

            string? mid = e.GetMessageId();
            if (mid is not null)
            {
                bag.Add(MapiProps.Str(MapiPid.InternetMessageId, "<" + mid + ">"));
            }

            IReadOnlyList<string> irt = e.GetInReplyTo();
            if (irt.Count > 0)
            {
                bag.Add(MapiProps.Str(MapiPid.InReplyToId, "<" + irt[0] + ">"));
                if (irt.Count > 1)
                {
                    report.Add("mime", "In-Reply-To", LossKind.Approximated, "InReplyToFirstOnly", "PidTagInReplyToId holds one identifier; the others stay in the transport headers.", path);
                }
            }

            IReadOnlyList<string> refs = e.GetReferences();
            if (refs.Count > 0)
            {
                bag.Add(MapiProps.Str(MapiPid.InternetReferences, string.Join(' ', refs.Select(x => "<" + x + ">"))));
            }

            int? importance = ImportanceOf(h);
            if (importance is { } imp)
            {
                bag.Add(MapiProps.Int(MapiPid.Importance, imp));
            }

            if (h.GetFirst("Sensitivity") is { } sens)
            {
                int? sv = sens.Value.Trim().ToLowerInvariant() switch
                {
                    "normal" => 0,
                    "personal" => 1,
                    "private" => 2,
                    "company-confidential" => 3,
                    _ => null,
                };
                if (sv is { } s)
                {
                    bag.Add(MapiProps.Int(MapiPid.Sensitivity, s));
                }
            }

            if (h.GetFirst("Keywords") is { } kw)
            {
                var words = new List<string>();
                foreach (string w in Rfc2047.DecodeText(kw.RawValue.Span).Split(','))
                {
                    if (w.Trim() is { Length: > 0 } t)
                    {
                        words.Add(t);
                    }
                }

                if (words.Count > 0)
                {
                    bag.Add(MapiProps.NamedStrings(IdFor(Keywords), Keywords, words));
                }
            }

            if (h.GetFirst("Thread-Topic") is { } topic)
            {
                bag.Add(MapiProps.Str(MapiPid.ConversationTopic, Rfc2047.DecodeText(topic.RawValue.Span).Trim()));
            }

            if (h.GetFirst("Thread-Index") is { } ti)
            {
                try
                {
                    bag.Add(MapiProps.Bin(MapiPid.ConversationIndex, System.Convert.FromBase64String(string.Concat(ti.Value.Where(c => !char.IsWhiteSpace(c))))));
                }
                catch (FormatException)
                {
                    // MS-OXCMAIL 2.2.3.2.8: a Thread-Index that is not base64 is ignored.
                    report.Add("mime", "Thread-Index", LossKind.Dropped, "ThreadIndexNotBase64", "The value is not base64 and is ignored.", path);
                }
            }

            if (h.GetFirst("Disposition-Notification-To") is not null)
            {
                bag.Add(MapiProps.Bool(MapiPid.ReadReceiptRequested, true));
            }

            if (h.GetFirst("Return-Receipt-To") is not null)
            {
                bag.Add(MapiProps.Bool(MapiPid.OriginatorDeliveryReportRequested, true));
            }

            // MS-OXCMAIL 2.2.3.2.29: every other field is a string-named property of PS_INTERNET_HEADERS.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (MimeHeader f in h)
            {
                string name = f.Name;
                if (MappedOrExcluded.Contains(name))
                {
                    continue;
                }

                if (!seen.Add(name.ToLowerInvariant()))
                {
                    report.Add("mime", name, LossKind.Dropped, "RepeatedHeaderNotMapped", "A named property holds one value; the later fields of the same name stay in the transport headers.", path);
                    continue;
                }

                // The name is kept as the message wrote it: header names are case-insensitive (RFC 5322 §1.2.2) and stores differ in the case they keep.
                var np = new MapiNamedProperty(MapiPropertySets.InternetHeaders, name);
                bag.Add(MapiProps.NamedStr(IdFor(np), np, Rfc2047.DecodeText(f.RawValue.Span).Trim()));
            }

            ReportUnmappedRepeats(h, path);
        }

        // Fields the mapping reads once: a second one is not used.
        private void ReportUnmappedRepeats(MimeHeaderList h, string path)
        {
            foreach (string name in new[] { "Subject", "Date", "Message-ID", "Sender", "To", "Cc", "Bcc", "Reply-To", "Importance", "Sensitivity", "Thread-Topic", "Thread-Index", "Keywords" })
            {
                int n = 0;
                foreach (MimeHeader f in h.GetAll(name))
                {
                    if (++n == 2)
                    {
                        report.Add("mime", name, LossKind.Dropped, "RepeatedHeaderNotMapped", "Only the first " + name + " field is mapped (the others stay in the transport headers).", path);
                    }
                }
            }
        }

        private ushort IdFor(MapiNamedProperty np)
        {
            if (!_namedIds.TryGetValue(np, out ushort id))
            {
                id = (ushort)Math.Min(_nextNamedId++, 0xFFFE);
                _namedIds[np] = id;
            }

            return id;
        }

        private static string Decode(ReadOnlySpan<byte> raw) =>
            Utf8.IsValid(raw) ? Encoding.UTF8.GetString(raw) : Encoding.Latin1.GetString(raw);

        // MS-OXCMAIL 2.2.3.2.6.1: one to three characters other than colon, blank or digit, then a colon and blanks, are the prefix.
        internal static (string Prefix, string Normalized) SplitSubject(string subject)
        {
            int i = 0;
            while (i < subject.Length && i < 3 && subject[i] is not (':' or ' ') && !char.IsAsciiDigit(subject[i]))
            {
                i++;
            }

            if (i > 0 && i < subject.Length && subject[i] == ':')
            {
                int j = i + 1;
                while (j < subject.Length && subject[j] == ' ')
                {
                    j++;
                }

                return (subject[..i] + ": ", subject[j..]);
            }

            return (string.Empty, subject);
        }

        private List<MimeMailbox> Mailboxes(MimeEntity e, string header, string path)
        {
            IReadOnlyList<MimeAddress> list = e.GetAddresses(header);
            foreach (MimeAddress a in list)
            {
                if (a is MimeGroup g)
                {
                    report.Add("mime", header, LossKind.Approximated, "GroupFlattened", "The group name \"" + g.Name + "\" is not kept; its members are listed individually (they stay in the transport headers with the group).", path);
                }
            }

            return AddressParser.Flatten(list);
        }

        private static void Party(MapiPropertyBag bag, MimeMailbox mb, ushort name, ushort type, ushort email, ushort smtp)
        {
            bag.Add(MapiProps.Str(name, mb.Name ?? mb.Address));
            bag.Add(MapiProps.Str(type, "SMTP"));
            bag.Add(MapiProps.Str(email, mb.Address));
            bag.Add(MapiProps.Str(smtp, mb.Address));
        }

        // MS-OXCMAIL 2.2.3.2.5: Importance first, then Priority, X-Priority and X-MSMail-Priority.
        private static int? ImportanceOf(MimeHeaderList h)
        {
            if (h.GetFirst("Importance") is { } i)
            {
                return i.Value.Trim().ToLowerInvariant() switch { "low" => 0, "normal" => 1, "high" => 2, _ => null };
            }

            if (h.GetFirst("Priority") is { } p)
            {
                return p.Value.Trim().ToLowerInvariant() switch { "non-urgent" => 0, "normal" => 1, "urgent" => 2, _ => null };
            }

            if (h.GetFirst("X-Priority") is { } x)
            {
                string v = x.Value.Trim();
                if (v.Length > 0 && char.IsAsciiDigit(v[0]))
                {
                    return v[0] switch { '1' or '2' => 2, '3' => 1, '4' or '5' => 0, _ => null };
                }
            }

            if (h.GetFirst("X-MSMail-Priority") is { } ms)
            {
                return ms.Value.Trim().ToLowerInvariant() switch { "low" => 0, "normal" => 1, "high" => 2, _ => null };
            }

            return null;
        }

        // ---- parts ------------------------------------------------------------------------------------------------

        private sealed class BodyCollector
        {
            public string? Text;
            public (byte[] Bytes, int CodePage)? Html;
            public byte[]? Rtf;

            // The parts chosen as bodies; every other leaf is an attachment.
            public MimePart? TextPart;
            public MimePart? HtmlPart;
            public MimePart? RtfPart;
        }

        private void Walk(MimeEntity e, MapiMessage m, BodyCollector bodies, List<MapiAttachment> attachments, string path, int depth, int level, bool signed)
        {
            if (level > options.MaxPartDepth)
            {
                report.Add("mime", "part", LossKind.Dropped, "PartsTooDeep", "The part is nested deeper than the limit and is left out.", path);
                return;
            }

            switch (e)
            {
                case MimeMultipart mp:
                    foreach (MimeEntity child in mp.Children)
                    {
                        Walk(child, m, bodies, attachments, path, depth, level + 1, signed || mp.MediaType == "multipart/signed");
                    }

                    break;
                case MimeMessagePart msg:
                    AddAttachment(attachments, msg, null, m, path, depth);
                    break;
                case MimePart leaf:
                    if (options.PartHandler is { } handler && handler(leaf, m, report))
                    {
                        break;
                    }

                    if (options.Tnef == OxcMailTnefHandling.Merge && leaf.IsTnef())
                    {
                        if (TnefInMime.TryRead(leaf, _root!, options.TnefReader, out TnefMessage? t, out TnefInMime.Refusal refusal))
                        {
                            _tnef.Add(t!);
                            if (refusal == TnefInMime.Refusal.TrailingCrLf)
                            {
                                (string noteCode, string noteWhy) = TnefInMime.Describe(refusal);
                                report.Add("mime", leaf.MediaType, LossKind.Internal, noteCode, noteWhy, path);
                            }

                            attachments.AddRange(t!.Attachments);
                            report.Add("mime", leaf.MediaType, LossKind.Internal, "TnefPartUnpacked", "The TNEF part is not an attachment: its properties, recipients and attachments are the message's (MS-OXCMAIL 2.2.3.8).", path);
                            break;
                        }

                        (string code, string why) = TnefInMime.Describe(refusal);
                        report.Add("mime", leaf.MediaType, LossKind.Approximated, code, why, path);
                        AddAttachment(attachments, leaf, leaf, m, path, depth, forceType: "application/octet-stream");
                        break;
                    }

                    if (!TryBody(leaf, bodies))
                    {
                        AddAttachment(attachments, leaf, leaf, m, path, depth);
                    }

                    break;
            }
        }

        // RFC 2046 §5.1.4: the last alternative is the preferred one; related and signed parts are searched from their first child. A text
        // part is a body unless it is marked as an attachment (MS-OXCMAIL 2.2.3.3.2), as the shared view reads MIME.
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

        // An RTF part is the RTF body when it is the first one that is neither marked as an attachment nor named.
        private static MimePart? FindRtf(MimeEntity e)
        {
            switch (e)
            {
                case MimePart p:
                    return p.MediaType is "text/rtf" or "application/rtf" && p.Disposition != "attachment" && p.FileName is null ? p : null;
                case MimeMultipart m:
                    foreach (MimeEntity c in m.Children)
                    {
                        if (FindRtf(c) is { } r)
                        {
                            return r;
                        }
                    }

                    return null;
                default:
                    return null;
            }
        }

        private static bool TryBody(MimePart p, BodyCollector bodies)
        {
            if (ReferenceEquals(p, bodies.TextPart))
            {
                bodies.Text = DecodeText(p);
                return true;
            }

            if (ReferenceEquals(p, bodies.HtmlPart))
            {
                bodies.Html = HtmlBytes(p);
                return true;
            }

            if (ReferenceEquals(p, bodies.RtfPart))
            {
                bodies.Rtf = p.GetDecodedContent();
                return true;
            }

            return false;
        }

        private static string DecodeText(MimePart p)
        {
            byte[] bytes = p.GetDecodedContent();
            string? label = p.GetContentTypeParameter("charset");
            MailEncoding? enc = label is null ? null : MailEncoding.ForLabel(label);
            enc ??= Utf8.IsValid(bytes) ? MailEncoding.Utf8 : MailEncoding.Windows1252;
            return enc.GetString(bytes);
        }

        // HTML keeps its bytes and names their code page in PidTagInternetCodepage (MS-OXCMAIL 2.1.3.2.1); a charset without a Windows
        // code page number, or none, is read as for text and stored as UTF-8.
        private static (byte[], int) HtmlBytes(MimePart p)
        {
            byte[] bytes = p.GetDecodedContent();
            string? label = p.GetContentTypeParameter("charset");
            MailEncoding? enc = label is null ? null : MailEncoding.ForLabel(label);
            if (enc is { CodePage: > 0 and not (1200 or 1201) and int cp } && enc.CanEncode)
            {
                return (bytes, cp);
            }

            enc ??= Utf8.IsValid(bytes) ? MailEncoding.Utf8 : MailEncoding.Windows1252;
            return (Encoding.UTF8.GetBytes(enc.GetString(bytes)), 65001);
        }

        private static void ApplyBodies(MapiMessage m, BodyCollector b)
        {
            MapiPropertyBag bag = m.Properties;
            if (b.Text is not null)
            {
                bag.Add(MapiProps.Str(MapiPid.Body, b.Text));
            }

            int codePage = 65001;
            if (b.Html is { } h)
            {
                bag.Add(MapiProps.Bin(MapiPid.Html, h.Bytes));
                codePage = h.CodePage;
            }

            if (b.Rtf is not null)
            {
                bag.Add(MapiProps.Bin(MapiPid.RtfCompressed, RtfCompressor.Compress(b.Rtf)));
            }

            if (b.Text is not null || b.Html is not null || b.Rtf is not null)
            {
                bag.Add(MapiProps.Int(MapiPid.InternetCodepage, codePage));
                bag.Add(MapiProps.Int(MapiPid.NativeBody, b.Html is not null ? 3 : b.Rtf is not null ? 2 : 1));
            }
        }

        private void AddAttachment(List<MapiAttachment> list, MimeEntity e, MimePart? leaf, MapiMessage owner, string path, int depth, string? forceType = null)
        {
            int index = list.Count;
            string where = path.Length == 0 ? $"attachment[{index}]" : $"{path}/attachment[{index}]";
            var a = new MapiAttachment(owner);
            string? fileName = e.FileName;
            string type = forceType ?? e.MediaType;
            string? cid = e.GetContentId();
            string? disposition = e.Headers.GetFirst("Content-Disposition")?.Value;
            string? location = e.Headers.GetFirst("Content-Location")?.Value.Trim();
            MapiPropertyBag bag = a.Properties;

            if (e is MimeMessagePart { Message: { } inner })
            {
                if (depth >= options.MaxEmbeddedDepth)
                {
                    report.Add("mime", type, LossKind.Dropped, "EmbeddedMessageTooDeep", "The embedded message is nested deeper than the limit.", where);
                    return;
                }

                var innerReport = new LossReport();
                MapiMessage embedded = new Importer(options, innerReport).Import(inner, a, string.Empty, depth + 1);
                report.Merge(innerReport, where + "/message");
                a.SetEmbeddedMessage(embedded);
                bag.Add(MapiProps.Int(MapiPid.AttachMethod, 5));
                if (fileName is not null)
                {
                    bag.Add(MapiProps.Str(MapiPid.DisplayName, fileName));
                }
            }
            else
            {
                byte[] data = leaf?.GetDecodedContent() ?? [];
                bag.Add(MapiProps.Int(MapiPid.AttachMethod, 1));
                bag.Add(MapiProps.Bin(MapiPid.AttachDataBinary, data));
                bag.Add(MapiProps.Int(MapiPid.AttachSize, data.Length));
                if (fileName is not null)
                {
                    bag.Add(MapiProps.Str(MapiPid.DisplayName, fileName));
                }
            }

            if (fileName is not null)
            {
                bag.Add(MapiProps.Str(MapiPid.AttachLongFilename, fileName));
                bag.Add(MapiProps.Str(MapiPid.AttachFilename, fileName));
                string ext = Path.GetExtension(fileName);
                if (ext.Length > 0)
                {
                    bag.Add(MapiProps.Str(MapiPid.AttachExtension, ext));
                }
            }

            bag.Add(MapiProps.Str(MapiPid.AttachMimeTag, type));
            bag.Add(MapiProps.Int(MapiPid.RenderingPosition, -1));
            if (cid is not null)
            {
                bag.Add(MapiProps.Str(MapiPid.AttachContentId, cid));
            }

            if (!string.IsNullOrEmpty(location))
            {
                bag.Add(MapiProps.Str(MapiPid.AttachContentLocation, location));
            }

            if (disposition is not null)
            {
                bag.Add(MapiProps.Str(0x3716, disposition));
            }

            bool inline = cid is not null && e.Disposition != "attachment";
            bag.Add(MapiProps.Int(MapiPid.AttachFlags, inline ? 4 : 0));

            foreach (MimeHeader f in e.Headers)
            {
                if (!(f.IsNamed("Content-Type") || f.IsNamed("Content-Disposition") || f.IsNamed("Content-ID") || f.IsNamed("Content-Location")
                      || f.IsNamed("Content-Transfer-Encoding") || f.IsNamed("MIME-Version")))
                {
                    report.Add("mime", f.Name, LossKind.Dropped, "PartHeaderNotMapped", "MS-OXCMAIL maps no attachment property from this part header.", where);
                }
            }

            list.Add(a);
        }
    }
}
