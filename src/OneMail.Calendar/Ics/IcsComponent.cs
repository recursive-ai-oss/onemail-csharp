using System.Globalization;
using RecursiveAI.OneMail.Calendar.ContentLines;

namespace RecursiveAI.OneMail.Calendar.Ics;

/// <summary>The iTIP METHOD values (RFC 5546 §1.4).</summary>
public enum IcsMethod
{
    /// <summary>No METHOD, or one this enum does not name; see <see cref="IcsComponent.MethodText"/>.</summary>
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
}

/// <summary>A CAL-ADDRESS property (ORGANIZER, ATTENDEE) with its common parameters.</summary>
/// <param name="Address">The value, usually <c>mailto:</c> plus an address.</param>
/// <param name="CommonName">The CN parameter.</param>
/// <param name="Role">The ROLE parameter.</param>
/// <param name="ParticipationStatus">The PARTSTAT parameter.</param>
/// <param name="Rsvp">The RSVP parameter, when present and TRUE/FALSE.</param>
/// <param name="CalendarUserType">The CUTYPE parameter.</param>
/// <param name="SentBy">The SENT-BY parameter.</param>
/// <param name="Property">The property itself, with every parameter.</param>
public sealed record IcsCalAddress(
    string Address, string? CommonName, string? Role, string? ParticipationStatus, bool? Rsvp, string? CalendarUserType, string? SentBy, ContentProperty Property)
{
    /// <summary>The address without a leading <c>mailto:</c> (case-insensitive).</summary>
    public string Email => Address.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? Address[7..] : Address;
}

/// <summary>A VALARM trigger (RFC 5545 §3.8.6.3): a duration from the start or end, or an absolute time.</summary>
/// <param name="Offset">The duration, for a relative trigger.</param>
/// <param name="Absolute">The date-time, for <c>VALUE=DATE-TIME</c>.</param>
/// <param name="RelatedToEnd">True when <c>RELATED=END</c>.</param>
public readonly record struct IcsTrigger(IcsDuration? Offset, IcsTime? Absolute, bool RelatedToEnd);

/// <summary>
/// A typed view over a <see cref="Component"/> read from an ICS file: RFC 5545 properties, the RFC 7986 additions
/// and the iTIP METHOD. Reads parse on demand and return null (or an empty list) when a property is missing or its value is malformed;
/// the property itself stays in the tree. The <c>Set*</c> methods change the underlying tree.
/// </summary>
public sealed class IcsComponent : IEquatable<IcsComponent>
{
    /// <summary>Wraps a component.</summary>
    /// <param name="component">Any component; the view does not check its name.</param>
    public IcsComponent(Component component)
    {
        ArgumentNullException.ThrowIfNull(component);
        Component = component;
    }

    /// <summary>The underlying tree node.</summary>
    public Component Component { get; }

    /// <summary>The component name as written, for example <c>VEVENT</c>.</summary>
    public string Name => Component.Name;

    /// <summary>The nested components.</summary>
    public IEnumerable<IcsComponent> Children => Component.Components.Select(c => new IcsComponent(c));

    /// <summary>Nested VEVENTs.</summary>
    public IEnumerable<IcsComponent> Events => Of("VEVENT");

    /// <summary>Nested VTODOs.</summary>
    public IEnumerable<IcsComponent> Todos => Of("VTODO");

    /// <summary>Nested VJOURNALs.</summary>
    public IEnumerable<IcsComponent> Journals => Of("VJOURNAL");

    /// <summary>Nested VFREEBUSYs.</summary>
    public IEnumerable<IcsComponent> FreeBusies => Of("VFREEBUSY");

    /// <summary>Nested VTIMEZONEs.</summary>
    public IEnumerable<IcsComponent> TimeZones => Of("VTIMEZONE");

    /// <summary>Nested VALARMs.</summary>
    public IEnumerable<IcsComponent> Alarms => Of("VALARM");

    /// <summary>The STANDARD and DAYLIGHT observances of a VTIMEZONE.</summary>
    public IEnumerable<IcsComponent> Observances => Children.Where(c => c.Component.IsNamed("STANDARD") || c.Component.IsNamed("DAYLIGHT"));

