using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.Ics;
using RecursiveAI.OneMail.Display;

namespace RecursiveAI.OneMail.Fuzz;

// The calendar card on hostile input. Byte 0 picks the shape:
//   0: the rest is an iCalendar file: every card is built, rendered as HTML and text, and the HTML must hold no raw '<' outside the fixed tags;
//   1: the rest is patched into a valid series (16-bit offset, value): built with a display zone, so zone resolution and expansion run too;
//   2: the rest is an RRULE value: described, and the sentence must parse back (checked by length only here; the unit tests do the full parse-back).
// Nothing may throw, hang or grow without bound.
internal static class CalcardTarget
{
    private const string Series =
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nMETHOD:REQUEST\r\nBEGIN:VEVENT\r\nUID:a@example.com\r\nDTSTAMP:20260101T000000Z\r\n" +
        "DTSTART;TZID=Europe/Paris:20260105T100000\r\nDTEND;TZID=Europe/Paris:20260105T103000\r\nRRULE:FREQ=WEEKLY;INTERVAL=2;COUNT=10;BYDAY=MO,TH\r\n" +
        "EXDATE;TZID=Europe/Paris:20260108T100000\r\nSUMMARY:Series\r\nLOCATION:Room\r\nURL:https://example.com/x\r\nCONFERENCE:https://meet.example.com/y\r\n" +
        "ORGANIZER;CN=Org:mailto:org@example.com\r\nATTENDEE;CN=A;PARTSTAT=ACCEPTED;ROLE=REQ-PARTICIPANT:mailto:a@example.com\r\nDESCRIPTION:Hello\\nworld\r\n" +
        "ATTACH;FILENAME=f.txt:https://example.com/f.txt\r\nEND:VEVENT\r\n" +
        "BEGIN:VEVENT\r\nUID:a@example.com\r\nDTSTAMP:20260101T000000Z\r\nRECURRENCE-ID;TZID=Europe/Paris:20260119T100000\r\nDTSTART;TZID=Europe/Paris:20260120T110000\r\nSUMMARY:Moved\r\nEND:VEVENT\r\n" +
        "END:VCALENDAR\r\n";

    private static readonly byte[] SeriesBytes = System.Text.Encoding.ASCII.GetBytes(Series);
    private static readonly CalendarCardOptions Zoned = new() { DisplayTimeZone = "Asia/Tokyo", NextOccurrences = 20, After = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc) };

    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        ReadOnlySpan<byte> rest = data[1..];
        switch (data[0] % 3)
        {
            case 0:
                Build(rest.ToArray(), null);
                break;
            case 1:
                byte[] ics = (byte[])SeriesBytes.Clone();
                for (int i = 0; i + 2 < rest.Length; i += 3)
                {
                    ics[(rest[i] | (rest[i + 1] << 8)) % ics.Length] = rest[i + 2];
                }

                Build(ics, Zoned);
                break;
            default:
                string text = System.Text.Encoding.Latin1.GetString(rest);
                if (RecurrenceRule.TryParse(text, null, out RecurrenceRule rule))
                {
                    string? s = CalendarCardRecurrence.Describe(rule, new IcsTime(new DateTime(2026, 1, 5, 9, 0, 0), IcsTimeKind.Floating));
                    if (s is not null && s.Length == 0)
                    {
                        throw new InvalidOperationException("empty sentence");
                    }
                }

                break;
        }
    }

    private static void Build(byte[] ics, CalendarCardOptions? options)
    {
        foreach (CalendarCard c in CalendarCard.FromIcs(ics, options))
        {
            string html = c.ToHtml(linkUrls: true);
            _ = c.ToText();
            // Every '<' in the output starts one of the fixed tags; event text can only be there escaped.
            foreach (int i in Indexes(html))
            {
                string t = html.Substring(i, Math.Min(8, html.Length - i));
                if (!(t.StartsWith("<div ", StringComparison.Ordinal) || t.StartsWith("<p ", StringComparison.Ordinal) || t.StartsWith("<ul ", StringComparison.Ordinal)
                    || t.StartsWith("<li ", StringComparison.Ordinal) || t.StartsWith("<span ", StringComparison.Ordinal) || t.StartsWith("<strong>", StringComparison.Ordinal)
                    || t.StartsWith("<a href=", StringComparison.Ordinal) || t.StartsWith("</", StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException("raw markup in card html");
                }
            }
        }
    }

    private static IEnumerable<int> Indexes(string s)
    {
        for (int i = s.IndexOf('<', StringComparison.Ordinal); i >= 0; i = s.IndexOf('<', i + 1))
        {
            yield return i;
        }
    }
}
