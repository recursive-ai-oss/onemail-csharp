using System.Security.Cryptography;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Pst.Messaging;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Tests.Pst;

public class PstMessagingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<NdbFormat> Formats => NdbTests.Formats;

    private static readonly Guid Set1 = PstFixtures.Set1;

    internal static byte[] Data(int n, int seed) => PstFixtures.Data(n, seed);

    internal static FxFolder SampleTree() => PstFixtures.Sample();

    internal static PstStore Open(byte[] bytes)
    {
        Assert.True(PstStore.TryOpen(bytes, out PstStore? s, out IReadOnlyList<MapiDefect> why), string.Join("; ", why));
        return s!;
    }

    internal static byte[] Build(NdbFormat format, FxFolder? tree = null, byte crypt = 0) =>
        new PstFixtureBuilder(format).Build(tree ?? SampleTree(), compress: format == NdbFormat.Unicode4K, crypt: crypt).Bytes;

    [Theory, MemberData(nameof(Formats))]
    public void FolderTreeAndMessagesRead(NdbFormat format)
    {
        using PstStore s = Open(Build(format));
        Assert.Equal("Test Store", s.DisplayName);
        Assert.Equal(format, s.Format);
        Assert.NotNull(s.Root);
        var paths = s.EnumerateFolders(Ct).Select(f => f.Path).ToList();
        Assert.Equal(["", "Inbox", "Inbox/Deep", "Empty", "Calendar"], paths);
        Assert.Equal(["Hello world", "Second", "In deep", "Meeting"], s.EnumerateMessages(cancellationToken: Ct).Select(m => ((MapiMessage)m).Subject).ToList());
        PstFolder inbox = s.EnumerateFolders(Ct).Single(f => f.Name == "Inbox");
        Assert.Equal(["Rule"], inbox.EnumerateMessages(PstMessageSet.Associated, Ct).Select(m => m.Subject).ToList());
        Assert.True(inbox.EnumerateMessages(PstMessageSet.Associated, Ct).Single().IsAssociated);
        Assert.Equal(3, inbox.EnumerateMessages(PstMessageSet.All, Ct).Count());
        Assert.Empty(s.Defects);
        Assert.Empty(s.DatabaseDefects);
    }

    [Theory, MemberData(nameof(Formats))]
    public void MessageDetailsReadThroughMapi(NdbFormat format)
    {
        using PstStore s = Open(Build(format));
        PstMessage m = s.EnumerateMessages(cancellationToken: Ct).First();
        Assert.Equal("IPM.Note", m.MessageClass);
        Assert.Equal("Plain body", m.Body);
        Assert.Equal(["Ann", "Bob", "Cy"], m.Recipients.Select(r => r.DisplayName).ToList());
        Assert.Equal([MapiRecipientType.To, MapiRecipientType.Cc, MapiRecipientType.Bcc], m.Recipients.Select(r => r.RecipientType!.Value).ToList());
        Assert.Equal("ann@example.com", m.Recipients[0].SmtpAddress);

        // Named properties come back with the names the map gives their IDs.
        MapiProperty num = m.Properties.Get(new MapiNamedProperty(Set1, 0x8101))!;
        Assert.NotNull(num);
        Assert.Equal(42, num.AsInt32());
        MapiProperty str = m.Properties.Get(new MapiNamedProperty(Set1, "x-custom-name"))!;
        Assert.Equal("custom value", str.AsString(m.String8Encoding));

        Assert.Equal(4, m.Attachments.Count);
        Assert.Equal(["small.bin", "large.bin", "inline.png", "inner.msg"], m.Attachments.Select(a => a.FileName).ToList());
        Assert.Equal(Data(100, 1), ReadAll(m.Attachments[0].OpenData()!));
        using (Stream big = m.Attachments[1].OpenData()!)
        {
            Assert.Equal(70_000, big.Length);
            Assert.Equal(SHA256.HashData(Data(70_000, 2)), SHA256.HashData(big));
        }

        MapiMessage inner = m.Attachments[3].EmbeddedMessage!;
        Assert.NotNull(inner);
        Assert.Equal("Forwarded inner", inner.Subject);
        Assert.True(((PstMessage)inner).IsEmbedded);
        Assert.Equal("Inner Rcpt", inner.Recipients.Single().DisplayName);
        Assert.Equal("deep.txt", inner.Attachments.Single().FileName);
        Assert.Equal(Data(300, 3), ReadAll(inner.Attachments[0].OpenData()!));
        Assert.Empty(s.Defects);
    }

    private static byte[] ReadAll(Stream s)
    {
        using (s)
        {
            var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }

    [Theory, MemberData(nameof(Formats))]
    public void BagsEqualLtpEntries(NdbFormat format)
    {
        using PstStore s = Open(Build(format));
        PstChecks.Result r = PstChecks.PropertyExact(s, Ct);
        Assert.Empty(r.Differences);
        Assert.True(r.Objects > 20);
        Assert.True(r.Properties > 100);
    }

    [Fact]
    public void PermutedFileReadsTheSame()
    {
        using PstStore s = Open(Build(NdbFormat.Unicode, crypt: 1));
        Assert.Equal("Hello world", s.EnumerateMessages(cancellationToken: Ct).First().Subject);
        Assert.Empty(PstChecks.PropertyExact(s, Ct).Differences);
    }

    [Theory, MemberData(nameof(Formats))]
    public void ViewMapsTheMessage(NdbFormat format)
    {
        using var ms = new MemoryStream(Build(format));
        using MailContainer c = Mail.Open(ms);
        Assert.Equal(format == NdbFormat.Ansi ? MailFormat.Pst : MailFormat.Pst, c.Format);
        var items = c.Enumerate().ToList();
        Assert.Equal(4, items.Count);
        IMailItem i = items[0];
        Assert.Equal(MailItemKind.Message, i.Kind);
        Assert.Equal("Hello world", i.Subject);
        Assert.Equal("Sender <sender@example.com>", i.From[0].ToString());
        Assert.Equal(["ann@example.com"], i.ToRecipients.Select(a => a.Address).ToList());
        Assert.Equal(["bob@example.com"], i.Cc.Select(a => a.Address).ToList());
        Assert.Equal(["cy@example.com"], i.Bcc.Select(a => a.Address).ToList());
        Assert.Equal("Plain body", i.TextBody!.ReadText());
        Assert.Equal("<p>Hello <b>world</b></p>", i.HtmlBody!.ReadText());
        Assert.Equal("utf-8", i.HtmlBody.Charset);
        Assert.Equal(new DateTimeOffset(DateTime.FromFileTimeUtc(133_500_000_000_000_000L), TimeSpan.Zero), i.Sent!.Value.Value);
        Assert.Equal(MailDateSource.ClientSubmit, i.Sent.Value.Source);
        Assert.False(i.TryGetDate(MailDateSource.Delivery, out _));
        Assert.Equal(4, i.Attachments.Count);
        Assert.Equal(70_000, i.Attachments[1].Size);
        Assert.True(i.Attachments[2].IsInline);
        Assert.Equal("img1@example.com", i.Attachments[2].ContentId);
        Assert.False(i.Attachments[0].IsInline);
        IMailItem inner = i.Attachments[3].Embedded!;
        Assert.Equal("Forwarded inner", inner.Subject);
        Assert.Equal("inner body", inner.TextBody!.ReadText());
        Assert.Equal(MailItemKind.Appointment, items[3].Kind);
        Assert.Equal("Inbox", items[0].Provenance.Folder);
        Assert.Equal(0, items[0].Provenance.Index);
        Assert.Equal(3, items[3].Provenance.Index);
        Assert.Empty(i.Defects);
    }

    [Theory]
    [InlineData("IPM.Note", MailItemKind.Message)]
    [InlineData("IPM.Note.SMIME.MultipartSigned", MailItemKind.Message)]
    [InlineData("ipm.appointment", MailItemKind.Appointment)]
    [InlineData("IPM.Appointment.Occurrence", MailItemKind.Appointment)]
    [InlineData("IPM.Schedule.Meeting.Request", MailItemKind.Message)]
    [InlineData("IPM.TaskRequest", MailItemKind.Message)]
    [InlineData("IPM.Task", MailItemKind.Task)]
    [InlineData("IPM.Contact", MailItemKind.Contact)]
    [InlineData("IPM.DistList", MailItemKind.DistributionList)]
    [InlineData("IPM.StickyNote", MailItemKind.Note)]
    [InlineData("IPM.Activity", MailItemKind.Journal)]
    [InlineData("REPORT.IPM.Note.NDR", MailItemKind.Message)]
    [InlineData("IPM.Configuration.Foo", MailItemKind.Other)]
    [InlineData("IPM.Contacts", MailItemKind.Other)]
    [InlineData("", MailItemKind.Message)]
    public void KindFollowsTheMessageClass(string cls, MailItemKind kind) => Assert.Equal(kind, MapiMailItem.KindOf(cls));

    [Fact]
    public async Task AsyncEnumerationMatchesAndCancels()
    {
        using PstStore s = Open(Build(NdbFormat.Unicode));
        var subjects = new List<string?>();
        await foreach (PstMessage m in s.EnumerateMessagesAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            subjects.Add(m.Subject);
        }

        Assert.Equal(["Hello world", "Second", "In deep", "Meeting"], subjects);
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (PstMessage m in s.EnumerateMessagesAsync(cancellationToken: cts.Token))
            {
                cts.Cancel();
            }
        });
    }

    [Fact]
    public void ManyItemsAndDeepTrees()
    {
        FxFolder leaf = new() { Name = "leaf", Messages = [new FxMessage { Subject = "bottom" }] };
        FxFolder chain = leaf;
        for (int i = 0; i < 40; i++)
        {
            chain = new FxFolder { Name = "d" + i, Children = [chain] };
        }

        var many = new FxFolder { Name = "many", Messages = [.. Enumerable.Range(0, 600).Select(i => new FxMessage { Subject = "m" + i, Body = "b" + i })] };
        var root = new FxFolder { Name = "top", Children = [chain, many] };
        using PstStore s = Open(Build(NdbFormat.Unicode4K, root));
        PstWalkCounts c = s.ReadEverything(Ct);
        Assert.Equal(43, c.Folders);
        Assert.Equal(601, c.Messages);
        Assert.Empty(s.Defects);
        Assert.Equal("bottom", s.EnumerateMessages(cancellationToken: Ct).First().Subject);
    }

    [Fact]
    public void FolderLoopIsCutWithADefect()
    {
        var root = new FxFolder
        {
            Name = "top",
            Children = [new FxFolder { Name = "a", RawChildNids = [0x122], Messages = [new FxMessage { Subject = "keep" }] }],
        };
        using PstStore s = Open(Build(NdbFormat.Unicode, root));
        Assert.Equal(["", "a"], s.EnumerateFolders(Ct).Select(f => f.Path).ToList());
        Assert.Contains(s.Defects, d => d.Kind == MapiDefectKind.FolderLoop);
    }

    [Fact]
    public void ListedButMissingNodesAreSkipped()
    {
        var root = new FxFolder { Name = "top", RawChildNids = [(0x5000u << 5) | 2], Messages = [new FxMessage { Subject = "ok" }] };
        using PstStore s = Open(Build(NdbFormat.Unicode, root));
        Assert.Equal([""], s.EnumerateFolders(Ct).Select(f => f.Path).ToList());
        Assert.Equal(["ok"], s.EnumerateMessages(cancellationToken: Ct).Select(m => m.Subject).ToList());
        Assert.Contains(s.Defects, d => d.Kind == MapiDefectKind.NodeMissing);
    }

    [Fact]
    public void ArbitraryDamageNeverThrows()
    {
        byte[] good = Build(NdbFormat.Unicode);
        var rng = new Random(12345);
        int opened = 0;
        for (int round = 0; round < 250; round++)
        {
            byte[] bad = (byte[])good.Clone();
            for (int k = 0; k < 1 + (round % 6); k++)
            {
                bad[rng.Next(bad.Length)] = (byte)rng.Next(256);
            }

            if (round % 25 == 0) { Array.Resize(ref bad, rng.Next(bad.Length)); }
            if (!PstStore.TryOpen(bad, out PstStore? s, out _))
            {
                continue;
            }

            using (s)
            {
                opened++;
                _ = s!.ReadEverything(Ct);
                foreach (PstMessage m in s.EnumerateMessages(PstMessageSet.All, Ct))
                {
                    var view = new MapiMailItem(m, default);
                    _ = view.Subject;
                    _ = view.From;
                    _ = view.TextBody?.ReadText();
                    _ = view.HtmlBody?.ReadText();
                    foreach (IMailAttachment a in view.Attachments)
                    {
                        using Stream st = a.Open();
                        _ = st.ReadByte();
                    }

                    _ = view.Defects.Count();
                }
            }
        }

        Assert.True(opened > 50);
    }

    [Fact]
    public void WorkingSetStaysFlatAcrossRepeatedWalks()
    {
        var many = new FxFolder { Name = "many", Messages = [.. Enumerable.Range(0, 400).Select(i => new FxMessage { Subject = "m" + i, Body = "b" + i, Recipients = [new FxRecipient { Name = "r", Smtp = "r@x" }] })] };
        using PstStore s = Open(Build(NdbFormat.Unicode4K, new FxFolder { Name = "top", Children = [many] }));
        _ = s.ReadEverything(Ct);
        GC.Collect();
        long before = GC.GetTotalMemory(true);
        for (int i = 0; i < 5; i++)
        {
            _ = s.ReadEverything(Ct);
        }

        GC.Collect();
        long after = GC.GetTotalMemory(true);
        Assert.True(after - before < 512 * 1024, $"live heap grew by {after - before} bytes over five walks");
    }
}
