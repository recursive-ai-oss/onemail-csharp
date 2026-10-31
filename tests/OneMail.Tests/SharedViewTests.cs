using System.Text;
using RecursiveAI.OneMail.Sources;
using RecursiveAI.OneMail.Stores.Maildir;
using RecursiveAI.OneMail.Stores;

namespace RecursiveAI.OneMail.Tests;

public class SharedViewTests
{
    private static byte[] B(string s) => Encoding.Latin1.GetBytes(s);

    private static string Crlf(params string[] lines) => string.Join("\r\n", lines) + "\r\n";

    private const string Headers =
        "From: =?utf-8?B?SsO8cmdlbg==?= <j@example.com>, Team: a@example.com, b@example.com;\r\n" +
        "To: Bob <bob@example.com>\r\nCc: c@example.com\r\nReply-To: r@example.com\r\nSender: s@example.com\r\n" +
        "Subject: =?utf-8?Q?caf=C3=A9?= menu\r\nDate: Mon, 14 May 2001 23:39:00 +0200\r\n" +
        "Message-ID: <id1@example.com>\r\nIn-Reply-To: <id0@example.com>\r\nReferences: <a@x> <b@x>\r\n" +
        "Thread-Topic: topic\r\nThread-Index: AQID\r\nMIME-Version: 1.0\r\n";

    [Fact]
    public void ProjectsHeadersOfAMimeMessage()
    {
        IMailItem m = Mail.Parse(B(Headers + "Content-Type: text/plain; charset=iso-8859-1\r\n\r\ncaf\xE9\r\n"));
        Assert.Equal(MailItemKind.Message, m.Kind);
        Assert.Equal("café menu", m.Subject);
        Assert.Equal([new MailAddress("Jürgen", "j@example.com"), new MailAddress(null, "a@example.com"), new MailAddress(null, "b@example.com")], m.From);
        Assert.Equal([new MailAddress("Bob", "bob@example.com")], m.ToRecipients);
        Assert.Equal("c@example.com", Assert.Single(m.Cc).Address);
        Assert.Empty(m.Bcc);
        Assert.Equal("r@example.com", Assert.Single(m.ReplyTo).Address);
        Assert.Equal("s@example.com", m.Sender?.Address);
        Assert.Equal(new DateTimeOffset(2001, 5, 14, 23, 39, 0, TimeSpan.FromHours(2)), m.Sent?.Value);
        Assert.Equal(MailDateSource.DateHeader, m.Sent?.Source);
        Assert.True(m.Sent?.ZoneKnown);
        Assert.False(m.TryGetDate(MailDateSource.ClientSubmit, out _));
        Assert.Equal("id1@example.com", m.MessageId);
        Assert.Equal(["id0@example.com"], m.InReplyTo);
        Assert.Equal(["a@x", "b@x"], m.References);
        Assert.Equal("topic", m.ConversationTopic);
        Assert.Equal([1, 2, 3], m.ConversationIndex.ToArray());
        Assert.Equal("café\r\n", m.TextBody!.ReadText());
        Assert.Equal("iso-8859-1", m.TextBody.Charset);
        Assert.Null(m.HtmlBody);
        Assert.Null(m.RtfBody);
        Assert.Empty(m.Attachments);
        Assert.Empty(m.Defects);
    }

    [Fact]
    public void AbsentHeadersGiveNullsAndEmptyLists()
    {
        IMailItem m = Mail.Parse(B("X: y\r\n\r\nbody"));
        Assert.Null(m.Subject);
        Assert.Null(m.Sent);
        Assert.Null(m.MessageId);
        Assert.Empty(m.From);
        Assert.Empty(m.References);
        Assert.True(m.ConversationIndex.IsEmpty);
        Assert.Null(m.ConversationTopic);
    }

