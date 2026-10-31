using RecursiveAI.OneMail.Calendar.Ics;
using RecursiveAI.OneMail.Calendar.TimeZones;

namespace RecursiveAI.OneMail.Calendar.Recurrence;

/// <summary>How a local time that does not exist (the hour skipped when clocks go forward) is handled.</summary>
public enum SkippedTimePolicy
{
    /// <summary>
    /// The occurrence is kept and read with the offset in force before the gap, so 02:30 on the spring-forward day is 03:30
    /// after the change (RFC 5545 §3.3.5). Every calendar client and library checked does this. This is the default.
    /// </summary>
    Shift = 0,

    /// <summary>The occurrence is dropped and not counted against COUNT (the strict reading of RFC 5545 §3.3.10).</summary>
    Skip,
}

/// <summary>Which occurrences a window selects.</summary>
public enum WindowMode
{
    /// <summary>Occurrences that overlap the window: they start before its end and end after its start (a zero-length one must start inside it). This is the default and what a calendar view shows.</summary>
    Overlapping = 0,

    /// <summary>Occurrences that start inside the window.</summary>
    StartingIn,
}

/// <summary>Limits and choices for an expansion. The limits are on by default and end the enumeration with a defect rather than hanging.</summary>
public sealed record ExpansionOptions
{
    /// <summary>The most occurrences one enumeration yields. Default 100,000. Reaching it ends the enumeration with <see cref="RecurrenceDefectKind.OccurrenceLimit"/>.</summary>
    public int MaxOccurrences { get; init; } = 100_000;

    /// <summary>
    /// The most consecutive periods (years, months, … seconds, counted in steps of INTERVAL) one rule may go through without
    /// producing an instance. Default 1,000,000. Rules that can never match, like <c>BYMONTH=2;BYMONTHDAY=30</c>, end here or at year 9999.
    /// </summary>
    public int MaxIterationsWithoutHit { get; init; } = 1_000_000;

    /// <summary>The most instances one rule may generate before the window starts (only rules with COUNT must generate them). Default 5,000,000.</summary>
    public long MaxInstancesScanned { get; init; } = 5_000_000;

    /// <summary>Local times in a DST gap. Default <see cref="SkippedTimePolicy.Shift"/>.</summary>
    public SkippedTimePolicy SkippedTime { get; init; } = SkippedTimePolicy.Shift;

    /// <summary>Which occurrences a window selects. Default <see cref="WindowMode.Overlapping"/>.</summary>
    public WindowMode WindowMode { get; init; } = WindowMode.Overlapping;

    /// <summary>Where zone names may be looked up besides the file's VTIMEZONE components.</summary>
    public TimeZoneResolverOptions TimeZones { get; init; } = new();
}

/// <summary>
/// A half-open time window <c>[Start, End)</c>. Both are UTC instants. Values with a zone or in UTC are compared by instant;
/// floating times and dates are compared by their wall-clock value as if it were UTC, since they have no instant.
/// </summary>
/// <param name="Start">The first instant in the window.</param>
/// <param name="End">The first instant after the window.</param>
public readonly record struct OccurrenceWindow(DateTime Start, DateTime End)
{
    /// <summary>A window from a start and a length.</summary>
    /// <param name="start">The start.</param>
    /// <param name="length">The length.</param>
    /// <returns>The window.</returns>
    public static OccurrenceWindow From(DateTime start, TimeSpan length) => new(start, TimeMath.AddClamped(start, length, DateTimeKind.Utc));

    /// <summary>Every time.</summary>
    public static OccurrenceWindow All => new(DateTime.MinValue, DateTime.MaxValue);
}

/// <summary>Where an occurrence came from.</summary>
public enum OccurrenceOrigin
{
    /// <summary>DTSTART itself.</summary>
    Start = 0,

    /// <summary>An RRULE.</summary>
    Rule,

