using System.IO;
using System.Linq;
using System.Text;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.Ics;
using RecursiveAI.OneMail.Calendar.Recurrence;
using RecursiveAI.OneMail.Calendar.VCard;
using RecursiveAI.OneMail.Calendaring;
using RecursiveAI.OneMail.Conversion;
using RecursiveAI.OneMail.Conversion.OxcIcal;
using RecursiveAI.OneMail.Conversion.OxcMail;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Rtf;
using RecursiveAI.OneMail.Rtf.Compression;
using RecursiveAI.OneMail.Sources;
using RecursiveAI.OneMail.Tests.Msg;
using RecursiveAI.OneMail.Tests.Pst;
using RecursiveAI.OneMail.Tests.Tnef;
using RecursiveAI.OneMail.Tnef;

namespace RecursiveAI.OneMail.WasmSmoke;

/// <summary>
/// The Outlook-format checks, written to run in the browser's .NET runtime (and shared with the NativeAOT smoke): MSG, a small generated PST,
/// TNEF, compressed RTF with a non-UTF-8 code page, recurrence through a VTIMEZONE and through the IANA database, and the MS-OXCMAIL conversions.
/// Each returns true when it passed and prints its own line.
/// </summary>
internal static class OutlookChecks
{
    private static bool Report(string name, bool ok)
    {
        Console.WriteLine(name + ": " + (ok ? "ok" : "FAILED"));
        return ok;
    }

    private static bool Guard(string name, Func<bool> check)
    {
        try
        {
            return Report(name, check());
        }
        catch (Exception e)
        {
            Console.WriteLine(name + ": FAILED with " + e.GetType().Name + ": " + e.Message);
            return false;
        }
    }

    public static bool RunAll() =>
        Guard("msg", Msg) & Guard("pst", Pst) & Guard("tnef", Tnef) & Guard("rtf", Rtf) & Guard("recurrence-vtimezone", RecurrenceVtimezone)
        & Guard("recurrence-iana", RecurrenceIana) & Guard("oxcmail", OxcMail) & Guard("tnef-in-mime", TnefInMime) & Guard("contacts", Contacts)
        & Guard("oxocal", Oxocal);

    public static bool Msg()
    {
        byte[] bytes = MsgFixtures.All()["recipients-attachments.msg"];
        using var source = new MemorySource(bytes);
        RecursiveAI.OneMail.Msg.MsgFile? file = null;
        bool ok = RecursiveAI.OneMail.Msg.MsgFile.IsMsg(source) && MailFormatSniffer.Sniff(source) == MailFormat.Msg && RecursiveAI.OneMail.Msg.MsgFile.TryOpen(bytes, out file, out _);
        if (!ok)
        {
            return false;
        }

        using (file)
        {
            MapiMessage m = file!.Message;
            ok = m.Subject == "Recipients, attachments and an embedded message" && m.Recipients.Count == 3 && m.Recipients[0].EmailAddress == "ann@example.org"
                && m.Attachments[0].FileName == "data.bin" && m.Attachments[0].OpenData()!.Length == 5000 && m.Body == "Hello\r\nworld";
        }

        using MailContainer opened = Mail.Open(new MemorySource(bytes));
        IMailItem[] items = opened.Enumerate().ToArray();
        return ok && opened.Format == MailFormat.Msg && items.Length == 1 && items[0].Subject == "Recipients, attachments and an embedded message";
    }

    public static bool Pst()
    {
        byte[] bytes = new PstFixtureBuilder(RecursiveAI.OneMail.Pst.Ndb.NdbFormat.Unicode).Build(PstFixtures.Sample(), "Smoke store").Bytes;
        bool ok = RecursiveAI.OneMail.Pst.Messaging.PstStore.TryOpen(bytes, out RecursiveAI.OneMail.Pst.Messaging.PstStore? store, out _) && store is not null;
        if (!ok)
        {
            return false;
        }

        using (store)
        {
            var counts = store!.ReadEverything();
            ok = counts.Messages >= 4 && counts.Attachments >= 4 && counts.EmbeddedMessages == 1 && store.Defects.Count == 0;
        }

        using MailContainer opened = Mail.Open(new MemorySource(bytes));
        IMailItem? hello = opened.Enumerate().FirstOrDefault(x => x.Subject == "Hello world");
        return ok && opened.Format == MailFormat.Pst && hello is not null && hello.Attachments.Count == 4 && hello.ToRecipients[0].Address == "ann@example.com"
            && hello.TextBody!.ReadText() == "Plain body" && hello.Attachments[1].Size == 70_000 && hello.Attachments[3].Embedded!.Subject == "Forwarded inner";
    }

