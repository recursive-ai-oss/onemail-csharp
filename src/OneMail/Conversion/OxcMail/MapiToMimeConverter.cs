using System.Globalization;
using System.Text;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Mime.Transfer;
using RecursiveAI.OneMail.Rtf;
using RecursiveAI.OneMail.Text;

namespace RecursiveAI.OneMail.Conversion.OxcMail;

/// <summary>Settings for <see cref="MapiToMimeConverter"/>.</summary>
public sealed class OxcMailExportOptions
{
    /// <summary>
    /// Use <c>PidTagTransportMessageHeaders</c>, when the message has it, as the header block of the Internet message (MS-OXCMAIL
    /// 2.2.3.2.28 says readers keep the received headers there). Default true. Fields the properties also define are kept as received when
    /// they agree with the properties and replaced by the properties' values when they do not, with an <see cref="LossKind.Overridden"/> entry.
    /// </summary>
    public bool UseTransportHeaders { get; init; } = true;

    /// <summary>Write the Bcc header (and keep one found in the transport headers). Default false: a message that travels does not name its blind copies.</summary>
    public bool IncludeBcc { get; init; }

    /// <summary>Supplies multipart boundaries, for reproducible output. Null (the default) generates random ones.</summary>
    public Func<string>? BoundaryFactory { get; init; }

    /// <summary>The deepest nesting of embedded messages converted; a deeper one is left out and reported. Default 16.</summary>
    public int MaxEmbeddedDepth { get; init; } = 16;
}

/// <summary>
/// Converts an Outlook message to an Internet message as MS-OXCMAIL section 2.1 specifies (the "sending" direction): envelope and
/// address properties, dates, identifiers, importance, sensitivity, categories, the three body forms, attachments by value and embedded
/// messages. The result is a new entity tree built with the MIME writer's types; the source is not changed. Everything that
/// has no Internet form is listed in the <see cref="LossReport"/>; <c>docs/conversion.md</c> has the rules.
/// </summary>
public sealed class MapiToMimeConverter : IItemConversion<MapiMessage, MimeEntity>
{
    private readonly OxcMailExportOptions _options;

    /// <summary>Creates a converter.</summary>
    /// <param name="options">Settings, or null for the defaults.</param>
    public MapiToMimeConverter(OxcMailExportOptions? options = null) => _options = options ?? new OxcMailExportOptions();

    /// <inheritdoc/>
    public ConversionResult<MimeEntity> Convert(MapiMessage source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var report = new LossReport();
        MimeEntity root = new Exporter(_options, report).Export(source, string.Empty, 0);
        return new ConversionResult<MimeEntity>(root, report);
    }

    private const ushort PidAttachContentDisposition = 0x3716;
    private const int AttachFlagMhtmlRef = 0x4;

    private static readonly MapiNamedProperty Keywords = new(MapiPropertySets.PublicStrings, "Keywords");

    // Message properties the conversion reads, and the types it reads them as: a property of another type was not read and is reported.
    private static readonly Dictionary<ushort, MapiType[]> Consumed = BuildConsumed();

    private static Dictionary<ushort, MapiType[]> BuildConsumed()
    {
        MapiType[] text = [MapiType.UnicodeString, MapiType.String8];
        MapiType[] integer = [MapiType.Integer32];
        MapiType[] flag = [MapiType.Boolean];
        MapiType[] time = [MapiType.Time];
        MapiType[] binary = [MapiType.Binary];
        MapiType[] anyBody = [MapiType.UnicodeString, MapiType.String8, MapiType.Binary];
        var d = new Dictionary<ushort, MapiType[]>();
        foreach (ushort id in new[]
        {
            MapiPid.Subject, MapiPid.SubjectPrefix, MapiPid.NormalizedSubject, MapiPid.SentRepresentingName, MapiPid.SentRepresentingEmailAddress,
            MapiPid.SentRepresentingAddressType, MapiPid.SentRepresentingSmtpAddress, MapiPid.SenderName, MapiPid.SenderEmailAddress,
            MapiPid.SenderAddressType, MapiPid.SenderSmtpAddress, MapiPid.InternetMessageId, MapiPid.InReplyToId, MapiPid.InternetReferences,
            MapiPid.ConversationTopic, MapiPid.Body, MapiPid.TransportMessageHeaders, MapiPid.ReplyRecipientNames,
        })
        {
            d[id] = text;
        }

        d[MapiPid.Html] = anyBody;
        d[MapiPid.Importance] = integer;
        d[MapiPid.Sensitivity] = integer;
        d[MapiPid.ReadReceiptRequested] = flag;
        d[MapiPid.OriginatorDeliveryReportRequested] = flag;
        d[MapiPid.ClientSubmitTime] = time;
        d[MapiPid.ReplyRecipientEntries] = binary;
        d[MapiPid.ConversationIndex] = binary;
        d[MapiPid.RtfCompressed] = binary;
        return d;
    }

