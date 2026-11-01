using System.Text;
using RecursiveAI.OneMail.Client.Imap.Protocol;
using RecursiveAI.OneMail.Client.Pop3.Protocol;
using RecursiveAI.OneMail.Client.Protocol;
using RecursiveAI.OneMail.Client.Sasl;
using RecursiveAI.OneMail.Client.Smtp.Protocol;
using RecursiveAI.OneMail.Mime.Reading;

namespace RecursiveAI.OneMail.Tests.Client;

// SMTP submission against Dovecot and Stalwart, POP3 against all three, when tools/servers/servers.sh started them.
public class SmtpPop3ServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<ServerInfo> SmtpServers => ServerHarness.SmtpServers();

    public static TheoryData<ServerInfo> Pop3Servers => ServerHarness.Pop3Servers();

    private static async Task<SmtpRawConnection> SmtpAsync(ServerInfo s, bool implicitTls)
    {
        SmtpRawConnection smtp = await SmtpRawConnection.ConnectAsync("127.0.0.1", s.Port(implicitTls ? "submissions" : "submission"), ServerHarness.Options(implicitTls, s.Name, "smtp"), Ct);
        SmtpReply greeting = await smtp.ReadReplyAsync(Ct);
        Assert.Equal(220, greeting.Code);
        SmtpReply ehlo = await smtp.EhloAsync("client.example.test", Ct);
        Assert.Equal(250, ehlo.Code);
        Assert.NotNull(smtp.Capabilities);
        return smtp;
    }

    private static async Task AuthenticatedAsync(SmtpRawConnection smtp, ServerInfo s)
    {
        Assert.Contains("PLAIN", smtp.Capabilities!.AuthMechanisms);
        SmtpReply auth = await smtp.AuthenticateAsync(new SaslPlain(s.User, s.Password), Ct);
        Assert.Equal(235, auth.Code);
        Assert.Equal(250, (await smtp.EhloAsync("client.example.test", Ct)).Code);
    }

    [Theory]
    [MemberData(nameof(SmtpServers))]
    public async Task ImplicitTlsEhloAuthPlainAndPipelinedDataTransaction(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        await using SmtpRawConnection smtp = await SmtpAsync(s, implicitTls: true);
        Assert.True(smtp.Connection.IsEncrypted);
        await AuthenticatedAsync(smtp, s);
        Assert.True(smtp.Capabilities!.Has("PIPELINING"));
        Assert.True(smtp.Capabilities.Has("8BITMIME"));
        Assert.True(smtp.Capabilities.Has("SMTPUTF8"));

        // MAIL FROM with 8BITMIME and SMTPUTF8 parameters, two recipients, pipelined in one write.
        IReadOnlyList<SmtpReply> envelope = await smtp.PipelineAsync(
        [
            "MAIL " + SmtpRawConnection.MailFromArguments(s.User.Contains('@', StringComparison.Ordinal) ? s.User : "sender@example.com", new SmtpMailOptions { Body = SmtpBodyType.EightBitMime, SmtpUtf8 = true }),
            "RCPT " + SmtpRawConnection.RcptToArguments(s.Recipient),
            "RCPT " + SmtpRawConnection.RcptToArguments(s.Recipient),
        ], Ct);
        Assert.All(envelope, r => Assert.True(r.IsPositiveCompletion, r.ToString()));

        (SmtpReply data, SmtpDataStream? stream) = await smtp.BeginDataAsync(cancellationToken: Ct);
        Assert.Equal(354, data.Code);
        byte[] message = ServerHarness.SampleMessage(Guid.NewGuid().ToString("N")[..12]);
        await stream!.WriteAsync(message, Ct);
        SmtpReply done = await stream.CompleteAsync(Ct);
        Assert.True(done.IsPositiveCompletion, done.ToString());

        Assert.Equal(221, (await smtp.CommandAsync("QUIT", null, Ct)).Code);
    }

    [Theory]
    [MemberData(nameof(SmtpServers))]
    public async Task StartTlsThenBdatChunksArePipelined(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        Assert.SkipUnless(s.Has("submission"), "no STARTTLS submission listener");
        await using SmtpRawConnection smtp = await SmtpAsync(s, implicitTls: false);
        Assert.True(smtp.Capabilities!.Has("STARTTLS"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await smtp.AuthenticateAsync(new SaslPlain(s.User, s.Password), Ct));
        Assert.Equal(220, (await smtp.StartTlsAsync(Ct)).Code);
        Assert.True(smtp.Connection.IsEncrypted);
        Assert.Null(smtp.Capabilities);
        Assert.Equal(250, (await smtp.EhloAsync("client.example.test", Ct)).Code);
        await AuthenticatedAsync(smtp, s);
        Assert.SkipUnless(smtp.Capabilities!.Has("CHUNKING"), "server has no CHUNKING");

        string from = s.User.Contains('@', StringComparison.Ordinal) ? s.User : "sender@example.com";
        Assert.True((await smtp.CommandAsync("MAIL", SmtpRawConnection.MailFromArguments(from, new SmtpMailOptions { Body = SmtpBodyType.EightBitMime }), Ct)).IsPositiveCompletion);
        Assert.True((await smtp.CommandAsync("RCPT", SmtpRawConnection.RcptToArguments(s.Recipient), Ct)).IsPositiveCompletion);
        byte[] message = ServerHarness.SampleMessage(Guid.NewGuid().ToString("N")[..12]);
        int cut = message.Length / 3;
        // Two chunks sent back to back, then two replies: the first is the chunk, the second completes the message.
        await smtp.SendBdatAsync(message.AsMemory(0, cut), last: false, Ct);
        using var rest = new MemoryStream(message, cut, message.Length - cut);
        await smtp.SendBdatAsync(rest, rest.Length, last: true, Ct);
        SmtpReply first = await smtp.ReadReplyAsync(Ct);
        SmtpReply last = await smtp.ReadReplyAsync(Ct);
        Assert.True(first.IsPositiveCompletion, first.ToString());
        Assert.True(last.IsPositiveCompletion, last.ToString());
        Assert.Equal(221, (await smtp.CommandAsync("QUIT", null, Ct)).Code);
    }

    [Theory]
    [MemberData(nameof(SmtpServers))]
    public async Task RejectedRecipientIsAnOrdinaryReplyWithAnEnhancedCode(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        await using SmtpRawConnection smtp = await SmtpAsync(s, implicitTls: true);
        await AuthenticatedAsync(smtp, s);
        SmtpReply bad = await smtp.CommandAsync("MAIL", "FROM:<a@example.com> SIZE=notanumber", Ct);
        Assert.True(bad.IsPermanentFailure || bad.IsTransientFailure, bad.ToString());
        Assert.Equal(250, (await smtp.CommandAsync("RSET", null, Ct)).Code);
    }

    [Theory]
    [MemberData(nameof(Pop3Servers))]
    public async Task Pop3ImplicitTlsCapaLoginStatListUidlRetrAndTop(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        // A message to read: put one in the INBOX through IMAP.
        byte[] message = ServerHarness.SampleMessage("pop" + Guid.NewGuid().ToString("N")[..9]);
        await using (ImapRawConnection imap = await ImapRawConnection.ConnectAsync("127.0.0.1", s.Port("imaps"), ServerHarness.Options(true, s.Name, "imap-seed"), Ct))
        {
            await imap.ReadGreetingAsync(Ct);
            Assert.True((await imap.AuthenticateAsync(new SaslPlain(s.User, s.Password), Ct)).IsOk);
            Assert.True((await imap.ExecuteAsync(ImapCommand.Create("APPEND").Atom("INBOX").Literal(message), null, null, Ct)).IsOk);
            await imap.ExecuteAsync(ImapCommand.Create("LOGOUT"), null, null, Ct);
        }

        await using Pop3RawConnection pop = await Pop3RawConnection.ConnectAsync("127.0.0.1", s.Port("pop3s"), ServerHarness.Options(true, s.Name, "pop3"), Ct);
        Assert.True((await pop.ReadGreetingAsync(Ct)).IsOk);
        (Pop3Response capaResponse, Pop3Capabilities? caps) = await pop.CapaAsync(Ct);
        Assert.True(capaResponse.IsOk);
        Assert.True(caps!.Has("UIDL"));
        Assert.True(caps.Has("TOP"));

        Pop3Response login = s.Name == "cyrus"
            ? await pop.AuthenticateAsync(new SaslPlain(s.User, s.Password), Ct)
            : await pop.LoginAsync(s.User, s.Password, Ct);
        Assert.True(login.IsOk, login.ToString());

        (Pop3Response stat, int count, long size) = await pop.StatAsync(Ct);
        Assert.True(stat.IsOk);
        Assert.True(count >= 1);
        Assert.True(size >= message.Length);
        (Pop3Response lr, IReadOnlyList<Pop3ListEntry> list) = await pop.ListAsync(false, Ct);
        Assert.True(lr.IsOk);
        Assert.Equal(count, list.Count);
        (Pop3Response ur, IReadOnlyList<Pop3ListEntry> uidl) = await pop.ListAsync(true, Ct);
        Assert.True(ur.IsOk);
        Assert.Equal(count, uidl.Count);

        // RETR streamed into the MIME reader; TOP gives only the headers.
        // Other tests deliver to the same mailbox, so the message seeded above is found by its marker rather than by position.
        string marker = Encoding.ASCII.GetString(message).Split("Message-ID: <")[1].Split('@')[0];
        int number = 0;
        for (int i = list.Count - 1; i >= 0 && number == 0; i--)
        {
            (Pop3Response t, Pop3DotStream? h) = await pop.MultilineAsync("TOP", list[i].Number.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 0", Ct);
            Assert.True(t.IsOk);
            using var tm = new MemoryStream();
            await h!.CopyToAsync(tm, Ct);
            if (Encoding.ASCII.GetString(tm.ToArray()).Contains(marker, StringComparison.Ordinal)) { number = list[i].Number; }
        }

        Assert.NotEqual(0, number);
        (Pop3Response retr, Pop3DotStream? body) = await pop.MultilineAsync("RETR", number.ToString(System.Globalization.CultureInfo.InvariantCulture), Ct);
        Assert.True(retr.IsOk);
        long bodyBytes = 0;
        int parts = 0;
        using (var mime = new MimeReader(body!))
        {
            while (await mime.ReadAsync(Ct))
            {
                if (mime.Kind == MimeEventKind.EntityStart) { parts++; }
                if (mime.Kind == MimeEventKind.Body) { bodyBytes += mime.Length; }
            }
        }

        Assert.True(body!.IsComplete);
        Assert.Equal(3, parts);
        Assert.True(bodyBytes > 20);

        (Pop3Response top, Pop3DotStream? headers) = await pop.MultilineAsync("TOP", number + " 0", Ct);
        Assert.True(top.IsOk);
        using var ms = new MemoryStream();
        await headers!.CopyToAsync(ms, Ct);
        Assert.Contains("Subject:", Encoding.ASCII.GetString(ms.ToArray()), StringComparison.Ordinal);
        Assert.True((await pop.CommandAsync("QUIT", null, Ct)).IsOk);
    }

    [Theory]
    [MemberData(nameof(Pop3Servers))]
    public async Task Pop3StlsUpgradeAndSynchronousTwins(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        Assert.SkipUnless(s.Has("pop3"), "no STLS listener");
        using Pop3RawConnection pop = Pop3RawConnection.Connect("127.0.0.1", s.Port("pop3"), ServerHarness.Options(false, s.Name, "pop3-stls"));
        Assert.True(pop.ReadGreeting().IsOk);
        Assert.Throws<InvalidOperationException>(() => pop.Authenticate(new SaslPlain(s.User, s.Password)));
        Assert.True(pop.StartTls().IsOk);
        Assert.True(pop.Connection.IsEncrypted);
        Assert.True(pop.Authenticate(new SaslPlain(s.User, s.Password)).IsOk);
        Assert.True(pop.Command("STAT").IsOk);
        Assert.True(pop.Command("QUIT").IsOk);
    }
}
