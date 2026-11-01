using System.Text;
using RecursiveAI.OneMail.Client.Smtp;
using RecursiveAI.OneMail.Composition;
using RecursiveAI.OneMail.Mime;

namespace RecursiveAI.OneMail.Tests.Client;

// The SMTP client's own logic against a scripted loopback server: parameters, pipelining, BDAT, rejections, size, downgrade, Bcc.
public class SmtpClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SmtpClientOptions Options(FakeSmtpServer s, bool pipelining = true, bool chunking = true) => new()
    {
        Host = "127.0.0.1",
        Port = s.Port,
        Security = SmtpSecurity.None,
        UsePipelining = pipelining,
        UseChunking = chunking,
    };

    private const string Message = "From: a@example.com\r\nTo: b@example.com, c@example.com\r\nSubject: hi\r\n\r\n.dot line\r\nbody\r\n";

    [Fact]
    public async Task SendsWithDataAndPipeliningAndStuffsDots()
    {
        using var server = new FakeSmtpServer("PIPELINING", "8BITMIME", "SIZE 100000");
        await using SmtpClient client = await SmtpClient.ConnectAsync(Options(server), Ct);
        SmtpSendResult r = await client.SendAsync(SmtpMessage.FromBytes(Encoding.ASCII.GetBytes(Message)), Ct);
        Assert.True(r.Complete);
        Assert.Equal(2, r.Recipients.Count);
        Assert.False(r.UsedChunking);
        Assert.Equal(Message, Encoding.ASCII.GetString(server.Messages.Single()));
        Assert.Contains("SIZE=" + Message.Length, server.MailFromLines.Single(), StringComparison.Ordinal);
        Assert.Equal("2.0.0", r.DataReply!.EnhancedStatus!.Value.ToString());
    }

    [Fact]
    public async Task SendsWithBdatInSeveralChunksAndNormalizesBareLineFeeds()
    {
        using var server = new FakeSmtpServer("PIPELINING", "CHUNKING");
        SmtpClientOptions o = new() { Host = "127.0.0.1", Port = server.Port, Security = SmtpSecurity.None, ChunkSize = 1024 };
        await using SmtpClient client = await SmtpClient.ConnectAsync(o, Ct);
        string big = "From: a@example.com\nTo: b@example.com\n\n" + string.Concat(Enumerable.Repeat("0123456789abcdef\n", 400));
        SmtpSendResult r = await client.SendAsync(SmtpMessage.FromBytes(Encoding.ASCII.GetBytes(big)), Ct);
        Assert.True(r.UsedChunking);
        Assert.True(r.Complete);
        Assert.Equal(big.Replace("\n", "\r\n", StringComparison.Ordinal), Encoding.ASCII.GetString(server.Messages.Single()));
        Assert.True(server.Commands.Count(c => c.StartsWith("BDAT", StringComparison.Ordinal)) > 3);
        Assert.EndsWith("LAST", server.Commands.Last(c => c.StartsWith("BDAT", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectedRecipientIsAResultNotAnException()
    {
        using var server = new FakeSmtpServer("PIPELINING") { RejectRecipientContaining = "c@" };
        await using SmtpClient client = await SmtpClient.ConnectAsync(Options(server), Ct);
        SmtpSendResult r = await client.SendAsync(SmtpMessage.FromBytes(Encoding.ASCII.GetBytes(Message)), Ct);
        Assert.True(r.Delivered);
        Assert.False(r.Complete);
        SmtpRecipientResult bad = Assert.Single(r.Rejected);
        Assert.Equal("c@example.com", bad.Address);
        Assert.Equal(550, bad.Reply.Code);
        Assert.Equal("5.1.1", bad.EnhancedStatus!.Value.ToString());
        Assert.Single(r.Accepted);
    }

    [Fact]
    public async Task RequireAllRecipientsSendsNothingWhenOneIsRejected()
    {
        using var server = new FakeSmtpServer("PIPELINING") { RejectRecipientContaining = "c@" };
        await using SmtpClient client = await SmtpClient.ConnectAsync(Options(server), Ct);
        SmtpSendResult r = await client.SendAsync(Msg(Message, requireAll: true), Ct);
        Assert.False(r.Delivered);
        Assert.Null(r.DataReply);
        Assert.Empty(server.Messages);
        Assert.Contains("RSET", server.Commands);
        Assert.DoesNotContain("DATA", server.Commands);
        Assert.Throws<SmtpRejectedException>(r.ThrowIfNotDelivered);
    }

    [Fact]
    public async Task SizeLimitRefusesBeforeAnythingIsSent()
    {
        using var server = new FakeSmtpServer("SIZE 50");
        await using SmtpClient client = await SmtpClient.ConnectAsync(Options(server), Ct);
        var ex = await Assert.ThrowsAsync<SmtpMessageTooLargeException>(async () => await client.SendAsync(SmtpMessage.FromBytes(Encoding.ASCII.GetBytes(Message)), Ct));
        Assert.Equal(50, ex.Limit);
        Assert.DoesNotContain(server.Commands, c => c.StartsWith("MAIL", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SessionIsReusedForSeveralMessages()
    {
        using var server = new FakeSmtpServer("PIPELINING");
        await using SmtpClient client = await SmtpClient.ConnectAsync(Options(server), Ct);
        for (int i = 0; i < 3; i++)
        {
            Assert.True((await client.SendAsync(SmtpMessage.FromBytes(Encoding.ASCII.GetBytes(Message)), Ct)).Complete);
        }

        Assert.Equal(3, server.Messages.Count);
        Assert.Equal(1, server.Commands.Count(c => c.StartsWith("EHLO", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task EightBitBodyIsReEncodedWhenTheServerLacks8BitMime()
    {
        using var server = new FakeSmtpServer("PIPELINING");
        await using SmtpClient client = await SmtpClient.ConnectAsync(Options(server), Ct);
        byte[] msg = Encoding.UTF8.GetBytes("From: a@example.com\r\nTo: b@example.com\r\nMIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: 8bit\r\n\r\nGr\u00fc\u00dfe\r\n");
        SmtpSendResult r = await client.SendAsync(SmtpMessage.FromBytes(msg), Ct);
        Assert.True(r.Downgraded);
        string sent = Encoding.ASCII.GetString(server.Messages.Single());
        Assert.Contains("Content-Transfer-Encoding: quoted-printable", sent, StringComparison.Ordinal);
        Assert.Contains("Gr=C3=BC=C3=9Fe", sent, StringComparison.Ordinal);
        Assert.DoesNotContain(server.MailFromLines, l => l.Contains("BODY=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EightBitBodyIsSentAsIsWhenTheServerHas8BitMime()
    {
        using var server = new FakeSmtpServer("8BITMIME");
        await using SmtpClient client = await SmtpClient.ConnectAsync(Options(server), Ct);
        byte[] msg = Encoding.UTF8.GetBytes("From: a@example.com\r\nTo: b@example.com\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: 8bit\r\n\r\nGr\u00fc\u00dfe\r\n");
        SmtpSendResult r = await client.SendAsync(SmtpMessage.FromBytes(msg), Ct);
        Assert.False(r.Downgraded);
        Assert.Contains("BODY=8BITMIME", server.MailFromLines.Single(), StringComparison.Ordinal);
        Assert.Equal(msg, server.Messages.Single());
    }

    [Fact]
    public async Task SignedEightBitMessageIsRefusedNotReEncoded()
    {
        using var server = new FakeSmtpServer("PIPELINING");
        await using SmtpClient client = await SmtpClient.ConnectAsync(Options(server), Ct);
        byte[] msg = Encoding.UTF8.GetBytes("DKIM-Signature: v=1; a=rsa-sha256; d=example.com; s=x; h=from; bh=AA==; b=AA==\r\nFrom: a@example.com\r\nTo: b@example.com\r\nContent-Transfer-Encoding: 8bit\r\n\r\nGr\u00fc\u00dfe\r\n");
        await Assert.ThrowsAsync<SmtpDowngradeRefusedException>(async () => await client.SendAsync(SmtpMessage.FromBytes(msg), Ct));
        Assert.Empty(server.Messages);
    }

    [Fact]
    public async Task Utf8AddressNeedsSmtpUtf8()
    {
        using var server = new FakeSmtpServer("8BITMIME");
        await using SmtpClient client = await SmtpClient.ConnectAsync(Options(server), Ct);
        byte[] msg = Encoding.UTF8.GetBytes("From: a@example.com\r\nTo: j\u00fcrgen@example.com\r\n\r\nx\r\n");
        var ex = await Assert.ThrowsAsync<SmtpFeatureRequiredException>(async () => await client.SendAsync(SmtpMessage.FromBytes(msg), Ct));
        Assert.Equal("SMTPUTF8", ex.Feature);
        using var server2 = new FakeSmtpServer("8BITMIME", "SMTPUTF8");
        await using SmtpClient client2 = await SmtpClient.ConnectAsync(Options(server2), Ct);
        SmtpSendResult r = await client2.SendAsync(SmtpMessage.FromBytes(msg), Ct);
        Assert.True(r.UsedSmtpUtf8);
        Assert.Contains(" SMTPUTF8", server2.MailFromLines.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DsnAndRequireTlsNeedTheExtensions()
    {
        using var server = new FakeSmtpServer("PIPELINING");
        await using SmtpClient client = await SmtpClient.ConnectAsync(Options(server), Ct);
        Assert.Equal("DSN", (await Assert.ThrowsAsync<SmtpFeatureRequiredException>(async () => await client.SendAsync(
            Msg(Message, dsn: new SmtpDsnOptions { Return = "HDRS" }), Ct))).Feature);
        Assert.Equal("REQUIRETLS", (await Assert.ThrowsAsync<SmtpFeatureRequiredException>(async () => await client.SendAsync(
            Msg(Message, requireTls: true), Ct))).Feature);

        using var server2 = new FakeSmtpServer("DSN");
        await using SmtpClient client2 = await SmtpClient.ConnectAsync(Options(server2), Ct);
        await client2.SendAsync(Msg(Message, dsn: new SmtpDsnOptions { Return = "hdrs", EnvelopeId = "id+1", Notify = ["SUCCESS", "FAILURE"] }), Ct);
        Assert.Contains("RET=HDRS ENVID=id+2B1", server2.MailFromLines.Single(), StringComparison.Ordinal);
        Assert.Contains(server2.Commands, c => c.StartsWith("RCPT", StringComparison.Ordinal) && c.Contains("NOTIFY=SUCCESS,FAILURE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BccIsUsedForDeliveryButNotTransmittedAndTheTreeIsRestored()
    {
        using var server = new FakeSmtpServer("PIPELINING");
        await using SmtpClient client = await SmtpClient.ConnectAsync(Options(server), Ct);
        MimeEntity entity = new OutgoingMessage { From = "Ann <a@example.com>", To = ["b@example.com"], Bcc = ["secret@example.com"], Subject = "caf\u00e9", Text = "hi" }.ToEntity();
        string before = Encoding.ASCII.GetString(entity.ToArray());
        SmtpSendResult r = await client.SendAsync(SmtpMessage.FromEntity(entity), Ct);
        Assert.Equal(2, r.Accepted.Count());
        string sent = Encoding.ASCII.GetString(server.Messages.Single());
        Assert.DoesNotContain("secret@example.com", sent, StringComparison.Ordinal);
        Assert.Contains("Subject: =?", sent, StringComparison.Ordinal);
        Assert.Equal(before, Encoding.ASCII.GetString(entity.ToArray()));
        Assert.Contains("Bcc: secret@example.com", before, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MailSendAsyncIsOneCall()
    {
        using var server = new FakeSmtpServer("PIPELINING");
        SmtpSendResult r = await Mail.SendAsync(new OutgoingMessage { From = "a@example.com", To = ["b@example.com"], Subject = "x", Text = "hello" }, Options(server), Ct);
        Assert.True(r.Complete);
        Assert.Contains("QUIT", server.Commands);
        MimeEntity back = MimeEntity.Parse(server.Messages.Single());
        Assert.Equal("x", back.Headers.GetFirst("Subject")!.Value);
        Assert.NotNull(back.Headers.GetFirst("Message-ID"));
    }

    [Fact]
    public void SynchronousTwinWorks()
    {
        using var server = new FakeSmtpServer("PIPELINING", "CHUNKING");
        using SmtpClient client = SmtpClient.Connect(Options(server));
        SmtpSendResult r = client.Send(SmtpMessage.FromBytes(Encoding.ASCII.GetBytes(Message)));
        Assert.True(r.Complete);
        Assert.True(r.UsedChunking);
        client.Quit();
    }

    [Fact]
    public async Task AnEmptyResponseToAChallengeIsAnEmptyLine()
    {
        // RFC 4954 §4: "=" stands for an empty response only in place of an initial response; after a challenge it is an empty line.
        (RecursiveAI.OneMail.Client.Protocol.ProtocolConnection conn, ScriptedStream wire) = ClientTestSupport.Scripted(
            "334 eyJzdGF0dXMiOiI0MDEifQ==\r\n535 5.7.8 rejected\r\n", new RecursiveAI.OneMail.Client.Protocol.ProtocolConnectionOptions { AllowInsecureAuthentication = true });
        await using var raw = new RecursiveAI.OneMail.Client.Smtp.Protocol.SmtpRawConnection(conn);
        var reply = await raw.AuthenticateAsync(new RecursiveAI.OneMail.Client.Sasl.SaslXOAuth2("u", "t"), Ct);
        Assert.Equal(535, reply.Code);
        Assert.EndsWith("\r\n\r\n", wire.SentText, StringComparison.Ordinal);
        Assert.DoesNotContain("=\r\n", wire.SentText.Replace("==\r\n", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    private static SmtpMessage Msg(string text, bool requireAll = false, SmtpDsnOptions? dsn = null, bool requireTls = false)
    {
        SmtpMessage m = SmtpMessage.FromBytes(Encoding.ASCII.GetBytes(text));
        m.RequireAllRecipients = requireAll;
        m.Dsn = dsn;
        m.RequireTls = requireTls;
        return m;
    }
}