    // Properties that are store bookkeeping: they say where a thing lives in its store or how it is derived, not what it says.
    private static readonly HashSet<ushort> Bookkeeping =
    [
        MapiPid.EntryId, MapiPid.RecordKey, MapiPid.InstanceKey, MapiPid.ObjectType, MapiPid.Access, MapiPid.AccessLevel, MapiPid.StoreRecordKey,
        MapiPid.StoreEntryId, MapiPid.MappingSignature, MapiPid.SearchKey, MapiPid.ChangeKey, MapiPid.ParentEntryId, MapiPid.MessageSize,
        MapiPid.HasAttachments, MapiPid.HasNamedProperties, MapiPid.StoreSupportMask, MapiPid.DisplayBcc, MapiPid.DisplayCc, MapiPid.DisplayTo,
        MapiPid.MessageRecipients, MapiPid.MessageAttachments, MapiPid.RtfSyncBodyCrc, MapiPid.RtfSyncBodyCount, MapiPid.RtfSyncBodyTag,
        MapiPid.RtfSyncPrefixCount, MapiPid.RtfSyncTrailingCount, MapiPid.RtfInSync, MapiPid.NativeBody, MapiPid.SentRepresentingEntryId,
        MapiPid.SentRepresentingSearchKey, MapiPid.SenderEntryId, MapiPid.SenderSearchKey, MapiPid.MessageCodepage, MapiPid.InternetCodepage,
        MapiPid.MessageLocaleId, MapiPid.LocaleId, MapiPid.Rowid, MapiPid.DisplayType, MapiPid.Templateid, MapiPid.RowType, MapiPid.Depth,
        MapiPid.AttachNumber, MapiPid.AttachSize, MapiPid.RenderingPosition, MapiPid.AttachExtension, MapiPid.AttachEncoding,
        MapiPid.AttachRendering, MapiPid.RecipientOrder, MapiPid.RecipientFlags, MapiPid.RecipientTrackStatus, MapiPid.RecipientEntryId,
        MapiPid.AttachTag, MapiPid.AttachTransportName, MapiPid.AttachPathname, MapiPid.AttachLongPathname,
    ];

    private static readonly HashSet<ushort> RecipientConsumed =
    [
        MapiPid.DisplayName, MapiPid.AddressType, MapiPid.EmailAddress, MapiPid.SmtpAddress, MapiPid.RecipientType, MapiPid.RecipientDisplayName,
    ];

    private static readonly HashSet<ushort> AttachmentConsumed =
    [
        MapiPid.AttachDataBinary, MapiPid.AttachMimeTag, MapiPid.AttachLongFilename, MapiPid.AttachFilename, MapiPid.DisplayName,
        MapiPid.AttachContentId, MapiPid.AttachContentLocation, PidAttachContentDisposition, MapiPid.AttachFlags, MapiPid.AttachMethod,
    ];

    private sealed class Exporter(OxcMailExportOptions options, LossReport report)
    {
        // Header names that came from PS_INTERNET_HEADERS properties rather than from a property with a defined mapping.
        private readonly HashSet<string> named = new(StringComparer.OrdinalIgnoreCase);

        public MimeEntity Export(MapiMessage m, string path, int depth)
        {
            var resolver = new MapiAddressResolver(m);
            var body = new Bodies(m, report, path);
            MimeEntity root = BuildStructure(m, body, path, depth, out bool anyAttachments);
            List<(string Name, string Value)> computed = ComputeHeaders(m, resolver, path);
            List<(string Name, string Value)> final = MergeTransport(m, resolver, computed, path);
            final.Add(("MIME-Version", "1.0"));
            for (int i = 0; i < final.Count; i++)
            {
                root.Headers.Insert(i, new MimeHeader(final[i].Name, OneLine(final[i].Value)));
            }

            _ = anyAttachments;
            ReportProperties(m, path);
            return root;
        }

        private static string OneLine(string v) => v.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ');

        // ---- structure ----------------------------------------------------------------------------------------------------------

        private MimeEntity BuildStructure(MapiMessage m, Bodies body, string path, int depth, out bool anyAttachments)
        {
            var inlineParts = new List<MimeEntity>();
            var otherParts = new List<MimeEntity>();
            bool hasHtml = body.Html is not null;
            IReadOnlyList<MapiAttachment> attachments = m.Attachments;
            for (int i = 0; i < attachments.Count; i++)
            {
                MimeEntity? part = ExportAttachment(attachments[i], i, hasHtml, path, depth, out bool inline);
                if (part is null)
                {
                    continue;
                }

                (inline ? inlineParts : otherParts).Add(part);
            }

            anyAttachments = inlineParts.Count + otherParts.Count > 0;

            // MS-OXCMAIL 2.1.3.3: alternative text and HTML; inline images travel with the HTML in a related part.
            var alternatives = new List<MimeEntity>();
            if (body.Text is not null)
            {
                alternatives.Add(body.Text.ToPart("plain"));
            }

            if (body.Html is not null)
            {
                MimePart htmlPart = body.Html.ToPart("html");
                if (inlineParts.Count > 0)
                {
                    MimeMultipart related = NewMultipart("related");
                    related.Add(htmlPart);
                    foreach (MimeEntity p in inlineParts)
                    {
                        related.Add(p);
                    }

                    alternatives.Add(related);
                }
                else
                {
                    alternatives.Add(htmlPart);
                }
            }
            else
            {
                otherParts.InsertRange(0, inlineParts);
            }

            if (body.Rtf is not null)
            {
                alternatives.Add(body.Rtf.ToPart("rtf"));
            }

            MimeEntity? main = alternatives.Count switch
            {
                0 => null,
                1 => alternatives[0],
                _ => AddAll(NewMultipart("alternative"), alternatives),
            };

            if (otherParts.Count == 0)
            {
                return main ?? new Body(string.Empty, "utf-8", "text/plain").ToPart("plain");
            }

            MimeMultipart mixed = NewMultipart("mixed");
            if (main is not null)
            {
                mixed.Add(main);
            }

            foreach (MimeEntity p in otherParts)
            {
                mixed.Add(p);
            }

            return mixed;
        }

