using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneMail.Client.Imap;
using RecursiveAI.OneMail.Client.Imap.Protocol;
using RecursiveAI.OneMail.Mime;

namespace RecursiveAI.OneMail.Tests.Client;

// The IMAP client against the published Dovecot, Stalwart and Cyrus images (tools/servers/servers.sh). Each test skips, naming the
// capability, when a server does not offer what it exercises. Without ONEMAIL_SERVERS everything skips.
public class ImapClientServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<ServerInfo> Servers() => ServerHarness.ImapServers();

    internal static async Task<ImapClient> ConnectAsync(ServerInfo s, bool implicitTls = true, bool utf8 = false, bool compress = false)
    {
        var options = new ImapClientOptions { Connection = ServerHarness.Options(implicitTls, s.Name, "imap"), Utf8 = utf8, Compress = compress };
        ImapClient c = await ImapClient.ConnectAsync("127.0.0.1", s.Port(implicitTls ? "imaps" : "imap"), options, Ct);
        await c.LoginAsync(s.User, s.Password, Ct);
        return c;
    }

    // A connection whose protocol trace is kept, so a failing assertion can show what was on the wire.
    internal static async Task<ImapClient> ConnectTracedAsync(ServerInfo s, StringWriter log)
    {
        var options = new ImapClientOptions { Connection = new RecursiveAI.OneMail.Client.Protocol.ProtocolConnectionOptions { ImplicitTls = true, ServerCertificateValidationCallback = (_, cert, _, _) => cert is not null, ReadTimeout = TimeSpan.FromSeconds(30), Trace = new RecursiveAI.OneMail.Client.Protocol.TextWriterProtocolTrace(log) } };
        ImapClient c = await ImapClient.ConnectAsync("127.0.0.1", s.Port("imaps"), options, Ct);
        await c.LoginAsync(s.User, s.Password, Ct);
        return c;
    }

    internal static string NewName(string stem = "u47") => stem + "-" + Guid.NewGuid().ToString("N")[..8];

    internal static async Task<string> NewMailboxAsync(ImapClient c, string? name = null)
    {
        name ??= NewName();
        await c.CreateAsync(name, Ct);
        return name;
    }

    internal static async Task DropAsync(ImapClient c, string name)
    {
        try
        {
            if (c.Mailbox is not null)
            {
                await c.CloseMailboxAsync(expunge: true, Ct);
            }

            await c.DeleteAsync(name, Ct);
        }
        catch (Exception e) when (e is ImapCommandException or IOException)
        {
            // Cleanup only; the container is thrown away after the run.
        }
    }

    private static byte[] Message(string marker, string from = "ann@example.com", string subject = "Hello", string? inReplyTo = null, string date = "Tue, 03 Nov 2026 10:00:00 +0000") =>
        Encoding.UTF8.GetBytes(
            $"From: {from}\r\nTo: bob@example.com\r\nSubject: {subject} {marker}\r\nDate: {date}\r\nMessage-ID: <{marker}@example.com>\r\n" +
            (inReplyTo is null ? "" : $"In-Reply-To: <{inReplyTo}@example.com>\r\nReferences: <{inReplyTo}@example.com>\r\n") +
            "MIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nbody of " + marker + "\r\n");

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task ConnectStartTlsLoginIdNamespace(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        Assert.SkipUnless(s.Has("imap"), "no STARTTLS listener");
        await using ImapClient c = await ConnectAsync(s, implicitTls: false);
        Assert.True(c.IsEncrypted, "Auto STARTTLS should have upgraded the connection");
        Assert.True(c.IsAuthenticated);
        Assert.True(c.HasCapability("IMAP4rev1") || c.HasCapability("IMAP4rev2"));
        if (Environment.GetEnvironmentVariable("ONEMAIL_IMAP_REPORT") is { } report)
        {
            File.AppendAllText(report, $"[{s.Name}] IMAP capabilities: {string.Join(' ', c.Capabilities.Order(StringComparer.Ordinal))}\n");
        }

        if (c.HasCapability("ID"))
        {
            IReadOnlyDictionary<string, string> id = await c.IdAsync(new Dictionary<string, string> { ["name"] = "onemail-tests" }, Ct);
            Assert.NotNull(id);
        }

        if (c.HasCapability("NAMESPACE"))
        {
            ImapNamespaces ns = await c.NamespaceAsync(Ct);
            Assert.NotEmpty(ns.Personal);
        }

        await c.LogoutAsync(Ct);
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task BadPasswordIsAnImapCommandException(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        Assert.SkipWhen(s.Name == "cyrus", "this Cyrus image accepts any password");
        var options = new ImapClientOptions { Connection = ServerHarness.Options(true, s.Name, "imap") };
        await using ImapClient c = await ImapClient.ConnectAsync("127.0.0.1", s.Port("imaps"), options, Ct);
        ImapCommandException e = await Assert.ThrowsAsync<ImapCommandException>(async () => await c.LoginAsync(s.User, "wrong", Ct));
        Assert.False(c.IsAuthenticated);
        Assert.NotEmpty(e.Message);
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task MailboxLifecycleListStatusNonAsciiNames(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        await using ImapClient c = await ConnectAsync(s);
        string name = NewName("u47-Ünï-☃");
        string renamed = NewName("u47-Ünï-ß");
        await c.CreateAsync(name, Ct);
        try
        {
            IReadOnlyList<ImapMailboxInfo> all = await c.ListAsync(options: new ImapListOptions { Status = ImapStatusItems.Messages }, cancellationToken: Ct);
            ImapMailboxInfo mine = Assert.Single(all, m => m.Name == name);
            Assert.Contains(all, m => m.Name.Equals("INBOX", StringComparison.OrdinalIgnoreCase));
            if (c.HasCapability("LIST-STATUS"))
            {
                Assert.Equal(0, mine.Status?.Messages);
            }

            ImapMailboxStatus st = await c.StatusAsync(name, ImapStatusItems.Standard, Ct);
            Assert.Equal(0, st.Messages);
            Assert.True(st.UidValidity > 0);

            await c.RenameAsync(name, renamed, Ct);
            await c.SubscribeAsync(renamed, Ct);
            IReadOnlyList<ImapMailboxInfo> subscribed = await c.ListAsync(options: new ImapListOptions { SubscribedOnly = true }, cancellationToken: Ct);
            Assert.Contains(subscribed, m => m.Name == renamed);
            await c.UnsubscribeAsync(renamed, Ct);
            Assert.DoesNotContain(await c.ListAsync(options: new ImapListOptions { SubscribedOnly = true }, cancellationToken: Ct), m => m.Name == renamed);
            await c.DeleteAsync(renamed, Ct);
            Assert.DoesNotContain(await c.ListAsync(cancellationToken: Ct), m => m.Name == renamed);
        }
        finally
        {
            await DropAsync(c, name);
            await DropAsync(c, renamed);
        }
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task SpecialUseIsReported(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        await using ImapClient c = await ConnectAsync(s);
        Assert.SkipUnless(c.HasCapability("SPECIAL-USE"), s.Name + " does not offer SPECIAL-USE");
        IReadOnlyList<ImapMailboxInfo> all = await c.ListAsync(cancellationToken: Ct);
        // Not every account has special-use folders configured; the listing must at least be well formed.
        Assert.All(all, m => Assert.Equal(m.SpecialUse != ImapSpecialUse.None, m.Attributes.Any(a => a.Equals("\\" + m.SpecialUse, StringComparison.OrdinalIgnoreCase))));
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task AppendFetchRoundTripEnvelopeBodyStructureSections(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        await using ImapClient c = await ConnectAsync(s);
        string mb = await NewMailboxAsync(c);
        try
        {
            byte[] msg = ServerHarness.SampleMessage("rt" + Guid.NewGuid().ToString("N")[..6]);
            var date = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(2));
            ImapAppendResult added = await c.AppendAsync(mb, msg, ["\\Flagged"], date, Ct);
            ImapMailbox box = await c.SelectAsync(mb, cancellationToken: Ct);
            Assert.Equal(1, box.Exists);
            Assert.True(box.UidValidity > 0);
            if (added.UidValidity != 0)
            {
                Assert.Equal(box.UidValidity, added.UidValidity);
            }

            var query = new ImapFetchQuery
            {
                Uid = true, Flags = true, InternalDate = true, Size = true, Envelope = true, BodyStructure = true, Source = true,
                HeaderFields = ["Subject", "Message-ID"], Sections = [new ImapSectionRequest("1"), new ImapSectionRequest("2.MIME")],
            };
            var got = new List<ImapMessage>();
            await foreach (ImapMessage m in c.FetchAsync(ImapSet.All, query, uid: false, Ct))
            {
                got.Add(m);
            }

            ImapMessage one = Assert.Single(got);
            Assert.Equal(1, one.SequenceNumber);
            Assert.NotNull(one.Uid);
            if (added.Uids.Count == 1)
            {
                Assert.Equal(added.Uids[0], one.Uid);
            }

            Assert.Contains("\\Flagged", one.Flags!, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(msg.Length, one.Size);
            Assert.Equal(date.ToUniversalTime(), one.InternalDate?.ToUniversalTime());
            Assert.True(msg.AsSpan().SequenceEqual(one.Source.Span), "BODY[] is not byte-identical");
            Assert.Contains("Message-ID", Encoding.UTF8.GetString(one.GetHeaderFields().Span), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Hello from the container tests.\r\n.A line that starts with a period.\r\n", Encoding.UTF8.GetString(one.GetSection("1").Span));
            Assert.Contains("application/octet-stream", Encoding.UTF8.GetString(one.GetSection("2.MIME").Span));

            // RFC 9051 §7.5.2 sends the Subject as in the header (encoded words undecoded); Stalwart sends it decoded.
            Assert.Matches(@"(?i)^(=\?utf-8\?B\?|Äpfel und Birnen)", one.Envelope!.Subject);
            Assert.StartsWith("<rt", one.Envelope.MessageId);
            ImapAddress from = Assert.Single(one.Envelope.From);
            Assert.Equal("ann", from.Mailbox);
            Assert.Equal("example.com", from.Host);
            Assert.Equal("Ann", from.Name);
            ImapBodyStructure bs = one.BodyStructure!;
            Assert.True(bs.IsMultipart);
            Assert.Equal("multipart/mixed", bs.MediaType.ToLowerInvariant());
            Assert.Equal(2, bs.Parts!.Count);
            Assert.Equal("text/plain", bs.Parts[0].MediaType.ToLowerInvariant());
            Assert.Equal("1", bs.Parts[0].Section);
            Assert.Equal("attachment", bs.Parts[1].DispositionType?.ToLowerInvariant());

            // Partial fetch and streaming download of the same bytes.
            using var ms = new MemoryStream();
            Assert.Equal(msg.Length, await c.DownloadAsync(one.Uid!.Value, "", ms, cancellationToken: Ct));
            Assert.True(msg.AsSpan().SequenceEqual(ms.ToArray()));
            using var part = new MemoryStream();
            Assert.Equal(10, await c.DownloadAsync(one.Uid.Value, "", part, offset: 5, count: 10, cancellationToken: Ct));
            Assert.True(msg.AsSpan(5, 10).SequenceEqual(part.ToArray()));
            Assert.Equal(-1, await c.DownloadAsync(one.Uid.Value + 999, "", Stream.Null, cancellationToken: Ct));

            // The literal goes straight into the MIME model.
            MimeEntity tree = (await c.GetMessageAsync(one.Uid.Value, cancellationToken: Ct))!;
            Assert.Equal("multipart/mixed", tree.MediaType.ToLowerInvariant());
            Assert.Equal(2, ((MimeMultipart)tree).Children.Count);
            Assert.True(msg.AsSpan().SequenceEqual(tree.ToArray()));
        }
        finally
        {
            await DropAsync(c, mb);
        }
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task BinaryFetchDecodesTransferEncoding(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        await using ImapClient c = await ConnectAsync(s);
        Assert.SkipUnless(c.HasCapability("BINARY"), s.Name + " does not offer BINARY");
        string mb = await NewMailboxAsync(c);
        try
        {
            byte[] msg = ServerHarness.SampleMessage("bin");
            ImapAppendResult added = await c.AppendAsync(mb, msg, cancellationToken: Ct);
            await c.SelectAsync(mb, cancellationToken: Ct);
            long uid = (await c.SearchAsync(ImapSearch.All, uid: true, cancellationToken: Ct)).Numbers[0];
            using var ms = new MemoryStream();
            await c.DownloadBinaryAsync(uid, "2", ms, Ct);
            Assert.Equal(SHA256.HashData(Encoding.ASCII.GetBytes("bin")), ms.ToArray());
        }
        finally
        {
            await DropAsync(c, mb);
        }
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task SearchEsearchSortAndThread(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        var log = new StringWriter();
        await using ImapClient c = await ConnectTracedAsync(s, log);
        string mb = await NewMailboxAsync(c);
        try
        {
            await c.AppendAsync(mb, Message("m1", "zed@example.com", "Charlie"), ["\\Seen"], cancellationToken: Ct);
            await c.AppendAsync(mb, Message("m2", "amy@example.com", "Alpha", "m1", "Wed, 04 Nov 2026 10:00:00 +0000"), cancellationToken: Ct);
            await c.AppendAsync(mb, Message("m3", "bob@example.com", "Bravo =?UTF-8?Q?=C3=BCn=C3=AF?=", date: "Thu, 05 Nov 2026 10:00:00 +0000"), cancellationToken: Ct);
            await c.SelectAsync(mb, cancellationToken: Ct);
            // Stalwart indexes headers after APPEND returns, so a header search right away can come back empty: ask again for a little while.
            async Task Expect(long[] expected, ImapSearch q)
            {
                for (int attempt = 0; ; attempt++)
                {
                    IReadOnlyList<long> got = (await c.SearchAsync(q, cancellationToken: Ct)).Numbers;
                    if (expected.SequenceEqual(got) || attempt >= 25)
                    {
                        Assert.True(expected.SequenceEqual(got), "search expected " + string.Join(",", expected) + " got " + string.Join(",", got) + "\n" + log);
                        return;
                    }

                    await Task.Delay(200, Ct);
                }
            }

            await Expect([1L, 2L, 3L], ImapSearch.All);
            await Expect([2L, 3L], ImapSearch.Unseen);
            await Expect([2L], ImapSearch.From("amy").And(ImapSearch.Unseen));
            await Expect([1L, 3L], ImapSearch.From("zed").Or(ImapSearch.Subject("Bravo")));
            await Expect([3L], ImapSearch.Subject("ünï"));
            await Expect([1L, 2L], ImapSearch.Subject("Bravo").Not());
            await Expect([1L, 3L], ImapSearch.Header("Subject", "Charlie").Or(ImapSearch.Body("body of m3")));

            ImapSearchResult e = await c.SearchAsync(ImapSearch.All, returns: ImapSearchReturn.Min | ImapSearchReturn.Max | ImapSearchReturn.Count | ImapSearchReturn.All, cancellationToken: Ct);
            Assert.Equal(1, e.Min);
            Assert.Equal(3, e.Max);
            Assert.Equal(3, e.Count);
            Assert.Equal([1L, 2L, 3L], e.Numbers);

            ImapSearchResult byUid = await c.SearchAsync(ImapSearch.Unseen, uid: true, cancellationToken: Ct);
            Assert.True(byUid.IsUid);
            Assert.Equal(2, byUid.Numbers.Count);

            if (c.HasCapability("SORT"))
            {
                Assert.Equal([2L, 3L, 1L], await c.SortAsync([new ImapSortCriterion(ImapSortKey.Subject)], ImapSearch.All, cancellationToken: Ct));
                Assert.Equal([3L, 2L, 1L], await c.SortAsync([new ImapSortCriterion(ImapSortKey.Date, Reverse: true)], ImapSearch.All, cancellationToken: Ct));
            }
        }
        finally
        {
            await DropAsync(c, mb);
        }
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task ThreadByReferencesGroupsReplies(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        await using ImapClient c = await ConnectAsync(s);
        Assert.SkipUnless(c.HasCapability("THREAD=REFERENCES"), s.Name + " does not offer THREAD=REFERENCES");
        string mb = await NewMailboxAsync(c);
        try
        {
            // Servers that follow JMAP's rules (Stalwart) also need the subjects to agree.
            byte[] Msg(string id, string subject, string? parent) => Encoding.UTF8.GetBytes(
                $"From: a@example.com\r\nTo: b@example.com\r\nSubject: {subject}\r\nDate: Tue, 03 Nov 2026 10:00:00 +0000\r\nMessage-ID: <{id}@example.com>\r\n" +
                (parent is null ? "" : $"In-Reply-To: <{parent}@example.com>\r\nReferences: <{parent}@example.com>\r\n") + "\r\nbody\r\n");
            await c.AppendAsync(mb, [new ImapAppendItem(Msg("t1", "Topic", null)), new ImapAppendItem(Msg("t2", "Re: Topic", "t1")), new ImapAppendItem(Msg("t3", "Other", null))], Ct);
            await c.SelectAsync(mb, cancellationToken: Ct);
            IReadOnlyList<ImapThread> threads = await c.ThreadAsync(ImapThreadAlgorithm.References, ImapSearch.All, cancellationToken: Ct);
            Assert.Equal(2, threads.Count);
            ImapThread topic = threads.Single(t => t.Number == 1);
            Assert.Equal(2, Assert.Single(topic.Children).Number);
            Assert.Contains(threads, t => t.Number == 3 && t.Children.Count == 0);
        }
        finally
        {
            await DropAsync(c, mb);
        }
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task StoreCopyMoveExpunge(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        await using ImapClient c = await ConnectAsync(s);
        string src = await NewMailboxAsync(c);
        string dst = await NewMailboxAsync(c);
        try
        {
            await c.AppendAsync(src, [new ImapAppendItem(Message("s1")), new ImapAppendItem(Message("s2")), new ImapAppendItem(Message("s3"))], Ct);
            await c.SelectAsync(src, cancellationToken: Ct);
            Assert.Equal(3, c.Mailbox!.Exists);
            long[] uids = [.. (await c.SearchAsync(ImapSearch.All, uid: true, cancellationToken: Ct)).Numbers];

            await c.StoreAsync(ImapSet.Of(uids[0]), ImapStoreMode.Add, ["\\Flagged"], uid: true, cancellationToken: Ct);
            Assert.Equal(uids[..1], (await c.SearchAsync(ImapSearch.Flagged, uid: true, cancellationToken: Ct)).Numbers);
            await c.StoreAsync(ImapSet.Of(uids[0]), ImapStoreMode.Remove, ["\\Flagged"], uid: true, cancellationToken: Ct);
            Assert.Empty((await c.SearchAsync(ImapSearch.Flagged, cancellationToken: Ct)).Numbers);

            ImapCopyResult copied = await c.CopyAsync(ImapSet.Of(uids[..2]), dst, uid: true, Ct);
            if (c.HasCapability("UIDPLUS") || c.HasCapability("IMAP4rev2"))
            {
                Assert.Equal(uids[..2], copied.SourceUids);
                Assert.Equal(2, copied.DestinationUids.Count);
                Assert.True(copied.UidValidity > 0);
            }

            Assert.Equal(2, (await c.StatusAsync(dst, ImapStatusItems.Messages, Ct)).Messages);

            ImapCopyResult moved = await c.MoveAsync(ImapSet.Of(uids[2]), dst, uid: true, Ct);
            Assert.Equal(2, c.Mailbox.Exists);
            Assert.Equal(3, (await c.StatusAsync(dst, ImapStatusItems.Messages, Ct)).Messages);

            await c.StoreAsync(ImapSet.Of(uids[0]), ImapStoreMode.Add, ["\\Deleted"], uid: true, cancellationToken: Ct);
            IReadOnlyList<ImapEvent> gone = await c.ExpungeAsync(cancellationToken: Ct);
            Assert.Single(gone);
            Assert.Equal(1, c.Mailbox.Exists);
            await c.StoreAsync(ImapSet.Of(uids[1]), ImapStoreMode.Add, ["\\Deleted"], uid: true, cancellationToken: Ct);
            if (c.HasCapability("UIDPLUS"))
            {
                Assert.Single(await c.ExpungeAsync(ImapSet.Of(uids[1]), Ct));
                Assert.Equal(0, c.Mailbox.Exists);
            }
        }
        finally
        {
            await DropAsync(c, src);
            await DropAsync(c, dst);
        }
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task CondStoreModSeqAndUnchangedSince(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        await using ImapClient c = await ConnectAsync(s);
        Assert.SkipUnless(c.HasCapability("CONDSTORE") || c.HasCapability("QRESYNC"), s.Name + " does not offer CONDSTORE");
        string mb = await NewMailboxAsync(c);
        try
        {
            await c.AppendAsync(mb, Message("c1"), cancellationToken: Ct);
            ImapMailbox box = await c.SelectAsync(mb, new ImapSelectOptions { CondStore = true }, Ct);
            Assert.True(box.HighestModSeq > 0 || box.NoModSeq == false);
            ImapMessage m = await c.FetchAsync(ImapSet.All, new ImapFetchQuery { Uid = true, ModSeq = true }, false, Ct).SingleAsync(Ct);
            Assert.True(m.ModSeq > 0);
            // A store guarded by a stale mod-sequence is refused for that message.
            long stale = m.ModSeq!.Value - 1;
            IReadOnlyList<long> refused = await c.StoreAsync(ImapSet.All, ImapStoreMode.Add, ["\\Flagged"], false, unchangedSince: stale, cancellationToken: Ct);
            Assert.Equal([1L], refused);
            Assert.Empty(await c.StoreAsync(ImapSet.All, ImapStoreMode.Add, ["\\Flagged"], false, unchangedSince: m.ModSeq, cancellationToken: Ct));
            // CHANGEDSINCE returns only what changed after the given mod-sequence.
            Assert.Single(await c.FetchAsync(ImapSet.All, new ImapFetchQuery { Flags = true, ChangedSince = m.ModSeq }, false, Ct).ToListAsync(Ct));
        }
        finally
        {
            await DropAsync(c, mb);
        }
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task QResyncReportsVanishedSinceKnownState(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        await using ImapClient a = await ConnectAsync(s);
        Assert.SkipUnless(a.HasCapability("QRESYNC"), s.Name + " does not offer QRESYNC");
        await using ImapClient b = await ConnectAsync(s);
        string mb = await NewMailboxAsync(a);
        try
        {
            await a.AppendAsync(mb, [new ImapAppendItem(Message("qa")), new ImapAppendItem(Message("qb"))], Ct);
            await a.EnableAsync(["QRESYNC"], Ct);
            ImapMailbox first = await a.SelectAsync(mb, new ImapSelectOptions { CondStore = true }, Ct);
            long[] uids = [.. (await a.SearchAsync(ImapSearch.All, uid: true, cancellationToken: Ct)).Numbers];
            long validity = first.UidValidity;
            long modSeq = first.HighestModSeq;
            Assert.True(modSeq > 0);
            await a.CloseMailboxAsync(expunge: false, Ct);

            // Another client expunges the first message and flags the second while A is away.
            await b.SelectAsync(mb, cancellationToken: Ct);
            await b.StoreAsync(ImapSet.Of(uids[0]), ImapStoreMode.Add, ["\\Deleted"], uid: true, cancellationToken: Ct);
            await b.ExpungeAsync(cancellationToken: Ct);
            await b.StoreAsync(ImapSet.Of(uids[1]), ImapStoreMode.Add, ["\\Flagged"], uid: true, cancellationToken: Ct);

            ImapMailbox again = await a.SelectAsync(mb, new ImapSelectOptions { QResync = new ImapQResync(validity, modSeq, ImapSet.Of(uids)) }, Ct);
            Assert.Equal(1, again.Exists);
            ImapEvent vanished = Assert.Single(again.ResyncEvents, e => e.Kind == ImapEventKind.Vanished);
            Assert.True(vanished.Earlier);
            Assert.Equal([uids[0]], vanished.Uids.Expand());
            ImapEvent changed = Assert.Single(again.ResyncEvents, e => e.Kind == ImapEventKind.Fetch);
            Assert.Equal(uids[1], changed.Fetch!.Uid);
            Assert.Contains("\\Flagged", changed.Fetch.Flags!, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            await DropAsync(a, mb);
        }
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task MultiAppendAndAppendLimit(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        await using ImapClient c = await ConnectAsync(s);
        string mb = await NewMailboxAsync(c);
        try
        {
            ImapAppendResult r = await c.AppendAsync(mb, [new ImapAppendItem(Message("a1")) { Flags = ["\\Seen"] }, new ImapAppendItem(Message("a2")), new ImapAppendItem(Message("a3"))], Ct);
            Assert.Equal(3, (await c.StatusAsync(mb, ImapStatusItems.Messages, Ct)).Messages);
            if (c.HasCapability("UIDPLUS") && c.HasCapability("MULTIAPPEND"))
            {
                Assert.Equal(3, r.Uids.Count);
            }

            if (c.AppendLimit is { } limit)
            {
                var big = new byte[limit + 1];
                await Assert.ThrowsAsync<ArgumentException>(async () => await c.AppendAsync(mb, big, cancellationToken: Ct));
            }
            else
            {
                Assert.True(true, s.Name + " announces no APPENDLIMIT");
            }
        }
        finally
        {
            await DropAsync(c, mb);
        }
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task IdleReportsAppendFlagChangeAndExpungeFromAnotherConnection(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        var log = new StringWriter();
        await using ImapClient a = await ConnectTracedAsync(s, log);
        Assert.SkipUnless(a.HasCapability("IDLE"), s.Name + " does not offer IDLE");
        await using ImapClient b = await ConnectAsync(s);
        string mb = await NewMailboxAsync(a);
        try
        {
            await a.SelectAsync(mb, cancellationToken: Ct);
            await b.SelectAsync(mb, cancellationToken: Ct);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            stop.CancelAfter(TimeSpan.FromSeconds(45));
            var seen = new List<ImapEvent>();
            Task idle = Task.Run(async () =>
            {
                await foreach (ImapEvent e in a.IdleAsync(stop: stop.Token))
                {
                    lock (seen) { seen.Add(e); }
                }
            }, Ct);

            async Task<bool> SeenAsync(ImapEventKind kind)
            {
                for (int i = 0; i < 300 && !idle.IsCompleted; i++)
                {
                    lock (seen) { if (seen.Any(x => x.Kind == kind)) { return true; } }
                    await Task.Delay(100, Ct);
                }

                return false;
            }

            // One change at a time: a server that polls (no inotify) would fold an append and its expunge into nothing.
            await Task.Delay(500, Ct);
            await b.AppendAsync(mb, Message("i1"), cancellationToken: Ct);
            bool exists = await SeenAsync(ImapEventKind.Exists);
            await b.StoreAsync(ImapSet.Of(1), ImapStoreMode.Add, ["\\Flagged"], silent: true, cancellationToken: Ct);
            bool fetch = await SeenAsync(ImapEventKind.Fetch);
            await b.StoreAsync(ImapSet.Of(1), ImapStoreMode.Add, ["\\Deleted"], cancellationToken: Ct);
            await b.ExpungeAsync(cancellationToken: Ct);
            bool expunge = await SeenAsync(ImapEventKind.Expunge);
            string why = $"IDLE reported exists={exists} fetch={fetch} expunge={expunge}; events: " + string.Join(",", seen.Select(e => e.Kind));
            Assert.True(exists && fetch && expunge, why);
            stop.Cancel();
            await idle;
            // Leaving the enumeration ended IDLE cleanly, so the connection is usable and the model is current.
            await a.NoopAsync(Ct);
            Assert.Equal(0, a.Mailbox!.Exists);
        }
        finally
        {
            await DropAsync(a, mb);
        }
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task CompressDeflateRoundTrip(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        await using ImapClient c = await ConnectAsync(s, compress: true);
        Assert.SkipUnless(c.HasCapability("COMPRESS=DEFLATE") || c.Raw.Connection.IsCompressed, s.Name + " does not offer COMPRESS=DEFLATE");
        Assert.True(c.Raw.Connection.IsCompressed);
        string mb = await NewMailboxAsync(c);
        try
        {
            byte[] msg = ServerHarness.SampleMessage("z");
            await c.AppendAsync(mb, msg, cancellationToken: Ct);
            await c.SelectAsync(mb, cancellationToken: Ct);
            ImapMessage m = await c.FetchAsync(ImapSet.All, new ImapFetchQuery { Source = true }, false, Ct).SingleAsync(Ct);
            Assert.True(msg.AsSpan().SequenceEqual(m.Source.Span));
        }
        finally
        {
            await DropAsync(c, mb);
        }
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task Utf8AcceptSendsNamesAsUtf8(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        await using ImapClient probe = await ConnectAsync(s);
        Assert.SkipUnless(probe.HasCapability("UTF8=ACCEPT"), s.Name + " does not offer UTF8=ACCEPT");
        await using ImapClient c = await ConnectAsync(s, utf8: true);
        Assert.Contains("UTF8=ACCEPT", c.Enabled, StringComparer.OrdinalIgnoreCase);
        string name = NewName("u47-日本語-Ü");
        await c.CreateAsync(name, Ct);
        try
        {
            Assert.Contains(await c.ListAsync(cancellationToken: Ct), m => m.Name == name);
        }
        finally
        {
            await DropAsync(c, name);
        }
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task ObjectIdsAreFetched(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        await using ImapClient c = await ConnectAsync(s);
        Assert.SkipUnless(c.HasCapability("OBJECTID"), s.Name + " does not offer OBJECTID");
        string mb = await NewMailboxAsync(c);
        try
        {
            await c.AppendAsync(mb, Message("o1"), cancellationToken: Ct);
            ImapMailbox box = await c.SelectAsync(mb, cancellationToken: Ct);
            ImapMessage m = await c.FetchAsync(ImapSet.All, new ImapFetchQuery { ObjectIds = true }, false, Ct).SingleAsync(Ct);
            Assert.False(string.IsNullOrEmpty(m.EmailId));
            Assert.False(string.IsNullOrEmpty(box.MailboxId));
        }
        finally
        {
            await DropAsync(c, mb);
        }
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task AbandonedFetchEnumerationLeavesTheConnectionUsable(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        await using ImapClient c = await ConnectAsync(s);
        string mb = await NewMailboxAsync(c);
        try
        {
            await c.AppendAsync(mb, [.. Enumerable.Range(0, 20).Select(i => new ImapAppendItem(Message("e" + i)))], Ct);
            await c.SelectAsync(mb, cancellationToken: Ct);
            await foreach (ImapMessage m in c.FetchAsync(ImapSet.All, new ImapFetchQuery { Source = true }, false, Ct))
            {
                break;
            }

            // The rest of the response was read and discarded.
            Assert.Equal(20, (await c.SearchAsync(ImapSearch.All, cancellationToken: Ct)).Count);
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await foreach (ImapMessage m in c.FetchAsync(ImapSet.All, new ImapFetchQuery { Flags = true }, false, Ct))
                {
                    await c.NoopAsync(Ct);
                }
            });
        }
        finally
        {
            await DropAsync(c, mb);
        }
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task SyncTwinsWork(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        await Task.Run(() =>
        {
            var options = new ImapClientOptions { Connection = ServerHarness.Options(true, s.Name, "imap") };
            using ImapClient c = ImapClient.Connect("127.0.0.1", s.Port("imaps"), options);
            c.Login(s.User, s.Password);
            Assert.True(c.IsAuthenticated);
            c.Logout();
        }, Ct);
        await using ImapClient d = await ConnectAsync(s);
        string name = NewName();
        await d.CreateAsync(name, Ct);
        try
        {
            await Task.Run(() =>
            {
                d.Append(name, Message("sync"));
                d.Select(name);
                Assert.Single(d.Fetch(ImapSet.All, new ImapFetchQuery { Uid = true, Envelope = true }));
                Assert.Equal([1L], d.Search(ImapSearch.All).Numbers);
                d.Store(ImapSet.Of(1), ImapStoreMode.Add, ["\\Seen"]);
                Assert.Equal(1, d.Status(name, ImapStatusItems.Messages).Messages);
                Assert.NotNull(d.GetMessage(d.Search(ImapSearch.All, uid: true).Numbers[0]));
                d.Noop();
            }, Ct);
        }
        finally
        {
            await DropAsync(d, name);
        }
    }
}
