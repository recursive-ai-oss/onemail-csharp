using System.Buffers.Binary;
using System.Text;

namespace RecursiveAI.OneMail.Calendaring;

/// <summary>Conversions between "minutes since midnight 1 January 1601" (how MS-OXOCAL stores dates and times of a recurrence) and <see cref="DateTime"/>.</summary>
public static class OutlookMinutes
{
    private static readonly DateTime Epoch = new(1601, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    /// <summary>The value of EndDate that means "never ends" (MS-OXOCAL 2.2.1.44.1).</summary>
    public const uint NeverEnds = 0x5AE980DF;

    /// <summary>The local date and time that a minute count stands for; <see cref="DateTimeKind.Unspecified"/>.</summary>
    /// <param name="minutes">Minutes since 1601-01-01 00:00.</param>
    /// <returns>The date and time, clamped to <see cref="DateTime.MaxValue"/> for counts beyond year 9999.</returns>
    public static DateTime ToDateTime(uint minutes)
    {
        long ticks = Epoch.Ticks + (minutes * TimeSpan.TicksPerMinute);
        return new DateTime(Math.Min(ticks, DateTime.MaxValue.Ticks), DateTimeKind.Unspecified);
    }

    /// <summary>The minute count of a local date and time.</summary>
    /// <param name="value">A date and time; its kind is ignored. Seconds are dropped.</param>
    /// <returns>Minutes since 1601-01-01 00:00, or 0 for earlier values.</returns>
    public static uint FromDateTime(DateTime value) => value <= Epoch ? 0u : (uint)Math.Min((value.Ticks - Epoch.Ticks) / TimeSpan.TicksPerMinute, uint.MaxValue);
}

/// <summary>RecurFrequency (MS-OXOCAL 2.2.1.44.1).</summary>
public enum OutlookRecurFrequency : ushort
{
    /// <summary>0x200A.</summary>
    Daily = 0x200A,

    /// <summary>0x200B.</summary>
    Weekly = 0x200B,

    /// <summary>0x200C.</summary>
    Monthly = 0x200C,

    /// <summary>0x200D.</summary>
    Yearly = 0x200D,
}

/// <summary>PatternType (MS-OXOCAL 2.2.1.44.1).</summary>
public enum OutlookPatternType : ushort
{
    /// <summary>Every n days; no PatternTypeSpecific.</summary>
    Day = 0,

    /// <summary>Every n weeks on the days of the weekday mask.</summary>
    Week = 1,

    /// <summary>The nth day of every n months.</summary>
    Month = 2,

    /// <summary>The nth weekday (or weekend, or day) of every n months.</summary>
    MonthNth = 3,

    /// <summary>The last day of every n months.</summary>
    MonthEnd = 4,

    /// <summary>Hijri-calendar Month.</summary>
    HjMonth = 0x000A,

    /// <summary>Hijri-calendar MonthNth.</summary>
    HjMonthNth = 0x000B,

    /// <summary>Hijri-calendar MonthEnd.</summary>
    HjMonthEnd = 0x000C,
}

/// <summary>A RecurrencePattern (MS-OXOCAL 2.2.1.44.1): when a series repeats and the days it skips or moves. Also the value of PidLidTaskRecurrence.</summary>
public sealed class RecurrencePattern
{
    /// <summary>EndType: the series ends on <see cref="EndDate"/> (0x00002021).</summary>
    public const uint EndAfterDate = 0x00002021;

    /// <summary>EndType: the series ends after <see cref="OccurrenceCount"/> instances (0x00002022).</summary>
    public const uint EndAfterCount = 0x00002022;

    /// <summary>EndType: the series never ends (0x00002023; 0xFFFFFFFF is also read as this).</summary>
    public const uint EndNever = 0x00002023;

    /// <summary>ReaderVersion, 0x3004.</summary>
    public ushort ReaderVersion { get; set; } = 0x3004;

    /// <summary>WriterVersion, 0x3004.</summary>
    public ushort WriterVersion { get; set; } = 0x3004;

