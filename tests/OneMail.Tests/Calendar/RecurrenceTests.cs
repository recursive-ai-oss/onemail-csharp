using RecursiveAI.OneMail.Calendar.Ics;
using RecursiveAI.OneMail.Calendar.Recurrence;
using RecursiveAI.OneMail.Calendar.TimeZones;

namespace RecursiveAI.OneMail.Tests.Calendar;

public class RecurrenceTests
{
    private static List<Occurrence> Run(string[] lines, ExpansionReport? report = null, ExpansionOptions? options = null, string? vtimezone = null, OccurrenceWindow? window = null) =>
        RecurrenceTestKit.Expand(RecurrenceTestKit.Calendar(RecurrenceTestKit.Event(lines), vtimezone), options, report, window);

    private static string[] Utc(IEnumerable<Occurrence> o) => [.. o.Select(x => x.StartUtc?.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture) ?? "-")];

    private static string[] Local(IEnumerable<Occurrence> o) => [.. o.Select(x => RecurrenceTestKit.Iso(x.Start.Value))];

    [Fact]
    public void ASingleEventIsItsOwnOccurrence()
    {
        List<Occurrence> o = Run(["DTSTART:20260105T090000Z", "DTEND:20260105T100000Z"]);
        Assert.Single(o);
        Assert.Equal(OccurrenceOrigin.Start, o[0].Origin);
        Assert.Equal(new DateTime(2026, 1, 5, 10, 0, 0, DateTimeKind.Utc), o[0].EndUtc);
    }

    [Fact]
    public void WindowSelectsOverlappingOrStartingOccurrences()
    {
        string[] ev = ["DTSTART:20260105T090000Z", "DTEND:20260105T110000Z", "RRULE:FREQ=DAILY;COUNT=5"];
        OccurrenceWindow w = new(new DateTime(2026, 1, 6, 10, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 8, 9, 30, 0, DateTimeKind.Utc));
        Assert.Equal(["20260106T090000Z", "20260107T090000Z", "20260108T090000Z"], Utc(Run(ev, window: w)));
        Assert.Equal(["20260107T090000Z", "20260108T090000Z"], Utc(Run(ev, options: new ExpansionOptions { WindowMode = WindowMode.StartingIn }, window: w)));
    }

    [Fact]
    public void FarWindowOfAnEndlessRuleIsReachedWithoutScanningFromTheStart()
    {
        // SECONDLY since 2000, asked about 2026: without a jump this would be 800 million steps.
        List<Occurrence> o = Run(["DTSTART:20000101T000000Z", "RRULE:FREQ=SECONDLY"], window: OccurrenceWindow.From(new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc), TimeSpan.FromSeconds(3)));
        Assert.Equal(["20260601T120000Z", "20260601T120001Z", "20260601T120002Z"], Utc(o));
    }

    [Fact]
    public void InvalidDatesAreSkippedAndNotCounted()
    {
        // RFC 5545 section 3.3.10: February 30 and a fifth Monday that does not exist are ignored.
        Assert.Equal(["2026-01-30T09:00:00", "2026-03-30T09:00:00", "2026-04-30T09:00:00"], Local(Run(["DTSTART:20260130T090000", "RRULE:FREQ=MONTHLY;BYMONTHDAY=30;COUNT=3"])));
        Assert.Equal(["2026-03-30T09:00:00", "2026-06-29T09:00:00"], Local(Run(["DTSTART:20260330T090000", "RRULE:FREQ=MONTHLY;BYDAY=5MO;COUNT=2"])));
    }

    [Fact]
    public void SkippedLocalTimeShiftsForwardByDefaultAndCanBeDropped()
    {
        string[] ev = ["DTSTART;TZID=America/New_York:20260307T023000", "RRULE:FREQ=DAILY;COUNT=3"];
        ExpansionReport report = new();
        List<Occurrence> shifted = Run(ev, report);
        // 02:30 on 8 March does not exist; RFC 5545 section 3.3.5 reads it with the offset before the gap, 03:30 EDT.
        Assert.Equal(["20260307T073000Z", "20260308T073000Z", "20260309T063000Z"], Utc(shifted));
        Assert.Equal(1, report.CountOf(RecurrenceDefectKind.AmbiguousSkippedLocalTime));
        // Dropped ones are not counted, so the third instance still comes.
        Assert.Equal(["20260307T073000Z", "20260309T063000Z", "20260310T063000Z"], Utc(Run(ev, options: new ExpansionOptions { SkippedTime = SkippedTimePolicy.Skip })));
    }

    [Fact]
    public void RepeatedLocalTimeTakesTheFirstOccurrence()
    {
        ExpansionReport report = new();
        List<Occurrence> o = Run(["DTSTART;TZID=America/New_York:20261101T013000", "RRULE:FREQ=DAILY;COUNT=1"], report);
        Assert.Equal(["20261101T053000Z"], Utc(o)); // 01:30 EDT, not 01:30 EST
        Assert.Equal(1, report.CountOf(RecurrenceDefectKind.AmbiguousRepeatedLocalTime));
    }

    [Theory]
    [InlineData("America/New_York", 2026, 3, 7, -5, -4)]
    [InlineData("America/New_York", 2026, 10, 31, -4, -5)]
    [InlineData("Europe/Berlin", 2026, 3, 28, 1, 2)]
    [InlineData("Europe/Berlin", 2026, 10, 24, 2, 1)]
    [InlineData("Australia/Sydney", 2026, 4, 4, 11, 10)]
    [InlineData("Australia/Sydney", 2026, 10, 3, 10, 11)]
    [InlineData("Pacific/Auckland", 2026, 9, 26, 12, 13)]
    [InlineData("Pacific/Auckland", 2026, 4, 4, 13, 12)]
    public void DailyEventKeepsItsWallClockTimeAcrossBothChanges(string tz, int year, int month, int day, int offsetBefore, int offsetAfter)
    {
        // 10:00 local on the day before, the day of and the day after the clock change: the UTC time moves by the offset difference.
        List<Occurrence> o = Run([$"DTSTART;TZID={tz}:{year}{month:00}{day:00}T100000", "RRULE:FREQ=DAILY;COUNT=3"]);
        DateTime d0 = new(year, month, day, 10, 0, 0, DateTimeKind.Utc);
        Assert.Equal(d0.AddHours(-offsetBefore), o[0].StartUtc);
        Assert.Equal(d0.AddDays(2).AddHours(-offsetAfter), o[2].StartUtc);
        Assert.All(o, x => Assert.Equal(10, x.Start.Value.Hour));
    }

    [Fact]
    public void ExactDurationSurvivesAChangeAndNominalDaysDoNot()
    {
        // RFC 5545 section 3.8.5.3: the span between DTSTART and DTEND here is 23 hours (the clocks go forward in between) and every
        // instance keeps that exact length; DURATION:P1D is a nominal day, so only the instance that spans the change is 23 hours long.
        List<Occurrence> exact = Run(["DTSTART;TZID=America/New_York:20260307T120000", "DTEND;TZID=America/New_York:20260308T120000", "RRULE:FREQ=DAILY;COUNT=2"]);
        Assert.Equal(TimeSpan.FromHours(23), exact[0].EndUtc - exact[0].StartUtc);
        Assert.Equal(TimeSpan.FromHours(23), exact[1].EndUtc - exact[1].StartUtc);
        List<Occurrence> nominal = Run(["DTSTART;TZID=America/New_York:20260307T120000", "DURATION:P1D", "RRULE:FREQ=DAILY;COUNT=2"]);
        Assert.Equal(TimeSpan.FromHours(23), nominal[0].EndUtc - nominal[0].StartUtc);
        Assert.Equal(TimeSpan.FromHours(24), nominal[1].EndUtc - nominal[1].StartUtc);
    }

    [Fact]
    public void ExdateRdateAndPeriods()
    {
        List<Occurrence> o = Run(["DTSTART:20260105T090000Z", "RRULE:FREQ=DAILY;COUNT=4", "EXDATE:20260106T090000Z", "RDATE:20260110T120000Z", "RDATE;VALUE=PERIOD:20260111T080000Z/PT3H"]);
        Assert.Equal(["20260105T090000Z", "20260107T090000Z", "20260108T090000Z", "20260110T120000Z", "20260111T080000Z"], Utc(o));
        Assert.Equal(OccurrenceOrigin.Period, o[4].Origin);
        Assert.Equal(new DateTime(2026, 1, 11, 11, 0, 0, DateTimeKind.Utc), o[4].EndUtc);
    }

    [Fact]
    public void DateExdateRemovesTheWholeDayAndIsReported()
    {
        ExpansionReport report = new();
        List<Occurrence> o = Run(["DTSTART;TZID=America/New_York:20260105T090000", "RRULE:FREQ=DAILY;COUNT=4", "EXDATE;VALUE=DATE:20260106"], report);
        Assert.Equal(3, o.Count);
        Assert.Equal(1, report.CountOf(RecurrenceDefectKind.AmbiguousExdateType));
    }

    [Fact]
    public void ExruleRemovesItsInstances()
    {
        Assert.Equal(["2026-01-05T09:00:00", "2026-01-06T09:00:00", "2026-01-07T09:00:00"], Local(Run(["DTSTART:20260105T090000", "RRULE:FREQ=DAILY;COUNT=7", "EXRULE:FREQ=WEEKLY;BYDAY=TH,FR,SA,SU"])));
    }

    [Fact]
    public void OverrideMovesOneOccurrenceAndThisAndFutureMovesTheRest()
    {
        string master = RecurrenceTestKit.Event("DTSTART:20260105T090000Z", "RRULE:FREQ=DAILY;COUNT=5", "SUMMARY:series");
        string one = "BEGIN:VEVENT\r\nUID:test@example\r\nDTSTART:20260107T150000Z\r\nRECURRENCE-ID:20260107T090000Z\r\nSUMMARY:moved\r\nEND:VEVENT\r\n";
        List<Occurrence> o = RecurrenceTestKit.Expand(RecurrenceTestKit.Calendar(master + one));
        Assert.Equal(["20260105T090000Z", "20260106T090000Z", "20260107T150000Z", "20260108T090000Z", "20260109T090000Z"], Utc(o));
        Assert.Equal("moved", o[2].Component.Summary);
        Assert.Equal(new DateTime(2026, 1, 7, 9, 0, 0, DateTimeKind.Utc), o[2].RecurrenceId.Value);

        string future = "BEGIN:VEVENT\r\nUID:test@example\r\nDTSTART:20260107T100000Z\r\nDTEND:20260107T113000Z\r\nRECURRENCE-ID;RANGE=THISANDFUTURE:20260107T090000Z\r\nSUMMARY:later\r\nEND:VEVENT\r\n";
        o = RecurrenceTestKit.Expand(RecurrenceTestKit.Calendar(master + future));
        Assert.Equal(["20260105T090000Z", "20260106T090000Z", "20260107T100000Z", "20260108T100000Z", "20260109T100000Z"], Utc(o));
        Assert.Equal(OccurrenceOrigin.FutureOverride, o[3].Origin);
        Assert.Equal(TimeSpan.FromMinutes(90), o[4].EndUtc - o[4].StartUtc);
    }

    [Fact]
    public void OrphanOverrideIsReported()
    {
        ExpansionReport report = new();
        List<Occurrence> o = RecurrenceTestKit.Expand(RecurrenceTestKit.Calendar("BEGIN:VEVENT\r\nUID:x\r\nDTSTART:20260107T150000Z\r\nRECURRENCE-ID:20260107T090000Z\r\nEND:VEVENT\r\n"), null, report);
        Assert.Single(o);
        Assert.Equal(1, report.CountOf(RecurrenceDefectKind.OrphanOverride));
    }

    [Theory]
    [InlineData("DATE", "DTSTART;VALUE=DATE:20260105", "RRULE:FREQ=DAILY;UNTIL=20260107", 3)]
    [InlineData("zoned, UTC until", "DTSTART;TZID=America/New_York:20260105T090000", "RRULE:FREQ=DAILY;UNTIL=20260107T140000Z", 3)]
    [InlineData("zoned, UTC until just before", "DTSTART;TZID=America/New_York:20260105T090000", "RRULE:FREQ=DAILY;UNTIL=20260107T135959Z", 2)]
    [InlineData("zoned, DATE until", "DTSTART;TZID=America/New_York:20260105T090000", "RRULE:FREQ=DAILY;UNTIL=20260107", 3)]
    [InlineData("floating", "DTSTART:20260105T090000", "RRULE:FREQ=DAILY;UNTIL=20260107T090000", 3)]
    public void UntilIsInclusiveInTheValueTypeItIsWritten(string name, string start, string rule, int count)
    {
        Assert.True(Run([start, rule]).Count == count, name);
    }

    [Fact]
    public void WeekStartChangesWhichDaysAWeeklyIntervalPicks()
    {
        // RFC 5545 section 3.8.5.3: with INTERVAL=2 and BYDAY=TU,SU the week boundary decides which Sundays count.
        Assert.Equal(["1997-08-05", "1997-08-10", "1997-08-19", "1997-08-24"], Local(Run(["DTSTART:19970805T090000", "RRULE:FREQ=WEEKLY;INTERVAL=2;COUNT=4;BYDAY=TU,SU;WKST=MO"])).Select(x => x[..10]));
        Assert.Equal(["1997-08-05", "1997-08-17", "1997-08-19", "1997-08-31"], Local(Run(["DTSTART:19970805T090000", "RRULE:FREQ=WEEKLY;INTERVAL=2;COUNT=4;BYDAY=TU,SU;WKST=SU"])).Select(x => x[..10]));
    }

    [Fact]
    public void WeekNumberOneCanStartInDecember()
    {
        // ISO 8601 week 1 of 2026 starts on Monday 29 December 2025.
        Assert.Equal(["2025-12-29"], Local(Run(["DTSTART:20251229T090000", "RRULE:FREQ=YEARLY;BYWEEKNO=1;BYDAY=MO;COUNT=1"])).Select(x => x[..10]));
        Assert.Equal(["2026-12-28"], Local(Run(["DTSTART:20261228T090000", "RRULE:FREQ=YEARLY;BYWEEKNO=53;BYDAY=MO;COUNT=1"])).Select(x => x[..10]));
    }

    [Fact]
    public void BySetPosPicksFromTheSortedPeriod()
    {
        Assert.Equal(["2026-01-30", "2026-02-27", "2026-03-31"], Local(Run(["DTSTART:20260130T090000", "RRULE:FREQ=MONTHLY;BYDAY=MO,TU,WE,TH,FR;BYSETPOS=-1;COUNT=3"])).Select(x => x[..10]));
    }

    // ---- Hostile rules ----

    [Fact]
    public void ImpossibleRuleEndsWithADefectNotAHang()
    {
        ExpansionReport report = new();
        List<Occurrence> o = Run(["DTSTART:20260105T090000", "RRULE:FREQ=YEARLY;BYMONTH=2;BYMONTHDAY=30"], report);
        Assert.Single(o); // only DTSTART
    }

    [Fact]
    public void ContradictoryPartsAreLimitedByIterations()
    {
        ExpansionReport report = new();
        // Every second, but only in a month that has no 31st and only on the 31st: nothing can ever match.
        List<Occurrence> o = Run(["DTSTART:20260105T090000", "RRULE:FREQ=SECONDLY;BYMONTH=2;BYMONTHDAY=31"], report, new ExpansionOptions { MaxIterationsWithoutHit = 50_000 });
        Assert.Single(o);
        Assert.True(report.CountOf(RecurrenceDefectKind.IterationLimit) >= 1);
    }

    [Fact]
    public void SecondlyOverCenturiesStopsAtTheOccurrenceLimit()
    {
        ExpansionReport report = new();
        List<Occurrence> o = Run(["DTSTART:20260105T090000Z", "RRULE:FREQ=SECONDLY"], report, new ExpansionOptions { MaxOccurrences = 1000 });
        Assert.Equal(1000, o.Count);
        Assert.Equal(1, report.CountOf(RecurrenceDefectKind.OccurrenceLimit));
    }

    [Fact]
    public void HugeCountAndIntervalAreHarmless()
    {
        ExpansionReport report = new();
        List<Occurrence> o = Run(["DTSTART:20260105T090000Z", "RRULE:FREQ=YEARLY;INTERVAL=2147483647;COUNT=2147483647"], report);
        Assert.Single(o);
        o = Run(["DTSTART:20260105T090000Z", "RRULE:FREQ=DAILY;COUNT=2147483647"], report, new ExpansionOptions { MaxOccurrences = 10 });
        Assert.Equal(10, o.Count);
    }

    [Theory]
    [InlineData("RRULE:FREQ=DAILY;BYMONTHDAY=0")]
    [InlineData("RRULE:FREQ=DAILY;BYHOUR=24")]
    [InlineData("RRULE:FREQ=DAILY;BYSETPOS=0;BYDAY=MO")]
    [InlineData("RRULE:FREQ=DAILY;COUNT=0")]
    [InlineData("RRULE:FREQ=NEVER")]
    [InlineData("RRULE:RSCALE=HEBREW;FREQ=YEARLY")]
    public void InvalidRulesAreIgnoredWithADefect(string rule)
    {
        ExpansionReport report = new();
        List<Occurrence> o = Run(["DTSTART:20260105T090000Z", rule], report);
        Assert.Single(o);
        Assert.True(report.Defects.Count > 0);
    }

    [Fact]
    public void MissingStartIsADefect()
    {
        ExpansionReport report = new();
        Assert.Empty(Run(["RRULE:FREQ=DAILY"], report));
        Assert.Equal(1, report.CountOf(RecurrenceDefectKind.MissingStart));
    }

    [Fact]
    public void DateSeriesWithTimeRulesCollapsesToDays()
    {
        ExpansionReport report = new();
        List<Occurrence> o = Run(["DTSTART;VALUE=DATE:20260105", "RRULE:FREQ=HOURLY;COUNT=5"], report);
        Assert.True(o.All(x => x.Start.IsDate));
        Assert.True(report.CountOf(RecurrenceDefectKind.TimeRuleOnDate) >= 1);
    }
}

