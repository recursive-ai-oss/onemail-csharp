using System.Text;

namespace RecursiveAI.OneMail.Conversion.Stores;

/// <summary>How well a piece of an item survives a conversion.</summary>
public enum Fidelity
{
    /// <summary>Carried unchanged: the same bytes, or the same values.</summary>
    Exact = 0,

    /// <summary>Carried in the target's own form, recomputed from the source by a rule of the specification; reading it back gives the same meaning.</summary>
    Derived,

    /// <summary>Carried in a weaker form; each case is a loss-report entry.</summary>
    Approximated,

    /// <summary>Not carried; each case is a loss-report entry (or the item is skipped and counted).</summary>
    Lost,

    /// <summary>The source has nothing of this kind.</summary>
    None,
}

/// <summary>One source family written to one target.</summary>
/// <param name="Source">The source family (<see cref="FidelityMatrix.Sources"/>).</param>
/// <param name="Target">The target format.</param>
/// <param name="Content">Headers, addresses, bodies, attachments; for calendar and contact sources, the fields of the item.</param>
/// <param name="Properties">Properties and elements of the source that the target has no field for.</param>
/// <param name="Flags">Read, answered, flagged and draft state, and keywords or categories.</param>
/// <param name="Folders">The folder the item was in.</param>
/// <param name="Codes">Every loss-report code the conversion can raise for this cell, apart from store bookkeeping.</param>
/// <param name="SkipReason">For a cell whose items are not converted, the reason code the report counts; otherwise empty.</param>
/// <param name="Note">A sentence of explanation.</param>
public sealed record FidelityCell(string Source, ConvertFormat Target, Fidelity Content, Fidelity Properties, Fidelity Flags, Fidelity Folders, IReadOnlyList<string> Codes, string SkipReason, string Note);

/// <summary>
/// The fidelity matrix: for every kind of source and every format that can be written, what carries over exactly, what is derived,
/// what is approximated and what is lost. <c>docs/fidelity.md</c> is generated from it (<see cref="ToMarkdown"/>), and the tests run
/// conversions and check every code a report contains against the cell, so the table cannot drift from the code.
/// </summary>
public static class FidelityMatrix
{
    private static readonly string[] StoreCodes = ["FolderNameChanged", "FlagsNotCarried", "KeywordsNotCarried", "KeywordsNormalized"];

    private static readonly string[] MboxExtra = ["FolderFlattened", "EnvelopeDateUnknown", "FlagsWrittenAsHeaders", "FinalLineEndingAdded"];

    private static readonly string[] OutlookToMime =
    [
        "AddressSpellingFromTransportHeader", "AddressUnresolved", "AttachmentByReference", "AttachmentWithoutData", "BccNotWritten", "BodyLineBreaksNormalized",
        "CompositeTypeOnFlatData", "DateBeforeFileTimeEpoch", "DateFromDeliveryTime", "DateSubSecondDropped", "DefaultValueNotWritten", "EmbeddedMessageTooDeep",
        "EmptyValueNotWritten", "ExchangeAddressWrittenAsSmtp", "HtmlFromRtf", "InvalidMediaType", "MessageClassNotCarried", "NamedPropertyValueSetAside",
        "NoInternetMapping", "ReplyToUnresolved", "RtfCarriedAsTextRtf", "RtfRedundant", "RtfUndecodable", "StoreBookkeeping", "SubjectPrefixMarkerRemoved",
        "SubjectWhitespaceTrimmed", "TextFromRtf", "TransportHeaderLineSkipped", "TransportHeaderOverridden", "TransportHeadersRewritten", "ValueOutOfRange",
    ];