    public static bool Tnef()
    {
        byte[] bytes = TnefTestBuilder.Rich();
        TnefMessage m = TnefMessage.Read(bytes);
        using var written = new MemoryStream();
        TnefMessageWriter.Write(m, written);
        TnefMessage again = TnefMessage.Read(written.ToArray());
        using var doc = new MemoryStream();
        TnefDocument.Read(bytes).WriteTo(doc);

        // a Cyrillic legacy-only stream: 8-bit strings in windows-1251 as attOemCodepage declares
        TnefMessage legacy = TnefMessage.Read(TnefTestBuilder.LegacyOnly());
        return m.Subject == "Caf\u00e9 menu \u2713" && m.Attachments.Count == 3 && m.Recipients.Count == 2 && again.Subject == m.Subject && again.Attachments.Count == 3
            && again.Properties.Count == m.Properties.Count && doc.ToArray().AsSpan().SequenceEqual(bytes) && Mail.Open(new MemorySource(bytes)).Format == MailFormat.Tnef
            && legacy.Subject == "\u041f\u0440\u0438\u0432\u0435\u0442" && legacy.Body == "\u0442\u0435\u043b\u043e";
    }

    public static bool Rtf()
    {
        string html = "<p>caf\u00e9 \u65e5 &amp; x</p>\r\n";
        RtfDecompressionResult back = RtfDecompressor.Decompress(RtfCompressor.Compress(RtfEncapsulator.EncapsulateHtml(html)));
        RtfDeencapsulationResult fromHtml = RtfDeencapsulator.Deencapsulate(back.Data);
        RtfDeencapsulationResult text = RtfDeencapsulator.Deencapsulate(RtfEncapsulator.EncapsulateText("a\tb\r\n"));
        // \ansicpg1251 and 0xCF is Cyrillic capital Pe; \ansicpg932 and 0x93FA is the kanji "ni" of Nihon; windows-1252 0xE9 is e-acute
        RtfDeencapsulationResult cyrillic = RtfDeencapsulator.Deencapsulate("{\\rtf1\\ansi\\ansicpg1251\\fromtext\\'cf}"u8);
        RtfDeencapsulationResult japanese = RtfDeencapsulator.Deencapsulate("{\\rtf1\\ansi\\ansicpg932\\fromtext\\'93\\'fa}"u8);
        RtfDeencapsulationResult latin = RtfDeencapsulator.Deencapsulate("{\\rtf1\\ansi\\ansicpg1252\\fromtext\\'e9}"u8);
        return back.IsClean && back.CrcVerified && fromHtml.Kind == RtfEncapsulation.Html && fromHtml.Text == html && text.Text == "a\tb\r\n"
            && cyrillic.Text == "\u041f" && japanese.Text == "\u65e5" && latin.Text == "\u00e9";
    }


    private static Occurrence[] Expand(string zoneBlock, string zone, bool runtimeDatabase, ExpansionReport report)
    {
        string ics = "BEGIN:VCALENDAR\r\n" + zoneBlock + "BEGIN:VEVENT\r\nUID:a\r\nDTSTART;TZID=" + zone + ":20260307T093000\r\nRRULE:FREQ=DAILY;COUNT=3\r\nEND:VEVENT\r\n" + "END:VCALENDAR\r\n";
        IcsComponent cal = IcsDocument.Calendars(ContentDocument.Parse(Encoding.UTF8.GetBytes(ics))).Single();
        return CalendarRecurrence.Expand(cal, OccurrenceWindow.All,
            new ExpansionOptions { TimeZones = new RecursiveAI.OneMail.Calendar.TimeZones.TimeZoneResolverOptions { UseRuntimeDatabase = runtimeDatabase } }, report).OrderBy(o => o.StartUtc).ToArray();
    }

