using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Pst.Messaging;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Tests.Pst;

/// <summary>The node database editor: allocation, B-tree edits, reference counts, zeroing and the commit.</summary>
public sealed class NdbEditorTests
{
    private static readonly DateTimeOffset Stamp = new(2024, 5, 6, 7, 8, 9, TimeSpan.Zero);

    internal static string TempFile() => Path.Combine(Path.GetTempPath(), "onemail-edit-" + Guid.NewGuid().ToString("N") + ".pst");

    internal static string Build(int messages, int attachments = 1, int folders = 1)
    {
        string path = TempFile();
        using PstBuilder b = PstBuilder.Create(path, new PstBuilderOptions { Timestamp = Stamp, StoreId = Guid.Parse("11111111-2222-3333-4444-555555555555") });
        for (int f = 0; f < folders; f++)
        {
            PstFolderWriter folder = b.IpmSubtree.CreateFolder("Folder " + f);
            for (int i = 0; i < messages; i++)
            {
                folder.AddMessage(PstBuilderTests.Message($"message {f}/{i}", 2, i % 3 == 0 ? attachments : 0, new string('x', 300 + (i * 17))));
            }
        }

        b.Complete();
        return path;
    }

    /// <summary>Every free 64-byte slot of every allocation map, as byte ranges.</summary>
    internal static IEnumerable<(long Ib, int Length)> FreeExtents(NdbFile file)
    {
        byte[] bits = new byte[496];
        for (long k = 0; k < file.AMapCount; k++)
        {
            Assert.True(file.TryReadAMap(k, bits, out _));
            long start = file.AMapOffset(k);
            for (int s = 0; s < 3968; s++)
            {
                if ((bits[s >> 3] & (0x80 >> (s & 7))) == 0)
                {
                    yield return (start + (s * 64L), 64);
                }
            }
        }
    }

    internal static long NonZeroFreeBytes(string path)
    {
        Assert.True(NdbFile.TryOpen(path, out NdbFile? file, out _));
        using NdbFile f = file!;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long bad = 0;
        byte[] buf = new byte[64];
        foreach ((long ib, int len) in FreeExtents(f))
        {
            if (ib + len > fs.Length)
            {
                continue;
            }

            fs.Position = ib;
            fs.ReadExactly(buf.AsSpan(0, len));
            if (buf.AsSpan(0, len).ContainsAnyExcept((byte)0))
            {
                bad += len;
            }
        }

        return bad;
    }

    internal static void AssertClean(string path)
    {
        Assert.True(NdbFile.TryOpen(path, out NdbFile? file, out _));
        using NdbFile f = file!;
        NdbAllocationSummary sum = f.ValidateAllocation();
        Assert.Equal(0, sum.InvalidCount);
        Assert.Equal(sum.FreeBytes, sum.HeaderFreeBytes);
        _ = f.ReadDensityList();
        Assert.Empty(f.Defects);
        // scan mode finds every block and page and the trees agree with it
        long blocks = 0;
        foreach (BlockEntry e in f.Blocks)
        {
            blocks++;
            Assert.True(f.TryReadBlock(e, new byte[8192], out _, out NdbCheck c));
            Assert.True(c.IsClean, $"block {e.Bid} fails a check");
        }

        Assert.True(blocks > 0);
        Assert.Empty(f.Defects);
    }

    private static void AssertEveryBlockAllocated(string path)
    {
        Assert.True(NdbFile.TryOpen(path, out NdbFile? file, out _));
        using NdbFile f = file!;
        foreach (BlockEntry b in f.Blocks)
        {
            int total = ((b.Size + 16 + 63) / 64) * 64;
            Assert.True(f.IsAllocated((long)b.Block.Ib, total), $"block {b.Bid} at 0x{b.Block.Ib:x} is free in the allocation maps");
        }
    }

