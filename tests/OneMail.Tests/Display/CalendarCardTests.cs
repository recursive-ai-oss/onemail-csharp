using System.Text;
using RecursiveAI.OneHtml;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.Ics;
using RecursiveAI.OneMail.Display;

namespace RecursiveAI.OneMail.Tests.Display;

public class CalendarCardTests
{
    private static IcsComponent Cal(string text) =>
        new(ContentDocument.Parse(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal)).Components.First(c => c.IsNamed("VCALENDAR")));

    private static string Ics(string method, params string[] eventLines) =>
        "BEGIN:VCALENDAR\nVERSION:2.0\nPRODID:-//x//y//EN\n" + (method.Length > 0 ? "METHOD:" + method + "\n" : string.Empty)
        + "BEGIN:VEVENT\nUID:u1@example.com\nDTSTAMP:20260101T000000Z\n" + string.Join("\n", eventLines) + "\nEND:VEVENT\nEND:VCALENDAR\n";

    private static CalendarCard One(string method, string[] lines, CalendarCardOptions? o = null) => CalendarCard.Create(Cal(Ics(method, lines)), o)!;

    private static IcsTime Start(string v) => IcsTime.TryParse(v, null, out IcsTime t) ? t : throw new InvalidOperationException();

    private static RecurrenceRule Rule(string text) => RecurrenceRule.TryParse(text, null, out RecurrenceRule r) ? r : throw new InvalidOperationException(text);

    private static readonly string[] AllowedAttributes = ["class", "style", "href", "rel"];

    // The card's HTML must parse with no errors, use only the fixed element set and attributes, and carry no attribute value from the event.
    internal static void AssertInertHtml(string html, bool linksAllowed = false)
    {
        Document doc = Document.Parse("<!DOCTYPE html><title>t</title>" + html, new ParseOptions { RecordErrors = true });
        Assert.Empty(doc.ParseErrors);
        string[] elements = ["div", "p", "ul", "li", "span", "strong", "a"];
        Node body = doc.DocumentElement;
        foreach (Node el in body.DescendantElements)
        {
            string name = el.LocalName;
            if (name is "html" or "head" or "body" or "title")
            {
                continue;
            }

            Assert.Contains(name, elements);
            Assert.True(name != "a" || linksAllowed);
            foreach (Attr a in el.Attributes)
            {
                Assert.Contains(a.LocalName, AllowedAttributes);
                if (a.LocalName == "href")
                {
                    Assert.True(a.Value.StartsWith("http://", StringComparison.Ordinal) || a.Value.StartsWith("https://", StringComparison.Ordinal) || a.Value.StartsWith("mailto:", StringComparison.Ordinal));
                }
            }
        }
    }

    [Theory]
    [InlineData("FREQ=DAILY", "Daily")]
    [InlineData("FREQ=DAILY;INTERVAL=3", "Every 3 days")]
    [InlineData("FREQ=DAILY;BYDAY=MO,TU,WE,TH,FR", "Every weekday")]
    [InlineData("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;UNTIL=20261130", "Every 2 weeks on Monday and Wednesday, until 30 Nov 2026")]
    [InlineData("FREQ=WEEKLY;BYDAY=MO,WE,FR;COUNT=10", "Weekly on Monday, Wednesday and Friday, 10 times")]
    [InlineData("FREQ=MONTHLY;BYDAY=-1FR", "Monthly on the last Friday")]
    [InlineData("FREQ=MONTHLY;BYDAY=FR;BYSETPOS=-1", "Monthly on the last Friday")]
    [InlineData("FREQ=MONTHLY;BYDAY=MO,TU,WE,TH,FR;BYSETPOS=-1", "Monthly on the last weekday")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=1,15", "Monthly on day 1 and day 15")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=-1", "Monthly on the last day")]
    [InlineData("FREQ=YEARLY;BYMONTH=3;BYDAY=1MO", "Yearly on the first Monday of March")]
    [InlineData("FREQ=YEARLY", "Yearly on 5 January")]
    [InlineData("FREQ=HOURLY;INTERVAL=2", "Every 2 hours")]
    public void RecurrenceSentences(string rrule, string expected) =>
        Assert.Equal(expected, CalendarCardRecurrence.Describe(Rule(rrule), Start("20260105T090000")));

    [Theory]
    [InlineData("FREQ=DAILY;BYHOUR=9,17")]
    [InlineData("FREQ=YEARLY;BYWEEKNO=20")]
    [InlineData("FREQ=YEARLY;BYYEARDAY=100")]
    [InlineData("FREQ=MONTHLY;BYDAY=MO,TU;BYSETPOS=1,2")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=13;BYDAY=FR")]
    [InlineData("FREQ=WEEKLY;INTERVAL=2;WKST=SU;BYDAY=MO,TH")]
    [InlineData("FREQ=SECONDLY")]
    [InlineData("FREQ=YEARLY;BYMONTH=1,6")]
    [InlineData("FREQ=DAILY;COUNT=3;UNTIL=20270101")]
    public void RulesWithoutASentenceFallBack(string rrule) =>
        Assert.Null(CalendarCardRecurrence.Describe(Rule(rrule), Start("20260105T090000")));

    [Fact]
    public void EveryDescribedRuleParsesBackToTheSameRule()
    {
        string[] freqs = ["HOURLY", "MINUTELY", "DAILY", "WEEKLY", "MONTHLY", "YEARLY"];
        string[] intervals = ["", ";INTERVAL=2", ";INTERVAL=5"];
        string[] ends = ["", ";COUNT=1", ";COUNT=12", ";UNTIL=20271231", ";UNTIL=20271231T235959Z"];
        string[] by =
        [
            "", ";BYDAY=MO", ";BYDAY=TU,TH", ";BYDAY=MO,TU,WE,TH,FR", ";BYDAY=SA,SU", ";BYDAY=1MO", ";BYDAY=-1FR", ";BYDAY=2TU,4TU", ";BYDAY=5SU",
            ";BYDAY=MO,TU,WE,TH,FR;BYSETPOS=1", ";BYDAY=MO,TU,WE,TH,FR;BYSETPOS=-2", ";BYDAY=SA,SU;BYSETPOS=-1", ";BYDAY=WE;BYSETPOS=3",
            ";BYMONTHDAY=15", ";BYMONTHDAY=1,15,28", ";BYMONTHDAY=-1", ";BYMONTHDAY=-3", ";BYMONTH=3", ";BYMONTH=11;BYMONTHDAY=5",
            ";BYMONTH=3;BYDAY=1MO", ";BYMONTH=10;BYDAY=-1SU", ";BYMONTH=2;BYMONTHDAY=-1", ";BYMONTH=6;BYMONTHDAY=1,2", ";BYHOUR=9", ";BYYEARDAY=1", ";WKST=SU",
        ];
        IcsTime start = Start("20260217T090000");
        int described = 0, fallback = 0;
        foreach (string f in freqs)
        {
            foreach (string i in intervals)
            {
                foreach (string e in ends)
                {
                    foreach (string b in by)
                    {
                        string text = "FREQ=" + f + i + b + e;
                        if (!RecurrenceRule.TryParse(text, null, out RecurrenceRule rule))
                        {
                            continue;
                        }

                        string? sentence = CalendarCardRecurrence.Describe(rule, start);
                        if (sentence is null)
                        {
                            fallback++;
                            continue;
                        }

                        described++;
                        Assert.True(SentenceParser.KeyOfRule(rule, start) == SentenceParser.Parse(sentence), text + " -> " + sentence);
                    }
                }
            }
        }

        Assert.True(described > 500 && fallback > 100, described + "/" + fallback);
    }

    [Theory]
    [InlineData("REQUEST", 0, "Invitation")]
    [InlineData("REQUEST", 2, "Updated invitation")]
    [InlineData("CANCEL", 1, "Cancelled")]
    [InlineData("COUNTER", 0, "Counter-proposal")]
    [InlineData("DECLINECOUNTER", 0, "Counter-proposal declined")]
    [InlineData("PUBLISH", 0, "Event")]
    [InlineData("ADD", 0, "Added to the event")]
    [InlineData("REFRESH", 0, "Request for the latest version")]
    [InlineData("FROBNICATE", 0, "Calendar item")]
    public void MethodsBecomeActions(string method, int sequence, string action)
    {
        CalendarCard c = One(method, ["DTSTART:20261130T140000Z", "DTEND:20261130T150000Z", "SUMMARY:Planning", "SEQUENCE:" + sequence]);
        Assert.Equal(action, c.Action);
        Assert.Equal("Planning", c.Summary);
        Assert.Equal(method == "CANCEL" ? "Cancelled" : null, c.Status);
    }

    [Theory]
    [InlineData("ACCEPTED", "Alice accepted")]
    [InlineData("DECLINED", "Alice declined")]
    [InlineData("TENTATIVE", "Alice tentatively accepted")]
    public void RepliesNameWhoAnswered(string partstat, string action) =>
        Assert.Equal(action, One("REPLY", ["DTSTART:20261130T140000Z", "ATTENDEE;CN=Alice;PARTSTAT=" + partstat + ":mailto:alice@example.com"]).Action);

    [Fact]
    public void WhenIsConvertedAndTheOriginalKept()
    {
        string[] ev = ["DTSTART;TZID=Europe/Paris:20261130T140000", "DTEND;TZID=Europe/Paris:20261130T150000", "SUMMARY:Sync"];
        Assert.Equal("Mon, 30 Nov 2026 14:00–15:00 (Europe/Paris, UTC+01:00)", One("", ev).When);
        CalendarCard c = One("", ev, new CalendarCardOptions { DisplayTimeZone = "America/New_York" });
        Assert.Equal("Mon, 30 Nov 2026 08:00–09:00 (America/New_York, UTC-05:00)", c.When);
        Assert.Equal("Mon, 30 Nov 2026 14:00–15:00 (Europe/Paris, UTC+01:00)", c.OriginalTime);
        Assert.Equal("Mon, 30 Nov 2026 13:00–14:00 (UTC)", One("", ["DTSTART:20261130T130000Z", "DTEND:20261130T140000Z"]).When);
        Assert.Equal("All day, Mon, 30 Nov 2026", One("", ["DTSTART;VALUE=DATE:20261130", "DTEND;VALUE=DATE:20261201"]).When);
        Assert.Equal("All day, Mon, 30 Nov 2026 to Wed, 2 Dec 2026", One("", ["DTSTART;VALUE=DATE:20261130", "DTEND;VALUE=DATE:20261203"]).When);
        Assert.Equal("Mon, 30 Nov 2026 23:00 to Tue, 1 Dec 2026 01:00 (Asia/Tokyo, UTC+09:00)",
            One("", ["DTSTART:20261130T140000Z", "DTEND:20261130T160000Z"], new CalendarCardOptions { DisplayTimeZone = "Asia/Tokyo" }).When);
    }

    [Fact]
    public void UnknownZonesAreDefectsNotExceptions()
    {
        CalendarCard c = One("", ["DTSTART;TZID=Nowhere/Land:20261130T140000", "SUMMARY:x"], new CalendarCardOptions { DisplayTimeZone = "Not/AZone" });
        Assert.Contains(c.Defects, d => d.Kind == CalendarCardDefectKind.UnresolvedZone);
        Assert.Contains(c.Defects, d => d.Kind == CalendarCardDefectKind.DisplayZoneUnresolved);
        Assert.StartsWith("Mon, 30 Nov 2026 14:00", c.When, StringComparison.Ordinal);
    }

    [Fact]
    public void RecurringEventsListTheirNextOccurrencesFromTheExpansion()
    {
        CalendarCard c = One("", ["DTSTART;TZID=Europe/Paris:20261130T140000", "DTEND;TZID=Europe/Paris:20261130T150000", "RRULE:FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;UNTIL=20261231",
            "EXDATE;TZID=Europe/Paris:20261202T140000"]);
        Assert.Equal("Every 2 weeks on Monday and Wednesday, until 31 Dec 2026 (except 2 Dec 2026)", c.Recurrence);
        Assert.Equal(["Mon, 30 Nov 2026 14:00–15:00 (Europe/Paris, UTC+01:00)", "Mon, 14 Dec 2026 14:00–15:00 (Europe/Paris, UTC+01:00)", "Wed, 16 Dec 2026 14:00–15:00 (Europe/Paris, UTC+01:00)", "Mon, 28 Dec 2026 14:00–15:00 (Europe/Paris, UTC+01:00)", "Wed, 30 Dec 2026 14:00–15:00 (Europe/Paris, UTC+01:00)"], c.Occurrences);
        Assert.Equal(5, c.OccurrenceData.Count);
    }

    [Fact]
    public void CustomRulesSayCustomRule()
    {
        CalendarCard c = One("", ["DTSTART:20261130T140000Z", "RRULE:FREQ=YEARLY;BYWEEKNO=20;BYDAY=MO"]);
        Assert.Equal("Repeats (custom rule)", c.Recurrence);
        Assert.False(c.RecurrenceDescribed);
        Assert.Contains(c.Defects, d => d.Kind == CalendarCardDefectKind.RecurrenceNotDescribed);
        Assert.NotEmpty(c.Occurrences);
    }

    [Fact]
    public void PeopleLinksAndDescription()
    {
        CalendarCard c = One("REQUEST", ["DTSTART:20261130T140000Z", "SUMMARY:Kickoff", "LOCATION:Room 4", "URL:https://example.com/e?a=1&b=2", "CONFERENCE;VALUE=URI:https://meet.example.com/x",
            "X-MS-OLK-NETSHOWURL:javascript:alert(1)", "ORGANIZER;CN=Boss:mailto:boss@example.com",
            "ATTENDEE;CN=Alice;ROLE=REQ-PARTICIPANT;PARTSTAT=ACCEPTED:mailto:alice@example.com", "ATTENDEE;CUTYPE=ROOM;ROLE=NON-PARTICIPANT:mailto:room4@example.com",
            "DESCRIPTION:Line one\\nLine two", "ATTACH;FILENAME=agenda.pdf:https://example.com/a.pdf", "STATUS:TENTATIVE"]);
        Assert.Equal("Boss (boss@example.com)", c.Organizer);
        Assert.Equal("Tentative", c.Status);
        Assert.Equal(["https://meet.example.com/x", "https://example.com/e?a=1&b=2"], c.Links.Select(l => l.Url));
        Assert.Contains(c.Defects, d => d.Kind == CalendarCardDefectKind.UrlRejected);
        Assert.Equal(new CalendarCardAttendee("Alice", "alice@example.com", "required", "accepted", null), c.Attendees[0]);
        Assert.Equal("room", c.Attendees[1].Kind);
        Assert.Equal("Line one\nLine two", c.Description);
        Assert.Equal(["agenda.pdf"], c.Attachments);
        string text = c.ToText();
        Assert.Contains("Invitation [Tentative]\nKickoff\n", text, StringComparison.Ordinal);
        Assert.Contains("Alice (alice@example.com) – required, accepted", text, StringComparison.Ordinal);
        AssertInertHtml(c.ToHtml());
        AssertInertHtml(c.ToHtml(linkUrls: true), linksAllowed: true);
        Assert.DoesNotContain("<a ", c.ToHtml(), StringComparison.Ordinal);
        Assert.Contains("<a href=\"https://example.com/e?a=1&amp;b=2\"", c.ToHtml(linkUrls: true), StringComparison.Ordinal);
    }

    [Fact]
    public void HostileTextIsInert()
    {
        string x = "<script>alert(1)</script><img src=x onerror=alert(1)>\"'&\u202e";
        CalendarCard c = One("REQUEST", ["DTSTART:20261130T140000Z", "SUMMARY:" + x.Replace("\\", "\\\\", StringComparison.Ordinal), "LOCATION:" + x, "DESCRIPTION:" + x,
            "ORGANIZER;CN=\"" + x.Replace("\"", "'", StringComparison.Ordinal) + "\":mailto:a@b", "ATTENDEE;CN=\"" + x.Replace("\"", "'", StringComparison.Ordinal) + "\":mailto:\"" + x.Replace("\"", "'", StringComparison.Ordinal),
            "URL:javascript:alert(1)", "ATTACH:javascript:alert(1)//" + x, "ATTACH;FILENAME=\"" + x.Replace("\"", "'", StringComparison.Ordinal) + "\":data:text/html,x"]);
        string html = c.ToHtml(linkUrls: true);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\u202e", html, StringComparison.Ordinal);
        Assert.DoesNotContain("javascript:", string.Join("", c.Links.Select(l => l.Url)), StringComparison.Ordinal);
        AssertInertHtml(html, linksAllowed: true);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void HugeInputsAreBounded()
    {
        StringBuilder sb = new();
        sb.Append("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:h\r\nDTSTART:20261130T140000Z\r\nRRULE:FREQ=YEARLY;BYMONTH=2;BYMONTHDAY=30\r\n");
        sb.Append("DESCRIPTION:").Append('a', 3_000_000).Append("\r\n");
        sb.Append("SUMMARY:").Append('s', 100_000).Append("\r\n");
        for (int i = 0; i < 10_000; i++)
        {
            sb.Append("ATTENDEE;CN=n").Append(i).Append(":mailto:a").Append(i).Append("@example.com\r\n");
        }

        sb.Append("END:VEVENT\r\nEND:VCALENDAR\r\n");
        CalendarCard c = CalendarCard.FromIcs(Encoding.UTF8.GetBytes(sb.ToString()))[0];
        Assert.Equal(50, c.Attendees.Count);
        Assert.Equal(10_000, c.AttendeeCount);
        Assert.True(c.Description!.Length <= 1001);
        Assert.True(c.Summary.Length <= 201);
        Assert.Contains(c.Defects, d => d.Kind == CalendarCardDefectKind.AttendeesTruncated);
        Assert.Contains(c.Defects, d => d.Kind == CalendarCardDefectKind.DescriptionTruncated);
        Assert.Single(c.OccurrenceData); // a rule that never matches: DTSTART only, found without scanning to the end of time
        Assert.True(c.ToHtml().Length < 20_000);
    }

    [Fact]
    public void ManyEventsAreCappedWithADefect()
    {
        StringBuilder sb = new("BEGIN:VCALENDAR\nVERSION:2.0\n");
        for (int i = 0; i < 100; i++)
        {
            sb.Append("BEGIN:VEVENT\nUID:e").Append(i).Append("\nDTSTART:20261130T140000Z\nEND:VEVENT\n");
        }

        sb.Append("END:VCALENDAR\n");
        IReadOnlyList<CalendarCard> cards = CreateAllOf(sb.ToString());
        Assert.Equal(20, cards.Count);
        Assert.Contains(cards[0].Defects, d => d.Kind == CalendarCardDefectKind.EventsTruncated && d.Detail == "80");
    }

    private static IReadOnlyList<CalendarCard> CreateAllOf(string ics) => CalendarCard.CreateAll(Cal(ics));

    [Fact]
    public void OverridesJoinTheirSeriesAndNoEventGivesNoCard()
    {
        IReadOnlyList<CalendarCard> cards = CreateAllOf("BEGIN:VCALENDAR\nBEGIN:VEVENT\nUID:a\nDTSTART:20261130T140000Z\nRRULE:FREQ=DAILY;COUNT=3\nEND:VEVENT\nBEGIN:VEVENT\nUID:a\nRECURRENCE-ID:20261201T140000Z\nDTSTART:20261201T150000Z\nEND:VEVENT\nEND:VCALENDAR\n");
        Assert.Single(cards);
        Assert.EndsWith("(1 changed occurrence)", cards[0].Recurrence, StringComparison.Ordinal);
        Assert.Empty(CalendarCard.FromIcs("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n"u8.ToArray()));
        Assert.Empty(CalendarCard.FromIcs("garbage"u8.ToArray()));
    }
}