    // Spring forward on 8 March 2026 in the eastern United States: 09:30 local is 14:30 UTC before it and 13:30 UTC from it.
    private static bool SpringForward(Occurrence[] o, ExpansionReport report) =>
        o.Length == 3 && o[0].StartUtc == new DateTime(2026, 3, 7, 14, 30, 0, DateTimeKind.Utc) && o[1].StartUtc == new DateTime(2026, 3, 8, 13, 30, 0, DateTimeKind.Utc)
        && o[2].StartUtc == new DateTime(2026, 3, 9, 13, 30, 0, DateTimeKind.Utc) && report.CountOf(RecurrenceDefectKind.UnknownTimeZone) == 0;

    public static bool RecurrenceVtimezone()
    {
        const string zone = "BEGIN:VTIMEZONE\r\nTZID:Smoke/Eastern\r\nBEGIN:DAYLIGHT\r\nDTSTART:20070311T020000\r\nTZOFFSETFROM:-0500\r\nTZOFFSETTO:-0400\r\nRRULE:FREQ=YEARLY;BYMONTH=3;BYDAY=2SU\r\nEND:DAYLIGHT\r\n"
            + "BEGIN:STANDARD\r\nDTSTART:20071104T020000\r\nTZOFFSETFROM:-0400\r\nTZOFFSETTO:-0500\r\nRRULE:FREQ=YEARLY;BYMONTH=11;BYDAY=1SU\r\nEND:STANDARD\r\nEND:VTIMEZONE\r\n";
        var report = new ExpansionReport();
        return SpringForward(Expand(zone, "Smoke/Eastern", runtimeDatabase: false, report), report);
    }

    public static bool RecurrenceIana()
    {
        // No VTIMEZONE: the name has to come from the runtime's own time zone database, which the browser runtime must carry.
        var report = new ExpansionReport();
        return SpringForward(Expand(string.Empty, "America/New_York", runtimeDatabase: true, report), report);
    }

    // MS-OXOCAL / MS-OXCICAL: a weekly series with a time zone becomes an Outlook item, reads through the typed view, and converts back to an RRULE.
    public static bool Oxocal()
    {
        const string ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//x//EN\r\nBEGIN:VTIMEZONE\r\nTZID:Pacific Standard Time\r\n" +
            "BEGIN:STANDARD\r\nDTSTART:20071104T020000\r\nTZOFFSETFROM:-0700\r\nTZOFFSETTO:-0800\r\nRRULE:FREQ=YEARLY;BYMONTH=11;BYDAY=1SU\r\nEND:STANDARD\r\n" +
            "BEGIN:DAYLIGHT\r\nDTSTART:20070311T020000\r\nTZOFFSETFROM:-0800\r\nTZOFFSETTO:-0700\r\nRRULE:FREQ=YEARLY;BYMONTH=3;BYDAY=2SU\r\nEND:DAYLIGHT\r\nEND:VTIMEZONE\r\n" +
            "BEGIN:VEVENT\r\nUID:a@example.com\r\nDTSTAMP:20260101T000000Z\r\nDTSTART;TZID=Pacific Standard Time:20260105T100000\r\n" +
            "DTEND;TZID=Pacific Standard Time:20260105T103000\r\nRRULE:FREQ=WEEKLY;COUNT=6;BYDAY=MO\r\nSUMMARY:Standup\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        IcsComponent cal = IcsDocument.Calendars(ContentDocument.Parse(Encoding.ASCII.GetBytes(ics))).First();
        MapiMessage item = IcsToMapiConverter.ConvertAll(cal)[0].Value;
        OutlookAppointment? a = OutlookAppointment.TryCreate(item);
        string back = Encoding.UTF8.GetString(new MapiToIcsConverter().Convert(item).Value.ToArray());
        return a is { IsRecurring: true, Subject: "Standup", Recurrence: not null } && new MapiMailItem(item, default).Kind == MailItemKind.Appointment
            && back.Contains("RRULE:FREQ=WEEKLY", StringComparison.Ordinal) && back.Contains("BYDAY=MO", StringComparison.Ordinal);
    }

