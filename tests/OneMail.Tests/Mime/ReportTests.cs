using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Mime.Reading;
using RecursiveAI.OneMail.Mime.Reports;
using static RecursiveAI.OneMail.Tests.Mime.T;

namespace RecursiveAI.OneMail.Tests.Mime;

public class ReportFieldTests
{
    private static List<ReportFieldBlock> Parse(string body, List<MimeDefect>? defects = null) =>
        ReportFieldParser.Parse(B(body), defects);

    [Fact]
    public void BlocksAreSeparatedByEmptyLinesAndFoldsAreUnfolded()
    {
        List<ReportFieldBlock> blocks = Parse("A: 1\r\nB: two\r\n  words\r\n\r\n\r\nC: 3\r\n");
        Assert.Equal(2, blocks.Count);
        Assert.Equal("two  words", blocks[0].Get("b"));
        Assert.Equal("3", blocks[1].Get("C"));
        Assert.Equal(0, blocks[0].Offset);
    }

    [Fact]
    public void LfAndCrlfBothEndLines()
    {
        Assert.Equal(2, Parse("A: 1\nB: 2\n\nC: 3").Count);
        Assert.Equal(2, Parse("A: 1\r\nB: 2\r\n\r\nC: 3").Count);
    }

    [Fact]
    public void AWhiteSpaceOnlyLineIsAFoldNotABlankLine()
    {
        // The printed delayed-DSN example of RFC 3464 has such a line; header syntax makes it a continuation.
        List<ReportFieldBlock> blocks = Parse("Final-Recipient: rfc822;a@x\r\n  \r\nAction: delayed\r\n");
        Assert.Single(blocks);
        Assert.Equal("delayed", blocks[0].Get("Action"));
        Assert.Equal("rfc822;a@x", blocks[0].Get("Final-Recipient"));
    }

    [Fact]
    public void ExtraBlankLinesAreSkippedAndReportedOncePerRun()
    {
        var d = new List<MimeDefect>();
        List<ReportFieldBlock> blocks = Parse("\r\n\r\nA: 1\r\n\r\n\r\n\r\nB: 2\r\n\r\n", d);
        Assert.Equal(2, blocks.Count);
        // the first redundant blank line of each run: the leading run at 0 and the run after the first block at 12
        Assert.Equal([0L, 12L], d.Where(x => x.Kind == MimeDefectKind.AmbiguousReportBlankLines && x.IsAmbiguity).Select(x => x.Offset));
    }

    [Fact]
    public void ASingleBlockBodyThatStartsWithABlankLineHasAnEmptyFirstBlock()
    {
        var d = new List<MimeDefect>();
        List<ReportFieldBlock> blocks = ReportFieldParser.Parse(B("\r\nA: 1\r\n"), d, singleBlock: true);
        Assert.Equal(2, blocks.Count);
        Assert.Empty(blocks[0]);
        Assert.Equal("1", blocks[1].Get("A"));
        Assert.Empty(d);
        FeedbackReport r = FeedbackReport.Parse(B("\r\nFeedback-Type: abuse\r\nUser-Agent: X\r\nVersion: 1\r\n"));
        Assert.Equal(FeedbackKind.Unknown, r.Kind);
        Assert.Contains(r.Defects, x => x.Kind == MimeDefectKind.ReportMissingGroup);
        Assert.Contains(r.Defects, x => x.Kind == MimeDefectKind.AmbiguousReportExtraBlocks);
    }

    [Fact]
    public void ALineWithoutColonIsSkippedAndReported()
    {
        var d = new List<MimeDefect>();
        List<ReportFieldBlock> blocks = Parse("A: 1\r\nnot a field\r\nB: 2\r\n", d);
        Assert.Single(blocks);
        Assert.Equal(2, blocks[0].Count);
        MimeDefect x = Assert.Single(d, y => y.Kind == MimeDefectKind.ReportMalformedLine);
        Assert.Equal(6, x.Offset);
    }

