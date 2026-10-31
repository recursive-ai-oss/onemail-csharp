using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using RecursiveAI.OneMail.Calendar;

namespace RecursiveAI.OneMail.Calendaring;

/// <summary>A Windows SYSTEMTIME (MS-DTYP 2.3.13) as used by the time zone structures: eight little-endian 16-bit fields.</summary>
/// <param name="Year">Zero for a yearly rule; a year for a date that happens once.</param>
/// <param name="Month">1 to 12; zero means "this time zone has no daylight saving time".</param>
/// <param name="DayOfWeek">0 (Sunday) to 6; used by yearly rules.</param>
/// <param name="Day">For a yearly rule the occurrence of <paramref name="DayOfWeek"/> in the month (1 to 4, 5 = the last); for a single date the day of the month.</param>
/// <param name="Hour">Local hour of the change.</param>
/// <param name="Minute">Local minute of the change.</param>
/// <param name="Second">Second of the change.</param>
/// <param name="Milliseconds">Milliseconds; ignored.</param>
public readonly record struct SystemTime(ushort Year, ushort Month, ushort DayOfWeek, ushort Day, ushort Hour, ushort Minute, ushort Second, ushort Milliseconds)
{
    /// <summary>The size on the wire: 16 bytes.</summary>
    public const int Size = 16;

    /// <summary>True when the rule names no change (month zero).</summary>
    public bool IsUnset => Month == 0;

    internal static SystemTime Read(ReadOnlySpan<byte> s) => new(
        BinaryPrimitives.ReadUInt16LittleEndian(s), BinaryPrimitives.ReadUInt16LittleEndian(s[2..]), BinaryPrimitives.ReadUInt16LittleEndian(s[4..]),
        BinaryPrimitives.ReadUInt16LittleEndian(s[6..]), BinaryPrimitives.ReadUInt16LittleEndian(s[8..]), BinaryPrimitives.ReadUInt16LittleEndian(s[10..]),
        BinaryPrimitives.ReadUInt16LittleEndian(s[12..]), BinaryPrimitives.ReadUInt16LittleEndian(s[14..]));

    internal void Write(Span<byte> s)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(s, Year);
        BinaryPrimitives.WriteUInt16LittleEndian(s[2..], Month);
        BinaryPrimitives.WriteUInt16LittleEndian(s[4..], DayOfWeek);
        BinaryPrimitives.WriteUInt16LittleEndian(s[6..], Day);
        BinaryPrimitives.WriteUInt16LittleEndian(s[8..], Hour);
        BinaryPrimitives.WriteUInt16LittleEndian(s[10..], Minute);
        BinaryPrimitives.WriteUInt16LittleEndian(s[12..], Second);
        BinaryPrimitives.WriteUInt16LittleEndian(s[14..], Milliseconds);
    }
}

/// <summary>One time zone rule: the offsets and the yearly changes in force from a year on (MS-OXOCAL 2.2.1.41.1 TZRule, and the body of the 48-byte PidLidTimeZoneStruct 2.2.1.39).</summary>
/// <param name="Flags">TZRULE_FLAG_RECUR_CURRENT_TZREG (0x0001) and TZRULE_FLAG_EFFECTIVE_TZREG (0x0002); zero for a rule read from a PidLidTimeZoneStruct.</param>
/// <param name="Year">The year the rule takes effect, on 1 January.</param>
/// <param name="Bias">Minutes to add to local time to get UTC (so UTC-8 is +480).</param>
/// <param name="StandardBias">Minutes added to <paramref name="Bias"/> during standard time (usually 0).</param>
/// <param name="DaylightBias">Minutes added to <paramref name="Bias"/> during daylight saving time (usually -60).</param>
/// <param name="StandardDate">When standard time begins, in local daylight time.</param>
/// <param name="DaylightDate">When daylight saving time begins, in local standard time.</param>
public sealed record TzRule(ushort Flags, ushort Year, int Bias, int StandardBias, int DaylightBias, SystemTime StandardDate, SystemTime DaylightDate)
{
    /// <summary>Size of a TZRule on the wire: 66 bytes.</summary>
    public const int Size = 66;

    /// <summary>True when this is the rule that converts times to and from UTC.</summary>
    public bool IsEffective => (Flags & 0x0002) != 0;

    /// <summary>True when the rule has daylight saving time (both changes are set).</summary>
    public bool HasDaylight => !StandardDate.IsUnset && !DaylightDate.IsUnset;

    /// <summary>The UTC offset during standard time.</summary>
    public TimeSpan StandardOffset => TimeSpan.FromMinutes(-(long)Bias - StandardBias);

