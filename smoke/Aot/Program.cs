using System.IO;
using System.Linq;
using RecursiveAI.OneMail;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.Ics;
using RecursiveAI.OneMail.Calendar.Recurrence;
using RecursiveAI.OneMail.Calendar.TimeZones;
using RecursiveAI.OneMail.Calendar.VCard;
using RecursiveAI.OneMail.Client;
using RecursiveAI.OneMail.Cryptography;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Mime.Authentication;
using RecursiveAI.OneMail.Mime.Mhtml;
using RecursiveAI.OneMail.Mime.Reports;
using RecursiveAI.OneMail.Render;
using RecursiveAI.OneMail.Pst.Ltp;
using RecursiveAI.OneMail.Rtf;
using RecursiveAI.OneMail.Rtf.Compression;
using RecursiveAI.OneMail.Pst.Ndb;
using RecursiveAI.OneMail.Sources;
using RecursiveAI.OneMail.Stores;
using RecursiveAI.OneMail.Stores.Emlx;
using RecursiveAI.OneMail.Stores.Maildir;
using RecursiveAI.OneMail.Stores.Mbox;
using RecursiveAI.OneMail.Text;
using RecursiveAI.OneMail.Tests.Pst;

namespace RecursiveAI.OneMail.AotSmoke;

/// <summary>Touches every package under NativeAOT with trimming; the publish must produce no trim or AOT warning.</summary>
internal static class Program
{
    private static int Main()
    {
        string[] lines =
        [
            CalendarLibrary.Name,
            OneMailLibrary.Describe(),
            ClientLibrary.Describe(),
            CryptographyLibrary.Describe(),
            RenderLibrary.Describe(),
        ];
        foreach (string line in lines)
        {
            Console.WriteLine(line);
        }

        // Text layer: encoded words, charsets (OneHtml tables, ISO-2022, UTF-7), RFC 2231 parameters.
        var collector = new ParameterCollector();
        collector.Add("filename*0*"u8, "utf-8''caf%C3"u8);
        collector.Add("filename*1*"u8, "%A9.txt"u8);
        bool textOk =
            Rfc2047.DecodeText("=?utf-8?B?5pel5pys6Kqe?= =?iso-8859-1?Q?caf=E9?=") == "日本語café"
            && MailEncoding.ForLabel("shift_jis")!.GetString([0x93, 0xFA, 0x96, 0x7B]) == "日本"
            && MailEncoding.ForLabel("iso-2022-jp")!.GetString([0x1B, 0x24, 0x42, 0x46, 0x7C, 0x1B, 0x28, 0x42]) == "日"
            && MailEncoding.ForCodePage(50221) is not null
            && ImapUtf7.Decode("&ZeVnLIqe-"u8) == "日本語"
            && Utf7.Decode("+ZeVnLIqe-"u8) == "日本語"
            && collector.Resolve()[0].Value == "café.txt"
            && Rfc2047.DecodeText(Rfc2047.Encode("café 日")) == "café 日";
        Console.WriteLine("text: " + (textOk ? "ok" : "FAILED"));

        // The MIME layer under NativeAOT: parse a small multipart, read a decoded body, write it back unchanged.
        byte[] message = "Content-Type: multipart/mixed; boundary=b\r\n\r\n--b\r\nContent-Transfer-Encoding: base64\r\n\r\nSGk=\r\n--b--\r\n"u8.ToArray();
        var root = (MimeMultipart)MimeEntity.Parse(message);
        var part = (MimePart)root.Children[0];
        bool mimeOk = System.Text.Encoding.ASCII.GetString(part.GetDecodedContent()) == "Hi" && root.ToArray().AsSpan().SequenceEqual(message);
        Console.WriteLine(mimeOk ? "mime ok" : "mime FAILED");

        // Calendar: parse, read typed values, edit one line and write back, all under NativeAOT.
        ContentDocument cal = ContentDocument.Parse("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nSUMMARY:a\\,b\r\nDTSTART;TZID=Europe/Paris:20260101T100000\r\nRRULE:FREQ=DAILY;COUNT=2\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
        IcsComponent ev = IcsDocument.Calendars(cal).Single().Events.Single();
        ev.SetText("SUMMARY", "c;d");
        ContentDocument card = ContentDocument.Parse("BEGIN:VCARD\r\nVERSION:2.1\r\nN;ENCODING=QUOTED-PRINTABLE:M=C3=BCller;Hans\r\nTEL;WORK:+1\r\nEND:VCARD\r\n");
        VCardView c = VCardDocument.Cards(card).Single();
        if (ev.Summary != "c;d" || ev.RecurrenceRules.Count != 1 || c.Name?.Given[0] != "Hans" || c.Phones[0].Types[0] != "WORK" || cal.ToArray().Length == 0)
        {
            return 2;
        }

        // Node database: write a small 4 KiB file (compressed blocks) and a Unicode one, read both back through the trees.
        bool ndbOk = true;
        foreach (NdbFormat format in new[] { NdbFormat.Unicode, NdbFormat.Unicode4K })
        {
            byte[] payload = new byte[3000];
            for (int i = 0; i < payload.Length; i++) { payload[i] = (byte)(i % 11); }
            byte[] file = NdbTestBuilder.Build(format, [new TestNode { Nid = 0x21, Data = payload }], new TestBuild { Compress = true }).Bytes;
            ndbOk &= NdbFile.TryOpen(file, out NdbFile? ndb, out _) && ndb is not null;
            if (ndb is null) { continue; }
            using (ndb)
            {
                ndbOk &= ndb.Nodes.TryGet(new Nid(0x21), out NodeEntry node);
                using NdbDataTree? data = ndb.OpenDataTree(node.Data);
                byte[] back = new byte[3000];
                ndbOk &= data is not null && data.Read(0, back) == 3000 && back.AsSpan().SequenceEqual(payload) && ndb.Defects.Count == 0;
            }
        }

        Console.WriteLine("ndb: " + (ndbOk ? "ok" : "FAILED"));

        // Lists, tables and properties: a property context and a table context (rows in a subnode) in a 4 KiB file with compressed blocks.
        bool ltpOk = true;
        {
            var pcNode = PcBuilder.Build(0x2004, [new PcSpec(0x37, LtpPropertyTypes.PtypString, System.Text.Encoding.Unicode.GetBytes("hi")), new PcSpec(0x0E07, LtpPropertyTypes.PtypInteger32, [7, 0, 0, 0]), new PcSpec(0x3701, LtpPropertyTypes.PtypBinary, new byte[5000], InSubnode: true)]);
            var tcNode = TcBuilder.Build(0x80E, new TcSpec { Columns = [(0x3001, LtpPropertyTypes.PtypString), (0x0E08, LtpPropertyTypes.PtypInteger32)], Rows = Enumerable.Range(0, 30).Select(i => ((uint)(0x2004 + (i * 32)), new byte[]?[] { System.Text.Encoding.Unicode.GetBytes("n" + i), BitConverter.GetBytes(i) })).ToList(), RowsInSubnode = true, MatrixBlockSize = 300 });
            byte[] lfile = NdbTestBuilder.Build(NdbFormat.Unicode4K, [pcNode, tcNode], new TestBuild { Compress = true }).Bytes;
            ltpOk &= NdbFile.TryOpen(lfile, out NdbFile? lf, out _) && lf is not null;
            if (lf is not null)
            {
                using (lf)
                {
                    ltpOk &= PropertyContext.TryOpen(lf, new Nid(0x2004), out PropertyContext? lpc, out _) && lpc is not null;
                    ltpOk &= lpc!.TryGet(0x0E07, out PcProperty p) && LtpValues.TryGetInt32(lpc.ReadValue(p)!, out int seven) && seven == 7;
                    ltpOk &= lpc.TryGet(0x3701, out PcProperty big) && lpc.GetValueLength(big) == 5000;
                    lpc.Dispose();
                    ltpOk &= TableContext.TryOpen(lf, new Nid(0x80E), out TableContext? ltc, out _) && ltc is not null && ltc.RowCount == 30;
                    byte[] row = new byte[ltc!.RowSize];
                    ltpOk &= ltc.TryReadRow(29, row) && ltc.GetRow(row).TryGetCell(ltc.FindColumn(0x3001), out LtpValueRef cell) && System.Text.Encoding.Unicode.GetString(ltc.ReadValue(cell)!) == "n29";
                    ltc.Dispose();
                    ltpOk &= lf.Defects.Count == 0;
                }
            }
        }

        Console.WriteLine("ltp: " + (ltpOk ? "ok" : "FAILED"));

        // Structured headers: addresses, dates, identifiers and RFC 2231 parameters, and the writer's header encoding.
        byte[] structured = "From: =?utf-8?q?Caf=C3=A9?= <a@example.com>\r\nTo: Doe, John <j@example.org>, g: x@example.net;\r\nDate: Mon, 14 May 2001 23:39:00 +0200\r\nMessage-ID: <id@example.com>\r\nContent-Disposition: attachment; filename*=utf-8''caf%C3%A9.txt\r\n\r\nx"u8.ToArray();
        MimeEntity sm = MimeEntity.Parse(structured);
        var mailboxes = AddressParser.Flatten(sm.GetAddresses("From")).Concat(AddressParser.Flatten(sm.GetAddresses("To"))).ToList();
        var written = new MimeHeader("Subject", "caf\u00e9 \u65e5");
        bool structuredOk = mailboxes.Count == 3 && mailboxes[0].Name == "Caf\u00e9" && mailboxes[1].Name == "Doe, John"
            && sm.GetDate()!.Value.OffsetMinutes == 120 && sm.GetMessageId() == "id@example.com" && sm.FileName == "caf\u00e9.txt"
            && written.Value == "caf\u00e9 \u65e5" && System.Text.Ascii.IsValid(written.Raw.Span)
            && MimeHeaderValue.Parameter("filename", "caf\u00e9.txt").Contains("filename*=", StringComparison.Ordinal);
        Console.WriteLine("structured: " + (structuredOk ? "ok" : "FAILED"));

        // Mailbox stores: split and write an mbox, deliver to an in-memory Maildir, read an emlx with its property list.
        byte[] mbox = "From a@b.c Mon Jan  1 00:00:00 2024\nSubject: x\n\n>From quoted\n\nFrom d@e.f Tue Jan  2 00:00:00 2024\nSubject: y\n\nz\n"u8.ToArray();
        using var mboxSource = new MemorySource(mbox);
        using var mboxReader = new MboxReader(mboxSource);
        int count = 0;
        while (mboxReader.Read())
        {
            count++;
        }

        using var mboxOut = new MemoryStream();
        new MboxWriter(mboxOut, new MboxWriterOptions { Variant = MboxVariant.Mboxcl }).Append("Subject: s\n\nFrom x\n"u8.ToArray(), "me", DateTimeOffset.UnixEpoch);
        var fs = new MemoryStoreFileSystem();
        MaildirEntry delivered = new MaildirStore(fs).Deliver("Subject: m\n\nbody\n"u8.ToArray(), marks: MessageMarks.Seen, keywords: ["k"]);
        byte[] emlx = "3\nabc<?xml version=\"1.0\"?><plist version=\"1.0\"><dict><key>flags</key><integer>1</integer></dict></plist>"u8.ToArray();
        using var emlxSource = new MemorySource(emlx);
        EmlxMetadata meta = EmlxReader.ReadMetadata(emlxSource, EmlxReader.Read(emlxSource));
        bool storesOk = count == 2 && mboxOut.Length > 0 && delivered.Keywords[0] == "k" && meta.Marks == MessageMarks.Seen
            && StoreSniffer.Sniff(mboxSource) == StoreFormat.Mbox && StoreSniffer.Sniff(emlxSource) == StoreFormat.Emlx;
        Console.WriteLine("stores: " + (storesOk ? "ok" : "FAILED"));

        // Reports, MHTML and authentication results.
        byte[] dsn = "Content-Type: multipart/report; report-type=delivery-status; boundary=b\r\nAuthentication-Results: mx.example.org; spf=pass smtp.mailfrom=a@example.com\r\n\r\n--b\r\nContent-Type: text/plain\r\n\r\nfailed\r\n--b\r\nContent-Type: message/delivery-status\r\n\r\nReporting-MTA: dns; mx.example.org\r\n\r\nFinal-Recipient: rfc822;a@example.org\r\nAction: failed\r\nStatus: 5.1.1\r\n\r\n--b--\r\n"u8.ToArray();
        MimeEntity dm = MimeEntity.Parse(dsn);
        DeliveryStatusReport? ds = MimeReport.Find(dm)?.GetDeliveryStatus();
        var ar = dm.GetAuthenticationResults();
        bool reportsOk = ds is { Recipients.Count: 1 } && ds.Recipients[0].Action == DsnAction.Failed && ds.Recipients[0].Status == new EnhancedStatusCode(5, 1, 1)
            && ar.Count == 1 && ar[0].Results[0].Method == "spf" && MhtmlUri.Resolve("http://a/b/c/d", "../g") == "http://a/b/g";
        Console.WriteLine("reports: " + (reportsOk ? "ok" : "FAILED"));

        // The shared view and Mail.Open: sniff, enumerate an mbox through the view, read bodies, attachments and defects.
        byte[] viewMbox = "From a@b.c Mon Jan  1 00:00:00 2024\nSubject: =?utf-8?B?Y2Fmw6k=?=\nFrom: A <a@b.c>\nDate: Mon, 1 Jan 2024 00:00:00 +0000\nContent-Type: multipart/mixed; boundary=b\n\n--b\nContent-Type: text/plain\n\nhi\n--b\nContent-Type: application/pdf; name=x.pdf\nContent-Transfer-Encoding: base64\n\nAAEC\n--b--\n\nFrom d@e.f Tue Jan  2 00:00:00 2024\nSubject: y\n\nz\n"u8.ToArray();
        using MailContainer opened = Mail.Open(new MemorySource(viewMbox));
        IMailItem[] viewed = opened.Enumerate().ToArray();
        bool viewOk = opened.Format == MailFormat.Mbox && viewed.Length == 2 && viewed[0].Subject == "caf\u00e9" && viewed[0].From[0].Address == "a@b.c"
            && viewed[0].TextBody!.ReadText().StartsWith("hi", StringComparison.Ordinal) && viewed[0].Attachments[0].FileName == "x.pdf" && viewed[0].Attachments[0].Size == 3
            && viewed[1].Subject == "y" && Mail.Parse("Subject: q\r\n\r\n"u8.ToArray()).Subject == "q"
            && MailFormatSniffer.Sniff(new MemorySource("BEGIN:VCALENDAR\r\n"u8.ToArray())) == MailFormat.Ics;
        Console.WriteLine("view: " + (viewOk ? "ok" : "FAILED"));

        // Recurrence: a zoned daily rule across the spring-forward day with the file's own VTIMEZONE (no tz database needed), an EXDATE,
        // a Windows zone name and a hostile rule that must end at a limit.
        const string recIcs = "BEGIN:VCALENDAR\r\nBEGIN:VTIMEZONE\r\nTZID:Eastern Standard Time\r\nBEGIN:DAYLIGHT\r\nDTSTART:20070311T020000\r\nTZOFFSETFROM:-0500\r\nTZOFFSETTO:-0400\r\nRRULE:FREQ=YEARLY;BYMONTH=3;BYDAY=2SU\r\nEND:DAYLIGHT\r\n"
            + "BEGIN:STANDARD\r\nDTSTART:20071104T020000\r\nTZOFFSETFROM:-0400\r\nTZOFFSETTO:-0500\r\nRRULE:FREQ=YEARLY;BYMONTH=11;BYDAY=1SU\r\nEND:STANDARD\r\nEND:VTIMEZONE\r\n"
            + "BEGIN:VEVENT\r\nUID:a\r\nDTSTART;TZID=Eastern Standard Time:20260307T093000\r\nRRULE:FREQ=DAILY;COUNT=4\r\nEXDATE;TZID=Eastern Standard Time:20260309T093000\r\nEND:VEVENT\r\n"
            + "BEGIN:VEVENT\r\nUID:b\r\nDTSTART:20260105T090000Z\r\nRRULE:FREQ=SECONDLY;BYMONTH=2;BYMONTHDAY=31\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        IcsComponent recCal = IcsDocument.Calendars(ContentDocument.Parse(System.Text.Encoding.UTF8.GetBytes(recIcs))).Single();
        ExpansionReport recReport = new();
        Occurrence[] recOccurrences = CalendarRecurrence.Expand(recCal, OccurrenceWindow.All,
            new ExpansionOptions { MaxIterationsWithoutHit = 20000, TimeZones = new TimeZoneResolverOptions { UseRuntimeDatabase = false } }, recReport).ToArray();
        IcsTimeZone? recZone = new TimeZoneResolver(null).Resolve("Eastern Standard Time");
        // COUNT=4 counts the excluded 9 March too, so three remain; the hostile rule still yields its DTSTART (RFC 5545 3.8.5.3).
        Occurrence[] march = recOccurrences.Where(o => o.StartUtc?.Month == 3).OrderBy(o => o.StartUtc).ToArray();
        bool recurrenceOk = recOccurrences.Length == 4 && march.Length == 3 && march[0].StartUtc == new DateTime(2026, 3, 7, 14, 30, 0, DateTimeKind.Utc)
            && march[1].StartUtc == new DateTime(2026, 3, 8, 13, 30, 0, DateTimeKind.Utc) && march[2].StartUtc == new DateTime(2026, 3, 10, 13, 30, 0, DateTimeKind.Utc)
            && recReport.CountOf(RecurrenceDefectKind.IterationLimit) == 1 && (recZone is null || recZone.Source == ZoneSource.WindowsName);
        Console.WriteLine("recurrence: " + (recurrenceOk ? "ok" : "FAILED"));


        // MSG: sniff, open, read recipients, an attachment and an embedded message.
        byte[] msgBytes = RecursiveAI.OneMail.Tests.Msg.MsgFixtures.All()["recipients-attachments.msg"];
        using var msgSource = new MemorySource(msgBytes);
        RecursiveAI.OneMail.Msg.MsgFile? msgFile = null;
        bool msgOk = RecursiveAI.OneMail.Msg.MsgFile.IsMsg(msgSource) && MailFormatSniffer.Sniff(msgSource) == MailFormat.Msg
            && RecursiveAI.OneMail.Msg.MsgFile.TryOpen(msgBytes, out msgFile, out _);
        if (msgOk)
        {
            using (msgFile)
            {
                var mm = msgFile!.Message;
                msgOk = mm.Subject == "Recipients, attachments and an embedded message" && mm.Recipients.Count == 3 && mm.Recipients[0].EmailAddress == "ann@example.org"
                    && mm.Attachments.Count >= 2 && mm.Attachments[0].FileName == "data.bin" && mm.Attachments[0].OpenData()!.Length == 5000
                    && mm.Body == "Hello\r\nworld";
            }
        }

        Console.WriteLine("msg: " + (msgOk ? "ok" : "FAILED"));

        // PST: build a small store, open it through the messaging layer and through Mail.Open, read an attachment and an embedded message.
        byte[] pstBytes = new RecursiveAI.OneMail.Tests.Pst.PstFixtureBuilder(RecursiveAI.OneMail.Pst.Ndb.NdbFormat.Unicode).Build(RecursiveAI.OneMail.Tests.Pst.PstFixtures.Sample(), "Smoke store").Bytes;
        bool pstOk = RecursiveAI.OneMail.Pst.Messaging.PstStore.TryOpen(pstBytes, out var pstStore, out _) && pstStore is not null;
        if (pstOk)
        {
            using (pstStore)
            {
                var counts = pstStore!.ReadEverything();
                pstOk = counts.Messages >= 4 && counts.Attachments >= 4 && counts.EmbeddedMessages == 1 && pstStore.Defects.Count == 0;
            }
        }

        using (MailContainer pstOpened = Mail.Open(new MemorySource(pstBytes)))
        {
            IMailItem[] pstItems = pstOpened.Enumerate().ToArray();
            IMailItem? hello = pstItems.FirstOrDefault(x => x.Subject == "Hello world");
            pstOk &= pstOpened.Format == MailFormat.Pst && hello is not null && hello.Attachments.Count == 4 && hello.ToRecipients.Count == 1 && hello.ToRecipients[0].Address == "ann@example.com"
                && hello.TextBody!.ReadText() == "Plain body" && hello.Attachments[1].Size == 70_000 && hello.Attachments[3].Embedded!.Subject == "Forwarded inner";
            using Stream large = hello!.Attachments[1].Open();
            pstOk &= large.Length == 70_000;
        }

        Console.WriteLine("pst: " + (pstOk ? "ok" : "FAILED"));

        // Mail.Open on an MSG goes through the same view.
        using (MailContainer msgOpened = Mail.Open(new MemorySource(msgBytes)))
        {
            IMailItem[] msgItems = msgOpened.Enumerate().ToArray();
            bool openMsgOk = msgOpened.Format == MailFormat.Msg && msgItems.Length == 1 && msgItems[0].Subject == "Recipients, attachments and an embedded message";
            Console.WriteLine("open-msg: " + (openMsgOk ? "ok" : "FAILED"));
            msgOk &= openMsgOk;
        }

        // Compressed RTF and de-encapsulation: compress, decompress, wrap HTML and text in RTF and take them back out, with a
        // code-page byte (needs the platform's code-page provider under trimming and AOT).
        string rtfHtml = "<p>caf\u00e9 \u65e5 &amp; x</p>\r\n";
        byte[] rtfBody = RtfEncapsulator.EncapsulateHtml(rtfHtml);
        RtfDecompressionResult rtfBack = RtfDecompressor.Decompress(RtfCompressor.Compress(rtfBody));
        RtfDeencapsulationResult rtfOut = RtfDeencapsulator.Deencapsulate(rtfBack.Data);
        RtfDeencapsulationResult rtfText = RtfDeencapsulator.Deencapsulate(RtfEncapsulator.EncapsulateText("a\tb\r\n"));
        RtfDeencapsulationResult rtfCp = RtfDeencapsulator.Deencapsulate("{\\rtf1\\ansi\\ansicpg1251\\fromtext\\'cf}"u8);
        bool rtfOk = rtfBack.IsClean && rtfBack.CrcVerified && rtfOut.Kind == RtfEncapsulation.Html && rtfOut.Text == rtfHtml
            && rtfText.Text == "a\tb\r\n" && rtfCp.Text == "\u041f";
        Console.WriteLine("rtf: " + (rtfOk ? "ok" : "FAILED"));

        byte[] tnefBytes = RecursiveAI.OneMail.Tests.Tnef.TnefTestBuilder.Rich();
        RecursiveAI.OneMail.Tnef.TnefMessage tnefMsg = RecursiveAI.OneMail.Tnef.TnefMessage.Read(tnefBytes);
        using var tnefOut = new MemoryStream();
        RecursiveAI.OneMail.Tnef.TnefMessageWriter.Write(tnefMsg, tnefOut);
        RecursiveAI.OneMail.Tnef.TnefMessage tnefAgain = RecursiveAI.OneMail.Tnef.TnefMessage.Read(tnefOut.ToArray());
        using var tnefDocOut = new MemoryStream();
        RecursiveAI.OneMail.Tnef.TnefDocument.Read(tnefBytes).WriteTo(tnefDocOut);
        bool tnefOk = tnefMsg.Subject == "Caf\u00e9 menu \u2713" && tnefMsg.Attachments.Count == 3 && tnefMsg.Recipients.Count == 2
            && tnefAgain.Subject == tnefMsg.Subject && tnefAgain.Attachments.Count == 3 && tnefAgain.Properties.Count == tnefMsg.Properties.Count
            && tnefDocOut.ToArray().AsSpan().SequenceEqual(tnefBytes)
            && Mail.Open(new MemorySource(tnefBytes)).Format == MailFormat.Tnef;
        Console.WriteLine("tnef: " + (tnefOk ? "ok" : "FAILED"));

        // MS-OXCMAIL: an Outlook message to Internet mail and back, with an Exchange sender resolved through its SMTP property.
        var oxcMsg = new RecursiveAI.OneMail.Mapi.MapiMessage();
        oxcMsg.Properties.Add(RecursiveAI.OneMail.Mapi.MapiProperty.CreateOwned(new RecursiveAI.OneMail.Mapi.MapiTag(RecursiveAI.OneMail.Mapi.MapiPid.Subject, RecursiveAI.OneMail.Mapi.MapiType.UnicodeString), System.Text.Encoding.Unicode.GetBytes("caf\u00e9")));
        oxcMsg.Properties.Add(RecursiveAI.OneMail.Mapi.MapiProperty.CreateOwned(new RecursiveAI.OneMail.Mapi.MapiTag(RecursiveAI.OneMail.Mapi.MapiPid.SenderAddressType, RecursiveAI.OneMail.Mapi.MapiType.UnicodeString), System.Text.Encoding.Unicode.GetBytes("EX")));
        oxcMsg.Properties.Add(RecursiveAI.OneMail.Mapi.MapiProperty.CreateOwned(new RecursiveAI.OneMail.Mapi.MapiTag(RecursiveAI.OneMail.Mapi.MapiPid.SenderEmailAddress, RecursiveAI.OneMail.Mapi.MapiType.UnicodeString), System.Text.Encoding.Unicode.GetBytes("/o=x/cn=a")));
        oxcMsg.Properties.Add(RecursiveAI.OneMail.Mapi.MapiProperty.CreateOwned(new RecursiveAI.OneMail.Mapi.MapiTag(RecursiveAI.OneMail.Mapi.MapiPid.SenderSmtpAddress, RecursiveAI.OneMail.Mapi.MapiType.UnicodeString), System.Text.Encoding.Unicode.GetBytes("a@example.com")));
        oxcMsg.Properties.Add(RecursiveAI.OneMail.Mapi.MapiProperty.CreateOwned(new RecursiveAI.OneMail.Mapi.MapiTag(RecursiveAI.OneMail.Mapi.MapiPid.Body, RecursiveAI.OneMail.Mapi.MapiType.UnicodeString), System.Text.Encoding.Unicode.GetBytes("body")));
        RecursiveAI.OneMail.Conversion.ConversionResult<RecursiveAI.OneMail.Mime.MimeEntity> oxcMime = new RecursiveAI.OneMail.Conversion.OxcMail.MapiToMimeConverter().Convert(oxcMsg);
        byte[] oxcBytes = oxcMime.Value.ToArray();
        RecursiveAI.OneMail.Conversion.ConversionResult<RecursiveAI.OneMail.Mapi.MapiMessage> oxcBack = new RecursiveAI.OneMail.Conversion.OxcMail.MimeToMapiConverter().Convert(RecursiveAI.OneMail.Mime.MimeEntity.Parse(oxcBytes));
        var oxcView = new MapiMailItem(oxcMsg, default);
        bool oxcOk = oxcView.From.Count == 1 && oxcView.From[0].Address == "a@example.com" && oxcView.From[0].X500Address == "/o=x/cn=a"
            && Mail.Parse(oxcBytes).Subject == "caf\u00e9" && oxcBack.Value.GetString(RecursiveAI.OneMail.Mapi.MapiPid.Body) == "body"
            && oxcBack.Value.GetString(RecursiveAI.OneMail.Mapi.MapiPid.SentRepresentingSmtpAddress) == "a@example.com";
        Console.WriteLine("oxcmail: " + (oxcOk ? "ok" : "FAILED"));

        // TNEF inside MIME: unpacked into the MAPI message and the view, and exported as a TNEF message.
        bool tnefMimeOk = RecursiveAI.OneMail.WasmSmoke.OutlookChecks.TnefInMime();
        Console.WriteLine("tnef-in-mime: " + (tnefMimeOk ? "ok" : "FAILED"));

        // Outlook Express database: a generated store read through the reader (shuffled chains, a narrow tree) and the happy path.
        bool dbxOk;
        {
            List<RecursiveAI.OneMail.Tests.Stores.DbxTestMessage> dbxMsgs = [];
            for (int i = 0; i < 30; i++)
            {
                dbxMsgs.Add(new RecursiveAI.OneMail.Tests.Stores.DbxTestMessage { Text = System.Text.Encoding.ASCII.GetBytes("Subject: dbx " + i + "\r\n\r\n" + new string('x', i * 40)), Subject = "dbx " + i });
            }

            byte[] dbxFile = RecursiveAI.OneMail.Tests.Stores.DbxTestBuilder.MessageDatabase(dbxMsgs, new RecursiveAI.OneMail.Tests.Stores.DbxBuild { TreeFanout = 3, ShuffleSeed = 4 });
            using RecursiveAI.OneMail.Sources.MemorySource dbxSrc = new(dbxFile);
            RecursiveAI.OneMail.Stores.Dbx.DbxReader dbxReader = new(dbxSrc);
            int dbxN = 0;
            bool dbxSame = true;
            foreach (RecursiveAI.OneMail.Stores.Dbx.DbxMessageEntry e in dbxReader.EnumerateMessages())
            {
                dbxSame &= dbxReader.ReadMessage(e, out _).AsSpan().SequenceEqual(dbxMsgs[dbxN].Text);
                dbxN++;
            }

            using MemoryStream dbxStream = new(dbxFile);
            using MailContainer dbxBox = Mail.Open(dbxStream);
            IMailItem[] dbxItems = [.. dbxBox.Enumerate()];
            dbxOk = dbxReader.Kind == RecursiveAI.OneMail.Stores.Dbx.DbxFileKind.MessageDatabase && dbxN == 30 && dbxSame
                && dbxBox.Format == MailFormat.Dbx && dbxItems.Length == 30 && dbxItems[29].Subject == "dbx 29";
        }

        Console.WriteLine("dbx: " + (dbxOk ? "ok" : "FAILED"));

        return dbxOk && tnefMimeOk && pstOk && textOk && mimeOk && ndbOk && structuredOk && storesOk && reportsOk && viewOk && ltpOk && msgOk && rtfOk && tnefOk && recurrenceOk && oxcOk && OneMailLibrary.Describe() == "RecursiveAI.OneMail (on RecursiveAI.OneMail.Calendar)" ? 0 : 1;
    }
}
