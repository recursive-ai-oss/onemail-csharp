using System.Buffers.Binary;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Tests.Pst;

public class NdbBlockBuilderTests
{
    private static byte[] ReadAll(NdbFile f, Bid root, long expected)
    {
        using NdbDataTree? t = f.OpenDataTree(root);
        Assert.NotNull(t);
        Assert.Equal(expected, t!.Length);
        var buf = new byte[expected];
        Assert.Equal(expected, t.Read(0, buf));
        return buf;
    }

    public static TheoryData<int> TreeSizes => new()
    {
        1, 100, NdbBlockBuilder.MaxBlockData - 1, NdbBlockBuilder.MaxBlockData, NdbBlockBuilder.MaxBlockData + 1,
        (2 * NdbBlockBuilder.MaxBlockData) + 5,
        NdbBlockBuilder.XBlockCapacity * NdbBlockBuilder.MaxBlockData,         // a full XBLOCK
        (NdbBlockBuilder.XBlockCapacity * NdbBlockBuilder.MaxBlockData) + 1,   // the first XXBLOCK
        (NdbBlockBuilder.XBlockCapacity * NdbBlockBuilder.MaxBlockData * 2) + 99,
    };

    [Theory, MemberData(nameof(TreeSizes))]
    public void DataTreesReadBackByteForByte(int size)
    {
        var store = new BuilderStore();
        byte[] data = NdbTests.Pattern(size, size);
        DataTreeResult r = store.Blocks.WriteData(data);
        Assert.Equal(size, r.Length);
        Assert.Equal((size + NdbBlockBuilder.MaxBlockData - 1) / NdbBlockBuilder.MaxBlockData, r.BlockCount);
        store.AddNode(0x21, r.Root);
        using NdbFile f = store.Open();
        Assert.True(f.Nodes.TryGet(new Nid(0x21), out NodeEntry e));
        Assert.Equal(data, ReadAll(f, e.Data, size));
        // Shape: one block is its own tree, up to 1,021 blocks an XBLOCK, beyond that an XXBLOCK.
        Assert.Equal(r.BlockCount == 1, !r.Root.IsInternal);
        int internalBlocks = store.Written.Count(b => b.IsInternal);
        long x = (r.BlockCount + NdbBlockBuilder.XBlockCapacity - 1) / NdbBlockBuilder.XBlockCapacity;
        Assert.Equal(r.BlockCount == 1 ? 0 : x == 1 ? 1 : x + 1, internalBlocks);
    }