    /// <summary>The UTC offset during daylight saving time.</summary>
    public TimeSpan DaylightOffset => TimeSpan.FromMinutes(-(long)Bias - DaylightBias);

    /// <summary>True when the two rules agree on the fields MS-OXOCAL 2.2.1.41 says must match between the definition and the struct.</summary>
    public bool SameAs(TzRule other) =>
        Bias == other.Bias && StandardBias == other.StandardBias && DaylightBias == other.DaylightBias
        && StandardDate == other.StandardDate && DaylightDate == other.DaylightDate;
}

/// <summary>PidLidTimeZoneStruct (MS-OXOCAL 2.2.1.39): the time zone a recurring series was made in, as one rule without a year.</summary>
public static class TimeZoneStruct
{
    /// <summary>The wire size: 48 bytes.</summary>
    public const int Size = 48;

    /// <summary>Reads the structure.</summary>
    /// <param name="data">The property value.</param>
    /// <param name="rule">The rule (flags zero, year zero), or null when <paramref name="data"/> is shorter than 48 bytes.</param>
    /// <param name="defects">Receives <see cref="CalendaringDefectKind.Truncated"/> or <see cref="CalendaringDefectKind.TrailingBytes"/>; may be null.</param>
    /// <returns>True when a rule was read. Never throws.</returns>
    public static bool TryParse(ReadOnlySpan<byte> data, out TzRule? rule, List<CalendaringDefect>? defects = null)
    {
        if (data.Length < Size)
        {
            defects?.Add(new CalendaringDefect(CalendaringDefectKind.Truncated, data.Length, "PidLidTimeZoneStruct is shorter than 48 bytes"));
            rule = null;
            return false;
        }

        if (data.Length > Size)
        {
            defects?.Add(new CalendaringDefect(CalendaringDefectKind.TrailingBytes, Size, "PidLidTimeZoneStruct has bytes after the 48 it defines"));
        }

        // wStandardYear and wDaylightYear repeat the year of the SYSTEMTIME that follows each; the SYSTEMTIME is the one read.
        rule = new TzRule(
            0, 0,
            BinaryPrimitives.ReadInt32LittleEndian(data), BinaryPrimitives.ReadInt32LittleEndian(data[4..]), BinaryPrimitives.ReadInt32LittleEndian(data[8..]),
            SystemTime.Read(data[14..]), SystemTime.Read(data[32..]));
        return true;
    }

    /// <summary>Writes a rule as a PidLidTimeZoneStruct.</summary>
    /// <param name="rule">The rule; its flags and year are not stored.</param>
    /// <returns>48 bytes.</returns>
    public static byte[] ToArray(TzRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        byte[] b = new byte[Size];
        BinaryPrimitives.WriteInt32LittleEndian(b, rule.Bias);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), rule.StandardBias);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), rule.DaylightBias);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(12), rule.StandardDate.Year);
        rule.StandardDate.Write(b.AsSpan(14));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(30), rule.DaylightDate.Year);
        rule.DaylightDate.Write(b.AsSpan(32));
        return b;
    }
}

/// <summary>A TZDEFINITION (MS-OXOCAL 2.2.1.41): a named time zone and its rules over the years, as stored in the three PidLidAppointmentTimeZoneDefinition* properties.</summary>
public sealed class TimeZoneDefinition
{
    /// <summary>Creates a definition.</summary>
    /// <param name="keyName">The Windows time zone key name, for example <c>Pacific Standard Time</c>.</param>
    /// <param name="rules">The rules in ascending year order.</param>
    public TimeZoneDefinition(string keyName, IReadOnlyList<TzRule> rules)
    {
        KeyName = keyName ?? throw new ArgumentNullException(nameof(keyName));
        Rules = rules ?? throw new ArgumentNullException(nameof(rules));
    }

    /// <summary>The Windows time zone key name (not localised, at most 260 characters).</summary>
    public string KeyName { get; }

    /// <summary>The rules, ascending by year.</summary>
    public IReadOnlyList<TzRule> Rules { get; }

    /// <summary>The rule marked effective, else the last rule, else null when there are no rules.</summary>
    public TzRule? Effective => Rules.FirstOrDefault(r => r.IsEffective) ?? (Rules.Count > 0 ? Rules[^1] : null);

