namespace RecursiveAI.OneMail.Calendar.TimeZones;

/// <summary>Where a resolved time zone's rules came from.</summary>
public enum ZoneSource
{
    /// <summary>Coordinated Universal Time.</summary>
    Utc = 0,

    /// <summary>A VTIMEZONE component in the file (RFC 5545 §3.6.5).</summary>
    VTimeZone,

    /// <summary>An IANA name found in the runtime's time zone database.</summary>
    Iana,

    /// <summary>A Windows zone name, mapped to an IANA name with the CLDR table and found in the runtime's database.</summary>
    WindowsName,
}

/// <summary>Whether a local wall-clock time exists (RFC 5545 §3.3.5).</summary>
public enum LocalTimeState
{
    /// <summary>The local time happens once.</summary>
    Unique = 0,

    /// <summary>The local time happens twice, when clocks go back. The first occurrence is used.</summary>
    Ambiguous,

    /// <summary>The local time never happens, in the hour skipped when clocks go forward. The offset before the gap is used.</summary>
    Skipped,
}

/// <summary>
/// A time zone that converts between UTC and local wall-clock time. Local times that happen twice resolve to the first
/// occurrence and local times that never happen resolve with the offset in force before the gap (RFC 5545 §3.3.5).
/// All <see cref="DateTime"/> arguments and results are plain values; <see cref="DateTime.Kind"/> is ignored on input and
/// is <see cref="DateTimeKind.Utc"/> or <see cref="DateTimeKind.Unspecified"/> on output as the name says.
/// </summary>
public abstract class IcsTimeZone
{
    /// <summary>The TZID this zone was resolved for.</summary>
    public abstract string Id { get; }

    /// <summary>Where the rules came from.</summary>
    public abstract ZoneSource Source { get; }

    /// <summary>The UTC offset in force at a UTC instant.</summary>
    /// <param name="utc">The instant.</param>
    /// <returns>The offset.</returns>
    public abstract TimeSpan GetUtcOffset(DateTime utc);

    /// <summary>Converts a local wall-clock time to UTC.</summary>
    /// <param name="local">The local time.</param>
    /// <param name="state">Whether the time exists, happens twice, or falls in a gap.</param>
    /// <returns>The UTC instant. Results are clamped to the range of <see cref="DateTime"/>.</returns>
    public abstract DateTime ToUtc(DateTime local, out LocalTimeState state);

    /// <summary>Converts a UTC instant to local wall-clock time.</summary>
    /// <param name="utc">The instant.</param>
    /// <returns>The local time, clamped to the range of <see cref="DateTime"/>.</returns>
    public virtual DateTime ToLocal(DateTime utc) => TimeMath.AddClamped(utc, GetUtcOffset(utc), DateTimeKind.Unspecified);
}

internal static class TimeMath
{
    internal static DateTime FromTicks(long ticks, DateTimeKind kind) =>
        new(Math.Clamp(ticks, DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks), kind);

    internal static DateTime AddClamped(DateTime t, TimeSpan d, DateTimeKind kind) => FromTicks(t.Ticks + d.Ticks, kind);
}

/// <summary>UTC itself.</summary>
internal sealed class UtcTimeZone : IcsTimeZone
{
    internal static readonly UtcTimeZone Instance = new();

    public override string Id => "UTC";

    public override ZoneSource Source => ZoneSource.Utc;

    public override TimeSpan GetUtcOffset(DateTime utc) => TimeSpan.Zero;

    public override DateTime ToUtc(DateTime local, out LocalTimeState state)
    {
        state = LocalTimeState.Unique;
        return DateTime.SpecifyKind(local, DateTimeKind.Utc);
    }
}

/// <summary>A constant offset; used for the observance rules of a VTIMEZONE, whose UNTIL is in UTC.</summary>
internal sealed class FixedOffsetZone(TimeSpan offset) : IcsTimeZone
{
    public override string Id => "";

    public override ZoneSource Source => ZoneSource.VTimeZone;

    public override TimeSpan GetUtcOffset(DateTime utc) => offset;

    public override DateTime ToUtc(DateTime local, out LocalTimeState state)
    {
        state = LocalTimeState.Unique;
        return TimeMath.AddClamped(local, -offset, DateTimeKind.Utc);
    }
}

/// <summary>A zone from the runtime's time zone database (IANA names, or Windows names the runtime itself understands).</summary>
internal sealed class RuntimeTimeZone(string id, ZoneSource source, TimeZoneInfo info) : IcsTimeZone
{
    public override string Id => id;

    public override ZoneSource Source => source;

    public override TimeSpan GetUtcOffset(DateTime utc) => info.GetUtcOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));

    public override DateTime ToUtc(DateTime local, out LocalTimeState state)
    {
        DateTime l = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        TimeSpan offset;
        if (info.IsInvalidTime(l))
        {
            // RFC 5545 §3.3.5: a time in the gap uses the offset before the gap. A day earlier is safely before it.
            state = LocalTimeState.Skipped;
            offset = info.GetUtcOffset(TimeMath.AddClamped(l, TimeSpan.FromDays(-1), DateTimeKind.Utc));
        }
        else if (info.IsAmbiguousTime(l))
        {
            // RFC 5545 §3.3.5: a time that happens twice is the first one, the one with the larger offset.
            state = LocalTimeState.Ambiguous;
            offset = TimeSpan.MinValue;
            foreach (TimeSpan o in info.GetAmbiguousTimeOffsets(l))
            {
                if (o > offset)
                {
                    offset = o;
                }
            }
        }
        else
        {
            state = LocalTimeState.Unique;
            offset = info.GetUtcOffset(l);
        }

        return TimeMath.AddClamped(l, -offset, DateTimeKind.Utc);
    }
}
