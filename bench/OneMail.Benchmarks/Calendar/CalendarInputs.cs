using System.Globalization;
using System.Text;

namespace RecursiveAI.OneMail.Benchmarks;

/// <summary>
/// The calendar and contact files the rows run over. The holiday calendar comes from the fetched calendar corpus
/// (<c>tools/calendar/corpus.py fetch</c>, never committed); every other input is generated here with fixed values, so the same
/// bytes are used on every machine.
/// </summary>
internal static class CalendarInputs
{
    // The 50k-event rows take seconds per operation, so they run in "large" classes with fewer iterations (see LargeRowConfig).
    public static readonly string[] IcsNames = ["ics-invite", "ics-holidays"];

    public static readonly string[] IcsLargeNames = ["ics-50k"];

    public static readonly string[] IcsBuildNames = ["build-1", "build-1k"];

    public static readonly string[] IcsBuildLargeNames = ["build-50k"];

    public static IEnumerable<string> IcsAll => IcsNames.Concat(IcsLargeNames);

    public static IEnumerable<string> IcsBuildAll => IcsBuildNames.Concat(IcsBuildLargeNames);

    public static readonly string[] VcfNames = ["vcf-1", "vcf-10k"];

    public static readonly string[] VcfBuildNames = ["build-1", "build-10k"];

    public static readonly string[] RecurrenceNames = ["daily-utc", "weekly-floating", "monthly-ny", "yearly-berlin", "daily-ny-long"];

