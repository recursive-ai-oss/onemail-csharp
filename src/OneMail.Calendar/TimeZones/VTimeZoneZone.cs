using RecursiveAI.OneMail.Calendar.Ics;
using RecursiveAI.OneMail.Calendar.Recurrence;

namespace RecursiveAI.OneMail.Calendar.TimeZones;

/// <summary>
/// A time zone defined by a VTIMEZONE component (RFC 5545 §3.6.5): STANDARD and DAYLIGHT observances, each with its onset
/// (DTSTART, RRULE, RDATE) and the offsets before and after. The onsets become a sorted list of UTC transitions, built
/// up to a year horizon and extended on demand. Before the first transition the first one's TZOFFSETFROM applies.
/// </summary>
internal sealed class VTimeZoneZone : IcsTimeZone
{
    private const int InitialHorizonYear = 2100;
    private const int MaxOnsetsPerObservance = 20000;

    private readonly string _id;
    private readonly Observance[] _observances;
    private readonly object _gate = new();
    private Transition[] _transitions = [];
    private int _horizon;

    private VTimeZoneZone(string id, Observance[] observances)
    {
        _id = id;
        _observances = observances;
        Build(InitialHorizonYear);
    }

    public override string Id => _id;

    public override ZoneSource Source => ZoneSource.VTimeZone;

    /// <summary>Reads the observances of a VTIMEZONE; null when it has none that carry a start and both offsets.</summary>
    internal static VTimeZoneZone? TryCreate(IcsComponent vtimezone, string id)
    {
        List<Observance> list = [];
        foreach (IcsComponent o in vtimezone.Observances)
        {
            if (o.Start is not { } start || o.OffsetFrom is not { } from || o.OffsetTo is not { } to)
            {
                continue;
            }

            list.Add(new Observance(start.Value, from.ToTimeSpan(), to.ToTimeSpan(), [.. o.RecurrenceRules], [.. o.RecurrenceDates], [.. o.RecurrencePeriods]));
        }

        return list.Count == 0 ? null : new VTimeZoneZone(id, [.. list]);
    }

    private void Build(int horizonYear)
    {
        List<Transition> all = [];
        foreach (Observance o in _observances)
        {
            // RFC 5545 §3.6.5: the onset time is local time before the change, so UTC = onset - TZOFFSETFROM.
            HashSet<long> seen = [];
            void Add(DateTime wall)
            {
                long utc = wall.Ticks - o.From.Ticks;
                if (seen.Add(utc))
                {
                    all.Add(new Transition(utc, o.From, o.To));
                }
            }

            Add(o.Start);
            foreach (IcsTime r in o.RDates)
            {
                Add(r.Value);
            }

            foreach (IcsPeriod p in o.RPeriods)
            {
                Add(p.Start.Value);
            }

            foreach (RecurrenceRule rule in o.Rules)
            {
                ExpansionReport scratch = new();
                RuleEngine? engine = RuleEngine.Create(rule, o.Start, false, 100_000, scratch, rule.ToString());
                if (engine is null)
                {
                    continue;
                }

                int emitted = 0;
                while (emitted < MaxOnsetsPerObservance && engine.TryNext(out DateTime wall) && wall.Year <= horizonYear)
                {
                    if (rule.Until is { } until && (until.Kind == IcsTimeKind.Utc ? wall.Ticks - o.From.Ticks > until.Value.Ticks : wall > until.Value))
                    {
                        break;
                    }

                    emitted++;
                    if (rule.Count is { } c && emitted > c)
                    {
                        break;
                    }

                    Add(wall);
                }
            }
        }

        all.Sort(static (a, b) => a.Utc.CompareTo(b.Utc));
        _horizon = horizonYear;
        _transitions = [.. all];
    }

    private void EnsureHorizon(int year)
    {
        if (year <= _horizon)
        {
            return;
        }

        lock (_gate)
        {
            if (year > _horizon)
            {
                Build(Math.Min(year + 20, 9998));
            }
        }
    }

    private static int FirstAfter(Transition[] t, long utcTicks)
    {
        int lo = 0;
        int hi = t.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (t[mid].Utc <= utcTicks)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    private static TimeSpan OffsetAt(Transition[] t, long utcTicks)
    {
        int i = FirstAfter(t, utcTicks);
        return i == 0 ? t[0].From : t[i - 1].To;
    }

    public override TimeSpan GetUtcOffset(DateTime utc)
    {
        EnsureHorizon(utc.Year + 1);
        return OffsetAt(_transitions, utc.Ticks);
    }

    public override DateTime ToUtc(DateTime local, out LocalTimeState state)
    {
        EnsureHorizon(local.Year + 1);
        Transition[] tr = _transitions;
        long l = local.Ticks;
        long window = 2 * TimeSpan.TicksPerDay;
        int start = Math.Max(0, FirstAfter(tr, l - window) - 1);

        // Candidate offsets are the ones in force around l. An offset is right when it is still in force at the UTC instant it implies.
        Span<long> offsets = stackalloc long[8];
        int n = 0;
        if (start == 0)
        {
            n = Offer(offsets, n, tr[0].From);
        }

        for (int j = start; j < tr.Length && tr[j].Utc <= l + window; j++)
        {
            n = Offer(offsets, n, tr[j].From);
            n = Offer(offsets, n, tr[j].To);
        }

        if (n == 0)
        {
            n = Offer(offsets, n, OffsetAt(tr, l));
        }

        int valid = 0;
        long best = long.MaxValue;
        for (int k = 0; k < n; k++)
        {
            long u = l - offsets[k];
            if (OffsetAt(tr, u).Ticks == offsets[k])
            {
                valid++;
                best = Math.Min(best, u);
            }
        }

        if (valid > 0)
        {
            state = valid > 1 ? LocalTimeState.Ambiguous : LocalTimeState.Unique;
            return TimeMath.FromTicks(best, DateTimeKind.Utc);
        }

        // RFC 5545 §3.3.5: a local time inside the gap is read with the offset before the gap.
        state = LocalTimeState.Skipped;
        for (int j = start; j < tr.Length && tr[j].Utc <= l + window; j++)
        {
            if (tr[j].Utc + tr[j].From.Ticks <= l && l < tr[j].Utc + tr[j].To.Ticks)
            {
                return TimeMath.FromTicks(l - tr[j].From.Ticks, DateTimeKind.Utc);
            }
        }

        return TimeMath.FromTicks(l - OffsetAt(tr, l).Ticks, DateTimeKind.Utc);
    }

    private static int Offer(Span<long> offsets, int n, TimeSpan o)
    {
        for (int k = 0; k < n; k++)
        {
            if (offsets[k] == o.Ticks)
            {
                return n;
            }
        }

        if (n < offsets.Length)
        {
            offsets[n++] = o.Ticks;
        }

        return n;
    }

    private readonly record struct Transition(long Utc, TimeSpan From, TimeSpan To);

    private sealed record Observance(DateTime Start, TimeSpan From, TimeSpan To, RecurrenceRule[] Rules, IcsTime[] RDates, IcsPeriod[] RPeriods);
}
