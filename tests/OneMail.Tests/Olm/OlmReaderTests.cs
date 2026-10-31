using System.IO.Compression;
using System.Text;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Conversion;
using RecursiveAI.OneMail.Sources;
using RecursiveAI.OneMail.Stores.Olm;

namespace RecursiveAI.OneMail.Tests.Olm;

/// <summary>Writes small Outlook for Mac archives in the layout of the sample files, for tests of the reader and its limits.</summary>
internal sealed class OlmTestArchive
{
    public const string X = " xml:space=\"preserve\"";
    public const string Root = "Local/com.microsoft.__Messages/";
    private readonly List<(string Name, byte[] Data)> _entries = [];

    public OlmTestArchive Add(string name, string text) => Add(name, Encoding.UTF8.GetBytes(text));

    public OlmTestArchive Add(string name, byte[] data)
    {
        _entries.Add((name, data));
        return this;
    }

    public static string El(string name, string text) => $"<{name}{X}>{System.Security.SecurityElement.Escape(text)}</{name}>";

    public static string Addresses(string list, params (string? Name, string? Address)[] people) =>
        $"<{list}{X}>" + string.Concat(people.Select(p => $"<emailAddress{X}" + (p.Address is null ? "" : $" OPFContactEmailAddressAddress=\"{p.Address}\"")
            + (p.Name is null ? "" : $" OPFContactEmailAddressName=\"{System.Security.SecurityElement.Escape(p.Name)}\"") + " OPFContactEmailAddressType=\"0\"/>")) + $"</{list}>";

    public static string Emails(params string[] emailBodies) =>
        $"<emails{X} elementCount=\"{emailBodies.Length}\">" + string.Concat(emailBodies.Select(b => $"<email{X}>{b}</email>")) + "</emails>";

    public OlmTestArchive Message(string folder, int n, string body) => Add($"{Root}{folder}/message_{n:D5}.xml", Emails(body));

    public byte[] Build(CompressionLevel level = CompressionLevel.Fastest)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] data) in _entries)
            {
                ZipArchiveEntry e = z.CreateEntry(name, level);
                using Stream s = e.Open();
                s.Write(data);
            }
        }

        return ms.ToArray();
    }

    // The layout markers a real archive has, so the sniffer recognises the zip.
    public static OlmTestArchive WithFolders() => new OlmTestArchive().Add(Root + "Inbox/", Array.Empty<byte>());
}

public class OlmReaderTests
{
    private static OlmArchive Open(byte[] zip, OlmReaderOptions? o = null) => OlmArchive.Open(new MemorySource(zip), false, null, o);

    private static string SimpleBody(string subject, string html = "0E0", string text = "hello") =>
        OlmTestArchive.El("OPFMessageCopyBody", text) + OlmTestArchive.El("OPFMessageCopyHTMLBody", text) + OlmTestArchive.El("OPFMessageCopySubject", subject)
        + OlmTestArchive.El("OPFMessageGetHasHTML", html);