    private static readonly string[] MimeToOutlook =
    [
        "DateZoneNotStored", "ExtraAuthorsNotMapped", "GroupFlattened", "InReplyToFirstOnly", "PartHeaderNotMapped", "PartsTooDeep", "RepeatedHeaderNotMapped",
        "SignatureNotVerifiable", "ThreadIndexNotBase64", "TnefCodePageDiffers", "TnefMessageClassUsed", "TnefNotUsed", "TnefPartUnpacked", "TnefPropertyIgnored",
        "TnefPropertyWithoutName", "TnefRecipientIgnored", "TnefTrailingCrLfIgnored", "ValueOutOfRange", "DefaultValueNotWritten", "EmptyValueNotWritten",
        "EmbeddedMessageTooDeep", "InvalidMediaType", "NamedPropertyValueSetAside", "StoreBookkeeping",
    ];

    private static readonly string[] OutlookToIcs =
    [
        "AttachmentNotCarried", "DerivedProperty", "ForwardNotificationHasNoMethod", "MessageClassNotCarried", "NoIcalMapping", "OrganizerWithoutSmtpAddress",
        "RecurrenceNotCarried", "RtfBodyNotCarried", "StoreBookkeeping", "TaskStatusWeakened", "UidSynthesized",
        .. Enum.GetNames<Calendaring.CalendaringDefectKind>(),
    ];

    private static readonly string[] OutlookToVCard =
    [
        "DefaultValueNotWritten", "DerivedValue", "EmailSlotChanged", "EmptyValueNotWritten", "ExchangeAddressNotExported", "MemberNotCarried", "NotAContact",
        "PhoneSlotChanged", "StoreBookkeeping", "AttachmentNotCarried", "UnsupportedPhotoType",
    ];

    private static readonly string[] OlmToCalendar =
    [
        "AllDayEnd", "AttendeeStatus", "CalendarAttachments", "DateOnlyZone", "EndZoneIgnored", "FloatingTime", "NotACalendarItem", "OrganizerNotAnAddress",
        "RecurrenceEnd", "RecurrenceKind", "RelativeRecurrence", "Unmapped",
    ];

    private static readonly string[] OlmToCard = ["MemberName", "NotAContact", "Unmapped"];

    private static readonly string[] OlmToMime = ["AddressUnresolved", "BccNotWritten", "ConversationTopicWhitespaceTrimmed", "DateZoneNotStored", "EmbeddedMessageAsData", "Unmapped", "OlmFieldDiffersFromSource"];

    private static readonly string[] Forms = [.. Enum.GetNames<Calendar.Forms.FormLossKind>().Where(n => n != "None").Select(n => "Form" + n)];

    /// <summary>The source families, in the order of the table.</summary>
    public static IReadOnlyList<(string Id, string Title)> Sources { get; } =
    [
        ("mime", "MIME message (EML, mbox, Maildir, EMLX, DBX, MHTML)"),
        ("outlook-mail", "Outlook mail (MSG, OFT, PST, OST, TNEF)"),
        ("outlook-calendar", "Outlook appointment, meeting or task (MSG, PST, OST, TNEF)"),
        ("outlook-contact", "Outlook contact (MSG, PST, OST, TNEF)"),
        ("olm-mail", "Outlook for Mac mail (OLM)"),
        ("olm-calendar", "Outlook for Mac appointment, task or note (OLM)"),
        ("olm-contact", "Outlook for Mac contact or group (OLM)"),
        ("olm-other", "Outlook for Mac categories and other records (OLM)"),
        ("calendar-file", "Calendar file (ICS, jCal, xCal)"),
        ("card-file", "Card file (vCard, jCard, xCard)"),
    ];

    /// <summary>Every cell.</summary>
    public static IReadOnlyList<FidelityCell> Cells { get; } = Build();

    /// <summary>Finds the cell for a source family and a target.</summary>
    /// <param name="source">A source id from <see cref="Sources"/>.</param>
    /// <param name="target">The target format.</param>
    /// <returns>The cell.</returns>
    /// <exception cref="KeyNotFoundException">There is no such cell.</exception>
    public static FidelityCell Get(string source, ConvertFormat target) =>
        Cells.First(c => c.Source == source && c.Target == target);