    [Fact]
    public void AFoldBeforeAnyFieldIsReported()
    {
        var d = new List<MimeDefect>();
        Parse(" lonely\r\nA: 1\r\n", d);
        Assert.Contains(d, y => y.Kind == MimeDefectKind.ReportMalformedLine && y.Offset == 0);
    }

    [Fact]
    public void WhitespaceBeforeTheColonIsReportedAndTrimmed()
    {
        var d = new List<MimeDefect>();
        List<ReportFieldBlock> blocks = Parse("Action : failed\r\n", d);
        Assert.Equal("Action", blocks[0][0].Name);
        Assert.Contains(d, y => y.Kind == MimeDefectKind.WhitespaceBeforeHeaderColon);
    }

    [Fact]
    public void AnInvalidNameIsKeptAndReported()
    {
        var d = new List<MimeDefect>();
        List<ReportFieldBlock> blocks = Parse("X Bad: v\r\n", d);
        Assert.Equal("X Bad", blocks[0][0].Name);
        Assert.Contains(d, y => y.Kind == MimeDefectKind.InvalidHeaderName);
    }

    [Fact]
    public void TooManyFieldsStopsAtTheLimit()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < ReportFieldParser.MaxFields + 5; i++)
        {
            sb.Append("X: 1\n");
        }

        var d = new List<MimeDefect>();
        List<ReportFieldBlock> blocks = Parse(sb.ToString(), d);
        Assert.Equal(ReportFieldParser.MaxFields, blocks[0].Count);
        Assert.Contains(d, y => y.Kind == MimeDefectKind.ReportLimitExceeded);
    }

    [Fact]
    public void NonAsciiValuesReadAsUtf8WhenValidElseWindows1252()
    {
        byte[] utf8 = Encoding.UTF8.GetBytes("D: caf\u00e9\r\n");
        Assert.Equal("caf\u00e9", ReportFieldParser.Parse(utf8)[0].Get("D"));
        Assert.Equal("caf\u00e9", ReportFieldParser.Parse(new byte[] { (byte)'D', (byte)':', (byte)' ', (byte)'c', (byte)'a', (byte)'f', 0xE9 })[0].Get("D"));
    }

    [Fact]
    public void CreatedBlocksWriteFoldedAndRefuseLineBreaks()
    {
        var b = new ReportFieldBlock();
        b.Add("Diagnostic-Code", "smtp; " + string.Join(' ', Enumerable.Repeat("word", 30)));
        string text = Encoding.UTF8.GetString(b.ToBytes());
        Assert.All(text.Split("\r\n"), l => Assert.True(l.Length <= 78));
        Assert.EndsWith("\r\n\r\n", text, StringComparison.Ordinal);
        Assert.Equal(b.Get("Diagnostic-Code"), ReportFieldParser.Parse(b.ToBytes())[0].Get("Diagnostic-Code"));
        Assert.Throws<ArgumentException>(() => b.Add("X", "a\r\nb"));
        Assert.Throws<ArgumentException>(() => b.Add("X:Y", "a"));
    }
}

public class DeliveryStatusTests
{
    private static readonly string Ms = "Reporting-MTA: dns; mx.example.org\r\nArrival-Date: Sat, 2 Jul 1994 17:10:28 -0400\r\n\r\n";

    private static DeliveryStatusReport P(string text) => DeliveryStatusReport.Parse(B(text));

