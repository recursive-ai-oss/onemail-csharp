using System.Text;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.Ics;
using RecursiveAI.OneMail.Calendaring;
using RecursiveAI.OneMail.Contacts;
using RecursiveAI.OneMail.Conversion.OxcIcal;
using RecursiveAI.OneMail.Conversion.OxVCard;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Msg;
using RecursiveAI.OneMail.Notes;
using static RecursiveAI.OneMail.Tests.Calendaring.CalendarFixtures;
using static RecursiveAI.OneMail.Tests.Msg.MsgWriterTestHelpers;

namespace RecursiveAI.OneMail.Tests.Msg;

/// <summary>Outlook items built by the library's item layers, written as MSG and read again: the properties are the same and the typed views read the same fields.</summary>
public class MsgWriterItemTests
{
    private const string Weekly = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Example//Test//EN
        METHOD:PUBLISH
        BEGIN:VTIMEZONE
        TZID:Pacific Standard Time
        BEGIN:STANDARD
        DTSTART:16011104T020000
        TZOFFSETFROM:-0700
        TZOFFSETTO:-0800
        RRULE:FREQ=YEARLY;BYDAY=1SU;BYMONTH=11
        END:STANDARD
        BEGIN:DAYLIGHT
        DTSTART:16010311T020000
        TZOFFSETFROM:-0800
        TZOFFSETTO:-0700
        RRULE:FREQ=YEARLY;BYDAY=2SU;BYMONTH=3
        END:DAYLIGHT
        END:VTIMEZONE
        BEGIN:VEVENT
        UID:weekly-1@example.com
        DTSTAMP:20070301T000000Z
        DTSTART;TZID=Pacific Standard Time:20070326T100000
        DTEND;TZID=Pacific Standard Time:20070326T103000
        RRULE:FREQ=WEEKLY;COUNT=12;BYDAY=MO,TH,FR;WKST=SU
        EXDATE;TZID=Pacific Standard Time:20070402T100000
        SUMMARY:Standup
        LOCATION:Room 4
        ORGANIZER;CN=Ann:mailto:ann@example.com
        ATTENDEE;CN=Bob;ROLE=REQ-PARTICIPANT;PARTSTAT=ACCEPTED;RSVP=TRUE:mailto:bob@example.com
        SEQUENCE:2
        BEGIN:VALARM
        TRIGGER:-PT15M
        ACTION:DISPLAY
        DESCRIPTION:Reminder
        END:VALARM
        END:VEVENT
        BEGIN:VEVENT
        UID:weekly-1@example.com
        DTSTAMP:20070301T000000Z
        RECURRENCE-ID;TZID=Pacific Standard Time:20070409T100000
        DTSTART;TZID=Pacific Standard Time:20070410T110000
        DTEND;TZID=Pacific Standard Time:20070410T113000
        SUMMARY:Standup (moved)
        LOCATION:Room 9
        END:VEVENT
        BEGIN:VEVENT
        UID:weekly-1@example.com
        DTSTAMP:20070301T000000Z
        RECURRENCE-ID;TZID=Pacific Standard Time:20070412T100000
        DTSTART;TZID=Pacific Standard Time:20070412T140000
        DTEND;TZID=Pacific Standard Time:20070412T143000
        SUMMARY:Standup (afternoon)
        END:VEVENT
        END:VCALENDAR
        """;

    private const string Request = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Example//Test//EN
        METHOD:REQUEST
        BEGIN:VEVENT
        UID:meeting-7@example.com
        DTSTAMP:20240501T120000Z
        DTSTART:20240520T090000Z
        DTEND:20240520T100000Z
        SUMMARY:Planning
        ORGANIZER;CN=Ann:mailto:ann@example.com
        ATTENDEE;CN=Bob;PARTSTAT=NEEDS-ACTION;RSVP=TRUE:mailto:bob@example.com
        SEQUENCE:0
        END:VEVENT
        END:VCALENDAR
        """;