    private static List<FidelityCell> Build()
    {
        List<FidelityCell> l = [];
        ConvertFormat[] mail = [ConvertFormat.Eml, ConvertFormat.Mbox, ConvertFormat.Maildir, ConvertFormat.AppleMail];
        ConvertFormat[] cal = [ConvertFormat.Ics, ConvertFormat.JCal, ConvertFormat.XCal];
        ConvertFormat[] card = [ConvertFormat.VCard, ConvertFormat.JCard, ConvertFormat.XCard];

        // MIME: the bytes are the item, so a mail target keeps them whole. Only what the store keeps outside the bytes is at stake.
        foreach (ConvertFormat t in mail)
        {
            (Fidelity flags, Fidelity folders, string[] codes, string note) = t switch
            {
                ConvertFormat.Eml => (Fidelity.Lost, Fidelity.Derived, StoreCodes, "One .eml file per message; folders become directories. A file has no flags, so they are reported."),
                ConvertFormat.Mbox => (Fidelity.Derived, Fidelity.Derived, ["FolderFlattened", "FolderNameChanged", "FlagsNotCarried", "KeywordsNormalized", "EnvelopeDateUnknown", "FlagsWrittenAsHeaders", "FinalLineEndingAdded"], "Flags that the message does not state itself are written as Status, X-Status and X-Keywords fields in front of it; the only other case in which bytes change is a message with no final line ending, which gains one. One file per folder; a single-file destination drops folders."),
                ConvertFormat.Maildir => (Fidelity.Exact, Fidelity.Derived, StoreCodes, "Flags and up to 26 keywords per folder go into the file name; Maildir++ folder names cannot hold a dot."),
                _ => (Fidelity.Derived, Fidelity.Derived, StoreCodes, "Flags go into the .emlx property list; keywords have no place there."),
            };
            l.Add(new FidelityCell("mime", t, Fidelity.Exact, Fidelity.None, flags, folders, codes, string.Empty, note));
        }

        l.Add(new FidelityCell("mime", ConvertFormat.Tnef, Fidelity.Approximated, Fidelity.None, Fidelity.None, Fidelity.Lost, [.. MimeToOutlook, "TnefWriterLimit"], string.Empty,
            "The message is converted to an Outlook item (MS-OXCMAIL) and written as TNEF."));
        foreach (ConvertFormat t in new[] { ConvertFormat.Ics, ConvertFormat.JCal, ConvertFormat.XCal })
        {
            l.Add(new FidelityCell("mime", t, Fidelity.Lost, Fidelity.None, Fidelity.None, Fidelity.Lost, [], "NotCalendarItem", "A message is not a calendar item; it is skipped and counted."));
        }

        foreach (ConvertFormat t in new[] { ConvertFormat.VCard, ConvertFormat.JCard, ConvertFormat.XCard })
        {
            l.Add(new FidelityCell("mime", t, Fidelity.Lost, Fidelity.None, Fidelity.None, Fidelity.Lost, [], "NotContactItem", "A message is not a contact; it is skipped and counted."));
        }

        // Outlook mail: converted by MS-OXCMAIL. The properties are the truth; what MIME has no field for is reported.
        foreach (ConvertFormat t in mail)
        {
            Fidelity flags = t switch { ConvertFormat.Eml => Fidelity.Lost, ConvertFormat.Mbox => Fidelity.Derived, _ => Fidelity.Derived };
            string[] extra = t == ConvertFormat.Mbox ? MboxExtra : [];
            l.Add(new FidelityCell("outlook-mail", t, Fidelity.Derived, Fidelity.Lost, flags, Fidelity.Derived, [.. OutlookToMime, .. StoreCodes, .. extra], string.Empty,
                "MS-OXCMAIL 2.1 builds the message from the properties; properties with no Internet form are reported. Flags come from PidTagMessageFlags (read, unsent), PidTagFlagStatus and PidTagLastVerbExecuted."));
        }

        l.Add(new FidelityCell("outlook-mail", ConvertFormat.Tnef, Fidelity.Exact, Fidelity.Exact, Fidelity.Exact, Fidelity.Lost, ["TnefWriterLimit"], string.Empty,
            "TNEF carries every property; a type or an OLE object it cannot hold is reported by name."));
        AddSkips(l, "outlook-mail", cal, "NotCalendarItem");
        AddSkips(l, "outlook-mail", card, "NotContactItem");

        // Outlook calendar items: written as iCalendar where the target is a calendar format; for a mail target as a side .ics file.
        foreach (ConvertFormat t in mail)
        {
            l.Add(new FidelityCell("outlook-calendar", t, Fidelity.Approximated, Fidelity.Lost, Fidelity.None, Fidelity.Derived, [.. OutlookToIcs, .. StoreCodes], string.Empty,
                "Routed to an .ics file in the side directory (or skipped with OtherItemsMode.Skip) by MS-OXCICAL."));
        }

        foreach (ConvertFormat t in cal)
        {
            l.Add(new FidelityCell("outlook-calendar", t, Fidelity.Approximated, Fidelity.Lost, Fidelity.None, Fidelity.Lost, t == ConvertFormat.Ics ? OutlookToIcs : [.. OutlookToIcs, .. Forms], string.Empty,
                "MS-OXCICAL: recurrence, exceptions, attendees and reminders carry; properties iCalendar has no field for are reported."));
        }

        l.Add(new FidelityCell("outlook-calendar", ConvertFormat.Tnef, Fidelity.Exact, Fidelity.Exact, Fidelity.Exact, Fidelity.Lost, ["TnefWriterLimit"], string.Empty, "Every property travels in the TNEF."));
        AddSkips(l, "outlook-calendar", card, "NotContactItem");

        foreach (ConvertFormat t in mail)
        {
            l.Add(new FidelityCell("outlook-contact", t, Fidelity.Approximated, Fidelity.Lost, Fidelity.None, Fidelity.Derived, [.. OutlookToVCard, .. StoreCodes], string.Empty,
                "Routed to a .vcf file in the side directory (or skipped) by MS-OXVCARD."));
        }

        foreach (ConvertFormat t in card)
        {
            l.Add(new FidelityCell("outlook-contact", t, Fidelity.Approximated, Fidelity.Lost, Fidelity.None, Fidelity.Lost, t == ConvertFormat.VCard ? OutlookToVCard : [.. OutlookToVCard, .. Forms], string.Empty,
                "MS-OXVCARD: names, phones, e-mail slots, addresses, dates and photo carry; properties vCard has no field for are reported."));
        }

        l.Add(new FidelityCell("outlook-contact", ConvertFormat.Tnef, Fidelity.Exact, Fidelity.Exact, Fidelity.Exact, Fidelity.Lost, ["TnefWriterLimit"], string.Empty, "Every property travels in the TNEF."));
        AddSkips(l, "outlook-contact", cal, "NotCalendarItem");

        // OLM: the view carries the mail; a message source, when the record has one, is the message itself.
        foreach (ConvertFormat t in mail)
        {
            l.Add(new FidelityCell("olm-mail", t, Fidelity.Approximated, Fidelity.Lost, Fidelity.Derived, Fidelity.Derived, [.. OlmToMime, .. StoreCodes, .. (t == ConvertFormat.Mbox ? MboxExtra : [])], string.Empty,
                "The message is rebuilt from the fields the view reads (addresses, subject, dates, identifiers, bodies, attachments); other OLM elements are listed as Unmapped. The read flag becomes Seen, categories become keywords."));
            l.Add(new FidelityCell("olm-calendar", t, Fidelity.Approximated, Fidelity.Lost, Fidelity.None, Fidelity.Derived, [.. OlmToCalendar, .. StoreCodes], string.Empty, "Routed to an .ics file in the side directory (or skipped)."));
            l.Add(new FidelityCell("olm-contact", t, Fidelity.Approximated, Fidelity.Lost, Fidelity.None, Fidelity.Derived, [.. OlmToCard, .. StoreCodes], string.Empty, "Routed to a .vcf file in the side directory (or skipped)."));
        }

        l.Add(new FidelityCell("olm-mail", ConvertFormat.Tnef, Fidelity.Lost, Fidelity.Lost, Fidelity.None, Fidelity.Lost, [], "NotMapiConvertible", "An OLM record has no property bag; it is skipped and counted."));
        AddSkips(l, "olm-mail", cal, "NotCalendarItem");
        AddSkips(l, "olm-mail", card, "NotContactItem");
        foreach (ConvertFormat t in cal)
        {
            l.Add(new FidelityCell("olm-calendar", t, Fidelity.Approximated, Fidelity.Lost, Fidelity.None, Fidelity.Lost, t == ConvertFormat.Ics ? OlmToCalendar : [.. OlmToCalendar, .. Forms], string.Empty, "Appointments, tasks and notes (VJOURNAL) carry their fields; the rest is listed as Unmapped."));
        }

        AddSkips(l, "olm-calendar", card, "NotContactItem");
        l.Add(new FidelityCell("olm-calendar", ConvertFormat.Tnef, Fidelity.Lost, Fidelity.Lost, Fidelity.None, Fidelity.Lost, [], "NotMapiConvertible", "An OLM record has no property bag; it is skipped and counted."));
        foreach (ConvertFormat t in card)
        {
            l.Add(new FidelityCell("olm-contact", t, Fidelity.Approximated, Fidelity.Lost, Fidelity.None, Fidelity.Lost, t == ConvertFormat.VCard ? OlmToCard : [.. OlmToCard, .. Forms], string.Empty, "Contacts and groups (KIND:group) carry their fields; the rest is listed as Unmapped."));
        }

        AddSkips(l, "olm-contact", cal, "NotCalendarItem");
        l.Add(new FidelityCell("olm-contact", ConvertFormat.Tnef, Fidelity.Lost, Fidelity.Lost, Fidelity.None, Fidelity.Lost, [], "NotMapiConvertible", "An OLM record has no property bag; it is skipped and counted."));

        foreach (ConvertFormat t in Enum.GetValues<ConvertFormat>())
        {
            string reason = t switch
            {
                ConvertFormat.Ics or ConvertFormat.JCal or ConvertFormat.XCal => "NotCalendarItem",
                ConvertFormat.VCard or ConvertFormat.JCard or ConvertFormat.XCard => "NotContactItem",
                ConvertFormat.Tnef or ConvertFormat.Msg or ConvertFormat.Pst => "NotMapiConvertible",
                _ => "NotMail",
            };
            l.Add(new FidelityCell("olm-other", t, Fidelity.Lost, Fidelity.None, Fidelity.None, Fidelity.Lost, [], reason, "A category or settings record is not mail, a calendar item or a contact; it is skipped and counted."));
        }

        // Calendar and card files: the same form is the file's own bytes; another form goes through the content-line model.
        foreach (ConvertFormat t in mail)
        {
            l.Add(new FidelityCell("calendar-file", t, Fidelity.Exact, Fidelity.None, Fidelity.None, Fidelity.Lost, [], string.Empty, "Kept as the file's own bytes in the side directory."));
            l.Add(new FidelityCell("card-file", t, Fidelity.Exact, Fidelity.None, Fidelity.None, Fidelity.Lost, [], string.Empty, "Kept as the file's own bytes in the side directory."));
        }

        foreach (ConvertFormat t in cal)
        {
            l.Add(new FidelityCell("calendar-file", t, t == ConvertFormat.Ics ? Fidelity.Exact : Fidelity.Derived, Fidelity.None, Fidelity.None, Fidelity.Lost, t == ConvertFormat.Ics ? [] : Forms, string.Empty,
                t == ConvertFormat.Ics ? "An iCalendar file converted to iCalendar is the same bytes; the forms of RFC 7265 and RFC 6321 carry the same components, and every difference is a Form code." : "Between forms the data carries; folding and name case change the text and are reported as bookkeeping."));
        }

        foreach (ConvertFormat t in card)
        {
            l.Add(new FidelityCell("card-file", t, t == ConvertFormat.VCard ? Fidelity.Exact : Fidelity.Derived, Fidelity.None, Fidelity.None, Fidelity.Lost, t == ConvertFormat.VCard ? [] : Forms, string.Empty,
                t == ConvertFormat.VCard ? "A vCard file converted to vCard is the same bytes." : "Between forms the data carries; every difference is a Form code."));
        }

        AddSkips(l, "calendar-file", card, "NotContactItem");
        AddSkips(l, "card-file", cal, "NotCalendarItem");
        l.Add(new FidelityCell("calendar-file", ConvertFormat.Tnef, Fidelity.Lost, Fidelity.None, Fidelity.None, Fidelity.Lost, [], "NotMail", "A calendar file is not a message; it is skipped and counted."));
        l.Add(new FidelityCell("card-file", ConvertFormat.Tnef, Fidelity.Lost, Fidelity.None, Fidelity.None, Fidelity.Lost, [], "NotMail", "A card file is not a message; it is skipped and counted."));
        // MSG: an Outlook item is written as it is; a MIME message or an OLM mail record becomes one first.
        string[] msgCodes = ["MsgNamedPropertyUnmapped", "MsgEmptyStringDropped", "MsgDuplicatePropertyDropped", "MsgValueResized", "MsgStorageEntryDropped", "MsgStorageDropped", "MsgStringsConverted", "MsgPropertyAdjusted", "FolderNameChanged"];
        string[] notKept = ["FlagsNotCarried", "KeywordsNotCarried"];
        l.Add(new FidelityCell("mime", ConvertFormat.Msg, Fidelity.Approximated, Fidelity.None, Fidelity.Lost, Fidelity.Lost, [.. MimeToOutlook, .. msgCodes, .. notKept], string.Empty,
            "The message is converted to an Outlook item (MS-OXCMAIL) and written as an MSG file. A file has no folder or store flags, so the flags and keywords are reported."));
        foreach (string family in new[] { "outlook-mail", "outlook-calendar", "outlook-contact" })
        {
            l.Add(new FidelityCell(family, ConvertFormat.Msg, Fidelity.Exact, Fidelity.Exact, Fidelity.Exact, Fidelity.Lost, msgCodes, string.Empty,
                "Every property is written, with its named-property name, recipients, attachments and embedded messages. Strings are written as Unicode and the store support mask says so."));
        }

        l.Add(new FidelityCell("olm-mail", ConvertFormat.Msg, Fidelity.Approximated, Fidelity.Lost, Fidelity.Lost, Fidelity.Lost, [.. OlmToMime, .. MimeToOutlook, .. msgCodes, .. notKept], string.Empty,
            "The record becomes a MIME message (its own source when it kept one), then an Outlook item."));
        l.Add(new FidelityCell("olm-calendar", ConvertFormat.Msg, Fidelity.Lost, Fidelity.Lost, Fidelity.None, Fidelity.Lost, [], "NotMapiConvertible", "An OLM record has no property bag; it is skipped and counted."));
        l.Add(new FidelityCell("olm-contact", ConvertFormat.Msg, Fidelity.Lost, Fidelity.Lost, Fidelity.None, Fidelity.Lost, [], "NotMapiConvertible", "An OLM record has no property bag; it is skipped and counted."));
        l.Add(new FidelityCell("calendar-file", ConvertFormat.Msg, Fidelity.Lost, Fidelity.None, Fidelity.None, Fidelity.Lost, [], "NotMail", "A calendar file is not a message; it is skipped and counted."));
        l.Add(new FidelityCell("card-file", ConvertFormat.Msg, Fidelity.Lost, Fidelity.None, Fidelity.None, Fidelity.Lost, [], "NotMail", "A card file is not a message; it is skipped and counted."));

        // PST: the same item conversions as MSG; the file is a store, so folders carry over and each item keeps its properties, named-property names, recipients, attachments and embedded messages.
        string[] pstCodes = ["PstNamedPropertyUnmapped", "PstNamedPropertyLimit", "PstDuplicatePropertyDropped", "PstUnsupportedType", "PstObjectStorageDropped", "PstPropertyAdded", "PstEmbeddingTooDeep", "PstTooManyChildren"];
        l.Add(new FidelityCell("mime", ConvertFormat.Pst, Fidelity.Approximated, Fidelity.None, Fidelity.Lost, Fidelity.Lost, [.. MimeToOutlook, .. pstCodes, .. notKept], string.Empty,
            "The message is converted to an Outlook item (MS-OXCMAIL) and written into the folder it came from. A PST holds flags as properties; a MIME message's flags and keywords are reported."));
        foreach (string family in new[] { "outlook-mail", "outlook-calendar", "outlook-contact" })
        {
            l.Add(new FidelityCell(family, ConvertFormat.Pst, Fidelity.Exact, Fidelity.Exact, Fidelity.Exact, Fidelity.Exact, pstCodes, string.Empty,
                "Every property is written as it is, with its named-property name, recipients, attachments and embedded messages, into the same folder. Properties a PST item must have and the item lacks are added and listed (PstPropertyAdded)."));
        }

        l.Add(new FidelityCell("olm-mail", ConvertFormat.Pst, Fidelity.Approximated, Fidelity.Lost, Fidelity.Lost, Fidelity.Lost, [.. OlmToMime, .. MimeToOutlook, .. pstCodes, .. notKept], string.Empty,
            "The record becomes a MIME message (its own source when it kept one), then an Outlook item."));
        l.Add(new FidelityCell("olm-calendar", ConvertFormat.Pst, Fidelity.Lost, Fidelity.Lost, Fidelity.None, Fidelity.Lost, [], "NotMapiConvertible", "An OLM record has no property bag; it is skipped and counted."));
        l.Add(new FidelityCell("olm-contact", ConvertFormat.Pst, Fidelity.Lost, Fidelity.Lost, Fidelity.None, Fidelity.Lost, [], "NotMapiConvertible", "An OLM record has no property bag; it is skipped and counted."));
        l.Add(new FidelityCell("calendar-file", ConvertFormat.Pst, Fidelity.Lost, Fidelity.None, Fidelity.None, Fidelity.Lost, [], "NotMail", "A calendar file is not a message; it is skipped and counted."));
        l.Add(new FidelityCell("card-file", ConvertFormat.Pst, Fidelity.Lost, Fidelity.None, Fidelity.None, Fidelity.Lost, [], "NotMail", "A card file is not a message; it is skipped and counted."));
        return l;
    }

