using System.Globalization;
using System.Text;

namespace RecursiveAI.OneMail.Calendar.Ics;

/// <summary>How an iCalendar date or date-time is anchored (RFC 5545 §3.3.4, §3.3.5, §3.3.12).</summary>
public enum IcsTimeKind
{
    /// <summary>A DATE value (<c>20260131</c>): a calendar day with no time.</summary>
    Date = 0,

    /// <summary>A DATE-TIME with no zone (<c>20260131T090000</c>): the same wall-clock time wherever it is read.</summary>
    Floating,

    /// <summary>A DATE-TIME in UTC (<c>20260131T090000Z</c>).</summary>
    Utc,

    /// <summary>A DATE-TIME in the zone named by <see cref="IcsTime.TzId"/> (the <c>TZID</c> parameter). Resolving the zone is not done here.</summary>
    Zoned,
}

/// <summary>A DATE or DATE-TIME value together with how it is anchored. The zone is carried, not resolved.</summary>
/// <param name="Value">The date and wall-clock time as written (time of day is midnight for a DATE). <c>Kind</c> is Utc for UTC values and Unspecified otherwise.</param>
/// <param name="Kind">How the value is anchored.</param>
/// <param name="TzId">The <c>TZID</c> parameter for <see cref="IcsTimeKind.Zoned"/> values, otherwise null.</param>
public readonly record struct IcsTime(DateTime Value, IcsTimeKind Kind, string? TzId = null)
{
    /// <summary>True for a DATE value.</summary>
    public bool IsDate => Kind == IcsTimeKind.Date;

    /// <summary>Parses a DATE or DATE-TIME. A <c>Z</c> suffix wins over <paramref name="tzid"/>. A leap second (<c>60</c>) reads as 59.</summary>
    /// <param name="value">The value text, for example <c>20260131T090000Z</c>.</param>
    /// <param name="tzid">The <c>TZID</c> parameter, or null.</param>
    /// <param name="result">The parsed value.</param>
    /// <returns>False when the text is not a date or date-time.</returns>
    public static bool TryParse(string? value, string? tzid, out IcsTime result)
    {
        result = default;
        if (value is null)
        {
            return false;
        }

        ReadOnlySpan<char> s = value.AsSpan().Trim();
        bool utc = false;
        if (s.Length > 0 && (s[^1] == 'Z' || s[^1] == 'z'))
        {
            utc = true;
            s = s[..^1];
        }

        if (s.Length == 8 && !utc && TryDigits(s, 0, 4, out int y) && TryDigits(s, 4, 2, out int mo) && TryDigits(s, 6, 2, out int d)
            && TryDate(y, mo, d, out DateTime date))
        {
            result = new IcsTime(date, IcsTimeKind.Date);
            return true;
        }

        if (s.Length == 15 && (s[8] == 'T' || s[8] == 't') && TryDigits(s, 0, 4, out y) && TryDigits(s, 4, 2, out mo) && TryDigits(s, 6, 2, out d)
            && TryDigits(s, 9, 2, out int h) && TryDigits(s, 11, 2, out int mi) && TryDigits(s, 13, 2, out int se)
            && TryDate(y, mo, d, out date) && h < 24 && mi < 60 && se <= 60)
        {
            DateTime dt = date.AddHours(h).AddMinutes(mi).AddSeconds(Math.Min(se, 59));
            result = utc ? new IcsTime(DateTime.SpecifyKind(dt, DateTimeKind.Utc), IcsTimeKind.Utc)
                : string.IsNullOrEmpty(tzid) ? new IcsTime(dt, IcsTimeKind.Floating)
                : new IcsTime(dt, IcsTimeKind.Zoned, tzid);
            return true;
        }

        return false;
    }

    internal static bool TryDigits(ReadOnlySpan<char> s, int start, int len, out int value)
    {
        value = 0;
        if (start + len > s.Length)
        {
            return false;
        }

        for (int i = start; i < start + len; i++)
        {
            if (s[i] is < '0' or > '9')
            {
                return false;
            }

            value = (value * 10) + (s[i] - '0');
        }

        return true;
    }

    private static bool TryDate(int y, int m, int d, out DateTime date)
    {
        date = default;
        if (y < 1 || m is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, m))
        {
            return false;
        }

        date = new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Unspecified);
        return true;
    }

    /// <summary>Formats the value as iCalendar text (the <c>TZID</c> parameter is not part of it).</summary>
    /// <returns>For example <c>20260131T090000Z</c>.</returns>
    public override string ToString() => Kind == IcsTimeKind.Date
        ? Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
        : Value.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture) + (Kind == IcsTimeKind.Utc ? "Z" : string.Empty);
}