    [Fact]
    public void XBlocksCarryTheirOwnTotalsAndTheXxBlockTheWhole()
    {
        var store = new BuilderStore();
        int size = (NdbBlockBuilder.XBlockCapacity * NdbBlockBuilder.MaxBlockData) + 500;
        DataTreeResult r = store.Blocks.WriteData(new byte[size]);
        NdbBlock root = store.Written.Single(b => b.Bid == r.Root);
        ReadOnlySpan<byte> d = root.Image[..root.DataLength];
        ulong firstBid = BinaryPrimitives.ReadUInt64LittleEndian(d[8..]);
        Assert.Equal(1, d[0]);                       // btype
        Assert.Equal(2, d[1]);                       // cLevel: XXBLOCK
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(d[2..]));
        Assert.Equal((uint)size, BinaryPrimitives.ReadUInt32LittleEndian(d[4..]));
        NdbBlock first = store.Written.Single(b => b.Bid.Value == firstBid);
        Assert.Equal((uint)(NdbBlockBuilder.XBlockCapacity * NdbBlockBuilder.MaxBlockData), BinaryPrimitives.ReadUInt32LittleEndian(first.Image[4..]));
        Assert.Equal(NdbBlockBuilder.XBlockCapacity, BinaryPrimitives.ReadUInt16LittleEndian(first.Image[2..]));
    }

    [Fact]
    public void EmptyDataIsNoBlock()
    {
        var store = new BuilderStore();
        DataTreeResult r = store.Blocks.WriteData([]);
        Assert.True(r.Root.IsNull);
        Assert.Empty(store.Written);
        Assert.Throws<ArgumentException>(() => store.Blocks.WriteDataBlock([]));
        Assert.Throws<ArgumentException>(() => store.Blocks.WriteDataBlock(new byte[NdbBlockBuilder.MaxBlockData + 1]));
    }

    [Fact]
    public void BlockTrailerFollowsTheSpec()
    {
        var store = new BuilderStore();
        byte[] data = NdbTests.Pattern(100, 7);
        Bid bid = store.Blocks.WriteDataBlock(data);
        NdbBlock b = store.Written[0];
        Assert.Equal(bid, b.Bid);
        Assert.Equal(100, b.DataLength);
        Assert.Equal(128, b.TotalLength);   // 100 + 16 rounded up to 64
        var image = new byte[b.TotalLength];
        b.WriteTo(image, 0x4800);
        Assert.Equal(data, image[..100]);
        Assert.All(image[100..(128 - 16)], x => Assert.Equal(0, x));
        ReadOnlySpan<byte> t = image.AsSpan(128 - 16);
        Assert.Equal(100, BinaryPrimitives.ReadUInt16LittleEndian(t));
        Assert.Equal(NdbChecksums.Signature(0x4800, bid.Value), BinaryPrimitives.ReadUInt16LittleEndian(t[2..]));
        Assert.Equal(NdbChecksums.Crc32(data), BinaryPrimitives.ReadUInt32LittleEndian(t[4..]));
        Assert.Equal(bid.Value, BinaryPrimitives.ReadUInt64LittleEndian(t[8..]));
        // The padding is outside the CRC and the image copy leaves the source untouched.
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(b.Image[(128 - 14)..]));
        Assert.Throws<ArgumentException>(() => b.WriteTo(new byte[10], 0));
    }

    [Theory]
    [InlineData(NdbEncryption.None)]
    [InlineData(NdbEncryption.Permute)]
    [InlineData(NdbEncryption.Cyclic)]
    public void EncodedFilesDecodeToTheOriginalAndInternalBlocksStayPlain(NdbEncryption enc)
    {
        var store = new BuilderStore(enc);
        byte[] data = NdbTests.Pattern((3 * NdbBlockBuilder.MaxBlockData) + 11, 4);
        DataTreeResult r = store.Blocks.WriteData(data);
        store.AddNode(0x21, r.Root);
        if (enc != NdbEncryption.None)
        {
            // The stored bytes differ from the plain ones.
            NdbBlock first = store.Written.First(b => !b.IsInternal);
            Assert.NotEqual(data[..first.DataLength], first.Image[..first.DataLength].ToArray());
        }

        using NdbFile f = store.Open();
        Assert.Equal(enc, f.Header.Encryption);
        Assert.True(f.Nodes.TryGet(new Nid(0x21), out NodeEntry e));
        Assert.Equal(data, ReadAll(f, e.Data, data.Length));
        Assert.All(store.Written.Where(b => b.IsInternal), b => Assert.Equal(1, b.Image[0]));
    }

    [Fact]
    public void WindowsInformationProtectionCannotBeWritten() =>
        Assert.Throws<ArgumentException>(() => new NdbBlockBuilder(new SequentialBidAllocator(), new CollectingSink(), NdbEncryption.Edp));

    [Fact]
    public void AllocatorMakesBidsTheFormatAllows()
    {
        var a = new SequentialBidAllocator(5);
        Assert.Equal(20UL, a.NextBlockBid(false).Value);
        Assert.Equal(26UL, a.NextBlockBid(true).Value);
        Assert.Equal(7UL, a.NextIndex);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SequentialBidAllocator(0));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(340)]
    [InlineData(341)]
    [InlineData(1000)]
    public void SubnodeTreesListEveryEntry(int count)
    {
        var store = new BuilderStore();
        // Entries given out of order, with data in the third of them only: the tree sorts them.
        var entries = Enumerable.Range(1, count).Reverse().Select(i => new SubnodeEntry(new Nid((uint)((i * 32) + 5)), new Bid((ulong)i * 4), new Bid(i % 3 == 0 ? (ulong)i * 4 + 2 : 0))).ToList();
        Bid sub = store.Blocks.WriteSubnodes(entries);
        Assert.Equal(count > NdbBlockBuilder.SlBlockCapacity, store.Written[^1].Image[1] == 1);
        store.AddNode(0x21, store.Blocks.WriteData(new byte[10]).Root, sub);
        using NdbFile f = store.Open();
        Assert.True(f.Nodes.TryGet(new Nid(0x21), out NodeEntry e));
        SubnodeTree? t = f.OpenSubnodes(e.Sub);
        Assert.NotNull(t);
        Assert.Equal(entries.OrderBy(x => x.Nid.Value), t!.Entries);
        Assert.Empty(f.Defects);
    }

    [Fact]
    public void ASubnodeTreeHasRoomForAFullSiBlockOfFullSlBlocks()
    {
        var store = new BuilderStore();
        int max = NdbBlockBuilder.MaxSubnodes;
        Assert.Equal(340 * 510, max);
        var entries = Enumerable.Range(1, max).Select(i => new SubnodeEntry(new Nid((uint)(i * 32) + 5), new Bid(4), default)).ToList();
        Bid sub = store.Blocks.WriteSubnodes(entries);
        Assert.Equal(NdbBlockBuilder.SiBlockCapacity + 1, store.Written.Count);
        store.AddNode(0x21, store.Blocks.WriteData(new byte[10]).Root, sub);
        using NdbFile f = store.Open();
        Assert.True(f.Nodes.TryGet(new Nid(0x21), out NodeEntry e));
        Assert.Equal(max, f.OpenSubnodes(e.Sub)!.Entries.Count);
        entries.Add(new SubnodeEntry(new Nid(5), default, default));
        Assert.Throws<ArgumentException>(() => store.Blocks.WriteSubnodes(entries));
    }

    [Fact]
    public void SubnodeTreesRefuseRepeatedNidsAndHaveNoBlockWhenEmpty()
    {
        var store = new BuilderStore();
        Assert.True(store.Blocks.WriteSubnodes([]).IsNull);
        Assert.Empty(store.Written);
        var dup = new SubnodeEntry(new Nid(0x25), new Bid(4), default);
        Assert.Throws<ArgumentException>(() => store.Blocks.WriteSubnodes([dup, dup]));
    }

    [Fact]
    public void StreamsAreCopiedBlockByBlockAndShortOnesFail()
    {
        var store = new BuilderStore();
        byte[] data = NdbTests.Pattern((5 * NdbBlockBuilder.MaxBlockData) + 3, 9);
        DataTreeResult r = store.Blocks.WriteData(new MemoryStream(data), data.Length);
        store.AddNode(0x21, r.Root);
        using (NdbFile f = store.Open())
        {
            Assert.True(f.Nodes.TryGet(new Nid(0x21), out NodeEntry e));
            Assert.Equal(data, ReadAll(f, e.Data, data.Length));
        }

        Assert.Throws<EndOfStreamException>(() => store.Blocks.WriteData(new MemoryStream(new byte[10]), 11));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Blocks.WriteData(new MemoryStream(), NdbBlockBuilder.MaxDataTreeBytes + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Blocks.WriteData(new MemoryStream(), -1));
    }

    [Fact]
    public void ADataTreeWriterTakesBlocksOfAnySizeAndCompletesOnce()
    {
        var store = new BuilderStore();
        NdbBlockBuilder.DataTreeWriter w = store.Blocks.BeginDataTree();
        w.AddBlock(new byte[1000]);
        w.AddBlock(new byte[NdbBlockBuilder.MaxBlockData]);
        w.AddBlock(new byte[7]);
        Assert.Equal(1000 + NdbBlockBuilder.MaxBlockData + 7, w.Length);
        DataTreeResult r = w.Complete();
        Assert.Equal(3, r.BlockCount);
        Assert.Throws<InvalidOperationException>(() => w.Complete());
        Assert.Throws<InvalidOperationException>(() => w.AddBlock(new byte[1]));
        store.AddNode(0x21, r.Root);
        using NdbFile f = store.Open();
        Assert.True(f.Nodes.TryGet(new Nid(0x21), out NodeEntry e));
        using NdbDataTree t = f.OpenDataTree(e.Data)!;
        Assert.Equal(3, t.LeafCount);
        Assert.Equal(1000, t.GetLeafSize(0));
    }
}