    [Fact]
    public void ReadsPerMessageAndPerRecipientFields()
    {
        DeliveryStatusReport r = P(Ms + "Original-Recipient: rfc822;list@example.org\r\nFinal-Recipient: rfc822;a+2Bb@example.org\r\nAction: Failed\r\nStatus: 5.1.1 (bad mailbox)\r\n" +
            "Remote-MTA: DNS; mail.example.org\r\nDiagnostic-Code: smtp; 550 5.1.1\r\n user unknown\r\nLast-Attempt-Date: Sat, 2 Jul 1994 17:15:49 -0400\r\nFinal-Log-ID: L1\r\n\r\n" +
            "Final-Recipient: rfc822;b@example.org\r\nAction: delayed\r\nStatus: 4.4.1\r\nWill-Retry-Until: Sun, 3 Jul 1994 17:15:49 -0400\r\n");
        Assert.Empty(r.Defects);
        Assert.Equal(new DsnTypedValue("dns", "mx.example.org"), r.ReportingMta);
        Assert.Equal(1994, r.ArrivalDate!.Value.Value.Year);
        Assert.Equal(2, r.Recipients.Count);
        RecipientStatus a = r.Recipients[0];
        Assert.Equal("a+2Bb@example.org", a.FinalRecipient!.Value.Value);
        Assert.Equal("a+b@example.org", a.FinalRecipient.Value.DecodeAddress());
        Assert.Equal("rfc822", a.OriginalRecipient!.Value.Type);
        Assert.Equal(DsnAction.Failed, a.Action);
        Assert.Equal(new EnhancedStatusCode(5, 1, 1), a.Status);
        Assert.Equal("dns", a.RemoteMta!.Value.Type);
        Assert.Equal("550 5.1.1 user unknown", a.DiagnosticCode!.Value.Value);
        Assert.NotNull(a.LastAttemptDate);
        Assert.Equal("L1", a.FinalLogId);
        Assert.Equal(DsnAction.Delayed, r.Recipients[1].Action);
        Assert.NotNull(r.Recipients[1].WillRetryUntil);
    }

    [Fact]
    public void GlobalAddressesDecodeBackslashEscapes()
    {
        Assert.Equal("a+b@example.org", new DsnTypedValue("utf-8", "a\\x{2B}b@example.org").DecodeAddress());
        Assert.Equal("\u7528\u6237@example.org", new DsnTypedValue("utf-8", "\u7528\u6237@example.org").DecodeAddress());
        Assert.Equal("a\\x{zz}b", new DsnTypedValue("utf-8", "a\\x{zz}b").DecodeAddress());
        Assert.Equal("a+Z", new DsnTypedValue("rfc822", "a+Z").DecodeAddress());
    }

    [Theory]
    [InlineData("5.1.1", 5, 1, 1, true)]
    [InlineData(" 4.0.0 (comment)", 4, 0, 0, true)]
    [InlineData("2.1.5 expanded", 2, 1, 5, true)]
    [InlineData("5.1", 0, 0, 0, false)]
    [InlineData("5.1.1.1", 0, 0, 0, false)]
    [InlineData("5.1.1234", 0, 0, 0, false)]
    [InlineData("x.1.1", 0, 0, 0, false)]
    [InlineData("", 0, 0, 0, false)]
    public void EnhancedStatusCodes(string text, int c, int s, int d, bool ok)
    {
        Assert.Equal(ok, EnhancedStatusCode.TryParse(text, out EnhancedStatusCode code));
        if (ok)
        {
            Assert.Equal(new EnhancedStatusCode(c, s, d), code);
            Assert.True(code.IsDefinedClass);
        }
    }

    [Fact]
    public void ACommentAfterTheActionAndStatusIsIgnored()
    {
        DeliveryStatusReport r = P(Ms + "Final-Recipient: rfc822;a@x\r\nAction: failed (really)\r\nStatus: (x) 5.0.0\r\n");
        Assert.Equal(DsnAction.Failed, r.Recipients[0].Action);
        Assert.Equal(new EnhancedStatusCode(5, 0, 0), r.Recipients[0].Status);
    }

    private static List<MimeDefectKind> Kinds(string text) => P(text).Defects.Select(d => d.Kind).ToList();

    [Fact]
    public void ReportsAMissingRecipientBlock() => Assert.Contains(MimeDefectKind.ReportMissingGroup, Kinds("Reporting-MTA: dns; x\r\n"));

    [Fact]
    public void ReportsAnEmptyBody() => Assert.Contains(MimeDefectKind.ReportMissingGroup, Kinds(string.Empty));