    [Fact]
    public void AlternativeBodiesFollowRfc2046AndOtherRenderingsAreNotAttachments()
    {
        IMailItem m = Mail.Parse(B(Crlf(
            "Content-Type: multipart/mixed; boundary=m", "",
            "--m", "Content-Type: multipart/alternative; boundary=a", "",
            "--a", "Content-Type: text/plain", "", "plain one",
            "--a", "Content-Type: text/plain", "", "plain two",
            "--a", "Content-Type: multipart/related; boundary=r", "",
            "--r", "Content-Type: text/html; charset=utf-8", "", "<p>hi <img src=cid:pic></p>",
            "--r", "Content-Type: image/png", "Content-ID: <pic>", "Content-Disposition: inline; filename=pic.png", "Content-Transfer-Encoding: base64", "", "AAEC",
            "--r--", "--a--",
            "--m", "Content-Type: application/pdf; name=\"inv.pdf\"", "Content-Disposition: attachment", "Content-Transfer-Encoding: base64", "", "AAECAwQ=",
            "--m", "Content-Type: message/rfc822", "Content-Disposition: attachment; filename=fwd.eml", "", "Subject: inner", "", "inner body",
            "--m--")));
        Assert.Equal("plain two", m.TextBody!.ReadText().TrimEnd());
        Assert.Contains("hi", m.HtmlBody!.ReadText(), StringComparison.Ordinal);
        Assert.Equal(["pic.png", "inv.pdf", "fwd.eml"], m.Attachments.Select(a => a.FileName));
        IMailAttachment pic = m.Attachments[0];
        Assert.True(pic.IsInline);
        Assert.Equal("pic", pic.ContentId);
        Assert.Equal(3, pic.Size);
        using (Stream s = pic.Open())
        {
            Assert.Equal([0, 1, 2], new BinaryReader(s).ReadBytes(10));
        }

        Assert.False(m.Attachments[1].IsInline);
        Assert.Equal(5, m.Attachments[1].Size);
        Assert.Null(m.Attachments[1].Embedded);
        IMailItem inner = m.Attachments[2].Embedded!;
        Assert.Equal("inner", inner.Subject);
        Assert.Equal("inner body", inner.TextBody!.ReadText().TrimEnd());
    }

    [Fact]
    public void AttachmentDispositionKeepsATextPartOutOfTheBody()
    {
        IMailItem m = Mail.Parse(B(Crlf(
            "Content-Type: multipart/mixed; boundary=m", "",
            "--m", "Content-Type: text/plain", "Content-Disposition: attachment; filename=notes.txt", "", "file",
            "--m", "Content-Type: text/plain", "", "body",
            "--m--")));
        Assert.Equal("body", m.TextBody!.ReadText().TrimEnd());
        Assert.Equal("notes.txt", Assert.Single(m.Attachments).FileName);
    }

    [Fact]
    public void ReportsDefectsOfTheTreeAndOfStructuredHeaders()
    {
        IMailItem m = Mail.Parse(B("Content-Type: multipart/mixed; boundary=b\r\nDate: not a date\r\n\r\n--b\r\n\r\nx\r\n"));
        List<MailDefect> d = [.. m.Defects];
        Assert.Contains(d, x => x.Family == "mime" && x.Code == "MissingFinalBoundary" && x.Offset > 0);
        Assert.Contains(d, x => x.Code == "InvalidDate");
    }

    [Fact]
    public void TextWithoutCharsetIsUtf8WhenValidElseWindows1252()
    {
        Assert.Equal("é", Mail.Parse(B("Content-Type: text/plain\r\n\r\n\xC3\xA9")).TextBody!.ReadText());
        Assert.Equal("é", Mail.Parse(B("Content-Type: text/plain\r\n\r\n\xE9")).TextBody!.ReadText());
    }

    // ---- sniffing ----

    private static readonly byte[] Eml = B("Subject: hello\r\nFrom: a@example.com\r\nDate: Mon, 14 May 2001 23:39:00 +0000\r\nMessage-ID: <m1@x>\r\n\r\nbody\r\n");

    private static byte[] Mbox(int n)
    {
        StringBuilder sb = new();
        for (int i = 0; i < n; i++)
        {
            sb.Append("From a@example.com Mon May 14 23:39:00 2001\n").Append("Subject: m").Append(i).Append("\nFrom: a@example.com\nDate: Mon, 14 May 2001 23:39:00 +0000\n\nbody ").Append(i).Append("\n\n");
        }

        return B(sb.ToString());
    }

    private static byte[] Emlx()
    {
        byte[] msg = Eml;
        return [.. B(msg.Length + "\n"), .. msg, .. B("<?xml version=\"1.0\"?><plist version=\"1.0\"><dict><key>flags</key><integer>1</integer></dict></plist>")];
    }

    // A compound-file header with nothing behind it: a container, but not a message.
    private static byte[] Cfb()
    {
        byte[] b = new byte[2048];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(b, 0);
        return b;
    }

