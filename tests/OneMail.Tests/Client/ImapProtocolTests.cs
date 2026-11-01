using System.Text;
using RecursiveAI.OneMail.Client.Imap.Protocol;
using RecursiveAI.OneMail.Client.Protocol;
using RecursiveAI.OneMail.Client.Sasl;
using RecursiveAI.OneMail.Mime.Reading;

namespace RecursiveAI.OneMail.Tests.Client;

public class ImapProtocolTests
{
    private static ImapResponse Parse(string wire)
    {
        (ProtocolConnection c, _) = ClientTestSupport.Scripted(wire);
        return new ImapResponseReader(c.Reader).ReadResponse();
    }

    [Fact]
    public void GreetingWithCodeAndCapabilities()
    {
        ImapResponse r = Parse("* OK [CAPABILITY IMAP4rev1 STARTTLS AUTH=PLAIN LITERAL+] Dovecot ready.\r\n");
        Assert.Equal(ImapResponseKind.Untagged, r.Kind);
        Assert.Equal(ImapStatus.Ok, r.Status);
        Assert.Equal("CAPABILITY", r.Code!.Name);
        Assert.Equal("Dovecot ready.", r.Text);
        Assert.True(r.TryGetCapabilities(out IReadOnlyList<string> caps));
        Assert.Equal(["IMAP4rev1", "STARTTLS", "AUTH=PLAIN", "LITERAL+"], caps);
    }

    [Fact]
    public void TaggedStatusAndCodesWithListsAndNumbers()
    {
        ImapResponse r = Parse("A0003 OK [APPENDUID 38505 3955] APPEND completed\r\n");
        Assert.True(r.IsOk);
        Assert.Equal("A0003", r.Tag);
        Assert.Equal("APPENDUID", r.Code!.Name);
        Assert.Equal(2, r.Code.Arguments.Count);
        Assert.True(r.Code.Arguments[1].TryGetNumber(out long n));
        Assert.Equal(3955, n);

        ImapResponse p = Parse("* OK [PERMANENTFLAGS (\\Seen \\Deleted \\*)] Limited\r\n");
        Assert.Equal(3, p.Code!.Arguments[0].Items.Count);

        ImapResponse no = Parse("A9 NO [TRYCREATE] Mailbox doesn't exist: \"x\"\r\n");
        Assert.Equal(ImapStatus.No, no.Status);
        Assert.Equal("TRYCREATE", no.Code!.Name);
    }

    [Fact]
    public void ListMailboxNamesDecodeModifiedUtf7()
    {
        ImapResponse r = Parse("* LIST (\\HasNoChildren \\Sent) \"/\" \"Entw&APw-rfe\"\r\n");
        Assert.True(r.TryGetList(out ImapListEntry e));
        Assert.Equal("Entwürfe", e.Name);
        Assert.Equal("/", e.Delimiter);
        Assert.Equal(["\\HasNoChildren", "\\Sent"], e.Attributes);
        ImapResponse nil = Parse("* LIST () NIL INBOX\r\n");
        Assert.True(nil.TryGetList(out ImapListEntry n));
        Assert.Null(n.Delimiter);
        Assert.Equal("INBOX", n.Name);
    }

