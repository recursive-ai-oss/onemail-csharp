using System.Text;
using RecursiveAI.OneMail.Client.Imap;
using RecursiveAI.OneMail.Client.Pop3;
using RecursiveAI.OneMail.Client.Sasl;
using RecursiveAI.OneMail.Mime;

namespace RecursiveAI.OneMail.Tests.Client;

// The POP3 client against the published server images. Messages are delivered into the INBOX through the IMAP client first.
public class Pop3ClientServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<ServerInfo> Servers() => ServerHarness.Pop3Servers();

    private static async Task<Pop3Client> ConnectAsync(ServerInfo s, bool implicitTls = true, bool login = true)
    {
        var options = new Pop3ClientOptions { Connection = ServerHarness.Options(implicitTls, s.Name, "pop3") };
        Pop3Client c = await Pop3Client.ConnectAsync("127.0.0.1", s.Port(implicitTls ? "pop3s" : "pop3"), options, Ct);
        if (login)
        {
            await c.LoginAsync(s.User, s.Password, Ct);
        }

        return c;
    }

    private static async Task DeliverAsync(ServerInfo s, params byte[][] messages)
    {
        await using ImapClient imap = await ImapClientServerTests.ConnectAsync(s);
        await imap.AppendAsync("INBOX", [.. messages.Select(m => new ImapAppendItem(m))], Ct);
    }

    private static byte[] Msg(string marker) => ServerHarness.SampleMessage(marker);

    private static async Task<int> FindAsync(Pop3Client c, string marker)
    {
        foreach (Pop3MessageInfo m in await c.ListAsync(Ct))
        {
            MimeEntity top = await c.GetTopAsync(m.Number, 0, cancellationToken: Ct);
            if (top.GetMessageId()?.Contains(marker, StringComparison.Ordinal) ?? false)
            {
                return m.Number;
            }
        }

        return -1;
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task StlsLoginStatListUidl(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        Assert.SkipUnless(s.Has("pop3"), "no STLS listener");
        string marker = "p" + Guid.NewGuid().ToString("N")[..8];
        await DeliverAsync(s, Msg(marker));
        await using Pop3Client c = await ConnectAsync(s, implicitTls: false);
        Assert.True(c.IsEncrypted, "Auto STLS should have upgraded the connection");
        Assert.True(c.IsAuthenticated);
        (int count, long size) = await c.StatAsync(Ct);
        Assert.True(count >= 1 && size > 0);
        IReadOnlyList<Pop3MessageInfo> list = await c.ListAsync(Ct);
        Assert.Equal(count, list.Count);
        Assert.All(list, m => Assert.True(m.Size > 0));
        if (c.Capabilities?.Has("UIDL") ?? true)
        {
            IReadOnlyList<Pop3MessageInfo> uids = await c.UidlAsync(Ct);
            Assert.Equal(count, uids.Count);
            Assert.Equal(count, uids.Select(u => u.Uid).Distinct().Count());
        }

        await c.NoopAsync(Ct);
        await c.QuitAsync(Ct);
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task RetrieveStreamsTheMessageUnstuffedAndParsesIt(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        string marker = "r" + Guid.NewGuid().ToString("N")[..8];
        byte[] msg = Msg(marker);
        await DeliverAsync(s, msg);
        await using Pop3Client c = await ConnectAsync(s);
        int n = await FindAsync(c, marker);
        Assert.True(n > 0, "the delivered message is not in the maildrop");
        using var ms = new MemoryStream();
        long written = await c.RetrieveAsync(n, ms, Ct);
        Assert.Equal(written, ms.Length);
        // The sample has a line that starts with a period: it must come back unstuffed.
        string text = Encoding.UTF8.GetString(ms.ToArray());
        Assert.Contains("\r\n.A line that starts with a period.\r\n", text, StringComparison.Ordinal);
        MimeEntity tree = await c.GetMessageAsync(n, cancellationToken: Ct);
        Assert.Equal("multipart/mixed", tree.MediaType.ToLowerInvariant());
        Assert.Equal(2, ((MimeMultipart)tree).Children.Count);
        MimeEntity top = await c.GetTopAsync(n, 2, cancellationToken: Ct);
        Assert.Contains(marker, top.GetMessageId() ?? "", StringComparison.Ordinal);
        await Assert.ThrowsAsync<Pop3CommandException>(async () => await c.RetrieveAsync(1_000_000, Stream.Null, Ct));
        // The failed command did not break the session.
        await c.NoopAsync(Ct);
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task PipelinedRetrieveAndDeleteAppliedAtQuit(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        string tag = "pl" + Guid.NewGuid().ToString("N")[..6];
        byte[][] batch = [.. Enumerable.Range(0, 24).Select(i => Msg($"{tag}-{i}"))];
        await DeliverAsync(s, batch);
        var mine = new List<int>();
        await using (Pop3Client c = await ConnectAsync(s))
        {
            var seen = new List<(int Number, MimeEntity Message)>();
            IReadOnlyList<Pop3MessageInfo> all = await c.ListAsync(Ct);
            await foreach ((int number, MimeEntity message) in c.GetMessagesAsync([.. all.Select(m => m.Number)], cancellationToken: Ct))
            {
                seen.Add((number, message));
            }

            Assert.Equal(all.Count, seen.Count);
            foreach ((int number, MimeEntity message) in seen)
            {
                if (message.GetMessageId()?.Contains(tag, StringComparison.Ordinal) ?? false)
                {
                    mine.Add(number);
                }
            }

            Assert.Equal(24, mine.Count);
            await c.DeleteAsync(mine, Ct);
            // RFC 1939 §5: marked messages disappear from the listing of this session. Stalwart still lists them.
            int listed = (await c.ListAsync(Ct)).Count;
            Assert.True(listed == all.Count - 24 || (s.Name == "stalwart" && listed == all.Count), $"{listed} listed after DELE of 24 from {all.Count}");
            await c.ResetAsync(Ct);
            Assert.Equal(all.Count, (await c.ListAsync(Ct)).Count);
            await c.DeleteAsync(mine, Ct);
            await c.QuitAsync(Ct);
        }

        await using Pop3Client again = await ConnectAsync(s);
        Assert.Equal(-1, await FindAsync(again, tag + "-0"));
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task SaslPlainAuthenticate(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        await using Pop3Client c = await ConnectAsync(s, login: false);
        Assert.SkipUnless(c.Capabilities?.SaslMechanisms.Contains("PLAIN") ?? false, s.Name + " does not list SASL PLAIN for POP3");
        await c.AuthenticateAsync(new SaslPlain(s.User, s.Password), Ct);
        Assert.True(c.IsAuthenticated);
        await c.StatAsync(Ct);
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task WrongPasswordIsAPop3CommandException(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        Assert.SkipWhen(s.Name == "cyrus", "this Cyrus image accepts any password");
        await using Pop3Client c = await ConnectAsync(s, login: false);
        await Assert.ThrowsAsync<Pop3CommandException>(async () => await c.LoginAsync(s.User, "wrong", Ct));
        Assert.False(c.IsAuthenticated);
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task ApopWhenTheGreetingCarriesATimestamp(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        Assert.SkipUnless(s.Has("pop3"), "no plain listener");
        // APOP needs the shared secret in the clear on the server side, so servers rarely enable it with hashed passwords.
        var options = new Pop3ClientOptions { Connection = ServerHarness.Options(false, s.Name, "pop3"), StartTls = Pop3StartTlsMode.Never };
        await using Pop3Client c = await Pop3Client.ConnectAsync("127.0.0.1", s.Port("pop3"), options, Ct);
        Assert.SkipUnless(c.Raw.ApopTimestamp is not null, s.Name + " sends no APOP timestamp in its greeting");
        try
        {
            await c.ApopAsync(s.User, s.Password, Ct);
            Assert.True(c.IsAuthenticated);
        }
        catch (Pop3CommandException)
        {
            Assert.Skip(s.Name + " advertises APOP but cannot verify it for this account");
        }
    }
}
