using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.Forms;
using RecursiveAI.OneMail.Conversion.OxcIcal;
using RecursiveAI.OneMail.Conversion.OxcMail;
using RecursiveAI.OneMail.Conversion.OxVCard;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Stores;
using RecursiveAI.OneMail.Stores.Olm;
using RecursiveAI.OneMail.Tnef;

namespace RecursiveAI.OneMail.Conversion.Stores;

/// <summary>The family of an item's converted form.</summary>
public enum PayloadKind
{
    /// <summary>A message: MIME bytes (or TNEF for the TNEF target).</summary>
    Message = 0,

    /// <summary>A calendar object in one of the calendar formats.</summary>
    Calendar,

    /// <summary>A contact in one of the card formats.</summary>
    Contact,
}

internal sealed record Payload(PayloadKind Kind, byte[] Content, string Extension);

/// <summary>
/// Converts one item of any source into the bytes a target stores. The report is the converters' own: nothing is added except the
/// entries for the pieces this layer sets aside itself (blind-copy recipients of a view-built message, OLM elements no field carries,
/// the loss of a JSON or XML form).
/// </summary>
internal static class ItemTranscoder
{
    public static Payload? Convert(IMailItem item, ConvertFormat target, StoreConversionOptions options, LossReport report, out string reason)
    {
        reason = string.Empty;
        switch (target)
        {
            case ConvertFormat.Eml or ConvertFormat.Mbox or ConvertFormat.Maildir or ConvertFormat.AppleMail:
                switch (item.Kind)
                {
                    case MailItemKind.Message:
                        return Mime(item, options, report);
                    case MailItemKind.Appointment or MailItemKind.Task or MailItemKind.Note or MailItemKind.Journal:
                        return options.OtherItems == OtherItemsMode.Skip ? Skip(out reason, "OtherItemsSkipped") : Calendar(item, ConvertFormat.Ics, report, out reason);
                    case MailItemKind.Contact or MailItemKind.DistributionList:
                        return options.OtherItems == OtherItemsMode.Skip ? Skip(out reason, "OtherItemsSkipped") : Contact(item, ConvertFormat.VCard, report, out reason);
                    default:
                        return Skip(out reason, "NotMail");
                }

            case ConvertFormat.Ics or ConvertFormat.JCal or ConvertFormat.XCal:
                return Calendar(item, target, report, out reason);
            case ConvertFormat.VCard or ConvertFormat.JCard or ConvertFormat.XCard:
                return Contact(item, target, report, out reason);
            case ConvertFormat.Tnef:
                return Tnef(item, options, report, out reason);
            default:
                return Skip(out reason, "Unconvertible");
        }
    }

    private static Payload? Skip(out string reason, string code)
    {
        reason = code;
        return null;
    }

    private static Payload Mime(IMailItem item, StoreConversionOptions o, LossReport report)
    {
        switch (item)
        {
            case MimeMailItem m:
                // The tree keeps every byte, so an untouched message is written as it was read.
                return new Payload(PayloadKind.Message, m.Entity.ToArray(), ".eml");
            case MapiMailItem mi:
                ConversionResult<MimeEntity> r = new MapiToMimeConverter(o.MapiToMime).Convert(mi.Message);
                Merge(report, r.Report);
                return new Payload(PayloadKind.Message, r.Value.ToArray(), ".eml");
            case OlmItem oi:
                MimeEntity e;
                if (oi.MimeSource is { Length: > 0 } src)
                {
                    e = MimeEntity.Parse(System.Text.Encoding.UTF8.GetBytes(src), o.Mime);
                }
                else
                {
                    e = ViewMimeBuilder.Build(oi, report);
                }

                ViewMimeBuilder.ReportUnmapped(oi, report, "OPFMessageGetIsRead");
                return new Payload(PayloadKind.Message, e.ToArray(), ".eml");
            default:
                return new Payload(PayloadKind.Message, ViewMimeBuilder.Build(item, report).ToArray(), ".eml");
        }
    }

    private static Payload? Calendar(IMailItem item, ConvertFormat format, LossReport report, out string reason)
    {
        reason = string.Empty;
        ContentDocument doc;
        switch (item)
        {
            case MapiMailItem mi:
                ConversionResult<ContentDocument> r = new MapiToIcsConverter().Convert(mi.Message);
                Merge(report, r.Report);
                doc = r.Value;
                break;
            case OlmItem oi:
                ConversionResult<Component> c = new OlmToCalendarConverter().Convert(oi);
                Merge(report, c.Report);
                doc = new ContentDocument();
                doc.Root.Add(c.Value);
                break;
            default:
                reason = "NotCalendarItem";
                return null;
        }

        if (!doc.Root.Components.Any(v => v.Components.Any(x => x.Name is "VEVENT" or "VTODO" or "VJOURNAL")))
        {
            reason = "NotCalendarItem";
            return null;
        }

        return Encode(doc, format, report, calendar: true);
    }