/// <summary>A DURATION value (RFC 5545 §3.3.6), kept in the units it was written in: <c>P1W</c> stays one week, <c>PT90M</c> stays 90 minutes.</summary>
/// <param name="Negative">True for a leading minus.</param>
/// <param name="Weeks">Weeks (a duration with weeks has no other part in the grammar, but lenient input may mix them).</param>
/// <param name="Days">Days.</param>
/// <param name="Hours">Hours.</param>
/// <param name="Minutes">Minutes.</param>
/// <param name="Seconds">Seconds.</param>
public readonly record struct IcsDuration(bool Negative, int Weeks, int Days, int Hours, int Minutes, int Seconds)
{
    /// <summary>The length as a <see cref="TimeSpan"/> (a day counts 24 hours; nominal durations are the caller's concern).</summary>
    public TimeSpan ToTimeSpan()
    {
        TimeSpan t = new((Weeks * 7) + Days, Hours, Minutes, Seconds);
        return Negative ? -t : t;
    }

    /// <summary>Parses a duration such as <c>-PT15M</c>, <c>P1DT2H</c> or <c>P2W</c>.</summary>
    /// <param name="value">The text.</param>
    /// <param name="result">The duration.</param>
    /// <returns>False when the text is not a duration.</returns>
    public static bool TryParse(string? value, out IcsDuration result)
    {
        result = default;
        if (value is null)
        {
            return false;
        }

        ReadOnlySpan<char> s = value.AsSpan().Trim();
        bool neg = false;
        if (s.Length > 0 && (s[0] == '-' || s[0] == '+'))
        {
            neg = s[0] == '-';
            s = s[1..];
        }

        if (s.Length < 2 || (s[0] != 'P' && s[0] != 'p'))
        {
            return false;
        }

        s = s[1..];
        int w = 0, d = 0, h = 0, m = 0, sec = 0;
        bool inTime = false;
        bool any = false;
        int num = -1;
        foreach (char c in s)
        {
            if (c is >= '0' and <= '9')
            {
                num = num < 0 ? c - '0' : checked((num * 10) + (c - '0'));
                if (num > 100_000_000)
                {
                    return false;
                }

                continue;
            }

            char u = char.ToUpperInvariant(c);
            if (u == 'T' && num < 0 && !inTime)
            {
                inTime = true;
                continue;
            }

            if (num < 0)
            {
                return false;
            }

            switch (u)
            {
                case 'W' when !inTime: w = num; break;
                case 'D' when !inTime: d = num; break;
                case 'H' when inTime: h = num; break;
                case 'M' when inTime: m = num; break;
                case 'S' when inTime: sec = num; break;
                default: return false;
            }

            any = true;
            num = -1;
        }

        if (num >= 0 || !any)
        {
            return false;
        }

        result = new IcsDuration(neg, w, d, h, m, sec);
        return true;
    }

    /// <summary>Formats as iCalendar text, in the stored units.</summary>
    /// <returns>For example <c>-PT15M</c>.</returns>
    public override string ToString()
    {
        StringBuilder sb = new();
        if (Negative)
        {
            sb.Append('-');
        }

        sb.Append('P');
        if (Weeks != 0)
        {
            sb.Append(Weeks.ToString(CultureInfo.InvariantCulture)).Append('W');
        }

        if (Days != 0)
        {
            sb.Append(Days.ToString(CultureInfo.InvariantCulture)).Append('D');
        }

        if (Hours != 0 || Minutes != 0 || Seconds != 0)
        {
            sb.Append('T');
            if (Hours != 0)
            {
                sb.Append(Hours.ToString(CultureInfo.InvariantCulture)).Append('H');
            }

            if (Minutes != 0)
            {
                sb.Append(Minutes.ToString(CultureInfo.InvariantCulture)).Append('M');
            }

            if (Seconds != 0)
            {
                sb.Append(Seconds.ToString(CultureInfo.InvariantCulture)).Append('S');
            }
        }

        if (sb.Length == (Negative ? 2 : 1))
        {
            sb.Append("T0S");
        }

        return sb.ToString();
    }
}