    [Fact]
    public void FetchEnvelopeAndBodyStructureAreTyped()
    {
        string wire = "* 12 FETCH (UID 4827 FLAGS (\\Seen $Forwarded) INTERNALDATE \"17-Jul-1996 02:44:25 -0700\" RFC822.SIZE 4286 "
            + "ENVELOPE (\"Wed, 17 Jul 1996 02:23:25 -0700 (PDT)\" \"IMAP4rev1 WG mtg summary\" ((\"Terry Gray\" NIL \"gray\" \"cac.washington.edu\")) NIL NIL "
            + "((NIL NIL \"imap\" \"cac.washington.edu\")) NIL NIL NIL \"<B27397-0100000@cac.washington.edu>\") "
            + "BODYSTRUCTURE ((\"TEXT\" \"PLAIN\" (\"CHARSET\" \"US-ASCII\") NIL NIL \"7BIT\" 3028 92 NIL NIL NIL)(\"APPLICATION\" \"PDF\" (\"NAME\" \"a.pdf\") NIL NIL \"BASE64\" 100 NIL (\"attachment\" (\"filename\" \"a.pdf\")) NIL) \"MIXED\" (\"BOUNDARY\" \"xyz\") NIL NIL) X-GM-LABELS (\"\\\\Inbox\" Work))\r\n";
        ImapResponse r = Parse(wire);
        Assert.True(r.TryGetFetch(out ImapFetchData f));
        Assert.Equal(12, f.SequenceNumber);
        Assert.Equal(4827, f.Uid);
        Assert.Equal(["\\Seen", "$Forwarded"], f.Flags);
        Assert.Equal(4286, f.Rfc822Size);
        Assert.Equal(new DateTimeOffset(1996, 7, 17, 2, 44, 25, TimeSpan.FromHours(-7)), f.InternalDate);
        Assert.Equal("IMAP4rev1 WG mtg summary", f.Envelope!.Subject);
        Assert.Equal("gray", f.Envelope.From[0].Mailbox);
        Assert.Equal("<B27397-0100000@cac.washington.edu>", f.Envelope.MessageId);
        ImapBodyStructure b = f.BodyStructure!;
        Assert.True(b.IsMultipart);
        Assert.Equal("mixed", b.Subtype);
        Assert.Equal(2, b.Parts!.Count);
        Assert.Equal("1", b.Parts[0].Section);
        Assert.Equal("text/plain", b.Parts[0].MediaType);
        Assert.Equal(92, b.Parts[0].Lines);
        Assert.Equal("2", b.Parts[1].Section);
        Assert.Equal("attachment", b.Parts[1].DispositionType);
        Assert.Equal("a.pdf", b.Parts[1].DispositionParameters[0].Value);
        Assert.Equal("boundary", b.Parameters[0].Key);
        // Unknown items are kept.
        Assert.Equal("X-GM-LABELS", f.Other[0].Key);
        Assert.Equal(2, f.Other[0].Value.Items.Count);
        Assert.Empty(r.Defects);
    }

    [Fact]
    public void SearchEsearchStatusNamespaceVanished()
    {
        Assert.True(Parse("* SEARCH 2 84 882\r\n").TryGetSearch(out IReadOnlyList<long> nums));
        Assert.Equal([2L, 84, 882], nums);
        Assert.True(Parse("* ESEARCH (TAG \"A282\") UID MIN 2 MAX 47 COUNT 3 ALL 2,10:11\r\n").TryGetEsearch(out ImapEsearchData es));
        Assert.Equal("A282", es.Tag);
        Assert.True(es.IsUid);
        Assert.Equal("2,10:11", es.Get("ALL")!.Text);
        Assert.Equal([2L, 10, 11], ImapSequenceSet.Expand("2,10:11"));
        Assert.True(Parse("* STATUS \"blurdybloop\" (MESSAGES 231 UIDNEXT 44292)\r\n").TryGetStatus(out ImapStatusData st));
        Assert.Equal(231, st.Items["MESSAGES"]);
        Assert.True(Parse("* NAMESPACE ((\"\" \"/\")) NIL ((\"Public/\" \"/\"))\r\n").TryGetNamespace(out ImapNamespaceData ns));
        Assert.Equal("Public/", ns.Shared[0].Key);
        Assert.True(Parse("* VANISHED (EARLIER) 300:310,405\r\n").TryGetVanished(out ImapVanishedData v));
        Assert.True(v.Earlier);
        Assert.Equal("300:310,405", v.UidSet);
    }