        private static MimeMultipart AddAll(MimeMultipart mp, List<MimeEntity> parts)
        {
            foreach (MimeEntity p in parts)
            {
                mp.Add(p);
            }

            return mp;
        }

        private MimeMultipart NewMultipart(string subtype) =>
            new(subtype, options.BoundaryFactory?.Invoke());

        // ---- attachments ----------------------------------------------------------------------------------------------------------

        private MimeEntity? ExportAttachment(MapiAttachment a, int index, bool hasHtml, string path, int depth, out bool inline)
        {
            inline = false;
            string where = path.Length == 0 ? $"attachment[{index}]" : $"{path}/attachment[{index}]";
            string? fileName = a.GetString(MapiPid.AttachLongFilename) ?? a.GetString(MapiPid.AttachFilename) ?? a.GetString(MapiPid.DisplayName);
            string? cid = a.ContentId?.Trim();
            if (cid is { Length: > 1 } && cid[0] == '<' && cid[^1] == '>')
            {
                cid = cid[1..^1];
            }

            if (string.IsNullOrEmpty(cid))
            {
                cid = null;
            }

            string? location = a.GetString(MapiPid.AttachContentLocation);
            string? disp = a.GetString(PidAttachContentDisposition);
            string dispType = disp is not null && disp.TrimStart().StartsWith("attachment", StringComparison.OrdinalIgnoreCase) ? "attachment"
                : disp is not null && disp.TrimStart().StartsWith("inline", StringComparison.OrdinalIgnoreCase) ? "inline"
                : cid is not null || ((a.GetInt32(MapiPid.AttachFlags) ?? 0) & AttachFlagMhtmlRef) != 0 ? "inline" : "attachment";
            inline = hasHtml && cid is not null && dispType == "inline";

            MimeEntity part;
            MapiMessage? embedded = a.EmbeddedMessage;
            if (embedded is not null)
            {
                if (depth >= options.MaxEmbeddedDepth)
                {
                    report.Add("mapi", fileName ?? "embedded message", LossKind.Dropped, "EmbeddedMessageTooDeep", "The embedded message is nested deeper than the limit.", where);
                    return null;
                }

                var inner = new LossReport();
                MimeEntity innerRoot = new Exporter(options, inner).Export(embedded, string.Empty, depth + 1);
                report.Merge(inner, where + "/message");
                var mp = new MimeMessagePart { Message = innerRoot };
                part = mp;
            }
            else
            {
                int method = a.AttachMethodValue ?? 1;
                MapiProperty? data = a.Properties.Get(MapiPid.AttachDataBinary, MapiType.Binary);
                if (data is null && method is 2 or 3 or 4 or 7)
                {
                    report.Add("mapi", fileName ?? "attachment", LossKind.Dropped, "AttachmentByReference", "The attachment is a reference to a file or web resource, not data (MS-OXCMAIL 2.1.3.4 covers attachments by value only).", where);
                    return null;
                }

                if (data is null)
                {
                    report.Add("mapi", fileName ?? "attachment", LossKind.Dropped, "AttachmentWithoutData", method == 6 ? "The OLE object has no flat data (its storage is not an Internet form)." : "The attachment has no data property.", where);
                    return null;
                }

                string? type = a.MimeType?.Trim().ToLowerInvariant();
                if (type is not null && (type.StartsWith("multipart/", StringComparison.Ordinal) || type.StartsWith("message/", StringComparison.Ordinal)))
                {
                    // Flat attachment data is not a body of a composite type (RFC 2046 §5.1 needs a boundary, §5.2.1 forbids base64 for message/*).
                    report.Add("mapi", "PidTagAttachMimeTag", LossKind.Approximated, "CompositeTypeOnFlatData", "The stored content type (" + type + ") is composite but the attachment holds flat data; application/octet-stream is used.", where);
                    type = "application/octet-stream";
                }

                if (!IsMediaType(type))
                {
                    if (!string.IsNullOrEmpty(type))
                    {
                        report.Add("mapi", "PidTagAttachMimeTag", LossKind.Approximated, "InvalidMediaType", "The stored content type is not a valid media type; application/octet-stream is used.", where);
                    }

                    type = "application/octet-stream";
                }

                var mime = new MimePart(type + (fileName is null ? string.Empty : MimeHeaderValue.Parameter("name", OneLine(fileName))));
                mime.SetDecodedContent(data.GetBytes().Span, ContentTransferEncoding.Base64);
                part = mime;
            }

            if (embedded is not null && fileName is not null)
            {
                part.Headers.Set("Content-Type", "message/rfc822" + MimeHeaderValue.Parameter("name", OneLine(fileName)));
            }

            part.Headers.Add("Content-Disposition", dispType + (fileName is null ? string.Empty : MimeHeaderValue.Parameter("filename", OneLine(fileName))));
            if (cid is not null)
            {
                part.Headers.Add("Content-ID", "<" + OneLine(cid) + ">");
            }

            if (!string.IsNullOrWhiteSpace(location))
            {
                part.Headers.Add("Content-Location", OneLine(location));
            }

            foreach (MapiProperty p in a.Properties)
            {
                if (!AttachmentConsumed.Contains(p.Id) || p.Name is not null)
                {
                    ReportProperty(p, where, "attachment");
                }
            }

            return part;
        }

        private static bool IsMediaType(string? t)
        {
            if (t is null || t.Length < 3)
            {
                return false;
            }

            int slash = t.IndexOf('/', StringComparison.Ordinal);
            if (slash <= 0 || slash == t.Length - 1 || t.IndexOf('/', slash + 1) >= 0)
            {
                return false;
            }

            foreach (char c in t)
            {
                if (c == '/')
                {
                    continue;
                }

                if (!(char.IsAsciiLetterOrDigit(c) || "!#$&^_.+-".Contains(c, StringComparison.Ordinal)))
                {
                    return false;
                }
            }

            return true;
        }