    [Fact]
    public void FilesTheBuilderWritesMarkEveryBlockAllocatedWithTheFirstSlotInTheHighBit()
    {
        string path = Build(40, attachments: 1);
        try
        {
            AssertEveryBlockAllocated(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("testPST.pst")]
    [InlineData("testPST_variousBodyTypes.pst")]
    public void FilesOutlookWroteMarkEveryBlockAllocatedWithTheFirstSlotInTheHighBit(string name)
    {
        if (PstWritableCorpusTests.Find(name) is { } source)
        {
            AssertEveryBlockAllocated(source);
        }
    }

    [Fact]
    public void RemovingNodesFreesBlocksZeroesThemAndKeepsTheFileClean()
    {
        string path = Build(200, attachments: 2);
        try
        {
            AssertClean(path);
            long before;
            var gone = new List<Nid>();
            using (NdbEditor ed = NdbEditor.Open(path))
            {
                before = (long)ed.Committed.Header.Root.AMapFree;
                ed.Committed.Nodes.ToList().Where(n => n.Nid.Type == NidType.NormalMessage).Take(120).ToList().ForEach(n => gone.Add(n.Nid));
                foreach (Nid n in gone)
                {
                    Assert.True(ed.RemoveNode(n));
                }

                ed.Commit();
                Assert.True(ed.Committed.Header.Root.AMapFree > (ulong)before);
                foreach (Nid n in gone)
                {
                    Assert.False(ed.Committed.Nodes.TryGet(n, out _));
                }
            }

            AssertClean(path);
            Assert.Equal(0, NonZeroFreeBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OptingOutOfZeroingLeavesFreedBlocks()
    {
        string path = Build(100, attachments: 1);
        try
        {
            using (NdbEditor ed = NdbEditor.Open(path, new NdbEditorOptions { ZeroFreedSpace = false }))
            {
                foreach (NodeEntry n in ed.Committed.Nodes.Where(n => n.Nid.Type == NidType.NormalMessage).Take(50).ToList())
                {
                    ed.RemoveNode(n.Nid);
                }

                ed.Commit();
            }

            AssertClean(path);
            Assert.True(NonZeroFreeBytes(path) > 0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AllocationReusesFreedSpaceAndGrowsPastARangeBoundary()
    {
        string path = Build(50, attachments: 0);
        try
        {
            long length = new FileInfo(path).Length;
            using (NdbEditor ed = NdbEditor.Open(path))
            {
                // Write enough blocks to run out of the free space in the first range and open the next one.
                var nids = new List<Nid>();
                for (int i = 0; i < 60; i++)
                {
                    byte[] data = new byte[100_000];
                    new Random(i).NextBytes(data);
                    DataTreeResult t = ed.Blocks.WriteData(data);
                    Nid nid = ed.AllocateNid(NidType.NormalMessage);
                    ed.UpsertNode(new NodeEntry(nid, t.Root, default, default));
                    nids.Add(nid);
                }

                ed.Commit();
                Assert.True(new FileInfo(path).Length > length);
                Assert.True(ed.Committed.AMapCount >= 2);
            }

            AssertClean(path);
            using (NdbEditor ed = NdbEditor.Open(path))
            {
                // delete them all: the space comes back and the next write reuses it instead of growing the file again
                long grown = new FileInfo(path).Length;
                foreach (NodeEntry n in ed.Committed.Nodes.Where(n => n.Nid.Type == NidType.NormalMessage && n.Nid.Index >= 0x400 + 50).ToList())
                {
                    ed.RemoveNode(n.Nid);
                }

                ed.Commit();
                DataTreeResult t = ed.Blocks.WriteData(new byte[200_000]);
                ed.UpsertNode(new NodeEntry(ed.AllocateNid(NidType.NormalMessage), t.Root, default, default));
                ed.Commit();
                Assert.Equal(grown, new FileInfo(path).Length);
            }

            AssertClean(path);
            Assert.Equal(0, NonZeroFreeBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ManyNodesSplitAndMergeTheTrees()
    {
        string path = Build(10, attachments: 0);
        try
        {
            var added = new List<Nid>();
            using (NdbEditor ed = NdbEditor.Open(path))
            {
                for (int i = 0; i < 3000; i++)
                {
                    DataTreeResult t = ed.Blocks.WriteData(new byte[] { (byte)i, 1, 2, 3 });
                    Nid nid = ed.AllocateNid(NidType.NormalMessage);
                    ed.UpsertNode(new NodeEntry(nid, t.Root, default, default));
                    added.Add(nid);
                }

                ed.Commit();
                int count = 0;
                foreach (NodeEntry unused in ed.Committed.Nodes)
                {
                    count++;
                }

                Assert.True(count >= 3000);
            }

            AssertClean(path);
            using (NdbEditor ed = NdbEditor.Open(path))
            {
                var rnd = new Random(7);
                foreach (Nid n in added.OrderBy(_ => rnd.Next()).Take(2900))
                {
                    Assert.True(ed.RemoveNode(n));
                }

                ed.Commit();
            }

            AssertClean(path);
            Assert.Equal(0, NonZeroFreeBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ARolledBackChangeLeavesTheFileUntouched()
    {
        string path = Build(20, attachments: 1);
        try
        {
            byte[] before = File.ReadAllBytes(path);
            using (NdbEditor ed = NdbEditor.Open(path))
            {
                foreach (NodeEntry n in ed.Committed.Nodes.Where(n => n.Nid.Type == NidType.NormalMessage).Take(5).ToList())
                {
                    ed.RemoveNode(n.Nid);
                }

                ed.Rollback();
                Assert.False(ed.HasChanges);
            }

            byte[] after = File.ReadAllBytes(path);
            Assert.Equal(before.Length, after.Length);
            // the only differences are in free space (blocks written before the rollback would be; none were here)
            Assert.True(before.AsSpan().SequenceEqual(after));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnOpenEditorHoldsTheFileExclusively()
    {
        string path = Build(5);
        try
        {
            using NdbEditor ed = NdbEditor.Open(path);
            Assert.Throws<IOException>(() => NdbEditor.Open(path));
            Assert.Throws<IOException>(() => File.OpenRead(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
