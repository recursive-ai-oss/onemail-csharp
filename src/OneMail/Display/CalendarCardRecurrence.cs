using System.Globalization;
using RecursiveAI.OneMail.Calendar.Ics;

namespace RecursiveAI.OneMail.Display;

/// <summary>
/// Describes a recurrence rule in plain English (RFC 5545 §3.3.10). A rule is described only when the sentence says exactly what
/// the rule does; anything else (BYHOUR, BYWEEKNO, BYYEARDAY, RSCALE, several BYSETPOS values, ...) gives null and the card
/// shows the fixed "Repeats (custom rule)" text instead of a sentence that could be wrong.
/// </summary>
public static class CalendarCardRecurrence
{
    /// <summary>
    /// The sentence forms (every form is also parsed back by the tests, which is how "never a wrong sentence" is checked):
    /// <c>Daily</c>, <c>Every 3 days</c>, <c>Every weekday</c>, <c>Weekly on Monday and Wednesday</c>, <c>Every 2 weeks on Monday</c>,
    /// <c>Monthly on day 15</c>, <c>Monthly on the last Friday</c>, <c>Monthly on the second to last weekday</c>,
    /// <c>Yearly on 15 March</c>, <c>Yearly on the first Monday of March</c>, <c>Hourly</c>, <c>Every 15 minutes</c>,
    /// each optionally followed by <c>, until 30 Nov 2026</c> or <c>, 10 times</c>.
    /// </summary>
    /// <param name="rule">The rule.</param>
    /// <param name="start">The series' DTSTART, which supplies the weekday, day of month and month a rule leaves out.</param>
    /// <returns>The sentence, or null when the rule is not one of the forms.</returns>
    public static string? Describe(RecurrenceRule rule, IcsTime start)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule.RScale is not null || rule.Skip is not null || rule.OtherParts.Count > 0 || rule.BySecond.Count > 0 || rule.ByMinute.Count > 0
            || rule.ByHour.Count > 0 || rule.ByYearDay.Count > 0 || rule.ByWeekNo.Count > 0 || (rule.Count is not null && rule.Until is not null))
        {
            return null;
        }

        int n = rule.Interval;
        if (n < 1 || n > 1_000_000)
        {
            return null;
        }

        string? body = rule.Frequency switch
        {
            RecurrenceFrequency.Hourly => Sub(rule, n, "hour", "Hourly"),
            RecurrenceFrequency.Minutely => Sub(rule, n, "minute", "Every minute"),
            RecurrenceFrequency.Daily => Daily(rule, n),
            RecurrenceFrequency.Weekly => Weekly(rule, n, start),
            RecurrenceFrequency.Monthly => Monthly(rule, n, start),
            RecurrenceFrequency.Yearly => Yearly(rule, n, start),
            _ => null,
        };
        if (body is null)
        {
            return null;
        }

        if (rule.Count is { } c)
        {
            if (c < 1)
            {
                return null;
            }

            return body + ", " + c.ToString(CultureInfo.InvariantCulture) + (c == 1 ? " time" : " times");
        }

        if (rule.Until is { } u)
        {
            return body + ", until " + CalendarCardFormat.Date(u.Value);
        }

        return body;
    }

    private static bool NoBy(RecurrenceRule r) =>
        r.ByDay.Count == 0 && r.ByMonthDay.Count == 0 && r.ByMonth.Count == 0 && r.BySetPos.Count == 0;

    private static string? Sub(RecurrenceRule r, int n, string unit, string single) =>
        NoBy(r) ? (n == 1 ? single : "Every " + n.ToString(CultureInfo.InvariantCulture) + " " + unit + "s") : null;

    private static string? Daily(RecurrenceRule r, int n)
    {
        if (r.ByMonthDay.Count > 0 || r.ByMonth.Count > 0 || r.BySetPos.Count > 0)
        {
            return null;
        }

        if (r.ByDay.Count == 0)
        {
            return n == 1 ? "Daily" : "Every " + n.ToString(CultureInfo.InvariantCulture) + " days";
        }

        return n == 1 && IsWeekdays(r.ByDay) ? "Every weekday" : null;
    }

    private static bool IsWeekdays(IReadOnlyList<WeekdayNumber> days)
    {
        if (days.Any(d => d.Ordinal is not null))
        {
            return false;
        }

        HashSet<DayOfWeek> set = [.. days.Select(d => d.Day)];
        return set.Count == 5 && set.SetEquals([DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday]);
    }

    private static string? Weekly(RecurrenceRule r, int n, IcsTime start)
    {
        if (r.ByMonthDay.Count > 0 || r.ByMonth.Count > 0 || r.BySetPos.Count > 0 || r.ByDay.Any(d => d.Ordinal is not null))
        {
            return null;
        }

        List<DayOfWeek> days = [.. r.ByDay.Select(d => d.Day).Distinct()];
        if (days.Count == 0)
        {
            days.Add(start.Value.DayOfWeek);
        }

        // The week start decides which days share a "week" when the interval skips weeks (RFC 5545 §3.3.10, WKST). Outlook writes WKST=SU on every
        // rule, so Sunday-start rules are described when no day of the set, nor DTSTART's day, is a Sunday: then Monday-start and Sunday-start weeks
        // group the days alike. Any other week start, or a Sunday in play, is left to the custom-rule text.
        if (n > 1 && r.WeekStart is { } w && w != DayOfWeek.Monday && !(days.Count == 1 && days[0] == start.Value.DayOfWeek))
        {
            if (w != DayOfWeek.Sunday || days.Contains(DayOfWeek.Sunday) || start.Value.DayOfWeek == DayOfWeek.Sunday)
            {
                return null;
            }
        }

        days.Sort((a, b) => ((int)a + 6) % 7 - ((int)b + 6) % 7);
        string list = JoinList(days.Select(d => CalendarCardStrings.Days[(int)d]));
        return (n == 1 ? "Weekly" : "Every " + n.ToString(CultureInfo.InvariantCulture) + " weeks") + " on " + list;
    }

    private static string Every(int n, string unit, string adverb) =>
        n == 1 ? adverb : "Every " + n.ToString(CultureInfo.InvariantCulture) + " " + unit + "s";

    private static string? Ordinal(int o) =>
        o >= 1 && o <= 5 ? CalendarCardStrings.Ordinals[o] : o <= -1 && o >= -5 ? CalendarCardStrings.OrdinalsFromEnd[-o] : null;

    private static string? Monthly(RecurrenceRule r, int n, IcsTime start)
    {
        if (r.ByMonth.Count > 0)
        {
            return null;
        }

        string head = Every(n, "month", "Monthly");
        if (r.ByDay.Count == 0 && r.BySetPos.Count == 0)
        {
            if (r.ByMonthDay.Count == 0)
            {
                return head + " on day " + start.Value.Day.ToString(CultureInfo.InvariantCulture);
            }

            List<string> items = [];
            foreach (int d in r.ByMonthDay.Distinct())
            {
                if (d >= 1 && d <= 31)
                {
                    items.Add("day " + d.ToString(CultureInfo.InvariantCulture));
                }
                else if (d <= -1 && d >= -5)
                {
                    items.Add("the " + CalendarCardStrings.OrdinalsFromEnd[-d] + " day");
                }
                else
                {
                    return null;
                }
            }

            return head + " on " + JoinList(items);
        }

        if (r.ByMonthDay.Count > 0)
        {
            return null;
        }

        if (r.BySetPos.Count == 0)
        {
            List<string> items = [];
            foreach (WeekdayNumber d in r.ByDay.Distinct())
            {
                if (d.Ordinal is not { } o || Ordinal(o) is not { } word)
                {
                    return null;
                }

                items.Add("the " + word + " " + CalendarCardStrings.Days[(int)d.Day]);
            }

            return head + " on " + JoinList(items);
        }

        // RFC 5545 §3.3.10: BYSETPOS picks from the set the BYDAY days make in each month.
        if (r.BySetPos.Count != 1 || r.ByDay.Any(d => d.Ordinal is not null) || Ordinal(r.BySetPos[0]) is not { } pos)
        {
            return null;
        }

        HashSet<DayOfWeek> set = [.. r.ByDay.Select(d => d.Day)];
        string noun;
        if (set.Count == 1)
        {
            noun = CalendarCardStrings.Days[(int)set.First()];
        }
        else if (IsWeekdays(r.ByDay))
        {
            noun = "weekday";
        }
        else if (set.Count == 2 && set.Contains(DayOfWeek.Saturday) && set.Contains(DayOfWeek.Sunday))
        {
            noun = "weekend day";
        }
        else
        {
            return null;
        }

        return head + " on the " + pos + " " + noun;
    }

    private static string? Yearly(RecurrenceRule r, int n, IcsTime start)
    {
        string head = Every(n, "year", "Yearly");
        if (r.BySetPos.Count > 0 || r.ByMonth.Count > 1)
        {
            return null;
        }

        int month = r.ByMonth.Count == 1 ? r.ByMonth[0] : start.Value.Month;
        if (month < 1 || month > 12)
        {
            return null;
        }

        string monthName = CalendarCardStrings.Months[month];
        if (r.ByDay.Count > 0)
        {
            if (r.ByMonthDay.Count > 0 || r.ByMonth.Count == 0)
            {
                return null;
            }

            List<string> items = [];
            foreach (WeekdayNumber d in r.ByDay.Distinct())
            {
                if (d.Ordinal is not { } o || Ordinal(o) is not { } word)
                {
                    return null;
                }

                items.Add("the " + word + " " + CalendarCardStrings.Days[(int)d.Day]);
            }

            return head + " on " + JoinList(items) + " of " + monthName;
        }

        if (r.ByMonthDay.Count == 0)
        {
            return head + " on " + start.Value.Day.ToString(CultureInfo.InvariantCulture) + " " + monthName;
        }

        if (r.ByMonth.Count == 0)
        {
            return null;
        }

        if (r.ByMonthDay.Count == 1 && r.ByMonthDay[0] < 0)
        {
            return r.ByMonthDay[0] is <= -1 and >= -5 ? head + " on the " + CalendarCardStrings.OrdinalsFromEnd[-r.ByMonthDay[0]] + " day of " + monthName : null;
        }

        if (r.ByMonthDay.Any(d => d < 1 || d > 31))
        {
            return null;
        }

        return head + " on " + JoinList(r.ByMonthDay.Distinct().Select(d => d.ToString(CultureInfo.InvariantCulture))) + " " + monthName;
    }

    internal static string JoinList(IEnumerable<string> items)
    {
        List<string> l = [.. items];
        return l.Count switch
        {
            0 => string.Empty,
            1 => l[0],
            _ => string.Join(", ", l.Take(l.Count - 1)) + " and " + l[^1],
        };
    }
}