    /// <summary>RecurFrequency, as stored (see <see cref="OutlookRecurFrequency"/>).</summary>
    public ushort RecurFrequency { get; set; }

    /// <summary>PatternType, as stored (see <see cref="OutlookPatternType"/>).</summary>
    public ushort PatternType { get; set; }

    /// <summary>CalendarType; 0 and 1 are Gregorian.</summary>
    public ushort CalendarType { get; set; }

    /// <summary>FirstDateTime: minutes from 1601-01-01 to the first day, week or month the series covers (MS-OXOCAL 2.2.1.44.1.1).</summary>
    public uint FirstDateTime { get; set; }

    /// <summary>Period: minutes for a daily series, weeks for weekly, months for monthly and yearly (12).</summary>
    public uint Period { get; set; } = 1;

    /// <summary>SlidingFlag; only tasks use it.</summary>
    public uint SlidingFlag { get; set; }

    /// <summary>The weekday bits of PatternTypeSpecific for <c>Week</c> and <c>MonthNth</c>: Sunday 0x01, Monday 0x02 ... Saturday 0x40.</summary>
    public uint WeekdayMask { get; set; }

    /// <summary>PatternTypeSpecific.Month.Day for <c>Month</c> and <c>MonthEnd</c>.</summary>
    public uint DayOfMonth { get; set; }

    /// <summary>PatternTypeSpecific.MonthNth.N: 1 to 4, or 5 for the last.</summary>
    public uint Nth { get; set; }

    /// <summary>EndType (see the <c>End*</c> constants).</summary>
    public uint EndType { get; set; } = EndNever;

    /// <summary>OccurrenceCount; meaningful when <see cref="EndType"/> is <see cref="EndAfterCount"/>.</summary>
    public uint OccurrenceCount { get; set; }

    /// <summary>FirstDOW: the day a calendar week starts, 0 (Sunday) to 6.</summary>
    public uint FirstDayOfWeek { get; set; }

    /// <summary>DeletedInstanceDates: midnight of each deleted or modified instance's original day, as minutes since 1601, ascending.</summary>
    public List<uint> DeletedInstanceDates { get; } = [];

    /// <summary>ModifiedInstanceDates: midnight of each modified instance's new day, ascending.</summary>
    public List<uint> ModifiedInstanceDates { get; } = [];

    /// <summary>StartDate: midnight of the first instance's day, as minutes since 1601.</summary>
    public uint StartDate { get; set; }

    /// <summary>EndDate: midnight of the last instance's day, or <see cref="OutlookMinutes.NeverEnds"/>.</summary>
    public uint EndDate { get; set; } = OutlookMinutes.NeverEnds;

    /// <summary>True for Hijri pattern types and for CalendarType values other than Gregorian; such a series is not expanded.</summary>
    public bool IsNonGregorian => PatternType is 0x000A or 0x000B or 0x000C || CalendarType > 2 && CalendarType != 9 && CalendarType != 0x0A && CalendarType != 0x0B && CalendarType != 0x0C;

    /// <summary>True when the series has an end (by date or count).</summary>
    public bool HasEnd => EndType is EndAfterDate or EndAfterCount;