    private static Payload? Contact(IMailItem item, ConvertFormat format, LossReport report, out string reason)
    {
        reason = string.Empty;
        ContentDocument doc = new();
        switch (item)
        {
            case MapiMailItem { Kind: MailItemKind.Contact } mi:
                ConversionResult<Component> r = new ContactToVCardConverter().Convert(mi.Message);
                Merge(report, r.Report);
                doc.Root.Add(r.Value);
                break;
            case OlmItem { OlmKind: OlmItemKind.Contact or OlmItemKind.Group } oi:
                ConversionResult<Component> c = new OlmToVCardConverter().Convert(oi);
                Merge(report, c.Report);
                doc.Root.Add(c.Value);
                break;
            default:
                reason = item.Kind == MailItemKind.DistributionList ? "DistributionListNotCarried" : "NotContactItem";
                return null;
        }

        return Encode(doc, format, report, calendar: false);
    }

    private static Payload? Tnef(IMailItem item, StoreConversionOptions o, LossReport report, out string reason)
    {
        reason = string.Empty;
        MapiMessage message;
        switch (item)
        {
            case MapiMailItem mi:
                message = mi.Message;
                break;
            case MimeMailItem m:
                ConversionResult<MapiMessage> r = new MimeToMapiConverter().Convert(m.Entity);
                Merge(report, r.Report);
                message = r.Value;
                break;
            default:
                reason = "NotMapiConvertible";
                return null;
        }

        using MemoryStream ms = new();
        foreach (string w in TnefMessageWriter.Write(message, ms))
        {
            report.Add("mapi", w, LossKind.Dropped, "TnefWriterLimit", "TNEF cannot carry this property: " + w);
        }

        return new Payload(PayloadKind.Message, ms.ToArray(), ".tnef");
    }

    /// <summary>Writes a document in the calendar or card format and folds the form's own losses into the report.</summary>
    public static Payload Encode(ContentDocument doc, ConvertFormat format, LossReport report, bool calendar)
    {
        switch (format)
        {
            case ConvertFormat.Ics or ConvertFormat.VCard:
                return new Payload(calendar ? PayloadKind.Calendar : PayloadKind.Contact, doc.ToArray(), format == ConvertFormat.Ics ? ".ics" : ".vcf");
            case ConvertFormat.JCal or ConvertFormat.JCard:
                return FromForm(JsonForms.ToJson(doc), report, calendar, format == ConvertFormat.JCal ? ".jcal" : ".jcard");
            default:
                return FromForm(format == ConvertFormat.XCal ? XmlForms.ToXCal(doc) : XmlForms.ToXCard(doc), report, calendar, format == ConvertFormat.XCal ? ".xcal" : ".xcard");
        }
    }

    private static Payload FromForm(FormWriteResult w, LossReport report, bool calendar, string ext)
    {
        foreach (FormLoss l in w.Losses)
        {
            // Folding and name case change the text but not the data; everything else is a statement about the data.
            LossKind kind = l.Kind is FormLossKind.Folding or FormLossKind.NameCase ? LossKind.Internal : LossKind.Approximated;
            report.Add("ics", l.Path, kind, "Form" + l.Kind, l.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " occurrence(s) differ in the JSON or XML form.");
        }

        return new Payload(calendar ? PayloadKind.Calendar : PayloadKind.Contact, w.Bytes, ext);
    }

    private static void Merge(LossReport into, LossReport from)
    {
        foreach (LossEntry e in from)
        {
            into.Add(e);
        }
    }
}

/// <summary>Flags, keywords and dates of an item, as the stores want them.</summary>
internal static class ItemFlags
{
    private const int MsgflagRead = 1;
    private const int MsgflagUnsent = 8;

    // The message bytes are passed so that a message whose store keeps no flags is not serialised twice.
    public static (MessageMarks Marks, IReadOnlyList<string> Keywords) Of(IMailItem item, ReadOnlySpan<byte> message)
    {
        switch (item)
        {
            case MimeMailItem m:
                return m.FlagsInStore ? m.StoreFlags() : MimeMailItem.HeaderFlags(message);
            case MapiMailItem mi:
                MessageMarks marks = MessageMarks.None;
                int flags = mi.Message.GetInt32(MapiPid.MessageFlags) ?? 0;
                if ((flags & MsgflagRead) != 0)
                {
                    marks |= MessageMarks.Seen;
                }

                if ((flags & MsgflagUnsent) != 0)
                {
                    marks |= MessageMarks.Draft;
                }

                // PidTagFlagStatus (MS-OXOFLAG): 2 is flagForFollowUp.
                if (mi.Message.GetInt32(MapiPid.FlagStatus) == 2)
                {
                    marks |= MessageMarks.Flagged;
                }

                // PidTagLastVerbExecuted (MS-OXOMSG): 102 reply to sender, 103 reply to all, 104 forward.
                switch (mi.Message.GetInt32(MapiPid.LastVerbExecuted))
                {
                    case 102 or 103:
                        marks |= MessageMarks.Answered;
                        break;
                    case 104:
                        marks |= MessageMarks.Forwarded;
                        break;
                }

                return (marks, []);
            case OlmItem oi:
                return (oi.OlmKind == OlmItemKind.Email && oi.Flag("OPFMessageGetIsRead") ? MessageMarks.Seen : MessageMarks.None, oi.Categories);
            default:
                return (MessageMarks.None, []);
        }
    }

    /// <summary>The arrival date when the store has one, else the sent date.</summary>
    public static DateTimeOffset? Received(IMailItem item) =>
        item.TryGetDate(MailDateSource.Delivery, out MailDate d) ? d.Value : item.Sent?.Value;
}
