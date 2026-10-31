using System.Text;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.Ics;
using RecursiveAI.OneMail.Calendar.Recurrence;
using RecursiveAI.OneMail.Calendaring;
using RecursiveAI.OneMail.Conversion;
using RecursiveAI.OneMail.Conversion.OxcIcal;
using RecursiveAI.OneMail.Mapi;
using static RecursiveAI.OneMail.Tests.Calendaring.CalendarFixtures;

namespace RecursiveAI.OneMail.Tests.Calendaring;

public class OutlookCalendarTests
{
    private static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

    // MS-OXOCAL 4.1.1.1: every Monday, Thursday and Friday 10:00 to 10:30, 12 occurrences from 2007-03-26 (fields from the example's table).
    private const string WeeklyBlob = "0430 0430 0B20 0100 0000 C0210000 01000000 00000000 32000000 22200000 0C000000 00000000 00000000 00000000 80 20 BC 0C 20 AD BC 0C 06300000 09300000 58020000 76020000 0000 00000000 00000000";

    // MS-OXOCAL 4.1.1.3: every three days from 2011-04-07 to 2011-05-04 08:00-08:30; the 19th and 22nd of April deleted.
    private const string DailyBlob = "0430 0430 0A20 0000 0000 A0050000 E0100000 00000000 21200000 0C000000 00000000 02000000 A0C1DC0C 80D2DC0C 00000000 207EDC0C 0016DD0C 06300000 09300000 E0010000 FE010000 0000 00000000 00000000";

    // MS-OXPROPS 2.19: PidLidAllAttendeesString is LID 0x8238 (0x823A is not a defined property of the set).
    [Fact]
    public void AllAttendeesStringUsesTheLidOfMsOxprops()
    {
        Assert.Equal(0x8238u, (uint)MapiNames.AllAttendeesString.Lid);
        Assert.Equal(MapiNames.AllAttendeesString, OutlookNames.AllAttendeesString);
    }

    [Fact]
    public void WeeklyExampleOfTheSpecReadsAndWritesBack()
    {
        byte[] blob = Hex(WeeklyBlob);
        List<CalendaringDefect> defects = [];
        Assert.True(AppointmentRecurrencePattern.TryParse(blob, out AppointmentRecurrencePattern? r, defects));
        Assert.Empty(defects);
        RecurrencePattern p = r!.Pattern;
        Assert.Equal(0x200B, p.RecurFrequency);
        Assert.Equal(1, p.PatternType);
        Assert.Equal(8640u, p.FirstDateTime);
        Assert.Equal(0x32u, p.WeekdayMask);   // Monday + Thursday + Friday
        Assert.Equal(12u, p.OccurrenceCount);
        Assert.Equal(600u, r.StartTimeOffset);
        Assert.Equal(630u, r.EndTimeOffset);
        Assert.Equal(blob, r.ToArray());
        List<DateTime> days = OutlookRecurrence.Days(p).ToList();
        Assert.Equal(12, days.Count);
        Assert.Equal(D("2007-03-26T00:00"), days[0]);
        Assert.Equal(OutlookMinutes.ToDateTime(p.EndDate), days[^1]);   // 2007-04-20
        Assert.Equal(new DateTime(2007, 4, 20), days[^1]);
        Assert.Equal(p.FirstDateTime, OutlookRecurrence.FirstDateTimeFor(p, days[0]));
    }

    [Fact]
    public void DailyExampleOfTheSpecWithDeletedInstances()
    {
        byte[] blob = Hex(DailyBlob);
        List<CalendaringDefect> defects = [];
        Assert.True(AppointmentRecurrencePattern.TryParse(blob, out AppointmentRecurrencePattern? r, defects));
        Assert.Empty(defects);
        Assert.Equal(1440u, r!.Pattern.FirstDateTime);
        Assert.Equal(2, r.Pattern.DeletedInstanceDates.Count);
        Assert.Equal(blob, r.ToArray());
        Assert.Equal(1440u, OutlookRecurrence.FirstDateTimeFor(r.Pattern, new DateTime(2011, 4, 7)));
        List<DateTime> days = OutlookRecurrence.Days(r.Pattern).ToList();
        Assert.Equal(new DateTime(2011, 4, 7), days[0]);
        Assert.Contains(new DateTime(2011, 4, 19), days);
        Assert.Equal(new DateTime(2011, 5, 4), days[^1]);
        MapiMessage m = Series(r.Pattern, r.StartTimeOffset, r.EndTimeOffset, UtcRule, "UTC");
        IcsComponent cal = ToIcs(m, out LossReport rep);
        List<DateTime> occ = Occurrences(cal, new DateTime(2011, 1, 1), new DateTime(2011, 12, 31));
        Assert.DoesNotContain(new DateTime(2011, 4, 19, 8, 0, 0), occ);
        Assert.DoesNotContain(new DateTime(2011, 4, 22, 8, 0, 0), occ);
        Assert.Equal(days.Count - 2, occ.Count);
        Assert.False(rep.HasContentLoss, string.Join("; ", rep.Where(e => e.IsContentLoss).Select(e => e.Name + " " + e.Code)));
    }