        // ---- headers ----------------------------------------------------------------------------------------------------------

        private List<(string Name, string Value)> ComputeHeaders(MapiMessage m, MapiAddressResolver r, string path)
        {
            var h = new List<(string, string)>();
            ResolvedAddress sr = r.SentRepresenting();
            ResolvedAddress se = r.Sender();
            ResolvedAddress from = sr.IsEmpty ? se : sr;

            // MS-OXCMAIL 2.1.3.1.3 and 2.1.3.1.4: From from the sent-representing group, Sender from the sender group unless they are one person.
            Add(h, "From", FormatList([from], "From", path));
            if (!sr.IsEmpty && !se.IsEmpty && se.Smtp is not null && !string.Equals(se.Smtp, sr.Smtp, StringComparison.OrdinalIgnoreCase))
            {
                Add(h, "Sender", FormatList([se], "Sender", path));
            }

            foreach ((MapiRecipientType type, string name) in new[] { (MapiRecipientType.To, "To"), (MapiRecipientType.Cc, "Cc"), (MapiRecipientType.Bcc, "Bcc") })
            {
                var list = new List<ResolvedAddress>();
                foreach (MapiRecipient rec in m.Recipients)
                {
                    if (rec.RecipientType == type)
                    {
                        list.Add(r.Recipient(rec));
                    }
                }

                if (type == MapiRecipientType.Bcc && !options.IncludeBcc)
                {
                    if (list.Count > 0)
                    {
                        report.Add("mapi", "Bcc recipients", LossKind.Dropped, "BccNotWritten", "Blind-copy recipients are not written to the Internet message unless the options ask for them.", path);
                    }

                    continue;
                }

                Add(h, name, FormatList(list, name, path));
            }

            if (m.Properties.Get(MapiPid.ReplyRecipientEntries, MapiType.Binary) is { } replyTo)
            {
                var list = new List<string>();
                foreach (byte[] e in OneOffEntryId.ParseList(replyTo.GetBytes().Span))
                {
                    if (OneOffEntryId.TryParse(e, out string name, out string type, out string address) && string.Equals(type, "SMTP", StringComparison.OrdinalIgnoreCase) && address.Length > 0)
                    {
                        list.Add(FormatMailbox(name, address));
                    }
                    else
                    {
                        // MS-OXCMAIL 2.1.3.1.2 would look up an address-book EntryID in the directory; without one the address is not known.
                        report.Add("mapi", "PidTagReplyRecipientEntries", LossKind.Dropped, "ReplyToUnresolved", "A reply-to entry is not a one-off SMTP address and cannot be resolved without a directory.", path);
                    }
                }

                Add(h, "Reply-To", string.Join(", ", list));
            }

            string? subject = SubjectOf(m, path);
            if (subject is not null)
            {
                // An empty subject is still a subject: the property exists, so the field does.
                h.Add(("Subject", subject));
            }

            DateTimeOffset? date = m.GetTime(MapiPid.ClientSubmitTime);
            if (date is null && m.GetTime(MapiPid.MessageDeliveryTime) is { } delivered)
            {
                date = delivered;
                report.Add("mapi", "PidTagClientSubmitTime", LossKind.Approximated, "DateFromDeliveryTime", "The message has no submit time; the Date header carries the delivery time.", path);
            }

            if (date is { } d)
            {
                Add(h, "Date", MapiAddressResolver.FormatDate(d));
                if (d.UtcTicks % TimeSpan.TicksPerSecond != 0)
                {
                    report.Add("mapi", "PidTagClientSubmitTime", LossKind.Approximated, "DateSubSecondDropped", "The Date header has whole seconds; the fraction of a second is not written.", path);
                }
            }

            if (m.GetString(MapiPid.InternetMessageId) is { } mid)
            {
                Add(h, "Message-ID", Id(mid) ?? EmptyNotWritten("PidTagInternetMessageId", path));
            }

            if (m.GetString(MapiPid.InReplyToId) is { } irt)
            {
                Add(h, "In-Reply-To", Id(irt) ?? EmptyNotWritten("PidTagInReplyToId", path));
            }

            if (m.GetString(MapiPid.InternetReferences) is { } refs)
            {
                Add(h, "References", Ids(refs) is { Length: > 0 } refsText ? refsText : EmptyNotWritten("PidTagInternetReferences", path));
            }

            if (m.GetInt32(MapiPid.Importance) is int imp)
            {
                // MS-OXCMAIL 2.1.3.2.5: 0 Low, 1 Normal, 2 High; other values have no header.
                Add(h, "Importance", imp switch { 0 => "Low", 1 => "Normal", 2 => "High", _ => string.Empty });
                if (imp is < 0 or > 2)
                {
                    report.Add("mapi", "PidTagImportance", LossKind.Dropped, "ValueOutOfRange", "Only 0, 1 and 2 have an Importance header value.", path);
                }
            }

            if (m.GetInt32(MapiPid.Sensitivity) is int sens)
            {
                // MS-OXCMAIL 2.1.3.2.6: 0 is the default and has no header; so do values outside 0 to 3.
                Add(h, "Sensitivity", sens switch { 1 => "Personal", 2 => "Private", 3 => "Company-Confidential", _ => string.Empty });
                if (sens == 0)
                {
                    report.Add("mapi", "PidTagSensitivity", LossKind.Approximated, "DefaultValueNotWritten", "Normal sensitivity is the default and has no header; reading the message back leaves the property absent.", path);
                }
                else if (sens is < 0 or > 3)
                {
                    report.Add("mapi", "PidTagSensitivity", LossKind.Dropped, "ValueOutOfRange", "Only 0 to 3 have a Sensitivity header value.", path);
                }
            }

            if (m.Properties.Get(Keywords) is { } kw && kw.ValueCount > 0)
            {
                var words = new List<string>();
                for (int i = 0; i < kw.ValueCount; i++)
                {
                    if (kw.AsString(m.String8Encoding, null, i) is { Length: > 0 } w)
                    {
                        words.Add(w);
                    }
                }

                Add(h, "Keywords", string.Join(", ", words));
            }

            if (m.GetString(MapiPid.ConversationTopic) is { } topic)
            {
                Add(h, "Thread-Topic", topic.Length > 0 ? topic : EmptyNotWritten("PidTagConversationTopic", path));
            }

            if (m.Properties.Get(MapiPid.ConversationIndex, MapiType.Binary) is { } ci)
            {
                Add(h, "Thread-Index", ci.GetByteLength() > 0 ? System.Convert.ToBase64String(ci.GetBytes().Span) : EmptyNotWritten("PidTagConversationIndex", path));
            }

            ResolvedAddress receipt = from.Smtp is not null ? from : se;
            // MS-OXCMAIL 2.1.3.1.5 and 2.1.3.1.6: the fields exist only when the property is true.
            if (m.GetBoolean(MapiPid.ReadReceiptRequested) is { } rr)
            {
                if (rr)
                {
                    Add(h, "Disposition-Notification-To", FormatList([receipt], "Disposition-Notification-To", path));
                }
                else
                {
                    report.Add("mapi", "PidTagReadReceiptRequested", LossKind.Approximated, "DefaultValueNotWritten", "False is the default and has no header.", path);
                }
            }

            if (m.GetBoolean(MapiPid.OriginatorDeliveryReportRequested) is { } dr)
            {
                if (dr)
                {
                    Add(h, "Return-Receipt-To", FormatList([receipt], "Return-Receipt-To", path));
                }
                else
                {
                    report.Add("mapi", "PidTagOriginatorDeliveryReportRequested", LossKind.Approximated, "DefaultValueNotWritten", "False is the default and has no header.", path);
                }
            }

            // MS-OXCMAIL 2.1.3.2.4: a string-named property of PS_INTERNET_HEADERS is a header of that name.
            foreach (MapiProperty p in m.Properties)
            {
                if (p.Name is { IsString: true } n && n.PropertySet == MapiPropertySets.InternetHeaders && p.Type is MapiType.UnicodeString or MapiType.String8
                    && p.AsString(m.String8Encoding) is { } text && IsFieldName(n.Name!) && !IsStructural(n.Name!))
                {
                    Add(h, n.Name!, text);
                    named.Add(n.Name!);
                }
            }

            if (m.MessageClass is { Length: > 0 } cls && !cls.Equals("IPM.Note", StringComparison.OrdinalIgnoreCase))
            {
                report.Add("mapi", "PidTagMessageClass", LossKind.Dropped, "MessageClassNotCarried", "The Internet message has no field for a message class other than IPM.Note (" + cls + "); reading it back gives IPM.Note.", path);
            }

            return h;
        }