    [Fact]
    public void ReportsMissingRequiredFields()
    {
        DeliveryStatusReport r = P("Arrival-Date: Sat, 2 Jul 1994 17:10:28 -0400\r\n\r\nFinal-Recipient: rfc822;a@x\r\n");
        Assert.Equal(3, r.Defects.Count(d => d.Kind == MimeDefectKind.ReportMissingField)); // Reporting-MTA, Action, Status
    }

    [Fact]
    public void ReportsValuesOfTheWrongForm()
    {
        DeliveryStatusReport r = P(Ms + "Final-Recipient: a@x\r\nAction: exploded\r\nStatus: 55.1\r\nLast-Attempt-Date: yesterday\r\n");
        Assert.Equal(4, r.Defects.Count(d => d.Kind == MimeDefectKind.ReportInvalidFieldValue));
        Assert.Equal(DsnAction.Unknown, r.Recipients[0].Action);
        Assert.Null(r.Recipients[0].Status);
        Assert.Equal("a@x", r.Recipients[0].FinalRecipient!.Value.Value); // still readable
    }

    [Fact]
    public void ARepeatedSingleValuedFieldIsAmbiguousAndTheFirstWins()
    {
        DeliveryStatusReport r = P(Ms + "Final-Recipient: rfc822;a@x\r\nAction: failed\r\nAction: delayed\r\nStatus: 5.0.0\r\n");
        Assert.Contains(r.Defects, d => d.Kind == MimeDefectKind.AmbiguousReportDuplicateField && d.IsAmbiguity);
        Assert.Equal(DsnAction.Failed, r.Recipients[0].Action);
    }

    [Fact]
    public void ExtensionFieldsMayRepeat()
    {
        DeliveryStatusReport r = P(Ms + "Final-Recipient: rfc822;a@x\r\nAction: failed\r\nStatus: 5.0.0\r\nX-A: 1\r\nX-A: 2\r\n");
        Assert.Empty(r.Defects);
        Assert.Equal(["1", "2"], r.Recipients[0].Fields.GetAll("x-a"));
    }

    [Fact]
    public void BuiltReportsWriteAndReadBack()
    {
        var pm = new ReportFieldBlock();
        pm.Add("Reporting-MTA", "dns; mx.example.org");
        var r = new DeliveryStatusReport(pm, [
            RecipientStatus.Create("a+b@example.org", DsnAction.Failed, new EnhancedStatusCode(5, 1, 1), "smtp; 550 5.1.1 no such user", "dns; mail.example.org", "list@example.org"),
            RecipientStatus.Create("c@example.org", DsnAction.Delayed, new EnhancedStatusCode(4, 4, 1)),
        ]);
        Assert.False(r.RequiresGlobal);
        DeliveryStatusReport back = DeliveryStatusReport.Parse(r.ToBytes());
        Assert.Empty(back.Defects);
        Assert.Equal("a+2Bb@example.org", back.Recipients[0].FinalRecipient!.Value.Value);
        Assert.Equal("a+b@example.org", back.Recipients[0].FinalRecipient!.Value.DecodeAddress());
        Assert.Equal(new EnhancedStatusCode(4, 4, 1), back.Recipients[1].Status);
        Assert.Equal(r.ToBytes(), back.ToBytes());
    }

    [Fact]
    public void NonAsciiAddressesNeedTheGlobalMediaType()
    {
        var pm = new ReportFieldBlock();
        pm.Add("Reporting-MTA", "dns; mx.example.org");
        var r = new DeliveryStatusReport(pm, [RecipientStatus.Create("\u7528\u6237@example.org", DsnAction.Failed, new EnhancedStatusCode(5, 1, 1))]);
        Assert.True(r.RequiresGlobal);
        MimeMultipart m = MimeReport.CreateDeliveryStatus("text", r);
        Assert.Equal("message/global-delivery-status", m.Children[1].MediaType);
        MimeReport view = MimeReport.TryCreate(MimeEntity.Parse(m.ToArray()))!;
        Assert.Equal(ReportKind.DeliveryStatus, view.Kind);
        Assert.Equal("utf-8", view.GetDeliveryStatus()!.Recipients[0].FinalRecipient!.Value.Type);
        Assert.Equal("\u7528\u6237@example.org", view.GetDeliveryStatus()!.Recipients[0].FinalRecipient!.Value.DecodeAddress());
    }
}