    /// <summary>Reads a RecurrencePattern.</summary>
    /// <param name="data">The bytes.</param>
    /// <param name="consumed">How many bytes the pattern used.</param>
    /// <param name="pattern">The pattern, or null when the fixed fields do not fit.</param>
    /// <param name="defects">Receives the repairs; may be null.</param>
    /// <param name="limits">Bounds on list sizes; default <see cref="CalendaringLimits.Default"/>.</param>
    /// <returns>True when a pattern was produced. A pattern cut short keeps what was read and reports <see cref="CalendaringDefectKind.Truncated"/>. Never throws.</returns>
    public static bool TryParse(ReadOnlySpan<byte> data, out int consumed, out RecurrencePattern? pattern, List<CalendaringDefect>? defects = null, CalendaringLimits? limits = null)
    {
        limits ??= CalendaringLimits.Default;
        pattern = null;
        consumed = 0;
        if (data.Length < 22)
        {
            defects?.Add(new CalendaringDefect(CalendaringDefectKind.Truncated, data.Length, "RecurrencePattern is shorter than its fixed header"));
            return false;
        }

        RecurrencePattern p = new()
        {
            ReaderVersion = BinaryPrimitives.ReadUInt16LittleEndian(data),
            WriterVersion = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]),
            RecurFrequency = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]),
            PatternType = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]),
            CalendarType = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]),
            FirstDateTime = BinaryPrimitives.ReadUInt32LittleEndian(data[10..]),
            Period = BinaryPrimitives.ReadUInt32LittleEndian(data[14..]),
            SlidingFlag = BinaryPrimitives.ReadUInt32LittleEndian(data[18..]),
        };
        pattern = p;
        if (p.ReaderVersion != 0x3004 || p.WriterVersion != 0x3004)
        {
            defects?.Add(new CalendaringDefect(CalendaringDefectKind.UnexpectedVersion, 0, $"RecurrencePattern versions 0x{p.ReaderVersion:X4}/0x{p.WriterVersion:X4}, expected 0x3004"));
        }

        if (p.RecurFrequency is < 0x200A or > 0x200D)
        {
            defects?.Add(new CalendaringDefect(CalendaringDefectKind.ValueOutOfRange, 4, $"RecurFrequency 0x{p.RecurFrequency:X4}"));
        }

        int pos = 22;

        // PatternTypeSpecific (MS-OXOCAL 2.2.1.44.1.3 to .6): none for Day, one DWORD for Week, Month and MonthEnd, two for MonthNth.
        switch (p.PatternType)
        {
            case 0x0000:
                break;
            case 0x0001:
                if (!U32(data, ref pos, out uint week))
                {
                    return Cut(defects, pos);
                }

                p.WeekdayMask = week & 0x7F;
                break;
            case 0x0002 or 0x0004 or 0x000A or 0x000C:
                if (!U32(data, ref pos, out uint day))
                {
                    return Cut(defects, pos);
                }

                p.DayOfMonth = day;
                break;
            case 0x0003 or 0x000B:
                if (!U32(data, ref pos, out uint mask) || !U32(data, ref pos, out uint n))
                {
                    return Cut(defects, pos);
                }

                p.WeekdayMask = mask & 0x7F;
                p.Nth = n;
                break;
            default:
                defects?.Add(new CalendaringDefect(CalendaringDefectKind.ValueOutOfRange, 6, $"PatternType 0x{p.PatternType:X4}"));
                break;
        }

        if (!U32(data, ref pos, out uint endType) || !U32(data, ref pos, out uint count) || !U32(data, ref pos, out uint dow) || !U32(data, ref pos, out uint deletedCount))
        {
            return Cut(defects, pos);
        }

        p.EndType = endType == 0xFFFFFFFF ? EndNever : endType;
        if (p.EndType is not (EndAfterDate or EndAfterCount or EndNever))
        {
            defects?.Add(new CalendaringDefect(CalendaringDefectKind.ValueOutOfRange, pos - 16, $"EndType 0x{endType:X8}"));
        }

        p.OccurrenceCount = count;
        p.FirstDayOfWeek = dow;
        if (dow > 6)
        {
            defects?.Add(new CalendaringDefect(CalendaringDefectKind.ValueOutOfRange, pos - 8, $"FirstDOW {dow}"));
            p.FirstDayOfWeek = 0;
        }

        if (!ReadDates(data, ref pos, deletedCount, p.DeletedInstanceDates, defects, limits, "DeletedInstanceDates") || !U32(data, ref pos, out uint modifiedCount)
            || !ReadDates(data, ref pos, modifiedCount, p.ModifiedInstanceDates, defects, limits, "ModifiedInstanceDates"))
        {
            consumed = pos;
            return true;
        }

        if (!U32(data, ref pos, out uint start) || !U32(data, ref pos, out uint end))
        {
            defects?.Add(new CalendaringDefect(CalendaringDefectKind.Truncated, pos, "RecurrencePattern ends before StartDate and EndDate"));
            consumed = pos;
            return true;
        }

        p.StartDate = start;
        p.EndDate = end;
        consumed = pos;
        return true;
    }

    private static bool Cut(List<CalendaringDefect>? defects, int pos)
    {
        defects?.Add(new CalendaringDefect(CalendaringDefectKind.Truncated, pos, "RecurrencePattern is cut short"));
        return true;
    }

    internal static bool U32(ReadOnlySpan<byte> data, ref int pos, out uint value)
    {
        if (pos + 4 > data.Length)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
        pos += 4;
        return true;
    }

    private static bool ReadDates(ReadOnlySpan<byte> data, ref int pos, uint count, List<uint> into, List<CalendaringDefect>? defects, CalendaringLimits limits, string name)
    {
        long fit = (data.Length - pos) / 4;
        long take = Math.Min(Math.Min(count, fit), limits.MaxInstanceDates);
        if (count > take)
        {
            defects?.Add(new CalendaringDefect(CalendaringDefectKind.CountOutOfRange, pos - 4, $"{name} declares {count} dates; {take} are read"));
        }

        for (long i = 0; i < take; i++)
        {
            into.Add(BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]));
            pos += 4;
        }

        // Skip dates beyond the limit so the fields after the list still line up when the bytes are there.
        pos += (int)Math.Min(Math.Max(0, Math.Min(count, fit) - take) * 4, data.Length - pos);
        return count <= fit;
    }

    internal void Write(List<byte> o)
    {
        W16(o, ReaderVersion);
        W16(o, WriterVersion);
        W16(o, RecurFrequency);
        W16(o, PatternType);
        W16(o, CalendarType);
        W32(o, FirstDateTime);
        W32(o, Period);
        W32(o, SlidingFlag);
        switch (PatternType)
        {
            case 0x0001:
                W32(o, WeekdayMask);
                break;
            case 0x0002 or 0x0004 or 0x000A or 0x000C:
                W32(o, DayOfMonth);
                break;
            case 0x0003 or 0x000B:
                W32(o, WeekdayMask);
                W32(o, Nth);
                break;
        }

        W32(o, EndType);
        W32(o, OccurrenceCount);
        W32(o, FirstDayOfWeek);
        W32(o, (uint)DeletedInstanceDates.Count);
        DeletedInstanceDates.ForEach(d => W32(o, d));
        W32(o, (uint)ModifiedInstanceDates.Count);
        ModifiedInstanceDates.ForEach(d => W32(o, d));
        W32(o, StartDate);
        W32(o, EndDate);
    }

    internal static void W16(List<byte> o, ushort v)
    {
        o.Add((byte)v);
        o.Add((byte)(v >> 8));
    }

    internal static void W32(List<byte> o, uint v)
    {
        W16(o, (ushort)v);
        W16(o, (ushort)(v >> 16));
    }

    /// <summary>Writes the pattern as the value of PidLidTaskRecurrence (a bare RecurrencePattern).</summary>
    /// <returns>The bytes.</returns>
    public byte[] ToArray()
    {
        List<byte> o = [];
        Write(o);
        return [.. o];
    }
}

