using System.Buffers.Binary;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Tests.Pst;

public class NdbTests
{
    public static TheoryData<NdbFormat> Formats => new() { NdbFormat.Ansi, NdbFormat.Unicode, NdbFormat.Unicode4K };

    internal static byte[] Pattern(int n, int seed)
    {
        var b = new byte[n];
        var r = new Random(seed);
        r.NextBytes(b);
        return b;
    }

    internal static List<TestNode> SampleNodes(NdbFormat f, int max)
    {
        var big = Pattern((max * 2) + 777, 3);
        return
        [
            new TestNode { Nid = 0x21, Data = Pattern(100, 1) },
            new TestNode { Nid = 0x122, Data = [] },
            new TestNode { Nid = 0x8082, Data = big },
            new TestNode { Nid = 0x8003, Data = Pattern(40, 2), Subnodes = [new TestNode { Nid = 0x671, Data = Pattern(9, 5) }, new TestNode { Nid = 0x692, Data = Pattern(max + 5, 6) }] },
        ];
    }

    [Theory, MemberData(nameof(Formats))]
    public void ReadsWhatTheBuilderWrote(NdbFormat format)
    {
        int max = format == NdbFormat.Unicode4K ? 65536 - 24 : 8192 - (format == NdbFormat.Ansi ? 12 : 16);
        var nodes = SampleNodes(format, max);
        var built = NdbTestBuilder.Build(format, nodes);
        Assert.True(NdbFile.TryOpen(built.Bytes, out NdbFile? f, out _));
        using (f)
        {
            Assert.Equal(format, f!.Format);
            Assert.Empty(f.Defects);
            var seen = new List<NodeEntry>();
            foreach (NodeEntry n in f.Nodes) { seen.Add(n); }
            Assert.Equal(nodes.Select(n => n.Nid).Order(), seen.Select(n => n.Nid.Value));
            foreach (TestNode want in nodes)
            {
                Assert.True(f.Nodes.TryGet(new Nid(want.Nid), out NodeEntry e));
                AssertData(f, e.Data, want.Data);
                if (want.Subnodes.Count > 0)
                {
                    SubnodeTree? st = f.OpenSubnodes(e.Sub);
                    Assert.NotNull(st);
                    Assert.Equal(want.Subnodes.Count, st!.Entries.Count);
                    foreach (TestNode sub in want.Subnodes)
                    {
                        Assert.True(st.TryGet(new Nid(sub.Nid), out SubnodeEntry se));
                        AssertData(f, se.Data, sub.Data);
                    }
                }
            }

            Assert.False(f.Nodes.TryGet(new Nid(0x9999), out _));
            NdbAllocationSummary a = f.ValidateAllocation(TestContext.Current.CancellationToken);
            Assert.Equal(0, a.InvalidCount);
            Assert.Equal(a.FreeBytes, a.HeaderFreeBytes);
            Assert.NotNull(f.ReadDensityList());
            Assert.Empty(f.Defects);

            // Scan mode must find every block the BBT has, and every B-tree page.
            var scanBlocks = f.Scan(null, TestContext.Current.CancellationToken).Where(i => i.Kind == NdbScanKind.Block).Select(i => i.Ib).ToHashSet();
            foreach (long ib in built.BlockOffsets.Values) { Assert.Contains(ib, scanBlocks); }
            Assert.Equal(built.BlockOffsets.Count, scanBlocks.Count);
            var scanPages = f.Scan(null, TestContext.Current.CancellationToken).Where(i => i.Kind == NdbScanKind.Page).Select(i => i.Ib).ToHashSet();
            foreach (long ib in built.NbtPages.Concat(built.BbtPages)) { Assert.Contains(ib, scanPages); }
        }
    }

    private static void AssertData(NdbFile f, Bid bid, byte[] want)
    {
        using NdbDataTree? t = f.OpenDataTree(bid);
        Assert.NotNull(t);
        Assert.Equal(want.Length, t!.Length);
        var got = new byte[want.Length];
        Assert.Equal(want.Length, t.Read(0, got));
        Assert.True(want.AsSpan().SequenceEqual(got));
        if (want.Length > 10)
        {
            var mid = new byte[7];
            Assert.Equal(7, t.Read(want.Length - 9, mid));
            Assert.True(want.AsSpan(want.Length - 9, 7).SequenceEqual(mid));
        }
    }

