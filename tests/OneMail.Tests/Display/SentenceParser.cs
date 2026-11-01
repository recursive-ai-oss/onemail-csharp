using System.Globalization;
using RecursiveAI.OneMail.Calendar.Ics;

namespace RecursiveAI.OneMail.Tests.Display;

/// <summary>
/// A small parser of the calendar card's recurrence sentences, written from the sentence forms alone (not from the generator), and the matching
/// normaliser of an RRULE. A described rule is right when both give the same key: frequency, interval, weekday set (with ordinals), days of
/// the month, month, count and until date.
/// </summary>
public static class SentenceParser
{
    private static readonly string[] Days = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
    private static readonly string[] Codes = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];
    private static readonly string[] Months = ["", "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];
    private static readonly string[] ShortMonths = ["", "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
    private static readonly string[] Ord = ["", "first", "second", "third", "fourth", "fifth"];
    private static readonly string[] OrdEnd = ["", "last", "second to last", "third to last", "fourth to last", "fifth to last"];
    private const string Weekdays = "MO,TU,WE,TH,FR";

    private static string Key(string freq, int interval, IEnumerable<string>? days, IEnumerable<int>? monthDays, int? month, int? count, DateTime? until) =>
        $"{freq}/{interval}/d={string.Join(",", (days ?? []).OrderBy(x => x, StringComparer.Ordinal))}/md={string.Join(",", (monthDays ?? []).OrderBy(x => x))}/m={month}/c={count}/u={until:yyyyMMdd}";

    /// <summary>The key of a rule, as the sentence for it should read back. <paramref name="start"/> supplies what the rule leaves out.</summary>
    public static string? KeyOfRule(RecurrenceRule r, IcsTime start)
    {
        DateTime? until = r.Until?.Value.Date;
        string f = r.Frequency.ToString().ToUpperInvariant();
        switch (r.Frequency)
        {
            case RecurrenceFrequency.Hourly:
            case RecurrenceFrequency.Minutely:
                return Key(f, r.Interval, null, null, null, r.Count, until);
            case RecurrenceFrequency.Daily:
                return Key(f, r.Interval, r.ByDay.Count > 0 ? Weekdays.Split(',') : null, null, null, r.Count, until);
            case RecurrenceFrequency.Weekly:
                return Key(f, r.Interval, r.ByDay.Count > 0 ? r.ByDay.Select(d => Codes[(int)d.Day]).Distinct() : [Codes[(int)start.Value.DayOfWeek]], null, null, r.Count, until);
            case RecurrenceFrequency.Monthly:
                if (r.ByDay.Count == 0 && r.ByMonthDay.Count == 0)
                {
                    return Key(f, r.Interval, null, [start.Value.Day], null, r.Count, until);
                }

                if (r.BySetPos.Count == 1)
                {
                    int p = r.BySetPos[0];
                    string set = string.Join(",", r.ByDay.Select(d => Codes[(int)d.Day]).Distinct().OrderBy(x => x, StringComparer.Ordinal));
                    return Key(f, r.Interval, [SetName(set, p)], null, null, r.Count, until);
                }

                return Key(f, r.Interval, r.ByDay.Select(d => (d.Ordinal ?? 0) + d.Day.ToString()), r.ByMonthDay, null, r.Count, until);
            case RecurrenceFrequency.Yearly:
                int month = r.ByMonth.Count == 1 ? r.ByMonth[0] : start.Value.Month;
                return r.ByDay.Count > 0
                    ? Key(f, r.Interval, r.ByDay.Select(d => (d.Ordinal ?? 0) + d.Day.ToString()), null, month, r.Count, until)
                    : Key(f, r.Interval, null, r.ByMonthDay.Count > 0 ? r.ByMonthDay : [start.Value.Day], month, r.Count, until);
            default:
                return null;
        }
    }

    // BYDAY=FR;BYSETPOS=-1 is the last Friday; weekday and weekend sets keep a set name.
    private static string SetName(string set, int pos)
    {
        string[] codes = set.Split(',');
        if (codes.Length == 1)
        {
            return pos + Days[Array.IndexOf(Codes, codes[0])];
        }

        return set == "FR,MO,TH,TU,WE" ? pos + "weekday" : set == "SA,SU" ? pos + "weekend" : pos + "?" + set;
    }

    private static int OrdinalOf(string w)
    {
        int i = Array.IndexOf(Ord, w);
        if (i > 0)
        {
            return i;
        }

        i = Array.IndexOf(OrdEnd, w);
        return i > 0 ? -i : 0;
    }

    /// <summary>Parses a sentence into a key, or null if it is not one of the forms.</summary>
    public static string? Parse(string text)
    {
        // The exception and changed-occurrence notes follow the sentence in parentheses.
        int paren = text.IndexOf(" (", StringComparison.Ordinal);
        if (paren >= 0)
        {
            text = text[..paren];
        }

        int? count = null;
        DateTime? until = null;
        int comma = text.LastIndexOf(", ", StringComparison.Ordinal);
        if (comma >= 0)
        {
            string tail = text[(comma + 2)..];
            if (tail.StartsWith("until ", StringComparison.Ordinal))
            {
                string[] p = tail[6..].Split(' ');
                if (p.Length != 3 || !int.TryParse(p[0], out int d) || Array.IndexOf(ShortMonths, p[1]) < 1 || !int.TryParse(p[2], out int y))
                {
                    return null;
                }

                until = new DateTime(y, Array.IndexOf(ShortMonths, p[1]), d);
            }
            else if (tail.EndsWith(" times", StringComparison.Ordinal) || tail.EndsWith(" time", StringComparison.Ordinal))
            {
                if (!int.TryParse(tail.Split(' ')[0], out int c))
                {
                    return null;
                }

                count = c;
            }
            else
            {
                comma = -1;
            }

            if (comma >= 0)
            {
                text = text[..comma];
            }
        }

        switch (text)
        {
            case "Hourly": return Key("HOURLY", 1, null, null, null, count, until);
            case "Every minute": return Key("MINUTELY", 1, null, null, null, count, until);
            case "Daily": return Key("DAILY", 1, null, null, null, count, until);
            case "Every weekday": return Key("DAILY", 1, Weekdays.Split(','), null, null, count, until);
        }

        string head = text;
        string? rest = null;
        int on = text.IndexOf(" on ", StringComparison.Ordinal);
        if (on >= 0)
        {
            head = text[..on];
            rest = text[(on + 4)..];
        }

        string freq;
        int interval = 1;
        if (head is "Weekly" or "Monthly" or "Yearly")
        {
            freq = head.ToUpperInvariant();
        }
        else if (head.StartsWith("Every ", StringComparison.Ordinal))
        {
            string[] p = head.Split(' ');
            if (p.Length != 3 || !int.TryParse(p[1], out interval) || interval < 2)
            {
                return null;
            }

            freq = p[2] switch { "hours" => "HOURLY", "minutes" => "MINUTELY", "days" => "DAILY", "weeks" => "WEEKLY", "months" => "MONTHLY", "years" => "YEARLY", _ => "" };
            if (freq.Length == 0)
            {
                return null;
            }
        }
        else
        {
            return null;
        }

        if (rest is null)
        {
            return freq is "HOURLY" or "MINUTELY" or "DAILY" ? Key(freq, interval, null, null, null, count, until) : null;
        }

        string[] items = rest.Replace(" and ", ", ", StringComparison.Ordinal).Split(", ");
        switch (freq)
        {
            case "WEEKLY":
                List<string> codes = [];
                foreach (string it in items)
                {
                    int di = Array.IndexOf(Days, it);
                    if (di < 0)
                    {
                        return null;
                    }

                    codes.Add(Codes[di]);
                }

                return Key(freq, interval, codes, null, null, count, until);
            case "MONTHLY":
                return Monthly(freq, interval, items, count, until);
            case "YEARLY":
                return Yearly(freq, interval, rest, count, until);
            default:
                return null;
        }
    }

    private static string? Monthly(string freq, int interval, string[] items, int? count, DateTime? until)
    {
        List<int> md = [];
        List<string> days = [];
        foreach (string it in items)
        {
            if (it.StartsWith("day ", StringComparison.Ordinal) && int.TryParse(it[4..], out int d))
            {
                md.Add(d);
            }
            else if (it.StartsWith("the ", StringComparison.Ordinal) && it.EndsWith(" day", StringComparison.Ordinal) && !it.EndsWith("weekend day", StringComparison.Ordinal))
            {
                int o = OrdinalOf(it[4..^4]);
                if (o >= 0)
                {
                    return null;
                }

                md.Add(o);
            }
            else if (it.StartsWith("the ", StringComparison.Ordinal))
            {
                string body = it[4..];
                int sp = -1;
                int o = 0;
                foreach (string w in OrdEnd.Skip(1).Concat(Ord.Skip(1)).OrderByDescending(x => x.Length))
                {
                    if (body.StartsWith(w + " ", StringComparison.Ordinal))
                    {
                        o = OrdinalOf(w);
                        sp = w.Length + 1;
                        break;
                    }
                }

                if (sp < 0)
                {
                    return null;
                }

                string noun = body[sp..];
                int di = Array.IndexOf(Days, noun);
                if (di >= 0)
                {
                    days.Add(o + Days[di]);
                }
                else if (noun == "weekday" || noun == "weekend day")
                {
                    days.Add(o + (noun == "weekday" ? "weekday" : "weekend"));
                }
                else
                {
                    return null;
                }
            }
            else
            {
                return null;
            }
        }

        return Key(freq, interval, days, md, null, count, until);
    }

    private static string? Yearly(string freq, int interval, string rest, int? count, DateTime? until)
    {
        // "15 March", "1 and 15 March", "the last day of March", "the first Monday of March"
        int of = rest.LastIndexOf(" of ", StringComparison.Ordinal);
        if (of >= 0)
        {
            int m = Array.IndexOf(Months, rest[(of + 4)..]);
            string[] items = rest[..of].Replace(" and ", ", ", StringComparison.Ordinal).Split(", ");
            if (m < 1)
            {
                return null;
            }

            if (items.Length == 1 && items[0].EndsWith(" day", StringComparison.Ordinal))
            {
                int o = OrdinalOf(items[0][4..^4]);
                return o < 0 ? Key(freq, interval, null, [o], m, count, until) : null;
            }

            string? mk = Monthly("MONTHLY", interval, items, count, until);
            if (mk is null)
            {
                return null;
            }

            // Reuse the monthly day parse, then move it to the yearly key shape.
            List<string> days = [];
            foreach (string it in items)
            {
                string body = it[4..];
                string[] words = body.Split(' ');
                int dayIdx = Array.IndexOf(Days, words[^1]);
                if (dayIdx < 0)
                {
                    return null;
                }

                days.Add(OrdinalOf(string.Join(' ', words[..^1])) + Days[dayIdx]);
            }

            return Key(freq, interval, days, null, m, count, until);
        }

        int last = rest.LastIndexOf(' ');
        if (last < 0)
        {
            return null;
        }

        int month = Array.IndexOf(Months, rest[(last + 1)..]);
        if (month < 1)
        {
            return null;
        }

        List<int> md = [];
        foreach (string it in rest[..last].Replace(" and ", ", ", StringComparison.Ordinal).Split(", "))
        {
            if (!int.TryParse(it, NumberStyles.None, CultureInfo.InvariantCulture, out int d))
            {
                return null;
            }

            md.Add(d);
        }

        return Key(freq, interval, null, md, month, count, until);
    }
}