    [Fact]
    public void ContinuationKeepsItsText()
    {
        ImapResponse r = Parse("+ idling\r\n");
        Assert.Equal(ImapResponseKind.Continuation, r.Kind);
        Assert.Equal("idling", r.Text);
        Assert.Equal("", Parse("+\r\n").Text);
    }

    [Fact]
    public void LiteralsAreBufferedAndFollowedByTheRestOfTheLine()
    {
        ImapResponse r = Parse("* 3 FETCH (UID 9 BODY[HEADER.FIELDS (SUBJECT)] {15}\r\nSubject: hi\r\n\r\n FLAGS (\\Seen))\r\n");
        Assert.True(r.TryGetFetch(out ImapFetchData f));
        Assert.Equal(9, f.Uid);
        Assert.Equal(["\\Seen"], f.Flags);
        ImapFetchSection s = Assert.Single(f.Sections);
        Assert.Equal("BODY[HEADER.FIELDS (SUBJECT)]", s.Name);
        Assert.Equal("Subject: hi\r\n\r\n", s.Data.Text);
    }

    [Fact]
    public void LiteralInAListOfNamesAndNonSynchronisingMarker()
    {
        ImapResponse r = Parse("* LIST () \"/\" {5}\r\nb\"c d\r\n");
        Assert.True(r.TryGetList(out ImapListEntry e));
        Assert.Equal("b\"c d", e.Name);
        ImapResponse plus = Parse("* LIST () \"/\" {3+}\r\nabc\r\n");
        Assert.True(plus.TryGetList(out ImapListEntry e2));
        Assert.Equal("abc", e2.Name);
    }

    [Fact]
    public void StatusTextEndingInBracesIsNotALiteral()
    {
        ImapResponse r = Parse("* OK weird {5}\r\n");
        Assert.Equal("weird {5}", r.Text);
    }

    [Fact]
    public async Task FetchLiteralStreamsIntoTheMimeReaderWithoutBuffering()
    {
        string message = "From: a@example.com\r\nSubject: s\r\n\r\nbody line\r\n";
        string wire = "* 1 FETCH (BODY[] {" + message.Length + "}\r\n" + message + " UID 7)\r\nA1 OK done\r\n";
        (ProtocolConnection c, _) = ClientTestSupport.Scripted(wire);
        var reader = new ImapResponseReader(c.Reader);
        int events = 0;
        string? seenItem = null;
        long bodyBytes = 0;
        ImapResponse r = await reader.ReadResponseAsync(async (ctx, stream, ct) =>
        {
            seenItem = ctx.ItemName;
            Assert.Equal(message.Length, ctx.Length);
            using var mime = new MimeReader(stream);
            while (await mime.ReadAsync(ct))
            {
                events++;
                if (mime.Kind == MimeEventKind.Body)
                {
                    bodyBytes += mime.Length;
                }
            }

            return true;
        }, TestContext.Current.CancellationToken);
        Assert.Equal("BODY[]", seenItem);
        Assert.True(events > 4);
        Assert.Equal("body line\r\n".Length, bodyBytes);
        Assert.True(r.TryGetFetch(out ImapFetchData f));
        Assert.Equal(7, f.Uid);
        Assert.True(f.Sections[0].Data.IsStreamed);
        Assert.Equal(message.Length, f.Sections[0].Data.LiteralLength);
        ImapResponse done = await reader.ReadResponseAsync(null, TestContext.Current.CancellationToken);
        Assert.True(done.IsOk);
    }

    [Fact]
    public void HandlerThatReadsNothingAndReturnsFalseGetsTheLiteralBuffered()
    {
        (ProtocolConnection c, _) = ClientTestSupport.Scripted("* 1 FETCH (BODY[] {3}\r\nabc)\r\n");
        ImapResponse r = new ImapResponseReader(c.Reader).ReadResponse((_, _) => false);
        Assert.True(r.TryGetFetch(out ImapFetchData f));
        Assert.Equal("abc", f.Sections[0].Data.Text);
    }

