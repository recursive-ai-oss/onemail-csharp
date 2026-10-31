using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.Ics;
using RecursiveAI.OneMail.Calendar.Recurrence;
using RecursiveAI.OneMail.Calendaring;
using RecursiveAI.OneMail.Conversion;
using RecursiveAI.OneMail.Conversion.OxcIcal;
using RecursiveAI.OneMail.Mapi;

namespace RecursiveAI.OneMail.Tests.Calendaring;

/// <summary>Builds Outlook calendar items in memory the way Outlook lays them out (MS-OXOCAL), for the reading and conversion tests.</summary>
internal static class CalendarFixtures
{
    internal static readonly TzRule UtcRule = new(3, 0, 0, 0, 0, default, default);

    // Pacific time, 2007 US rules: DST from the second Sunday of March 02:00, standard from the first Sunday of November 02:00.
    internal static readonly TzRule Pacific = new(3, 0, 480, 0, -60, new SystemTime(0, 11, 0, 1, 2, 0, 0, 0), new SystemTime(0, 3, 0, 2, 2, 0, 0, 0));

    // Sydney: standard from the first Sunday of April 03:00, DST from the first Sunday of October 02:00.
    internal static readonly TzRule Sydney = new(3, 0, -600, 0, -60, new SystemTime(0, 4, 0, 1, 3, 0, 0, 0), new SystemTime(0, 10, 0, 1, 2, 0, 0, 0));

    internal static DateTime D(string s) => DateTime.ParseExact(s, "yyyy-MM-dd'T'HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    internal static MapiMessage Series(RecurrencePattern p, uint startOffset, uint endOffset, TzRule rule, string tzName, string subject = "Series", string cls = "IPM.Appointment",
        IEnumerable<ExceptionInfo>? exceptions = null)
    {
        var props = new IcsToMapiConverter.Props();
        props.Str(MapiPid.MessageClass, cls);
        props.Str(MapiPid.Subject, subject);
        AppointmentRecurrencePattern r = new() { Pattern = p, StartTimeOffset = startOffset, EndTimeOffset = endOffset };
        if (exceptions is not null)
        {
            r.Exceptions.AddRange(exceptions);
        }

        DateTime first = OutlookRecurrence.FirstDay(p) ?? OutlookMinutes.ToDateTime(p.StartDate);
        ZoneInfo zi = new(rule);
        props.NTime(OutlookNames.StartWhole, zi.ToUtc(first.AddMinutes(startOffset)));
        props.NTime(OutlookNames.EndWhole, zi.ToUtc(first.AddMinutes(endOffset)));
        props.NBool(OutlookNames.Recurring, true);
        props.NBin(OutlookNames.AppointmentRecur, r.ToArray());
        props.NBin(OutlookNames.TimeZoneStruct, TimeZoneStruct.ToArray(rule));
        props.NBin(OutlookNames.TimeZoneDefinitionRecur, new TimeZoneDefinition(tzName, [rule with { Flags = 3 }]).ToArray());
        props.NBin(OutlookNames.GlobalObjectId, IcsToMapiConverter.GlobalId("series-" + subject, null).Goid);
        return props.Message;
    }

    // Local-to-UTC for the generated rules, via the same VTIMEZONE route the library uses.
    private sealed class ZoneInfo(TzRule rule)
    {
        private readonly OutlookTimeZone _z = Make(rule);

        private static OutlookTimeZone Make(TzRule rule)
        {
            var m = new IcsToMapiConverter.Props();
            m.NBin(OutlookNames.TimeZoneStruct, TimeZoneStruct.ToArray(rule));
            m.NBin(OutlookNames.AppointmentRecur, new AppointmentRecurrencePattern().ToArray());
            return OutlookAppointment.TryCreate(WithClass(m.Message))!.TimeZone ?? throw new InvalidOperationException();
        }

        public DateTime ToUtc(DateTime local) => _z.Zone.ToUtc(local, out _);
    }

    private static MapiMessage WithClass(MapiMessage m)
    {
        m.Properties.Add(MapiProperty.CreateOwned(new MapiTag(MapiPid.MessageClass, MapiType.UnicodeString), System.Text.Encoding.Unicode.GetBytes("IPM.Appointment")));
        return m;
    }

