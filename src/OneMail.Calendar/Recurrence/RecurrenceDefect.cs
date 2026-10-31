namespace RecursiveAI.OneMail.Calendar.Recurrence;

/// <summary>
/// What recurrence expansion or time-zone resolution repaired, guessed, refused or cut short. Expansion never throws on
/// content. Kinds whose name starts with <c>Ambiguous</c> mark a place where real clients disagree and one documented reading
/// was taken (see <c>docs/recurrence.md</c> and the calendar section of <c>docs/ambiguities.md</c>). Limit kinds end the
/// enumeration early.
/// </summary>
public enum RecurrenceDefectKind
{
    /// <summary>No defect.</summary>
    None = 0,

    // ---- Time zones ----

    /// <summary>
    /// A <c>TZID</c> names no VTIMEZONE in the file, no IANA zone in the runtime's database and no known Windows name.
    /// The values stay floating (no instant is claimed). <see cref="RecurrenceDefect.Detail"/> is the TZID.
    /// </summary>
    UnknownTimeZone,

    /// <summary>A VTIMEZONE has no usable observance (none, or one without offsets). Resolution continues with the other sources.</summary>
    InvalidTimeZone,

    /// <summary>
    /// A TZID was resolved by a repair: a vendor path prefix such as <c>/softwarestudio.org/Olson_20011030_5/</c> was stripped,
    /// or the name matched only ignoring case.
    /// </summary>
    AmbiguousZoneName,

    /// <summary>A Windows zone name was mapped to one IANA zone (the CLDR default territory). The same name can differ by region.</summary>
    AmbiguousWindowsZone,

    // ---- Rules and values ----

    /// <summary>A component that recurs has no (parseable) DTSTART. It yields no occurrences.</summary>
    MissingStart,

    /// <summary>An RRULE or EXRULE does not parse, or has a part out of range (for example <c>BYMONTHDAY=0</c>). The rule is ignored.</summary>
    InvalidRule,

    /// <summary>A rule uses RSCALE other than GREGORIAN (RFC 7529). It is not expanded.</summary>
    UnsupportedRScale,

    /// <summary>A BYDAY ordinal where RFC 5545 §3.3.10 does not allow one (not MONTHLY/YEARLY, or with BYWEEKNO). The ordinal is ignored.</summary>
    OrdinalIgnored,

    /// <summary>A rule with a time-of-day frequency or BYHOUR/BYMINUTE/BYSECOND on a DATE start. Dates are kept; repeated days collapse.</summary>
    TimeRuleOnDate,

    /// <summary>DTSTART does not match its own rule (RFC 5545 §3.8.5.3: the result is undefined). DTSTART is still the first occurrence.</summary>
    AmbiguousUnsynchronizedStart,

    /// <summary>UNTIL has a different value type than DTSTART (DATE against DATE-TIME, or local against UTC). It was compared as described in docs/recurrence.md.</summary>
    AmbiguousUntilType,

    /// <summary>An EXDATE is a DATE where the series has date-times. It removes every occurrence on that day.</summary>
    AmbiguousExdateType,

    /// <summary>
    /// An occurrence falls in a local time that does not exist (the hour skipped when clocks go forward). It is read with the
    /// offset before the gap (RFC 5545 §3.3.5), unless <see cref="SkippedTimePolicy.Skip"/> was chosen. Counted per occurrence.
    /// </summary>
    AmbiguousSkippedLocalTime,

    /// <summary>An occurrence falls in a local time that happens twice (clocks go back). The first one is used (RFC 5545 §3.3.5). Counted per occurrence.</summary>
    AmbiguousRepeatedLocalTime,

    /// <summary>A component has a RECURRENCE-ID but no component with its UID and without one exists. It is yielded as a single occurrence at its own time.</summary>
    OrphanOverride,

    /// <summary>Two overrides carry the same RECURRENCE-ID. The one later in the file wins.</summary>
    DuplicateOverride,

    // ---- Resource limits ----

    /// <summary>More than <see cref="ExpansionOptions.MaxOccurrences"/> occurrences were produced. Enumeration ends there.</summary>
    OccurrenceLimit,

    /// <summary>A rule went <see cref="ExpansionOptions.MaxIterationsWithoutHit"/> periods without producing an instance. Enumeration of that rule ends.</summary>
    IterationLimit,

    /// <summary>A rule generated more than <see cref="ExpansionOptions.MaxInstancesScanned"/> instances before the window. Enumeration of that rule ends.</summary>
    ScanLimit,
}

/// <summary>A defect found during expansion, folded by kind and detail.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Detail">The TZID, rule text or property involved, or null.</param>
/// <param name="Count">How many times it happened.</param>
public readonly record struct RecurrenceDefect(RecurrenceDefectKind Kind, string? Detail, int Count)
{
    /// <summary>True for the <c>Ambiguous*</c> kinds.</summary>
    public bool IsAmbiguity => Kind is >= RecurrenceDefectKind.AmbiguousZoneName and <= RecurrenceDefectKind.AmbiguousWindowsZone
        or RecurrenceDefectKind.AmbiguousUnsynchronizedStart or RecurrenceDefectKind.AmbiguousUntilType or RecurrenceDefectKind.AmbiguousExdateType
        or RecurrenceDefectKind.AmbiguousSkippedLocalTime or RecurrenceDefectKind.AmbiguousRepeatedLocalTime;

    /// <summary>True for the kinds that report a resource limit being reached.</summary>
    public bool IsLimit => Kind >= RecurrenceDefectKind.OccurrenceLimit;
}

/// <summary>Collects the defects of one expansion. Safe to share across the enumerations of one expansion; not thread-safe.</summary>
public sealed class ExpansionReport
{
    private readonly Dictionary<(RecurrenceDefectKind, string?), int> _counts = [];
    private readonly List<(RecurrenceDefectKind, string?)> _order = [];

    /// <summary>The defects so far, in the order each kind and detail first appeared.</summary>
    public IReadOnlyList<RecurrenceDefect> Defects
    {
        get
        {
            List<RecurrenceDefect> list = new(_order.Count);
            foreach ((RecurrenceDefectKind k, string? d) in _order)
            {
                list.Add(new RecurrenceDefect(k, d, _counts[(k, d)]));
            }

            return list;
        }
    }

    /// <summary>How often a kind happened, over all details.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The count.</returns>
    public int CountOf(RecurrenceDefectKind kind)
    {
        int n = 0;
        foreach (KeyValuePair<(RecurrenceDefectKind, string?), int> kv in _counts)
        {
            if (kv.Key.Item1 == kind)
            {
                n += kv.Value;
            }
        }

        return n;
    }

    internal void Add(RecurrenceDefectKind kind, string? detail = null)
    {
        (RecurrenceDefectKind, string?) key = (kind, detail);
        if (_counts.TryGetValue(key, out int n))
        {
            _counts[key] = n + 1;
        }
        else
        {
            _counts[key] = 1;
            _order.Add(key);
        }
    }
}