    public static bool OxcMail()
    {
        var msg = new MapiMessage();
        msg.Properties.Add(Str(MapiPid.Subject, "caf\u00e9"));
        msg.Properties.Add(Str(MapiPid.SenderAddressType, "EX"));
        msg.Properties.Add(Str(MapiPid.SenderEmailAddress, "/o=x/cn=a"));
        msg.Properties.Add(Str(MapiPid.SenderSmtpAddress, "a@example.com"));
        msg.Properties.Add(Str(MapiPid.Body, "body"));
        byte[] eml = new MapiToMimeConverter().Convert(msg).Value.ToArray();
        MapiMessage back = new MimeToMapiConverter().Convert(MimeEntity.Parse(eml)).Value;
        var view = new MapiMailItem(msg, default);
        return view.From[0].Address == "a@example.com" && view.From[0].X500Address == "/o=x/cn=a" && Mail.Parse(eml).Subject == "caf\u00e9"
            && back.GetString(MapiPid.Body) == "body" && back.GetString(MapiPid.SentRepresentingSmtpAddress) == "a@example.com";
    }

    // A contact to a vCard and back, a vCard read through the typed view, and a distribution list as a group card.
    public static bool Contacts()
    {
        var msg = new MapiMessage();
        msg.Properties.Add(Str(MapiPid.MessageClass, "IPM.Contact"));
        msg.Properties.Add(Str(MapiPid.DisplayName, "Dan Fennell"));
        msg.Properties.Add(Str(MapiPid.GivenName, "Dan"));
        msg.Properties.Add(Str(MapiPid.Surname, "Fennell"));
        msg.Properties.Add(Str(MapiPid.CompanyName, "Contoso, Ltd."));
        msg.Properties.Add(Str(MapiPid.BusinessTelephoneNumber, "+1-206-555-0102"));
        ConversionResult<RecursiveAI.OneMail.Calendar.Component> card = new RecursiveAI.OneMail.Conversion.OxVCard.ContactToVCardConverter().Convert(msg);
        var doc = new ContentDocument();
        doc.Root.Add(card.Value);
        byte[] bytes = doc.ToArray();
        VCardView view = VCardDocument.Cards(ContentDocument.Parse(bytes)).Single();
        MapiMessage back = new RecursiveAI.OneMail.Conversion.OxVCard.VCardToContactConverter().Convert(card.Value).Value;
        var contact = new RecursiveAI.OneMail.Contacts.MapiContact(back);
        return view.FormattedName == "Dan Fennell" && view.Phones.Single().Value == "+1-206-555-0102" && view.Organization[0] == "Contoso, Ltd."
            && contact.Surname == "Fennell" && contact.Phone(RecursiveAI.OneMail.Contacts.ContactPhoneKind.Business) == "+1-206-555-0102" && contact.CompanyName == "Contoso, Ltd."
            && new MapiMailItem(msg, default).Contact?.DisplayName == "Dan Fennell";
    }

    public static bool TnefInMime()
    {
        byte[] tnef = TnefTestBuilder.Rich(1252, everyType: false);
        string text = "From: a@example.com\r\nSubject: wrapped\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=\"b\"\r\n\r\n--b\r\nContent-Type: text/plain\r\n\r\nmime text\r\n--b\r\n"
            + "Content-Type: application/ms-tnef; name=\"winmail.dat\"\r\nContent-Transfer-Encoding: base64\r\n\r\n" + Convert.ToBase64String(tnef, Base64FormattingOptions.InsertLineBreaks) + "\r\n--b--\r\n";
        byte[] eml = Encoding.ASCII.GetBytes(text);
        ConversionResult<MapiMessage> merged = new MimeToMapiConverter().Convert(MimeEntity.Parse(eml));
        MimeMailItem view = MimeMailItem.Parse(eml);

        // and out again as a TNEF message
        byte[] outEml = new MapiToMimeConverter(new OxcMailExportOptions { Tnef = OxcMailTnefExport.TnefMessage }).Convert(merged.Value).Value.ToArray();
        MapiMessage again = new MimeToMapiConverter().Convert(MimeEntity.Parse(outEml)).Value;
        return merged.Value.Attachments.Count == 3 && merged.Value.Recipients.Count == 2 && merged.Value.GetString(MapiPid.Subject) == "wrapped"
            && merged.Report.Any(e => e.Code == "TnefPartUnpacked") && view.TnefParts.Count == 1 && view.Attachments.Select(a => a.FileName).SequenceEqual(["report \u00e9.txt", "props.bin", "inner.msg"])
            && view.RtfBody is not null && again.Attachments.Count == 3 && again.Attachments[0].FileName == "report \u00e9.txt";
    }

    private static MapiProperty Str(ushort id, string value) =>
        MapiProperty.CreateOwned(new MapiTag(id, MapiType.UnicodeString), Encoding.Unicode.GetBytes(value));
}