        // A property with an empty value has no field to carry it; reading the message back leaves the property absent.
        private string? EmptyNotWritten(string property, string path)
        {
            report.Add("mapi", property, LossKind.Approximated, "EmptyValueNotWritten", "The value is empty and an empty field would say nothing; the property comes back absent.", path);
            return null;
        }

        private static bool IsFieldName(string s)
        {
            if (s.Length == 0)
            {
                return false;
            }

            foreach (char c in s)
            {
                if (c <= ' ' || c > '~' || c == ':')
                {
                    return false;
                }
            }

            return true;
        }

        private static void Add(List<(string, string)> h, string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                h.Add((name, value));
            }
        }

        // Subject: the full subject without the control-character pair Outlook stores in front of a split prefix (MS-OXPROPS PidTagSubject),
        // else prefix and normalized subject joined (MS-OXCMAIL 2.1.3.2.8).
        private string? SubjectOf(MapiMessage m, string path)
        {
            string? s = m.GetString(MapiPid.Subject);
            if (s is null)
            {
                string? n = m.GetString(MapiPid.NormalizedSubject);
                return n is null ? null : (m.GetString(MapiPid.SubjectPrefix) ?? string.Empty) + n;
            }

            if (s.Length >= 2 && s[0] == '\u0001')
            {
                report.Add("mapi", "PidTagSubject", LossKind.Approximated, "SubjectPrefixMarkerRemoved", "The control characters that mark the stored subject's prefix split are not part of the Subject header.", path);
                s = s[2..];
            }

            if (s.Length != s.Trim(' ', '\t').Length)
            {
                report.Add("mapi", "PidTagSubject", LossKind.Approximated, "SubjectWhitespaceTrimmed", "White space at the ends of a header value is not part of the value (RFC 5322 §2.2.3).", path);
            }

            return s;
        }

