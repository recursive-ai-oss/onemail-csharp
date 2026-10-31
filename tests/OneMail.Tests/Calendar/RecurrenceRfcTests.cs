using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.Ics;
using RecursiveAI.OneMail.Calendar.Recurrence;
using RecursiveAI.OneMail.Calendar.TimeZones;

namespace RecursiveAI.OneMail.Tests.Calendar;

/// <summary>Helpers shared by the recurrence tests.</summary>
internal static class RecurrenceTestKit
{
    internal static string DataRoot([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "testdata"));

    /// <summary>The US Eastern rules as a VTIMEZONE: 1987-2006 first Sunday of April to last Sunday of October, 2007 on second Sunday of March to first Sunday of November.</summary>
    internal const string NewYorkVTimeZone =
        "BEGIN:VTIMEZONE\r\nTZID:America/New_York\r\n"
        + "BEGIN:DAYLIGHT\r\nDTSTART:19870405T020000\r\nTZOFFSETFROM:-0500\r\nTZOFFSETTO:-0400\r\nRRULE:FREQ=YEARLY;UNTIL=20060402T070000Z;BYMONTH=4;BYDAY=1SU\r\nEND:DAYLIGHT\r\n"
        + "BEGIN:STANDARD\r\nDTSTART:19671029T020000\r\nTZOFFSETFROM:-0400\r\nTZOFFSETTO:-0500\r\nRRULE:FREQ=YEARLY;UNTIL=20061029T060000Z;BYMONTH=10;BYDAY=-1SU\r\nEND:STANDARD\r\n"
        + "BEGIN:DAYLIGHT\r\nDTSTART:20070311T020000\r\nTZOFFSETFROM:-0500\r\nTZOFFSETTO:-0400\r\nRRULE:FREQ=YEARLY;BYMONTH=3;BYDAY=2SU\r\nEND:DAYLIGHT\r\n"
        + "BEGIN:STANDARD\r\nDTSTART:20071104T020000\r\nTZOFFSETFROM:-0400\r\nTZOFFSETTO:-0500\r\nRRULE:FREQ=YEARLY;BYMONTH=11;BYDAY=1SU\r\nEND:STANDARD\r\n"
        + "END:VTIMEZONE\r\n";

    internal static IcsComponent Calendar(string body, string? vtimezone = null)
    {
        string text = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\n" + vtimezone + body + "END:VCALENDAR\r\n";
        ContentDocument doc = ContentDocument.Parse(Encoding.UTF8.GetBytes(text));
        return IcsDocument.Calendars(doc).First();
    }

    internal static string Event(params string[] lines) => "BEGIN:VEVENT\r\nUID:test@example\r\nDTSTAMP:20260101T000000Z\r\n" + string.Join("\r\n", lines) + "\r\nEND:VEVENT\r\n";

    internal static List<Occurrence> Expand(IcsComponent cal, ExpansionOptions? options = null, ExpansionReport? report = null, OccurrenceWindow? window = null) =>
        [.. CalendarRecurrence.Expand(cal, window ?? OccurrenceWindow.All, options, report)];

    internal static string Iso(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
}

public class RfcRecurrenceExamplesTests
{
    public static IEnumerable<object[]> Cases()
    {
        string path = Path.Combine(RecurrenceTestKit.DataRoot(), "calendar", "recurrence", "rfc5545-3.8.5.3.jsonl");
        int i = 0;
        foreach (string line in File.ReadAllLines(path))
        {
            JsonElement e = JsonSerializer.Deserialize<JsonElement>(line);
            int alt = 0;
            foreach (JsonElement a in e.GetProperty("alternatives").EnumerateArray())
            {
                yield return [i, alt++, false];
                yield return [i, alt - 1, true];
            }

            i++;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ExpandsToTheListedOccurrences(int example, int alternative, bool inlineTimeZone)
    {
        string path = Path.Combine(RecurrenceTestKit.DataRoot(), "calendar", "recurrence", "rfc5545-3.8.5.3.jsonl");
        JsonElement e = JsonSerializer.Deserialize<JsonElement>(File.ReadLines(path).ElementAt(example));
        string title = e.GetProperty("title").GetString()!;
        string[] props = [e.GetProperty("dtstart").GetString()!, .. e.GetProperty("alternatives")[alternative].EnumerateArray().Select(x => x.GetString()!)];
        bool prefix = e.GetProperty("prefix").GetBoolean();
        JsonElement[] expected = [.. e.GetProperty("expected").EnumerateArray()];
        if (title.StartsWith("Every 3 hours", StringComparison.Ordinal))
        {
            // The RFC lists 15:00 as well, but UNTIL=17:00Z is 13:00 EDT, so the rule ends after 12:00. We follow the rule, not the listing.
            expected = expected[..2];
        }

        ExpansionOptions options = new()
        {
            MaxOccurrences = prefix ? expected.Length + 5 : 100_000,
            TimeZones = new TimeZoneResolverOptions { UseRuntimeDatabase = !inlineTimeZone },
        };
        IcsComponent cal = RecurrenceTestKit.Calendar(RecurrenceTestKit.Event(props), inlineTimeZone ? RecurrenceTestKit.NewYorkVTimeZone : null);
        ExpansionReport report = new();
        List<Occurrence> got = RecurrenceTestKit.Expand(cal, options, report);

        Assert.True(got.Count >= expected.Length, $"{title}: expected at least {expected.Length}, got {got.Count}");
        if (!prefix)
        {
            Assert.Equal(expected.Length, got.Count);
        }

        for (int k = 0; k < expected.Length; k++)
        {
            string local = expected[k].GetProperty("local").GetString()!;
            int offset = expected[k].GetProperty("offset").GetInt32();
            Assert.Equal(local, RecurrenceTestKit.Iso(got[k].Start.Value));
            DateTime utc = DateTime.ParseExact(local, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture).AddHours(-offset);
            Assert.Equal(utc, got[k].StartUtc);
        }

        if (!prefix)
        {
            Assert.DoesNotContain(report.Defects, d => d.IsLimit);
        }
    }
}