    [Fact]
    public void TruncatedAndHostileBlobsGiveDefectsNotExceptions()
    {
        byte[] blob = Hex(DailyBlob);
        for (int cut = 0; cut < blob.Length; cut++)
        {
            List<CalendaringDefect> d = [];
            bool ok = AppointmentRecurrencePattern.TryParse(blob.AsSpan(0, cut), out _, d);
            Assert.True(cut >= 22 ? ok : !ok && d.Count > 0);
        }

        // A deleted-instance count that promises far more dates than the bytes hold.
        byte[] big = (byte[])blob.Clone();
        big[34] = 0xFF;
        big[35] = 0xFF;
        big[36] = 0xFF;
        big[37] = 0x7F;
        List<CalendaringDefect> defects = [];
        Assert.True(AppointmentRecurrencePattern.TryParse(big, out AppointmentRecurrencePattern? r, defects));
        Assert.Contains(defects, x => x.Kind == CalendaringDefectKind.CountOutOfRange);
        Assert.NotNull(r);
    }

    [Fact]
    public void TimeZoneStructsRoundTripAndConvert()
    {
        foreach (TzRule rule in new[] { Pacific, Sydney, UtcRule })
        {
            byte[] b = TimeZoneStruct.ToArray(rule);
            Assert.Equal(48, b.Length);
            Assert.True(TimeZoneStruct.TryParse(b, out TzRule? back));
            Assert.True(rule.SameAs(back!));
        }

        TimeZoneDefinition def = new("Pacific Standard Time", [Pacific with { Flags = 2, Year = 2007 }, Pacific with { Flags = 0, Year = 1601 }]);
        Assert.True(TimeZoneDefinition.TryParse(def.ToArray(), out TimeZoneDefinition? d2));
        Assert.Equal("Pacific Standard Time", d2!.KeyName);
        Assert.Equal(2, d2.Rules.Count);
        Assert.Equal(def.ToArray(), d2.ToArray());
    }

    [Theory]
    [InlineData("2007-01-15T12:00", -8)]
    [InlineData("2007-07-15T12:00", -7)]
    [InlineData("2007-03-11T09:59", -8)]   // the instant before the spring change
    [InlineData("2007-03-11T10:00", -7)]
    [InlineData("2007-11-04T08:59", -7)]
    [InlineData("2007-11-04T09:00", -8)]
    public void PacificRuleOffsets(string utc, int hours)
    {
        OutlookTimeZone z = ZoneOf(Pacific, "Pacific Standard Time");
        Assert.Equal(TimeSpan.FromHours(hours), z.Zone.GetUtcOffset(DateTime.SpecifyKind(D(utc), DateTimeKind.Utc)));
    }

    [Theory]
    [InlineData("2007-01-15T12:00", 11)]   // southern summer
    [InlineData("2007-07-15T12:00", 10)]
    [InlineData("2007-03-31T15:59", 11)]
    [InlineData("2007-03-31T16:00", 10)]
    public void SydneyRuleOffsets(string utc, int hours)
    {
        OutlookTimeZone z = ZoneOf(Sydney, "AUS Eastern Standard Time");
        Assert.Equal(TimeSpan.FromHours(hours), z.Zone.GetUtcOffset(DateTime.SpecifyKind(D(utc), DateTimeKind.Utc)));
    }

