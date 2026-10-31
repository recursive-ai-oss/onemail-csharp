using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.ContentLines;
using RecursiveAI.OneMail.Calendar.Ics;
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
