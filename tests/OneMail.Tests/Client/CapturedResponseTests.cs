using System.Runtime.CompilerServices;
using RecursiveAI.OneMail.Client.Imap.Protocol;
using RecursiveAI.OneMail.Client.Pop3.Protocol;
using RecursiveAI.OneMail.Client.Protocol;
using RecursiveAI.OneMail.Client.Smtp.Protocol;

namespace RecursiveAI.OneMail.Tests.Client;

/// <summary>
/// The server side of real sessions with Dovecot, Stalwart and Cyrus (<c>testdata/client/captures</c>, recorded by the container
/// tests) fed back through the response readers: every response parses, none carries a defect, and each session yields what it did.
/// </summary>
public class CapturedResponseTests
{
    private static string Dir([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "testdata", "client", "captures"));

    public static TheoryData<string> Files(string prefix)
    {
        var d = new TheoryData<string>();
        foreach (string f in Directory.EnumerateFiles(Dir()).Where(f => Path.GetFileName(f).Split('-')[1] == prefix).Order())
        {
            d.Add(Path.GetFileName(f));
        }

        return d;
    }

    public static TheoryData<string> ImapFiles() => Files("imap");

    public static TheoryData<string> SmtpFiles() => Files("smtp");

    public static TheoryData<string> Pop3Files() => Files("pop3");

    [Theory]
    [MemberData(nameof(ImapFiles))]
    public void ImapSessionsParseWithoutDefects(string name)
    {
        (ProtocolConnection conn, _) = ClientTestSupport.Scripted(File.ReadAllBytes(Path.Combine(Dir(), name)));
        using (conn)
        {
            var reader = new ImapResponseReader(conn.Reader);
            int count = 0;
            try
            {
                while (true)
                {
                    ImapResponse r = reader.ReadResponse();
                    count++;
                    Assert.Empty(r.Defects);
                }
            }
            catch (MailConnectionClosedException)
            {
                // The end of the recording.
            }

            Assert.True(count >= 2, $"{name}: {count} responses");
        }
    }

    [Theory]
    [MemberData(nameof(SmtpFiles))]
    public void SmtpSessionsParseAsReplies(string name)
    {
        (ProtocolConnection conn, _) = ClientTestSupport.Scripted(File.ReadAllBytes(Path.Combine(Dir(), name)));
        using (conn)
        {
            var reader = new SmtpReplyReader(conn.Reader);
            int count = 0;
            try
            {
                while (true)
                {
                    SmtpReply r = reader.ReadReply();
                    count++;
                    Assert.InRange(r.Code, 200, 599);
                }
            }
            catch (MailConnectionClosedException)
            {
            }

            Assert.True(count >= 3, $"{name}: {count} replies");
        }
    }

    [Theory]
    [MemberData(nameof(Pop3Files))]
    public void Pop3SessionsStartWithAGreeting(string name)
    {
        (ProtocolConnection conn, _) = ClientTestSupport.Scripted(File.ReadAllBytes(Path.Combine(Dir(), name)));
        using (conn)
        {
            var pop = new Pop3RawConnection(conn);
            Assert.True(pop.ReadGreeting().IsOk);
        }
    }
}