/// <summary>A PERIOD value (RFC 5545 §3.3.9): a start and either an end or a duration.</summary>
/// <param name="Start">The start.</param>
/// <param name="End">The end, when written as start/end.</param>
/// <param name="Duration">The duration, when written as start/duration.</param>
public readonly record struct IcsPeriod(IcsTime Start, IcsTime? End, IcsDuration? Duration)
{
    /// <summary>Parses <c>start/end</c> or <c>start/duration</c>.</summary>
    /// <param name="value">The text.</param>
    /// <param name="tzid">The <c>TZID</c> parameter for floating times, or null.</param>
    /// <param name="result">The period.</param>
    /// <returns>False when the text is not a period.</returns>
    public static bool TryParse(string? value, string? tzid, out IcsPeriod result)
    {
        result = default;
        value = value?.Trim();
        int slash = value?.IndexOf('/', StringComparison.Ordinal) ?? -1;
        if (value is null || slash <= 0 || !IcsTime.TryParse(value[..slash], tzid, out IcsTime start))
        {
            return false;
        }

        string rest = value[(slash + 1)..];
        if (IcsDuration.TryParse(rest, out IcsDuration dur))
        {
            result = new IcsPeriod(start, null, dur);
            return true;
        }

        if (IcsTime.TryParse(rest, tzid, out IcsTime end))
        {
            result = new IcsPeriod(start, end, null);
            return true;
        }

        return false;
    }

    /// <summary>Formats as <c>start/end</c> or <c>start/duration</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => Start + "/" + (End is { } e ? e.ToString() : Duration?.ToString());
}

/// <summary>A UTC-OFFSET value (RFC 5545 §3.3.14), for example <c>-0500</c> or <c>+053000</c>.</summary>
/// <param name="Negative">True for a minus sign. <c>-0000</c> (invalid in the RFC) reads as negative zero.</param>
/// <param name="Hours">Hours.</param>
/// <param name="Minutes">Minutes.</param>
/// <param name="Seconds">Seconds, 0 when absent.</param>
public readonly record struct IcsUtcOffset(bool Negative, int Hours, int Minutes, int Seconds)
{
    /// <summary>The offset as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan ToTimeSpan()
    {
        TimeSpan t = new(Hours, Minutes, Seconds);
        return Negative ? -t : t;
    }

    /// <summary>Parses an offset of the form <c>±HHMM</c> or <c>±HHMMSS</c>.</summary>
    /// <param name="value">The text.</param>
    /// <param name="result">The offset.</param>
    /// <returns>False when the text is not an offset.</returns>
    public static bool TryParse(string? value, out IcsUtcOffset result)
    {
        result = default;
        ReadOnlySpan<char> s = value.AsSpan().Trim();
        if (s.Length is not (5 or 7) || (s[0] != '+' && s[0] != '-')
            || !IcsTime.TryDigits(s, 1, 2, out int h) || !IcsTime.TryDigits(s, 3, 2, out int m))
        {
            return false;
        }

        int sec = 0;
        if (s.Length == 7 && !IcsTime.TryDigits(s, 5, 2, out sec))
        {
            return false;
        }

        if (m > 59 || sec > 59)
        {
            return false;
        }

        result = new IcsUtcOffset(s[0] == '-', h, m, sec);
        return true;
    }

    /// <summary>Formats as <c>±HHMM</c>, or <c>±HHMMSS</c> when there are seconds.</summary>
    /// <returns>The text.</returns>
    public override string ToString() =>
        (Negative ? "-" : "+") + Hours.ToString("00", CultureInfo.InvariantCulture) + Minutes.ToString("00", CultureInfo.InvariantCulture)
        + (Seconds != 0 ? Seconds.ToString("00", CultureInfo.InvariantCulture) : string.Empty);
}

/// <summary>The FREQ part of a recurrence rule.</summary>
public enum RecurrenceFrequency
{
    /// <summary>SECONDLY.</summary>
    Secondly = 1,

    /// <summary>MINUTELY.</summary>
    Minutely,

    /// <summary>HOURLY.</summary>
    Hourly,

    /// <summary>DAILY.</summary>
    Daily,

    /// <summary>WEEKLY.</summary>
    Weekly,

    /// <summary>MONTHLY.</summary>
    Monthly,

    /// <summary>YEARLY.</summary>
    Yearly,
}