    private IEnumerable<IcsComponent> Of(string name) => Component.FindComponents(name).Select(c => new IcsComponent(c));

    // ---- Text ----

    /// <summary>The first property of this name as plain text (escapes resolved), or null.</summary>
    /// <param name="property">The property name.</param>
    /// <returns>The text.</returns>
    public string? GetText(string property) => Component.FindProperty(property)?.GetText();

    /// <summary>UID.</summary>
    public string? Uid => GetText("UID");

    /// <summary>SUMMARY.</summary>
    public string? Summary => GetText("SUMMARY");

    /// <summary>DESCRIPTION.</summary>
    public string? Description => GetText("DESCRIPTION");

    /// <summary>LOCATION.</summary>
    public string? Location => GetText("LOCATION");

    /// <summary>STATUS.</summary>
    public string? Status => GetText("STATUS");

    /// <summary>CLASS.</summary>
    public string? Classification => GetText("CLASS");

    /// <summary>TRANSP.</summary>
    public string? Transparency => GetText("TRANSP");

    /// <summary>URL.</summary>
    public string? Url => GetText("URL");

    /// <summary>PRODID (VCALENDAR).</summary>
    public string? ProductId => GetText("PRODID");

    /// <summary>VERSION (VCALENDAR), normally <c>2.0</c>.</summary>
    public string? Version => GetText("VERSION");

    /// <summary>CALSCALE (VCALENDAR).</summary>
    public string? CalendarScale => GetText("CALSCALE");

    /// <summary>NAME (RFC 7986 §5.1).</summary>
    public string? CalendarName => GetText("NAME");

    /// <summary>COLOR (RFC 7986 §5.9), a CSS3 color name.</summary>
    public string? Color => GetText("COLOR");

    /// <summary>TZID of a VTIMEZONE.</summary>
    public string? TimeZoneId => GetText("TZID");

    /// <summary>ACTION of a VALARM.</summary>
    public string? Action => GetText("ACTION");

    /// <summary>The METHOD text (VCALENDAR) as written, whatever it is.</summary>
    public string? MethodText => GetText("METHOD");

    /// <summary>METHOD as an iTIP method; <see cref="IcsMethod.None"/> when absent or unknown.</summary>
    public IcsMethod Method => Enum.TryParse(MethodText?.Trim(), true, out IcsMethod m) && Enum.IsDefined(m) ? m : IcsMethod.None;

    /// <summary>The CATEGORIES values from every CATEGORIES property, split at unescaped commas.</summary>
    public IReadOnlyList<string> Categories => Component.FindProperties("CATEGORIES")
        .SelectMany(p => ContentValues.SplitUnescaped(p.Value, ',').Select(v => ContentValues.Unescape(v))).ToList();

    // ---- Numbers ----

    private int? GetInt(string property) =>
        int.TryParse(Component.FindProperty(property)?.Value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int v) ? v : null;

    /// <summary>PRIORITY.</summary>
    public int? Priority => GetInt("PRIORITY");

    /// <summary>SEQUENCE.</summary>
    public int? Sequence => GetInt("SEQUENCE");

    /// <summary>PERCENT-COMPLETE.</summary>
    public int? PercentComplete => GetInt("PERCENT-COMPLETE");

    /// <summary>REPEAT of a VALARM.</summary>
    public int? Repeat => GetInt("REPEAT");