        private string FormatList(IEnumerable<ResolvedAddress> list, string header, string path)
        {
            var parts = new List<string>();
            foreach (ResolvedAddress a in list)
            {
                if (a.IsEmpty)
                {
                    continue;
                }

                if (a.Smtp is not null)
                {
                    parts.Add(FormatMailbox(a.Name, a.Smtp));
                    if (a.X500 is not null)
                    {
                        report.Add("mapi", header, LossKind.Approximated, "ExchangeAddressWrittenAsSmtp", "The address is stored as an Exchange (X.500) address; the field has its SMTP address, which reading the message back stores as an SMTP address.", path);
                    }
                }
                else
                {
                    report.Add("mapi", header, LossKind.Dropped, "AddressUnresolved",
                        "An address has no SMTP form (" + (a.X500 is not null ? "Exchange address without PidTagSmtpAddress and no match in the message" : "address type is not SMTP") + "), so it cannot be written to the field.", path);
                }
            }

            return string.Join(", ", parts);
        }

        internal static string FormatMailbox(string? name, string address)
        {
            string addr = address.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                return addr;
            }

            string display = name.Trim();
            string phrase;
            if (display.AsSpan().ContainsAnyExceptInRange((char)0x20, (char)0x7E))
            {
                // RFC 2047 §5(3): in a phrase every word is an atom or an encoded-word, so the whole name is encoded; a name whose plain
                // stretches held specials such as "@" or "(" would otherwise corrupt the address.
                phrase = EncodeWords(display);
            }
            else if (IsAtomText(display))
            {
                phrase = display;
            }
            else
            {
                phrase = "\"" + display.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
            }