/// <summary>A BYDAY entry: a weekday with an optional ordinal (<c>-1SU</c> is the last Sunday).</summary>
/// <param name="Ordinal">The ordinal, or null for every such weekday.</param>
/// <param name="Day">The weekday.</param>
public readonly record struct WeekdayNumber(int? Ordinal, DayOfWeek Day)
{
    private static readonly string[] Codes = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];

    internal static bool TryParse(string s, out WeekdayNumber result)
    {
        result = default;
        s = s.Trim();
        if (s.Length < 2)
        {
            return false;
        }

        int dayIndex = Array.IndexOf(Codes, s[^2..].ToUpperInvariant());
        if (dayIndex < 0)
        {
            return false;
        }

        int? ord = null;
        if (s.Length > 2)
        {
            if (!int.TryParse(s[..^2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int o) || o == 0 || Math.Abs(o) > 53)
            {
                return false;
            }

            ord = o;
        }

        result = new WeekdayNumber(ord, (DayOfWeek)dayIndex);
        return true;
    }

    /// <summary>Formats as iCalendar text, for example <c>-1SU</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => (Ordinal?.ToString(CultureInfo.InvariantCulture) ?? string.Empty) + Codes[(int)Day];
}

/// <summary>
/// A parsed RECUR value (RFC 5545 §3.3.10; RFC 7529 <c>RSCALE</c>/<c>SKIP</c> accepted). Only the data is parsed here;
/// expansion is a separate concern. Unknown parts are kept in <see cref="OtherParts"/>.
/// </summary>
public sealed class RecurrenceRule
{
    /// <summary>FREQ.</summary>
    public RecurrenceFrequency Frequency { get; private set; }

    /// <summary>UNTIL, a DATE or DATE-TIME; null when absent.</summary>
    public IcsTime? Until { get; private set; }

    /// <summary>COUNT; null when absent.</summary>
    public int? Count { get; private set; }

    /// <summary>INTERVAL; 1 when absent.</summary>
    public int Interval { get; private set; } = 1;

    /// <summary>BYSECOND.</summary>
    public IReadOnlyList<int> BySecond { get; private set; } = [];

    /// <summary>BYMINUTE.</summary>
    public IReadOnlyList<int> ByMinute { get; private set; } = [];

    /// <summary>BYHOUR.</summary>
    public IReadOnlyList<int> ByHour { get; private set; } = [];

    /// <summary>BYDAY.</summary>
    public IReadOnlyList<WeekdayNumber> ByDay { get; private set; } = [];

    /// <summary>BYMONTHDAY.</summary>
    public IReadOnlyList<int> ByMonthDay { get; private set; } = [];

    /// <summary>BYYEARDAY.</summary>
    public IReadOnlyList<int> ByYearDay { get; private set; } = [];

    /// <summary>BYWEEKNO.</summary>
    public IReadOnlyList<int> ByWeekNo { get; private set; } = [];

    /// <summary>BYMONTH. A leap month in RFC 7529 form (<c>5L</c>) is kept in <see cref="OtherParts"/> instead.</summary>
    public IReadOnlyList<int> ByMonth { get; private set; } = [];

    /// <summary>BYSETPOS.</summary>
    public IReadOnlyList<int> BySetPos { get; private set; } = [];

    /// <summary>WKST; null when absent (the RFC default is Monday).</summary>
    public DayOfWeek? WeekStart { get; private set; }

    /// <summary>RSCALE (RFC 7529), or null.</summary>
    public string? RScale { get; private set; }

    /// <summary>SKIP (RFC 7529), or null.</summary>
    public string? Skip { get; private set; }

    /// <summary>Parts this parser does not know, as written, in order.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> OtherParts { get; private set; } = [];

    /// <summary>Parses a recurrence rule. Each part may appear once; part names are case-insensitive.</summary>
    /// <param name="value">The RECUR text, for example <c>FREQ=WEEKLY;BYDAY=MO,WE</c>.</param>
    /// <param name="tzid">The <c>TZID</c> of the owning property, used to read a date-time UNTIL, or null.</param>
    /// <param name="rule">The rule.</param>
    /// <returns>False when FREQ is missing or invalid or a number is malformed.</returns>
    public static bool TryParse(string? value, string? tzid, out RecurrenceRule rule)
    {
        rule = new RecurrenceRule();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        bool haveFreq = false;
        List<KeyValuePair<string, string>> other = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string part in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                return false;
            }

            string key = part[..eq].Trim().ToUpperInvariant();
            string v = part[(eq + 1)..].Trim();
            if (!seen.Add(key))
            {
                return false;
            }

