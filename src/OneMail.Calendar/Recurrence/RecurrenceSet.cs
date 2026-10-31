using RecursiveAI.OneMail.Calendar.Ics;
using RecursiveAI.OneMail.Calendar.TimeZones;

namespace RecursiveAI.OneMail.Calendar.Recurrence;

/// <summary>
/// The recurrence set of one VEVENT, VTODO or VJOURNAL (RFC 5545 §3.8.5): DTSTART, RRULE and RDATE, minus EXDATE and EXRULE,
/// with RECURRENCE-ID overrides applied. Expanding is a view: the component tree is never changed. A component without
/// recurrence properties expands to its single occurrence.
/// </summary>
public sealed class RecurrenceSet
{
    private readonly IcsComponent _master;
    private readonly TimeZoneResolver _zones;
    private readonly ExpansionOptions _options;
    private readonly ExpansionReport _report;
    private readonly SeriesFrame? _frame;
    private readonly Inst _start;
    private readonly SpanSpec _span;
    private readonly List<(RecurrenceRule Rule, string Text)> _rules = [];
    private readonly List<(RecurrenceRule Rule, string Text)> _exrules = [];
    private readonly List<RDate> _rdates = [];
    private readonly HashSet<long> _exKeys = [];
    private readonly HashSet<long> _exDays = [];
    private readonly Dictionary<long, Override> _overrides = [];
    private readonly Dictionary<long, Override> _overridesByDay = [];
    private readonly List<Override> _future = [];

    /// <summary>Prepares the set. Nothing is expanded until <see cref="Expand"/> runs.</summary>
    /// <param name="master">The component with DTSTART and the recurrence properties.</param>
    /// <param name="overrides">Components with the same UID and a RECURRENCE-ID, or null.</param>
    /// <param name="zones">Resolves TZIDs.</param>
    /// <param name="options">Limits and choices; null for the defaults.</param>
    /// <param name="report">Receives defects; null shares the zone resolver's report.</param>
    public RecurrenceSet(IcsComponent master, IEnumerable<IcsComponent>? overrides, TimeZoneResolver zones, ExpansionOptions? options = null, ExpansionReport? report = null)
    {
        ArgumentNullException.ThrowIfNull(master);
        ArgumentNullException.ThrowIfNull(zones);
        _master = master;
        _zones = zones;
        _options = options ?? new ExpansionOptions();
        _report = report ?? zones.Report;
        if (master.Start is not { } start)
        {
            _report.Add(RecurrenceDefectKind.MissingStart, master.Name);
            return;
        }

        _frame = new SeriesFrame(start, zones);
        _start = _frame.Make(start);
        _span = SpanOf(master, _frame, _start);
        string? tzid = master.Component.FindProperty("DTSTART")?.GetParameter("TZID");

        foreach (ContentProperty p in master.Component.FindProperties("RRULE"))
        {
            AddRule(p, tzid, _rules);
        }

        foreach (ContentProperty p in master.Component.FindProperties("EXRULE"))
        {
            AddRule(p, tzid, _exrules);
        }

        foreach (ContentProperty p in master.Component.FindProperties("RDATE"))
        {
            AddRDates(p);
        }

        _rdates.Sort(static (a, b) => a.Inst.Key.CompareTo(b.Inst.Key));

        foreach (ContentProperty p in master.Component.FindProperties("EXDATE"))
        {
            string? ptz = p.GetParameter("TZID");
            foreach (string v in p.Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!IcsTime.TryParse(v, ptz, out IcsTime t))
                {
                    continue;
                }

                if (t.IsDate && _frame.Kind != IcsTimeKind.Date)
                {
                    // Clients take a DATE in EXDATE to remove the whole day, though RFC 5545 §3.8.5.1 asks for the series' own value type.
                    _exDays.Add(t.Value.Ticks / TimeSpan.TicksPerDay);
                    _report.Add(RecurrenceDefectKind.AmbiguousExdateType);
                }
                else
                {
                    _exKeys.Add(_frame.Make(t).Key);
                }
            }
        }

