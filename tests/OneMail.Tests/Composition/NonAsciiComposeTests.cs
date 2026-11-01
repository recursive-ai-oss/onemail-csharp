using System.Text;
using RecursiveAI.OneMail.Composition;

namespace RecursiveAI.OneMail.Tests.Composition;

/// <summary>Display names, subjects and file names with non-ASCII text, composed and read back.</summary>
public class NonAsciiComposeTests
{
    private static readonly string[] Names =
    [
        "Jörg Müller",
        "Müller, Jörg",
        "Zoë \"Z\" @home",
        "日本語の名前がとても長い人 日本語の名前がとても長い人 日本語の名前がとても長い人",
        "😀 Emoji (work)",
        "O'Brien Ünal",
        "Plain, Name",
        "Ünal; <not> an address",
    ];

    private static readonly string[] FileNames =
    [
        "Übersicht für Quartal 3 – final (v2) 😀.pdf",
        "résumé.docx",
        "名前.txt",
        "with \"quotes\" and ünïcode.txt",
        "100% été.txt",
        "a-very-long-file-name-with-ümlauts-" + new string('x', 120) + ".bin",
    ];

    public static IEnumerable<TheoryDataRow<bool>> Modes() => [new(false), new(true)];

    [Theory]
    [MemberData(nameof(Modes))]
    public void NamesSubjectAndFileNamesReadBackAsComposed(bool utf8)
    {
        for (int i = 0; i < Names.Length; i++)
        {
            string name = Names[i];
            string file = FileNames[i % FileNames.Length];
            string subject = "Grüße " + i + " aus Köln – ein Betreff mit Emoji 😀 und \"Anführungszeichen\", der lang genug ist, um gefaltet zu werden";
            var m = new OutgoingMessage
            {
                From = Mailbox(name, "from@example.org"),
                To = [Mailbox(name, "to1@example.org"), Mailbox("Second Ünal", "to2@example.org")],
                Cc = [Mailbox(name, "cc@example.org")],
                ReplyTo = Mailbox(name, "reply@example.org"),
                Subject = subject,
                Text = "hello",
                Attachments = [new OutgoingAttachment(file, new byte[] { 1, 2, 3 }, "application/octet-stream")],
                MessageId = "fixed@example.org",
                Date = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
                Utf8Headers = utf8,
            };

            byte[] bytes = m.ToEntity().ToArray();
            if (!utf8)
            {
                // Without the SMTPUTF8 request nothing outside ASCII may reach the headers.
                int blank = IndexOf(bytes, "\r\n\r\n"u8);
                Assert.True(System.Text.Ascii.IsValid(bytes.AsSpan(0, blank)), "non-ASCII byte in the header block for " + name);
            }

            IMailItem item = Mail.Parse(bytes);
            Assert.Equal(subject, item.Subject);
            Assert.Equal(name.Trim(), item.From.Single().Name);
            Assert.Equal("from@example.org", item.From.Single().Address);
            Assert.Equal([name, "Second Ünal"], item.ToRecipients.Select(a => a.Name));
            Assert.Equal(["to1@example.org", "to2@example.org"], item.ToRecipients.Select(a => a.Address));
            Assert.Equal(name, item.Cc.Single().Name);
            Assert.Equal(file, item.Attachments.Single().FileName);
        }
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void ARepliesAndAForwardsSubjectNameAndFileNameReadBackToo(bool utf8)
    {
        string name = "Müller, Jörg \"JM\"";
        string file = FileNames[0];
        MimeBytes parent = new(new OutgoingMessage
        {
            From = Mailbox(name, "jm@example.org"),
            To = [Mailbox("Zoë", "me@example.org")],
            Subject = "Grüße – Übersicht",
            Text = "body",
            Attachments = [new OutgoingAttachment(file, new byte[] { 9, 9 })],
            MessageId = "p1@example.org",
        }.ToEntity().ToArray());

        IMailItem original = Mail.Parse(parent.Bytes);
        var options = new CompositionOptions { Utf8Headers = utf8, From = new MailAddress("Zoë Ünal", "me@example.org"), IncludeAttachments = true, MessageIdDomain = "example.org" };
        foreach (ComposedMessage c in new[] { MessageComposer.Reply(original, options), MessageComposer.Forward(original, options) })
        {
            byte[] bytes = c.Message.ToArray();
            if (!utf8)
            {
                int blank = IndexOf(bytes, "\r\n\r\n"u8);
                Assert.True(System.Text.Ascii.IsValid(bytes.AsSpan(0, blank)), "non-ASCII byte in the header block");
            }

            IMailItem item = Mail.Parse(bytes);
            Assert.Equal(c.Subject, item.Subject);
            Assert.Contains("Grüße – Übersicht", item.Subject);
            Assert.Equal("Zoë Ünal", item.From.Single().Name);
            if (c.To.Count > 0)
            {
                Assert.Equal(name, item.ToRecipients.Single().Name);
            }

            Assert.Contains(item.Attachments, a => a.FileName == file);
        }
    }

    private readonly record struct MimeBytes(byte[] Bytes);

    private static string Mailbox(string name, string address) => "\"" + name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\" <" + address + ">";

    private static int IndexOf(byte[] data, ReadOnlySpan<byte> pattern) => data.AsSpan().IndexOf(pattern);
}