/// <summary>OverrideFlags of an ExceptionInfo (MS-OXOCAL 2.2.1.44.2): which fields differ from the series.</summary>
[Flags]
public enum ExceptionOverrides : ushort
{
    /// <summary>Nothing is overridden: the instance only moved.</summary>
    None = 0,

    /// <summary>ARO_SUBJECT.</summary>
    Subject = 0x0001,

    /// <summary>ARO_MEETINGTYPE.</summary>
    MeetingType = 0x0002,

    /// <summary>ARO_REMINDERDELTA.</summary>
    ReminderDelta = 0x0004,

    /// <summary>ARO_REMINDER.</summary>
    ReminderSet = 0x0008,

    /// <summary>ARO_LOCATION.</summary>
    Location = 0x0010,

    /// <summary>ARO_BUSYSTATUS.</summary>
    BusyStatus = 0x0020,

    /// <summary>ARO_ATTACHMENT.</summary>
    Attachment = 0x0040,

    /// <summary>ARO_SUBTYPE.</summary>
    SubType = 0x0080,

    /// <summary>ARO_APPTCOLOR.</summary>
    AppointmentColor = 0x0100,

    /// <summary>ARO_EXCEPTIONAL_BODY.</summary>
    ExceptionalBody = 0x0200,
}

/// <summary>An ExceptionInfo (MS-OXOCAL 2.2.1.44.2) joined with its ExtendedException (2.2.1.44.4): one modified instance of a series.</summary>
public sealed class ExceptionInfo
{
    /// <summary>StartDateTime: the instance's new start, local time, minutes since 1601.</summary>
    public uint StartDateTime { get; set; }

