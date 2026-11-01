using System.Text;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.Ics;
using RecursiveAI.OneMail.Calendar.Recurrence;
using RecursiveAI.OneMail.Calendaring;
using RecursiveAI.OneMail.Conversion;
using RecursiveAI.OneMail.Conversion.OxcIcal;
using RecursiveAI.OneMail.Mapi;
using Xunit;

namespace RecursiveAI.OneMail.Tests.Calendaring;

/// <summary>The iCalendar-to-Outlook rules of MS-OXCICAL one at a time: what carries, what is reported, and that the series means the same afterwards.</summary>
public sealed class OxcIcalImportRuleTests
{
    private static IcsComponent Cal(string body, string head = "") =>
        IcsDocument.Calendars(ContentDocument.Parse(Encoding.UTF8.GetBytes("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//Test//EN\r\n" + head + body.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\nEND:VCALENDAR\r\n"))).First();

    private static (MapiMessage Item, LossReport Import, IcsComponent Back, LossReport Export) RoundTrip(IcsComponent cal)
    {
        ConversionResult<MapiMessage> imp = IcsToMapiConverter.ConvertAll(cal)[0];
        ConversionResult<ContentDocument> exp = new MapiToIcsConverter().Convert(imp.Value);
        return (imp.Value, imp.Report, IcsDocument.Calendars(ContentDocument.Parse(exp.Value.ToArray())).First(), exp.Report);
    }

    private static IcsComponent Ev(IcsComponent calendar) => calendar.Events.First();

    private static List<string> Starts(IcsComponent cal, string until = "20300101T000000Z") =>
        CalendarRecurrence.Expand(cal, new OccurrenceWindow(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc), DateTime.ParseExact(until, "yyyyMMdd'T'HHmmss'Z'", null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal)))
            .Select(o => (o.StartUtc ?? o.Start.Value).ToString("yyyyMMdd'T'HHmmss", System.Globalization.CultureInfo.InvariantCulture)).OrderBy(x => x, StringComparer.Ordinal).ToList();

    private static void SameOccurrences(string body, string head = "")
    {
        IcsComponent cal = Cal(body, head);
        var (_, _, back, _) = RoundTrip(cal);
        Assert.Equal(Starts(cal), Starts(back));
    }

    [Fact]
    public void UntilBoundsTheStartOfTheLastInstanceNotItsDay()
    {
        // The last instance would start at 17:00 local on the 10th, which is after UNTIL (14:58 UTC), so the 9th is the last.
        SameOccurrences("""
            BEGIN:VEVENT
            UID:until@example.com
            DTSTAMP:20200101T000000Z
            DTSTART;TZID=Europe/Berlin:20201004T170000
            DTEND;TZID=Europe/Berlin:20201004T180000
            RRULE:FREQ=DAILY;UNTIL=20201010T145800Z
            END:VEVENT
            """);
    }

    [Fact]
    public void YearlyByMonthKeepsTheRulesMonthNotTheStartsMonth()
    {
        IcsComponent cal = Cal("""
            BEGIN:VEVENT
            UID:ym@example.com
            DTSTAMP:20200101T000000Z
            DTSTART:20260217T090000Z
            DTEND:20260217T100000Z
            RRULE:FREQ=YEARLY;BYMONTH=3;COUNT=3
            END:VEVENT
            """);
        var (item, import, back, _) = RoundTrip(cal);
        Assert.True(import.Has("DTSTART"));   // the 17 February start is not an instance of the rule
        Assert.Contains("BYMONTH=3", Ev(back).Component.FindProperty("RRULE")!.Value, StringComparison.Ordinal);
        Assert.Equal(3u, OutlookAppointment.TryCreate(item)!.Recurrence!.Pattern.OccurrenceCount);
    }

    [Fact]
    public void YearlyByMonthDayWithoutMonthIsEveryMonthAndOnlyThatIsCarried()
    {
        SameOccurrences("""
            BEGIN:VEVENT
            UID:ymd@example.com
            DTSTAMP:20200101T000000Z
            DTSTART:20260128T110000Z
            DTEND:20260128T120000Z
            RRULE:FREQ=YEARLY;BYMONTHDAY=28;COUNT=30
            END:VEVENT
            """);
        // Every month of every second year cannot be held.
        IcsComponent two = Cal("""
            BEGIN:VEVENT
            UID:ymd2@example.com
            DTSTAMP:20200101T000000Z
            DTSTART:20260128T110000Z
            DTEND:20260128T120000Z
            RRULE:FREQ=YEARLY;INTERVAL=2;BYMONTHDAY=28
            END:VEVENT
            """);
        var (item, import, _, _) = RoundTrip(two);
        Assert.True(import.Has("RRULE"));
        Assert.Null(OutlookAppointment.TryCreate(item)!.Recurrence);
    }

    [Fact]
    public void DailyOnWeekdaysIsTheWeeklyTemplate()
    {
        IcsComponent cal = Cal("""
            BEGIN:VEVENT
            UID:dw@example.com
            DTSTAMP:20200101T000000Z
            DTSTART:20260105T090000Z
            DTEND:20260105T100000Z
            RRULE:FREQ=DAILY;BYDAY=MO,TU,WE,TH,FR;COUNT=20
            END:VEVENT
            """);
        var (item, import, back, _) = RoundTrip(cal);
        Assert.False(import.Has("RRULE"));
        Assert.Equal(1, OutlookAppointment.TryCreate(item)!.Recurrence!.Pattern.PatternType);
        Assert.Equal(Starts(cal), Starts(back));
    }

    [Fact]
    public void ByHourAndByMinuteGiveTheSeriesTimeOfDay()
    {
        IcsComponent cal = Cal("""
            BEGIN:VEVENT
            UID:bh@example.com
            DTSTAMP:20200101T000000Z
            DTSTART:20260105T093000Z
            DTEND:20260105T100000Z
            RRULE:FREQ=DAILY;COUNT=4;BYHOUR=11;BYMINUTE=15
            END:VEVENT
            """);
        var (item, import, back, _) = RoundTrip(cal);
        Assert.True(import.Has("DTSTART"));   // 09:30 is not 11:15
        AppointmentRecurrencePattern r = OutlookAppointment.TryCreate(item)!.Recurrence!;
        Assert.Equal(11u * 60 + 15, r.StartTimeOffset);
        Assert.All(Starts(back), s => Assert.EndsWith("T111500", s, StringComparison.Ordinal));
    }

    [Fact]
    public void SecondsAreDroppedFromASeriesAndSaid()
    {
        IcsComponent cal = Cal("""
            BEGIN:VEVENT
            UID:sec@example.com
            DTSTAMP:20200101T000000Z
            DTSTART:20260105T093015Z
            DTEND:20260105T100000Z
            RRULE:FREQ=DAILY;COUNT=3
            END:VEVENT
            """);
        var (_, import, back, _) = RoundTrip(cal);
        Assert.True(import.Has("DTSTART"));
        Assert.Equal(["20260105T093000", "20260106T093000", "20260107T093000"], Starts(back));
    }

    [Fact]
    public void ARdatePairedWithAnExdateIsAMovedInstance()
    {
        IcsComponent cal = Cal("""
            BEGIN:VEVENT
            UID:rd@example.com
            DTSTAMP:20200101T000000Z
            DTSTART:20260105T090000Z
            DTEND:20260105T100000Z
            RRULE:FREQ=DAILY;COUNT=6
            EXDATE:20260107T090000Z
            RDATE:20260107T140000Z
            END:VEVENT
            """);
        var (item, import, back, _) = RoundTrip(cal);
        Assert.False(import.Has("RDATE"));
        OutlookAppointment a = OutlookAppointment.TryCreate(item)!;
        Assert.Single(a.Exceptions);
        Assert.Equal(new DateTime(2026, 1, 7, 14, 0, 0), a.Exceptions[0].Start);
        Assert.Equal(new DateTime(2026, 1, 7, 9, 0, 0), a.Exceptions[0].OriginalStart);
        Assert.Equal(Starts(cal), Starts(back));
    }

    [Fact]
    public void ARdateThatMovesNothingIsReported()
    {
        IcsComponent cal = Cal("""
            BEGIN:VEVENT
            UID:rd2@example.com
            DTSTAMP:20200101T000000Z
            DTSTART:20260105T090000Z
            DTEND:20260105T100000Z
            RRULE:FREQ=DAILY;COUNT=6
            RDATE:20260120T090000Z
            END:VEVENT
            """);
        var (_, import, _, _) = RoundTrip(cal);
        Assert.Contains(import, e => e.Code == "RdateWithoutException");
    }

    [Fact]
    public void ASeriesAcrossAChangeOfTheZonesRulesKeepsItsInstants()
    {
        // The United States moved daylight saving time in 2007; one rule for the whole series would be an hour out in some weeks.
        SameOccurrences("""
            BEGIN:VEVENT
            UID:ny@example.com
            DTSTAMP:20200101T000000Z
            DTSTART;TZID=America/New_York:20050103T090000
            DTEND;TZID=America/New_York:20050103T100000
            RRULE:FREQ=WEEKLY;BYDAY=MO;UNTIL=20121231T000000Z
            END:VEVENT
            """);
        var (item, _, _, _) = RoundTrip(Cal("""
            BEGIN:VEVENT
            UID:ny@example.com
            DTSTAMP:20200101T000000Z
            DTSTART;TZID=America/New_York:20050103T090000
            DTEND;TZID=America/New_York:20050103T100000
            RRULE:FREQ=WEEKLY;BYDAY=MO;UNTIL=20121231T000000Z
            END:VEVENT
            """));
        Assert.True(OutlookAppointment.TryCreate(item)!.TimeZone!.Definition!.Rules.Count >= 2);
    }

    [Fact]
    public void AReplyIsSentByTheAttendeeToTheOrganizer()
    {
        IcsComponent cal = Cal("""
            BEGIN:VEVENT
            UID:reply@example.com
            DTSTAMP:20200101T000000Z
            DTSTART:20260105T090000Z
            DTEND:20260105T100000Z
            ORGANIZER;CN=Ann:mailto:ann@example.com
            ATTENDEE;CN=Bob;PARTSTAT=ACCEPTED:mailto:bob@example.com
            END:VEVENT
            """, "METHOD:REPLY\r\n");
        var (item, import, back, _) = RoundTrip(cal);
        OutlookAppointment a = OutlookAppointment.TryCreate(item)!;
        Assert.Equal(OutlookAppointmentKind.MeetingResponseAccepted, a.Kind);
        Assert.Equal("bob@example.com", a.Organizer!.Value.Address);
        Assert.Equal("ann@example.com", Assert.Single(a.Attendees).Address);
        Assert.Equal("mailto:ann@example.com", Ev(back).Organizer!.Address);
        Assert.Equal("mailto:bob@example.com", Assert.Single(Ev(back).Attendees).Address);
        Assert.Equal("ACCEPTED", Ev(back).Attendees[0].ParticipationStatus);
        Assert.DoesNotContain(import, e => e.Name.StartsWith("ATTENDEE", StringComparison.Ordinal) || e.Name == "ORGANIZER");
    }

    [Fact]
    public void ACounterIsATentativeResponseWithTheCounterProposalFlag()
    {
        IcsComponent cal = Cal("""
            BEGIN:VEVENT
            UID:counter@example.com
            DTSTAMP:20200101T000000Z
            DTSTART:20260105T110000Z
            DTEND:20260105T120000Z
            ORGANIZER;CN=Ann:mailto:ann@example.com
            ATTENDEE;CN=Bob;PARTSTAT=TENTATIVE:mailto:bob@example.com
            END:VEVENT
            """, "METHOD:COUNTER\r\n");
        var (item, import, back, _) = RoundTrip(cal);
        Assert.Equal(OutlookAppointmentKind.MeetingResponseTentative, OutlookAppointment.TryCreate(item)!.Kind);
        Assert.False(import.Has("METHOD"));
        Assert.Equal("COUNTER", back.MethodText);
    }

    [Fact]
    public void AttendeeParametersOutlookCannotHoldAreReported()
    {
        IcsComponent cal = Cal("""
            BEGIN:VEVENT
            UID:att@example.com
            DTSTAMP:20200101T000000Z
            DTSTART:20260105T090000Z
            DTEND:20260105T100000Z
            STATUS:CONFIRMED
            ORGANIZER;CN=Ann:mailto:ann@example.com
            ATTENDEE;CN=Bob;ROLE=CHAIR;RSVP=TRUE:mailto:bob@example.com
            ATTENDEE;CN=Team;CUTYPE=GROUP;PARTSTAT=DELEGATED:mailto:team@example.com
            END:VEVENT
            """);
        var (_, import, back, _) = RoundTrip(cal);
        Assert.Contains(import, e => e.Code == "AttendeeRoleNotCarried");
        Assert.Contains(import, e => e.Code == "AttendeeTypeApproximated");
        Assert.Contains(import, e => e.Code == "PartstatNotCarried");
        Assert.Contains(import, e => e.Code == "RsvpIsPerMessage");
        Assert.Contains(import, e => e.Code == "StatusNotExported");
        Assert.Null(Ev(back).Status);
    }

    [Fact]
    public void ADayAShorterMonthLacksIsReported()
    {
        IcsComponent cal = Cal("""
            BEGIN:VEVENT
            UID:short@example.com
            DTSTAMP:20200101T000000Z
            DTSTART:20261031T090000Z
            DTEND:20261031T100000Z
            RRULE:FREQ=MONTHLY;COUNT=4
            END:VEVENT
            """);
        var (_, import, _, _) = RoundTrip(cal);
        Assert.Contains(import, e => e.Code == nameof(CalendaringDefectKind.AmbiguousShortMonth));
    }

    [Fact]
    public void RulesOutlookCannotExpressAreReportedWithTheirReason()
    {
        foreach ((string rule, string fragment) in new[]
        {
            ("FREQ=HOURLY;COUNT=5", "within a day"),
            ("FREQ=WEEKLY;BYSETPOS=1;BYDAY=MO,TU", "weekly"),
            ("FREQ=MONTHLY;BYMONTH=3,6", "BYMONTH"),
            ("FREQ=YEARLY;BYWEEKNO=20", "BYWEEKNO"),
            ("FREQ=YEARLY;BYDAY=20MO", "whole year"),
            ("FREQ=DAILY;BYHOUR=9,15", "several"),
        })
        {
            IcsComponent cal = Cal($"BEGIN:VEVENT\nUID:no@example.com\nDTSTAMP:20200101T000000Z\nDTSTART:20260105T090000Z\nDTEND:20260105T100000Z\nRRULE:{rule}\nEND:VEVENT");
            var (item, import, back, _) = RoundTrip(cal);
            LossEntry e = Assert.Single(import, x => x.Code == "RecurrenceNotMapped");
            Assert.Contains(fragment, e.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.Null(OutlookAppointment.TryCreate(item)!.Recurrence);
            Assert.Null(Ev(back).Component.FindProperty("RRULE"));
        }
    }

    [Fact]
    public void ADuplicateUidWithoutARecurrenceIdIsReported()
    {
        IcsComponent cal = Cal("""
            BEGIN:VEVENT
            UID:dup@example.com
            DTSTAMP:20200101T000000Z
            DTSTART:20260105T090000Z
            END:VEVENT
            BEGIN:VEVENT
            UID:dup@example.com
            DTSTAMP:20200101T000000Z
            DTSTART:20260106T090000Z
            END:VEVENT
            """);
        Assert.Contains(IcsToMapiConverter.ConvertAll(cal)[0].Report, e => e.Code == "DuplicateUidNotConverted");
    }

    [Fact]
    public void EveryCodeTheItemConvertersCanReportIsCatalogued()
    {
        // The loss codes are written at their call sites; each one is listed in docs/conversion.md.
        string root = Root();
        string notes = File.ReadAllText(Path.Combine(root, "docs", "conversion.md")) + File.ReadAllText(Path.Combine(root, "docs", "calendar-items.md"));
        System.Text.RegularExpressions.Regex call = new(@"report\.Add\(\s*""(?:ics|mapi)"",[^;]*?LossKind\.\w+,\s*(?:""(\w+)""|nameof\(CalendaringDefectKind\.(\w+)\))", System.Text.RegularExpressions.RegexOptions.Singleline);
        HashSet<string> codes = [];
        foreach (string file in new[] { "IcsToMapiConverter.cs", "MapiToIcsConverter.cs" })
        {
            foreach (System.Text.RegularExpressions.Match m in call.Matches(File.ReadAllText(Path.Combine(root, "src", "OneMail", "Conversion", "OxcIcal", file))))
            {
                codes.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
            }
        }

        Assert.True(codes.Count > 40, $"only {codes.Count} codes found");
        string[] missing = [.. codes.Where(c => !notes.Contains(c, StringComparison.Ordinal))];
        Assert.True(missing.Length == 0, "undocumented codes: " + string.Join(", ", missing));
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", ".."));
}