            return phrase + " <" + addr + ">";
        }

        // The text as B encoded-words of UTF-8, each at most 75 characters (RFC 2047 §2) and never cutting a character.
        private static string EncodeWords(string text)
        {
            var words = new List<string>();
            var chunk = new List<byte>();
            Span<byte> buf = stackalloc byte[4];
            foreach (Rune r in text.EnumerateRunes())
            {
                int n = r.EncodeToUtf8(buf);
                if (chunk.Count + n > 42)
                {
                    words.Add(Word(chunk));
                    chunk.Clear();
                }

                chunk.AddRange(buf[..n].ToArray());
            }

            if (chunk.Count > 0)
            {
                words.Add(Word(chunk));
            }

            return string.Join(' ', words);

            static string Word(List<byte> bytes) => "=?utf-8?B?" + System.Convert.ToBase64String([.. bytes]) + "?=";
        }

        private static bool IsAtomText(string s)
        {
            foreach (char c in s)
            {
                if (!(char.IsAsciiLetterOrDigit(c) || c == ' ' || "!#$%&'*+-/=?^_`{|}~".Contains(c, StringComparison.Ordinal)))
                {
                    return false;
                }
            }

            return !s.StartsWith(' ') && !s.EndsWith(' ') && !s.Contains("  ", StringComparison.Ordinal);
        }

        private static string? Id(string raw)
        {
            string t = raw.Trim();
            if (t.Length == 0)
            {
                return null;
            }

            return t.StartsWith('<') ? t : "<" + t + ">";
        }

        private static string Ids(string raw)
        {
            string t = raw.Trim();
            if (t.Contains('<', StringComparison.Ordinal))
            {
                return t;
            }

            return string.Join(' ', t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(x => "<" + x + ">"));
        }

        // ---- transport headers ----------------------------------------------------------------------------------------------------------

        private List<(string Name, string Value)> MergeTransport(MapiMessage m, MapiAddressResolver r, List<(string Name, string Value)> computed, string path)
        {
            IReadOnlyList<HeaderField> transport = options.UseTransportHeaders ? r.Transport : [];
            if (transport.Count == 0)
            {
                return computed;
            }

            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string n, string v) in computed)
            {
                byName.TryAdd(n, v);
            }

            var result = new List<(string, string)>();
            var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool rewritten = false;
            if (r.SkippedLines > 0)
            {
                report.Add("mime", "(header lines)", LossKind.Dropped, "TransportHeaderLineSkipped", r.SkippedLines + " line(s) of the transport headers are not header fields (no valid field name) and are not written.", path);
                rewritten = true;
            }

            foreach (HeaderField f in transport)
            {
                if (IsStructural(f.Name))
                {
                    rewritten = true;
                    continue;
                }

                if (f.Name.Equals("Bcc", StringComparison.OrdinalIgnoreCase) && !options.IncludeBcc)
                {
                    report.Add("mime", "Bcc", LossKind.Dropped, "BccNotWritten", "A Bcc field in the transport headers is not written unless the options ask for it.", path);
                    rewritten = true;
                    continue;
                }

                if (byName.TryGetValue(f.Name, out string? mine) && emitted.Add(f.Name))
                {
                    if (Agrees(f.Name, f.Value, mine))
                    {
                        result.Add((f.Name, f.Value));
                        if (IsAddressField(f.Name) && MailboxSet(f.Value) != MailboxSet(mine))
                        {
                            report.Add("mime", f.Name, LossKind.Approximated, "AddressSpellingFromTransportHeader",
                                "The transport header and the properties name the same addresses but spell a display name or address differently; the transport header's spelling is written.", path);
                        }
                    }
                    else if (named.Contains(f.Name))
                    {
                        // Two sources for an arbitrary field disagree; the received one is the original, so it stays.
                        result.Add((f.Name, f.Value));
                        report.Add("mime", f.Name, LossKind.Overridden, "NamedPropertyValueSetAside", "The transport headers and the PS_INTERNET_HEADERS property give different values; the transport headers' value is written.", path);
                    }
                    else
                    {
                        result.Add((f.Name, mine));
                        rewritten = true;
                        report.Add("mime", f.Name, LossKind.Overridden, "TransportHeaderOverridden",
                            "The transport headers' " + f.Name + " differs from the message's properties; the properties' value is written, since the properties are the item's truth.", path);
                    }
                }
                else
                {
                    result.Add((f.Name, f.Value));
                }
            }

            foreach ((string n, string v) in computed)
            {
                if (!emitted.Contains(n))
                {
                    result.Add((n, v));
                    rewritten = true;
                }
            }

            if (rewritten)
            {
                // The block the message is read back with is not the stored one: the structural fields are the new message's own.
                report.Add("mapi", "PidTagTransportMessageHeaders", LossKind.Approximated, "TransportHeadersRewritten",
                    "The received header block is the base of the new message's headers, but its Content-* and MIME-Version fields are replaced by the new message's own and fields built from the properties are added.", path);
            }

            return result;
        }

        // The fields this conversion builds itself, so the received ones are never copied.
        private static bool IsStructural(string name) =>
            name.Equals("MIME-Version", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)
               && !name.Equals("Content-Language", StringComparison.OrdinalIgnoreCase) && !name.Equals("Content-Class", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Lines", StringComparison.OrdinalIgnoreCase);

        // Do a received field and the value from the properties say the same thing? Only the fields the properties define are compared.
        private bool Agrees(string name, string received, string mine)
        {
            switch (name.ToLowerInvariant())
            {
                case "subject":
                case "thread-topic":
                    return Squash(Rfc2047.DecodeText(received)) == Squash(mine);
                case "from":
                case "sender":
                case "to":
                case "cc":
                case "bcc":
                case "reply-to":
                    return AddressSet(received) == AddressSet(mine);
                case "date":
                    return DateParser.TryParse(Encoding.UTF8.GetBytes(received), out MimeDate a) && DateParser.TryParse(Encoding.UTF8.GetBytes(mine), out MimeDate b)
                        && Math.Abs((a.Utc - b.Utc).TotalSeconds) < 1;
                case "message-id":
                case "in-reply-to":
                case "references":
                    return string.Join(' ', MessageIdParser.Parse(Encoding.UTF8.GetBytes(received))) == string.Join(' ', MessageIdParser.Parse(Encoding.UTF8.GetBytes(mine)));
                default:
                    return !named.Contains(name) || Squash(received) == Squash(mine);
            }
        }

        private static bool IsAddressField(string name) =>
            name.ToLowerInvariant() is "from" or "sender" or "to" or "cc" or "bcc" or "reply-to";

        // Display names and addresses exactly as the field spells them (names decoded), for telling a kept header from the properties' own.
        private static string MailboxSet(string value)
        {
            var set = new SortedSet<string>(StringComparer.Ordinal);
            foreach (MimeMailbox mb in AddressParser.Flatten(AddressParser.Parse(Encoding.UTF8.GetBytes(value))))
            {
                set.Add(Squash(mb.Name ?? string.Empty) + "\0" + mb.Address);
            }

            return string.Join('\u0001', set);
        }

        private static string Squash(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        private static string AddressSet(string value)
        {
            var set = new SortedSet<string>(StringComparer.Ordinal);
            foreach (MimeMailbox mb in AddressParser.Flatten(AddressParser.Parse(Encoding.UTF8.GetBytes(value))))
            {
                set.Add(mb.Address.ToLowerInvariant());
            }

            return string.Join(',', set);
        }

        // ---- loss report for properties ----------------------------------------------------------------------------------------------------------

        private void ReportProperties(MapiMessage m, string path)
        {
            foreach (MapiProperty p in m.Properties)
            {
                bool handled = p.Name is { } n
                    ? n == Keywords || (n.IsString && n.PropertySet == MapiPropertySets.InternetHeaders && p.Type is MapiType.UnicodeString or MapiType.String8 && IsFieldName(n.Name!) && !IsStructural(n.Name!))
                    : (Consumed.TryGetValue(p.Id, out MapiType[]? kinds) && Array.IndexOf(kinds, p.Type) >= 0) || p.Id == MapiPid.MessageClass && (m.MessageClass is null or { Length: 0 } || m.MessageClass.Equals("IPM.Note", StringComparison.OrdinalIgnoreCase));
                if (!handled)
                {
                    ReportProperty(p, path, "message");
                }
            }

            var seen = new HashSet<MapiTag>();
            foreach (MapiRecipient rec in m.Recipients)
            {
                foreach (MapiProperty p in rec.Properties)
                {
                    if (!RecipientConsumed.Contains(p.Id) && seen.Add(p.Tag))
                    {
                        ReportProperty(p, path.Length == 0 ? "recipient" : path + "/recipient", "recipient");
                    }
                }
            }
        }

        private void ReportProperty(MapiProperty p, string path, string what)
        {
            string name = p.Name is { } n ? n.ToString() : MapiPropertyNames.GetName(p.Id) ?? p.Tag.ToString();
            if (p.Name is null && Bookkeeping.Contains(p.Id))
            {
                report.Add("mapi", name, LossKind.Internal, "StoreBookkeeping", "A store-internal " + what + " property with no meaning outside the store.", path);
            }
            else
            {
                report.Add("mapi", name, LossKind.Dropped, "NoInternetMapping", "MS-OXCMAIL maps no Internet field from this " + what + " property.", path);
            }
        }
    }

    // One body in one representation, ready to become a part.
    private sealed class Body(string text, string charset, string family)
    {
        public Body(byte[] bytes, string charset, string family)
            : this(string.Empty, charset, family) => Bytes = bytes;

        public byte[]? Bytes { get; }

        public string Charset => charset;

        public MimePart ToPart(string subtype)
        {
            byte[] bytes = Bytes ?? Encoding.UTF8.GetBytes(NormalizeLines(text));
            var part = new MimePart(subtype == "rtf" ? "text/rtf" : "text/" + subtype + "; charset=" + charset);
            bool plain = System.Text.Ascii.IsValid(bytes) && bytes.AsSpan().IndexOf((byte)0) < 0 && MaxLine(bytes) <= 998;
            part.SetDecodedContent(bytes, plain ? ContentTransferEncoding.SevenBit : ContentTransferEncoding.QuotedPrintable);
            _ = family;
            return part;
        }

        // Body text in MAPI uses CRLF; a lone CR or LF is made a CRLF so that the transfer encodings keep line structure.
        internal static string NormalizeLines(string s)
        {
            if (!s.AsSpan().ContainsAny('\r', '\n'))
            {
                return s;
            }

            var sb = new StringBuilder(s.Length + 16);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\r')
                {
                    sb.Append("\r\n");
                    if (i + 1 < s.Length && s[i + 1] == '\n')
                    {
                        i++;
                    }
                }
                else if (c == '\n')
                {
                    sb.Append("\r\n");
                }
                else
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }

        private static int MaxLine(byte[] b)
        {
            int max = 0;
            int cur = 0;
            foreach (byte x in b)
            {
                if (x == (byte)'\n')
                {
                    max = Math.Max(max, cur);
                    cur = 0;
                }
                else
                {
                    cur++;
                }
            }

            return Math.Max(max, cur);
        }
    }

    // The bodies of a message as the conversion writes them (MS-OXCMAIL 2.1.3.3).
    private sealed class Bodies
    {
        public Body? Text { get; }

        public Body? Html { get; }

        public Body? Rtf { get; }

        public Bodies(MapiMessage m, LossReport report, string path)
        {
            if (m.Properties.Get(MapiPid.Body, MapiType.String8, MapiType.UnicodeString) is { } tp && tp.AsString(m.String8Encoding) is { } t)
            {
                Text = new Body(t, "utf-8", "text");
                if (Body.NormalizeLines(t) != t)
                {
                    report.Add("mapi", "PidTagBody", LossKind.Approximated, "BodyLineBreaksNormalized", "A lone CR or LF in the body is written as CRLF, the only line break a MIME text part has.", path);
                }
            }

            if (m.Properties.Get(MapiPid.Html, MapiType.Binary, MapiType.String8, MapiType.UnicodeString) is { } hp)
            {
                Html = HtmlBody(m, hp);
            }

            RtfBodies rtf = RtfBodies.Read(m);
            if (rtf.Data is null && m.Properties.Get(MapiPid.RtfCompressed, MapiType.Binary) is not null)
            {
                report.Add("mapi", "PidTagRtfCompressed", LossKind.Dropped, "RtfUndecodable", "The compressed RTF body does not decompress and is not written.", path);
            }

            if (rtf.Data is not null)
            {
                if (Html is null && rtf.Html is { } rh)
                {
                    Html = new Body(rh, "utf-8", "html");
                    report.Add("mapi", "PidTagRtfCompressed", LossKind.Approximated, "HtmlFromRtf", "The message has no HTML body; the HTML encapsulated in its RTF body is written (MS-OXRTFEX).", path);
                }
                else if (Text is null && rtf.Text is { } rt)
                {
                    Text = new Body(rt, "utf-8", "text");
                    report.Add("mapi", "PidTagRtfCompressed", LossKind.Approximated, "TextFromRtf", "The message has no plain-text body; the text encapsulated in its RTF body is written (MS-OXRTFEX).", path);
                }
                else if (rtf.Kind == RtfEncapsulation.None && Html is null)
                {
                    Rtf = new Body(rtf.Data, "us-ascii", "rtf");
                    report.Add("mapi", "PidTagRtfCompressed", LossKind.Approximated, "RtfCarriedAsTextRtf", "The RTF body does not encapsulate HTML or text; it is carried as a text/rtf part.", path);
                }
                else
                {
                    report.Add("mapi", "PidTagRtfCompressed", LossKind.Dropped, "RtfRedundant", "The RTF body is not written because the message has the same content as plain text or HTML.", path);
                }
            }
        }

        // HTML is stored as bytes in the message's Internet code page; they are passed on unchanged with that charset, so nothing is
        // re-encoded. UTF-16 and text properties become UTF-8.
        private static Body HtmlBody(MapiMessage m, MapiProperty p)
        {
            if (p.Type == MapiType.UnicodeString)
            {
                return new Body(p.AsString() ?? string.Empty, "utf-8", "html");
            }

            ReadOnlySpan<byte> raw = p.GetBytes().Span;
            while (raw.Length > 0 && raw[^1] == 0)
            {
                raw = raw[..^1];
            }

            int cp = m.GetInt32(MapiPid.InternetCodepage) ?? 0;
            // 8-bit text properties are read with the item's own code page rule, as the shared view reads them; binary HTML names its own.
            MailEncoding? enc = cp > 0 && p.Type == MapiType.Binary ? MailEncoding.ForCodePage(cp) : null;
            if (p.Type == MapiType.Binary && (cp is 1200 or 1201 || MapiCodePages.IsUnicodeTransformation(cp) && cp != 65001))
            {
                string decoded = cp == 1201 ? Encoding.BigEndianUnicode.GetString(raw) : Encoding.Unicode.GetString(raw);
                return new Body(decoded, "utf-8", "html");
            }

            enc ??= m.String8Encoding;
            return new Body(raw.ToArray(), enc.Name.ToLowerInvariant(), "html");
        }
    }
}