public class DispositionAndFeedbackTests
{
    [Theory]
    [InlineData("manual-action/MDN-sent-manually; displayed", "manual-action", "mdn-sent-manually", MdnDispositionType.Displayed, "")]
    [InlineData("automatic-action/MDN-sent-automatically; processed/error,warning", "automatic-action", "mdn-sent-automatically", MdnDispositionType.Processed, "error,warning")]
    [InlineData("automatic-action/MDN-sent-automatically; deleted/expired (note)", "automatic-action", "mdn-sent-automatically", MdnDispositionType.Deleted, "expired")]
    [InlineData("MANUAL-ACTION/MDN-SENT-MANUALLY; DENIED", "manual-action", "mdn-sent-manually", MdnDispositionType.Denied, "")]
    public void ParsesDisposition(string text, string action, string sending, MdnDispositionType type, string modifiers)
    {
        Assert.True(MdnDisposition.TryParse(text, out MdnDisposition? d));
        Assert.Equal(action, d!.ActionMode);
        Assert.Equal(sending, d.SendingMode);
        Assert.Equal(type, d.Type);
        Assert.Equal(modifiers, string.Join(',', d.Modifiers));
    }

    [Theory]
    [InlineData("whatever")]
    [InlineData("manual-action; displayed")]
    [InlineData("manual-action/MDN-sent-manually;")]
    [InlineData("manual-action/MDN-sent-manually; sulking")]
    [InlineData("")]
    public void RefusesBadDisposition(string text) => Assert.False(MdnDisposition.TryParse(text, out _));

    [Fact]
    public void ReadsANotificationAndTheMessageId()
    {
        DispositionNotification n = DispositionNotification.Parse(B("Reporting-UA: pc; Foomail 1\r\nFinal-Recipient: rfc822;j@example.com\r\nOriginal-Message-ID: <id@example.org>\r\nDisposition: manual-action/MDN-sent-manually; displayed\r\nError: oops\r\n"));
        Assert.Empty(n.Defects);
        Assert.Equal("pc; Foomail 1", n.ReportingUa);
        Assert.Equal("id@example.org", n.OriginalMessageId);
        Assert.Equal(MdnDispositionType.Displayed, n.Disposition!.Type);
        Assert.Equal("oops", n.Fields.Get("Error"));
    }

    [Fact]
    public void ANotificationDefects()
    {
        Assert.Contains(DispositionNotification.Parse(B("Final-Recipient: rfc822;a@x\r\n")).Defects, d => d.Kind == MimeDefectKind.ReportMissingField);
        Assert.Contains(DispositionNotification.Parse(B("Final-Recipient: rfc822;a@x\r\nDisposition: nonsense\r\n")).Defects, d => d.Kind == MimeDefectKind.ReportInvalidFieldValue);
        Assert.Contains(DispositionNotification.Parse(B("")).Defects, d => d.Kind == MimeDefectKind.ReportMissingGroup);
        DispositionNotification extra = DispositionNotification.Parse(B("Final-Recipient: rfc822;a@x\r\nDisposition: manual-action/MDN-sent-manually; displayed\r\n\r\nFinal-Recipient: rfc822;b@x\r\n"));
        Assert.Contains(extra.Defects, d => d.Kind == MimeDefectKind.AmbiguousReportExtraBlocks && d.IsAmbiguity);
        Assert.Single(extra.ExtraBlocks);
        Assert.Contains(DispositionNotification.Parse(B("Final-Recipient: rfc822;a@x\r\nDisposition: manual-action/MDN-sent-manually; displayed\r\nDisposition: manual-action/MDN-sent-manually; failed\r\n")).Defects, d => d.Kind == MimeDefectKind.AmbiguousReportDuplicateField);
    }

