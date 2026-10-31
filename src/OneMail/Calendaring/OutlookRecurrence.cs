using System.Globalization;
using System.Text;

namespace RecursiveAI.OneMail.Calendaring;

/// <summary>
/// The days an Outlook recurrence covers, and its mapping to an iCalendar RRULE (MS-OXOCAL 2.2.1.44.1.2 for the valid days,
/// MS-OXCICAL 2.1.3.2.2 for the RRULE templates). Expansion into occurrences is left to the <c>.Calendar</c> recurrence engine;
/// the day walk here only anchors the first instance, counts a series that ends by date, and gives tests an independent check.
/// </summary>
public static class OutlookRecurrence
{
    private static readonly DateTime Epoch = new(1601, 1, 1);

    private static readonly string[] Codes = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];

    /// <summary>The number of days since 1601-01-01.</summary>
    internal static long DayIndex(DateTime date) => (date.Date - Epoch).Days;

    internal static int Weekday(long dayIndex) => (int)((dayIndex + 1) % 7);   // 1601-01-01 was a Monday

    private static long MonthIndex(DateTime d) => ((d.Year - 1601L) * 12) + d.Month - 1;

    private static long Mod(long a, long m) => ((a % m) + m) % m;

    /// <summary>True when the pattern places an instance on this day, ignoring the pattern's start and end and its deleted and modified instances.</summary>
    /// <param name="p">The pattern. Non-Gregorian patterns never match.</param>
    /// <param name="date">The day (time of day ignored).</param>
    /// <returns>Whether <paramref name="date"/> is a valid day of the series by the rules of MS-OXOCAL 2.2.1.44.1.2.</returns>
    public static bool IsValidDay(RecurrencePattern p, DateTime date)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (p.IsNonGregorian || p.Period == 0)
        {
            return false;
        }

        long di = DayIndex(date);
        int wd = Weekday(di);
        switch (p.PatternType)
        {
            case 0:
                return Mod((di * 1440) - p.FirstDateTime, p.Period) == 0;
            case 1:
            {
                long ws = di - Mod(wd - (long)p.FirstDayOfWeek, 7);
                return Mod((ws * 1440) - p.FirstDateTime, p.Period * 10080L) == 0 && (p.WeekdayMask & (1u << wd)) != 0;
            }

            case 2 or 3 or 4:
            {
                long first = MonthIndex(OutlookMinutes.ToDateTime(p.FirstDateTime));
                if (Mod(MonthIndex(date) - first, p.Period) != 0)
                {
                    return false;
                }

                int dim = DateTime.DaysInMonth(date.Year, date.Month);
                return p.PatternType switch
                {
                    2 => date.Day == Math.Min((int)p.DayOfMonth, dim),   // MS-OXOCAL 2.2.1.44.1.5 with the short-month rule of MS-OXCICAL 2.1.3.2.3
                    4 => date.Day == dim,
                    _ => NthDay(p, date.Year, date.Month) == date.Day,
                };
            }

            default:
                return false;
        }
    }

    // MS-OXOCAL 2.2.1.44.1.6: the nth (5 = last) day of the month whose weekday is in the mask.
    private static int NthDay(RecurrencePattern p, int year, int month)
    {
        List<int> days = [];
        for (int d = 1; d <= DateTime.DaysInMonth(year, month); d++)
        {
            if ((p.WeekdayMask & (1u << (int)new DateTime(year, month, d).DayOfWeek)) != 0)
            {
                days.Add(d);
            }
        }

        if (days.Count == 0 || p.Nth == 0)
        {
            return -1;
        }

        return p.Nth >= 5 ? days[^1] : p.Nth <= days.Count ? days[(int)p.Nth - 1] : -1;
    }

    /// <summary>The days of the series in order, honouring its start and end but not the deleted and modified instances.</summary>
    /// <param name="p">The pattern.</param>
    /// <param name="maxDays">How many calendar days to walk before giving up; default 366,000 (about 1,000 years).</param>
    /// <returns>The days, lazily. A series that never ends is infinite up to <paramref name="maxDays"/>.</returns>
    public static IEnumerable<DateTime> Days(RecurrencePattern p, int maxDays = 366_000)
    {
        ArgumentNullException.ThrowIfNull(p);
        DateTime start = OutlookMinutes.ToDateTime(p.StartDate).Date;
        DateTime? last = p.EndType == RecurrencePattern.EndAfterDate ? OutlookMinutes.ToDateTime(p.EndDate).Date : null;
        long count = 0;
        for (int i = 0; i < maxDays; i++)
        {
            DateTime day = start.AddDays(i);
            if (last is { } l && day > l)
            {
                yield break;
            }

            if (day.Year >= 9999)
            {
                yield break;
            }

            if (!IsValidDay(p, day))
            {
                continue;
            }

            yield return day;
            if (p.EndType == RecurrencePattern.EndAfterCount && ++count >= p.OccurrenceCount)
            {
                yield break;
            }
        }
    }

    /// <summary>The first day the series really covers (the start date may sit on a day the pattern skips).</summary>
    /// <param name="p">The pattern.</param>
    /// <returns>The day, or null when the pattern covers none within the walk limit.</returns>
    public static DateTime? FirstDay(RecurrencePattern p) => Days(p, 4000).Cast<DateTime?>().FirstOrDefault();

    /// <summary>
    /// The FirstDateTime a pattern needs so that <paramref name="start"/> is a valid day (MS-OXOCAL 2.2.1.44.1.1): the first day, week or month the
    /// series could have begun on, counted back to 1601 in whole periods. Uses <c>PatternType</c>, <c>Period</c> and <c>FirstDayOfWeek</c>.
    /// </summary>
    /// <param name="p">The pattern with its type, period and first day of week set.</param>
    /// <param name="start">A day the series occupies.</param>
    /// <returns>Minutes since 1601-01-01 00:00.</returns>
    public static uint FirstDateTimeFor(RecurrencePattern p, DateTime start)
    {
        ArgumentNullException.ThrowIfNull(p);
        long di = DayIndex(start);
        long period = Math.Max(1, p.Period);
        switch (p.PatternType)
        {
            case 0:
                return (uint)(Mod(di * 1440, period));
            case 1:
            {
                long ws = di - Mod(Weekday(di) - (long)p.FirstDayOfWeek, 7);
                return (uint)Mod(ws * 1440, period * 10080);
            }

            default:
            {
                long first = Mod(MonthIndex(start), period);
                return OutlookMinutes.FromDateTime(new DateTime(1601 + (int)(first / 12), (int)(first % 12) + 1, 1));
            }
        }
    }

    /// <summary>Whether the pattern can be written as an RRULE.</summary>
    internal static bool CanMap(RecurrencePattern p) => !p.IsNonGregorian && p.RecurFrequency is >= 0x200A and <= 0x200D && p.Period > 0 && p.PatternType is 0 or 1 or 2 or 3 or 4;

    /// <summary>
    /// Writes the series as an RRULE value following the templates of MS-OXCICAL 2.1.3.2.2.
    /// </summary>
    /// <param name="p">The pattern.</param>
    /// <param name="startOffsetMinutes">StartTimeOffset, used to place a series that ends by date.</param>
    /// <param name="untilFormat">Writes the last instance's start (local) as an UNTIL value; the caller knows whether the series is all-day or zoned.</param>
    /// <param name="defects">Receives <see cref="CalendaringDefectKind.AmbiguousShortMonth"/> when the day is 29 or 30.</param>
    /// <returns>The RRULE text, or null when the pattern has no iCalendar form (another calendar, an unknown type).</returns>
    public static string? ToRRule(RecurrencePattern p, uint startOffsetMinutes, Func<DateTime, string> untilFormat, List<CalendaringDefect>? defects = null)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(untilFormat);
        if (!CanMap(p))
        {
            return null;
        }

        StringBuilder sb = new();
        int interval = (int)p.Period;
        switch (p.PatternType)
        {
            case 0:
                sb.Append("FREQ=DAILY");
                AppendInterval(sb, interval / 1440);
                break;
            case 1:
                sb.Append("FREQ=WEEKLY");
                AppendInterval(sb, interval);
                sb.Append(";BYDAY=").Append(string.Join(',', Enumerable.Range(0, 7).Where(i => (p.WeekdayMask & (1u << i)) != 0).Select(i => Codes[i])));
                sb.Append(";WKST=").Append(Codes[(int)Math.Min(p.FirstDayOfWeek, 6)]);   // MS-OXCICAL defaults to SU, RFC 5545 to MO, so it is always written
                break;
            default:
            {
                bool yearly = interval % 12 == 0;
                sb.Append(yearly ? "FREQ=YEARLY" : "FREQ=MONTHLY");
                AppendInterval(sb, yearly ? interval / 12 : interval);
                if (yearly)
                {
                    sb.Append(";BYMONTH=").Append(OutlookMinutes.ToDateTime(p.FirstDateTime).Month.ToString(CultureInfo.InvariantCulture));
                }

                if (p.PatternType == 3)
                {
                    sb.Append(";BYDAY=").Append(string.Join(',', Enumerable.Range(0, 7).Where(i => (p.WeekdayMask & (1u << i)) != 0).Select(i => Codes[i])));
                    sb.Append(";BYSETPOS=").Append(p.Nth >= 5 ? "-1" : p.Nth.ToString(CultureInfo.InvariantCulture));
                }
                else if (p.PatternType == 4 || p.DayOfMonth >= 31)
                {
                    sb.Append(";BYMONTHDAY=-1");
                }
                else
                {
                    sb.Append(";BYMONTHDAY=").Append(p.DayOfMonth.ToString(CultureInfo.InvariantCulture));
                    if (p.DayOfMonth is 29 or 30)
                    {
                        defects?.Add(new CalendaringDefect(CalendaringDefectKind.AmbiguousShortMonth, -1,
                            "monthly day 29 or 30: Outlook uses the last day of a shorter month, RFC 5545 skips the month"));
                    }
                }

                break;
            }
        }

        if (p.EndType == RecurrencePattern.EndAfterCount)
        {
            sb.Append(";COUNT=").Append(p.OccurrenceCount.ToString(CultureInfo.InvariantCulture));
        }
        else if (p.EndType == RecurrencePattern.EndAfterDate)
        {
            // MS-OXCICAL 2.1.3.2.2.1: UNTIL is EndDate plus StartTimeOffset, converted to UTC.
            sb.Append(";UNTIL=").Append(untilFormat(OutlookMinutes.ToDateTime(p.EndDate).Date.AddMinutes(startOffsetMinutes)));
        }

        return sb.ToString();
    }

    private static void AppendInterval(StringBuilder sb, int interval)
    {
        if (interval > 1)
        {
            sb.Append(";INTERVAL=").Append(interval.ToString(CultureInfo.InvariantCulture));
        }
    }
}