    private static void AddSkips(List<FidelityCell> l, string source, ConvertFormat[] targets, string reason)
    {
        foreach (ConvertFormat t in targets)
        {
            l.Add(new FidelityCell(source, t, Fidelity.Lost, Fidelity.None, Fidelity.None, Fidelity.Lost, [], reason, "Not an item of that family; it is skipped and counted."));
        }
    }

    /// <summary>Writes the matrix as the text of <c>docs/fidelity.md</c>'s tables.</summary>
    /// <returns>Markdown, LF line ends, ending with a newline.</returns>
    public static string ToMarkdown()
    {
        StringBuilder sb = new();
        foreach ((string id, string title) in Sources)
        {
            sb.Append("### ").Append(title).Append('\n').Append('\n');
            sb.Append("| To | Content | Properties | Flags | Folders | Codes it can report | Note |\n|---|---|---|---|---|---|---|\n");
            foreach (FidelityCell c in Cells.Where(c => c.Source == id))
            {
                string codes = c.SkipReason.Length > 0 ? "skipped: `" + c.SkipReason + "`" : c.Codes.Count == 0 ? "none" : string.Join(", ", c.Codes.Order(StringComparer.Ordinal).Select(x => "`" + x + "`"));
                sb.Append("| ").Append(c.Target).Append(" | ").Append(c.Content).Append(" | ").Append(c.Properties).Append(" | ").Append(c.Flags).Append(" | ").Append(c.Folders)
                    .Append(" | ").Append(codes).Append(" | ").Append(c.Note).Append(" |\n");
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }
}