    /// <summary>EndDateTime: the new end.</summary>
    public uint EndDateTime { get; set; }

    /// <summary>OriginalStartTime: where the instance would have started.</summary>
    public uint OriginalStartTime { get; set; }

    /// <summary>OverrideFlags.</summary>
    public ExceptionOverrides Overrides { get; set; }

    /// <summary>The overriding subject; from the wide-character copy in the extended part when it has one, else the 8-bit copy decoded as Latin-1.</summary>
    public string? Subject { get; set; }

    /// <summary>MeetingType (the exception's PidLidAppointmentStateFlags).</summary>
    public uint? MeetingType { get; set; }

    /// <summary>ReminderDelta in minutes.</summary>
    public uint? ReminderDelta { get; set; }

    /// <summary>ReminderSet.</summary>
    public bool? ReminderSet { get; set; }

    /// <summary>The overriding location; wide-character copy preferred.</summary>
    public string? Location { get; set; }

    /// <summary>BusyStatus.</summary>
    public uint? BusyStatus { get; set; }

    /// <summary>Whether the exception message has attachments.</summary>
    public bool? HasAttachments { get; set; }

    /// <summary>SubType (all-day flag).</summary>
    public uint? SubType { get; set; }

    /// <summary>AppointmentColor.</summary>
    public uint? AppointmentColor { get; set; }

    /// <summary>The exception's PidLidChangeHighlight, when the writer version carries it.</summary>
    public uint? ChangeHighlight { get; set; }
}

/// <summary>An AppointmentRecurrencePattern (MS-OXOCAL 2.2.1.44.5): the value of PidLidAppointmentRecur.</summary>
public sealed class AppointmentRecurrencePattern
{
    /// <summary>The series' pattern.</summary>
    public RecurrencePattern Pattern { get; set; } = new();

    /// <summary>ReaderVersion2, 0x00003006.</summary>
    public uint ReaderVersion2 { get; set; } = 0x00003006;

    /// <summary>WriterVersion2: 0x00003009 when exceptions carry ChangeHighlight, 0x00003008 otherwise.</summary>
    public uint WriterVersion2 { get; set; } = 0x00003009;

    /// <summary>StartTimeOffset: minutes after midnight at which each instance starts.</summary>
    public uint StartTimeOffset { get; set; }

    /// <summary>EndTimeOffset: minutes after midnight at which each instance ends (may pass 1440 for instances that cross midnight).</summary>
    public uint EndTimeOffset { get; set; }

    /// <summary>The modified instances, in the order stored.</summary>
    public List<ExceptionInfo> Exceptions { get; } = [];