    private static byte[] Pst(string magic)
    {
        byte[] b = new byte[564];
        "!BDN"u8.CopyTo(b);
        Encoding.ASCII.GetBytes(magic).CopyTo(b, 8);
        return b;
    }

    public static TheoryData<string, MailFormat> Samples() => new()
    {
        { "eml", MailFormat.Eml },
        { "mbox", MailFormat.Mbox },
        { "emlx", MailFormat.Emlx },
        { "ics", MailFormat.Ics },
        { "vcf", MailFormat.VCard },
        { "pst", MailFormat.Pst },
        { "ost", MailFormat.Ost },
        { "msg", MailFormat.Msg },
        { "cfb", MailFormat.CompoundFile },
        { "tnef", MailFormat.Tnef },
        { "text", MailFormat.Unknown },
        { "empty", MailFormat.Unknown },
        { "obsolete-header", MailFormat.Eml },
    };

    private static byte[] Sample(string kind) => kind switch
    {
        "eml" => Eml,
        "mbox" => Mbox(3),
        "emlx" => Emlx(),
        "ics" => B("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nEND:VCALENDAR\r\n"),
        "vcf" => B("begin:vcard\r\nVERSION:3.0\r\nFN:A\r\nEND:VCARD\r\n"),
        "pst" => Pst("SM"),
        "ost" => Pst("SO"),
        "msg" => Msg.MsgFixtures.All()["empty.msg"],
        "cfb" => Cfb(),
        "tnef" => [0x78, 0x9F, 0x3E, 0x22, 1, 0],
        "text" => B("just some words\r\nand more\r\n"),
        "empty" => [],
        "obsolete-header" => B("Subject :x\r\n\r\ny"),
        _ => throw new ArgumentException(kind),
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public void SniffsByContentWhateverTheFileIsCalled(string kind, MailFormat expected)
    {
        byte[] data = Sample(kind);
        Assert.Equal(expected, MailFormatSniffer.Sniff(new MemorySource(data)));

        // The same bytes under every other kind's customary extension: the answer must not change.
        string dir = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "sniff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (string ext in new[] { ".eml", ".mbox", ".emlx", ".ics", ".vcf", ".pst", ".msg", ".dat", ".txt", "" })
            {
                string path = Path.Combine(dir, "sample" + ext);
                File.WriteAllBytes(path, data);
                if (expected is MailFormat.Eml or MailFormat.Mbox or MailFormat.Emlx)
                {
                    using MailContainer c = Mail.Open(path);
                    Assert.Equal(expected, c.Format);
                }
                else
                {
                    UnsupportedMailFormatException e = Assert.Throws<UnsupportedMailFormatException>(() => Mail.Open(path));
                    Assert.Equal(expected, e.Format);
                }
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SniffsDirectoriesByLayoutNotByName()
    {
        string dir = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "sniffdir-" + Guid.NewGuid().ToString("N"));
        try
        {
            PhysicalStoreFileSystem fs = new(dir);
            MaildirStore store = new(fs, "plain");
            store.Deliver(Eml);
            store.Deliver(Eml, marks: MessageMarks.Seen);
            Directory.CreateDirectory(Path.Combine(dir, "bundle", "Messages"));
            File.WriteAllBytes(Path.Combine(dir, "bundle", "Messages", "1.emlx"), Emlx());
            File.WriteAllBytes(Path.Combine(dir, "bundle", "Messages", "2.emlx"), Emlx());
            Directory.CreateDirectory(Path.Combine(dir, "loose"));
            File.WriteAllBytes(Path.Combine(dir, "loose", "1"), Eml);

            Assert.Equal(MailFormat.MaildirFolder, MailFormatSniffer.SniffDirectory(Path.Combine(dir, "plain")));
            Assert.Equal(MailFormat.AppleMailBundle, MailFormatSniffer.SniffDirectory(Path.Combine(dir, "bundle")));
            Assert.Equal(MailFormat.Unknown, MailFormatSniffer.SniffDirectory(Path.Combine(dir, "loose")));
            Assert.Throws<UnsupportedMailFormatException>(() => Mail.Open(Path.Combine(dir, "loose")));

            using MailContainer md = Mail.Open(Path.Combine(dir, "plain"));
            Assert.Equal(2, md.Enumerate().Count());
            using MailContainer ab = Mail.Open(Path.Combine(dir, "bundle"));
            List<IMailItem> items = [.. ab.Enumerate()];
            Assert.Equal(2, items.Count);
            Assert.Equal("hello", items[1].Subject);
            Assert.Equal(MailFormat.Emlx, items[0].Provenance.Format);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- containers ----

    [Fact]
    public async Task EnumeratesAnMboxLazilyWithProvenanceAndStoreDates()
    {
        byte[] data = Mbox(5);
        using MailContainer c = Mail.Open(new MemorySource(data));
        Assert.Equal(MailFormat.Mbox, c.Format);
        Assert.True(c.IsStore);
        List<string?> subjects = [];
        await foreach (IMailItem item in c.WithCancellation(TestContext.Current.CancellationToken))
        {
            subjects.Add(item.Subject);
            Assert.Equal(item.Provenance.Index, subjects.Count - 1);
            Assert.True(item.TryGetDate(MailDateSource.Delivery, out MailDate d));
            Assert.Equal(2001, d.Value.Year);
            Assert.False(d.ZoneKnown);
            ReadOnlySpan<byte> slice = data.AsSpan((int)item.Provenance.Offset, (int)item.Provenance.Length);
            Assert.True(slice.StartsWith("Subject: m"u8));
        }

        Assert.Equal(["m0", "m1", "m2", "m3", "m4"], subjects);
        Assert.Equal(5, c.Enumerate().Count());
    }

    [Fact]
    public void StoreItemsReportStoreDefectsAndSingleMessagesAreCollectionsOfOne()
    {
        byte[] data = B("From a@example.com Mon May 14 23:39:00 2001\nSubject: x\n\ny\nFrom b@example.com Mon May 14 23:39:00 2001\nSubject: z\n\nw\n");
        using MailContainer c = Mail.Open(new MemorySource(data));
        List<IMailItem> items = [.. c.Enumerate()];
        Assert.Equal(2, items.Count);
        Assert.Contains(items[0].Defects, d => d.Family == "mailbox" && d.Code == "MissingSeparatorLine");
        Assert.DoesNotContain(items[1].Defects, d => d.Family == "mailbox");

        using MailContainer one = Mail.Open(new MemoryStream(Eml));
        Assert.False(one.IsStore);
        Assert.Equal("hello", Assert.Single(one.Enumerate()).Subject);
    }

    [Fact]
    public void ItemsOfADisposedContainerCannotBeRead()
    {
        MailContainer c = Mail.Open(new MemorySource(Mbox(2)));
        IMailItem first = c.Enumerate().First();
        c.Dispose();
        Assert.Throws<ObjectDisposedException>(() => first.Subject);
    }

    [Fact]
    public void MailParseOnArbitraryBytesNeverThrows()
    {
        Random rng = new(7);
        for (int i = 0; i < 200; i++)
        {
            byte[] junk = new byte[rng.Next(0, 600)];
            rng.NextBytes(junk);
            IMailItem m = Mail.Parse(junk);
            _ = m.Subject;
            _ = m.From;
            _ = m.Sent;
            _ = m.Attachments.Count;
            _ = m.Defects.Count();
        }
    }

    [Fact]
    public void EnumeratingAnMboxAllocatesLittlePerMessage()
    {
#if DEBUG
        Assert.Skip("allocation counts are only meaningful in optimised builds (async state machines are classes in Debug)");
#endif
        byte[] data = Mbox(2000);
        using MailContainer c = Mail.Open(new MemorySource(data));
        long Measure(Action<IMailItem> use)
        {
            foreach (IMailItem warm in c.Enumerate().Take(50))
            {
                use(warm);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            int n = 0;
            foreach (IMailItem item in c.Enumerate())
            {
                use(item);
                n++;
            }

            Assert.Equal(2000, n);
            return (GC.GetAllocatedBytesForCurrentThread() - before) / n;
        }

        // Enumeration alone creates one small item per message and reads nothing; docs/shared-view.md has the figure for subject, from and date.
        long enumerate = Measure(_ => { });
        long read = Measure(i =>
        {
            _ = i.Subject;
            _ = i.From;
            _ = i.Sent;
        });
        Assert.True(enumerate < 300, "enumeration allocated " + enumerate + " bytes per message");
        Assert.True(read < 8000, "subject+from+date allocated " + read + " bytes per message");
    }
}