    [Fact]
    public void UnreadStreamedLiteralIsSkippedSoTheNextResponseIsAligned()
    {
        (ProtocolConnection c, _) = ClientTestSupport.Scripted("* 1 FETCH (BODY[] {10}\r\n0123456789)\r\nA1 OK x\r\n");
        var reader = new ImapResponseReader(c.Reader);
        ImapResponse r = reader.ReadResponse((_, s) => { s.ReadByte(); return true; });
        Assert.True(r.TryGetFetch(out _));
        Assert.True(reader.ReadResponse().IsOk);
    }

    [Fact]
    public void OversizedBufferedLiteralIsSkippedWithADefect()
    {
        var options = new ProtocolConnectionOptions { Limits = new ProtocolLimits { MaxBufferedLiteral = 4 } };
        (ProtocolConnection c, _) = ClientTestSupport.Scripted("* 1 FETCH (BODY[] {10}\r\n0123456789)\r\n", options);
        ImapResponse r = new ImapResponseReader(c.Reader).ReadResponse();
        Assert.Contains(r.Defects, d => d.Kind == ProtocolDefectKind.LiteralSkipped);
        Assert.True(r.TryGetFetch(out ImapFetchData f));
        Assert.True(f.Sections[0].Data.IsSkipped);
    }

    [Fact]
    public void LimitsThrowAndMalformedInputDoesNot()
    {
        var small = new ProtocolConnectionOptions { Limits = new ProtocolLimits { MaxLineLength = 64 } };
        (ProtocolConnection c, _) = ClientTestSupport.Scripted("* OK " + new string('x', 200) + "\r\n", small);
        Assert.Throws<MailProtocolLimitException>(() => new ImapResponseReader(c.Reader).ReadResponse());

        (ProtocolConnection big, _) = ClientTestSupport.Scripted("* 1 FETCH (BODY[] {99999999999}\r\n");
        Assert.Throws<MailProtocolLimitException>(() => new ImapResponseReader(big.Reader).ReadResponse());

        (ProtocolConnection cut, _) = ClientTestSupport.Scripted("* 1 FETCH (BODY[] {10}\r\nabc");
        Assert.Throws<MailConnectionClosedException>(() => new ImapResponseReader(cut.Reader).ReadResponse());

        ImapResponse open = Parse("* LIST (\\A \"/\" \"unterminated\r\n");
        Assert.NotEmpty(open.Defects);
        ImapResponse deep = Parse("* X " + new string('(', 500) + "\r\n");
        Assert.Contains(deep.Defects, d => d.Kind == ProtocolDefectKind.NestingTooDeep);
        Assert.NotEmpty(Parse("garbage\r\n").Defects);
        Assert.True(Parse("* 1 FETCH (UID x ENVELOPE 5)\r\n").TryGetFetch(out ImapFetchData bad));
        Assert.Equal(2, bad.Defects.Count);
    }

    // ---- sending ----