    [Fact]
    public void BuiltNotificationRoundTrips()
    {
        var n = DispositionNotification.Create("pc; Foomail 1", "j@example.com", "<id@example.org>", new MdnDisposition("automatic-action", "MDN-sent-automatically", MdnDispositionType.Processed, "processed", ["error"]));
        DispositionNotification back = DispositionNotification.Parse(n.ToBytes());
        Assert.Empty(back.Defects);
        Assert.Equal("id@example.org", back.OriginalMessageId);
        Assert.Equal(["error"], back.Disposition!.Modifiers);
    }

    private static FeedbackReport F(string text) => FeedbackReport.Parse(B(text));

    [Fact]
    public void ReadsAFeedbackReport()
    {
        FeedbackReport r = F("Feedback-Type: Abuse\r\nUser-Agent: Gen/1\r\nVersion: 1\r\nOriginal-Mail-From: <s@example.net>\r\nOriginal-Rcpt-To: <a@x>\r\nOriginal-Rcpt-To: <b@x>\r\n" +
            "Arrival-Date: Thu, 8 Mar 2005 14:00:00 -0400\r\nReporting-MTA: dns; mail.example.com\r\nSource-IP: 192.0.2.1 (host)\r\nIncidents: 3\r\nReported-Domain: example.net\r\nReported-URI: http://example.net/\r\n" +
            "Removal-Recipient: u@x\r\nAuthentication-Results: mx; spf=fail\r\nAuth-Failure: Signature\r\nDelivery-Result: delivered\r\nDKIM-Domain: example.net\r\nDKIM-Selector: s1\r\nDKIM-Identity: @example.net\r\n");
        Assert.Empty(r.Defects);
        Assert.Equal(FeedbackKind.Abuse, r.Kind);
        Assert.Equal("abuse", r.FeedbackType);
        Assert.Equal("Gen/1", r.UserAgent);
        Assert.Equal(1, r.Version);
        Assert.Equal(["<a@x>", "<b@x>"], r.OriginalRcptTo);
        Assert.Equal(IPAddress.Parse("192.0.2.1"), r.SourceIp);
        Assert.Equal(3, r.Incidents);
        Assert.Equal("dns", r.ReportingMta!.Value.Type);
        Assert.Equal(2005, r.ArrivalDate!.Value.Value.Year);
        Assert.Single(r.AuthenticationResults);
        Assert.Equal("signature", r.AuthFailure);
        Assert.Equal("delivered", r.DeliveryResult);
        Assert.Equal("example.net", r.DkimDomain);
        Assert.Equal("s1", r.DkimSelector);
        Assert.Equal("@example.net", r.DkimIdentity);
        Assert.Equal(["u@x"], r.RemovalRecipients);
        Assert.Equal(["example.net"], r.ReportedDomains);
        Assert.Equal(["http://example.net/"], r.ReportedUris);
    }