    [Fact]
    public void ReadsAMessageThroughTheSharedView()
    {
        string body = OlmTestArchive.Addresses("OPFMessageCopyFromAddresses", ("Zoë Müller", "zoe@example.org"))
            + OlmTestArchive.Addresses("OPFMessageCopyToAddresses", ("A", "a@example.org"), (null, "b@example.org"))
            + OlmTestArchive.Addresses("OPFMessageCopyCCAddresses", ("C", "c@example.org"))
            + OlmTestArchive.El("OPFMessageCopyBody", "plain & <text>") + OlmTestArchive.El("OPFMessageCopyHTMLBody", "plain & <text>")
            + OlmTestArchive.El("OPFMessageCopyMessageID", "id-1@example.org") + OlmTestArchive.El("OPFMessageCopyInReplyTo", "<p@example.org>")
            + OlmTestArchive.El("OPFMessageCopyReferences", "<r1@example.org> <r2@example.org>") + OlmTestArchive.El("OPFMessageCopySentTime", "2020-02-03T04:05:06")
            + OlmTestArchive.El("OPFMessageCopyReceivedTime", "2020-02-03T04:05:09") + OlmTestArchive.El("OPFMessageCopySubject", "日本語 & more")
            + OlmTestArchive.El("OPFMessageCopyThreadIndex", Convert.ToBase64String([1, 2, 3, 4])) + OlmTestArchive.El("OPFMessageCopyThreadTopic", "topic")
            + OlmTestArchive.El("OPFMessageGetHasHTML", "0E0");
        byte[] zip = OlmTestArchive.WithFolders().Message("Inbox/Sub folder", 0, body).Build();
        using OlmArchive olm = Open(zip);
        OlmItem item = Assert.Single(olm.Enumerate());
        Assert.Equal(MailItemKind.Message, item.Kind);
        Assert.Equal("日本語 & more", item.Subject);
        Assert.Equal("Zoë Müller <zoe@example.org>", item.From[0].ToString());
        Assert.Equal(["a@example.org", "b@example.org"], item.ToRecipients.Select(a => a.Address));
        Assert.Equal("C", item.Cc[0].Name);
        Assert.Equal("id-1@example.org", item.MessageId);
        Assert.Equal(["p@example.org"], item.InReplyTo);
        Assert.Equal(["r1@example.org", "r2@example.org"], item.References);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, item.ConversationIndex.ToArray());
        Assert.Equal("topic", item.ConversationTopic);
        Assert.Equal("plain & <text>", item.TextBody!.ReadText());
        Assert.Null(item.HtmlBody);
        Assert.Equal(new DateTimeOffset(2020, 2, 3, 4, 5, 6, TimeSpan.Zero), item.Sent!.Value.Value);
        Assert.False(item.Sent.Value.ZoneKnown);
        Assert.True(item.TryGetDate(MailDateSource.Delivery, out MailDate d) && d.Value.Second == 9);
        Assert.Equal("Inbox/Sub folder", item.Folder);
        Assert.Contains(item.Defects, x => x.Code == "AmbiguousDateZone" && x.IsAmbiguity);
        Assert.Equal(["Inbox", "Inbox/Sub folder"], olm.GetFolders().Select(f => f.Path));
    }

    [Fact]
    public void HtmlFlagDecidesWhichBodyTheMessageHas()
    {
        byte[] zip = OlmTestArchive.WithFolders().Message("Inbox", 0, SimpleBody("h", html: "1E0", text: "<p>x</p>")).Build();
        using OlmArchive olm = Open(zip);
        OlmItem item = olm.Enumerate().Single();
        Assert.Null(item.TextBody);
        Assert.Equal("<p>x</p>", item.HtmlBody!.ReadText());
        Assert.Equal("text/html", item.HtmlBody.ContentType);
    }

    [Fact]
    public void AttachmentsAreLazyStreamsOverTheirEntries()
    {
        byte[] data = new byte[300_000];
        new Random(5).NextBytes(data);
        string url = OlmTestArchive.Root + "Inbox/com.microsoft.__Attachments/id@x_0000";
        string att = $"<OPFMessageCopyAttachmentList{OlmTestArchive.X}><messageAttachment{OlmTestArchive.X} OPFAttachmentContentExtension=\"bin\" OPFAttachmentContentFileSize=\"3E5\" "
            + $"OPFAttachmentContentType=\"application/x-test\" OPFAttachmentName=\"a b.bin\" OPFAttachmentURL=\"{url}\"/>"
            + $"<messageAttachment{OlmTestArchive.X} OPFAttachmentName=\"gone.bin\" OPFAttachmentURL=\"{OlmTestArchive.Root}Inbox/com.microsoft.__Attachments/nothing\"/></OPFMessageCopyAttachmentList>";
        byte[] zip = OlmTestArchive.WithFolders().Add(url, data).Message("Inbox", 0, SimpleBody("s") + att).Build();
        using OlmArchive olm = Open(zip);
        OlmItem item = olm.Enumerate().Single();
        Assert.Equal(2, item.Attachments.Count);
        Assert.Equal("a b.bin", item.Attachments[0].FileName);
        Assert.Equal("application/x-test", item.Attachments[0].ContentType);
        Assert.Equal(data.Length, item.Attachments[0].Size);
        using (Stream s = item.Attachments[0].Open())
        {
            var ms = new MemoryStream();
            s.CopyTo(ms);
            Assert.Equal(data, ms.ToArray());
        }

        // The second attachment names an entry that is not in the archive: listed with no content, and the item says so.
        Assert.Equal(0, item.Attachments[1].Size);
        Assert.Equal(0, item.Attachments[1].Open().Length);
        Assert.Contains(item.OlmDefects, d => d.Kind == OlmDefectKind.AttachmentMissing);
    }

    [Fact]
    public void RawMimeSourceIsKept()
    {
        byte[] zip = OlmTestArchive.WithFolders().Message("Inbox", 0, SimpleBody("s") + OlmTestArchive.El("OPFMessageCopySource", "Subject: raw\nTo: x@y\n\nbody\n")).Build();
        using OlmArchive olm = Open(zip);
        Assert.Equal("Subject: raw\nTo: x@y\n\nbody\n", olm.Enumerate().Single().MimeSource);
    }

    [Fact]
    public void EveryRecordKindIsAnItemAndNothingIsLostFromTheTree()
    {
        string appt = OlmTestArchive.El("OPFCalendarEventCopySummary", "Meet") + OlmTestArchive.El("OPFCalendarEventCopyStartTime", "2020-03-04T09:00:00")
            + OlmTestArchive.El("OPFCalendarEventCopyEndTime", "2020-03-04T10:00:00") + OlmTestArchive.El("OPFCalendarEventCopyUUID", "u-1")
            + OlmTestArchive.El("OPFCalendarEventGetHasReminder", "1") + OlmTestArchive.El("OPFCalendarEventCopyReminderDelta", "900") + OlmTestArchive.El("OPFFutureElement", "kept");
        byte[] zip = new OlmTestArchive()
            .Add("Local/Calendar/Calendar.xml", $"<appointments{OlmTestArchive.X} elementCount=\"1\"><appointment{OlmTestArchive.X}>{appt}</appointment></appointments>")
            .Add("Local/Address Book/Contacts.xml", $"<contacts{OlmTestArchive.X} elementCount=\"1\"><contact{OlmTestArchive.X}>" + OlmTestArchive.El("OPFContactCopyDisplayName", "Jane")
                + "</contact></contacts>")
            .Add("Local/Address Book/Groups.xml", $"<groups{OlmTestArchive.X} elementCount=\"1\"><group{OlmTestArchive.X}>" + OlmTestArchive.El("OPFGroupCopyDisplayName", "G") + "</group></groups>")
            .Add("Local/Tasks/Tasks.xml", $"<tasks{OlmTestArchive.X} elementCount=\"1\"><task{OlmTestArchive.X}>" + OlmTestArchive.El("OPFTaskCopyName", "T") + "</task></tasks>")
            .Add("Local/Notes/Notes.xml", $"<notes{OlmTestArchive.X} elementCount=\"1\"><note{OlmTestArchive.X}>" + OlmTestArchive.El("OPFNoteCopyTitle", "N") + "</note></notes>")
            .Add("Categories.xml", "<categories elementCount=\"1\"><category OPFCategoryCopyBackgroundColor=\"r:1 g:1 b:1\" OPFCategoryCopyName=\"Blue\"></category></categories>")
            .Build();
        using OlmArchive olm = Open(zip);
        List<OlmItem> items = olm.Enumerate().ToList();
        Assert.Equal(
            [OlmItemKind.Appointment, OlmItemKind.Contact, OlmItemKind.Group, OlmItemKind.Task, OlmItemKind.Note, OlmItemKind.Category],
            items.Select(i => i.OlmKind));
        Assert.Equal(["Meet", "Jane", "G", "T", "N", "Blue"], items.Select(i => i.Subject));
        Assert.Equal([MailItemKind.Appointment, MailItemKind.Contact, MailItemKind.DistributionList, MailItemKind.Task, MailItemKind.Note, MailItemKind.Other], items.Select(i => i.Kind));
        // An element no reader knows is still in the tree and is listed as unmapped.
        Assert.Contains(items[0].UnmappedElements(), e => e.Name == "OPFFutureElement" && e.Text == "kept");
        Assert.Empty(olm.Defects);
    }

    private static readonly string[] DayNames = ["allDays", "friday", "monday", "saturday", "sunday", "thursday", "tuesday", "wednesday", "weekdays", "weekenddays"];

    [Fact]
    public void HostileZoneTextAndAddressesStillWriteAsCalendar()
    {
        // Found by fuzzing: an unterminated VTIMEZONE swallows lines that have no colon, and a line break in an address or UID must not reach the writer.
        const string tz = "BEGIN:VCALENDAR\r\nBEGIN:VTIMEZONE\r\nTZID:Z\r\nBEGIN:STANDARD\r\nTZOFFSETTO:-0500\r\nEND:STANDARD\r\nEND7VTIM\r\nEEENND:VCALENDAR\r\n";
        string appt = OlmTestArchive.El("OPFCalendarEventCopySummary", "x") + OlmTestArchive.El("OPFCalendarEventCopyStartTime", "2020-03-02T09:00:00")
            + OlmTestArchive.El("OPFCalendarEventCopyUUID", "u\r\n1") + OlmTestArchive.El("OPFCalendarEventCopyOrganizer", "a@b\r\nc")
            + OlmTestArchive.El("OPFCalendarEventGetStartTimeZoneICSData", tz);
        byte[] zip = new OlmTestArchive().Add("Local/Calendar/Calendar.xml", $"<appointments{OlmTestArchive.X} elementCount=\"1\"><appointment{OlmTestArchive.X}>{appt}</appointment></appointments>").Build();
        using OlmArchive olm = Open(zip);
        ConversionResult<Component> r = new OlmToCalendarConverter().Convert(olm.Enumerate().Single());
        var doc = new ContentDocument();
        doc.Root.Add(r.Value);
        Assert.NotEmpty(doc.ToArray());
    }

    [Fact]
    public void AppointmentConvertsToCalendarWithZoneRecurrenceAndReminder()
    {
        const string tz = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VTIMEZONE\r\nTZID:Mountain Standard Time\r\nBEGIN:STANDARD\r\nDTSTART:16010101T000000\r\nTZOFFSETFROM:-0600\r\nTZOFFSETTO:-0700\r\nEND:STANDARD\r\nEND:VTIMEZONE\r\nEND:VCALENDAR\r\n";
        string days = string.Concat(DayNames.Select(d => OlmTestArchive.El(d, d is "monday" or "wednesday" ? "1" : "0")));
        string rec = $"<OPFCalendarEventCopyRecurrence{OlmTestArchive.X}>" + OlmTestArchive.El("OPFRecurrenceGetOccurenceCount", "6E0") + OlmTestArchive.El("OPFRecurrenceIsNumbered", "1")
            + $"<OPFRecurrencePattern{OlmTestArchive.X}><OPFRecurrencePatternDaysOfWeek{OlmTestArchive.X}>{days}</OPFRecurrencePatternDaysOfWeek>"
            + OlmTestArchive.El("OPFRecurrencePatternInterval", "2") + OlmTestArchive.El("OPFRecurrencePatternType", "OPFRecurrencePatternWeekly") + "</OPFRecurrencePattern></OPFCalendarEventCopyRecurrence>";
        string appt = OlmTestArchive.El("OPFCalendarEventCopySummary", "Weekly; sync, ok") + OlmTestArchive.El("OPFCalendarEventCopyStartTime", "2020-03-02T09:00:00")
            + OlmTestArchive.El("OPFCalendarEventCopyEndTime", "2020-03-02T10:00:00") + OlmTestArchive.El("OPFCalendarEventCopyUUID", "u-1")
            + OlmTestArchive.El("OPFCalendarEventGetHasReminder", "1") + OlmTestArchive.El("OPFCalendarEventCopyReminderDelta", "900") + OlmTestArchive.El("OPFCalendarEventGetStartTimeZoneICSData", tz)
            + OlmTestArchive.El("OPFCalendarEventCopyModDate", "2020-01-01T00:00:00") + OlmTestArchive.El("OPFCalendarEventCopyTravelTime", "0") + rec;
        byte[] zip = new OlmTestArchive().Add("Local/Calendar/Calendar.xml", $"<appointments{OlmTestArchive.X} elementCount=\"1\"><appointment{OlmTestArchive.X}>{appt}</appointment></appointments>").Build();
        using OlmArchive olm = Open(zip);
        OlmItem item = olm.Enumerate().Single();
        ConversionResult<Component> r = new OlmToCalendarConverter().Convert(item);
        string ics = Encoding.UTF8.GetString(ContentDocumentBuilder.Of(r.Value));
        Assert.Contains("SUMMARY:Weekly\\; sync\\, ok", ics);
        Assert.Contains("DTSTART;TZID=Mountain Standard Time:20200302T090000", ics);
        Assert.Contains("RRULE:FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;COUNT=6", ics);
        Assert.Contains("BEGIN:VTIMEZONE", ics);
        Assert.Contains("TRIGGER:-PT900S", ics);
        Assert.Contains(r.Report, e => e.Name == "OPFCalendarEventCopyTravelTime" && e.Code == "Unmapped");
    }

    [Fact]
    public void ContactAndGroupConvertToVCards()
    {
        string c = OlmTestArchive.El("OPFContactCopyDisplayName", "Jane Doe") + OlmTestArchive.El("OPFContactCopyFirstName", "Jane") + OlmTestArchive.El("OPFContactCopyLastName", "Doe")
            + OlmTestArchive.El("OPFContactCopyBusinessCompany", "A; B") + OlmTestArchive.El("OPFContactCopyCellPhone", "123")
            + $"<OPFContactCopyEmailAddressList{OlmTestArchive.X}><contactEmailAddress{OlmTestArchive.X} OPFContactEmailAddressAddress=\"j@x.org\" OPFContactEmailAddressIsDefault=\"1\" OPFContactEmailAddressType=\"1\"/></OPFContactCopyEmailAddressList>"
            + OlmTestArchive.El("OPFContactCopyHomeCity", "Springfield") + OlmTestArchive.El("OPFContactCopyBirthday", "2000-05-06T06:00:00") + OlmTestArchive.El("OPFContactCopyAstrologySign", "x");
        string g = OlmTestArchive.El("OPFGroupCopyDisplayName", "Team") + OlmTestArchive.Addresses("OPFGroupCopyMemberList", ("M", "m@x.org"));
        byte[] zip = new OlmTestArchive()
            .Add("Local/Address Book/Contacts.xml", $"<contacts{OlmTestArchive.X} elementCount=\"1\"><contact{OlmTestArchive.X}>{c}</contact></contacts>")
            .Add("Local/Address Book/Groups.xml", $"<groups{OlmTestArchive.X} elementCount=\"1\"><group{OlmTestArchive.X}>{g}</group></groups>").Build();
        using OlmArchive olm = Open(zip);
        List<OlmItem> items = olm.Enumerate().ToList();
        var conv = new OlmToVCardConverter();
        string card = Encoding.UTF8.GetString(ContentDocumentBuilder.Of(conv.Convert(items[0]).Value));
        Assert.Contains("FN:Jane Doe", card);
        Assert.Contains("N:Doe;Jane;;;", card);
        Assert.Contains("ORG:A\\; B", card);
        Assert.Contains("TEL;TYPE=cell:123", card);
        Assert.Contains("EMAIL;TYPE=home,pref:j@x.org", card);
        Assert.Contains("BDAY:2000-05-06", card);
        ConversionResult<Component> grp = conv.Convert(items[1]);
        string gc = Encoding.UTF8.GetString(ContentDocumentBuilder.Of(grp.Value));
        Assert.Contains("KIND:group", gc);
        Assert.Contains("MEMBER:mailto:m@x.org", gc);
        Assert.Contains(conv.Convert(items[0]).Report, e => e.Name == "OPFContactCopyAstrologySign");
    }

    [Fact]
    public void SniffsByContentNotName()
    {
        byte[] zip = OlmTestArchive.WithFolders().Message("Inbox", 0, SimpleBody("x")).Build();
        Assert.Equal(MailFormat.Olm, MailFormatSniffer.Sniff(new MemorySource(zip)));
        byte[] plain = new OlmTestArchive().Add("readme.txt", "hi").Build();
        Assert.Equal(MailFormat.Unknown, MailFormatSniffer.Sniff(new MemorySource(plain)));

        using MailContainer c = Mail.Open(new MemoryStream(zip));
        Assert.Equal(MailFormat.Olm, c.Format);
        Assert.True(c.IsStore);
        Assert.Equal("x", Assert.Single(c.Enumerate()).Subject);
    }

    [Fact]
    public void MalformedXmlKeepsTheRecordsBeforeTheDamage()
    {
        string good = $"<email{OlmTestArchive.X}>{SimpleBody("one")}</email>";
        string xml = $"<emails{OlmTestArchive.X} elementCount=\"3\">{good}{good.Replace("one", "two")}<email{OlmTestArchive.X}><OPFMessageCopySubject>cut";
        byte[] zip = OlmTestArchive.WithFolders().Add(OlmTestArchive.Root + "Inbox/message_00000.xml", xml).Build();
        using OlmArchive olm = Open(zip);
        Assert.Equal(["one", "two"], olm.Enumerate().Select(i => i.Subject));
        Assert.Contains(olm.Defects, d => d.Kind == OlmDefectKind.XmlMalformed);
    }

    [Fact]
    public void DeepAndHugeXmlAreRefusedPerRecord()
    {
        string deep = string.Concat(Enumerable.Repeat("<a>", 5000)) + string.Concat(Enumerable.Repeat("</a>", 5000));
        string ok = $"<email{OlmTestArchive.X}>{SimpleBody("fine")}</email>";
        string xml = $"<emails{OlmTestArchive.X}><email{OlmTestArchive.X}>{deep}</email>{ok}</emails>";
        byte[] zip = OlmTestArchive.WithFolders().Add(OlmTestArchive.Root + "Inbox/message_00000.xml", xml).Build();
        using (OlmArchive olm = Open(zip))
        {
            Assert.Equal(["fine"], olm.Enumerate().Select(i => i.Subject));
            Assert.Contains(olm.Defects, d => d.Kind == OlmDefectKind.XmlTooDeep);
        }

        string many = "<email xml:space=\"preserve\">" + string.Concat(Enumerable.Repeat("<b/>", 50)) + "</email>";
        byte[] zip2 = OlmTestArchive.WithFolders().Add(OlmTestArchive.Root + "Inbox/message_00000.xml", $"<emails>{many}{ok}</emails>").Build();
        using (OlmArchive olm = Open(zip2, new OlmReaderOptions { MaxElementsPerItem = 20 }))
        {
            Assert.Equal(["fine"], olm.Enumerate().Select(i => i.Subject));
            Assert.Contains(olm.Defects, d => d.Kind == OlmDefectKind.XmlTooManyElements);
        }

        byte[] zip3 = OlmTestArchive.WithFolders().Add(OlmTestArchive.Root + "Inbox/message_00000.xml", $"<emails>{ok}<email><x>{new string('y', 5000)}</x></email></emails>").Build();
        using (OlmArchive olm = Open(zip3, new OlmReaderOptions { MaxTextLength = 1000 }))
        {
            Assert.Equal(["fine"], olm.Enumerate().Select(i => i.Subject));
            Assert.Contains(olm.Defects, d => d.Kind == OlmDefectKind.XmlTooLarge);
        }

        byte[] zip4 = OlmTestArchive.WithFolders().Add(OlmTestArchive.Root + "Inbox/message_00000.xml", $"<emails>{ok}{ok}{ok}</emails>").Build();
        using (OlmArchive olm = Open(zip4, new OlmReaderOptions { MaxXmlBytes = 300 }))
        {
            Assert.True(olm.Enumerate().Count() < 3);
            Assert.Contains(olm.Defects, d => d.Kind == OlmDefectKind.XmlTooLarge);
        }
    }

    [Fact]
    public void CompressionBombIsRefusedByTheRatioLimit()
    {
        // 200 MiB of one repeated element deflates by about 1000:1; the zip reader's ratio limit stops the read, which becomes a defect.
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            z.CreateEntry(OlmTestArchive.Root + "Inbox/");
            using Stream s = z.CreateEntry(OlmTestArchive.Root + "Inbox/message_00000.xml", CompressionLevel.SmallestSize).Open();
            s.Write("<emails>"u8);
            byte[] chunk = Encoding.ASCII.GetBytes("<email><OPFMessageCopySubject>"  + new string('a', 1 << 20) + "</OPFMessageCopySubject></email>");
            for (int i = 0; i < 200; i++)
            {
                s.Write(chunk);
            }

            s.Write("</emails>"u8);
        }

        using OlmArchive olm = Open(ms.ToArray(), new OlmReaderOptions { MaxExpansionRatio = 100 });
        int n = olm.Enumerate().Count();
        Assert.True(n < 200);
        Assert.Contains(olm.Defects, d => d.Kind is OlmDefectKind.ArchiveLimitExceeded or OlmDefectKind.EntryUnreadable);
    }

    [Fact]
    public void BadValuesBecomeDefectsNotExceptions()
    {
        string body = OlmTestArchive.El("OPFMessageCopySubject", "s") + OlmTestArchive.El("OPFMessageCopySentTime", "yesterday") + OlmTestArchive.El("OPFMessageGetHasHTML", "maybe")
            + OlmTestArchive.Addresses("OPFMessageCopyToAddresses", ("Only Name", null)) + OlmTestArchive.El("OPFMessageCopyThreadIndex", "!!not base64!!");
        byte[] zip = OlmTestArchive.WithFolders().Message("Inbox", 0, body).Build();
        using OlmArchive olm = Open(zip);
        OlmItem item = olm.Enumerate().Single();
        Assert.Null(item.Sent);
        Assert.True(item.ConversationIndex.IsEmpty);
        Assert.Equal("Only Name", item.ToRecipients[0].Name);
        Assert.Equal(string.Empty, item.ToRecipients[0].Address);
        Assert.Contains(item.OlmDefects, d => d.Kind == OlmDefectKind.BadDate);
        Assert.Contains(item.OlmDefects, d => d.Kind == OlmDefectKind.BadNumber);
        Assert.Contains(item.OlmDefects, d => d.Kind == OlmDefectKind.AddressMissing);
    }

    [Fact]
    public void WrongRootsAndStrayFilesAreReported()
    {
        byte[] zip = OlmTestArchive.WithFolders().Add(OlmTestArchive.Root + "Inbox/message_00000.xml", "<unknown/>").Add(OlmTestArchive.Root + "Inbox/junk.bin", "x")
            .Add(OlmTestArchive.Root + "Inbox/message_00001.xml", "not xml at all").Add(OlmTestArchive.Root + "Inbox/message_00002.xml", Array.Empty<byte>())
            .Add(OlmTestArchive.Root + "Inbox/message_00003.xml", $"<emails elementCount=\"5\"><email>{SimpleBody("k")}</email></emails>").Build();
        using OlmArchive olm = Open(zip);
        Assert.Equal(["k"], olm.Enumerate().Select(i => i.Subject));
        Assert.Contains(olm.Defects, d => d.Kind == OlmDefectKind.UnrecognisedRoot);
        Assert.Contains(olm.Defects, d => d.Kind == OlmDefectKind.XmlMalformed);
        Assert.Contains(olm.Defects, d => d.Kind == OlmDefectKind.ElementCountMismatch);
    }

    [Fact]
    public void DamagedEntryDataIsADefectAndLaterEntriesStillRead()
    {
        byte[] zip = new OlmTestArchive().Add(OlmTestArchive.Root + "Inbox/message_00000.xml", $"<emails>{string.Concat(Enumerable.Repeat($"<email>{SimpleBody("a")}</email>", 40))}</emails>")
            .Add(OlmTestArchive.Root + "Inbox/message_00001.xml", $"<emails><email>{SimpleBody("b")}</email></emails>").Build(CompressionLevel.NoCompression);
        // Flip one byte inside the first entry's stored data: its CRC no longer matches.
        int at = new string(Encoding.Latin1.GetChars(zip)).IndexOf("<email>", StringComparison.Ordinal) + 100;
        zip[at] ^= 0x20;
        using OlmArchive olm = Open(zip);
        List<string?> subjects = olm.Enumerate().Select(i => i.Subject).ToList();
        Assert.Contains("b", subjects);
        Assert.Contains(olm.Defects, d => d.Kind is OlmDefectKind.EntryUnreadable or OlmDefectKind.XmlMalformed);
    }

    [Fact]
    public void NotAZipIsRefusedWithATypedException()
    {
        Assert.False(OlmArchive.IsOlm(new MemorySource(Encoding.ASCII.GetBytes("PK\u0003\u0004 and then nothing useful at all, not a zip directory"))));
        Assert.Throws<InvalidDataException>(() => Open(Encoding.ASCII.GetBytes("PK\u0003\u0004 and then nothing useful at all, not a zip directory")));
        byte[] zip = OlmTestArchive.WithFolders().Message("Inbox", 0, SimpleBody("x")).Build();
        Assert.Throws<InvalidDataException>(() => Open(zip[..(zip.Length / 2)]));
    }
}

/// <summary>Serialises a component tree for assertions.</summary>
internal static class ContentDocumentBuilder
{
    public static byte[] Of(Component root)
    {
        var doc = new ContentDocument();
        doc.Root.Add(root);
        return doc.ToArray();
    }
}
