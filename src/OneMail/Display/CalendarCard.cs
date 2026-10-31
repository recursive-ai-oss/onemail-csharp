using System.Globalization;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.Ics;
using RecursiveAI.OneMail.Calendar.Recurrence;
using RecursiveAI.OneMail.Calendar.TimeZones;

namespace RecursiveAI.OneMail.Display;

/// <summary>Something the card builder had to cut, skip or could not do. Never an exception.</summary>
public enum CalendarCardDefectKind
{
    /// <summary>The description was cut at <see cref="CalendarCardOptions.MaxDescriptionLength"/> characters or the line limit.</summary>
    DescriptionTruncated = 1,

    /// <summary>A title, location, name or other field was cut at <see cref="CalendarCardOptions.MaxFieldLength"/> characters.</summary>
    FieldTruncated,

    /// <summary>More attendees than <see cref="CalendarCardOptions.MaxAttendees"/>; the rest are counted, not listed.</summary>
    AttendeesTruncated,

    /// <summary>A URL was left out: it is not an absolute http(s) URL (or mailto for a link), or it is longer than the field limit.</summary>
    UrlRejected,

    /// <summary>The recurrence rule is not one the sentence forms cover; the card says "Repeats (custom rule)".</summary>
    RecurrenceNotDescribed,

    /// <summary>A TZID could not be resolved; the time is shown as written without a conversion.</summary>
    UnresolvedZone,

    /// <summary>The requested display time zone is unknown; times are shown in the event's own zone.</summary>
    DisplayZoneUnresolved,

    /// <summary>Expanding the recurrence hit a limit before it found the occurrences asked for.</summary>
    ExpansionLimited,

    /// <summary>The calendar object holds more events than <see cref="CalendarCardOptions.MaxCards"/>; the rest have no card.</summary>
    EventsTruncated,

    /// <summary>More attachments than the card lists.</summary>
    AttachmentsTruncated,
}

/// <summary>A defect with the detail that explains it (a TZID, a URL, a count), already cleaned for display.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Detail">The value concerned, or null.</param>
public readonly record struct CalendarCardDefect(CalendarCardDefectKind Kind, string? Detail);

/// <summary>An attendee as the card shows them.</summary>
/// <param name="Name">The common name, or null.</param>
/// <param name="Address">The address without <c>mailto:</c>.</param>
/// <param name="Role">The role in words ("required", "optional", "chair", "for information"), or null.</param>
/// <param name="Response">The participation status in words ("accepted", "declined", "tentative", "no reply yet", "delegated"), or null.</param>
/// <param name="Kind">"room", "resource" or "group" for a non-person, or null.</param>
public sealed record CalendarCardAttendee(string? Name, string Address, string? Role, string? Response, string? Kind);

/// <summary>A conference or event URL: the property it came from and the normalised absolute URL.</summary>
/// <param name="Label">"Join" for conference properties, otherwise the property name.</param>
/// <param name="Url">The URL as <see cref="Uri.AbsoluteUri"/>, with an http, https or mailto scheme.</param>
public sealed record CalendarCardLink(string Label, string Url);

/// <summary>Which iTIP method the card shows (RFC 5546 §1.4).</summary>
public enum CalendarCardMethod
{
    /// <summary>No METHOD: a plain event (an .ics attachment or an appointment).</summary>
    None = 0,

    /// <summary>PUBLISH.</summary>
    Publish,

    /// <summary>REQUEST.</summary>
    Request,

    /// <summary>REPLY.</summary>
    Reply,

    /// <summary>ADD.</summary>
    Add,

    /// <summary>CANCEL.</summary>
    Cancel,

    /// <summary>REFRESH.</summary>
    Refresh,

    /// <summary>COUNTER.</summary>
    Counter,

    /// <summary>DECLINECOUNTER.</summary>
    DeclineCounter,

    /// <summary>A METHOD value this list does not name.</summary>
    Other,
}

/// <summary>Choices for building a <see cref="CalendarCard"/>. Every limit is on by default.</summary>
public sealed record CalendarCardOptions
{
    /// <summary>The zone to show times in, as an IANA, Windows or calendar-defined name (<c>Europe/Paris</c>). Null shows each time in the event's own zone. An unknown name is a defect and falls back to the event's zone.</summary>
    public string? DisplayTimeZone { get; init; }