            switch (key)
            {
                case "FREQ":
                    if (!Enum.TryParse(v, true, out RecurrenceFrequency f) || !Enum.IsDefined(f))
                    {
                        return false;
                    }

                    rule.Frequency = f;
                    haveFreq = true;
                    break;
                case "UNTIL":
                    if (!IcsTime.TryParse(v, tzid, out IcsTime u))
                    {
                        return false;
                    }

                    rule.Until = u;
                    break;
                case "COUNT":
                    if (!int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out int c))
                    {
                        return false;
                    }

                    rule.Count = c;
                    break;
                case "INTERVAL":
                    if (!int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out int i) || i < 1)
                    {
                        return false;
                    }

                    rule.Interval = i;
                    break;
                case "BYSECOND": if (!Ints(v, out int[] a0)) { return false; } rule.BySecond = a0; break;
                case "BYMINUTE": if (!Ints(v, out int[] a1)) { return false; } rule.ByMinute = a1; break;
                case "BYHOUR": if (!Ints(v, out int[] a2)) { return false; } rule.ByHour = a2; break;
                case "BYMONTHDAY": if (!Ints(v, out int[] a3)) { return false; } rule.ByMonthDay = a3; break;
                case "BYYEARDAY": if (!Ints(v, out int[] a4)) { return false; } rule.ByYearDay = a4; break;
                case "BYWEEKNO": if (!Ints(v, out int[] a5)) { return false; } rule.ByWeekNo = a5; break;
                case "BYSETPOS": if (!Ints(v, out int[] a6)) { return false; } rule.BySetPos = a6; break;
                case "BYMONTH":
                    if (Ints(v, out int[] a7))
                    {
                        rule.ByMonth = a7;
                    }
                    else
                    {
                        other.Add(new(key, v)); // RFC 7529 leap months such as 5L
                    }

                    break;
                case "BYDAY":
                    List<WeekdayNumber> days = [];
                    foreach (string d in v.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!WeekdayNumber.TryParse(d, out WeekdayNumber wd))
                        {
                            return false;
                        }

                        days.Add(wd);
                    }

                    rule.ByDay = days;
                    break;
                case "WKST":
                    if (!WeekdayNumber.TryParse(v, out WeekdayNumber ws) || ws.Ordinal is not null)
                    {
                        return false;
                    }

                    rule.WeekStart = ws.Day;
                    break;
                case "RSCALE": rule.RScale = v; break;
                case "SKIP": rule.Skip = v; break;
                default: other.Add(new(key, v)); break;
            }
        }

        rule.OtherParts = other;
        return haveFreq;
    }

    private static bool Ints(string v, out int[] values)
    {
        string[] parts = v.Split(',', StringSplitOptions.RemoveEmptyEntries);
        values = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out values[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Formats the rule in a canonical part order.</summary>
    /// <returns>The RECUR text.</returns>
    public override string ToString()
    {
        List<string> p = ["FREQ=" + Frequency.ToString().ToUpperInvariant()];
        if (RScale is not null) { p.Insert(0, "RSCALE=" + RScale); }
        if (Skip is not null) { p.Add("SKIP=" + Skip); }
        if (Until is { } u) { p.Add("UNTIL=" + u); }
        if (Count is { } c) { p.Add("COUNT=" + c.ToString(CultureInfo.InvariantCulture)); }
        if (Interval != 1) { p.Add("INTERVAL=" + Interval.ToString(CultureInfo.InvariantCulture)); }
        AddList(p, "BYSECOND", BySecond);
        AddList(p, "BYMINUTE", ByMinute);
        AddList(p, "BYHOUR", ByHour);
        if (ByDay.Count > 0) { p.Add("BYDAY=" + string.Join(',', ByDay)); }
        AddList(p, "BYMONTHDAY", ByMonthDay);
        AddList(p, "BYYEARDAY", ByYearDay);
        AddList(p, "BYWEEKNO", ByWeekNo);
        AddList(p, "BYMONTH", ByMonth);
        AddList(p, "BYSETPOS", BySetPos);
        if (WeekStart is { } w) { p.Add("WKST=" + new WeekdayNumber(null, w)); }
        foreach (KeyValuePair<string, string> kv in OtherParts) { p.Add(kv.Key + "=" + kv.Value); }
        return string.Join(';', p);
    }

    private static void AddList(List<string> p, string name, IReadOnlyList<int> v)
    {
        if (v.Count > 0)
        {
            p.Add(name + "=" + string.Join(',', v.Select(x => x.ToString(CultureInfo.InvariantCulture))));
        }
    }
}