    private static string Corpus => Environment.GetEnvironmentVariable("ONEMAIL_CORPUS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "onemail", "corpus");

    /// <summary>The bytes of an iCalendar input.</summary>
    public static byte[] Ics(string name) => name switch
    {
        "ics-invite" => Encoding.UTF8.GetBytes(GenerateIcs(1, invite: true)),
        "ics-holidays" => File.ReadAllBytes(LargestHoliday()),
        "ics-50k" => Encoding.UTF8.GetBytes(GenerateIcs(50_000, invite: false)),
        _ => throw new ArgumentException("unknown ICS input " + name, nameof(name)),
    };

    /// <summary>The bytes of a vCard input.</summary>
    public static byte[] Vcf(string name) => name switch
    {
        "vcf-1" => Encoding.UTF8.GetBytes(GenerateVcf(1)),
        "vcf-10k" => Encoding.UTF8.GetBytes(GenerateVcf(10_000)),
        _ => throw new ArgumentException("unknown vCard input " + name, nameof(name)),
    };

    /// <summary>How many events (or cards) a build row creates.</summary>
    public static int BuildCount(string name) => name switch
    {
        "build-1" => 1,
        "build-1k" => 1_000,
        "build-10k" => 10_000,
        "build-50k" => 50_000,
        _ => throw new ArgumentException("unknown build input " + name, nameof(name)),
    };

    // The largest Thunderbird holiday calendar by size, then name: the same file wherever the corpus is the same.
    private static string LargestHoliday()
    {
        string dir = Path.Combine(Corpus, "calendar", "thunderbird");
        return Directory.EnumerateFiles(dir, "*.ics", SearchOption.AllDirectories)
            .OrderByDescending(f => new FileInfo(f).Length).ThenBy(f => f, StringComparer.Ordinal).First();
    }

    /// <summary>The recurrence cases: the calendar text, the start and end of the window, and the number of occurrences the case expects.</summary>
    public static (string Ics, DateTime Start, DateTime End) Recurrence(string name)
    {
        (string tz, string dtstart, string rule, int fromYear, int years) = name switch
        {
            "daily-utc" => ("", "DTSTART:20200101T090000Z", "FREQ=DAILY", 2020, 10),
            "weekly-floating" => ("", "DTSTART:20200101T090000", "FREQ=WEEKLY;BYDAY=MO,WE,FR", 2020, 10),
            "monthly-ny" => ("", "DTSTART;TZID=America/New_York:20200131T090000", "FREQ=MONTHLY;BYDAY=-1FR", 2020, 20),
            "yearly-berlin" => ("", "DTSTART;TZID=Europe/Berlin:20200308T020000", "FREQ=YEARLY;BYMONTH=3;BYDAY=2SU", 2020, 50),
            "daily-ny-long" => ("", "DTSTART;TZID=America/New_York:20200101T090000", "FREQ=DAILY", 2020, 50),
            _ => throw new ArgumentException("unknown recurrence input " + name, nameof(name)),
        };
        string ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//bench//EN\r\n" + tz + "BEGIN:VEVENT\r\nUID:r@example.com\r\nDTSTAMP:20200101T000000Z\r\n"
            + dtstart + "\r\nDURATION:PT1H\r\nSUMMARY:Series\r\nRRULE:" + rule + "\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        return (ics, new DateTime(fromYear, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(fromYear + years, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    /// <summary>The fields of generated event <paramref name="i"/>; the text generator and both build rows use these.</summary>
    public static (string Uid, DateTime Start, DateTime End, string Summary, string Description, string Location, string[] Attendees) Event(int i)
    {
        DateTime start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i % 700).AddHours(8 + (i % 9));
        return ($"evt-{i}@example.com", start, start.AddMinutes(30 + (i % 4) * 30), $"Planning meeting {i}",
            $"Agenda for meeting {i}: review the roadmap, assign owners, and agree the next steps, with notes kept in the shared folder.",
            $"Room {i % 50}", [$"user{i % 97}@example.com", $"user{(i + 1) % 97}@example.com", $"user{(i + 2) % 97}@example.com"]);
    }

    private static string GenerateIcs(int events, bool invite)
    {
        StringBuilder sb = new();
        Line(sb, "BEGIN:VCALENDAR");
        Line(sb, "VERSION:2.0");
        Line(sb, "PRODID:-//OneMail bench//EN");
        if (invite)
        {
            Line(sb, "METHOD:REQUEST");
        }

        for (int i = 0; i < events; i++)
        {
            var e = Event(i);
            Line(sb, "BEGIN:VEVENT");
            Line(sb, "UID:" + e.Uid);
            Line(sb, "DTSTAMP:20240101T000000Z");
            Line(sb, "DTSTART:" + e.Start.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
            Line(sb, "DTEND:" + e.End.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
            Line(sb, "SUMMARY:" + e.Summary);
            Line(sb, "DESCRIPTION:" + e.Description.Replace(",", "\\,", StringComparison.Ordinal));
            Line(sb, "LOCATION:" + e.Location);
            Line(sb, "ORGANIZER;CN=Alice Example:mailto:alice@example.com");
            foreach (string a in e.Attendees)
            {
                Line(sb, "ATTENDEE;CN=" + a[..a.IndexOf('@', StringComparison.Ordinal)] + ";PARTSTAT=ACCEPTED;RSVP=TRUE:mailto:" + a);
            }

            Line(sb, "CATEGORIES:Work,Planning");
            Line(sb, "STATUS:CONFIRMED");
            Line(sb, "SEQUENCE:0");
            if (i % 10 == 0)
            {
                Line(sb, "RRULE:FREQ=WEEKLY;COUNT=10");
            }

            if (invite)
            {
                Line(sb, "BEGIN:VALARM");
                Line(sb, "ACTION:DISPLAY");
                Line(sb, "DESCRIPTION:Reminder");
                Line(sb, "TRIGGER:-PT15M");
                Line(sb, "END:VALARM");
            }

            Line(sb, "END:VEVENT");
        }

        Line(sb, "END:VCALENDAR");
        return sb.ToString();
    }

    /// <summary>The fields of generated card <paramref name="i"/>.</summary>
    public static (string Given, string Family, string Email1, string Email2, string Phone1, string Phone2, string Org, string Street, string City, string Note) Card(int i) =>
        ($"Given{i}", $"Family{i % 1000}", $"given{i}@example.com", $"g{i}@work.example.org", $"+1-555-{i % 10000:D4}-{i % 7:D3}1", $"+44-20-7946-{i % 10000:D4}",
         $"Company {i % 300}, Inc.", $"{i % 999} Main Street", $"City{i % 120}",
         $"Met at the conference in {2000 + i % 25}; follows up about the proposal, the schedule and the budget for the next quarter.");

    private static string GenerateVcf(int cards)
    {
        StringBuilder sb = new();
        for (int i = 0; i < cards; i++)
        {
            var c = Card(i);
            Line(sb, "BEGIN:VCARD");
            Line(sb, "VERSION:3.0");
            Line(sb, $"N:{c.Family};{c.Given};;;");
            Line(sb, $"FN:{c.Given} {c.Family}");
            Line(sb, "ORG:" + c.Org.Replace(",", "\\,", StringComparison.Ordinal));
            Line(sb, "EMAIL;TYPE=INTERNET,PREF:" + c.Email1);
            Line(sb, "EMAIL;TYPE=INTERNET,WORK:" + c.Email2);
            Line(sb, "TEL;TYPE=CELL:" + c.Phone1);
            Line(sb, "TEL;TYPE=WORK,VOICE:" + c.Phone2);
            Line(sb, $"ADR;TYPE=HOME:;;{c.Street};{c.City};;12345;USA");
            Line(sb, "NOTE:" + c.Note.Replace(";", "\\;", StringComparison.Ordinal));
            Line(sb, "END:VCARD");
        }

        return sb.ToString();
    }

    // Writes one content line folded at 75 octets (RFC 5545 §3.1) with CRLF.
    private static void Line(StringBuilder sb, string line)
    {
        byte[] b = Encoding.UTF8.GetBytes(line);
        int pos = 0, limit = 75;
        while (b.Length - pos > limit)
        {
            sb.Append(Encoding.UTF8.GetString(b, pos, limit)).Append("\r\n ");
            pos += limit;
            limit = 74;
        }

        sb.Append(Encoding.UTF8.GetString(b, pos, b.Length - pos)).Append("\r\n");
    }
}
