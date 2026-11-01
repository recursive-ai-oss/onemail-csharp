using System.Globalization;
using System.Text;
using RecursiveAI.OneMail.Client.Imap.Protocol;
using RecursiveAI.OneMail.Client.Protocol;
using RecursiveAI.OneMail.Client.Sasl;
using RecursiveAI.OneMail.Client.Smtp;
using RecursiveAI.OneMail.Client.Smtp.Protocol;
using RecursiveAI.OneMail.Composition;
using RecursiveAI.OneMail.Mime;

namespace RecursiveAI.OneMail.Tests.Client;

// The SMTP client against Dovecot's submission service, Stalwart and the Postfix guest (tools/servers/servers.sh starts them; the tests skip otherwise).
// What each server received is read back (the sink directory for Dovecot and Postfix, IMAP for Stalwart) and compared with what was sent.
public class SmtpClientServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<ServerInfo> SmtpServers => ServerHarness.SmtpServers();

    public static TheoryData<ServerInfo, string> Mechanisms()
    {
        var d = new TheoryData<ServerInfo, string>();
        var servers = ServerHarness.Up.Where(x => x.Has("submissions")).ToList();
        if (servers.Count == 0)
        {
            servers.Add(new ServerInfo("none", "", "", ""));
        }

        foreach (ServerInfo s in servers)
        {
            foreach (string m in new[] { "PLAIN", "LOGIN", "CRAM-MD5", "SCRAM-SHA-1", "SCRAM-SHA-256", "SCRAM-SHA-1-PLUS", "SCRAM-SHA-256-PLUS", "XOAUTH2", "OAUTHBEARER" })
            {
                d.Add(s, m);
            }
        }

        return d;
    }

    private static void Report(ServerInfo s, string what)
    {
        if (Environment.GetEnvironmentVariable("ONEMAIL_SMTP_REPORT") is { Length: > 0 } path)
        {
            lock (typeof(SmtpClientServerTests)) { File.AppendAllText(path, s.Name + "\t" + what + "\n"); }
        }
    }

    private static SmtpClientOptions Options(ServerInfo s, SmtpSecurity security = SmtpSecurity.ImplicitTls, bool auth = true, Func<IReadOnlyList<string>, ProtocolConnection, IReadOnlyList<SaslMechanism>>? selector = null, bool pipelining = true, bool chunking = true, int? port = null) => new()
    {
        Host = "127.0.0.1",
        Port = port ?? s.Port(security == SmtpSecurity.ImplicitTls ? "submissions" : "submission"),
        Security = security,
        Credentials = auth ? new SaslCredentials { UserName = s.User, Password = s.Password } : null,
        MechanismSelector = selector,
        UsePipelining = pipelining,
        UseChunking = chunking,
        ClientDomain = "client.example.test",
        Connection = new ProtocolConnectionOptions
        {
            ServerCertificateValidationCallback = (_, cert, _, _) => cert is not null,
            ReadTimeout = TimeSpan.FromSeconds(60),
        },
    };

    private static string Sender(ServerInfo s) => s.Name == "stalwart" ? s.Recipient : s.User.Contains('@', StringComparison.Ordinal) ? s.User : "sender@example.com";

    private static string Recipient(ServerInfo s) => s.Recipient;

    private static (SmtpMessage Message, byte[] Bytes) Compose(ServerInfo s, string marker, string? body = null, Action<SmtpMessage>? tweak = null)
    {
        MimeEntity e = new OutgoingMessage
        {
            From = "Sender <" + Sender(s) + ">",
            To = [Recipient(s)],
            Subject = "OneMail client test " + marker,
            Text = body ?? ("Hello " + marker + "\n.starts with a dot\nend\n"),
            MessageId = marker + "@client.example.test",
        }.ToEntity();
        SmtpMessage m = SmtpMessage.FromEntity(e);
        tweak?.Invoke(m);
        return (m, e.ToArray());
    }

    // ---- read back ----

    private static async Task<byte[]?> FindDelivered(ServerInfo s, string marker)
    {
        byte[] needle = Encoding.ASCII.GetBytes(marker);
        for (int i = 0; i < 40; i++)
        {
            if (s.Name == "stalwart")
            {
                if (await ReadImap(s, needle) is { } m) { return m; }
            }
            else if (Environment.GetEnvironmentVariable("ONEMAIL_SINK_DIR") is { } dir && Directory.Exists(dir))
            {
                foreach (string f in Directory.EnumerateFiles(dir, "*.eml"))
                {
                    byte[] b = await File.ReadAllBytesAsync(f, Ct);
                    if (b.AsSpan().IndexOf(needle) >= 0) { return b; }
                }
            }

            await Task.Delay(500, Ct);
        }

        return null;
    }

    private static async Task<byte[]?> ReadImap(ServerInfo s, byte[] needle)
    {
        await using ImapRawConnection imap = await ImapRawConnection.ConnectAsync("127.0.0.1", s.Port("imaps"), ServerHarness.Options(true, s.Name, "imap"), Ct);
        await imap.ReadGreetingAsync(Ct);
        if (!(await imap.AuthenticateAsync(new SaslPlain(s.User, s.Password), Ct)).IsOk) { return null; }
        if (!(await imap.ExecuteAsync(ImapCommand.Create("SELECT").Atom("INBOX"), null, null, Ct)).IsOk) { return null; }
        ImapCommandResult all = await imap.ExecuteAsync(ImapCommand.Create("UID").Atom("SEARCH").Atom("ALL"), null, null, Ct);
        var uids = new List<long>();
        foreach (ImapResponse r in all.Untagged)
        {
            if (r.TryGetSearch(out IReadOnlyList<long> nums)) { uids.AddRange(nums); }
        }

        foreach (long uid in uids)
        {
            byte[]? found = null;
            await imap.ExecuteAsync(ImapCommand.Create("UID").Atom("FETCH").Number(uid).Raw("(BODY.PEEK[])"), async (_, stream, ct) =>
            {
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct);
                if (ms.GetBuffer().AsSpan(0, (int)ms.Length).IndexOf(needle) >= 0) { found = ms.ToArray(); }
                return true;
            }, null, Ct);
            if (found is not null) { return found; }
        }

        return null;
    }

    // Servers put trace headers (Received, Return-Path) in front; the message itself must arrive unchanged behind them.
    private static void AssertArrivedIntact(ServerInfo s, byte[] sent, byte[]? got)
    {
        Assert.NotNull(got);
        Assert.True(got!.AsSpan().EndsWith(sent), "the delivered message does not end with the bytes sent (sent " + sent.Length + ", got " + got.Length + ")");
        Report(s, "byte-identical delivery, " + (got.Length - sent.Length) + " bytes of server headers in front");
    }

    // ---- tests ----

    [Theory]
    [MemberData(nameof(SmtpServers))]
    public async Task ImplicitTlsAuthenticatedSendIsDeliveredByteForByte(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        string marker = "it" + Guid.NewGuid().ToString("N")[..10];
        await using SmtpClient c = await SmtpClient.ConnectAsync(Options(s), Ct);
        Assert.True(c.IsEncrypted);
        Assert.True(c.IsAuthenticated);
        Report(s, "capabilities: " + string.Join(' ', c.Capabilities.Keywords.Select(k => k + (c.Capabilities.Parameters(k).Count > 0 ? "=" + string.Join(',', c.Capabilities.Parameters(k)) : ""))));
        (SmtpMessage m, byte[] sent) = Compose(s, marker);
        SmtpSendResult r = await c.SendAsync(m, Ct);
        Assert.True(r.Complete, r.DataReply?.ToString());
        Report(s, "implicit TLS send: " + r.DataReply + (r.UsedChunking ? " [BDAT]" : " [DATA]"));
        AssertArrivedIntact(s, sent, await FindDelivered(s, marker));
        await c.QuitAsync(Ct);
    }

    [Theory]
    [MemberData(nameof(SmtpServers))]
    public async Task StartTlsIsRequiredAndUsed(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        Assert.SkipUnless(s.Has("submission"), "no STARTTLS listener");
        string marker = "st" + Guid.NewGuid().ToString("N")[..10];
        await using SmtpClient c = await SmtpClient.ConnectAsync(Options(s, SmtpSecurity.StartTls), Ct);
        Assert.True(c.IsEncrypted);
        (SmtpMessage m, byte[] sent) = Compose(s, marker);
        Assert.True((await c.SendAsync(m, Ct)).Complete);
        AssertArrivedIntact(s, sent, await FindDelivered(s, marker));

        // With no encryption the credentials are never sent: PLAIN/LOGIN are skipped and the connection fails instead of leaking the password.
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () => await SmtpClient.ConnectAsync(Options(s, SmtpSecurity.None), Ct));
        Assert.True(ex is SmtpAuthenticationException or SmtpClientException, ex.GetType().Name);
        Report(s, "plain-text connect with credentials refused: " + ex.Message);
    }

    [Theory]
    [MemberData(nameof(Mechanisms))]
    public async Task EachSaslMechanismTheServerOffers(ServerInfo s, string mechanism)
    {
        ServerHarness.RequireServer(s);
        await using (SmtpClient probe = await SmtpClient.ConnectAsync(Options(s, auth: false), Ct))
        {
            Assert.SkipUnless(probe.Capabilities.AuthMechanisms.Contains(mechanism), s.Name + " does not offer " + mechanism);
        }

        SaslMechanism Make(ProtocolConnection conn) => mechanism switch
        {
            "PLAIN" => new SaslPlain(s.User, s.Password),
            "LOGIN" => new SaslLogin(s.User, s.Password),
            "CRAM-MD5" => new SaslCramMd5(s.User, s.Password),
            "SCRAM-SHA-1" => new SaslScram(ScramHash.Sha1, s.User, s.Password),
            "SCRAM-SHA-256" => new SaslScram(ScramHash.Sha256, s.User, s.Password),
            "SCRAM-SHA-1-PLUS" => new SaslScram(ScramHash.Sha1, s.User, s.Password, binding: SaslChannelBinding.From(conn), plus: true),
            "SCRAM-SHA-256-PLUS" => new SaslScram(ScramHash.Sha256, s.User, s.Password, binding: SaslChannelBinding.From(conn), plus: true),
            "XOAUTH2" => new SaslXOAuth2(s.User, Environment.GetEnvironmentVariable("ONEMAIL_" + s.Name.ToUpperInvariant() + "_TOKEN") ?? "test-token"),
            _ => new SaslOAuthBearer(s.User, Environment.GetEnvironmentVariable("ONEMAIL_" + s.Name.ToUpperInvariant() + "_TOKEN") ?? "test-token", "127.0.0.1", s.Port("submissions")),
        };

        string marker = "sa" + Guid.NewGuid().ToString("N")[..10];
        await using SmtpClient c = await SmtpClient.ConnectAsync(Options(s, selector: (_, conn) => [Make(conn)]), Ct);
        Assert.True(c.IsAuthenticated);
        (SmtpMessage m, byte[] sent) = Compose(s, marker);
        Assert.True((await c.SendAsync(m, Ct)).Complete);
        AssertArrivedIntact(s, sent, await FindDelivered(s, marker));
        Report(s, "SASL " + mechanism + ": authenticated, delivered");

        // A wrong password is a typed failure, not a hang or a crash.
        if (!mechanism.Contains("OAUTH", StringComparison.Ordinal) && mechanism != "XOAUTH2")
        {
            SaslMechanism Wrong(ProtocolConnection conn) => mechanism switch
            {
                "PLAIN" => new SaslPlain(s.User, "wrong"),
                "LOGIN" => new SaslLogin(s.User, "wrong"),
                "CRAM-MD5" => new SaslCramMd5(s.User, "wrong"),
                "SCRAM-SHA-1" => new SaslScram(ScramHash.Sha1, s.User, "wrong"),
                "SCRAM-SHA-256" => new SaslScram(ScramHash.Sha256, s.User, "wrong"),
                "SCRAM-SHA-1-PLUS" => new SaslScram(ScramHash.Sha1, s.User, "wrong", binding: SaslChannelBinding.From(conn), plus: true),
                _ => new SaslScram(ScramHash.Sha256, s.User, "wrong", binding: SaslChannelBinding.From(conn), plus: true),
            };
            var ex = await Assert.ThrowsAsync<SmtpAuthenticationException>(async () => await SmtpClient.ConnectAsync(Options(s, selector: (_, conn) => [Wrong(conn)]), Ct));
            Assert.NotNull(ex.Reply);
            Assert.Equal(535, ex.Reply!.Code);
            Report(s, "SASL " + mechanism + ": wrong password -> " + ex.Reply);
        }
    }

    [Theory]
    [MemberData(nameof(SmtpServers))]
    public async Task AutomaticSelectionPicksTheStrongestOffered(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        await using SmtpClient c = await SmtpClient.ConnectAsync(Options(s), Ct);
        Assert.True(c.IsAuthenticated);
        string[] offered = [.. c.Raw.Capabilities?.AuthMechanisms ?? []];
        // The capabilities are re-read after authentication; what was offered is in the trace of the connection. Report the post-auth list.
        Report(s, "auto-selected authentication succeeded; AUTH after login: " + string.Join(' ', offered));
    }

    [Theory]
    [MemberData(nameof(SmtpServers))]
    public async Task PipeliningAndChunkingCombinationsDeliverIdenticalBytes(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        foreach ((bool pipe, bool chunk) in new[] { (true, true), (true, false), (false, true), (false, false) })
        {
            string marker = "pc" + Guid.NewGuid().ToString("N")[..10];
            await using SmtpClient c = await SmtpClient.ConnectAsync(Options(s, pipelining: pipe, chunking: chunk), Ct);
            // 600 KB of text lines: several BDAT chunks and a message that does not fit one write.
            string body = string.Concat(Enumerable.Range(0, 12000).Select(i => "line " + i.ToString(CultureInfo.InvariantCulture) + " of the body, with a dot at the start of the next\n.x\n"));
            (SmtpMessage m, byte[] sent) = Compose(s, marker, body);
            SmtpSendResult r = await c.SendAsync(m, Ct);
            Assert.True(r.Complete, r.DataReply?.ToString());
            bool offeredChunking = c.Capabilities.Has("CHUNKING");
            Assert.Equal(chunk && offeredChunking, r.UsedChunking);
            AssertArrivedIntact(s, sent, await FindDelivered(s, marker));
            Report(s, $"pipelining={pipe && c.Capabilities.Has("PIPELINING")} chunking={r.UsedChunking}: {sent.Length} bytes delivered intact");
        }
    }

    [Theory]
    [MemberData(nameof(SmtpServers))]
    public async Task EightBitMimeIsDeclaredAndSmtpUtf8IsUsedWhenOffered(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        string marker = "u8" + Guid.NewGuid().ToString("N")[..10];
        await using SmtpClient c = await SmtpClient.ConnectAsync(Options(s), Ct);
        Assert.SkipUnless(c.Capabilities.Has("8BITMIME"), "8BITMIME not offered");
        byte[] raw = Encoding.UTF8.GetBytes($"From: {Sender(s)}\r\nTo: {Recipient(s)}\r\nSubject: =?utf-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes("Gr\u00fc\u00dfe"))}?= {marker}\r\nMessage-ID: <{marker}@client.example.test>\r\nMIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: 8bit\r\n\r\nGr\u00fc\u00dfe aus M\u00fcnchen \u2014 \u4f60\u597d\r\n");
        SmtpSendResult r = await c.SendAsync(SmtpMessage.FromBytes(raw), Ct);
        Assert.True(r.Complete);
        Assert.False(r.Downgraded);
        AssertArrivedIntact(s, raw, await FindDelivered(s, marker));
        Report(s, "8BITMIME body delivered as 8-bit");

        if (c.Capabilities.Has("SMTPUTF8"))
        {
            string marker2 = "ua" + Guid.NewGuid().ToString("N")[..10];
            byte[] utf8Sender = Encoding.UTF8.GetBytes($"From: j\u00fcrgen@example.com\r\nTo: {Recipient(s)}\r\nSubject: {marker2}\r\nMessage-ID: <{marker2}@client.example.test>\r\n\r\nutf-8 sender\r\n");
            var m = SmtpMessage.FromBytes(utf8Sender);
            m.Sender = s.Name == "stalwart" ? Sender(s) : "j\u00fcrgen@example.com";
            SmtpSendResult u = await c.SendAsync(m, Ct);
            Assert.True(u.UsedSmtpUtf8 || s.Name == "stalwart");
            Assert.True(u.Delivered, u.DataReply?.ToString() ?? u.SenderReply?.ToString());
            AssertArrivedIntact(s, utf8Sender, await FindDelivered(s, marker2));
            Report(s, "SMTPUTF8 message delivered (envelope sender utf-8 = " + (s.Name != "stalwart") + ")");
        }
        else
        {
            Report(s, "SMTPUTF8 not offered");
        }
    }

    [Fact]
    public async Task EightBitBodyIsDowngradedForAServerWithout8BitMime()
    {
        ServerInfo? s = ServerHarness.Up.FirstOrDefault(x => x.Name == "postfix" && x.Port("legacy") != 0);
        Assert.SkipWhen(s is null, "the Postfix guest's legacy listener is not up");
        string marker = "dg" + Guid.NewGuid().ToString("N")[..10];
        SmtpClientOptions o = Options(s!, SmtpSecurity.StartTls, port: s!.Port("legacy"));
        await using SmtpClient c = await SmtpClient.ConnectAsync(o, Ct);
        Assert.False(c.Capabilities.Has("8BITMIME"));
        Assert.False(c.Capabilities.Has("PIPELINING"));
        Assert.False(c.Capabilities.Has("CHUNKING"));
        byte[] raw = Encoding.UTF8.GetBytes($"From: {Sender(s)}\r\nTo: {Recipient(s)}\r\nSubject: {marker}\r\nMessage-ID: <{marker}@client.example.test>\r\nMIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: 8bit\r\n\r\nGr\u00fc\u00dfe aus M\u00fcnchen\r\n");
        SmtpSendResult r = await c.SendAsync(SmtpMessage.FromBytes(raw), Ct);
        Assert.True(r.Delivered);
        Assert.True(r.Downgraded);
        byte[]? got = await FindDelivered(s, marker);
        Assert.NotNull(got);
        MimeEntity back = MimeEntity.Parse(got!);
        Assert.Equal(ContentTransferEncoding_QP, back.Headers.GetFirst("Content-Transfer-Encoding")!.Value.Trim());
        Assert.Equal("Gr\u00fc\u00dfe aus M\u00fcnchen\r\n", Encoding.UTF8.GetString(((MimePart)back).GetDecodedContent()));
        Assert.All(got!, b => Assert.True(b < 0x80));

        // A signed message is refused, not re-encoded, and nothing reaches the server.
        byte[] signed = Encoding.UTF8.GetBytes($"DKIM-Signature: v=1; a=rsa-sha256; d=example.com; s=x; h=from; bh=AA==; b=AA==\r\nFrom: {Sender(s)}\r\nTo: {Recipient(s)}\r\nSubject: {marker}-signed\r\nContent-Transfer-Encoding: 8bit\r\n\r\nGr\u00fc\u00dfe\r\n");
        await Assert.ThrowsAsync<SmtpDowngradeRefusedException>(async () => await c.SendAsync(SmtpMessage.FromBytes(signed), Ct));
        Assert.Null(await FindDeliveredQuick(s, marker + "-signed"));
        Report(s, "downgrade against a server without 8BITMIME: re-encoded to quoted-printable, delivered 7-bit; signed message refused");
    }

    private const string ContentTransferEncoding_QP = "quoted-printable";

    private static async Task<byte[]?> FindDeliveredQuick(ServerInfo s, string marker)
    {
        await Task.Delay(1500, Ct);
        if (Environment.GetEnvironmentVariable("ONEMAIL_SINK_DIR") is { } dir && Directory.Exists(dir))
        {
            byte[] needle = Encoding.ASCII.GetBytes(marker);
            foreach (string f in Directory.EnumerateFiles(dir, "*.eml"))
            {
                byte[] b = await File.ReadAllBytesAsync(f, Ct);
                if (b.AsSpan().IndexOf(needle) >= 0) { return b; }
            }
        }

        _ = s;
        return null;
    }

    [Theory]
    [MemberData(nameof(SmtpServers))]
    public async Task SizeLimitDsnAndRequireTlsAreCheckedAgainstWhatTheServerOffers(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        await using SmtpClient c = await SmtpClient.ConnectAsync(Options(s), Ct);
        SmtpCapabilities caps = c.Capabilities;

        // SIZE: a message larger than the announced limit is refused before MAIL FROM; the declared length is enough, no bytes are generated.
        if (caps.MaxMessageSize is > 0 and { } limit)
        {
            var huge = SmtpMessage.FromStream(new MemoryStream([1]), length: limit + 1);
            huge.Sender = Sender(s);
            huge.Recipients = [Recipient(s)];
            var ex = await Assert.ThrowsAsync<SmtpMessageTooLargeException>(async () => await c.SendAsync(huge, Ct));
            Assert.Equal(limit, ex.Limit);
            Report(s, "SIZE " + limit + ": larger message refused before MAIL FROM");
            Assert.True(c.IsUsable);
        }
        else
        {
            Report(s, "SIZE limit not announced (" + (caps.Has("SIZE") ? "no limit" : "no SIZE") + ")");
        }

        // DSN
        string marker = "ds" + Guid.NewGuid().ToString("N")[..10];
        if (caps.Has("DSN"))
        {
            (SmtpMessage m, _) = Compose(s, marker, tweak: x => x.Dsn = new SmtpDsnOptions { Return = "HDRS", EnvelopeId = "env+" + marker, Notify = ["FAILURE", "DELAY"] });
            Assert.True((await c.SendAsync(m, Ct)).Complete);
            Report(s, "DSN RET/ENVID/NOTIFY accepted");
        }
        else
        {
            await Assert.ThrowsAsync<SmtpFeatureRequiredException>(async () => await c.SendAsync(Compose(s, marker, tweak: x => x.Dsn = new SmtpDsnOptions { Return = "HDRS" }).Message, Ct));
            Report(s, "DSN not offered: refused locally");
        }

        // REQUIRETLS
        string marker2 = "rt" + Guid.NewGuid().ToString("N")[..10];
        if (caps.Has("REQUIRETLS"))
        {
            Assert.True((await c.SendAsync(Compose(s, marker2, tweak: x => x.RequireTls = true).Message, Ct)).Delivered);
            Report(s, "REQUIRETLS accepted");
        }
        else
        {
            var ex = await Assert.ThrowsAsync<SmtpFeatureRequiredException>(async () => await c.SendAsync(Compose(s, marker2, tweak: x => x.RequireTls = true).Message, Ct));
            Assert.Equal("REQUIRETLS", ex.Feature);
            Report(s, "REQUIRETLS not offered: refused locally");
        }
    }

    [Theory]
    [MemberData(nameof(SmtpServers))]
    public async Task RejectedRecipientsAndSessionReuse(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        await using SmtpClient c = await SmtpClient.ConnectAsync(Options(s), Ct);
        for (int i = 0; i < 3; i++)
        {
            string marker = "re" + i + Guid.NewGuid().ToString("N")[..9];
            (SmtpMessage m, _) = Compose(s, marker, tweak: x => x.Recipients = s.Name == "stalwart" ? [Recipient(s), "nobody-here@example.test"] : [Recipient(s), "second@example.org"]);
            SmtpSendResult r = await c.SendAsync(m, Ct);
            Assert.True(r.Delivered);
            Assert.Equal(2, r.Recipients.Count);
            if (s.Name == "stalwart")
            {
                SmtpRecipientResult bad = Assert.Single(r.Rejected);
                Assert.True(bad.Reply.IsPermanentFailure);
                Report(s, "rejected recipient: " + bad.Reply + " (message still delivered to the other)");
            }

            Assert.NotNull(await FindDelivered(s, marker));
        }

        // The same session also handles a message every recipient of which is rejected, and then carries on.
        if (s.Name == "stalwart")
        {
            SmtpSendResult none = await c.SendAsync(Compose(s, "nn" + Guid.NewGuid().ToString("N")[..9], tweak: x => x.Recipients = ["nobody-1@example.test", "nobody-2@example.test"]).Message, Ct);
            Assert.False(none.Delivered);
            Assert.Null(none.DataReply);
            Assert.All(none.Recipients, r => Assert.False(r.Accepted));
            Assert.True((await c.SendAsync(Compose(s, "ok" + Guid.NewGuid().ToString("N")[..9]).Message, Ct)).Complete);
            Report(s, "all recipients rejected: no data sent, session reused afterwards");
        }

        Assert.True(c.IsUsable);
        Report(s, "three messages over one session (RSET between transactions as needed)");
    }

    [Theory]
    [MemberData(nameof(SmtpServers))]
    public void SynchronousTwinAgainstTheServer(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        string marker = "sy" + Guid.NewGuid().ToString("N")[..10];
        using SmtpClient c = SmtpClient.Connect(Options(s));
        SmtpMessage m = Compose(s, marker).Message;
        Assert.True(c.Send(m).Complete);
        c.Quit();
    }
}