    [Theory, MemberData(nameof(Formats))]
    public void SmallPagesForceIndexLevels(NdbFormat format)
    {
        var nodes = Enumerable.Range(1, 60).Select(i => new TestNode { Nid = (uint)(i * 32) + 4, Data = Pattern(10 + i, i) }).ToList();
        var built = NdbTestBuilder.Build(format, nodes, new TestBuild { LeafCapacity = 3 });
        Assert.True(built.NbtPages.Count > 20);
        Assert.True(NdbFile.TryOpen(built.Bytes, out NdbFile? f, out _));
        using (f)
        {
            Assert.Equal(60, f!.Nodes.Count());
            foreach (TestNode n in nodes)
            {
                Assert.True(f.Nodes.TryGet(new Nid(n.Nid), out NodeEntry e));
                AssertData(f, e.Data, n.Data);
            }

            // Seek lands on the first key at or after the one asked for.
            using var c = f.Nodes.CreateCursor();
            c.Seek(37 * 32);
            Assert.True(c.MoveNext());
            Assert.Equal(37u * 32 + 4, c.Current.Nid.Value);
            Assert.Empty(f.Defects);
        }
    }

    [Theory]
    [InlineData(NdbFormat.Ansi, 1)]
    [InlineData(NdbFormat.Ansi, 2)]
    [InlineData(NdbFormat.Unicode, 1)]
    [InlineData(NdbFormat.Unicode, 2)]
    public void DecodesEncodedBlocks(NdbFormat format, byte crypt)
    {
        var data = Pattern(500, 9);
        var built = NdbTestBuilder.Build(format, [new TestNode { Nid = 0x21, Data = data }], new TestBuild { Crypt = crypt });
        Assert.NotEqual(data, built.Bytes.AsSpan((int)built.BlockOffsets.Values.Min(), 500).ToArray());
        Assert.True(NdbFile.TryOpen(built.Bytes, out NdbFile? f, out _));
        using (f)
        {
            Assert.Equal(crypt == 1 ? NdbEncryption.Permute : NdbEncryption.Cyclic, f!.Header.Encryption);
            Assert.True(f.Nodes.TryGet(new Nid(0x21), out NodeEntry e));
            AssertData(f, e.Data, data);
            Assert.Empty(f.Defects);
        }
    }

    [Fact]
    public void InflatesCompressedBlocksInFourKiBFiles()
    {
        var data = Enumerable.Range(0, 5000).Select(i => (byte)(i % 7)).ToArray();
        var built = NdbTestBuilder.Build(NdbFormat.Unicode4K, [new TestNode { Nid = 0x21, Data = data }], new TestBuild { Compress = true });
        Assert.True(NdbFile.TryOpen(built.Bytes, out NdbFile? f, out _));
        using (f)
        {
            Assert.True(f!.Nodes.TryGet(new Nid(0x21), out NodeEntry e));
            Assert.True(f.Blocks.TryGet(e.Data, out BlockEntry be));
            Assert.True(be.Size < data.Length);
            AssertData(f, e.Data, data);
            Assert.Empty(f.Defects);
        }
    }

    [Fact]
    public void ChecksumsMatchTheSpecExamples()
    {
        // MS-PST 5.3: the CRC of an empty input is zero, and the permutation tables invert each other.
        Assert.Equal(0u, NdbChecksums.Crc32([]));
        var b = Pattern(300, 4);
        var c = (byte[])b.Clone();
        NdbChecksums.Permute(c, encode: true);
        Assert.NotEqual(b, c);
        NdbChecksums.Permute(c, encode: false);
        Assert.Equal(b, c);
        NdbChecksums.Cyclic(c, 0x1234);
        NdbChecksums.Cyclic(c, 0x1234);
        Assert.Equal(b, c);
    }
}
