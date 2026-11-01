using System.Text;
using RecursiveAI.OneMail.Client.Imap;
using RecursiveAI.OneMail.Client.Pop3;
using RecursiveAI.OneMail.Client.Protocol;

namespace RecursiveAI.OneMail.Tests.Client;

// The typed IMAP and POP3 clients over scripted server output (the server side is a fixed script, read 7 bytes at a time): no sockets, so these run everywhere.
public class ImapPop3ClientScriptedTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ProtocolConnectionOptions Insecure = new() { AllowInsecureAuthentication = true };

    private const string Greeting = "* OK [CAPABILITY IMAP4rev1 SASL-IR AUTH=PLAIN ENABLE CONDSTORE QRESYNC UIDPLUS MOVE IDLE SORT THREAD=REFERENCES ESEARCH UNSELECT] ready\r\n";
    private const string Login = "A0001 OK [CAPABILITY IMAP4rev1 ENABLE CONDSTORE QRESYNC UIDPLUS MOVE IDLE SORT THREAD=REFERENCES ESEARCH UNSELECT] in\r\n";

    private static async Task<(ImapClient Client, ScriptedStream Stream)> OpenAsync(string rest)
    {
        (ProtocolConnection conn, ScriptedStream stream) = ClientTestSupport.Scripted(Greeting + Login + rest, Insecure);
        ImapClient c = await ImapClient.FromConnectionAsync(conn, cancellationToken: Ct);
        await c.LoginAsync("u", "p", Ct);
        return (c, stream);
    }

    [Fact]
    public void SetsFoldRunsAndRejectInjection()
    {
        Assert.Equal("1:3,5,9:10", ImapSet.Of([10, 3, 1, 2, 5, 9, 2]).Text);
        Assert.Equal("4:*", ImapSet.From(4).Text);
        Assert.Throws<ArgumentException>(() => new ImapSet("1 2"));
        Assert.Throws<ArgumentException>(() => new ImapSet("1)\r\nA2 LOGOUT"));
        Assert.Equal([3L, 4L, 5L, 9L], ImapSet.Parse("3:5,9").Expand());
    }

    [Fact]
    public async Task SelectFillsTheMailboxModelAndLaterResponsesKeepItCurrent()
    {
        (ImapClient c, ScriptedStream s) = await OpenAsync(
            "* FLAGS (\\Seen \\Flagged)\r\n* OK [PERMANENTFLAGS (\\Seen \\*)] ok\r\n* 3 EXISTS\r\n* 1 RECENT\r\n* OK [UIDVALIDITY 42] x\r\n* OK [UIDNEXT 9] x\r\n* OK [HIGHESTMODSEQ 77] x\r\nA0002 OK [READ-WRITE] done\r\n" +
            "* 4 EXISTS\r\n* 2 EXPUNGE\r\nA0003 OK noop\r\n" +
            "* VANISHED 5:6\r\nA0004 OK noop\r\n");
        var events = new List<ImapEvent>();
        c.EventReceived += events.Add;
        ImapMailbox box = await c.SelectAsync("Entwürfe", cancellationToken: Ct);
        Assert.Same(box, c.Mailbox);
        Assert.Equal((3, 1, 42, 9, 77), (box.Exists, box.Recent, box.UidValidity, box.UidNext, box.HighestModSeq));
        Assert.Equal(["\\Seen", "\\Flagged"], box.Flags);
        Assert.Equal(["\\Seen", "\\*"], box.PermanentFlags);
        Assert.False(box.ReadOnly);
        // The mailbox name goes out in modified UTF-7.
        Assert.Contains("A0002 SELECT Entw&APw-rfe\r\n", s.SentText, StringComparison.Ordinal);

        await c.NoopAsync(Ct);
        Assert.Equal(3, box.Exists); // 4 exist, one expunged
        await c.NoopAsync(Ct);
        Assert.Equal(1, box.Exists); // two UIDs vanished
        Assert.Equal([ImapEventKind.Exists, ImapEventKind.Expunge, ImapEventKind.Vanished], events.Where(e => e.Kind != ImapEventKind.Recent).Select(e => e.Kind).TakeLast(3));
    }

    [Fact]
    public async Task AFailedSelectLeavesNothingSelected()
    {
        (ImapClient c, _) = await OpenAsync("A0002 OK [READ-WRITE] ok\r\n");
        // Select once fine (empty), then a failing select must clear it.
        await c.SelectAsync("a", cancellationToken: Ct);
        Assert.NotNull(c.Mailbox);
        (ImapClient d, _) = await OpenAsync("A0002 OK ok\r\nA0003 NO [NONEXISTENT] no such mailbox\r\n");
        await d.SelectAsync("a", cancellationToken: Ct);
        ImapCommandException e = await Assert.ThrowsAsync<ImapCommandException>(async () => await d.SelectAsync("b", cancellationToken: Ct));
        Assert.Equal("NONEXISTENT", e.ResponseCode);
        Assert.Null(d.Mailbox);
    }

    [Fact]
    public async Task QResyncSelectReturnsTheServersReport()
    {
        (ImapClient c, ScriptedStream s) = await OpenAsync(
            "* OK [CLOSED] x\r\n* ENABLED QRESYNC\r\nA0002 OK enabled\r\n" +
            "* 5 EXISTS\r\n* OK [UIDVALIDITY 42] x\r\n* OK [HIGHESTMODSEQ 90] x\r\n* VANISHED (EARLIER) 41:43\r\n* 2 FETCH (UID 50 MODSEQ (88) FLAGS (\\Seen))\r\nA0003 OK [READ-WRITE] done\r\n");
        ImapMailbox box = await c.SelectAsync("INBOX", new ImapSelectOptions { QResync = new ImapQResync(42, 80, ImapSet.Parse("41:60")) }, Ct);
        Assert.Contains("A0002 ENABLE QRESYNC\r\n", s.SentText, StringComparison.Ordinal);
        Assert.Contains("A0003 SELECT INBOX (QRESYNC (42 80 41:60))\r\n", s.SentText, StringComparison.Ordinal);
        Assert.Equal(5, box.Exists);
        ImapEvent vanished = Assert.Single(box.ResyncEvents, e => e.Kind == ImapEventKind.Vanished);
        Assert.True(vanished.Earlier);
        Assert.Equal("41:43", vanished.Uids.Text);
        ImapEvent changed = Assert.Single(box.ResyncEvents, e => e.Kind == ImapEventKind.Fetch);
        Assert.Equal(50, changed.Fetch!.Uid);
        // A resync report is not news about the message count: EARLIER does not change Exists.
        Assert.Equal(5, box.Exists);
    }

    [Fact]
    public async Task FetchStreamsResultsBuffersSectionsAndStopsCleanlyWhenAbandoned()
    {
        (ImapClient c, ScriptedStream s) = await OpenAsync(
            "A0002 OK ok\r\n" +
            "* 1 FETCH (UID 7 FLAGS (\\Seen) BODY[HEADER.FIELDS (SUBJECT)] {16}\r\nSubject: hello\r\n BODY[1.2]<0> {3}\r\nabc)\r\n" +
            "* 2 FETCH (UID 8 FLAGS ())\r\n* 3 FETCH (UID 9 FLAGS ())\r\nA0003 OK done\r\n" +
            "* SEARCH 1 2 3\r\nA0004 OK done\r\n");
        await c.SelectAsync("x", cancellationToken: Ct);
        var query = new ImapFetchQuery { Uid = true, Flags = true, HeaderFields = ["Subject"], Sections = [new ImapSectionRequest("1.2", 0, 100)] };
        int seen = 0;
        await foreach (ImapMessage m in c.FetchAsync(ImapSet.All, query, false, Ct))
        {
            Assert.Equal(7, m.Uid);
            Assert.Equal("Subject: hello\r\n", Encoding.ASCII.GetString(m.GetHeaderFields().Span));
            Assert.Equal("abc", Encoding.ASCII.GetString(m.GetSection("1.2").Span));
            seen++;
            break; // abandon: the other two results must be read and discarded
        }

        Assert.Equal(1, seen);
        Assert.Contains("A0003 FETCH 1:* (UID FLAGS BODY.PEEK[HEADER.FIELDS (Subject)] BODY.PEEK[1.2]<0.100>)\r\n", s.SentText, StringComparison.Ordinal);
        // The connection is clean: the next command gets its own answer.
        Assert.Equal([1L, 2L, 3L], (await c.SearchAsync(ImapSearch.All, cancellationToken: Ct)).Numbers);
    }

    [Fact]
    public async Task OverlappingCommandsAreRefusedWhileAFetchIsOpen()
    {
        (ImapClient c, _) = await OpenAsync("A0002 OK ok\r\n* 1 FETCH (UID 7)\r\n* 2 FETCH (UID 8)\r\nA0003 OK done\r\n");
        await c.SelectAsync("x", cancellationToken: Ct);
        await using IAsyncEnumerator<ImapMessage> e = c.FetchAsync(ImapSet.All, new ImapFetchQuery { Uid = true }, false, Ct).GetAsyncEnumerator(Ct);
        Assert.True(await e.MoveNextAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await c.NoopAsync(Ct));
    }

    [Fact]
    public async Task SearchBuildsSafeCommandsAndReadsEsearchAndThreads()
    {
        (ImapClient c, ScriptedStream s) = await OpenAsync(
            "A0002 OK ok\r\n" +
            "+ go\r\n* ESEARCH (TAG \"A0003\") UID MIN 4 MAX 9 COUNT 2 ALL 4,9\r\nA0003 OK done\r\n" +
            "* THREAD (2)(3 6 (4 23)(44 7 96))((8)(9))\r\nA0004 OK done\r\n" +
            "* SORT 5 3 1\r\nA0005 OK done\r\n");
        await c.SelectAsync("x", cancellationToken: Ct);
        // A string argument with a quote, a backslash and a line break cannot leave its position.
        ImapSearchResult r = await c.SearchAsync(ImapSearch.Subject("a\"b\\c").And(ImapSearch.Subject("d\r\ne").Not()), true, ImapSearchReturn.Min | ImapSearchReturn.Max | ImapSearchReturn.Count | ImapSearchReturn.All, Ct);
        Assert.Equal((4L, 9L, 2L), (r.Min, r.Max, r.Count));
        Assert.Equal([4L, 9L], r.Numbers);
        Assert.True(r.IsUid);
        string sent = s.SentText;
        Assert.Contains("A0003 UID SEARCH RETURN (MIN MAX ALL COUNT) SUBJECT \"a\\\"b\\\\c\" NOT SUBJECT {4}\r\nd\r\ne\r\n", sent, StringComparison.Ordinal);

        IReadOnlyList<ImapThread> threads = await c.ThreadAsync(ImapThreadAlgorithm.References, ImapSearch.All, cancellationToken: Ct);
        Assert.Equal(3, threads.Count);
        Assert.Equal(2, threads[0].Number);
        ImapThread chain = threads[1];
        Assert.Equal(3, chain.Number);
        Assert.Equal(6, Assert.Single(chain.Children).Number);
        Assert.Equal([4L, 44L], chain.Children[0].Children.Select(t => t.Number!.Value));
        Assert.Null(threads[2].Number); // dummy parent
        Assert.Equal([8L, 9L], threads[2].Children.Select(t => t.Number!.Value));

        Assert.Equal([5L, 3L, 1L], await c.SortAsync([new ImapSortCriterion(ImapSortKey.Date, true), new ImapSortCriterion(ImapSortKey.Subject)], ImapSearch.All, cancellationToken: Ct));
        Assert.Contains("A0005 SORT (REVERSE DATE SUBJECT) UTF-8 ALL\r\n", s.SentText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoreCopyMoveAndAppendReadTheirResponseCodes()
    {
        (ImapClient c, ScriptedStream s) = await OpenAsync(
            "A0002 OK ok\r\n" +
            "A0003 OK [MODIFIED 7,9] conditional store done\r\n" +
            "A0004 OK [COPYUID 38505 304,319:320 3956:3958] copied\r\n" +
            "* OK [COPYUID 77 5 12] moved\r\n* VANISHED 5\r\nA0005 OK moved\r\n" +
            "+ ready\r\nA0006 OK [APPENDUID 99 1000:1001] appended\r\n");
        await c.SelectAsync("x", cancellationToken: Ct);
        Assert.Equal([7L, 9L], await c.StoreAsync(ImapSet.Parse("5:9"), ImapStoreMode.Add, ["\\Flagged", "$Forwarded"], uid: true, unchangedSince: 12, cancellationToken: Ct));
        ImapCopyResult copy = await c.CopyAsync(ImapSet.Parse("304,319:320"), "Archive", uid: true, Ct);
        Assert.Equal(38505L, copy.UidValidity);
        Assert.Equal([304L, 319L, 320L], copy.SourceUids);
        Assert.Equal([3956L, 3957L, 3958L], copy.DestinationUids);
        ImapCopyResult moved = await c.MoveAsync(ImapSet.Of(5), "Trash", uid: true, Ct);
        Assert.Equal([5L], moved.SourceUids);
        Assert.Equal([12L], moved.DestinationUids);
        ImapAppendResult added = await c.AppendAsync("Sent", Encoding.ASCII.GetBytes("Subject: x\r\n\r\nbody\r\n"), ["\\Seen"], new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.FromMinutes(-90)), Ct);
        Assert.Equal(99L, added.UidValidity);
        Assert.Equal([1000L, 1001L], added.Uids);
        string sent = s.SentText;
        Assert.Contains("A0003 UID STORE 5:9 (UNCHANGEDSINCE 12) +FLAGS.SILENT (\\Flagged $Forwarded)\r\n", sent, StringComparison.Ordinal);
        Assert.Contains("A0004 UID COPY 304,319:320 Archive\r\n", sent, StringComparison.Ordinal);
        Assert.Contains("A0005 UID MOVE 5 Trash\r\n", sent, StringComparison.Ordinal);
        Assert.Contains("A0006 APPEND Sent (\\Seen) \"04-Mar-2026 05:06:07 -0130\" {20}\r\n", sent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppendRefusesWhatTheServerLimitForbidsBeforeSendingAnything()
    {
        (ProtocolConnection conn, ScriptedStream stream) = ClientTestSupport.Scripted(
            "* OK [CAPABILITY IMAP4rev1 AUTH=PLAIN SASL-IR APPENDLIMIT=10] ready\r\nA0001 OK [CAPABILITY IMAP4rev1 APPENDLIMIT=10] in\r\n", Insecure);
        ImapClient c = await ImapClient.FromConnectionAsync(conn, cancellationToken: Ct);
        await c.LoginAsync("u", "p", Ct);
        Assert.Equal(10, c.AppendLimit);
        ArgumentException e = await Assert.ThrowsAsync<ArgumentException>(async () => await c.AppendAsync("x", new byte[11], cancellationToken: Ct));
        Assert.Contains("APPENDLIMIT", e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("APPEND x", stream.SentText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdleYieldsEventsAndEndsWithTheConnection()
    {
        (ImapClient c, ScriptedStream s) = await OpenAsync(
            "A0002 OK ok\r\n+ idling\r\n* 4 EXISTS\r\n* 2 FETCH (FLAGS (\\Seen))\r\n* 1 EXPUNGE\r\n* BYE shutting down\r\n");
        await c.SelectAsync("x", cancellationToken: Ct);
        var kinds = new List<ImapEventKind>();
        await foreach (ImapEvent e in c.IdleAsync(stop: Ct))
        {
            kinds.Add(e.Kind);
        }

        Assert.Equal([ImapEventKind.Exists, ImapEventKind.Fetch, ImapEventKind.Expunge, ImapEventKind.Bye], kinds);
        Assert.Equal(3, c.Mailbox!.Exists);
        Assert.Contains("A0003 IDLE\r\n", s.SentText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoppingIdleSendsDoneAndReadsTheCompletion()
    {
        (ProtocolConnection conn, ScriptedStream stream) = ClientTestSupport.Scripted(
            Greeting + Login + "A0002 OK ok\r\n+ idling\r\n* 4 EXISTS\r\nA0003 OK IDLE terminated\r\nA0004 OK noop\r\n", Insecure);
        ImapClient c = await ImapClient.FromConnectionAsync(conn, cancellationToken: Ct);
        await c.LoginAsync("u", "p", Ct);
        await c.SelectAsync("x", cancellationToken: Ct);
        await foreach (ImapEvent e in c.IdleAsync(stop: Ct))
        {
            Assert.Equal(ImapEventKind.Exists, e.Kind);
            break; // the consumer leaves: IDLE must be ended with DONE, and the completion consumed
        }

        Assert.Contains("A0003 IDLE\r\nDONE\r\n", stream.SentText, StringComparison.Ordinal);
        await c.NoopAsync(Ct); // reads A0004, so the stream was left on a response boundary
    }

    [Fact]
    public async Task ListDecodesNamesAndSpecialUse()
    {
        (ImapClient c, _) = await OpenAsync(
            "* LIST (\\HasNoChildren \\Sent) \"/\" \"Gesendet\"\r\n* LIST (\\HasChildren) \"/\" \"Entw&APw-rfe\"\r\nA0002 OK done\r\n");
        IReadOnlyList<ImapMailboxInfo> all = await c.ListAsync(cancellationToken: Ct);
        Assert.Equal(["Gesendet", "Entwürfe"], all.Select(m => m.Name));
        Assert.Equal(ImapSpecialUse.Sent, all[0].SpecialUse);
        Assert.True(all[1].HasAttribute("\\haschildren"));
    }

    [Fact]
    public async Task MissingCapabilitiesAreReportedBeforeAnythingIsSent()
    {
        (ProtocolConnection conn, ScriptedStream stream) = ClientTestSupport.Scripted("* OK [CAPABILITY IMAP4rev1 AUTH=PLAIN SASL-IR] ready\r\nA0001 OK [CAPABILITY IMAP4rev1] in\r\n", Insecure);
        ImapClient c = await ImapClient.FromConnectionAsync(conn, cancellationToken: Ct);
        await c.LoginAsync("u", "p", Ct);
        await Assert.ThrowsAsync<NotSupportedException>(async () => await c.SortAsync([new ImapSortCriterion(ImapSortKey.Date)], ImapSearch.All, cancellationToken: Ct));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await c.ThreadAsync(ImapThreadAlgorithm.References, ImapSearch.All, cancellationToken: Ct));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await c.EnableAsync(["CONDSTORE"], Ct));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await c.MoveAsync(ImapSet.Of(1), "x", false, Ct));
        Assert.DoesNotContain("SORT", stream.SentText.Replace("AUTHENTICATE", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CredentialsAreNotSentOverAnUnencryptedConnectionByDefault()
    {
        (ProtocolConnection conn, ScriptedStream stream) = ClientTestSupport.Scripted("* OK [CAPABILITY IMAP4rev1 AUTH=PLAIN] ready\r\n");
        ImapClient c = await ImapClient.FromConnectionAsync(conn, cancellationToken: Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await c.LoginAsync("u", "secret", Ct));
        Assert.DoesNotContain("secret", stream.SentText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pop3ReadsTheMaildropAndPipelinesRetrieveAndDelete()
    {
        (ProtocolConnection conn, ScriptedStream stream) = ClientTestSupport.Scripted(
            "+OK ready <1.2@host>\r\n+OK\r\nUSER\r\nPIPELINING\r\nUIDL\r\n.\r\n" +      // CAPA
            "+OK\r\n+OK\r\n" +                                                          // USER, PASS
            "+OK\r\nUSER\r\nPIPELINING\r\nUIDL\r\n.\r\n" +                              // CAPA again
            "+OK 2 300\r\n" +                                                           // STAT
            "+OK\r\n1 100\r\n2 200\r\n.\r\n" +                                          // LIST
            "+OK\r\n1 abc\r\n2 def\r\n.\r\n" +                                          // UIDL
            "+OK\r\nSubject: one\r\n\r\n..dot\r\n.\r\n" + "+OK\r\nSubject: two\r\n\r\nx\r\n.\r\n" +   // RETR 1, RETR 2 (pipelined)
            "+OK deleted\r\n+OK deleted\r\n" +                                          // DELE 1, DELE 2 (pipelined)
            "-ERR no such message\r\n" +                                                // RETR 9
            "+OK bye\r\n", Insecure);
        Pop3Client p = await Pop3Client.ConnectAsync(conn, cancellationToken: Ct);
        await p.LoginAsync("u", "p", Ct);
        Assert.Equal((2, 300L), await p.StatAsync(Ct));
        Assert.Equal([100L, 200L], (await p.ListAsync(Ct)).Select(m => m.Size));
        Assert.Equal(["abc", "def"], (await p.UidlAsync(Ct)).Select(m => m.Uid));

        var subjects = new List<string?>();
        await foreach ((int n, RecursiveAI.OneMail.Mime.MimeEntity m) in p.GetMessagesAsync([1, 2], cancellationToken: Ct))
        {
            subjects.Add(n + ":" + m.Headers.GetFirst("Subject")?.Value);
        }

        Assert.Equal(["1:one", "2:two"], subjects);
        // Both RETRs were on the wire before the first body was read (PIPELINING).
        Assert.Contains("RETR 1\r\nRETR 2\r\n", stream.SentText, StringComparison.Ordinal);
        await p.DeleteAsync([1, 2], Ct);
        Assert.Contains("DELE 1\r\nDELE 2\r\n", stream.SentText, StringComparison.Ordinal);
        Pop3CommandException e = await Assert.ThrowsAsync<Pop3CommandException>(async () => await p.RetrieveAsync(9, Stream.Null, Ct));
        Assert.Contains("no such message", e.Message, StringComparison.Ordinal);
        await p.QuitAsync(Ct);
    }

    [Fact]
    public async Task Pop3RefusesPasswordsInTheClearAndAPOPNeedsTheTimestamp()
    {
        (ProtocolConnection conn, ScriptedStream stream) = ClientTestSupport.Scripted("+OK ready\r\n-ERR\r\n");
        Pop3Client p = await Pop3Client.ConnectAsync(conn, cancellationToken: Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await p.LoginAsync("u", "secret", Ct));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await p.ApopAsync("u", "secret", Ct));
        Assert.DoesNotContain("secret", stream.SentText, StringComparison.Ordinal);

        // RFC 1939 §7's worked example: timestamp <1896.697170952@dbc.mtview.ca.us>, secret "tanstaaf" -> c4c9334bac560ecc979e58001b3e22fb.
        (ProtocolConnection conn2, ScriptedStream stream2) = ClientTestSupport.Scripted("+OK POP3 server ready <1896.697170952@dbc.mtview.ca.us>\r\n-ERR\r\n+OK\r\nUSER\r\n.\r\n", Insecure);
        Pop3Client q = await Pop3Client.ConnectAsync(conn2, cancellationToken: Ct);
        await q.ApopAsync("mrose", "tanstaaf", Ct);
        Assert.Contains("APOP mrose c4c9334bac560ecc979e58001b3e22fb\r\n", stream2.SentText, StringComparison.Ordinal);
    }
}