public class TimeZoneTests
{
    private static IcsTimeZone Zone(string tzid, TimeZoneResolverOptions? options = null, string? vtimezone = null, ExpansionReport? report = null) =>
        new TimeZoneResolver(vtimezone is null ? null : RecurrenceTestKit.Calendar(string.Empty, vtimezone), options, report).Resolve(tzid)!;

    // What the resolver's runtime lookup finds: the IANA name, or on Windows without ICU the zone CLDR names for it.
    private static TimeZoneInfo RuntimeZone(string iana)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(iana);
        }
        catch (TimeZoneNotFoundException)
        {
            Assert.True(WindowsZones.TryGetWindows(iana, out string? windows));
            return TimeZoneInfo.FindSystemTimeZoneById(windows);
        }
    }

    // Stands in for a Windows runtime without ICU on any platform: IANA names are not found, Windows ids are
    // (answered from the same data through CLDR's table, so only the lookup path differs).
    private static TimeZoneInfo? WindowsOnlyRuntime(string name) =>
        WindowsZones.TryGetIana(name, out string? iana) ? RuntimeZone(iana) : null;

    [Theory]
    [InlineData("America/New_York", "Eastern Standard Time")]
    [InlineData("Europe/Berlin", "W. Europe Standard Time")]
    [InlineData("Australia/Sydney", "AUS Eastern Standard Time")]
    [InlineData("Pacific/Auckland", "New Zealand Standard Time")]
    [InlineData("America/Tijuana", "Pacific Standard Time (Mexico)")]
    [InlineData("America/Toronto", "Eastern Standard Time")]
    public void IanaNamesFallBackToTheWindowsIdWhenTheRuntimeOnlyKnowsThose(string iana, string windows)
    {
        Assert.True(WindowsZones.TryGetWindows(iana, out string? w));
        Assert.Equal(windows, w);
        TimeZoneResolverOptions windowsOnly = new() { RuntimeLookup = WindowsOnlyRuntime };
        ExpansionReport report = new();
        IcsTimeZone z = Zone(iana, windowsOnly, report: report);
        Assert.Equal(ZoneSource.Iana, z.Source);
        Assert.Equal(iana, z.Id);
        Assert.Empty(report.Defects);
        foreach (DateTime t in new[] { new DateTime(2026, 1, 15, 12, 0, 0), new DateTime(2026, 7, 15, 12, 0, 0) })
        {
            Assert.Equal(Zone(iana).GetUtcOffset(t), z.GetUtcOffset(t));
        }

        // A Windows name still goes through the table, whatever the runtime knows.
        ExpansionReport wr = new();
        Assert.Equal(ZoneSource.WindowsName, Zone(windows, windowsOnly, report: wr).Source);
        Assert.Equal(1, wr.CountOf(RecurrenceDefectKind.AmbiguousWindowsZone));
    }

    [Fact]
    public void ZonesKnownToNeitherSideAreATypedDefectNotAnException()
    {
        ExpansionReport report = new();
        Assert.Null(new TimeZoneResolver(null, new TimeZoneResolverOptions { RuntimeLookup = _ => null }, report).Resolve("Europe/Berlin"));
        Assert.Contains(report.Defects, d => d.Kind == RecurrenceDefectKind.UnknownTimeZone);
        ExpansionReport r2 = new();
        Assert.Null(new TimeZoneResolver(null, new TimeZoneResolverOptions { RuntimeLookup = WindowsOnlyRuntime }, r2).Resolve("Nowhere/Middle"));
        Assert.Contains(r2.Defects, d => d.Kind == RecurrenceDefectKind.UnknownTimeZone);
    }

    [Fact]
    public void EveryWindowsNameMapsToAnIanaNameThatMapsBackToAWindowsName()
    {
        foreach ((string windows, string iana) in WindowsZones.Rows)
        {
            Assert.True(WindowsZones.TryGetWindows(iana, out string? back), iana);
            Assert.Equal(windows, back); // the 001 row wins, so the default-territory zone returns to its own Windows name
        }
    }

    [Theory]
    [InlineData("Eastern Standard Time", "America/New_York")]
    [InlineData("W. Europe Standard Time", "Europe/Berlin")]
    [InlineData("AUS Eastern Standard Time", "Australia/Sydney")]
    [InlineData("New Zealand Standard Time", "Pacific/Auckland")]
    [InlineData("Pacific Standard Time", "America/Los_Angeles")]
    public void WindowsNamesResolveThroughTheTable(string windows, string iana)
    {
        ExpansionReport report = new();
        IcsTimeZone w = Zone(windows, report: report);
        IcsTimeZone i = Zone(iana);
        Assert.Equal(ZoneSource.WindowsName, w.Source);
        Assert.Equal(1, report.CountOf(RecurrenceDefectKind.AmbiguousWindowsZone));
        foreach (DateTime t in new[] { new DateTime(2026, 1, 15, 12, 0, 0), new DateTime(2026, 7, 15, 12, 0, 0) })
        {
            Assert.Equal(i.GetUtcOffset(t), w.GetUtcOffset(t));
        }
    }

    [Fact]
    public void UnknownZoneIsADefectAndTheValuesStayFloating()
    {
        ExpansionReport report = new();
        IcsComponent cal = RecurrenceTestKit.Calendar(RecurrenceTestKit.Event("DTSTART;TZID=Nowhere/Middle:20260105T090000", "RRULE:FREQ=DAILY;COUNT=2"));
        List<Occurrence> o = RecurrenceTestKit.Expand(cal, null, report);
        Assert.Equal(2, o.Count);
        Assert.All(o, x => Assert.Null(x.StartUtc));
        Assert.Contains(report.Defects, d => d.Kind == RecurrenceDefectKind.UnknownTimeZone && d.Detail == "Nowhere/Middle");
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("/etc/localtime")]
    [InlineData("a\0b")]
    public void HostileZoneNamesNeverReachTheFileSystem(string tzid)
    {
        Assert.Null(new TimeZoneResolver().Resolve(tzid));
    }

    [Fact]
    public void VendorPrefixIsStrippedWithADefect()
    {
        ExpansionReport report = new();
        IcsTimeZone z = Zone("/softwarestudio.org/Olson_20011030_5/America/New_York", report: report);
        Assert.Equal(TimeSpan.FromHours(-5), z.GetUtcOffset(new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(1, report.CountOf(RecurrenceDefectKind.AmbiguousZoneName));
    }

    [Fact]
    public void VTimeZoneWinsOverTheRuntimeDatabase()
    {
        const string odd = "BEGIN:VTIMEZONE\r\nTZID:America/New_York\r\nBEGIN:STANDARD\r\nDTSTART:20000101T000000\r\nTZOFFSETFROM:+0300\r\nTZOFFSETTO:+0300\r\nEND:STANDARD\r\nEND:VTIMEZONE\r\n";
        IcsTimeZone z = Zone("America/New_York", vtimezone: odd);
        Assert.Equal(ZoneSource.VTimeZone, z.Source);
        Assert.Equal(TimeSpan.FromHours(3), z.GetUtcOffset(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void TzidAliasOfFollowsTheTarget()
    {
        const string alias = "BEGIN:VTIMEZONE\r\nTZID:Eastern\r\nTZID-ALIAS-OF:America/New_York\r\nEND:VTIMEZONE\r\n";
        IcsTimeZone z = Zone("Eastern", vtimezone: alias);
        Assert.Equal(TimeSpan.FromHours(-4), z.GetUtcOffset(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void NoRuntimeDatabaseStillWorksFromTheFilesOwnZones()
    {
        TimeZoneResolverOptions off = new() { UseRuntimeDatabase = false };
        Assert.Null(Zone("Europe/Berlin", off));
        IcsTimeZone z = Zone("America/New_York", off, RecurrenceTestKit.NewYorkVTimeZone);
        Assert.Equal(ZoneSource.VTimeZone, z.Source);
    }

    [Theory]
    [InlineData("America/New_York")]
    [InlineData("Europe/Berlin")]
    [InlineData("Australia/Sydney")]
    [InlineData("Pacific/Auckland")]
    public void RuntimeZoneConvertsBothWaysAcrossBothChanges(string tzid)
    {
        IcsTimeZone z = Zone(tzid);
        TimeZoneInfo info = RuntimeZone(tzid);
        // Every half hour of 2026: local -> UTC -> local is stable except inside the skipped hour, and the offset matches the runtime's.
        int skipped = 0, repeated = 0;
        for (DateTime l = new(2026, 1, 1); l < new DateTime(2027, 1, 1); l = l.AddMinutes(30))
        {
            DateTime u = z.ToUtc(l, out LocalTimeState state);
            if (state == LocalTimeState.Skipped)
            {
                skipped++;
                continue;
            }

            repeated += state == LocalTimeState.Ambiguous ? 1 : 0;
            Assert.Equal(l, z.ToLocal(u));
            Assert.Equal(info.GetUtcOffset(DateTime.SpecifyKind(u, DateTimeKind.Utc)), z.GetUtcOffset(u));
        }

        Assert.True(skipped == 2 && repeated == 2, $"{tzid}: skipped {skipped}, repeated {repeated}");
    }

    [Theory]
    [InlineData("America/New_York")]
    public void VTimeZoneAgreesWithTheRuntimeAroundBothChanges(string tzid)
    {
        IcsTimeZone v = Zone(tzid, new TimeZoneResolverOptions { UseRuntimeDatabase = false }, RecurrenceTestKit.NewYorkVTimeZone);
        IcsTimeZone r = Zone(tzid);
        // Windows zone data has only the current US rules (docs/recurrence.md); the VTIMEZONE here holds the pre-2007 ones too.
        int[] years = OperatingSystem.IsWindows() ? [2007, 2026, 2040] : [1999, 2006, 2007, 2026, 2040];
        foreach (int year in years)
        {
            for (DateTime t = new(year, 1, 1, 0, 0, 0, DateTimeKind.Utc); t < new DateTime(year + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc); t = t.AddMinutes(30))
            {
                Assert.Equal(r.GetUtcOffset(t), v.GetUtcOffset(t));
            }

            for (DateTime l = new(year, 1, 1); l < new DateTime(year + 1, 1, 1); l = l.AddMinutes(30))
            {
                Assert.Equal(r.ToUtc(l, out LocalTimeState rs), v.ToUtc(l, out LocalTimeState vs));
                Assert.Equal(rs, vs);
            }
        }
    }

    [Fact]
    public void FloatingStaysFloating()
    {
        List<Occurrence> o = RecurrenceTestKit.Expand(RecurrenceTestKit.Calendar(RecurrenceTestKit.Event("DTSTART:20260105T090000", "RRULE:FREQ=DAILY;COUNT=2")));
        Assert.All(o, x => { Assert.Null(x.StartUtc); Assert.Equal(IcsTimeKind.Floating, x.Start.Kind); });
    }
}
