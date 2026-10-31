using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.ContentLines;
using RecursiveAI.OneMail.Calendar.Ics;
using RecursiveAI.OneMail.Calendar.Recurrence;
using RecursiveAI.OneMail.Calendar.TimeZones;
using RecursiveAI.OneMail.Calendar.VCard;

namespace RecursiveAI.OneMail.Fuzz;

// Fuzz targets for the content-line reader and the ICS / vCard trees. Only typed results may escape: an exception, a
// failed invariant (bytes lost between reader and writer) or a hang is a finding.
internal static class CalendarTargets
{
    internal static Dictionary<string, Action<ReadOnlySpan<byte>>> Register(Dictionary<string, Action<ReadOnlySpan<byte>>> targets)
    {
        targets["contentlines"] = static d => ContentLines(d);
        targets["calendar"] = static d => Calendar(d);
        targets["recurrence"] = static d => Recurrence(d);
        return targets;
    }

    // The reader hands out every byte exactly once (after the optional byte order mark), however broken the input.
    private static void ContentLines(ReadOnlySpan<byte> data)
    {
        ContentLineReaderOptions options = new() { MaxLineBytes = 4096 };
        ContentLineReader r = new(data, options, []);
        long expected = r.Position;
        while (r.TryRead(out ContentLine line))
        {
            if (line.Offset != expected || line.Raw.Length == 0)
            {
                throw new InvalidOperationException("reader skipped or repeated bytes");
            }

            expected += line.Raw.Length;
            foreach (ContentParameterSpan p in line.EnumerateParameters())
            {
                foreach (ReadOnlySpan<byte> v in p.EnumerateValues())
                {
                    _ = v.Length;
                }
            }
        }

        if (expected != data.Length)
        {
            throw new InvalidOperationException("reader stopped early");
        }
    }

    // Parse, touch every typed accessor, write back: untouched input must come out byte for byte; an edited tree must
    // still write and parse again.
    private static void Calendar(ReadOnlySpan<byte> data)
    {
        byte[] bytes = data.ToArray();
        CalendarLimits limits = new() { MaxLineBytes = 8192, MaxDepth = 8, MaxComponents = 500, MaxProperties = 5000 };
        ContentDocument doc = ContentDocument.Parse(bytes, new ContentDocumentOptions { Limits = limits });
        foreach (Component c in doc.Components)
        {
            Touch(c, 0);
        }

        if (!doc.ToArray().AsSpan().SequenceEqual(bytes))
        {
            throw new InvalidOperationException("round trip changed the bytes");
        }

        foreach (Component c in doc.Components)
        {
            foreach (ContentProperty p in c.Properties)
            {
                p.Value += "x";
                p.Parameters.Add(new ContentParameter("X-FUZZ", "a\"b,c"));
                break;
            }
        }

        byte[] edited = doc.ToArray();
        _ = ContentDocument.Parse(edited);
    }

    // Expansion of whatever the bytes parse to, and of the bytes themselves as an RRULE on a zoned start. Limits are small so a
    // hostile rule ends quickly; only typed results may escape, and the occurrences must come out within the window and the cap.
    private static void Recurrence(ReadOnlySpan<byte> data)
    {
        ExpansionOptions options = new()
        {
            MaxOccurrences = 300,
            MaxIterationsWithoutHit = 3000,
            MaxInstancesScanned = 20_000,
            TimeZones = new TimeZoneResolverOptions { UseRuntimeDatabase = false },
        };
        OccurrenceWindow window = new(new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2041, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        byte[] bytes = data.ToArray();
        CalendarLimits limits = new() { MaxLineBytes = 4096, MaxDepth = 8, MaxComponents = 200, MaxProperties = 2000 };
        ContentDocument doc = ContentDocument.Parse(bytes, new ContentDocumentOptions { Limits = limits });
        foreach (IcsComponent cal in IcsDocument.Calendars(doc))
        {
            Check(CalendarRecurrence.Expand(cal, window, options, new ExpansionReport()), options);
        }

        // The input as a rule, on a start the rule is not tuned to: most inputs are not rules, the rest reach the engine.
        string text = System.Text.Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, 400)).Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal);
        foreach (string start in new[] { "DTSTART;TZID=Zone/Fuzz:20260328T023000", "DTSTART;VALUE=DATE:20240229", "DTSTART:20261031T235959Z" })
        {
            string ics = "BEGIN:VCALENDAR\r\nBEGIN:VTIMEZONE\r\nTZID:Zone/Fuzz\r\nBEGIN:DAYLIGHT\r\nDTSTART:19700329T020000\r\nTZOFFSETFROM:+0100\r\nTZOFFSETTO:+0200\r\nRRULE:FREQ=YEARLY;BYMONTH=3;BYDAY=-1SU\r\nEND:DAYLIGHT\r\n"
                + "BEGIN:STANDARD\r\nDTSTART:19701025T030000\r\nTZOFFSETFROM:+0200\r\nTZOFFSETTO:+0100\r\nRRULE:FREQ=YEARLY;BYMONTH=10;BYDAY=-1SU\r\nEND:STANDARD\r\nEND:VTIMEZONE\r\n"
                + "BEGIN:VEVENT\r\nUID:f\r\n" + start + "\r\nRRULE:" + text + "\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
            ContentDocument d2 = ContentDocument.Parse(System.Text.Encoding.UTF8.GetBytes(ics));
            foreach (IcsComponent cal in IcsDocument.Calendars(d2))
            {
                Check(CalendarRecurrence.Expand(cal, window, options, new ExpansionReport()), options);
            }
        }
    }

    private static void Check(IEnumerable<Occurrence> occurrences, ExpansionOptions options)
    {
        int n = 0;
        foreach (Occurrence _ in occurrences)
        {
            if (++n > options.MaxOccurrences)
            {
                throw new InvalidOperationException("more occurrences than the limit");
            }
        }
    }

    private static void Touch(Component c, int depth)
    {
        IcsComponent ics = new(c);
        _ = (ics.Summary, ics.Uid, ics.Method, ics.Start, ics.End, ics.Due, ics.Created, ics.Duration, ics.Trigger, ics.Geo, ics.Priority,
            ics.OffsetFrom, ics.OffsetTo, ics.Organizer, ics.Categories.Count, ics.Attendees.Count, ics.RecurrenceRules.Count,
            ics.ExceptionDates.Count, ics.RecurrenceDates.Count, ics.RecurrencePeriods.Count);
        VCardView card = new(c);
        _ = (card.Version, card.FormattedName, card.Name, card.Emails.Count, card.Phones.Count, card.Addresses.Count, card.Organization.Count,
            card.Photos.Count, card.Birthday, card.Groups.Count, card.Categories.Count);
        foreach (ContentProperty p in c.Properties)
        {
            _ = (p.GetText(), p.GetBytes(), p.Encoding, p.Charset);
        }

        if (depth < 16)
        {
            foreach (Component child in c.Components)
            {
                Touch(child, depth + 1);
            }
        }
    }
}