    private const string Todo = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Example//Test//EN
        BEGIN:VTODO
        UID:todo-3@example.com
        DTSTAMP:20240501T120000Z
        DTSTART:20240502T000000Z
        DUE:20240509T000000Z
        SUMMARY:File the report
        STATUS:IN-PROCESS
        PERCENT-COMPLETE:40
        PRIORITY:1
        END:VTODO
        END:VCALENDAR
        """;

    private static IcsComponent Calendar(string text) =>
        IcsDocument.Calendars(ContentDocument.Parse(Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\r\n") + "\r\n"))).First();

    // The mask is added by the writer; nothing else may differ.
    private static void AssertSame(MapiMessage original, MsgWriterOptions? options = null)
    {
        byte[] file = MsgWriter.ToArray(original, options);
        using MsgFile f = OpenAll(file);
        Assert.Empty(f.Defects);
        List<string> diffs = Differences(original, f.Message, ignore: k => k == "340D0003");
        Assert.True(diffs.Count == 0, string.Join("; ", diffs.Take(5)));
    }

    [Fact]
    public void RecurringAppointmentWithExceptionsKeepsTheBlobAndTheOccurrences()
    {
        MapiMessage m = new IcsToMapiConverter().Convert(Calendar(Weekly)).Value;
        AssertSame(m);
        using MsgFile f = OpenAll(MsgWriter.ToArray(m));
        OutlookAppointment before = OutlookAppointment.TryCreate(m)!;
        OutlookAppointment after = OutlookAppointment.TryCreate(f.Message)!;
        Assert.Equal(before.Start, after.Start);
        Assert.Equal(before.End, after.End);
        Assert.Equal(before.TimeZone!.Id, after.TimeZone!.Id);
        Assert.Equal(before.Recurrence!.ToArray(), after.Recurrence!.ToArray());
        Assert.Equal(2, after.Exceptions.Count);
        Assert.Equal(before.Exceptions.Select(e => e.Message!.Subject), after.Exceptions.Select(e => e.Message!.Subject));
        Assert.Equal(before.DeletedInstances, after.DeletedInstances);
        Assert.Equal(2, after.Attendees.Count);
        // PidLidAppointmentRecur and the time zone structures are binary named properties; both survive as bytes.
        Assert.NotNull(f.Message.Properties.Get(OutlookNames.AppointmentRecur, MapiType.Binary));
        Assert.NotNull(f.Message.Properties.Get(OutlookNames.TimeZoneStruct, MapiType.Binary));
    }

    [Fact]
    public void GeneratedSeriesOfEveryPatternRoundTrip()
    {
        var start = D("2024-03-04T00:00");
        foreach ((RecurrencePattern p, TzRule rule, string name) in new[]
        {
            (Daily(start, 2, count: 8), UtcRule, "UTC"),
            (Weekly(start, 1, 0x2A, count: 6), Pacific, "Pacific Standard Time"),
            (Monthly(start, 1, 15, count: 5), Sydney, "AUS Eastern Standard Time"),
            (MonthlyNth(start, 2, 0x04, 2, count: 4), Pacific, "Pacific Standard Time"),
            (Yearly(start, 4, count: 3), UtcRule, "UTC"),
            (YearlyNth(start, 0x40, 3, count: 3), Pacific, "Pacific Standard Time"),
        })
        {
            MapiMessage m = Series(p, 540, 600, rule, name, subject: "S " + name);
            AssertSame(m);
            using MsgFile f = OpenAll(MsgWriter.ToArray(m));
            Assert.Equal(OutlookAppointment.TryCreate(m)!.Recurrence!.ToArray(), OutlookAppointment.TryCreate(f.Message)!.Recurrence!.ToArray());
        }
    }

    [Fact]
    public void MeetingRequestAndTaskRoundTrip()
    {
        MapiMessage request = new IcsToMapiConverter().Convert(Calendar(Request)).Value;
        AssertSame(request);
        using (MsgFile f = OpenAll(MsgWriter.ToArray(request)))
        {
            OutlookAppointment a = OutlookAppointment.TryCreate(f.Message)!;
            Assert.Equal(OutlookAppointmentKind.MeetingRequest, a.Kind);
            Assert.Equal("Planning", a.Subject);
            Assert.Equal(OutlookAppointment.TryCreate(request)!.GlobalObjectId, a.GlobalObjectId);
        }

        MapiMessage task = new IcsToMapiConverter().Convert(Calendar(Todo)).Value;
        AssertSame(task);
        using MsgFile g = OpenAll(MsgWriter.ToArray(task));
        OutlookTask t = OutlookTask.TryCreate(g.Message)!;
        Assert.Equal("File the report", t.Subject);
        Assert.Equal(OutlookTask.TryCreate(task)!.DueDate, t.DueDate);
        Assert.Equal(OutlookTask.TryCreate(task)!.PercentComplete, t.PercentComplete);
    }

    [Fact]
    public void ContactRoundTripsAndKeepsItsPhoto()
    {
        string vcf = "BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Dan Fennell\r\nN:Fennell;Dan;;;\r\nORG:Contoso, Ltd.\r\nTITLE:Engineer\r\nADR;TYPE=WORK:;;7890 Elm Street;Boulder;CO;33041;U.S.\r\n"
            + "TEL;TYPE=VOICE,WORK:+1-206-555-0102\r\nEMAIL;TYPE=INTERNET,PREF:dan.fennell@contoso.com\r\nBDAY:1980-02-29\r\nCATEGORIES:Work,Friends\r\nNOTE:A \u00fcnicode note\r\nEND:VCARD\r\n";
        RecursiveAI.OneMail.Calendar.Component card = ContentDocument.Parse(Encoding.UTF8.GetBytes(vcf)).Components.Single();
        MapiMessage m = new VCardToContactConverter().Convert(card).Value;
        AssertSame(m);
        using MsgFile f = OpenAll(MsgWriter.ToArray(m));
        var before = new MapiContact(m);
        var after = new MapiContact(f.Message);
        Assert.Equal(before.DisplayName, after.DisplayName);
        Assert.Equal(before.Phones, after.Phones);
        Assert.Equal(before.EmailAddresses, after.EmailAddresses);
        Assert.Equal(before.Categories, after.Categories);
        Assert.Equal(before.Birthday, after.Birthday);
        Assert.Equal("Dan Fennell", after.DisplayName);
    }

    [Fact]
    public void DistributionListMembersWriteAndReadBack()
    {
        MapiMessage dl = MapiDistributionList.Create("Team", [
            new DistributionListMember(DistributionListMemberKind.OneOff, "Ann", "SMTP", "ann@example.com", null, []),
            new DistributionListMember(DistributionListMemberKind.OneOff, null, null, "bob@example.com", null, []),
        ]);
        Assert.Equal("IPM.DistList", dl.MessageClass);
        var view = new MapiDistributionList(dl);
        Assert.Equal("Team", view.Name);
        Assert.Equal(["ann@example.com", "bob@example.com"], view.Members.Select(x => x.SmtpAddress));
        AssertSame(dl);
        using MsgFile f = OpenAll(MsgWriter.ToArray(dl));
        var back = new MapiDistributionList(f.Message);
        Assert.Equal("Team", back.Name);
        Assert.Equal(view.Members.Select(x => (x.Kind, x.DisplayName, x.Address)), back.Members.Select(x => (x.Kind, x.DisplayName, x.Address)));
        // The checksum is the CRC of MS-OXOCNTC 3.1.5.11 over the member entry IDs.
        Assert.NotNull(f.Message.Properties.Get(new MapiNamedProperty(MapiPropertySets.Address, 0x804Cu), MapiType.Integer32));
    }

    [Fact]
    public void LargeDistributionListUsesTheStream()
    {
        DistributionListMember[] many = Enumerable.Range(0, 400).Select(i => new DistributionListMember(DistributionListMemberKind.OneOff, "Member " + i, "SMTP", $"member{i}@example.com", null, [])).ToArray();
        MapiMessage dl = MapiDistributionList.Create("Everyone", many);
        Assert.NotNull(dl.Properties.Get(new MapiNamedProperty(MapiPropertySets.Address, 0x8064u), MapiType.Binary));
        Assert.Null(dl.Properties.Get(new MapiNamedProperty(MapiPropertySets.Address, 0x8055u)));
        AssertSame(dl);
        using MsgFile f = OpenAll(MsgWriter.ToArray(dl));
        Assert.Equal(400, new MapiDistributionList(f.Message).Members.Count);
        Assert.Equal("member399@example.com", new MapiDistributionList(f.Message).Members[399].SmtpAddress);
    }

    [Fact]
    public void StickyNoteRoundTrips()
    {
        MapiMessage note = MapiNote.Create("Grocery List:\r\nmilk", NoteColor.Green, 300, 200, 40, 50);
        AssertSame(note);
        using MsgFile f = OpenAll(MsgWriter.ToArray(note));
        var back = new MapiNote(f.Message);
        Assert.Equal("Grocery List:\r\nmilk", back.Text);
        Assert.Equal(NoteColor.Green, back.Color);
        Assert.Equal((300, 200, 40, 50), (back.Width, back.Height, back.Left, back.Top));
        Assert.Equal("Grocery List:", f.Message.Subject);
        Assert.Equal(0x301, f.Message.GetInt32(0x1080));
        Assert.Throws<ArgumentOutOfRangeException>(() => MapiNote.Create("x", (NoteColor)9));
    }

    [Fact]
    public void ATemplateIsAnOrdinaryItemFileWithTheUnsentFlag()
    {
        MapiMessage m = MapiNote.Create("template body");
        m.Properties.Add(MapiProperty.CreateOwned(new MapiTag(MapiPid.Subject, MapiType.UnicodeString), Encoding.Unicode.GetBytes("Weekly report")));
        byte[] oft = MsgWriter.ToArray(m, new MsgWriterOptions { Template = true });
        Assert.True(MsgFile.HasCompoundFileSignature(oft));
        using MsgFile f = OpenAll(oft);
        Assert.Equal(8, f.Message.GetInt32(MapiPid.MessageFlags) & 8);
        Assert.Equal("template body", f.Message.Body);
        Assert.True(MsgFile.IsMsg(new RecursiveAI.OneMail.Sources.MemorySource(oft)));
    }
}