    /// <summary>An RDATE with a date or date-time.</summary>
    Date,

    /// <summary>An RDATE with a period (its own length).</summary>
    Period,

    /// <summary>A component with a RECURRENCE-ID that replaces one occurrence.</summary>
    Override,

    /// <summary>An occurrence changed by a RECURRENCE-ID override with <c>RANGE=THISANDFUTURE</c>.</summary>
    FutureOverride,
}

/// <summary>One occurrence of a recurring (or single) component.</summary>
/// <param name="Start">The start as the component writes it: a DATE, a floating time, UTC, or the local time in the component's zone.</param>
/// <param name="End">The end (exclusive for dates), in the same form as <paramref name="Start"/>. Equal to the start for zero-length components.</param>
/// <param name="StartUtc">The start as a UTC instant, or null for dates, floating times and unresolved zones.</param>
/// <param name="EndUtc">The end as a UTC instant, or null when the start has none.</param>
/// <param name="RecurrenceId">The start this occurrence has in the unmodified series (what a RECURRENCE-ID would carry).</param>
/// <param name="Origin">Where the occurrence came from.</param>
/// <param name="Component">The component that holds the occurrence's properties: the master, or the override that replaced it.</param>
public readonly record struct Occurrence(
    IcsTime Start, IcsTime End, DateTime? StartUtc, DateTime? EndUtc, IcsTime RecurrenceId, OccurrenceOrigin Origin, IcsComponent Component);

/// <summary>An instant in a series: the wall-clock value, and the UTC instant when the series has a zone.</summary>
internal readonly record struct Inst(DateTime Wall, DateTime Utc, bool HasUtc)
{
    internal long Key => HasUtc ? Utc.Ticks : Wall.Ticks;
}

/// <summary>How a series is anchored: DATE, floating, UTC or a resolved zone. An unresolvable zone makes the series floating.</summary>
internal sealed class SeriesFrame
{
    private readonly TimeZoneResolver _zones;

    internal SeriesFrame(IcsTime start, TimeZoneResolver zones)
    {
        _zones = zones;
        Kind = start.Kind;
        switch (start.Kind)
        {
            case IcsTimeKind.Utc:
                Zone = UtcTimeZone.Instance;
                break;
            case IcsTimeKind.Zoned:
                Zone = zones.Resolve(start.TzId);
                if (Zone is null)
                {
                    Kind = IcsTimeKind.Floating;
                }
                else
                {
                    TzId = start.TzId;
                }

                break;
            default:
                break;
        }
    }

    internal IcsTimeKind Kind { get; }

    internal IcsTimeZone? Zone { get; }

    internal string? TzId { get; }

    internal Inst Make(IcsTime t)
    {
        DateTime wall = Kind == IcsTimeKind.Date ? t.Value.Date : t.Value;
        switch (t.Kind)
        {
            case IcsTimeKind.Utc:
                if (Zone is not null)
                {
                    DateTime utc = DateTime.SpecifyKind(t.Value, DateTimeKind.Utc);
                    return new Inst(Zone.ToLocal(utc), utc, true);
                }

                return new Inst(wall, default, false);
            case IcsTimeKind.Zoned when Zone is not null && !string.Equals(t.TzId, TzId, StringComparison.Ordinal):
                IcsTimeZone? other = _zones.Resolve(t.TzId);
                if (other is not null)
                {
                    DateTime utc = other.ToUtc(t.Value, out _);
                    return new Inst(Zone.ToLocal(utc), utc, true);
                }

                break;
            default:
                break;
        }

        if (Zone is not null && t.Kind != IcsTimeKind.Date)
        {
            return new Inst(wall, Zone.ToUtc(wall, out _), true);
        }

        return new Inst(wall, default, false);
    }