    [Theory]
    [InlineData("User-Agent: X\r\nVersion: 1\r\n", MimeDefectKind.ReportMissingField)]
    [InlineData("Feedback-Type: abuse\r\nUser-Agent: X\r\n", MimeDefectKind.ReportMissingField)]
    [InlineData("Feedback-Type: nonsense\r\nUser-Agent: X\r\nVersion: 1\r\n", MimeDefectKind.ReportInvalidFieldValue)]
    [InlineData("Feedback-Type: abuse\r\nUser-Agent: X\r\nVersion: 2\r\n", MimeDefectKind.ReportInvalidFieldValue)]
    [InlineData("Feedback-Type: abuse\r\nUser-Agent: X\r\nVersion: 1\r\nSource-IP: nope\r\n", MimeDefectKind.ReportInvalidFieldValue)]
    [InlineData("Feedback-Type: abuse\r\nUser-Agent: X\r\nVersion: 1\r\nIncidents: many\r\n", MimeDefectKind.ReportInvalidFieldValue)]
    [InlineData("Feedback-Type: abuse\r\nUser-Agent: X\r\nVersion: 1\r\nArrival-Date: soon\r\n", MimeDefectKind.ReportInvalidFieldValue)]
    [InlineData("Feedback-Type: abuse\r\nUser-Agent: X\r\nVersion: 1\r\nReporting-MTA: bare\r\n", MimeDefectKind.ReportInvalidFieldValue)]
    [InlineData("Feedback-Type: abuse\r\nFeedback-Type: fraud\r\nUser-Agent: X\r\nVersion: 1\r\n", MimeDefectKind.AmbiguousReportDuplicateField)]
    [InlineData("Feedback-Type: abuse\r\nUser-Agent: X\r\nVersion: 1\r\n\r\nFeedback-Type: fraud\r\n", MimeDefectKind.AmbiguousReportExtraBlocks)]
    [InlineData("", MimeDefectKind.ReportMissingGroup)]
    public void FeedbackDefects(string body, MimeDefectKind kind) => Assert.Contains(F(body).Defects, d => d.Kind == kind);

    [Fact]
    public void FeedbackFirstDuplicateWins()
    {
        Assert.Equal(FeedbackKind.Abuse, F("Feedback-Type: abuse\r\nFeedback-Type: fraud\r\nUser-Agent: X\r\nVersion: 1\r\n").Kind);
    }

    [Fact]
    public void BuiltFeedbackReportRoundTripsInsideAMessage()
    {
        FeedbackReport r = FeedbackReport.Create("abuse", "Gen/1", IPAddress.Parse("2001:db8::1"), new DateTimeOffset(2005, 3, 8, 14, 0, 0, TimeSpan.FromHours(-4)));
        byte[] original = B("From: <s@example.net>\r\nSubject: x\r\n\r\nSpam\r\n");
        MimeMultipart m = MimeReport.CreateFeedbackReport("An abuse report.\r\n", r, original, originalHeadersOnly: true, boundary: "bb");
        MimeReport view = MimeReport.TryCreate(MimeEntity.Parse(m.ToArray()))!;
        Assert.Equal(ReportKind.Feedback, view.Kind);
        Assert.Empty(view.Defects);
        FeedbackReport back = view.GetFeedbackReport()!;
        Assert.Empty(back.Defects);
        Assert.Equal(IPAddress.Parse("2001:db8::1"), back.SourceIp);
        Assert.Equal(TimeSpan.FromHours(-4), back.ArrivalDate!.Value.Value.Offset);
        Assert.True(view.OriginalIsHeadersOnly);
        Assert.Equal("x", view.OriginalMessage!.Headers.GetFirst("Subject")!.Value);
        Assert.Null(view.GetDeliveryStatus());
    }
}

public class MimeReportTests
{
    private static string Dir([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "testdata", "reports"));

    private static MimeReport R(string file) => MimeReport.Find(MimeEntity.Parse(File.ReadAllBytes(Path.Combine(Dir(), file))))!;

    [Fact]
    public void ADeliveryStatusMessage()
    {
        MimeReport r = R("dsn-02-multi-recipient.eml");
        Assert.Equal(ReportKind.DeliveryStatus, r.Kind);
        Assert.Equal("delivery-status", r.ReportType);
        Assert.Empty(r.Defects);
        Assert.StartsWith("text/plain", r.HumanReadable!.MediaType, StringComparison.Ordinal);
        DeliveryStatusReport ds = r.GetDeliveryStatus()!;
        Assert.Equal(3, ds.Recipients.Count);
        Assert.Equal("550 'arathib@vnet.IBM.COM' is not a registered gateway user", ds.Recipients[0].DiagnosticCode!.Value.Value);
        Assert.False(r.OriginalIsHeadersOnly);
        Assert.Equal("hello", r.OriginalMessage!.Headers.GetFirst("Subject")!.Value);
        Assert.Null(r.GetFeedbackReport());
        Assert.Null(r.GetDispositionNotification());
    }