    /// <summary>GEO as latitude and longitude.</summary>
    public (double Latitude, double Longitude)? Geo
    {
        get
        {
            string[]? parts = Component.FindProperty("GEO")?.Value.Split(';');
            return parts is { Length: 2 }
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon)
                ? (lat, lon) : null;
        }
    }

    // ---- Time ----

    /// <summary>Reads a DATE or DATE-TIME property, using its <c>TZID</c> parameter.</summary>
    /// <param name="property">The property name.</param>
    /// <returns>The value, or null when missing or malformed.</returns>
    public IcsTime? GetTime(string property) => ReadTime(Component.FindProperty(property));

    private static IcsTime? ReadTime(ContentProperty? p) =>
        p is not null && IcsTime.TryParse(p.Value, p.GetParameter("TZID"), out IcsTime t) ? t : null;

    /// <summary>DTSTART.</summary>
    public IcsTime? Start => GetTime("DTSTART");

    /// <summary>DTEND.</summary>
    public IcsTime? End => GetTime("DTEND");

    /// <summary>DUE.</summary>
    public IcsTime? Due => GetTime("DUE");

    /// <summary>COMPLETED.</summary>
    public IcsTime? Completed => GetTime("COMPLETED");

    /// <summary>CREATED.</summary>
    public IcsTime? Created => GetTime("CREATED");

    /// <summary>LAST-MODIFIED.</summary>
    public IcsTime? LastModified => GetTime("LAST-MODIFIED");

    /// <summary>DTSTAMP.</summary>
    public IcsTime? Stamp => GetTime("DTSTAMP");

    /// <summary>RECURRENCE-ID.</summary>
    public IcsTime? RecurrenceId => GetTime("RECURRENCE-ID");

    /// <summary>DURATION.</summary>
    public IcsDuration? Duration => IcsDuration.TryParse(Component.FindProperty("DURATION")?.Value, out IcsDuration d) ? d : null;

    /// <summary>TZOFFSETFROM of an observance.</summary>
    public IcsUtcOffset? OffsetFrom => IcsUtcOffset.TryParse(Component.FindProperty("TZOFFSETFROM")?.Value, out IcsUtcOffset o) ? o : null;

    /// <summary>TZOFFSETTO of an observance.</summary>
    public IcsUtcOffset? OffsetTo => IcsUtcOffset.TryParse(Component.FindProperty("TZOFFSETTO")?.Value, out IcsUtcOffset o) ? o : null;

    /// <summary>Every RRULE that parses.</summary>
    public IReadOnlyList<RecurrenceRule> RecurrenceRules => Component.FindProperties("RRULE")
        .Select(p => RecurrenceRule.TryParse(p.Value, p.GetParameter("TZID") ?? Component.FindProperty("DTSTART")?.GetParameter("TZID"), out RecurrenceRule r) ? r : null)
        .OfType<RecurrenceRule>().ToList();

    /// <summary>Every EXDATE or RDATE value that is a DATE or DATE-TIME (PERIOD values of RDATE are skipped; read them with <see cref="RecurrencePeriods"/>).</summary>
    /// <param name="property">EXDATE or RDATE.</param>
    /// <returns>The times in order.</returns>
    public IReadOnlyList<IcsTime> GetTimeList(string property)
    {
        List<IcsTime> list = [];
        foreach (ContentProperty p in Component.FindProperties(property))
        {
            string? tzid = p.GetParameter("TZID");
            foreach (string v in p.Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (IcsTime.TryParse(v, tzid, out IcsTime t))
                {
                    list.Add(t);
                }
            }
        }

        return list;
    }

    /// <summary>EXDATE values.</summary>
    public IReadOnlyList<IcsTime> ExceptionDates => GetTimeList("EXDATE");

    /// <summary>RDATE values that are dates or date-times.</summary>
    public IReadOnlyList<IcsTime> RecurrenceDates => GetTimeList("RDATE");

    /// <summary>RDATE and FREEBUSY values that are periods.</summary>
    public IReadOnlyList<IcsPeriod> RecurrencePeriods
    {
        get
        {
            List<IcsPeriod> list = [];
            foreach (ContentProperty p in Component.Properties.Where(x => x.IsNamed("RDATE") || x.IsNamed("FREEBUSY")))
            {
                foreach (string v in p.Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (IcsPeriod.TryParse(v, p.GetParameter("TZID"), out IcsPeriod per))
                    {
                        list.Add(per);
                    }
                }
            }

            return list;
        }
    }

    /// <summary>TRIGGER of a VALARM.</summary>
    public IcsTrigger? Trigger
    {
        get
        {
            ContentProperty? p = Component.FindProperty("TRIGGER");
            if (p is null)
            {
                return null;
            }

            bool end = string.Equals(p.GetParameter("RELATED"), "END", StringComparison.OrdinalIgnoreCase);
            if (IcsDuration.TryParse(p.Value, out IcsDuration d))
            {
                return new IcsTrigger(d, null, end);
            }

            return IcsTime.TryParse(p.Value, p.GetParameter("TZID"), out IcsTime t) ? new IcsTrigger(null, t, end) : null;
        }
    }

    // ---- People ----

    private static IcsCalAddress Address(ContentProperty p)
    {
        bool? rsvp = p.GetParameter("RSVP") is { } r ? string.Equals(r, "TRUE", StringComparison.OrdinalIgnoreCase) ? true : string.Equals(r, "FALSE", StringComparison.OrdinalIgnoreCase) ? false : null : null;
        return new IcsCalAddress(ContentValues.Unescape(p.Value), p.GetParameter("CN"), p.GetParameter("ROLE"), p.GetParameter("PARTSTAT"), rsvp, p.GetParameter("CUTYPE"), p.GetParameter("SENT-BY"), p);
    }

    /// <summary>ORGANIZER.</summary>
    public IcsCalAddress? Organizer => Component.FindProperty("ORGANIZER") is { } p ? Address(p) : null;

    /// <summary>Every ATTENDEE.</summary>
    public IReadOnlyList<IcsCalAddress> Attendees => Component.FindProperties("ATTENDEE").Select(Address).ToList();

    // ---- Editing ----

    private ContentProperty Upsert(string name, string value)
    {
        ContentProperty? p = Component.FindProperty(name);
        if (p is null)
        {
            p = new ContentProperty(name, value);
            Component.Add(p);
        }
        else
        {
            p.Value = value;
        }

        return p;
    }

    /// <summary>Sets a TEXT property (escaping the text), replacing the first property of that name or adding one.</summary>
    /// <param name="property">The property name.</param>
    /// <param name="text">The plain text.</param>
    public void SetText(string property, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Upsert(property, ContentValues.Escape(text));
    }

    /// <summary>Sets a DATE or DATE-TIME property, adding <c>VALUE=DATE</c> or <c>TZID</c> as the value needs and dropping stale ones.</summary>
    /// <param name="property">The property name.</param>
    /// <param name="time">The time.</param>
    public void SetTime(string property, IcsTime time)
    {
        ContentProperty p = Upsert(property, time.ToString());
        p.Parameters.Remove("TZID");
        p.Parameters.Remove("VALUE");
        if (time.Kind == IcsTimeKind.Date)
        {
            p.Parameters.Set("VALUE", "DATE");
        }
        else if (time.Kind == IcsTimeKind.Zoned && time.TzId is not null)
        {
            p.Parameters.Set("TZID", time.TzId);
        }
    }

    /// <summary>Sets a DURATION property.</summary>
    /// <param name="duration">The duration.</param>
    public void SetDuration(IcsDuration duration) => Upsert("DURATION", duration.ToString());

    /// <summary>Removes every property of this name.</summary>
    /// <param name="property">The property name.</param>
    /// <returns>How many were removed.</returns>
    public int Remove(string property) => Component.RemoveProperties(property);

    /// <inheritdoc />
    public bool Equals(IcsComponent? other) => other is not null && ReferenceEquals(Component, other.Component);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is IcsComponent o && Equals(o);

    /// <inheritdoc />
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Component);
}

/// <summary>Reading ICS files.</summary>
public static class IcsDocument
{
    /// <summary>The VCALENDAR components of a parsed document, as typed views.</summary>
    /// <param name="document">A document from <see cref="ContentDocument"/>.</param>
    /// <returns>The calendars.</returns>
    public static IEnumerable<IcsComponent> Calendars(ContentDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Components.Where(c => c.IsNamed("VCALENDAR")).Select(c => new IcsComponent(c));
    }

    /// <summary>Parses ICS bytes and returns the document (a thin wrapper for <see cref="ContentDocument.Parse(ReadOnlyMemory{byte}, ContentDocumentOptions?)"/>).</summary>
    /// <param name="data">The bytes.</param>
    /// <returns>The document.</returns>
    public static ContentDocument Parse(ReadOnlyMemory<byte> data) => ContentDocument.Parse(data);
}