    [Fact]
    public void ZoneWithoutDaylightSavingTime()
    {
        OutlookTimeZone z = ZoneOf(new TzRule(3, 0, -330, 0, 0, default, default), "India Standard Time");
        Assert.Equal(TimeSpan.FromMinutes(330), z.Zone.GetUtcOffset(new DateTime(2007, 7, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    private static OutlookTimeZone ZoneOf(TzRule rule, string name)
    {
        var props = new IcsToMapiConverter.Props();
        props.Str(MapiPid.MessageClass, "IPM.Appointment");
        props.NBin(OutlookNames.TimeZoneDefinitionStartDisplay, new TimeZoneDefinition(name, [rule with { Flags = 2 }]).ToArray());
        return OutlookAppointment.TryCreate(props.Message)!.TimeZone!;
    }

    [Fact]
    public void AppointmentFieldsAreTyped()
    {
        var p = new IcsToMapiConverter.Props();
        p.Str(MapiPid.MessageClass, "IPM.Appointment");
        p.Str(MapiPid.Subject, "Review");
        p.NStr(OutlookNames.Location, "Room 4");
        p.NTime(OutlookNames.StartWhole, new DateTime(2007, 6, 1, 16, 0, 0));
        p.NTime(OutlookNames.EndWhole, new DateTime(2007, 6, 1, 17, 0, 0));
        p.NInt(OutlookNames.BusyStatus, 3);
        p.NInt(OutlookNames.AppointmentSequence, 2);
        p.NBool(OutlookNames.ReminderSet, true);
        p.NInt(OutlookNames.ReminderDelta, 30);
        p.NInt(OutlookNames.StateFlags, 1);
        p.NBin(OutlookNames.TimeZoneDefinitionStartDisplay, new TimeZoneDefinition("Pacific Standard Time", [Pacific with { Flags = 2 }]).ToArray());
        OutlookAppointment a = OutlookAppointment.TryCreate(p.Message)!;
        Assert.Equal(OutlookAppointmentKind.Appointment, a.Kind);
        Assert.Equal("Review", a.Subject);
        Assert.Equal("Room 4", a.Location);
        Assert.Equal(OutlookBusyStatus.OutOfOffice, a.BusyStatus);
        Assert.Equal(2, a.Sequence);
        Assert.True(a.IsMeeting);
        Assert.Equal(new DateTime(2007, 6, 1, 9, 0, 0), a.StartLocal);
        Assert.Equal(30, a.ReminderMinutes);
        Assert.Null(OutlookAppointment.TryCreate(new MapiMessage()));
    }

    [Theory]
    [InlineData("IPM.Appointment", OutlookAppointmentKind.Appointment)]
    [InlineData("IPM.Appointment.Custom", OutlookAppointmentKind.Appointment)]
    [InlineData("IPM.Schedule.Meeting.Request", OutlookAppointmentKind.MeetingRequest)]
    [InlineData("IPM.Schedule.Meeting.Resp.Pos", OutlookAppointmentKind.MeetingResponseAccepted)]
    [InlineData("IPM.Schedule.Meeting.Resp.Tent", OutlookAppointmentKind.MeetingResponseTentative)]
    [InlineData("IPM.Schedule.Meeting.Resp.Neg", OutlookAppointmentKind.MeetingResponseDeclined)]
    [InlineData("IPM.Schedule.Meeting.Canceled", OutlookAppointmentKind.MeetingCancellation)]
    [InlineData("IPM.Schedule.Meeting.Notification.Forward", OutlookAppointmentKind.MeetingForwardNotification)]
    [InlineData("IPM.OLE.CLASS.{00061055-0000-0000-C000-000000000046}", OutlookAppointmentKind.Exception)]
    public void MessageClassesMapToKinds(string cls, OutlookAppointmentKind kind) => Assert.Equal(kind, OutlookAppointment.KindOf(cls));

    [Fact]
    public void OtherClassesAreNotCalendarItems()
    {
        Assert.Null(OutlookAppointment.KindOf("IPM.Note"));
        Assert.Null(OutlookAppointment.KindOf("IPM.AppointmentX"));
        Assert.Null(OutlookAppointment.KindOf(null));
    }

    // ---- the expansion check: the RRULE we write against the spec's own valid-day walk ----

    public static TheoryData<string> Cases => new()
    {
        "daily-1-never", "daily-3-count", "daily-2-until", "weekly-mwf-count", "weekly-2w-sun-mon-wkst-mon", "weekly-2w-sun-mon-wkst-sun", "weekly-weekdays-never",
        "monthly-15-never", "monthly-31-until", "monthly-3m-5-count", "monthly-2nd-tue", "monthly-last-weekend-until", "monthly-first-weekday-count",
        "monthly-last-day-4th-thu-2m", "yearly-jun22-never", "yearly-jun22-count", "yearly-3rd-thu-nov", "yearly-last-fri-feb-until",
    };

    private static (RecurrencePattern P, uint S, uint E) Case(string id)
    {
        const uint mon = 2, tue = 4, thu = 16, fri = 32, sat = 64, sun = 1, weekdays = 0x3E, weekend = 0x41;
        uint s = 600, e = 630;
        RecurrencePattern p = id switch
        {
            "daily-1-never" => Daily(D("2007-03-01T00:00"), 1),
            "daily-3-count" => Daily(D("2007-03-01T00:00"), 3, RecurrencePattern.EndAfterCount, 7),
            "daily-2-until" => Daily(D("2007-03-01T00:00"), 2, RecurrencePattern.EndAfterDate, until: D("2007-04-01T00:00")),
            "weekly-mwf-count" => Weekly(D("2007-03-26T00:00"), 1, mon | thu | fri, 0, RecurrencePattern.EndAfterCount, 12),
            "weekly-2w-sun-mon-wkst-mon" => Weekly(D("2007-03-04T00:00"), 2, sun | mon, 1, RecurrencePattern.EndAfterCount, 9),
            "weekly-2w-sun-mon-wkst-sun" => Weekly(D("2007-03-04T00:00"), 2, sun | mon, 0, RecurrencePattern.EndAfterCount, 9),
            "weekly-weekdays-never" => Weekly(D("2007-03-05T00:00"), 1, weekdays),
            "monthly-15-never" => Monthly(D("2007-01-15T00:00"), 1, 15),
            "monthly-31-until" => Monthly(D("2007-01-31T00:00"), 1, 31, RecurrencePattern.EndAfterDate, until: D("2008-12-31T00:00")),
            "monthly-3m-5-count" => Monthly(D("2007-02-05T00:00"), 3, 5, RecurrencePattern.EndAfterCount, 8),
            "monthly-2nd-tue" => MonthlyNth(D("2007-01-09T00:00"), 1, tue, 2),
            "monthly-last-weekend-until" => MonthlyNth(D("2007-01-28T00:00"), 1, weekend, 5, RecurrencePattern.EndAfterDate, until: D("2008-06-30T00:00")),
            "monthly-first-weekday-count" => MonthlyNth(D("2007-01-01T00:00"), 1, weekdays, 1, RecurrencePattern.EndAfterCount, 14),
            "monthly-last-day-4th-thu-2m" => MonthlyNth(D("2007-03-22T00:00"), 2, thu, 4),
            "yearly-jun22-never" => Yearly(D("2007-06-22T00:00"), 22),
            "yearly-jun22-count" => Yearly(D("2007-06-22T00:00"), 22, RecurrencePattern.EndAfterCount, 4),
            "yearly-3rd-thu-nov" => YearlyNth(D("2007-11-15T00:00"), thu, 3),
            "yearly-last-fri-feb-until" => YearlyNth(D("2007-02-23T00:00"), fri, 5, RecurrencePattern.EndAfterDate, until: D("2012-12-31T00:00")),
            _ => throw new ArgumentException(id),
        };
        _ = sat;
        return (p, s, e);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void RRuleExpansionEqualsTheSpecDayWalk(string id)
    {
        (RecurrencePattern p, uint s, uint e) = Case(id);
        foreach ((TzRule rule, string name) in new[] { (UtcRule, "UTC"), (Pacific, "Pacific Standard Time"), (Sydney, "AUS Eastern Standard Time") })
        {
            MapiMessage m = Series(p, s, e, rule, name, id);
            IcsComponent cal = ToIcs(m, out LossReport rep);
            ExportForOracles(m, id + "-" + name.Replace(' ', '_'));
            DateTime from = new(2007, 1, 1), to = new(2012, 12, 31, 23, 59, 59);
            List<DateTime> got = Occurrences(cal, from.AddDays(-2), to.AddDays(2)).Where(t => t >= from && t <= to).ToList();   // the window is UTC; the local range is cut afterwards
            List<DateTime> want = OutlookRecurrence.Days(p).Select(d => d.AddMinutes(s)).Where(t => t >= from && t <= to).ToList();
            Assert.Equal(want, got);
            Assert.False(rep.HasContentLoss, id + ": " + string.Join("; ", rep.Where(x => x.IsContentLoss).Select(x => x.Name + " " + x.Code)));
            // DTSTART and TZID are the series' own clock.
            Assert.Equal(want[0], cal.Events.First().Start!.Value.Value);
            if (name != "UTC")
            {
                Assert.Equal(name, cal.Events.First().Start!.Value.TzId);
            }
        }
    }

    // Setting ONEMAIL_OXOCAL_OUT writes each generated series as .ics so the recurrence oracles can expand the same files.
    private static void ExportForOracles(MapiMessage m, string name)
    {
        string? dir = Environment.GetEnvironmentVariable("ONEMAIL_OXOCAL_OUT");
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, name + ".ics"), new MapiToIcsConverter().Convert(m).Value.ToArray());
        }
    }

    // Ambiguity OC2: the first instance is not an allowed day, so the first allowed day is the series start.
    [Fact]
    public void AFirstInstanceOffThePatternStartsOnTheFirstAllowedDay()
    {
        const uint mon = 2;
        RecurrencePattern p = Weekly(D("2007-03-27T00:00"), 1, mon, 0, RecurrencePattern.EndAfterCount, 3);   // starts on a Tuesday, Mondays only
        IcsComponent cal = ToIcs(Series(p, 600, 630, UtcRule, "UTC"), out LossReport rep);
        Assert.Contains(rep, x => x.Code == nameof(CalendaringDefectKind.AmbiguousFirstInstance));
        Assert.Equal(new DateTime(2007, 4, 2, 10, 0, 0), cal.Events.First().Start!.Value.Value);
    }

    // Found by fuzzing: a rule whose 16-bit year (or an absolute transition year) is past 9999 once overflowed DateTime.
    [Fact]
    public void ARuleYearBeyond9999StillMakesAVTimeZone()
    {
        byte[] blob = Convert.FromBase64String("ASwBAAAAADAAxP///wAAAAAACwABAAIAAAAAAAACArm5ubm5ubm5ubm5uQICAAAAAAACAP7//f//9wAAAA==")[1..];
        if (TimeZoneDefinition.TryParse(blob, out TimeZoneDefinition? d) && d is not null)
        {
            Assert.NotNull(d.ToVTimeZone("Fuzz"));
        }

        TzRule far = new(3, 65000, -60, 0, -60, new SystemTime(65000, 3, 0, 15, 2, 0, 0, 0), new SystemTime(65000, 10, 0, 15, 2, 0, 0, 0));
        Assert.NotNull(new TimeZoneDefinition("Far", [far]).ToVTimeZone("Far"));
    }

    // Found by fuzzing: an end date at the top of the range once overflowed DateTime when the UNTIL value was made.
    [Fact]
    public void DatesBeyondYear9999AreClampedNotThrown()
    {
        byte[] blob = Convert.FromBase64String("AAQwBDAKIAAAAACgBQAA4BAAAAAAAAAhIAAADAAAAAAAAAACAAAAoMHcDIDS3AwAAAAAIH7cDDAA/////////yYA4AEAAP4BAAAAAAD/AAAAAAAAAA==")[1..];
        if (AppointmentRecurrencePattern.TryParse(blob, out AppointmentRecurrencePattern? r) && r is not null)
        {
            _ = OutlookRecurrence.ToRRule(r.Pattern, r.StartTimeOffset, d => d.ToString("yyyyMMdd"));
            _ = OutlookRecurrence.Days(r.Pattern, 2000).Count();
        }
    }

    [Fact]
    public void MonthlyDay30IsAnAmbiguityNotASilentChange()
    {
        RecurrencePattern p = Monthly(D("2007-01-30T00:00"), 1, 30);
        IcsComponent cal = ToIcs(Series(p, 600, 630, UtcRule, "UTC"), out LossReport rep);
        Assert.Contains(rep, x => x.Code == nameof(CalendaringDefectKind.AmbiguousShortMonth) && x.Kind == LossKind.Approximated);
        Assert.Contains("BYMONTHDAY=30", cal.Events.First().Component.FindProperty("RRULE")!.Value);
    }

    [Fact]
    public void MovedAndDeletedInstancesBecomeOverridesAndExdates()
    {
        RecurrencePattern p = Weekly(D("2007-03-26T00:00"), 1, 2, 0, RecurrencePattern.EndAfterCount, 6);   // Mondays
        List<DateTime> days = OutlookRecurrence.Days(p).ToList();
        p.DeletedInstanceDates.Add(OutlookMinutes.FromDateTime(days[1]));          // deleted
        p.DeletedInstanceDates.Add(OutlookMinutes.FromDateTime(days[2]));          // moved (original day)
        p.ModifiedInstanceDates.Add(OutlookMinutes.FromDateTime(days[2].AddDays(1)));
        ExceptionInfo ex = new()
        {
            OriginalStartTime = OutlookMinutes.FromDateTime(days[2].AddMinutes(600)),
            StartDateTime = OutlookMinutes.FromDateTime(days[2].AddDays(1).AddMinutes(660)),
            EndDateTime = OutlookMinutes.FromDateTime(days[2].AddDays(1).AddMinutes(720)),
            Overrides = ExceptionOverrides.Subject | ExceptionOverrides.Location,
            Subject = "Moved",
            Location = "Elsewhere",
        };
        MapiMessage m = Series(p, 600, 630, UtcRule, "UTC", "Series", exceptions: [ex]);
        IcsComponent cal = ToIcs(m, out LossReport rep);
        List<IcsComponent> events = cal.Events.ToList();
        Assert.Equal(2, events.Count);
        Assert.Single(events[0].ExceptionDates);
        Assert.Equal(days[1].AddMinutes(600), events[0].ExceptionDates[0].Value);
        IcsComponent o = events[1];
        Assert.Equal("Moved", o.Summary);
        Assert.Equal("Elsewhere", o.Location);
        Assert.Equal(days[2].AddMinutes(600), o.RecurrenceId!.Value.Value);
        Assert.Equal(days[2].AddDays(1).AddMinutes(660), o.Start!.Value.Value);
        Assert.Equal(events[0].Uid, o.Uid);
        List<DateTime> all = CalendarRecurrenceOccurrences(cal);
        Assert.Contains(days[2].AddDays(1).AddMinutes(660), all);
        Assert.DoesNotContain(days[2].AddMinutes(600), all);
        Assert.DoesNotContain(days[1].AddMinutes(600), all);
        Assert.Equal(5, all.Count);   // six instances, one deleted, one moved (its own override), so four from the rule and one override
        Assert.DoesNotContain(rep, x => x.IsContentLoss && x.Code != nameof(CalendaringDefectKind.ExceptionWithoutAttachment));
    }

    private static List<DateTime> CalendarRecurrenceOccurrences(IcsComponent cal) =>
        CalendarRecurrence.Expand(cal, new OccurrenceWindow(new DateTime(2007, 1, 1), new DateTime(2008, 1, 1))).Select(x => x.Start.Value).OrderBy(x => x).ToList();

    [Fact]
    public void RecurrenceBlobWithExceptionsRoundTripsThroughTheWriter()
    {
        RecurrencePattern p = Weekly(D("2007-03-26T00:00"), 1, 2, 0, RecurrencePattern.EndAfterCount, 6);
        AppointmentRecurrencePattern r = new() { Pattern = p, StartTimeOffset = 600, EndTimeOffset = 630 };
        p.DeletedInstanceDates.Add(100);
        p.ModifiedInstanceDates.Add(200);
        r.Exceptions.Add(new ExceptionInfo
        {
            StartDateTime = 1, EndDateTime = 2, OriginalStartTime = 3, Overrides = ExceptionOverrides.Subject | ExceptionOverrides.Location | ExceptionOverrides.BusyStatus | ExceptionOverrides.ReminderSet | ExceptionOverrides.ReminderDelta,
            Subject = "Ünï", Location = "L", BusyStatus = 1, ReminderSet = true, ReminderDelta = 10, ChangeHighlight = 5,
        });
        byte[] b = r.ToArray();
        List<CalendaringDefect> d = [];
        Assert.True(AppointmentRecurrencePattern.TryParse(b, out AppointmentRecurrencePattern? back, d));
        Assert.Empty(d);
        Assert.Equal(b, back!.ToArray());
        Assert.Equal("Ünï", back.Exceptions[0].Subject);
        Assert.Equal(5u, back.Exceptions[0].ChangeHighlight);
    }

    [Fact]
    public void MeetingRequestExportsMethodOrganizerAndAttendees()
    {
        var p = new IcsToMapiConverter.Props();
        p.Str(MapiPid.MessageClass, "IPM.Schedule.Meeting.Request");
        p.Str(MapiPid.Subject, "Kickoff");
        p.Str(MapiPid.SentRepresentingName, "Ann");
        p.Str(MapiPid.SentRepresentingAddressType, "SMTP");
        p.Str(MapiPid.SentRepresentingEmailAddress, "ann@example.com");
        p.NTime(OutlookNames.StartWhole, new DateTime(2007, 6, 1, 16, 0, 0));
        p.NTime(OutlookNames.EndWhole, new DateTime(2007, 6, 1, 17, 0, 0));
        p.NInt(OutlookNames.StateFlags, 3);
        p.NInt(OutlookNames.MeetingType, 1);
        p.NBin(OutlookNames.GlobalObjectId, IcsToMapiConverter.GlobalId("uid-1@example.com", null).Goid);
        p.NBin(OutlookNames.TimeZoneDefinitionStartDisplay, new TimeZoneDefinition("Pacific Standard Time", [Pacific with { Flags = 2 }]).ToArray());
        MapiRecipient bob = new(null);
        bob.Properties.Add(MapiProperty.CreateOwned(new MapiTag(MapiPid.RecipientType, MapiType.Integer32), BitConverter.GetBytes(1)));
        bob.Properties.Add(MapiProperty.CreateOwned(new MapiTag(MapiPid.DisplayName, MapiType.UnicodeString), Encoding.Unicode.GetBytes("Bob")));
        bob.Properties.Add(MapiProperty.CreateOwned(new MapiTag(MapiPid.SmtpAddress, MapiType.UnicodeString), Encoding.Unicode.GetBytes("bob@example.com")));
        bob.Properties.Add(MapiProperty.CreateOwned(new MapiTag(MapiPid.RecipientTrackStatus, MapiType.Integer32), BitConverter.GetBytes(3)));
        p.Message.AddRecipient(bob);
        IcsComponent cal = ToIcs(p.Message, out _);
        Assert.Equal("REQUEST", cal.MethodText);
        IcsComponent ev = cal.Events.Single();
        Assert.Equal("uid-1@example.com", ev.Uid);
        Assert.Equal("ann@example.com", ev.Organizer!.Email);
        IcsCalAddress at = ev.Attendees.Single();
        Assert.Equal("bob@example.com", at.Email);
        Assert.Equal("ACCEPTED", at.ParticipationStatus);
        Assert.Equal(new DateTime(2007, 6, 1, 9, 0, 0), ev.Start!.Value.Value);
        Assert.Equal("Pacific Standard Time", ev.Start.Value.TzId);
    }

    [Fact]
    public void TaskReadsAndExportsToVTodo()
    {
        var p = new IcsToMapiConverter.Props();
        p.Str(MapiPid.MessageClass, "IPM.Task");
        p.Str(MapiPid.Subject, "Write report");
        p.NInt(OutlookNames.TaskStatus, 1);
        p.NDouble(OutlookNames.PercentComplete, 0.25);
        p.NTime(OutlookNames.TaskStartDate, new DateTime(2007, 6, 1));
        p.NTime(OutlookNames.TaskDueDate, new DateTime(2007, 6, 8));
        p.NStr(OutlookNames.TaskOwner, "Ann");
        OutlookTask t = OutlookTask.TryCreate(p.Message)!;
        Assert.Equal(OutlookTaskStatus.InProgress, t.Status);
        Assert.Equal(0.25, t.PercentComplete);
        Assert.Equal(new DateTime(2007, 6, 8), t.DueDate);
        Assert.Equal("Ann", t.Owner);
        IcsComponent cal = ToIcs(p.Message, out LossReport rep);
        IcsComponent todo = cal.Todos.Single();
        Assert.Equal("IN-PROCESS", todo.Status);
        Assert.Equal(25, todo.PercentComplete);
        Assert.Equal(new DateTime(2007, 6, 8), todo.Due!.Value.Value);
        Assert.Contains(rep, x => x.Name.Contains("0x811F", StringComparison.OrdinalIgnoreCase) || x.Name.Contains('8', StringComparison.Ordinal));
    }
}