    /// <summary>How many upcoming occurrences of a recurring event to list, 0 to 50. Default 5.</summary>
    public int NextOccurrences { get; init; } = 5;

    /// <summary>"Now" for the occurrence list, as a UTC instant. Null starts at the series' first occurrence. The builder never reads the clock.</summary>
    public DateTime? After { get; init; }

    /// <summary>The most description characters shown. Default 1,000; the description is also cut at 30 lines.</summary>
    public int MaxDescriptionLength { get; init; } = 1000;

    /// <summary>The most attendees listed. Default 50; the others are counted.</summary>
    public int MaxAttendees { get; init; } = 50;

    /// <summary>The most characters of any one-line field (title, location, name, URL). Default 200.</summary>
    public int MaxFieldLength { get; init; } = 200;

    /// <summary>The most cards one calendar object yields. Default 20.</summary>
    public int MaxCards { get; init; } = 20;
}

/// <summary>
/// A compact card for a calendar object in a message: what it is (invitation, update, cancellation, reply), when and where, the
/// recurrence in plain English, who is invited, and the description. Built from an iCalendar object, so Outlook meeting items
/// reach it through the MS-OXCICAL conversion. Every string is cleaned text; the HTML is built from escaped text with fixed markup.
/// </summary>
public sealed partial class CalendarCard
{
    internal CalendarCard()
    {
    }

    /// <summary>The method, from the calendar object's METHOD (or the message class for an Outlook item).</summary>
    public CalendarCardMethod Method { get; internal set; }

    /// <summary>The method as an action: "Invitation", "Updated invitation", "Cancelled", "Alice accepted", "Counter-proposal", ...</summary>
    public string Action { get; internal set; } = string.Empty;

    /// <summary>The SUMMARY, or "(no title)".</summary>
    public string Summary { get; internal set; } = string.Empty;

    /// <summary>"Cancelled" or "Tentative" from STATUS (or a CANCEL method), else null.</summary>
    public string? Status { get; internal set; }

    /// <summary>The time or range, in <see cref="CalendarCardOptions.DisplayTimeZone"/> when set and known: "Mon, 30 Nov 2026 14:00–15:00 (Europe/Paris, UTC+01:00)" or "All day, Mon, 30 Nov 2026". Empty when the event has no start.</summary>
    public string When { get; internal set; } = string.Empty;

    /// <summary>The same time in the event's own zone ("Organizer's time"), when the display zone differs; else null.</summary>
    public string? OriginalTime { get; internal set; }

    /// <summary>The recurrence sentence with its exception note, "Repeats (custom rule)" when the rule is not describable, or null for a single event.</summary>
    public string? Recurrence { get; internal set; }

    /// <summary>True when <see cref="Recurrence"/> is a sentence, false for the custom-rule text or no recurrence.</summary>
    public bool RecurrenceDescribed { get; internal set; }

    /// <summary>The upcoming occurrences, formatted like <see cref="When"/>; empty for a single event.</summary>
    public IReadOnlyList<string> Occurrences { get; internal set; } = [];

    /// <summary>The same occurrences as data: start and end as written, the UTC instants when the zone is known.</summary>
    public IReadOnlyList<Occurrence> OccurrenceData { get; internal set; } = [];

    /// <summary>The location, or null.</summary>
    public string? Location { get; internal set; }

    /// <summary>Conference and event URLs, at most five, as text.</summary>
    public IReadOnlyList<CalendarCardLink> Links { get; internal set; } = [];

    /// <summary>The organizer ("Name (address)"), or null.</summary>
    public string? Organizer { get; internal set; }

    /// <summary>The listed attendees.</summary>
    public IReadOnlyList<CalendarCardAttendee> Attendees { get; internal set; } = [];

    /// <summary>How many attendees the event has in all.</summary>
    public int AttendeeCount { get; internal set; }

    /// <summary>File names or URLs of ATTACH properties (at most ten), as text.</summary>
    public IReadOnlyList<string> Attachments { get; internal set; } = [];

    /// <summary>The description, truncated, with line breaks as <c>\n</c>; or null.</summary>
    public string? Description { get; internal set; }

    /// <summary>What was cut or skipped while building this card.</summary>
    public IReadOnlyList<CalendarCardDefect> Defects { get; internal set; } = [];

    /// <summary>The UID of the event.</summary>
    public string? Uid { get; internal set; }
}
