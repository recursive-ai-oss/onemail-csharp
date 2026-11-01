using System.Text;
using RecursiveAI.OneMail.Client.Pop3.Protocol;
using RecursiveAI.OneMail.Client.Protocol;
using RecursiveAI.OneMail.Client.Sasl;
using RecursiveAI.OneMail.Client.Smtp.Protocol;

namespace RecursiveAI.OneMail.Tests.Client;

public class SmtpPop3ProtocolTests
{
    private static SmtpReply Reply(string wire)
    {
        (ProtocolConnection c, _) = ClientTestSupport.Scripted(wire);
        return new SmtpReplyReader(c.Reader).ReadReply();
    }

    [Fact]
    public void RepliesSingleMultiAndEnhancedStatus()
    {
        SmtpReply one = Reply("250 2.1.0 Ok\r\n");
        Assert.Equal(250, one.Code);
        Assert.Equal(new SmtpEnhancedStatus(2, 1, 0), one.EnhancedStatus);
        Assert.True(one.IsPositiveCompletion);

        SmtpReply multi = Reply("250-mail.example.com greets you\r\n250-PIPELINING\r\n250-SIZE 35882577\r\n250-AUTH PLAIN LOGIN\r\n250-AUTH=CRAM-MD5\r\n250 8BITMIME\r\n");
        Assert.Equal(6, multi.Lines.Count);
        var caps = new SmtpCapabilities(multi);
        Assert.Equal("mail.example.com", caps.Domain);
        Assert.True(caps.Has("pipelining"));
        Assert.Equal(35882577, caps.MaxMessageSize);
        Assert.Equal(["PLAIN", "LOGIN", "CRAM-MD5"], caps.AuthMechanisms);

        Assert.Equal(354, Reply("354 go\r\n").Code);
        Assert.Equal(550, Reply("550\r\n").Code);
        Assert.True(Reply("550 5.7.1 nope\r\n").IsPermanentFailure);
        Assert.True(Reply("451 4.3.0 later\r\n").IsTransientFailure);
    }

    [Fact]
    public void MalformedRepliesAreRepairedWithDefects()
    {
        SmtpReply changed = Reply("250-first\r\n251 second\r\n");
        Assert.Equal(250, changed.Code);
        Assert.Contains(changed.Defects, d => d.Kind == ProtocolDefectKind.ReplyCodeChanged);
        SmtpReply none = Reply("hello there\r\n");
        Assert.Equal(0, none.Code);
        Assert.Contains(none.Defects, d => d.Kind == ProtocolDefectKind.ReplyLineWithoutCode);
        SmtpReply bare = Reply("250-a\nb without code\n250 c\n");
        Assert.Equal(3, bare.Lines.Count);
        var small = new ProtocolConnectionOptions { Limits = new ProtocolLimits { MaxReplyLines = 3 } };
        (ProtocolConnection c, _) = ClientTestSupport.Scripted("250-a\r\n250-b\r\n250-c\r\n250-d\r\n250 e\r\n", small);
        Assert.Throws<MailProtocolLimitException>(() => new SmtpReplyReader(c.Reader).ReadReply());
    }

    [Fact]
    public void MailAndRcptArgumentsAreChecked()
    {
        Assert.Equal("FROM:<a@example.com> SIZE=100 BODY=8BITMIME SMTPUTF8 RET=HDRS ENVID=QQ+3D1234",
            SmtpRawConnection.MailFromArguments("a@example.com", new SmtpMailOptions { Size = 100, Body = SmtpBodyType.EightBitMime, SmtpUtf8 = true, DsnReturn = "hdrs", DsnEnvelopeId = "QQ=1234" }));
        Assert.Equal("FROM:<>", SmtpRawConnection.MailFromArguments(""));
        Assert.Equal("TO:<b@example.com> NOTIFY=SUCCESS,FAILURE ORCPT=rfc822;b@example.com",
            SmtpRawConnection.RcptToArguments("b@example.com", new SmtpRcptOptions { Notify = ["success", "failure"], OriginalRecipient = "rfc822;b@example.com" }));
        Assert.Throws<ArgumentException>(() => SmtpRawConnection.RcptToArguments("a@b>\r\nRSET"));
        Assert.Throws<ArgumentException>(() => SmtpRawConnection.RcptToArguments("a b@c"));
    }

    [Fact]
    public async Task PipelinedEnvelopeIsOneWriteAndThreeReplies()
    {
        (ProtocolConnection c, ScriptedStream s) = ClientTestSupport.Scripted("250 2.1.0 ok\r\n250 2.1.5 ok\r\n250 2.1.5 ok\r\n");
        var smtp = new SmtpRawConnection(c);
        IReadOnlyList<SmtpReply> r = await smtp.PipelineAsync(["MAIL FROM:<a@x>", "RCPT TO:<b@x>", "RCPT TO:<c@x>"], TestContext.Current.CancellationToken);
        Assert.Equal(3, r.Count);
        Assert.All(r, x => Assert.Equal(250, x.Code));
        Assert.Equal("MAIL FROM:<a@x>\r\nRCPT TO:<b@x>\r\nRCPT TO:<c@x>\r\n", s.SentText);
        Assert.Throws<ArgumentException>(() => smtp.Send("NOOP", "a\r\nQUIT"));
    }

