using System.Globalization;
using RecursiveAI.OneMail.Conversion;

namespace RecursiveAI.OneMail.Stores.Olm;

/// <summary>
/// Which top-level elements of each record kind OneMail reads, either through the shared view (<see cref="OlmItem"/>) or through the
/// calendar and vCard conversions. Everything else stays in <see cref="OlmItem.Element"/> and is listed by <see cref="OlmItem.UnmappedElements"/>.
/// </summary>
internal static class OlmMapping
{
    public const string Family = "olm";

    private static readonly HashSet<string> Email = new(StringComparer.Ordinal)
    {
        "OPFMessageCopySubject", "OPFMessageCopySenderAddress", "OPFMessageCopyFromAddresses", "OPFMessageCopyToAddresses", "OPFMessageCopyCCAddresses",
        "OPFMessageCopyBCCAddresses", "OPFMessageCopyReplyToAddresses", "OPFMessageCopySentTime", "OPFMessageCopyReceivedTime", "OPFMessageCopyMessageID",
        "OPFMessageCopyInReplyTo", "OPFMessageCopyReferences", "OPFMessageCopyThreadIndex", "OPFMessageCopyThreadTopic", "OPFMessageCopyBody",
        "OPFMessageCopyHTMLBody", "OPFMessageGetHasHTML", "OPFMessageCopyAttachmentList", "OPFMessageCopySource", "OPFMessageCopyCategoryList",
    };

    private static readonly HashSet<string> Appointment = new(StringComparer.Ordinal)
    {
        "OPFCalendarEventCopySummary", "OPFCalendarEventCopyLocation", "OPFCalendarEventCopyDescriptionPlain", "OPFCalendarEventCopyDescription",
        "OPFCalendarEventCopyStartTime", "OPFCalendarEventCopyEndTime", "OPFCalendarEventCopyStartTimeZone", "OPFCalendarEventCopyEndTimeZone",
        "OPFCalendarEventGetStartTimeZoneICSData", "OPFCalendarEventGetIsAllDayEvent", "OPFCalendarEventCopyUUID", "OPFCalendarEventCopyModDate",
        "OPFCalendarEventCopyRecurrence", "OPFCalendarEventIsRecurring", "OPFCalendarEventGetHasReminder", "OPFCalendarEventCopyReminderDelta",
        "OPFCalendarEventGetIsPrivate", "OPFCalendarEventCopyAttendeeList", "OPFCalendarEventCopyOrganizer", "OPFCalendarEventCopyCategoryList",
        "OPFCalendarEventCopyAttachmentList",
    };

    private static readonly HashSet<string> Contact = new(StringComparer.Ordinal)
    {
        "OPFContactCopyDisplayName", "OPFContactCopyFirstName", "OPFContactCopyMiddleName", "OPFContactCopyLastName", "OPFContactCopyTitle",
        "OPFContactCopySetNameSuffix", "OPFContactCopyNickName", "OPFContactCopyBusinessCompany", "OPFContactCopyBusinessDepartment", "OPFContactCopyBusinessTitle",
        "OPFContactCopyBusinessPhone", "OPFContactCopyBusinessPhone2", "OPFContactCopyBusinessFax", "OPFContactCopyHomePhone", "OPFContactCopyHomePhone2",
        "OPFContactCopyHomeFax", "OPFContactCopyCellPhone", "OPFContactCopyPager", "OPFContactCopyOtherPhone", "OPFContactCopyOtherFax", "OPFContactCopyAssistantPhone",
        "OPFContactCopyEmailAddressList", "OPFContactCopyEmailAddressList1", "OPFContactCopyEmailAddressList2", "OPFContactCopyDefaultEmailAddress",
        "OPFContactCopyBusinessStreetAddress", "OPFContactCopyBusinessCity", "OPFContactCopyBusinessState", "OPFContactCopyBusinessZip", "OPFContactCopyBusinessCountry",
        "OPFContactCopyHomeStreetAddress", "OPFContactCopyHomeCity", "OPFContactCopyHomeState", "OPFContactCopyHomeZip", "OPFContactCopyHomeCountry",
        "OPFContactCopyOtherStreetAddress", "OPFContactCopyOtherCity", "OPFContactCopyOtherState", "OPFContactCopyOtherZip", "OPFContactCopyOtherCountry",
        "OPFContactCopyBusinessHomePage", "OPFContactCopyHomeWebPage", "OPFContactCopyBirthday", "OPFContactCopyAnniversary", "OPFContactCopyNotesPlain",
        "OPFContactCopyNotes", "OPFContactCopySpousesName", "OPFContactCopyCategoryList", "OPFContactCopyModDate",
    };

    private static readonly HashSet<string> Task = new(StringComparer.Ordinal)
    {
        "OPFTaskCopyName", "OPFTaskCopyNotePlain", "OPFTaskCopyNote", "OPFTaskCopyDueDateTime", "OPFTaskCopyStartDateTime", "OPFTaskCopyCompletedDateTime",
        "OPFTaskCopyReminderTime", "OPFTaskCopyRecurrence", "OPFTaskCopyCategoryList", "OPFTaskCopyModDate",
    };

    private static readonly HashSet<string> Note = new(StringComparer.Ordinal)
    {
        "OPFNoteCopyTitle", "OPFNoteCopyText", "OPFNoteCopyCreationDate", "OPFNoteCopyModDate",
    };

    private static readonly HashSet<string> Group = new(StringComparer.Ordinal)
    {
        "OPFGroupCopyDisplayName", "OPFGroupCopyMemberList", "OPFGroupCopyModDate",
    };

    private static readonly HashSet<string> None = new(StringComparer.Ordinal);

    public static string CategoryListName(OlmItemKind kind) => kind switch
    {
        OlmItemKind.Email => "OPFMessageCopyCategoryList",
        OlmItemKind.Appointment => "OPFCalendarEventCopyCategoryList",
        OlmItemKind.Contact => "OPFContactCopyCategoryList",
        OlmItemKind.Task => "OPFTaskCopyCategoryList",
        _ => string.Empty,
    };

    public static bool IsMapped(OlmItemKind kind, string name) => (kind switch
    {
        OlmItemKind.Email => Email,
        OlmItemKind.Appointment => Appointment,
        OlmItemKind.Contact => Contact,
        OlmItemKind.Task => Task,
        OlmItemKind.Note => Note,
        OlmItemKind.Group => Group,
        _ => None,
    }).Contains(name);

    public static IReadOnlyList<OlmElement> Unmapped(OlmItem item)
    {
        if (item.OlmKind == OlmItemKind.Category)
        {
            return [];
        }

        var list = new List<OlmElement>();
        foreach (OlmElement e in item.Element.Children)
        {
            if (!IsMapped(item.OlmKind, e.Name))
            {
                list.Add(e);
            }
        }

        return list;
    }
}

/// <summary>Reads an OLM date into UTC, the way the shared view does. The files carry no zone.</summary>
internal static class OlmValues
{
    // Outlook's "no date" is the year 4501 (MS-OXOCAL, MS-OXOCNTC); an OLM record that has the element but no date uses the same value.
    public static DateTimeOffset? Date(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !OlmItem.TryParseDate(text, out DateTimeOffset v) || v.Year >= 4500)
        {
            return null;
        }

        return v;
    }

    public static string Utc(DateTimeOffset d) => d.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    public static string Local(DateTimeOffset d) => d.UtcDateTime.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);

    public static string DateOnly(DateTimeOffset d) => d.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
}