    [Fact]
    public async Task LoginRedactsTheTraceAndRefusesCleartextByDefault()
    {
        var log = new StringWriter();
        (ProtocolConnection c, ScriptedStream s) = ClientTestSupport.Scripted("A0001 OK logged in\r\n",
            new ProtocolConnectionOptions { Trace = new TextWriterProtocolTrace(log), AllowInsecureAuthentication = true });
        var imap = new ImapRawConnection(c);
        ImapCommandResult r = await imap.LoginAsync("user", "p\"w d", TestContext.Current.CancellationToken);
        Assert.True(r.IsOk);
        Assert.Equal("A0001 LOGIN \"user\" \"p\\\"w d\"\r\n", s.SentText);
        Assert.DoesNotContain("p\\\"w", log.ToString());
        Assert.Contains("C: A0001 LOGIN ****** ********", log.ToString());

        (ProtocolConnection plain, ScriptedStream sent) = ClientTestSupport.Scripted("");
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await new ImapRawConnection(plain).LoginAsync("u", "p", TestContext.Current.CancellationToken));
        Assert.Equal("", sent.SentText);
    }

    [Fact]
    public void CommandPartsAreQuotedOrLiteralAndMailboxesAreModifiedUtf7()
    {
        (ProtocolConnection c, ScriptedStream s) = ClientTestSupport.Scripted("A0001 OK ok\r\nA0002 OK ok\r\n");
        var imap = new ImapRawConnection(c);
        imap.Execute(ImapCommand.Create("SELECT").Mailbox("Entwürfe & Co"));
        Assert.Equal("A0001 SELECT \"Entw&APw-rfe &- Co\"\r\n", s.SentText);
        imap.Execute(ImapCommand.Create("STORE").SequenceSet("1:*").Atom("+FLAGS").Flags(["\\Seen", "$x"]));
        Assert.EndsWith("A0002 STORE 1:* +FLAGS (\\Seen $x)\r\n", s.SentText, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => ImapCommand.Create("X").Atom("a b"));
        Assert.Throws<ArgumentException>(() => ImapCommand.Create("X").Raw("a\r\nB LOGOUT"));
        Assert.Throws<ArgumentException>(() => ImapCommand.Create("X").Flags(["a)"]));
    }

    [Fact]
    public void SynchronisingLiteralWaitsForTheContinuation()
    {
        (ProtocolConnection c, ScriptedStream s) = ClientTestSupport.Scripted("* 1 EXISTS\r\n+ Ready\r\nA0001 OK [APPENDUID 1 2] done\r\n");
        var imap = new ImapRawConnection(c);
        byte[] msg = "Subject: x\r\n\r\nhi\r\n"u8.ToArray();
        ImapCommandResult r = imap.Execute(ImapCommand.Create("APPEND").Mailbox("INBOX").Flags(["\\Seen"]).Literal(msg));
        Assert.Equal("A0001 APPEND INBOX (\\Seen) {18}\r\nSubject: x\r\n\r\nhi\r\n\r\n", s.SentText);
        Assert.True(r.IsOk);
        Assert.Equal("APPENDUID", r.Completion.Code!.Name);
        Assert.Single(r.Untagged);
    }

    [Fact]
    public void LiteralPlusIsUsedWhenAdvertisedAndSkipsTheWait()
    {
        (ProtocolConnection c, ScriptedStream s) = ClientTestSupport.Scripted("* CAPABILITY IMAP4rev1 LITERAL+\r\nA0001 OK ok\r\n");
        var imap = new ImapRawConnection(c);
        imap.Execute(ImapCommand.Create("CAPABILITY"));
        Assert.True(imap.HasCapability("literal+"));
        (ProtocolConnection c2, ScriptedStream s2) = ClientTestSupport.Scripted("A0001 OK ok\r\n");
        var imap2 = new ImapRawConnection(c2);
        imap2.Execute(ImapCommand.Create("X").Literal("ab"u8.ToArray(), ImapLiteralMode.NonSynchronizing));
        Assert.Equal("A0001 X {2+}\r\nab\r\n", s2.SentText);
        _ = s;
    }

    [Fact]
    public void ServerRefusingALiteralReturnsFalseAndKeepsTheCompletion()
    {
        (ProtocolConnection c, ScriptedStream s) = ClientTestSupport.Scripted("A0001 NO [TOOBIG] too large\r\n");
        var imap = new ImapRawConnection(c);
        Assert.False(imap.Send(ImapCommand.Create("APPEND").Mailbox("INBOX").Literal(new byte[100])));
        Assert.DoesNotContain("\0", s.SentText, StringComparison.Ordinal);
        ImapResponse r = imap.ReadResponse();
        Assert.Equal(ImapStatus.No, r.Status);
        Assert.Equal("TOOBIG", r.Code!.Name);
    }

    [Fact]
    public async Task AuthenticatePlainUsesTheInitialResponseWhenAdvertised()
    {
        var log = new StringWriter();
        (ProtocolConnection c, ScriptedStream s) = ClientTestSupport.Scripted("* CAPABILITY IMAP4rev1 SASL-IR AUTH=PLAIN\r\nA0001 OK ok\r\nA0002 OK [CAPABILITY IMAP4rev1 IDLE] auth ok\r\n",
            new ProtocolConnectionOptions { AllowInsecureAuthentication = true, Trace = new TextWriterProtocolTrace(log) });
        var imap = new ImapRawConnection(c);
        await imap.ExecuteAsync(ImapCommand.Create("CAPABILITY"), null, null, TestContext.Current.CancellationToken);
        ImapCommandResult r = await imap.AuthenticateAsync(new SaslPlain("tim", "tanstaaftanstaaf"), TestContext.Current.CancellationToken);
        Assert.True(r.IsOk);
        Assert.EndsWith("A0002 AUTHENTICATE PLAIN AHRpbQB0YW5zdGFhZnRhbnN0YWFm\r\n", s.SentText, StringComparison.Ordinal);
        Assert.DoesNotContain("AHRpbQB0", log.ToString());
        Assert.True(imap.HasCapability("IDLE"));
        Assert.False(imap.HasCapability("SASL-IR"));
    }

    [Fact]
    public void AuthenticateWithoutSaslIrAnswersTheContinuation()
    {
        (ProtocolConnection c, ScriptedStream s) = ClientTestSupport.Scripted("+ \r\nA0001 OK done\r\n", new ProtocolConnectionOptions { AllowInsecureAuthentication = true });
        var imap = new ImapRawConnection(c);
        Assert.True(imap.Authenticate(new SaslPlain("a", "b")).IsOk);
        Assert.Equal("A0001 AUTHENTICATE PLAIN\r\nAGEAYg==\r\n", s.SentText);
    }

    [Fact]
    public void Utf8EnabledSendsMailboxNamesAsUtf8()
    {
        (ProtocolConnection c, ScriptedStream s) = ClientTestSupport.Scripted("* ENABLED UTF8=ACCEPT\r\nA0001 OK ok\r\nA0002 OK ok\r\n");
        var imap = new ImapRawConnection(c);
        imap.Execute(ImapCommand.Create("ENABLE").Atom("UTF8=ACCEPT"));
        Assert.True(imap.Utf8Enabled);
        imap.Execute(ImapCommand.Create("SELECT").Mailbox("Entwürfe"));
        Assert.EndsWith("A0002 SELECT \"Entwürfe\"\r\n", s.SentText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdleStartAndDone()
    {
        (ProtocolConnection c, ScriptedStream s) = ClientTestSupport.Scripted("+ idling\r\n* 4 EXISTS\r\nA0001 OK IDLE terminated\r\n");
        var imap = new ImapRawConnection(c);
        string? tag = await imap.IdleStartAsync(TestContext.Current.CancellationToken);
        Assert.Equal("A0001", tag);
        ImapResponse push = await imap.ReadResponseAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal(4, push.Number);
        ImapCommandResult done = await imap.IdleDoneAsync(tag!, TestContext.Current.CancellationToken);
        Assert.True(done.IsOk);
        Assert.Equal("A0001 IDLE\r\nDONE\r\n", s.SentText);
    }

    [Fact]
    public async Task StreamedAppendLiteralIsSentFromAStream()
    {
        (ProtocolConnection c, ScriptedStream s) = ClientTestSupport.Scripted("+ go\r\nA0001 OK done\r\n");
        var imap = new ImapRawConnection(c);
        using var body = new MemoryStream(new byte[200_000]);
        ImapCommandResult r = await imap.ExecuteAsync(ImapCommand.Create("APPEND").Mailbox("INBOX").Literal(body, body.Length), null, null, TestContext.Current.CancellationToken);
        Assert.True(r.IsOk);
        Assert.Equal(200_000 + "A0001 APPEND INBOX {200000}\r\n\r\n".Length, s.Sent.Length);
    }
}
