using System.Text;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Pst.Ltp;
using RecursiveAI.OneMail.Pst.Messaging;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Tests.Pst;

/// <summary>The PST writer's file level and its harder paths: several allocation ranges, big heaps, big values, named properties, stopped builds.</summary>
public sealed class PstWriterTests
{
    private static readonly Guid Store = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static string Temp() => Path.Combine(Path.GetTempPath(), "onemail-pstw-" + Guid.NewGuid().ToString("N") + ".pst");

    private static PstBuilder Create(string path, bool addRequired = true) =>
        PstBuilder.Create(path, new PstBuilderOptions { Timestamp = new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.Zero), StoreId = Store, AddRequiredProperties = addRequired });

    private static PstStore Open(string path)
    {
        Assert.True(PstStore.TryOpen(path, out PstStore? s, out IReadOnlyList<MapiDefect> why), string.Join("; ", why));
        return s!;
    }

    private static void AssertClean(PstStore store)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Assert.Empty(store.Database.Defects);
        Assert.Empty(store.Defects);
        NdbAllocationSummary a = store.Database.ValidateAllocation(ct);
        Assert.Equal(0, a.InvalidCount);
        Assert.Equal(a.FreeBytes, a.HeaderFreeBytes);

        // Scan mode must find every block and page the trees list, and nothing else that passes its checks.
        var blocks = store.Database.Blocks.Select(e => (long)e.Block.Ib).ToHashSet();
        var scanned = store.Database.Scan(null, ct).Where(i => i.Kind == NdbScanKind.Block).Select(i => i.Ib).ToHashSet();
        Assert.Equal(blocks.Count, scanned.Count);
        Assert.True(blocks.SetEquals(scanned));
        Assert.Empty(PstChecks.PropertyExact(store, ct).Differences);
    }

    private static MapiProperty Prop(ushort id, MapiType type, byte[] bytes, MapiNamedProperty? name = null) => MapiProperty.Create(new MapiTag(id, type), bytes, name);

    private static MapiProperty Str(ushort id, string v) => Prop(id, MapiType.UnicodeString, Encoding.Unicode.GetBytes(v));

    [Fact]
    public void ManyMessagesSpanManyRangesAndReadBackClean()
    {
        string path = Temp();
        try
        {
            using (PstBuilder b = Create(path))
            {
                PstFolderWriter[] folders = [b.IpmSubtree.CreateFolder("A"), b.IpmSubtree.CreateFolder("B")];
                for (int i = 0; i < 6000; i++)
                {
                    MapiMessage m = PstBuilderTests.Message("subject " + i, recipients: 2, attachments: i % 50 == 0 ? 1 : 0, body: "body " + i);
                    folders[i % 2].AddMessage(m);
                }

                PstFileSummary s = b.Complete();
                Assert.True(s.AllocationMaps > 20);
                Assert.Equal(0x4400 + (s.AllocationMaps * 253_952), s.FileLength);
            }

            using PstStore store = Open(path);
            AssertClean(store);
            PstWalkCounts c = store.ReadEverything(TestContext.Current.CancellationToken);
            Assert.Equal(6000, c.Messages);
            Assert.Equal(12000, c.Recipients);
            Assert.Equal(120, c.Attachments);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ALargeFolderHasAHeapOfHundredsOfBlocksThatReadsBack()
    {
        // 30,000 subjects of about 120 UTF-16 bytes are a contents-table heap of well over 128 blocks: block 0, a block bitmap (block 8) and
        // a second window (block 136) are all written out of order, and the heap's data tree needs an XBLOCK.
        string path = Temp();
        try
        {
            using (PstBuilder b = Create(path))
            {
                PstFolderWriter big = b.IpmSubtree.CreateFolder("Big");
                for (int i = 0; i < 30_000; i++)
                {
                    var m = new MapiMessage();
                    m.Properties.Add(Str(0x001A, "IPM.Note"));
                    m.Properties.Add(Str(0x0037, "Subject " + i + " " + new string('x', 50)));
                    big.AddMessage(m);
                }

                b.Complete();
            }

            using PstStore store = Open(path);
            Assert.Empty(store.Database.Defects);
            PstFolder big2 = store.EnumerateFolders(TestContext.Current.CancellationToken).Single(f => f.Name == "Big");
            Nid[] ids = big2.EnumerateMessageIds(cancellationToken: TestContext.Current.CancellationToken).ToArray();
            Assert.Equal(30_000, ids.Length);
            Assert.Equal(30_000, big2.ContentCount);
            Assert.True(store.TryOpenMessage(ids[29_999], out PstMessage? last));
            Assert.Equal("Subject 29999 " + new string('x', 50), last!.Subject);
            Assert.Empty(store.Defects);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void NamedAndMultiValuedPropertiesKeepTheirNamesAndValues()
    {
        string path = Temp();
        var lid = new MapiNamedProperty(MapiPropertySets.Common, 0x8514);
        var named = new MapiNamedProperty(Guid.Parse("00020386-0000-0000-c000-000000000046"), "X-Custom-Header");
        try
        {
            var m = new MapiMessage();
            m.Properties.Add(Str(0x001A, "IPM.Note"));
            m.Properties.Add(Prop(0x8123, MapiType.Boolean, [1], lid));
            m.Properties.Add(Prop(0x8456, MapiType.UnicodeString, Encoding.Unicode.GetBytes("custom value"), named));
            m.Properties.Add(MapiProperty.CreateMulti(new MapiTag(0x3A00, MapiType.MultipleString), [Encoding.Unicode.GetBytes("one"), Encoding.Unicode.GetBytes("two"), Array.Empty<byte>()]));
            m.Properties.Add(MapiProperty.CreateMulti(new MapiTag(0x3A01, MapiType.MultipleInteger32), [BitConverter.GetBytes(7), BitConverter.GetBytes(-9)]));
            m.Properties.Add(Prop(0x8999, MapiType.Integer32, BitConverter.GetBytes(5)));   // a named ID with no name
            using (PstBuilder b = Create(path))
            {
                b.IpmSubtree.AddMessage(m);
                Assert.Contains(b.Defects, d => d.Kind == PstWriteDefectKind.NamedPropertyUnmapped);
                b.Complete();
            }

            using PstStore store = Open(path);
            AssertClean(store);
            PstMessage r = store.EnumerateMessages(cancellationToken: TestContext.Current.CancellationToken).Single();
            Assert.Equal(2, store.NamedPropertyCount);
            MapiProperty p1 = r.Properties.Single(p => p.Name == lid);
            Assert.True(p1.AsBoolean());
            Assert.Equal("custom value", r.Properties.Single(p => p.Name == named).AsString());
            MapiProperty mv = r.Properties.Get(0x3A00)!;
            Assert.Equal(3, mv.ValueCount);
            Assert.Equal(["one", "two", ""], Enumerable.Range(0, 3).Select(i => mv.AsString(null, null, i)).ToArray());
            Assert.Equal(-9, r.Properties.Get(0x3A01)!.AsInt32(1));
            Assert.DoesNotContain(r.Properties, p => p.Id == 0x8999 && p.Name is null && p.Tag.Type == (ushort)MapiType.Integer32 && p.AsInt32() == 5);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EmbeddedMessagesAndLargeLazyAttachmentsSurvive()
    {
        string path = Temp();
        byte[] data = new byte[10_000_000];
        new Random(5).NextBytes(data);
        try
        {
            var inner = PstBuilderTests.Message("inner", recipients: 1, attachments: 1);
            var outer = PstBuilderTests.Message("outer", recipients: 1);
            var emb = new MapiAttachment(outer);
            emb.Properties.Add(Prop(0x3705, MapiType.Integer32, BitConverter.GetBytes(5)));
            emb.SetEmbeddedMessage(inner);
            outer.AddAttachment(emb);
            var big = new MapiAttachment(outer);
            big.Properties.Add(Prop(0x3705, MapiType.Integer32, BitConverter.GetBytes(1)));
            big.Properties.Add(MapiProperty.CreateLazy(new MapiTag(0x3701, MapiType.Binary), new MapiStreamValue(() => new MemoryStream(data, writable: false), data.Length)));
            outer.AddAttachment(big);
            using (PstBuilder b = Create(path))
            {
                b.IpmSubtree.AddMessage(outer);
                b.Complete();
            }

            using PstStore store = Open(path);
            AssertClean(store);
            PstMessage r = store.EnumerateMessages(cancellationToken: TestContext.Current.CancellationToken).Single();
            Assert.Equal(2, r.Attachments.Count);
            MapiMessage e = r.Attachments[0].EmbeddedMessage!;
            Assert.Equal("inner", e.Subject);
            Assert.Single(e.Recipients);
            Assert.Single(e.Attachments);
            using Stream s = r.Attachments[1].OpenData()!;
            Assert.Equal(data.Length, s.Length);
            byte[] back = new byte[data.Length];
            s.ReadExactly(back);
            Assert.True(back.AsSpan().SequenceEqual(data));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RequiredPropertiesAreAddedAndReportedUnlessTurnedOff()
    {
        string path = Temp();
        try
        {
            var m = new MapiMessage();
            m.Properties.Add(Str(0x001A, "IPM.Note"));
            m.Properties.Add(Str(0x0037, "plain"));
            using (PstBuilder b = Create(path))
            {
                b.IpmSubtree.AddMessage(m);
                Assert.Equal(6, b.Defects.Count(d => d.Kind == PstWriteDefectKind.PropertyAdded));
                b.Complete();
            }

            using (PstStore s = Open(path))
            {
                PstMessage r = s.EnumerateMessages(cancellationToken: TestContext.Current.CancellationToken).Single();
                Assert.NotNull(r.Properties.Get(0x0E08));
                Assert.NotNull(r.Properties.Get(0x300B));
                Assert.Equal(16, r.Properties.Get(0x300B)!.GetByteLength());
            }

            File.Delete(path);
            using (PstBuilder b = Create(path, addRequired: false))
            {
                b.IpmSubtree.AddMessage(m);
                Assert.Empty(b.Defects);
                b.Complete();
            }

            using PstStore s2 = Open(path);
            Assert.Equal(2, s2.EnumerateMessages(cancellationToken: TestContext.Current.CancellationToken).Single().Properties.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SameInputsAndOptionsWriteTheSameBytes()
    {
        byte[] Build()
        {
            using var ms = new MemoryStream();
            using (PstBuilder b = PstBuilder.Create(ms, leaveOpen: true, new PstBuilderOptions { StoreId = Store, Timestamp = DateTimeOffset.UnixEpoch }))
            {
                PstFolderWriter f = b.IpmSubtree.CreateFolder("Inbox");
                for (int i = 0; i < 50; i++)
                {
                    f.AddMessage(PstBuilderTests.Message("m" + i, recipients: 3, attachments: i % 5 == 0 ? 1 : 0));
                }

                b.Complete();
            }

            return ms.ToArray();
        }

        Assert.True(Build().AsSpan().SequenceEqual(Build()));
    }

    [Fact]
    public void AStoppedBuildIsRefusedByEveryReader()
    {
        string path = Temp();
        try
        {
            using (PstBuilder b = Create(path))
            {
                for (int i = 0; i < 2000; i++)
                {
                    b.IpmSubtree.AddMessage(PstBuilderTests.Message("m" + i));
                }

                // disposed without Complete
            }

            Assert.True(new FileInfo(path).Length > 0x4400);
            Assert.False(PstStore.TryOpen(path, out _, out _));
            Assert.False(NdbFile.TryOpen(path, out _, out IReadOnlyList<NdbDefect> why));
            Assert.Contains(why, d => d.Kind == NdbDefectKind.BadMagic);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FoldersNestAndKeepTheirOrderAndAssociatedMessagesAreSeparate()
    {
        string path = Temp();
        try
        {
            using (PstBuilder b = Create(path))
            {
                PstFolderWriter f = b.IpmSubtree.GetOrCreatePath("Projects/2024/Spring");
                f.AddMessage(PstBuilderTests.Message("normal"));
                f.AddMessage(PstBuilderTests.Message("hidden"), associated: true);
                b.IpmSubtree.CreateFolder("Zed");
                b.IpmSubtree.CreateFolder("Alpha");
                Assert.Same(f, b.IpmSubtree.GetOrCreatePath("Projects/2024/Spring"));
                b.Complete();
            }

            using PstStore store = Open(path);
            AssertClean(store);
            PstFolder top = store.EnumerateFolders(TestContext.Current.CancellationToken).Single(x => x.Name == "Top of Personal Folders");
            Assert.Equal(["Deleted Items", "Projects", "Zed", "Alpha"], top.EnumerateSubfolders(TestContext.Current.CancellationToken).Select(x => x.Name!).ToArray());
            PstFolder spring = store.EnumerateFolders(TestContext.Current.CancellationToken).Single(x => x.Name == "Spring");
            Assert.Equal("Top of Personal Folders/Projects/2024/Spring", spring.Path);
            Assert.Equal(["normal"], spring.EnumerateMessages(PstMessageSet.Normal, TestContext.Current.CancellationToken).Select(x => x.Subject!).ToArray());
            Assert.Equal(["hidden"], spring.EnumerateMessages(PstMessageSet.Associated, TestContext.Current.CancellationToken).Select(x => x.Subject!).ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheBlockAndNodeTreesGrowDeepWhenTheyMust()
    {
        // 2,000 nodes cannot sit in 15-entry leaves under one page: the node tree gets index levels, and a small run length forces the spool and merge.
        var ms = new MemoryStream();
        using (var w = new PstFileWriter(ms, new PstFileWriterOptions { NodeRunLength = 64, CreateSpool = () => new MemoryStream() }, leaveOpen: true))
        {
            var rnd = new Random(3);
            uint[] nids = Enumerable.Range(1, 2000).Select(i => ((uint)i << 5) | 4).OrderBy(_ => rnd.Next()).ToArray();
            foreach (uint n in nids)
            {
                Bid bid = w.Blocks.WriteDataBlock(BitConverter.GetBytes(n));
                w.AddNode(new NodeEntry(new Nid(n), bid, default, default));
            }

            PstFileSummary s = w.Complete();
            Assert.Equal(2000, s.Nodes);
            Assert.Equal(2000, s.Blocks);
        }

        Assert.True(NdbFile.TryOpen(ms.ToArray(), out NdbFile? f, out IReadOnlyList<NdbDefect> why), string.Join("; ", why));
        using (f)
        {
            Assert.Empty(f!.Defects);
            Assert.Equal(2000, f.Nodes.Count());
            Assert.True(f.Nodes.TryGet(new Nid((777u << 5) | 4), out NodeEntry e));
            using NdbDataTree tree = f.OpenDataTree(e.Data)!;
            byte[] first = new byte[4];
            Assert.Equal(4, tree.Read(0, first));
            Assert.Equal((777u << 5) | 4, BitConverter.ToUInt32(first));
        }
    }
}