    [Theory]
    [InlineData("a\r\n.b\r\n..c\r\n", "a\r\n..b\r\n...c\r\n.\r\n", false)]
    [InlineData(".\r\n", "..\r\n.\r\n", false)]
    [InlineData("no end", "no end\r\n.\r\n", false)]
    [InlineData("", ".\r\n", false)]
    [InlineData("a\nb\r.c\r\n", "a\r\nb\r\n..c\r\n.\r\n", true)]
    [InlineData("x\r", "x\r\n.\r\n", true)]
    public void DataStreamStuffsDotsAndTerminates(string body, string expected, bool normalise)
    {
        (ProtocolConnection c, ScriptedStream s) = ClientTestSupport.Scripted("354 go\r\n250 2.0.0 queued\r\n");
        var smtp = new SmtpRawConnection(c);
        (SmtpReply r, SmtpDataStream? data) = smtp.BeginData(normalise);
        Assert.Equal(354, r.Code);
        // Write in awkward pieces so the stuffing state crosses chunk boundaries.
        foreach (byte b in Encoding.ASCII.GetBytes(body))
        {
            data!.Write([b]);
        }

        SmtpReply done = data!.Complete();
        Assert.Equal(250, done.Code);
        Assert.Equal("DATA\r\n" + expected, s.SentText);
    }

    [Fact]
    public async Task BdatChunksArePipelinedAndNotStuffed()
    {
        (ProtocolConnection c, ScriptedStream s) = ClientTestSupport.Scripted("250 2.0.0 ok\r\n250 2.0.0 queued\r\n");
        var smtp = new SmtpRawConnection(c);
        await smtp.SendBdatAsync(".first\r\n"u8.ToArray(), false, TestContext.Current.CancellationToken);
        using var tail = new MemoryStream("last\n.\r\n"u8.ToArray());
        await smtp.SendBdatAsync(tail, tail.Length, true, TestContext.Current.CancellationToken);
        Assert.Equal("BDAT 8\r\n.first\r\nBDAT 8 LAST\r\nlast\n.\r\n", s.SentText);
        Assert.Equal(250, (await smtp.ReadReplyAsync(TestContext.Current.CancellationToken)).Code);
        Assert.Equal(250, (await smtp.ReadReplyAsync(TestContext.Current.CancellationToken)).Code);
    }