    [Fact]
    public void TheOriginalCanBeHeadersOnly()
    {
        MimeReport r = R("dsn-07-headers-only.eml");
        Assert.True(r.OriginalIsHeadersOnly);
        Assert.Equal("orig2@example.com", r.OriginalMessage!.GetMessageId());
        Assert.IsType<MimePart>(r.Original);
    }

    [Fact]
    public void ADeliveryStatusWithNoReturnedContent() => Assert.Null(R("dsn-03-gateway.eml").Original);

    [Fact]
    public void AnMdnAndAnArf()
    {
        Assert.Equal(MdnDispositionType.Displayed, R("mdn-01-displayed.eml").GetDispositionNotification()!.Disposition!.Type);
        Assert.Equal(FeedbackKind.Abuse, R("arf-01-required.eml").GetFeedbackReport()!.Kind);
    }

    [Theory]
    [InlineData("dsn-22-no-report-type.eml")]
    [InlineData("dsn-23-wrong-second-part.eml")]
    [InlineData("dsn-24-one-part.eml")]
    public void StructureProblems(string file)
    {
        MimeReport r = R(file);
        Assert.Contains(r.Defects, d => d.Kind == MimeDefectKind.ReportStructureInvalid);
    }

    [Fact]
    public void WithoutReportTypeTheMachinePartDecidesTheKind()
    {
        MimeReport r = R("dsn-22-no-report-type.eml");
        Assert.Null(r.ReportType);
        Assert.Equal(ReportKind.DeliveryStatus, r.Kind);
        Assert.Single(r.GetDeliveryStatus()!.Recipients);
    }

    [Fact]
    public void TheCreatedStructureIsValid()
    {
        var ds = new DeliveryStatusReport(new ReportFieldBlock(), [RecipientStatus.Create("a@x.example", DsnAction.Failed, new EnhancedStatusCode(5, 1, 1))]);
        MimeMultipart m = MimeReport.CreateDeliveryStatus("Could not deliver.\n", ds, B("From: <s@x.example>\r\nSubject: q\r\n\r\nbody\r\n"), boundary: "bnd");
        Assert.Equal(3, m.Children.Count);
        Assert.IsType<MimeMessagePart>(m.Children[2]);
        MimeReport view = MimeReport.TryCreate(MimeEntity.Parse(m.ToArray()))!;
        Assert.Empty(view.Defects);
        Assert.False(view.OriginalIsHeadersOnly);
        Assert.Equal("q", view.OriginalMessage!.Headers.GetFirst("Subject")!.Value);
    }

    [Fact]
    public void NotAReportIsNull()
    {
        Assert.Null(MimeReport.TryCreate(MimeEntity.Parse(B("Content-Type: multipart/mixed; boundary=b\r\n\r\n--b\r\n\r\nx\r\n--b--\r\n"))));
        Assert.Null(MimeReport.Find(MimeEntity.Parse(B("Subject: x\r\n\r\ny\r\n"))));
    }

    public static IEnumerable<object[]> Files() =>
        Directory.EnumerateFiles(Dir(), "*.eml").Order(StringComparer.Ordinal).Select(f => new object[] { Path.GetFileName(f) });

    [Theory]
    [MemberData(nameof(Files))]
    public void EveryFixtureWritesBackByteIdenticalAndTheViewsNeverThrow(string file)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Dir(), file));
        MimeEntity e = MimeEntity.Parse(data);
        _ = MimeReport.Find(e)?.GetDeliveryStatus();
        _ = MimeReport.Find(e)?.GetDispositionNotification();
        _ = MimeReport.Find(e)?.GetFeedbackReport();
        _ = e.GetAuthenticationResults();
        _ = e.GetReceivedSpf();
        Assert.Equal(data, e.ToArray());
    }
}