    internal static RecurrencePattern Daily(DateTime start, int everyDays, uint end = RecurrencePattern.EndNever, uint count = 10, DateTime? until = null)
    {
        RecurrencePattern p = new() { RecurFrequency = 0x200A, PatternType = 0, Period = (uint)everyDays * 1440, StartDate = OutlookMinutes.FromDateTime(start.Date) };
        return Finish(p, start, end, count, until);
    }

    internal static RecurrencePattern Weekly(DateTime start, int everyWeeks, uint mask, uint firstDow = 0, uint end = RecurrencePattern.EndNever, uint count = 10, DateTime? until = null)
    {
        RecurrencePattern p = new() { RecurFrequency = 0x200B, PatternType = 1, Period = (uint)everyWeeks, WeekdayMask = mask, FirstDayOfWeek = firstDow, StartDate = OutlookMinutes.FromDateTime(start.Date) };
        return Finish(p, start, end, count, until);
    }

    internal static RecurrencePattern Monthly(DateTime start, int everyMonths, uint day, uint end = RecurrencePattern.EndNever, uint count = 10, DateTime? until = null)
    {
        RecurrencePattern p = new() { RecurFrequency = 0x200C, PatternType = 2, Period = (uint)everyMonths, DayOfMonth = day, StartDate = OutlookMinutes.FromDateTime(start.Date) };
        return Finish(p, start, end, count, until);
    }

    internal static RecurrencePattern MonthlyNth(DateTime start, int everyMonths, uint mask, uint nth, uint end = RecurrencePattern.EndNever, uint count = 10, DateTime? until = null)
    {
        RecurrencePattern p = new() { RecurFrequency = 0x200C, PatternType = 3, Period = (uint)everyMonths, WeekdayMask = mask, Nth = nth, StartDate = OutlookMinutes.FromDateTime(start.Date) };
        return Finish(p, start, end, count, until);
    }

    internal static RecurrencePattern Yearly(DateTime start, uint day, uint end = RecurrencePattern.EndNever, uint count = 10, DateTime? until = null)
    {
        RecurrencePattern p = new() { RecurFrequency = 0x200D, PatternType = 2, Period = 12, DayOfMonth = day, StartDate = OutlookMinutes.FromDateTime(start.Date) };
        return Finish(p, start, end, count, until);
    }

    internal static RecurrencePattern YearlyNth(DateTime start, uint mask, uint nth, uint end = RecurrencePattern.EndNever, uint count = 10, DateTime? until = null)
    {
        RecurrencePattern p = new() { RecurFrequency = 0x200D, PatternType = 3, Period = 12, WeekdayMask = mask, Nth = nth, StartDate = OutlookMinutes.FromDateTime(start.Date) };
        return Finish(p, start, end, count, until);
    }

    private static RecurrencePattern Finish(RecurrencePattern p, DateTime start, uint end, uint count, DateTime? until)
    {
        p.FirstDateTime = OutlookRecurrence.FirstDateTimeFor(p, start.Date);
        p.EndType = end;
        if (end == RecurrencePattern.EndAfterCount)
        {
            p.OccurrenceCount = count;
            p.EndDate = OutlookMinutes.FromDateTime(OutlookRecurrence.Days(p).Last());
        }
        else if (end == RecurrencePattern.EndAfterDate)
        {
            p.EndDate = OutlookMinutes.FromDateTime(until!.Value.Date);
            List<DateTime> days = OutlookRecurrence.Days(p).ToList();
            p.OccurrenceCount = (uint)days.Count;
            p.EndDate = OutlookMinutes.FromDateTime(days[^1]);
        }
        else
        {
            p.OccurrenceCount = 10;
            p.EndDate = OutlookMinutes.NeverEnds;
        }

        return p;
    }

    internal static IcsComponent ToIcs(MapiMessage m, out LossReport report, OxcIcalExportOptions? options = null)
    {
        ConversionResult<ContentDocument> r = new MapiToIcsConverter(options).Convert(m);
        report = r.Report;
        byte[] bytes = r.Value.ToArray();
        return IcsDocument.Calendars(ContentDocument.Parse(bytes)).First();
    }

    internal static List<DateTime> Occurrences(IcsComponent cal, DateTime from, DateTime to) =>
        CalendarRecurrence.Expand(cal, new OccurrenceWindow(from, to)).Where(o => o.Component.RecurrenceId is null).Select(o => o.Start.Value).OrderBy(x => x).ToList();
}