    [Fact]
    public async Task SmtpAuthPlainWithInitialResponseAndRefusalWithoutTls()
    {
        (ProtocolConnection c, ScriptedStream s) = ClientTestSupport.Scripted("235 2.7.0 ok\r\n", new ProtocolConnectionOptions { AllowInsecureAuthentication = true });
        var smtp = new SmtpRawConnection(c);
        Assert.Equal(235, (await smtp.AuthenticateAsync(new SaslPlain("tim", "tanstaaftanstaaf"), TestContext.Current.CancellationToken)).Code);
        Assert.Equal("AUTH PLAIN AHRpbQB0YW5zdGFhZnRhbnN0YWFm\r\n", s.SentText);

        (ProtocolConnection refused, ScriptedStream none) = ClientTestSupport.Scripted("");
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await new SmtpRawConnection(refused).AuthenticateAsync(new SaslPlain("a", "b"), TestContext.Current.CancellationToken));
        Assert.Equal("", none.SentText);
    }

    [Fact]
    public void SmtpAuthChallengeFlow()
    {
        (ProtocolConnection c, ScriptedStream s) = ClientTestSupport.Scripted("334 \r\n235 ok\r\n", new ProtocolConnectionOptions { AllowInsecureAuthentication = true });
        var smtp = new SmtpRawConnection(c);
        Assert.Equal(235, smtp.Authenticate(new ChallengeOnly()).Code);
        Assert.Equal("AUTH CHALLENGE\r\nAGhp\r\n", s.SentText);
    }

    private sealed class ChallengeOnly : SaslMechanism
    {
        public override string Name => "CHALLENGE";
        public override bool ClientFirst => false;
        public override byte[]? GetInitialResponse() => null;
        public override byte[] Respond(ReadOnlySpan<byte> challenge) => "\0hi"u8.ToArray();
    }

    // ---- POP3 ----

    [Fact]
    public void Pop3StatusLines()
    {
        Assert.True(Pop3Response.Parse("+OK POP3 server ready <1896.697170952@dbc.mtview.ca.us>"u8).IsOk);
        Pop3Response err = Pop3Response.Parse("-ERR [IN-USE] mailbox busy"u8);
        Assert.Equal(Pop3StatusKind.Err, err.Kind);
        Assert.Equal("IN-USE", err.Code);
        Assert.Equal("mailbox busy", err.Text);
        Assert.Equal(Pop3StatusKind.Ok, Pop3Response.Parse("+ok"u8).Kind);
        Assert.Equal(Pop3StatusKind.Continuation, Pop3Response.Parse("+ AAAA"u8).Kind);
        Pop3Response unknown = Pop3Response.Parse("hello"u8);
        Assert.Equal(Pop3StatusKind.Unknown, unknown.Kind);
        Assert.NotEmpty(unknown.Defects);
    }

    [Fact]
    public async Task Pop3StatListUidlCapaAndRetrUnstuffing()
    {
        string script = "+OK ready <1.2@h>\r\n+OK 2 320\r\n+OK\r\n1 120\r\n2 200\r\n.\r\n+OK\r\n1 uid-a\r\n2 uid-b\r\n.\r\n"
            + "+OK\r\nTOP\r\nUIDL\r\nSASL PLAIN SCRAM-SHA-256\r\n.\r\n+OK 25 octets\r\nSubject: x\r\n\r\n..dot\r\nend\r\n.\r\n+OK bye\r\n";
        (ProtocolConnection c, ScriptedStream s) = ClientTestSupport.Scripted(script);
        var pop = new Pop3RawConnection(c);
        Pop3Response g = await pop.ReadGreetingAsync(TestContext.Current.CancellationToken);
        Assert.True(g.IsOk);
        Assert.Equal("<1.2@h>", pop.ApopTimestamp);
        (Pop3Response _, int count, long size) = await pop.StatAsync(TestContext.Current.CancellationToken);
        Assert.Equal((2, 320L), (count, size));
        (_, IReadOnlyList<Pop3ListEntry> list) = await pop.ListAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal([new Pop3ListEntry(1, "120"), new Pop3ListEntry(2, "200")], list);
        (_, IReadOnlyList<Pop3ListEntry> uidl) = await pop.ListAsync(true, TestContext.Current.CancellationToken);
        Assert.Equal("uid-b", uidl[1].Value);
        (_, Pop3Capabilities? caps) = await pop.CapaAsync(TestContext.Current.CancellationToken);
        Assert.True(caps!.Has("uidl"));
        Assert.Equal(["PLAIN", "SCRAM-SHA-256"], caps.SaslMechanisms);

        (Pop3Response r, Pop3DotStream? body) = await pop.MultilineAsync("RETR", "1", TestContext.Current.CancellationToken);
        Assert.True(r.IsOk);
        // The next command is refused while the body is unread.
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await pop.CommandAsync("NOOP", null, TestContext.Current.CancellationToken));
        using var ms = new MemoryStream();
        await body!.CopyToAsync(ms, TestContext.Current.CancellationToken);
        Assert.Equal("Subject: x\r\n\r\n.dot\r\nend\r\n", Encoding.ASCII.GetString(ms.ToArray()));
        Assert.True(body.IsComplete);
        Assert.True((await pop.CommandAsync("QUIT", null, TestContext.Current.CancellationToken)).IsOk);
        Assert.Contains("RETR 1\r\n", s.SentText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pop3LoginRedactsAndRefusesCleartext()
    {
        var log = new StringWriter();
        (ProtocolConnection c, ScriptedStream s) = ClientTestSupport.Scripted("+OK\r\n+OK logged in\r\n",
            new ProtocolConnectionOptions { AllowInsecureAuthentication = true, Trace = new TextWriterProtocolTrace(log) });
        Assert.True((await new Pop3RawConnection(c).LoginAsync("u", "hunter2", TestContext.Current.CancellationToken)).IsOk);
        Assert.Equal("USER u\r\nPASS hunter2\r\n", s.SentText);
        Assert.DoesNotContain("hunter2", log.ToString());
        (ProtocolConnection plain, _) = ClientTestSupport.Scripted("");
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await new Pop3RawConnection(plain).LoginAsync("u", "p", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Pop3DotStreamKeepsLineEndsAndStopsAtTerminator()
    {
        (ProtocolConnection c, _) = ClientTestSupport.Scripted("+OK\r\na\nb\r\n.\r\nNEXT\r\n");
        var pop = new Pop3RawConnection(c);
        (Pop3Response r, Pop3DotStream? body) = pop.Multiline("RETR", "1");
        Assert.True(r.IsOk);
        using var ms = new MemoryStream();
        body!.CopyTo(ms);
        Assert.Equal("a\nb\r\n", Encoding.ASCII.GetString(ms.ToArray()));
        Assert.Equal("NEXT", Encoding.ASCII.GetString(c.Reader.ReadLine().Span));
    }
}
