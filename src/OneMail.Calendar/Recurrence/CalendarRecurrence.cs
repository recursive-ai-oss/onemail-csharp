using RecursiveAI.OneMail.Calendar.Ics;
using RecursiveAI.OneMail.Calendar.TimeZones;

namespace RecursiveAI.OneMail.Calendar.Recurrence;

/// <summary>Expands every event, to-do and journal of a calendar into occurrences.</summary>
public static class CalendarRecurrence
{
    /// <summary>
    /// Expands the VEVENT, VTODO and VJOURNAL components of a VCALENDAR over a window, merged in start order. Components that
    /// share a UID form one series: the one without RECURRENCE-ID is the master, the others are its overrides. An override
    /// whose UID has no master is yielded as a single occurrence with <see cref="RecurrenceDefectKind.OrphanOverride"/>.
    /// Zones are resolved through the calendar's own VTIMEZONE components first (see <see cref="TimeZoneResolver"/>).
    /// </summary>
    /// <param name="calendar">A VCALENDAR component.</param>
    /// <param name="window">The window.</param>
    /// <param name="options">Limits and choices; null for the defaults.</param>
    /// <param name="report">Receives defects; null discards them.</param>
    /// <returns>The occurrences, lazily. The total is capped by <see cref="ExpansionOptions.MaxOccurrences"/>.</returns>
    public static IEnumerable<Occurrence> Expand(IcsComponent calendar, OccurrenceWindow window, ExpansionOptions? options = null, ExpansionReport? report = null)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        return ExpandCore(calendar, window, options ?? new ExpansionOptions(), report ?? new ExpansionReport());
    }

    private static IEnumerable<Occurrence> ExpandCore(IcsComponent calendar, OccurrenceWindow window, ExpansionOptions options, ExpansionReport report)
    {
        TimeZoneResolver zones = new(calendar, options.TimeZones, report);
        List<RecurrenceSet> sets = [];
        Dictionary<string, List<IcsComponent>> overridesByUid = new(StringComparer.Ordinal);
        List<IcsComponent> masters = [];
        foreach (IcsComponent c in calendar.Children)
        {
            if (!(c.Component.IsNamed("VEVENT") || c.Component.IsNamed("VTODO") || c.Component.IsNamed("VJOURNAL")))
            {
                continue;
            }

            if (c.RecurrenceId is not null && c.Uid is { } uid)
            {
                if (!overridesByUid.TryGetValue(uid, out List<IcsComponent>? list))
                {
                    overridesByUid[uid] = list = [];
                }

                list.Add(c);
            }
            else
            {
                masters.Add(c);
            }
        }

        HashSet<string> used = new(StringComparer.Ordinal);
        foreach (IcsComponent m in masters)
        {
            List<IcsComponent>? overrides = null;
            if (m.Uid is { } uid && used.Add(uid))
            {
                overridesByUid.TryGetValue(uid, out overrides);
            }

            sets.Add(new RecurrenceSet(m, overrides, zones, options, report));
        }

        foreach ((string uid, List<IcsComponent> list) in overridesByUid)
        {
            if (!used.Contains(uid))
            {
                // Without a master the override is all there is: it stands for itself.
                foreach (IcsComponent o in list)
                {
                    report.Add(RecurrenceDefectKind.OrphanOverride, uid);
                    sets.Add(new RecurrenceSet(o, null, zones, options, report));
                }
            }
        }

        PriorityQueue<(IEnumerator<Occurrence> Cursor, Occurrence Current), long> queue = new();
        try
        {
            foreach (RecurrenceSet set in sets)
            {
                IEnumerator<Occurrence> e = set.Expand(window).GetEnumerator();
                if (e.MoveNext())
                {
                    queue.Enqueue((e, e.Current), Key(e.Current));
                }
                else
                {
                    e.Dispose();
                }
            }

            int produced = 0;
            while (queue.TryDequeue(out (IEnumerator<Occurrence> Cursor, Occurrence Current) top, out _))
            {
                if (produced++ >= options.MaxOccurrences)
                {
                    report.Add(RecurrenceDefectKind.OccurrenceLimit, "calendar");
                    top.Cursor.Dispose();
                    yield break;
                }

                yield return top.Current;
                if (top.Cursor.MoveNext())
                {
                    queue.Enqueue((top.Cursor, top.Cursor.Current), Key(top.Cursor.Current));
                }
                else
                {
                    top.Cursor.Dispose();
                }
            }
        }
        finally
        {
            while (queue.TryDequeue(out (IEnumerator<Occurrence> Cursor, Occurrence Current) rest, out _))
            {
                rest.Cursor.Dispose();
            }
        }
    }

    private static long Key(Occurrence o) => o.StartUtc?.Ticks ?? o.Start.Value.Ticks;
}