    /// <summary>Reads the value of PidLidAppointmentRecur.</summary>
    /// <param name="data">The property value.</param>
    /// <param name="result">The pattern, or null when not even the RecurrencePattern could be read.</param>
    /// <param name="defects">Receives the repairs; may be null.</param>
    /// <param name="limits">Bounds on list sizes; default <see cref="CalendaringLimits.Default"/>.</param>
    /// <returns>True when a result was produced. Never throws; malformed input gives defects.</returns>
    public static bool TryParse(ReadOnlySpan<byte> data, out AppointmentRecurrencePattern? result, List<CalendaringDefect>? defects = null, CalendaringLimits? limits = null)
    {
        limits ??= CalendaringLimits.Default;
        result = null;
        if (!RecurrencePattern.TryParse(data, out int pos, out RecurrencePattern? rp, defects, limits) || rp is null)
        {
            return false;
        }

        AppointmentRecurrencePattern a = new() { Pattern = rp };
        result = a;
        if (!RecurrencePattern.U32(data, ref pos, out uint r2) || !RecurrencePattern.U32(data, ref pos, out uint w2)
            || !RecurrencePattern.U32(data, ref pos, out uint startOffset) || !RecurrencePattern.U32(data, ref pos, out uint endOffset)
            || pos + 2 > data.Length)
        {
            defects?.Add(new CalendaringDefect(CalendaringDefectKind.Truncated, pos, "AppointmentRecurrencePattern ends before its exceptions"));
            return true;
        }

        a.ReaderVersion2 = r2;
        a.WriterVersion2 = w2;
        a.StartTimeOffset = startOffset;
        a.EndTimeOffset = endOffset;
        if (r2 != 0x3006 || w2 is not (0x3008 or 0x3009))
        {
            defects?.Add(new CalendaringDefect(CalendaringDefectKind.UnexpectedVersion, pos - 16, $"AppointmentRecurrencePattern versions 0x{r2:X8}/0x{w2:X8}, expected 0x00003006 and 0x00003008 or 0x00003009"));
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
        pos += 2;
        if (count > limits.MaxExceptions)
        {
            defects?.Add(new CalendaringDefect(CalendaringDefectKind.CountOutOfRange, pos - 2, $"ExceptionCount {count}; at most {limits.MaxExceptions} are read"));
            count = limits.MaxExceptions;
        }

        for (int i = 0; i < count; i++)
        {
            ExceptionInfo e = new();
            if (!ReadInfo(data, ref pos, e, defects))
            {
                defects?.Add(new CalendaringDefect(CalendaringDefectKind.Truncated, pos, $"ExceptionInfo {i} of {count} is cut short"));
                return true;
            }

            a.Exceptions.Add(e);
        }

        // ReservedBlock1Size (zero) and its bytes, then one ExtendedException per ExceptionInfo.
        if (!RecurrencePattern.U32(data, ref pos, out uint res1))
        {
            return true;
        }

        pos += (int)Math.Min(res1, (uint)(data.Length - pos));
        foreach (ExceptionInfo e in a.Exceptions)
        {
            if (!ReadExtended(data, ref pos, e, w2 >= 0x3009, defects))
            {
                defects?.Add(new CalendaringDefect(CalendaringDefectKind.Truncated, pos, "ExtendedException is cut short; the 8-bit subject and location are used"));
                return true;
            }
        }

        return true;
    }

    private static bool ReadInfo(ReadOnlySpan<byte> d, ref int pos, ExceptionInfo e, List<CalendaringDefect>? defects)
    {
        if (pos + 14 > d.Length)
        {
            return false;
        }

        e.StartDateTime = BinaryPrimitives.ReadUInt32LittleEndian(d[pos..]);
        e.EndDateTime = BinaryPrimitives.ReadUInt32LittleEndian(d[(pos + 4)..]);
        e.OriginalStartTime = BinaryPrimitives.ReadUInt32LittleEndian(d[(pos + 8)..]);
        e.Overrides = (ExceptionOverrides)BinaryPrimitives.ReadUInt16LittleEndian(d[(pos + 12)..]);
        pos += 14;
        ExceptionOverrides f = e.Overrides;
        if ((f & ExceptionOverrides.Subject) != 0)
        {
            if (!Str8(d, ref pos, out string? s))
            {
                return false;
            }

            e.Subject = s;
        }

        if ((f & ExceptionOverrides.MeetingType) != 0)
        {
            if (!RecurrencePattern.U32(d, ref pos, out uint v))
            {
                return false;
            }

            e.MeetingType = v;
        }

        if ((f & ExceptionOverrides.ReminderDelta) != 0)
        {
            if (!RecurrencePattern.U32(d, ref pos, out uint v))
            {
                return false;
            }

            e.ReminderDelta = v;
        }

        if ((f & ExceptionOverrides.ReminderSet) != 0)
        {
            if (!RecurrencePattern.U32(d, ref pos, out uint v))
            {
                return false;
            }

            e.ReminderSet = v != 0;
        }

        if ((f & ExceptionOverrides.Location) != 0)
        {
            if (!Str8(d, ref pos, out string? s))
            {
                return false;
            }

            e.Location = s;
        }

        if ((f & ExceptionOverrides.BusyStatus) != 0)
        {
            if (!RecurrencePattern.U32(d, ref pos, out uint v))
            {
                return false;
            }

            e.BusyStatus = v;
        }

        if ((f & ExceptionOverrides.Attachment) != 0)
        {
            if (!RecurrencePattern.U32(d, ref pos, out uint v))
            {
                return false;
            }

            e.HasAttachments = v != 0;
        }

        if ((f & ExceptionOverrides.SubType) != 0)
        {
            if (!RecurrencePattern.U32(d, ref pos, out uint v))
            {
                return false;
            }

            e.SubType = v;
        }

        if ((f & ExceptionOverrides.AppointmentColor) != 0)
        {
            if (!RecurrencePattern.U32(d, ref pos, out uint v))
            {
                return false;
            }

            e.AppointmentColor = v;
        }

        return true;
    }

    // SubjectLength (bytes + 1), SubjectLength2 (bytes), then the bytes (MS-OXOCAL 2.2.1.44.2); the code page is not stored, so Latin-1 is the fallback.
    private static bool Str8(ReadOnlySpan<byte> d, ref int pos, out string? s)
    {
        s = null;
        if (pos + 4 > d.Length)
        {
            return false;
        }

        int len2 = BinaryPrimitives.ReadUInt16LittleEndian(d[(pos + 2)..]);
        pos += 4;
        if (pos + len2 > d.Length)
        {
            return false;
        }

        s = Encoding.Latin1.GetString(d.Slice(pos, len2));
        pos += len2;
        return true;
    }

    private static bool ReadExtended(ReadOnlySpan<byte> d, ref int pos, ExceptionInfo e, bool hasChangeHighlight, List<CalendaringDefect>? defects)
    {
        if (hasChangeHighlight)
        {
            if (!RecurrencePattern.U32(d, ref pos, out uint size) || size < 4 || pos + size > d.Length)
            {
                return false;
            }

            e.ChangeHighlight = BinaryPrimitives.ReadUInt32LittleEndian(d[pos..]);
            pos += (int)size;
        }

        if (!RecurrencePattern.U32(d, ref pos, out uint res1) || pos + res1 > d.Length)
        {
            return false;
        }

        pos += (int)res1;
        if ((e.Overrides & (ExceptionOverrides.Subject | ExceptionOverrides.Location)) == 0)
        {
            return true;
        }

        // StartDateTime, EndDateTime, OriginalStartDate repeat the ExceptionInfo's.
        if (!RecurrencePattern.U32(d, ref pos, out _) || !RecurrencePattern.U32(d, ref pos, out _) || !RecurrencePattern.U32(d, ref pos, out _))
        {
            return false;
        }

        if ((e.Overrides & ExceptionOverrides.Subject) != 0)
        {
            if (!Wide(d, ref pos, out string? s))
            {
                return false;
            }

            e.Subject = s;
        }

        if ((e.Overrides & ExceptionOverrides.Location) != 0)
        {
            if (!Wide(d, ref pos, out string? s))
            {
                return false;
            }

            e.Location = s;
        }

        if (!RecurrencePattern.U32(d, ref pos, out uint res2) || pos + res2 > d.Length)
        {
            return false;
        }

        pos += (int)res2;
        return true;
    }

    private static bool Wide(ReadOnlySpan<byte> d, ref int pos, out string? s)
    {
        s = null;
        if (pos + 2 > d.Length)
        {
            return false;
        }

        int chars = BinaryPrimitives.ReadUInt16LittleEndian(d[pos..]);
        pos += 2;
        if (pos + (chars * 2) > d.Length)
        {
            return false;
        }

        s = Encoding.Unicode.GetString(d.Slice(pos, chars * 2));
        pos += chars * 2;
        return true;
    }

    /// <summary>Writes the pattern as the value of PidLidAppointmentRecur, with the wide-character parts of exceptions.</summary>
    /// <returns>The bytes.</returns>
    public byte[] ToArray()
    {
        List<byte> o = [];
        Pattern.Write(o);
        RecurrencePattern.W32(o, ReaderVersion2);
        RecurrencePattern.W32(o, WriterVersion2);
        RecurrencePattern.W32(o, StartTimeOffset);
        RecurrencePattern.W32(o, EndTimeOffset);
        RecurrencePattern.W16(o, (ushort)Exceptions.Count);
        foreach (ExceptionInfo e in Exceptions)
        {
            RecurrencePattern.W32(o, e.StartDateTime);
            RecurrencePattern.W32(o, e.EndDateTime);
            RecurrencePattern.W32(o, e.OriginalStartTime);
            RecurrencePattern.W16(o, (ushort)e.Overrides);
            ExceptionOverrides f = e.Overrides;
            if ((f & ExceptionOverrides.Subject) != 0)
            {
                Str8W(o, e.Subject);
            }

            Opt(o, f, ExceptionOverrides.MeetingType, e.MeetingType);
            Opt(o, f, ExceptionOverrides.ReminderDelta, e.ReminderDelta);
            Opt(o, f, ExceptionOverrides.ReminderSet, e.ReminderSet is true ? 1u : 0u);
            if ((f & ExceptionOverrides.Location) != 0)
            {
                Str8W(o, e.Location);
            }

            Opt(o, f, ExceptionOverrides.BusyStatus, e.BusyStatus);
            Opt(o, f, ExceptionOverrides.Attachment, e.HasAttachments is true ? 1u : 0u);
            Opt(o, f, ExceptionOverrides.SubType, e.SubType);
            Opt(o, f, ExceptionOverrides.AppointmentColor, e.AppointmentColor);
        }

        RecurrencePattern.W32(o, 0);
        foreach (ExceptionInfo e in Exceptions)
        {
            if (WriterVersion2 >= 0x3009)
            {
                RecurrencePattern.W32(o, 4);
                RecurrencePattern.W32(o, e.ChangeHighlight ?? 0);
            }

            RecurrencePattern.W32(o, 0);
            ExceptionOverrides f = e.Overrides;
            if ((f & (ExceptionOverrides.Subject | ExceptionOverrides.Location)) != 0)
            {
                RecurrencePattern.W32(o, e.StartDateTime);
                RecurrencePattern.W32(o, e.EndDateTime);
                RecurrencePattern.W32(o, e.OriginalStartTime);
                if ((f & ExceptionOverrides.Subject) != 0)
                {
                    WideW(o, e.Subject);
                }

                if ((f & ExceptionOverrides.Location) != 0)
                {
                    WideW(o, e.Location);
                }

                RecurrencePattern.W32(o, 0);
            }
        }

        RecurrencePattern.W32(o, 0);
        return [.. o];
    }

    private static void Opt(List<byte> o, ExceptionOverrides f, ExceptionOverrides flag, uint? v)
    {
        if ((f & flag) != 0)
        {
            RecurrencePattern.W32(o, v ?? 0);
        }
    }

    private static void Str8W(List<byte> o, string? s)
    {
        byte[] b = Encoding.Latin1.GetBytes(s ?? string.Empty);
        RecurrencePattern.W16(o, (ushort)(b.Length + 1));
        RecurrencePattern.W16(o, (ushort)b.Length);
        o.AddRange(b);
    }

    private static void WideW(List<byte> o, string? s)
    {
        s ??= string.Empty;
        RecurrencePattern.W16(o, (ushort)s.Length);
        o.AddRange(Encoding.Unicode.GetBytes(s));
    }
}