    /// <summary>Reads a TZDEFINITION.</summary>
    /// <param name="data">The property value.</param>
    /// <param name="definition">The definition, or null when not even the header could be read.</param>
    /// <param name="defects">Receives the repairs; may be null.</param>
    /// <param name="limits">Bounds on the rule count; default <see cref="CalendaringLimits.Default"/>.</param>
    /// <returns>True when a definition was produced (possibly with fewer rules than it declared). Never throws.</returns>
    public static bool TryParse(ReadOnlySpan<byte> data, out TimeZoneDefinition? definition, List<CalendaringDefect>? defects = null, CalendaringLimits? limits = null)
    {
        limits ??= CalendaringLimits.Default;
        definition = null;

        // Header: bMajorVersion, bMinorVersion, cbHeader, wReserved, cchKeyName, KeyName, cRules (MS-OXOCAL 2.2.1.41).
        if (data.Length < 8)
        {
            defects?.Add(new CalendaringDefect(CalendaringDefectKind.Truncated, data.Length, "TZDEFINITION header is cut short"));
            return false;
        }

        if (data[0] != 2 || data[1] != 1)
        {
            defects?.Add(new CalendaringDefect(CalendaringDefectKind.UnexpectedVersion, 0, $"TZDEFINITION version {data[0]}.{data[1]}, expected 2.1"));
        }

        int cchKeyName = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
        int pos = 8;
        int keyBytes = Math.Min(cchKeyName * 2, Math.Max(0, data.Length - pos));
        if (cchKeyName > 260)
        {
            defects?.Add(new CalendaringDefect(CalendaringDefectKind.CountOutOfRange, 6, "TZDEFINITION key name is longer than 260 characters"));
        }

        string key = Encoding.Unicode.GetString(data.Slice(pos, keyBytes & ~1));
        pos += cchKeyName * 2;
        if (pos + 2 > data.Length)
        {
            defects?.Add(new CalendaringDefect(CalendaringDefectKind.Truncated, data.Length, "TZDEFINITION ends before its rule count"));
            definition = new TimeZoneDefinition(key, []);
            return true;
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
        pos += 2;
        if (count > limits.MaxTimeZoneRules)
        {
            defects?.Add(new CalendaringDefect(CalendaringDefectKind.CountOutOfRange, pos - 2, $"TZDEFINITION declares {count} rules; at most {limits.MaxTimeZoneRules} are read"));
            count = limits.MaxTimeZoneRules;
        }

        List<TzRule> rules = [];
        for (int i = 0; i < count; i++)
        {
            if (pos + TzRule.Size > data.Length)
            {
                defects?.Add(new CalendaringDefect(CalendaringDefectKind.Truncated, pos, $"TZDEFINITION declares {count} rules but holds {i}"));
                break;
            }

            ReadOnlySpan<byte> r = data[pos..];

            // TZRule: major, minor, reserved(2), flags(2), wYear(2), X(14), lBias, lStandardBias, lDaylightBias, stStandardDate, stDaylightDate.
            if (r[0] != 2 || r[1] != 1)
            {
                defects?.Add(new CalendaringDefect(CalendaringDefectKind.UnexpectedVersion, pos, $"TZRule version {r[0]}.{r[1]}, expected 2.1"));
            }

            rules.Add(new TzRule(
                BinaryPrimitives.ReadUInt16LittleEndian(r[4..]), BinaryPrimitives.ReadUInt16LittleEndian(r[6..]),
                BinaryPrimitives.ReadInt32LittleEndian(r[22..]), BinaryPrimitives.ReadInt32LittleEndian(r[26..]), BinaryPrimitives.ReadInt32LittleEndian(r[30..]),
                SystemTime.Read(r[34..]), SystemTime.Read(r[50..])));
            pos += TzRule.Size;
        }

        definition = new TimeZoneDefinition(key, rules);
        return true;
    }

    /// <summary>Writes the definition as a TZDEFINITION.</summary>
    /// <returns>The property value.</returns>
    public byte[] ToArray()
    {
        byte[] key = Encoding.Unicode.GetBytes(KeyName.Length > 260 ? KeyName[..260] : KeyName);
        int cch = key.Length / 2;
        byte[] b = new byte[8 + key.Length + 2 + (Rules.Count * TzRule.Size)];
        b[0] = 2;
        b[1] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)(6 + key.Length));   // cbHeader counts Reserved, cchKeyName, KeyName and cRules
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), (ushort)cch);
        key.CopyTo(b.AsSpan(8));
        int pos = 8 + key.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(pos), (ushort)Rules.Count);
        pos += 2;
        foreach (TzRule rule in Rules)
        {
            Span<byte> r = b.AsSpan(pos, TzRule.Size);
            r[0] = 2;
            r[1] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(r[2..], 0x003E);
            BinaryPrimitives.WriteUInt16LittleEndian(r[4..], rule.Flags);
            BinaryPrimitives.WriteUInt16LittleEndian(r[6..], rule.Year);
            BinaryPrimitives.WriteInt32LittleEndian(r[22..], rule.Bias);
            BinaryPrimitives.WriteInt32LittleEndian(r[26..], rule.StandardBias);
            BinaryPrimitives.WriteInt32LittleEndian(r[30..], rule.DaylightBias);
            rule.StandardDate.Write(r[34..]);
            rule.DaylightDate.Write(r[50..]);
            pos += TzRule.Size;
        }

        return b;
    }

    /// <summary>
    /// Builds the VTIMEZONE component that stands for this definition, so the <c>.Calendar</c> time zone layer can do the conversions.
    /// The first rule applies from its year (or 1601 when earlier); a later rule starts on 1 January of its year.
    /// </summary>
    /// <param name="tzid">The TZID to give the component.</param>
    /// <returns>A VTIMEZONE component with one STANDARD and, where the rule has daylight saving time, one DAYLIGHT observance per rule.</returns>
    public Component ToVTimeZone(string tzid) => TimeZoneBuilder.Build(tzid, Rules.Count > 0 ? Rules : [new TzRule(0, 0, 0, 0, 0, default, default)]);
}

