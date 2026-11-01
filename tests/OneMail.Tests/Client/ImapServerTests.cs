using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneMail.Client.Imap.Protocol;
using RecursiveAI.OneMail.Client.Protocol;
using RecursiveAI.OneMail.Client.Sasl;
using RecursiveAI.OneMail.Mime.Reading;

namespace RecursiveAI.OneMail.Tests.Client;

// Runs against the published Dovecot, Stalwart and Cyrus images when tools/servers/servers.sh started them; skips otherwise.
public class ImapServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<ImapRawConnection> ConnectAsync(ServerInfo s, bool implicitTls)
    {
        ImapRawConnection imap = await ImapRawConnection.ConnectAsync("127.0.0.1", s.Port(implicitTls ? "imaps" : "imap"), ServerHarness.Options(implicitTls, s.Name, "imap"), Ct);
        ImapResponse greeting = await imap.ReadGreetingAsync(Ct);
        Assert.Equal(ImapStatus.Ok, greeting.Status);
        if (!imap.HasCapability("IMAP4rev1") && !imap.HasCapability("IMAP4rev2"))
        {
            ImapCommandResult caps = await imap.ExecuteAsync(ImapCommand.Create("CAPABILITY"), null, null, Ct);
            Assert.True(caps.IsOk);
        }

        Assert.True(imap.HasCapability("IMAP4rev1") || imap.HasCapability("IMAP4rev2"));
        return imap;
    }

    private static async Task LoginPlainAsync(ImapRawConnection imap, ServerInfo s)
    {
        ImapCommandResult r = await imap.AuthenticateAsync(new SaslPlain(s.User, s.Password), Ct);
        Assert.True(r.IsOk, r.Completion.FirstLine);
        // Capabilities are forgotten at login unless the server sent them in the OK.
        if (imap.Capabilities.Count == 0)
        {
            Assert.True((await imap.ExecuteAsync(ImapCommand.Create("CAPABILITY"), null, null, Ct)).IsOk);
        }
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task ImplicitTlsConnectCapabilityAndPlainLogin(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        await using ImapRawConnection imap = await ConnectAsync(s, implicitTls: true);
        Assert.True(imap.Connection.IsEncrypted);
        Assert.True(imap.HasCapability("AUTH=PLAIN"), string.Join(' ', imap.Capabilities));
        await LoginPlainAsync(imap, s);
        Assert.True((await imap.ExecuteAsync(ImapCommand.Create("NOOP"), null, null, Ct)).IsOk);
        Assert.True((await imap.ExecuteAsync(ImapCommand.Create("LOGOUT"), null, null, Ct)).IsOk);
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task StartTlsThenPlainLoginAndLoginCommandRefusedBeforeTls(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        Assert.SkipUnless(s.Has("imap"), "no STARTTLS listener");
        await using ImapRawConnection imap = await ConnectAsync(s, implicitTls: false);
        Assert.True(imap.HasCapability("STARTTLS"), string.Join(' ', imap.Capabilities));
        // Credentials are never sent in the clear by default.
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await imap.AuthenticateAsync(new SaslPlain(s.User, s.Password), Ct));
        ImapCommandResult tls = await imap.StartTlsAsync(Ct);
        Assert.True(tls.IsOk);
        Assert.True(imap.Connection.IsEncrypted);
        Assert.Empty(imap.Capabilities);
        Assert.True((await imap.ExecuteAsync(ImapCommand.Create("CAPABILITY"), null, null, Ct)).IsOk);
        Assert.True(imap.HasCapability("AUTH=PLAIN"));
        await LoginPlainAsync(imap, s);
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task RawCommandScriptAppendFetchStreamSearchIdleCompress(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        await using ImapRawConnection imap = await ConnectAsync(s, implicitTls: true);
        await LoginPlainAsync(imap, s);
        string mailbox = "OneMail raw " + Guid.NewGuid().ToString("N")[..8];
        byte[] message = ServerHarness.SampleMessage(Guid.NewGuid().ToString("N")[..12]);

        // CREATE / LIST with a mailbox name that needs modified UTF-7.
        string utf7 = mailbox + " Entwürfe";
        Assert.True((await imap.ExecuteAsync(ImapCommand.Create("CREATE").Mailbox(utf7), null, null, Ct)).IsOk);
        ImapCommandResult list = await imap.ExecuteAsync(ImapCommand.Create("LIST").Quoted("").Mailbox(utf7), null, null, Ct);
        Assert.True(list.IsOk);
        ImapListEntry? entry = null;
        foreach (ImapResponse r in list.Untagged)
        {
            if (r.TryGetList(out ImapListEntry e)) { entry = e; }
        }

        Assert.Equal(utf7, entry?.Name);

        // APPEND with a literal (synchronising, or LITERAL+ when the server advertises it).
        ImapCommandResult append = await imap.ExecuteAsync(ImapCommand.Create("APPEND").Mailbox(utf7).Flags(["\\Seen"]).Literal(message), null, null, Ct);
        Assert.True(append.IsOk, append.Completion.FirstLine);

        ImapCommandResult select = await imap.ExecuteAsync(ImapCommand.Create("SELECT").Mailbox(utf7), null, null, Ct);
        Assert.True(select.IsOk);
        Assert.Contains(select.Untagged, r => r.Is("EXISTS") && r.Number == 1);

        // FETCH ENVELOPE and BODYSTRUCTURE, typed.
        ImapCommandResult env = await imap.ExecuteAsync(ImapCommand.Create("FETCH").SequenceSet("1").Raw("(UID ENVELOPE BODYSTRUCTURE RFC822.SIZE FLAGS)"), null, null, Ct);
        Assert.True(env.IsOk);
        ImapFetchData? fetched = null;
        foreach (ImapResponse r in env.Untagged)
        {
            if (r.TryGetFetch(out ImapFetchData f)) { fetched = f; }
        }

        Assert.NotNull(fetched);
        Assert.Equal(message.Length, fetched!.Rfc822Size);
        Assert.Contains("\\Seen", fetched.Flags!);
        Assert.Equal("ann", fetched.Envelope!.From[0].Mailbox);
        Assert.StartsWith("=?UTF-8?B?", fetched.Envelope.Subject, StringComparison.OrdinalIgnoreCase);
        Assert.True(fetched.BodyStructure!.IsMultipart);
        Assert.Equal(["text/plain", "application/octet-stream"], fetched.BodyStructure.Parts!.Select(p => p.MediaType));
        long uid = fetched.Uid!.Value;

        // FETCH BODY.PEEK[] streamed into the MIME reader, hashed on the way: the bytes are exactly what was appended.
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int mimeEvents = 0;
        int parts = 0;
        ImapCommandResult body = await imap.ExecuteAsync(ImapCommand.Create("UID").Atom("FETCH").Number(uid).Raw("(BODY.PEEK[])"), async (ctx, stream, ct) =>
        {
            Assert.Equal(message.Length, ctx.Length);
            using var tee = new HashingStream(stream, sha);
            using var mime = new MimeReader(tee);
            while (await mime.ReadAsync(ct))
            {
                mimeEvents++;
                if (mime.Kind == MimeEventKind.EntityStart) { parts++; }
            }

            return true;
        }, null, Ct);
        Assert.True(body.IsOk);
        Assert.Equal(SHA256.HashData(message), sha.GetHashAndReset());
        Assert.Equal(3, parts);
        Assert.True(mimeEvents > 10);

        // SEARCH and ESEARCH.
        ImapCommandResult search = await imap.ExecuteAsync(ImapCommand.Create("UID").Atom("SEARCH").Atom("HEADER").Quoted("Subject").Quoted("Birnen"), null, null, Ct);
        Assert.True(search.IsOk);
        long hit = 0;
        foreach (ImapResponse r in search.Untagged)
        {
            if (r.TryGetSearch(out IReadOnlyList<long> nums) && nums.Count > 0) { hit = nums[0]; }
        }

        if (hit == 0)
        {
            // Servers that match the raw header text do not decode encoded words: search for the Message-ID instead.
            ImapCommandResult byId = await imap.ExecuteAsync(ImapCommand.Create("UID").Atom("SEARCH").Atom("ALL"), null, null, Ct);
            foreach (ImapResponse r in byId.Untagged)
            {
                if (r.TryGetSearch(out IReadOnlyList<long> nums) && nums.Count > 0) { hit = nums[0]; }
            }
        }

        Assert.Equal(uid, hit);
        if (imap.HasCapability("ESEARCH") || imap.HasCapability("IMAP4rev2"))
        {
            ImapCommandResult es = await imap.ExecuteAsync(ImapCommand.Create("UID").Atom("SEARCH").Atom("RETURN").Open().Atom("ALL").Atom("COUNT").Close().Atom("ALL"), null, null, Ct);
            Assert.True(es.IsOk);
            ImapEsearchData? data = null;
            foreach (ImapResponse r in es.Untagged)
            {
                if (r.TryGetEsearch(out ImapEsearchData e)) { data = e; }
            }

            Assert.NotNull(data);
            Assert.True(data!.IsUid);
            Assert.Equal("1", data.Get("COUNT")?.Text);
            Assert.Equal([uid], ImapSequenceSet.Expand(data.Get("ALL")!.Text!));
        }

        // STATUS.
        ImapCommandResult status = await imap.ExecuteAsync(ImapCommand.Create("STATUS").Mailbox(utf7).Raw("(MESSAGES UIDNEXT)"), null, null, Ct);
        Assert.True(status.IsOk);
        Assert.Contains(status.Untagged, r => r.TryGetStatus(out ImapStatusData st) && st.Items["MESSAGES"] == 1 && st.Mailbox == utf7);

        // COMPRESS=DEFLATE: after it, commands and a streamed FETCH still work.
        if (imap.HasCapability("COMPRESS=DEFLATE"))
        {
            Assert.True((await imap.CompressAsync(Ct)).IsOk);
            Assert.True(imap.Connection.IsCompressed);
            Assert.True((await imap.ExecuteAsync(ImapCommand.Create("NOOP"), null, null, Ct)).IsOk);
            long compressedBytes = 0;
            ImapCommandResult again = await imap.ExecuteAsync(ImapCommand.Create("UID").Atom("FETCH").Number(uid).Raw("(BODY.PEEK[1])"), async (_, stream, ct) =>
            {
                var buf = new byte[1024];
                int n;
                while ((n = await stream.ReadAsync(buf, ct)) > 0) { compressedBytes += n; }
                return true;
            }, null, Ct);
            Assert.True(again.IsOk);
            Assert.True(compressedBytes > 20);
        }

        // IDLE: start, see the continuation, stop.
        if (imap.HasCapability("IDLE"))
        {
            string? tag = await imap.IdleStartAsync(Ct);
            Assert.NotNull(tag);
            ImapCommandResult done = await imap.IdleDoneAsync(tag!, Ct);
            Assert.True(done.IsOk, done.Completion.FirstLine);
        }

        // ENABLE UTF8=ACCEPT, where offered: mailbox names then go out as UTF-8.
        if (imap.HasCapability("UTF8=ACCEPT") && !imap.HasCapability("IMAP4rev2"))
        {
            // (IMAP4rev2 servers enable UTF8 with the revision; UTF8=ACCEPT alone is the RFC 6855 path.)
            ImapCommandResult enable = await imap.ExecuteAsync(ImapCommand.Create("ENABLE").Atom("UTF8=ACCEPT"), null, null, Ct);
            Assert.True(enable.IsOk);
        }

        Assert.True((await imap.ExecuteAsync(ImapCommand.Create("CLOSE"), null, null, Ct)).IsOk);
        Assert.True((await imap.ExecuteAsync(ImapCommand.Create("DELETE").Mailbox(utf7), null, null, Ct)).IsOk);
        Assert.True((await imap.ExecuteAsync(ImapCommand.Create("LOGOUT"), null, null, Ct)).IsOk);
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task TaggedNoAndBadAreOrdinaryResponses(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        await using ImapRawConnection imap = await ConnectAsync(s, implicitTls: true);
        await LoginPlainAsync(imap, s);
        ImapCommandResult no = await imap.ExecuteAsync(ImapCommand.Create("SELECT").Mailbox("Does not exist " + Guid.NewGuid().ToString("N")), null, null, Ct);
        Assert.Equal(ImapStatus.No, no.Completion.Status);
        ImapCommandResult bad = await imap.ExecuteAsync(ImapCommand.Create("XNOSUCHCOMMAND"), null, null, Ct);
        Assert.Equal(ImapStatus.Bad, bad.Completion.Status);
        // The session is still usable.
        Assert.True((await imap.ExecuteAsync(ImapCommand.Create("NOOP"), null, null, Ct)).IsOk);
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public void SynchronousTwinsWork(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        using ImapRawConnection imap = ImapRawConnection.Connect("127.0.0.1", s.Port("imaps"), ServerHarness.Options(true, s.Name, "imap-sync"));
        Assert.Equal(ImapStatus.Ok, imap.ReadGreeting().Status);
        Assert.True(imap.Authenticate(new SaslPlain(s.User, s.Password)).IsOk);
        ImapCommandResult sel = imap.Execute(ImapCommand.Create("SELECT").Atom("INBOX"));
        Assert.True(sel.IsOk);
        Assert.True(imap.Execute(ImapCommand.Create("LOGOUT")).IsOk);
    }

    public static TheoryData<ServerInfo> Servers => ServerHarness.ImapServers();

    private sealed class HashingStream(Stream inner, IncrementalHash hash) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int n = inner.Read(buffer);
            hash.AppendData(buffer[..n]);
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int n = await inner.ReadAsync(buffer, cancellationToken);
            hash.AppendData(buffer.Span[..n]);
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
