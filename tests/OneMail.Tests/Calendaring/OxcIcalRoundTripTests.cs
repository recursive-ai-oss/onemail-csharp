using System.Text;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.Ics;
using RecursiveAI.OneMail.Calendaring;
using RecursiveAI.OneMail.Conversion;
using RecursiveAI.OneMail.Conversion.OxcIcal;
using RecursiveAI.OneMail.Mapi;
using static RecursiveAI.OneMail.Tests.Calendaring.CalendarFixtures;

namespace RecursiveAI.OneMail.Tests.Calendaring;

public class OxcIcalRoundTripTests
{
    private const string Weekly = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Example//Test//EN
        METHOD:PUBLISH
        BEGIN:VTIMEZONE
        TZID:Pacific Standard Time
        BEGIN:STANDARD
        DTSTART:16011104T020000
        TZOFFSETFROM:-0700
        TZOFFSETTO:-0800
        RRULE:FREQ=YEARLY;BYDAY=1SU;BYMONTH=11
        END:STANDARD
        BEGIN:DAYLIGHT
        DTSTART:16010311T020000
        TZOFFSETFROM:-0800
        TZOFFSETTO:-0700
        RRULE:FREQ=YEARLY;BYDAY=2SU;BYMONTH=3
        END:DAYLIGHT
        END:VTIMEZONE
        BEGIN:VEVENT
        UID:weekly-1@example.com
        DTSTAMP:20070301T000000Z
        DTSTART;TZID=Pacific Standard Time:20070326T100000
        DTEND;TZID=Pacific Standard Time:20070326T103000
        RRULE:FREQ=WEEKLY;COUNT=12;BYDAY=MO,TH,FR;WKST=SU
        EXDATE;TZID=Pacific Standard Time:20070402T100000
        SUMMARY:Standup
        LOCATION:Room 4
        DESCRIPTION:Daily sync
        ORGANIZER;CN=Ann:mailto:ann@example.com
        ATTENDEE;CN=Bob;ROLE=REQ-PARTICIPANT;PARTSTAT=ACCEPTED;RSVP=TRUE:mailto:bob@example.com
        ATTENDEE;CN=Cy;ROLE=OPT-PARTICIPANT;PARTSTAT=NEEDS-ACTION;RSVP=TRUE:mailto:cy@example.com
        PRIORITY:5
        CLASS:PRIVATE
        TRANSP:OPAQUE
        SEQUENCE:2
        CATEGORIES:Work,Team
        X-CUSTOM:kept-in-the-report
        BEGIN:VALARM
        TRIGGER:-PT15M
        ACTION:DISPLAY
        DESCRIPTION:Reminder
        END:VALARM
        END:VEVENT
        BEGIN:VEVENT
        UID:weekly-1@example.com
        DTSTAMP:20070301T000000Z
        RECURRENCE-ID;TZID=Pacific Standard Time:20070409T100000
        DTSTART;TZID=Pacific Standard Time:20070410T110000
        DTEND;TZID=Pacific Standard Time:20070410T113000
        SUMMARY:Standup (moved)
        LOCATION:Room 9
        END:VEVENT
        END:VCALENDAR
        """;

    private static IcsComponent Parse(string text) =>
        IcsDocument.Calendars(ContentDocument.Parse(Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\r\n") + "\r\n"))).First();

    [Fact]
    public void IcsToItemReadsBackAsAnAppointment()
    {
        ConversionResult<MapiMessage> r = new IcsToMapiConverter().Convert(Parse(Weekly));
        OutlookAppointment a = OutlookAppointment.TryCreate(r.Value)!;
        Assert.Equal(OutlookAppointmentKind.Appointment, a.Kind);
        Assert.Equal("Standup", a.Subject);
        Assert.Equal("Room 4", a.Location);
        Assert.Equal(OutlookBusyStatus.Busy, a.BusyStatus);
        Assert.Equal(2, a.Sequence);
        Assert.Equal(2, a.Sensitivity);
        Assert.Equal(1, a.Importance);
        Assert.True(a.ReminderSet);
        Assert.Equal(15, a.ReminderMinutes);
        Assert.Equal("Pacific Standard Time", a.TimeZone!.Id);
        AppointmentRecurrencePattern rec = a.Recurrence!;
        Assert.Equal(0x32u, rec.Pattern.WeekdayMask);
        Assert.Equal(12u, rec.Pattern.OccurrenceCount);
        Assert.Equal(600u, rec.StartTimeOffset);
        Assert.Equal(630u, rec.EndTimeOffset);
        Assert.Equal(new DateTime(2007, 3, 26, 10, 0, 0), a.StartLocal);
        Assert.Equal(new DateTime(2007, 3, 26, 17, 0, 0), a.Start!.Value.UtcDateTime);   // PDT is -7
        Assert.Single(a.DeletedInstances);
        OutlookModifiedInstance ex = Assert.Single(a.Exceptions);
        Assert.Equal("Standup (moved)", ex.Info.Subject);
        Assert.Equal("Standup (moved)", ex.Message!.Subject);
        Assert.Equal(new DateTime(2007, 4, 10, 11, 0, 0), ex.Start);
        Assert.Equal(2, a.Attendees.Count(x => !x.IsOrganizer));   // Bob and Cy; the organizer has a row of its own
        Assert.Contains(a.Attendees, x => x.Address == "cy@example.com" && x.Role == OutlookAttendeeRole.Optional);
        Assert.Contains(a.Attendees, x => x.Address == "bob@example.com" && x.Response == OutlookResponse.Accepted);
        Assert.Equal("ann@example.com", a.Organizer!.Value.Address);
        Assert.Contains(r.Report, e => e.Name == "X-CUSTOM" && e.Kind == LossKind.Dropped);
    }

    [Fact]
    public void IcsToItemToIcsKeepsTheMappedFieldsAndReportsTheRest()
    {
        IcsComponent src = Parse(Weekly);
        ConversionResult<MapiMessage> toItem = new IcsToMapiConverter().Convert(src);
        IcsComponent back = ToIcs(toItem.Value, out LossReport report);
        List<IcsComponent> a = src.Events.ToList();
        List<IcsComponent> b = back.Events.ToList();
        Assert.Equal(a.Count, b.Count);
        IcsComponent s = a[0], t = b[0];
        foreach (string f in new[] { "SUMMARY", "LOCATION", "DESCRIPTION", "UID", "PRIORITY", "CLASS", "TRANSP", "SEQUENCE" })
        {
            Assert.Equal(s.GetText(f), t.GetText(f));
        }

        Assert.Equal(s.Start, t.Start);
        Assert.Equal(s.End, t.End);
        RecurrenceRule x = s.RecurrenceRules[0], y = t.RecurrenceRules[0];
        Assert.Equal(x.Frequency, y.Frequency);
        Assert.Equal(x.Interval, y.Interval);
        Assert.Equal(x.Count, y.Count);
        Assert.Equal(x.ByDay, y.ByDay);
        Assert.Equal(x.WeekStart, y.WeekStart);
        Assert.Equal(s.ExceptionDates.Select(d => d.Value), t.ExceptionDates.Select(d => d.Value));
        Assert.Equal(s.Categories, t.Categories);
        Assert.Equal(s.Organizer!.Email, t.Organizer!.Email);
        Assert.Equal(s.Attendees.Select(q => (q.Email, q.ParticipationStatus)).OrderBy(q => q.Email), t.Attendees.Select(q => (q.Email, q.ParticipationStatus)).OrderBy(q => q.Email));
        Assert.Equal("OPT-PARTICIPANT", t.Attendees.Single(q => q.Email == "cy@example.com").Role);
        Assert.Equal(a[1].Summary, b[1].Summary);
        Assert.Equal(a[1].RecurrenceId, b[1].RecurrenceId);
        Assert.Equal(a[1].Start, b[1].Start);
        Assert.Equal(a[1].Location, b[1].Location);
        Assert.Equal(15, (int)Math.Abs(t.Alarms.First().Trigger!.Value.Offset!.Value.ToTimeSpan().TotalMinutes));

        // Nothing silent: every property name of the source event is in the output or in the loss report of one of the two legs.
        HashSet<string> outNames = t.Component.Properties.Select(p => p.Name.ToUpperInvariant()).ToHashSet();
        foreach (string name in s.Component.Properties.Select(p => p.Name.ToUpperInvariant()).Distinct())
        {
            bool reported = toItem.Report.Any(e => e.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase));
            Assert.True(outNames.Contains(name) || reported || name == "DTSTAMP", $"{name} vanished without a report");
        }

        Assert.DoesNotContain(report, e => e.IsContentLoss && e.Code != "NoIcalMapping");
    }

    [Theory]
    [MemberData(nameof(OutlookOriginCases))]
    public void ItemToIcsToItemKeepsTheMappedProperties(string id)
    {
        (RecurrencePattern p, uint s, uint e) = CaseFor(id);
        foreach ((TzRule rule, string name) in new[] { (UtcRule, "UTC"), (Pacific, "Pacific Standard Time"), (Sydney, "AUS Eastern Standard Time") })
        {
            MapiMessage m = Series(p, s, e, rule, name, id);
            IcsComponent ics = ToIcs(m, out _);
            MapiMessage back = new IcsToMapiConverter().Convert(ics).Value;
            OutlookAppointment a = OutlookAppointment.TryCreate(m)!, b = OutlookAppointment.TryCreate(back)!;
            Assert.Equal(a.Subject, b.Subject);
            Assert.Equal(a.Start, b.Start);
            Assert.Equal(a.End, b.End);
            Assert.Equal(a.GlobalObjectId, b.GlobalObjectId);
            Assert.Equal(m.Properties.Get(OutlookNames.TimeZoneStruct, MapiType.Binary)!.GetBytes().ToArray(), back.Properties.Get(OutlookNames.TimeZoneStruct, MapiType.Binary)!.GetBytes().ToArray());
            RecurrencePattern x = a.Recurrence!.Pattern, y = b.Recurrence!.Pattern;
            Assert.Equal((x.RecurFrequency, x.PatternType, x.Period, x.WeekdayMask, x.DayOfMonth, x.Nth, x.EndType, x.OccurrenceCount, x.StartDate, x.EndDate, x.FirstDateTime, x.FirstDayOfWeek),
                (y.RecurFrequency, y.PatternType, y.Period, y.WeekdayMask, y.DayOfMonth, y.Nth, y.EndType, y.OccurrenceCount, y.StartDate, y.EndDate, y.FirstDateTime, y.FirstDayOfWeek));
            Assert.Equal(a.Recurrence.StartTimeOffset, b.Recurrence!.StartTimeOffset);
            Assert.Equal(a.Recurrence.EndTimeOffset, b.Recurrence.EndTimeOffset);
        }
    }

    public static TheoryData<string> OutlookOriginCases() => new()
    {
        "daily-1-never", "daily-3-count", "daily-2-until", "weekly-mwf-count", "weekly-2w-sun-mon-wkst-mon", "weekly-weekdays-never",
        "monthly-15-never", "monthly-31-until", "monthly-3m-5-count", "monthly-2nd-tue", "monthly-last-weekend-until", "yearly-jun22-count", "yearly-3rd-thu-nov",
    };

    private static (RecurrencePattern, uint, uint) CaseFor(string id) =>
        id switch
        {
            "daily-1-never" => (Daily(D("2007-03-01T00:00"), 1), 600, 630),
            "daily-3-count" => (Daily(D("2007-03-01T00:00"), 3, RecurrencePattern.EndAfterCount, 7), 600, 630),
            "daily-2-until" => (Daily(D("2007-03-01T00:00"), 2, RecurrencePattern.EndAfterDate, until: D("2007-04-01T00:00")), 600, 630),
            "weekly-mwf-count" => (Weekly(D("2007-03-26T00:00"), 1, 0x32, 0, RecurrencePattern.EndAfterCount, 12), 600, 630),
            "weekly-2w-sun-mon-wkst-mon" => (Weekly(D("2007-03-04T00:00"), 2, 3, 1, RecurrencePattern.EndAfterCount, 9), 600, 630),
            "weekly-weekdays-never" => (Weekly(D("2007-03-05T00:00"), 1, 0x3E), 540, 1020),
            "monthly-15-never" => (Monthly(D("2007-01-15T00:00"), 1, 15), 600, 630),
            "monthly-31-until" => (Monthly(D("2007-01-31T00:00"), 1, 31, RecurrencePattern.EndAfterDate, until: D("2008-12-31T00:00")), 600, 630),
            "monthly-3m-5-count" => (Monthly(D("2007-02-05T00:00"), 3, 5, RecurrencePattern.EndAfterCount, 8), 600, 630),
            "monthly-2nd-tue" => (MonthlyNth(D("2007-01-09T00:00"), 1, 4, 2), 600, 630),
            "monthly-last-weekend-until" => (MonthlyNth(D("2007-01-28T00:00"), 1, 0x41, 5, RecurrencePattern.EndAfterDate, until: D("2008-06-30T00:00")), 600, 630),
            "yearly-jun22-count" => (Yearly(D("2007-06-22T00:00"), 22, RecurrencePattern.EndAfterCount, 4), 600, 630),
            "yearly-3rd-thu-nov" => (YearlyNth(D("2007-11-15T00:00"), 16, 3), 600, 630),
            _ => throw new ArgumentException(id),
        };

    [Fact]
    public void MeetingMethodsMapToMessageClasses()
    {
        foreach ((string method, string stat, string cls) in new[] { ("REQUEST", "NEEDS-ACTION", "IPM.Schedule.Meeting.Request"), ("CANCEL", "NEEDS-ACTION", "IPM.Schedule.Meeting.Canceled"), ("REPLY", "ACCEPTED", "IPM.Schedule.Meeting.Resp.Pos"), ("REPLY", "TENTATIVE", "IPM.Schedule.Meeting.Resp.Tent"), ("REPLY", "DECLINED", "IPM.Schedule.Meeting.Resp.Neg"), ("PUBLISH", "", "IPM.Appointment") })
        {
            string text = $"""
                BEGIN:VCALENDAR
                VERSION:2.0
                PRODID:-//x//EN
                METHOD:{method}
                BEGIN:VEVENT
                UID:m-1
                DTSTAMP:20070101T000000Z
                DTSTART:20070601T160000Z
                DTEND:20070601T170000Z
                SUMMARY:Meeting
                ORGANIZER:mailto:ann@example.com
                ATTENDEE;PARTSTAT={stat}:mailto:bob@example.com
                END:VEVENT
                END:VCALENDAR
                """;
            MapiMessage m = new IcsToMapiConverter().Convert(Parse(text)).Value;
            Assert.Equal(cls, m.MessageClass);
            OutlookAppointment a = OutlookAppointment.TryCreate(m)!;
            IcsComponent back = ToIcs(m, out _);
            Assert.Equal(method, back.MethodText);
            Assert.Equal(new DateTime(2007, 6, 1, 16, 0, 0), a.Start!.Value.UtcDateTime);
        }
    }

    [Fact]
    public void VTodoRoundTrips()
    {
        const string text = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//x//EN
            BEGIN:VTODO
            UID:t-1
            DTSTAMP:20070101T000000Z
            SUMMARY:File taxes
            DTSTART;VALUE=DATE:20070401
            DUE;VALUE=DATE:20070415
            STATUS:IN-PROCESS
            PERCENT-COMPLETE:40
            PRIORITY:1
            X-FOO:bar
            END:VTODO
            END:VCALENDAR
            """;
        ConversionResult<MapiMessage> r = new IcsToMapiConverter().Convert(Parse(text));
        OutlookTask t = OutlookTask.TryCreate(r.Value)!;
        Assert.Equal(OutlookTaskStatus.InProgress, t.Status);
        Assert.Equal(0.4, t.PercentComplete);
        Assert.Equal(new DateTime(2007, 4, 15), t.DueDate);
        Assert.Equal(2, t.Importance);
        Assert.Contains(r.Report, e => e.Name == "X-FOO");
        IcsComponent todo = ToIcs(r.Value, out _).Todos.Single();
        Assert.Equal("File taxes", todo.Summary);
        Assert.Equal(40, todo.PercentComplete);
        Assert.Equal(new DateTime(2007, 4, 15), todo.Due!.Value.Value);
        Assert.Equal(1, todo.Priority);
    }

    [Fact]
    public void AllDayEventsStayDates()
    {
        const string text = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//x//EN
            BEGIN:VEVENT
            UID:d-1
            DTSTAMP:20070101T000000Z
            DTSTART;VALUE=DATE:20070601
            DTEND;VALUE=DATE:20070603
            SUMMARY:Offsite
            END:VEVENT
            END:VCALENDAR
            """;
        ConversionResult<MapiMessage> r = new IcsToMapiConverter().Convert(Parse(text));
        OutlookAppointment a = OutlookAppointment.TryCreate(r.Value)!;
        Assert.True(a.IsAllDay);
        IcsComponent ev = ToIcs(r.Value, out _).Events.Single();
        Assert.True(ev.Start!.Value.IsDate);
        Assert.Equal(new DateTime(2007, 6, 1), ev.Start.Value.Value);
        Assert.Equal(new DateTime(2007, 6, 3), ev.End!.Value.Value);
        Assert.Contains(r.Report, e => e.Code == "FloatingTimeAssumedUtc");
    }

    [Fact]
    public void RulesWithNoOutlookFormAreReportedNotGuessed()
    {
        const string text = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//x//EN
            BEGIN:VEVENT
            UID:r-1
            DTSTAMP:20070101T000000Z
            DTSTART:20070601T160000Z
            DTEND:20070601T170000Z
            RRULE:FREQ=MONTHLY;BYMONTHDAY=1,15
            SUMMARY:Twice a month
            END:VEVENT
            END:VCALENDAR
            """;
        ConversionResult<MapiMessage> r = new IcsToMapiConverter().Convert(Parse(text));
        Assert.Contains(r.Report, e => e.Code == "RecurrenceNotMapped" && e.Kind == LossKind.Dropped);
        Assert.Null(OutlookAppointment.TryCreate(r.Value)!.Recurrence);
    }
}