        if (overrides is not null)
        {
            foreach (IcsComponent o in overrides)
            {
                AddOverride(o);
            }
        }
    }

    private sealed record RDate(Inst Inst, SpanSpec? Span);

    private sealed record Override(IcsComponent Component, Inst RecurrenceId, bool IsDay, Inst Start, SpanSpec Span, SeriesFrame Frame, bool ThisAndFuture);

    private void AddRule(ContentProperty p, string? tzid, List<(RecurrenceRule, string)> into)
    {
        if (RecurrenceRule.TryParse(p.Value, tzid, out RecurrenceRule rule))
        {
            into.Add((rule, p.Value));
        }
        else
        {
            _report.Add(RecurrenceDefectKind.InvalidRule, p.Value);
        }
    }

    private void AddRDates(ContentProperty p)
    {
        string? ownTz = p.GetParameter("TZID");
        foreach (string v in p.Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (v.Contains('/', StringComparison.Ordinal) && IcsPeriod.TryParse(v, ownTz, out IcsPeriod period))
            {
                Inst s = _frame!.Make(period.Start);
                SpanSpec span;
                if (period.End is { } e)
                {
                    Inst ei = _frame.Make(e);
                    span = new SpanSpec(true, s.HasUtc && ei.HasUtc ? ei.Utc - s.Utc : ei.Wall - s.Wall, 0, TimeSpan.Zero);
                }
                else
                {
                    span = Nominal(period.Duration!.Value);
                }

                _rdates.Add(new RDate(s, span));
            }
            else if (IcsTime.TryParse(v, ownTz, out IcsTime t))
            {
                _rdates.Add(new RDate(_frame!.Make(t), null));
            }
        }
    }

    private void AddOverride(IcsComponent o)
    {
        if (o.RecurrenceId is not { } rid)
        {
            return;
        }

        SeriesFrame frame = _frame!;
        bool isDay = rid.IsDate && frame.Kind != IcsTimeKind.Date;
        Inst ridInst = frame.Make(rid);
        SeriesFrame ofr = o.Start is { } os ? new SeriesFrame(os, _zones) : frame;
        Inst start = o.Start is { } s ? ofr.Make(s) : ridInst;
        SpanSpec span = SpanOf(o, ofr, start);
        bool future = string.Equals(o.Component.FindProperty("RECURRENCE-ID")?.GetParameter("RANGE"), "THISANDFUTURE", StringComparison.OrdinalIgnoreCase);
        Override ov = new(o, ridInst, isDay, start, span, ofr, future);
        long key = isDay ? rid.Value.Ticks / TimeSpan.TicksPerDay : ridInst.Key;
        Dictionary<long, Override> map = isDay ? _overridesByDay : _overrides;
        if (map.ContainsKey(key))
        {
            _report.Add(RecurrenceDefectKind.DuplicateOverride, o.Uid);
        }

        map[key] = ov;
        if (future)
        {
            _future.Add(ov);
        }
    }

    private static SpanSpec Nominal(IcsDuration d)
    {
        long days = ((long)d.Weeks * 7) + d.Days;
        TimeSpan time = new(d.Hours, d.Minutes, d.Seconds);
        return d.Negative ? new SpanSpec(false, TimeSpan.Zero, -days, -time) : new SpanSpec(false, TimeSpan.Zero, days, time);
    }

    // RFC 5545 §3.8.5.3: a length given by DTEND or DUE applies unchanged to every instance; DURATION days are nominal.
    private static SpanSpec SpanOf(IcsComponent c, SeriesFrame frame, Inst start)
    {
        bool todo = c.Component.IsNamed("VTODO");
        if ((todo ? c.Due : c.End) is { } end)
        {
            Inst e = frame.Make(end);
            TimeSpan exact = start.HasUtc && e.HasUtc ? e.Utc - start.Utc : e.Wall - start.Wall;
            return new SpanSpec(true, exact, 0, TimeSpan.Zero);
        }

        if (c.Duration is { } d)
        {
            return Nominal(d);
        }

        // RFC 5545 §3.6.1: an event that starts on a DATE and has no end lasts one day.
        return frame.Kind == IcsTimeKind.Date && c.Component.IsNamed("VEVENT") ? new SpanSpec(false, TimeSpan.Zero, 1, TimeSpan.Zero) : SpanSpec.Zero;
    }

    private static Inst EndOf(SeriesFrame frame, Inst s, SpanSpec span)
    {
        IcsTimeZone? zone = frame.Zone;
        if (span.IsExact)
        {
            if (zone is not null && s.HasUtc)
            {
                DateTime eu = TimeMath.AddClamped(s.Utc, span.Exact, DateTimeKind.Utc);
                return new Inst(zone.ToLocal(eu), eu, true);
            }

            return new Inst(TimeMath.AddClamped(s.Wall, span.Exact, DateTimeKind.Unspecified), default, false);
        }

        DateTime wall = TimeMath.AddClamped(s.Wall, TimeSpan.FromDays(span.NominalDays), DateTimeKind.Unspecified);
        if (zone is not null)
        {
            DateTime u = TimeMath.AddClamped(zone.ToUtc(wall, out _), span.TimePart, DateTimeKind.Utc);
            return new Inst(zone.ToLocal(u), u, true);
        }

        return new Inst(TimeMath.AddClamped(wall, span.TimePart, DateTimeKind.Unspecified), default, false);
    }

    private static Occurrence Build(SeriesFrame frame, Inst s, SpanSpec span, Inst recurrenceId, OccurrenceOrigin origin, IcsComponent component)
    {
        Inst e = EndOf(frame, s, span);
        return new Occurrence(frame.ToIcs(s), frame.ToIcs(e), s.HasUtc ? s.Utc : null, e.HasUtc ? e.Utc : null, frame.ToIcs(recurrenceId), origin, component);
    }

    /// <summary>Expands the set over a window, lazily and in start order.</summary>
    /// <param name="window">The window; see <see cref="OccurrenceWindow"/> for how floating times and dates are compared.</param>
    /// <returns>
    /// The occurrences, ordered by start (instants for zoned values, wall-clock values otherwise). Enumeration ends early with a
    /// defect when a limit in <see cref="ExpansionOptions"/> is reached. Occurrences inside a skipped local hour may come
    /// out of order by up to that hour.
    /// </returns>
    public IEnumerable<Occurrence> Expand(OccurrenceWindow window)
    {
        if (_frame is null || window.End <= window.Start)
        {
            return [];
        }

        return ExpandCore(window);
    }

    private IEnumerable<Occurrence> ExpandCore(OccurrenceWindow window)
    {
        SeriesFrame frame = _frame!;
        long ws = window.Start.Ticks;
        long we = window.End.Ticks;
        bool overlapping = _options.WindowMode == WindowMode.Overlapping;

        // Overrides replace single instances; their own times may be anywhere, so they are merged in separately.
        List<Occurrence> overrideOccurrences = [];
        foreach (Override o in _overrides.Values.Concat(_overridesByDay.Values))
        {
            Occurrence occ = Build(o.Frame, o.Start, o.Span, o.RecurrenceId, OccurrenceOrigin.Override, o.Component);
            if (Selected(occ, ws, we, overlapping))
            {
                overrideOccurrences.Add(occ);
            }
        }

        overrideOccurrences.Sort(static (a, b) => Key(a).CompareTo(Key(b)));
        _future.Sort(static (a, b) => a.RecurrenceId.Key.CompareTo(b.RecurrenceId.Key));

        // How far a THISANDFUTURE override can move later instances; the scan margin has to cover it.
        long shiftMargin = 0;
        foreach (Override f in _future)
        {
            shiftMargin = Math.Max(shiftMargin, Math.Abs(f.Start.Wall.Ticks - f.RecurrenceId.Wall.Ticks));
            shiftMargin = Math.Max(shiftMargin, Math.Abs(f.Span.Approx.Ticks));
        }

        long margin = TimeSpan.TicksPerDay * 2;
        long lead = Math.Max(0, _span.Approx.Ticks) + margin + shiftMargin;
        foreach (RDate r in _rdates)
        {
            if (r.Span is { } rs)
            {
                lead = Math.Max(lead, rs.Approx.Ticks + margin + shiftMargin);
            }
        }

        DateTime seek = TimeMath.FromTicks(ws - lead, DateTimeKind.Unspecified);
        long stopKey = we >= long.MaxValue - lead ? long.MaxValue : we + margin + shiftMargin;

        // Sources of instants, in priority order: DTSTART, rules, RDATEs.
        List<Source> sources = [];
        sources.Add(new Source(OccurrenceOrigin.Start, [_start]));
        bool synchronized = _rules.Count == 0;
        foreach ((RecurrenceRule rule, string text) in _rules)
        {
            RuleStream? s = MakeStream(rule, text, frame, _report);
            if (s is null)
            {
                continue;
            }

            if (!synchronized)
            {
                RuleStream probe = MakeStream(rule, text, frame, new ExpansionReport())!;
                synchronized = probe.Next(out Inst first) && first.Key == _start.Key;
                if (!synchronized)
                {
                    foreach (RDate r in _rdates)
                    {
                        if (r.Inst.Key == _start.Key)
                        {
                            synchronized = true;
                        }
                    }
                }
            }

            s.SeekNear(seek);
            sources.Add(new Source(OccurrenceOrigin.Rule, s));
        }

        if (!synchronized)
        {
            _report.Add(RecurrenceDefectKind.AmbiguousUnsynchronizedStart, _master.Uid);
        }

        if (_rdates.Count > 0)
        {
            sources.Add(new Source(OccurrenceOrigin.Date, [.. _rdates.Select(static r => r.Inst)]) { Spans = [.. _rdates.Select(static r => r.Span)] });
        }

        List<RuleStream> exStreams = [];
        List<Inst> exCurrent = [];
        List<bool> exHas = [];
        foreach ((RecurrenceRule rule, string text) in _exrules)
        {
            RuleStream? s = MakeStream(rule, text, frame, _report);
            if (s is null)
            {
                continue;
            }

            s.SeekNear(seek);
            exStreams.Add(s);
            exCurrent.Add(default);
            exHas.Add(s.Next(out Inst e0));
            exCurrent[^1] = e0;
        }

        foreach (Source s in sources)
        {
            s.Advance();
        }

        int produced = 0;
        int overrideIndex = 0;
        long scanned = 0;
        long lastKey = long.MinValue;
        DateTime lastWall = default;
        bool any = false;

        while (true)
        {
            // The next instant: the smallest current key over the sources; ties go to the earlier source.
            Source? pick = null;
            foreach (Source s in sources)
            {
                if (s.Has && (pick is null || s.Current.Key < pick.Current.Key))
                {
                    pick = s;
                }
            }

            if (pick is null)
            {
                break;
            }

            Inst inst = pick.Current;
            OccurrenceOrigin origin = pick.CurrentOrigin;
            SpanSpec? ownSpan = pick.CurrentSpan;
            long key = inst.Key;
            foreach (Source s in sources)
            {
                // The same instant at the same wall-clock time is one occurrence however many sources produce it. Two wall-clock
                // times that fall on one instant (either side of a skipped hour) stay two occurrences, as in the other libraries.
                while (s.Has && s.Current.Key == key && s.Current.Wall == inst.Wall)
                {
                    s.Advance();
                }
            }

            if (any && key == lastKey && inst.Wall == lastWall)
            {
                continue;
            }

            any = true;
            lastKey = key;
            lastWall = inst.Wall;
            if (key >= stopKey)
            {
                break;
            }

            // Overrides that start before this instant go first.
            while (overrideIndex < overrideOccurrences.Count && Key(overrideOccurrences[overrideIndex]) <= key)
            {
                if (!Take(ref produced))
                {
                    yield break;
                }

                yield return overrideOccurrences[overrideIndex++];
            }

            if (Excluded(inst, exStreams, exCurrent, exHas))
            {
                continue;
            }

            if (_overrides.ContainsKey(key) || (_overridesByDay.Count > 0 && _overridesByDay.ContainsKey(inst.Wall.Ticks / TimeSpan.TicksPerDay)))
            {
                continue; // replaced; the override is yielded from its own list
            }

            Occurrence occ;
            Override? fut = FutureFor(key);
            if (fut is not null)
            {
                // THISANDFUTURE: this and later instances move by the override's shift and take its properties and length.
                long shift = fut.Start.Wall.Ticks - fut.RecurrenceId.Wall.Ticks;
                DateTime nw = TimeMath.FromTicks(inst.Wall.Ticks + shift, DateTimeKind.Unspecified);
                Inst moved = fut.Frame.Zone is { } fz ? new Inst(nw, fz.ToUtc(nw, out _), true) : new Inst(nw, default, false);
                occ = Build(fut.Frame, moved, fut.Span, inst, OccurrenceOrigin.FutureOverride, fut.Component);
            }
            else
            {
                occ = Build(frame, inst, ownSpan ?? _span, inst, origin, _master);
            }

            if (Selected(occ, ws, we, overlapping))
            {
                if (!Take(ref produced))
                {
                    yield break;
                }

                yield return occ;
            }
            else if (Key(occ) < ws && ++scanned > _options.MaxInstancesScanned)
            {
                _report.Add(RecurrenceDefectKind.ScanLimit, _master.Uid);
                break;
            }
        }

        while (overrideIndex < overrideOccurrences.Count)
        {
            if (!Take(ref produced))
            {
                yield break;
            }

            yield return overrideOccurrences[overrideIndex++];
        }
    }

    private bool Take(ref int produced)
    {
        if (produced >= _options.MaxOccurrences)
        {
            _report.Add(RecurrenceDefectKind.OccurrenceLimit, _master.Uid);
            return false;
        }

        produced++;
        return true;
    }

    private Override? FutureFor(long key)
    {
        Override? best = null;
        foreach (Override f in _future)
        {
            if (f.RecurrenceId.Key <= key)
            {
                best = f;
            }
        }

        return best;
    }

    private static long Key(Occurrence o) => o.StartUtc?.Ticks ?? o.Start.Value.Ticks;

    private static bool Selected(Occurrence o, long ws, long we, bool overlapping)
    {
        long s = Key(o);
        if (!overlapping)
        {
            return s >= ws && s < we;
        }

        long e = o.EndUtc?.Ticks ?? o.End.Value.Ticks;
        return s < we && (e > ws || (e == s && s >= ws));
    }

    private bool Excluded(Inst inst, List<RuleStream> streams, List<Inst> current, List<bool> has)
    {
        long key = inst.Key;
        if (_exKeys.Contains(key) || (_exDays.Count > 0 && _exDays.Contains(inst.Wall.Ticks / TimeSpan.TicksPerDay)))
        {
            return true;
        }

        bool hit = false;
        for (int i = 0; i < streams.Count; i++)
        {
            while (has[i] && current[i].Key < key)
            {
                has[i] = streams[i].Next(out Inst n);
                current[i] = n;
            }

            if (has[i] && current[i].Key == key)
            {
                hit = true;
            }
        }

        return hit;
    }

    private RuleStream? MakeStream(RecurrenceRule rule, string text, SeriesFrame frame, ExpansionReport report)
    {
        RuleEngine? engine = RuleEngine.Create(rule, _start.Wall, frame.Kind == IcsTimeKind.Date, _options.MaxIterationsWithoutHit, report, text);
        if (engine is null)
        {
            return null;
        }

        if (rule.Until is { } u)
        {
            bool mismatch = frame.Kind switch
            {
                IcsTimeKind.Date => !u.IsDate,
                IcsTimeKind.Floating => u.Kind != IcsTimeKind.Floating && u.Kind != IcsTimeKind.Date ? true : u.IsDate,
                _ => u.Kind != IcsTimeKind.Utc,
            };
            if (mismatch)
            {
                report.Add(RecurrenceDefectKind.AmbiguousUntilType, text);
            }
        }

        return new RuleStream(engine, rule, text, frame, _options.SkippedTime, report);
    }

    /// <summary>One ordered supply of instants: a list, or a rule.</summary>
    private sealed class Source
    {
        private readonly Inst[]? _list;
        private readonly RuleStream? _stream;
        private int _index;

        internal Source(OccurrenceOrigin origin, Inst[] list)
        {
            Origin = origin;
            _list = list;
        }

        internal Source(OccurrenceOrigin origin, RuleStream stream)
        {
            Origin = origin;
            _stream = stream;
        }

        internal OccurrenceOrigin Origin { get; }

        internal OccurrenceOrigin CurrentOrigin => Spans is not null && Spans[_index - 1] is not null ? OccurrenceOrigin.Period : Origin;

        internal SpanSpec?[]? Spans { get; init; }

        internal bool Has { get; private set; }

        internal Inst Current { get; private set; }

        internal SpanSpec? CurrentSpan => Spans?[_index - 1];

        internal void Advance()
        {
            if (_stream is not null)
            {
                Has = _stream.Next(out Inst i);
                Current = i;
                return;
            }

            if (_index < _list!.Length)
            {
                Current = _list[_index++];
                Has = true;
            }
            else
            {
                Has = false;
            }
        }
    }
}