    internal IcsTime ToIcs(Inst i) => Kind switch
    {
        IcsTimeKind.Date => new IcsTime(i.Wall.Date, IcsTimeKind.Date),
        IcsTimeKind.Utc => new IcsTime(DateTime.SpecifyKind(i.Utc, DateTimeKind.Utc), IcsTimeKind.Utc),
        IcsTimeKind.Zoned => new IcsTime(DateTime.SpecifyKind(i.Wall, DateTimeKind.Unspecified), IcsTimeKind.Zoned, TzId),
        _ => new IcsTime(DateTime.SpecifyKind(i.Wall, DateTimeKind.Unspecified), IcsTimeKind.Floating),
    };
}

/// <summary>The length of an occurrence, as RFC 5545 §3.8.5.3 has it: an exact length from DTEND/DUE, or nominal days plus an exact time part from DURATION.</summary>
internal readonly record struct SpanSpec(bool IsExact, TimeSpan Exact, long NominalDays, TimeSpan TimePart)
{
    internal static readonly SpanSpec Zero = new(true, TimeSpan.Zero, 0, TimeSpan.Zero);

    internal TimeSpan Approx => IsExact ? Exact : TimeSpan.FromDays(NominalDays) + TimePart;
}

/// <summary>Generates one rule's instants in order and applies UNTIL, COUNT and the DST-gap policy.</summary>
internal sealed class RuleStream
{
    private readonly RuleEngine _engine;
    private readonly IcsTimeZone? _zone;
    private readonly RecurrenceRule _rule;
    private readonly string _text;
    private readonly SkippedTimePolicy _policy;
    private readonly ExpansionReport _report;
    private long _emitted;
    private bool _done;

    internal RuleStream(RuleEngine engine, RecurrenceRule rule, string text, SeriesFrame frame, SkippedTimePolicy policy, ExpansionReport report)
    {
        _engine = engine;
        _rule = rule;
        _text = text;
        _zone = frame.Zone;
        _policy = policy;
        _report = report;
    }

    /// <summary>Jumps near a wall-clock time. Only for rules without COUNT, whose instants carry no running total.</summary>
    internal void SeekNear(DateTime wall)
    {
        if (_rule.Count is null)
        {
            _engine.SeekNear(wall);
        }
    }

    internal bool Next(out Inst inst)
    {
        inst = default;
        while (!_done && _engine.TryNext(out DateTime wall))
        {
            Inst candidate;
            if (_zone is not null)
            {
                DateTime utc = _zone.ToUtc(wall, out LocalTimeState state);
                if (state == LocalTimeState.Skipped)
                {
                    _report.Add(RecurrenceDefectKind.AmbiguousSkippedLocalTime);
                    if (_policy == SkippedTimePolicy.Skip)
                    {
                        continue;
                    }
                }
                else if (state == LocalTimeState.Ambiguous)
                {
                    _report.Add(RecurrenceDefectKind.AmbiguousRepeatedLocalTime);
                }

                candidate = new Inst(wall, utc, true);
            }
            else
            {
                candidate = new Inst(wall, default, false);
            }

            if (PastUntil(candidate))
            {
                _done = true;
                return false;
            }

            if (_rule.Count is { } count && ++_emitted > count)
            {
                _done = true;
                return false;
            }

            inst = candidate;
            return true;
        }

        if (!_done && _engine.HitIterationLimit)
        {
            _report.Add(RecurrenceDefectKind.IterationLimit, _text);
        }

        _done = true;
        return false;
    }

    // RFC 5545 §3.3.10: UNTIL is inclusive. A UTC UNTIL is an instant; a DATE UNTIL covers its whole day; a local one is a wall-clock value.
    private bool PastUntil(Inst i)
    {
        if (_rule.Until is not { } until)
        {
            return false;
        }

        return until.Kind switch
        {
            IcsTimeKind.Date => i.Wall.Date > until.Value.Date,
            IcsTimeKind.Utc when i.HasUtc => i.Utc > until.Value,
            _ => i.Wall > until.Value,
        };
    }
}