internal static class TimeZoneBuilder
{
    internal static Component Build(string tzid, IReadOnlyList<TzRule> rules)
    {
        Component vtz = new("VTIMEZONE");
        vtz.Add(new ContentProperty("TZID", tzid));
        for (int i = 0; i < rules.Count; i++)
        {
            TzRule rule = rules[i];
            int year = Math.Max((int)rule.Year, 1601);
            int? untilYear = i + 1 < rules.Count ? Math.Max((int)rules[i + 1].Year, 1601) : null;
            if (!rule.HasDaylight)
            {
                vtz.Add(Observance("STANDARD", new DateTime(year, 1, 1), rule.StandardOffset, rule.StandardOffset, null, untilYear));
                continue;
            }

            // MS-OXOCAL 2.2.1.39: stStandardDate is when standard time begins, expressed in the daylight time then in force; stDaylightDate likewise in standard time.
            vtz.Add(Observance("STANDARD", Transition(year, rule.StandardDate), rule.DaylightOffset, rule.StandardOffset, rule.StandardDate, untilYear));
            vtz.Add(Observance("DAYLIGHT", Transition(year, rule.DaylightDate), rule.StandardOffset, rule.DaylightOffset, rule.DaylightDate, untilYear));
        }

        return vtz;
    }

    private static DateTime Transition(int year, SystemTime t)
    {
        int y = t.Year != 0 ? t.Year : year;
        int month = Math.Clamp((int)t.Month, 1, 12);
        DateTime date;
        if (t.Year != 0)
        {
            date = new DateTime(y, month, Math.Clamp((int)t.Day, 1, DateTime.DaysInMonth(y, month)));
        }
        else
        {
            date = NthWeekday(y, month, t.DayOfWeek % 7, t.Day);
        }

        return date.AddHours(Math.Min((int)t.Hour, 23)).AddMinutes(Math.Min((int)t.Minute, 59));
    }

    /// <summary>The nth (1 to 4) or last (5) given weekday of a month.</summary>
    internal static DateTime NthWeekday(int year, int month, int dayOfWeek, int n)
    {
        if (n >= 5 || n <= 0)
        {
            DateTime last = new(year, month, DateTime.DaysInMonth(year, month));
            return last.AddDays(-(((int)last.DayOfWeek - dayOfWeek + 7) % 7));
        }

        DateTime first = new(year, month, 1);
        return first.AddDays(((dayOfWeek - (int)first.DayOfWeek + 7) % 7) + (7 * (n - 1)));
    }

    private static Component Observance(string name, DateTime start, TimeSpan from, TimeSpan to, SystemTime? rule, int? untilYear)
    {
        Component c = new(name);
        c.Add(new ContentProperty("DTSTART", start.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture)));
        c.Add(new ContentProperty("TZOFFSETFROM", Offset(from)));
        c.Add(new ContentProperty("TZOFFSETTO", Offset(to)));
        if (rule is { Year: 0, Month: > 0 } r)
        {
            string byDay = (r.Day >= 5 || r.Day == 0 ? "-1" : r.Day.ToString(CultureInfo.InvariantCulture)) + Codes[r.DayOfWeek % 7];
            string rrule = $"FREQ=YEARLY;BYMONTH={r.Month};BYDAY={byDay}";
            if (untilYear is { } u)
            {
                rrule += $";UNTIL={u:D4}0101T000000Z";
            }

            c.Add(new ContentProperty("RRULE", rrule));
        }

        return c;
    }

    private static readonly string[] Codes = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];

    private static string Offset(TimeSpan o) =>
        (o < TimeSpan.Zero ? "-" : "+") + Math.Abs(o.Hours).ToString("D2", CultureInfo.InvariantCulture) + Math.Abs(o.Minutes).ToString("D2", CultureInfo.InvariantCulture);
}
