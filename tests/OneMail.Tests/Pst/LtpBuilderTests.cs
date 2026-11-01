using System.Buffers.Binary;
using RecursiveAI.OneMail.Pst.Ltp;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Tests.Pst;

public class LtpBuilderTests
{
    private const uint Nid1 = 0x21;

    private static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return b;
    }

    private static byte[] U64(ulong v)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, v);
        return b;
    }

    // ---- heap ----

    [Fact]
    public void HeapHidsAddressAllocationsAndBlocksFillBeforeTheNextOpens()
    {
        var heap = new HeapNodeBuilder(0xB5);
        var hids = new List<(Hid Hid, byte[] Data)>();
        for (int i = 0; i < 40; i++)
        {
            byte[] d = NdbTests.Pattern(1 + (i * 97 % HeapNodeBuilder.MaxAllocation), i);
            hids.Add((heap.Allocate(d), d));
        }

        Assert.Equal(1, hids[0].Hid.Index);
        Assert.Equal(0x20u, hids[0].Hid.Value);   // the first allocation of the first block, as in every real file
        Assert.True(heap.BlockCount > 1);
        // Every block but the last is nearly full: the next allocation would not have fit.
        heap.UserRoot = hids[0].Hid;
        IReadOnlyList<ReadOnlyMemory<byte>> blocks = heap.Finish();
        Assert.Equal(heap.BlockCount, blocks.Count);
        Assert.All(blocks, b => Assert.True(b.Length <= NdbBlockBuilder.MaxBlockData));

        var store = new BuilderStore();
        var tree = store.Blocks.BeginDataTree();
        foreach (var b in blocks) { tree.AddBlock(b.Span); }
        store.AddNode(Nid1, tree.Complete().Root);
        using NdbFile f = store.Open();
        Assert.True(HeapNode.TryOpen(f, new Nid(Nid1), out HeapNode? h, out _));
        using (h)
        {
            Assert.Equal(0xB5, h!.ClientSignature);
            Assert.Equal(heap.BlockCount, h.BlockCount);
            foreach ((Hid hid, byte[] data) in hids)
            {
                Assert.True(h.TryGetAllocation(hid, out HeapAllocation a));
                Assert.Equal(data, h.GetBytes(a).ToArray());
            }

            Assert.Empty(h.Defects);
        }
    }

    [Fact]
    public void HeapRefusesWhatTheFormatRefuses()
    {
        var heap = new HeapNodeBuilder(0xBC);
        Assert.Throws<ArgumentOutOfRangeException>(() => heap.Allocate(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => heap.Allocate(HeapNodeBuilder.MaxAllocation + 1));
        Assert.Throws<ArgumentException>(() => heap.GetSpan(new Hid(0x20)));
        heap.Allocate(HeapNodeBuilder.MaxAllocation);   // the largest allocation is fine
        heap.Finish();
        Assert.Throws<InvalidOperationException>(() => heap.Allocate(1));
        Assert.Throws<InvalidOperationException>(() => heap.Finish());
    }

    [Fact]
    public void HeapsOfManyBlocksCarryBitmapHeadersWhereTheFormatPutsThem()
    {
        // 300 allocations of 3,000 bytes: two per block, 150 blocks, so blocks 8 and 136 start an HNBITMAPHDR.
        var heap = new HeapNodeBuilder(0xB5);
        var hids = new List<Hid>();
        for (int i = 0; i < 300; i++)
        {
            var d = new byte[3000];
            Array.Fill(d, (byte)i);
            hids.Add(heap.Allocate(d));
        }

        Assert.Equal(150, heap.BlockCount);
        heap.UserRoot = hids[0];
        var blocks = heap.Finish();
        // The fill levels of blocks 8 and up are in the bitmap header of block 8: a block of 6,000 bytes has about 2,100 free, level 2 (2,048 to 2,560).
        Assert.Equal(2, blocks[8].Span[2] & 0xF);
        Assert.Equal(2, (blocks[0].Span[8] >> 4) & 0xF);   // block 1 in the HNHDR's map: the high nibble of its first byte (block 0 takes the low one)
        var store = new BuilderStore();
        var tree = store.Blocks.BeginDataTree();
        foreach (var b in blocks) { tree.AddBlock(b.Span); }
        DataTreeResult r = tree.Complete();
        Assert.Equal(150, r.BlockCount);
        store.AddNode(Nid1, r.Root);
        using NdbFile f = store.Open();
        Assert.True(HeapNode.TryOpen(f, new Nid(Nid1), out HeapNode? h, out _));
        using (h)
        {
            for (int i = 0; i < 300; i += 7)
            {
                Assert.True(h!.TryGetAllocation(hids[i], out HeapAllocation a));
                Assert.Equal(3000, a.Length);
                Assert.Equal((byte)i, h.GetBytes(a)[2999]);
            }

            Assert.Equal(135, hids[270].BlockIndex);
            Assert.True(h!.TryGetAllocation(hids[272], out HeapAllocation a136));   // block 136
            Assert.Equal(136, a136.BlockIndex);
            Assert.Equal(66, a136.Offset);   // after the 66-byte HNBITMAPHDR
            Assert.Empty(h.Defects);
        }
    }

    // ---- BTH ----

    private static (NdbFile File, Bth Tree, HeapNode Heap) OpenBth(Action<BthTreeBuilder> fill, int keySize, int dataSize, out BthTreeBuilder builder)
    {
        var store = new BuilderStore();
        var heap = new HeapNodeBuilder(0xB5);
        var bth = new BthTreeBuilder(heap, keySize, dataSize);
        builder = bth;
        fill(bth);
        heap.UserRoot = bth.Build();
        var tree = store.Blocks.BeginDataTree();
        foreach (var b in heap.Finish()) { tree.AddBlock(b.Span); }
        store.AddNode(Nid1, tree.Complete().Root);
        NdbFile f = store.Open();
        Assert.True(HeapNode.TryOpen(f, new Nid(Nid1), out HeapNode? h, out _));
        Assert.True(Bth.TryOpen(h!, h!.UserRoot, out Bth? t));
        return (f, t!, h);
    }

    [Theory]
    [InlineData(2, 6, 0, 0)]
    [InlineData(2, 6, 1, 0)]
    [InlineData(2, 6, 447, 0)]    // exactly one full leaf: 3,580 / 8
    [InlineData(2, 6, 448, 1)]    // the first index level
    [InlineData(4, 4, 5000, 1)]
    [InlineData(8, 1, 3000, 1)]
    [InlineData(16, 32, 70, 0)]
    [InlineData(16, 32, 75, 1)]
    [InlineData(16, 32, 14000, 2)]   // 74 records per leaf and 179 per index node: 190 leaves need two index nodes and a root above them
    public void BthsReadBackWithTheLevelsTheyNeed(int keySize, int dataSize, int count, int levels)
    {
        (NdbFile f, Bth t, HeapNode h) = OpenBth(b =>
        {
            // Added in descending order so the builder has to sort.
            for (int i = count - 1; i >= 0; i--)
            {
                var data = new byte[dataSize];
                data[0] = (byte)i;
                if (dataSize > 1) { data[^1] = (byte)(i >> 8); }
                b.Add(MakeKey(keySize, i), data);
            }
        }, keySize, dataSize, out BthTreeBuilder builder);
        using (f)
        using (h)
        {
            Assert.Equal(count, builder.Count);
            Assert.Equal(keySize, t.KeySize);
            Assert.Equal(dataSize, t.DataSize);
            Assert.Equal(levels, t.IndexLevels);
            Assert.Equal(count == 0, t.Root.IsNull);
            // The cursor returns every record once, in increasing key order, and TryFind agrees.
            var entries = new List<(UInt128 Key, byte[] Data)>();
            BthCursor c = t.CreateCursor();
            while (c.MoveNext()) { entries.Add((LtpBytesKey(c.Key), c.Data.ToArray())); }
            Assert.Equal(count, entries.Count);
            Assert.Equal(entries.Select(e => e.Key).Order(), entries.Select(e => e.Key));
            var probe = new byte[dataSize];
            for (int i = 0; i < count; i += Math.Max(1, count / 200))
            {
                Assert.True(t.TryFind(MakeKey(keySize, i), probe));
                Assert.Equal((byte)i, probe[0]);
                if (dataSize > 1) { Assert.Equal((byte)(i >> 8), probe[^1]); }
            }

            Assert.Empty(h.Defects);
        }
    }

    // Increasing in i, spread over the bytes of the key so wide keys exercise their high bytes.
    private static byte[] MakeKey(int keySize, int i)
    {
        var key = new byte[keySize];
        uint v = (uint)((i * 3) + 1);
        for (int k = 0; k < Math.Min(keySize, 4); k++) { key[k] = (byte)(v >> (8 * k)); }
        if (keySize == 16) { key[15] = (byte)(i / 20000); key[9] = 1; }
        return key;
    }

    private static UInt128 LtpBytesKey(ReadOnlySpan<byte> key)
    {
        UInt128 v = 0;
        for (int i = key.Length - 1; i >= 0; i--) { v = (v << 8) | key[i]; }
        return v;
    }

    [Fact]
    public void BthRefusesWhatTheFormatRefuses()
    {
        var heap = new HeapNodeBuilder(0xB5);
        Assert.Throws<ArgumentOutOfRangeException>(() => new BthTreeBuilder(heap, 3, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BthTreeBuilder(heap, 4, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BthTreeBuilder(heap, 4, 33));
        var b = new BthTreeBuilder(heap, 2, 2);
        Assert.Throws<ArgumentException>(() => b.Add(new byte[3], new byte[2]));
        Assert.Throws<ArgumentException>(() => b.Add(new byte[2], new byte[1]));
        Assert.Throws<ArgumentException>(() => b.Add(0x10000UL, new byte[2]));
        b.Add(5UL, new byte[2]);
        b.Add(5UL, new byte[2]);
        Assert.Throws<InvalidOperationException>(() => b.Build());   // repeated key
    }

    // ---- property context ----

    private static PropertyContext OpenPc(LtpNodeResult r, BuilderStore store, out NdbFile file)
    {
        store.AddNode(Nid1, r);
        file = store.Open();
        Assert.True(PropertyContext.TryOpen(file, new Nid(Nid1), out PropertyContext? pc, out _));
        return pc!;
    }

    [Fact]
    public void AnEmptyContextIsAHeapWithAnEmptyTree()
    {
        var store = new BuilderStore();
        LtpNodeResult r = new PropertyContextBuilder(store.Blocks).Build();
        Assert.Empty(r.Subnodes);
        Assert.Equal(1, r.HeapBlockCount);
        using PropertyContext pc = OpenPc(r, store, out NdbFile f);
        using (f)
        {
            Assert.Equal(0, pc.Count());
            Assert.True(r.WriteSubnodes(store.Blocks).IsNull);
        }
    }

    [Fact]
    public void PropertiesComeBackWithTheirTypesAndValues()
    {
        var store = new BuilderStore();
        var pcb = new PropertyContextBuilder(store.Blocks);
        byte[] subject = System.Text.Encoding.Unicode.GetBytes("Hello world");
        pcb.Set(0x0037, LtpPropertyTypes.PtypString, subject);
        pcb.Set(0x0E07, LtpPropertyTypes.PtypInteger32, U32(0xDEADBEEF));
        pcb.Set(0x0E08, LtpPropertyTypes.PtypInteger16, [0x34, 0x12]);
        pcb.Set(0x0E09, LtpPropertyTypes.PtypBoolean, [1]);
        pcb.Set(0x0E0A, LtpPropertyTypes.PtypErrorCode, U32(0x8004010F));
        pcb.Set(0x0E0B, LtpPropertyTypes.PtypInteger64, U64(0x1122334455667788));
        pcb.Set(0x0E0C, LtpPropertyTypes.PtypTime, U64(133_000_000_000_000_000));
        pcb.Set(0x0E0D, LtpPropertyTypes.PtypGuid, Guid.Parse("22222222-3333-4444-5555-666666666666").ToByteArray());
        pcb.Set(0x0E0E, LtpPropertyTypes.PtypBinary, []);                     // empty: a zero HNID
        pcb.Set(0x0E0F, LtpPropertyTypes.PtypString, []);
        pcb.Set(0x0E10, (ushort)(LtpPropertyTypes.PtypString | LtpPropertyTypes.MultiValueFlag), MultiString("a", "bc", ""));
        pcb.Set(0x0E11, (ushort)(LtpPropertyTypes.PtypInteger32 | LtpPropertyTypes.MultiValueFlag), [.. U32(1), .. U32(2), .. U32(3)]);
        LtpNodeResult r = pcb.Build();
        Assert.Empty(r.Subnodes);
        using PropertyContext pc = OpenPc(r, store, out NdbFile f);
        using (f)
        {
            Assert.Equal(12, pc.Count());
            Assert.Equal([0x0037, 0x0E07, 0x0E08, 0x0E09, 0x0E0A, 0x0E0B, 0x0E0C, 0x0E0D, 0x0E0E, 0x0E0F, 0x0E10, 0x0E11], pc.Select(p => (int)p.Id));
            Assert.Equal(subject, Value(pc, 0x0037));
            Assert.Equal(LtpValueKind.Inline, Get(pc, 0x0E07).Value.Kind);
            Assert.Equal(U32(0xDEADBEEF), Value(pc, 0x0E07));
            Assert.Equal<byte[]>([0x34, 0x12], Value(pc, 0x0E08));
            Assert.Equal<byte[]>([1], Value(pc, 0x0E09));
            Assert.Equal(U32(0x8004010F), Value(pc, 0x0E0A));
            Assert.Equal(LtpValueKind.Heap, Get(pc, 0x0E0B).Value.Kind);
            Assert.Equal(U64(0x1122334455667788), Value(pc, 0x0E0B));
            Assert.Equal(U64(133_000_000_000_000_000), Value(pc, 0x0E0C));
            Assert.Equal(16, Value(pc, 0x0E0D).Length);
            Assert.Equal(LtpValueKind.Empty, Get(pc, 0x0E0E).Value.Kind);
            Assert.Equal(LtpValueKind.Empty, Get(pc, 0x0E0F).Value.Kind);
            Assert.Equal(MultiString("a", "bc", ""), Value(pc, 0x0E10));
            Assert.Equal(12, Value(pc, 0x0E11).Length);
            Assert.Empty(pc.Defects);
        }
    }

    private static byte[] MultiString(params string[] s)
    {
        var parts = s.Select(x => System.Text.Encoding.Unicode.GetBytes(x)).ToArray();
        var ms = new MemoryStream();
        ms.Write(U32((uint)parts.Length));
        int at = 4 + (4 * parts.Length);
        foreach (var p in parts) { ms.Write(U32((uint)at)); at += p.Length; }
        foreach (var p in parts) { ms.Write(p); }
        return ms.ToArray();
    }

    private static PcProperty Get(PropertyContext pc, ushort id)
    {
        Assert.True(pc.TryGet(id, out PcProperty p));
        return p;
    }

    private static byte[] Value(PropertyContext pc, ushort id) => pc.ReadValue(Get(pc, id))!;

    [Theory]
    [InlineData(1, LtpValueKind.Heap)]
    [InlineData(3579, LtpValueKind.Heap)]
    [InlineData(3580, LtpValueKind.Heap)]       // the largest heap allocation
    [InlineData(3581, LtpValueKind.Subnode)]    // the first value that goes to a subnode
    [InlineData(8176, LtpValueKind.Subnode)]
    [InlineData(8177, LtpValueKind.Subnode)]    // two data blocks
    [InlineData(100_000, LtpValueKind.Subnode)]
    public void VariableValuesGoToTheHeapUpTo3580BytesAndToASubnodeAbove(int size, LtpValueKind kind)
    {
        var store = new BuilderStore();
        var pcb = new PropertyContextBuilder(store.Blocks);
        byte[] value = NdbTests.Pattern(size, size);
        pcb.Set(0x3001, LtpPropertyTypes.PtypBinary, value);
        pcb.Set(0x3002, LtpPropertyTypes.PtypBinary, new MemoryStream(value), value.Length);   // the stream overload makes the same choice
        LtpNodeResult r = pcb.Build();
        Assert.Equal(kind == LtpValueKind.Subnode ? 2 : 0, r.Subnodes.Count);
        using PropertyContext pc = OpenPc(r, store, out NdbFile f);
        using (f)
        {
            Assert.Equal(kind, Get(pc, 0x3001).Value.Kind);
            Assert.Equal(kind, Get(pc, 0x3002).Value.Kind);
            Assert.Equal(value, Value(pc, 0x3001));
            Assert.Equal(value, Value(pc, 0x3002));
            Assert.Equal(size, pc.GetValueLength(Get(pc, 0x3001)));
            Assert.Empty(pc.Defects);
        }
    }

    [Fact]
    public void ValueSubnodesGetDistinctLtpNidsAndMeetTheCallersOwnSubnodes()
    {
        var store = new BuilderStore();
        var pcb = new PropertyContextBuilder(store.Blocks);
        pcb.Set(0x1000, LtpPropertyTypes.PtypBinary, new byte[5000]);
        pcb.Set(0x1001, LtpPropertyTypes.PtypBinary, new byte[6000]);
        LtpNodeResult r = pcb.Build();
        Assert.Equal([0x3Fu, 0x5Fu], r.Subnodes.Select(s => s.Nid.Value));   // NID_TYPE_LTP, index 1 and 2
        Assert.All(r.Subnodes, s => Assert.Equal(NidType.Ltp, s.Nid.Type));
        var mine = new SubnodeEntry(new Nid(0x692), store.Blocks.WriteData(new byte[30]).Root, default);
        store.AddNode(Nid1, r, [mine]);
        using NdbFile f = store.Open();
        Assert.True(f.Nodes.TryGet(new Nid(Nid1), out NodeEntry e));
        Assert.Equal(3, f.OpenSubnodes(e.Sub)!.Entries.Count);
        Assert.Throws<ArgumentException>(() => r.WriteSubnodes(store.Blocks, [new SubnodeEntry(new Nid(0x3F), default, default)]));
    }

    [Fact]
    public void ACallerSuppliedNidAllocatorIsUsedAndTypeZeroIsRefused()
    {
        var store = new BuilderStore();
        uint next = 0x8000 + 0x12;
        var pcb = new PropertyContextBuilder(store.Blocks, () => new Nid(next++ ));
        pcb.Set(1, LtpPropertyTypes.PtypBinary, new byte[4000]);
        Assert.Equal(0x8012u, pcb.Build().Subnodes[0].Nid.Value);
        var bad = new PropertyContextBuilder(store.Blocks, () => new Nid(0x8000));
        Assert.Throws<InvalidOperationException>(() => bad.Set(1, LtpPropertyTypes.PtypBinary, new byte[4000]));
    }

    [Fact]
    public void PropertyContextRefusesBadCalls()
    {
        var store = new BuilderStore();
        var pcb = new PropertyContextBuilder(store.Blocks);
        pcb.Set(1, LtpPropertyTypes.PtypInteger32, U32(1));
        Assert.Throws<ArgumentException>(() => pcb.Set(1, LtpPropertyTypes.PtypInteger32, U32(2)));            // repeated ID
        Assert.Throws<ArgumentException>(() => pcb.Set(2, LtpPropertyTypes.PtypInteger32, [1, 2]));            // wrong size
        Assert.Throws<ArgumentException>(() => pcb.Set(3, LtpPropertyTypes.PtypInteger64, [1, 2, 3, 4]));
        Assert.Throws<ArgumentException>(() => pcb.SetNodeReference(4, LtpPropertyTypes.PtypObject, new Nid(0x8000)));   // type 0 would read as an HID
        Assert.Throws<EndOfStreamException>(() => pcb.Set(5, LtpPropertyTypes.PtypBinary, new MemoryStream(new byte[10]), 20));
        pcb.Build();
        Assert.Throws<InvalidOperationException>(() => pcb.Set(9, LtpPropertyTypes.PtypInteger32, U32(1)));
        Assert.Throws<InvalidOperationException>(() => pcb.Build());
    }

    [Fact]
    public void ANodeReferenceIsTheSubnodeNidAndItsSubnodeIsTheCallers()
    {
        var store = new BuilderStore();
        var pcb = new PropertyContextBuilder(store.Blocks);
        var embedded = new Nid(0x8045);   // an attachment-typed subnode (type 5)
        pcb.SetNodeReference(0x3701, LtpPropertyTypes.PtypObject, embedded);
        LtpNodeResult r = pcb.Build();
        using PropertyContext pc = OpenPc(r, store, out NdbFile f);
        using (f)
        {
            Assert.Equal(embedded.Value, Get(pc, 0x3701).Value.Hnid);
        }
    }

    // ---- table context ----

    private static readonly (ushort Id, ushort Type)[] SampleColumns =
    [
        (0x0037, LtpPropertyTypes.PtypString), (0x0E07, LtpPropertyTypes.PtypInteger32), (0x0E08, LtpPropertyTypes.PtypInteger16),
        (0x0E09, LtpPropertyTypes.PtypBoolean), (0x0E0B, LtpPropertyTypes.PtypInteger64), (0x0E0C, LtpPropertyTypes.PtypTime),
        (0x0E0D, LtpPropertyTypes.PtypGuid), (0x3001, LtpPropertyTypes.PtypBinary),
    ];

    private static TableContext OpenTc(LtpNodeResult r, BuilderStore store, out NdbFile file)
    {
        store.AddNode(Nid1, r);
        file = store.Open();
        Assert.True(TableContext.TryOpen(file, new Nid(Nid1), out TableContext? tc, out IReadOnlyList<LtpDefect> why), string.Join("; ", why));
        return tc!;
    }

    private static byte[]? Cell(TableContext tc, uint rowId, ushort column)
    {
        Assert.True(tc.TryFindRow(rowId, out uint index));
        var buf = new byte[tc.RowSize];
        Assert.True(tc.TryReadRow(index, buf));
        TableRow row = tc.GetRow(buf);
        int c = tc.FindColumn(column);
        Assert.True(c >= 0);
        return row.TryGetCell(c, out LtpValueRef v) ? tc.ReadValue(v) ?? [] : null;
    }

    [Fact]
    public void AnEmptyTableHasColumnsAndNoRows()
    {
        var store = new BuilderStore();
        var tcb = new TableContextBuilder(store.Blocks, SampleColumns);
        Assert.Equal(10, tcb.Columns.Count);   // the two LTP columns are added
        LtpNodeResult r = tcb.Build();
        using TableContext tc = OpenTc(r, store, out NdbFile f);
        using (f)
        {
            Assert.Equal(0, tc.RowCount);
            Assert.Equal(10, tc.Columns.Count);
            Assert.Equal(tcb.RowSize, tc.RowSize);
            Assert.False(tc.TryReadRow(0, new byte[tc.RowSize]));
        }
    }

    [Fact]
    public void ColumnsAreLaidOutAsTheSpecRequires()
    {
        var store = new BuilderStore();
        var tcb = new TableContextBuilder(store.Blocks, SampleColumns);
        IReadOnlyList<TcColumn> cols = tcb.Columns;
        Assert.Equal(cols.Select(c => c.Tag).Order(), cols.Select(c => c.Tag));   // sorted by tag
        TcColumn id = cols.Single(c => c.Id == 0x67F2), ver = cols.Single(c => c.Id == 0x67F3);
        Assert.Equal((0, 0, 4), (id.Offset, id.BitIndex, id.Size));
        Assert.Equal((4, 1, 4), (ver.Offset, ver.BitIndex, ver.Size));
        // 8-byte cells, then 4-byte ones, then 2, then 1, with no overlap; 10 columns need a 2-byte bitmap.
        Assert.Equal(8, cols.Single(c => c.Id == 0x0E0B).Size);
        Assert.Equal(4, cols.Single(c => c.Id == 0x0037).Size);   // an HNID
        Assert.Equal(4, cols.Single(c => c.Id == 0x0E0D).Size);   // a GUID is larger than 8 bytes: an HNID too
        Assert.Equal(2, cols.Single(c => c.Id == 0x0E08).Size);
        Assert.Equal(1, cols.Single(c => c.Id == 0x0E09).Size);
        var used = new bool[tcb.RowSize];
        foreach (TcColumn c in cols)
        {
            for (int i = c.Offset; i < c.Offset + c.Size; i++) { Assert.False(used[i]); used[i] = true; }
        }

        Assert.Equal(cols.Max(c => c.Offset + c.Size) + 2, tcb.RowSize);
        Assert.Equal(cols.Count, cols.Select(c => c.BitIndex).Distinct().Count());
    }

    [Fact]
    public void RowsReadBackCellByCellWithAbsentDistinctFromEmpty()
    {
        var store = new BuilderStore();
        var tcb = new TableContextBuilder(store.Blocks, SampleColumns);
        int Col(ushort id) => tcb.FindColumn(id);
        byte[] big = NdbTests.Pattern(5000, 1);   // above 3,580: a subnode
        tcb.BeginRow(0x2000A4);
        tcb.SetCell(Col(0x0037), System.Text.Encoding.Unicode.GetBytes("Row one"));
        tcb.SetCell(Col(0x0E07), U32(77));
        tcb.SetCell(Col(0x0E08), [9, 0]);
        tcb.SetCell(Col(0x0E09), [1]);
        tcb.SetCell(Col(0x0E0B), U64(1UL << 40));
        tcb.SetCell(Col(0x0E0D), Guid.NewGuid().ToByteArray());
        tcb.SetCell(Col(0x3001), big);
        tcb.EndRow();
        tcb.BeginRow(0x2000C4);
        tcb.SetCell(Col(0x0037), []);   // present and empty
        tcb.SetCell(Col(0x3001), new MemoryStream(big), big.Length);
        tcb.EndRow();
        Assert.Equal(2, tcb.RowCount);
        LtpNodeResult r = tcb.Build();
        Assert.Equal(2, r.Subnodes.Count);
        using TableContext tc = OpenTc(r, store, out NdbFile f);
        using (f)
        {
            Assert.Equal(2, tc.RowCount);
            Assert.Equal(System.Text.Encoding.Unicode.GetBytes("Row one"), Cell(tc, 0x2000A4, 0x0037));
            Assert.Equal(U32(77), Cell(tc, 0x2000A4, 0x0E07));
            Assert.Equal<byte[]>([9, 0], Cell(tc, 0x2000A4, 0x0E08));
            Assert.Equal<byte[]>([1], Cell(tc, 0x2000A4, 0x0E09));
            Assert.Equal(U64(1UL << 40), Cell(tc, 0x2000A4, 0x0E0B));
            Assert.Equal(big, Cell(tc, 0x2000A4, 0x3001));
            Assert.Equal(U32(0x2000A4), Cell(tc, 0x2000A4, 0x67F2));
            Assert.Equal(U32(0), Cell(tc, 0x2000A4, 0x67F3));
            Assert.Null(Cell(tc, 0x2000A4, 0x0E0C));            // never set: absent
            Assert.Empty(Cell(tc, 0x2000C4, 0x0037)!);          // set to empty: present
            Assert.Null(Cell(tc, 0x2000C4, 0x0E07));
            Assert.Equal(big, Cell(tc, 0x2000C4, 0x3001));
            Assert.Equal<uint>([0x2000A4, 0x2000C4], tc.EnumerateRowIndex().Select(e => e.RowId));
            Assert.Equal<uint>([0, 1], tc.EnumerateRowIndex().Select(e => e.RowIndex));
            Assert.Empty(tc.Defects);
        }
    }

    [Fact]
    public void OneIdMayBeAColumnTwiceWithTwoTypesButNotTwiceWithOne()
    {
        var store = new BuilderStore();
        var tcb = new TableContextBuilder(store.Blocks, [(0x3001, LtpPropertyTypes.PtypString), (0x3001, LtpPropertyTypes.PtypString8)]);
        int unicode = tcb.FindColumn(0x3001, LtpPropertyTypes.PtypString), ansi = tcb.FindColumn(0x3001, LtpPropertyTypes.PtypString8);
        Assert.NotEqual(unicode, ansi);
        tcb.BeginRow(1);
        tcb.SetCell(unicode, [0x41, 0]);
        tcb.SetCell(ansi, [0x42]);
        tcb.EndRow();
        using TableContext tc = OpenTc(tcb.Build(), store, out NdbFile f);
        using (f)
        {
            var buf = new byte[tc.RowSize];
            Assert.True(tc.TryReadRow(0, buf));
            TableRow row = tc.GetRow(buf);
            Assert.True(row.TryGetCell(unicode, out LtpValueRef u));
            Assert.True(row.TryGetCell(ansi, out LtpValueRef a));
            Assert.Equal<byte[]>([0x41, 0], tc.ReadValue(u));
            Assert.Equal<byte[]>([0x42], tc.ReadValue(a));
        }
    }

    [Fact]
    public void HeapOrderAndCellBitsFollowTheStatedConventions()
    {
        var store = new BuilderStore();
        var pcb = new PropertyContextBuilder(store.Blocks);
        pcb.Set(0x0037, LtpPropertyTypes.PtypString, [0x41, 0]);
        pcb.Set(0x0038, LtpPropertyTypes.PtypString, [0x42, 0]);
        LtpNodeResult r = pcb.Build();
        using PropertyContext pc = OpenPc(r, store, out NdbFile f);
        using (f)
        {
            Assert.Equal(0x20u, pc.Index.Header.Value);   // the BTHHEADER first, then the records, then the values
            Assert.Equal(0x40u, pc.Index.Root.Value);
            Assert.Equal(0x60u, Get(pc, 0x0037).Value.Hnid);
            Assert.Equal(0x80u, Get(pc, 0x0038).Value.Hnid);
        }

        var store2 = new BuilderStore();
        var tcb = new TableContextBuilder(store2.Blocks, SampleColumns);
        // The cell-existence bits number the cells in the order they sit in the row.
        Assert.Equal(Enumerable.Range(0, tcb.Columns.Count), tcb.Columns.OrderBy(c => c.Offset).Select(c => c.BitIndex));
        using TableContext tc = OpenTc(tcb.Build(), store2, out NdbFile f2);
        using (f2)
        {
            Assert.Equal(0x40u, tc.Heap.UserRoot.Value);   // TCINFO second, behind the row index header
            Assert.Equal(0x20u, tc.RowIndex!.Header.Value);
        }
    }

    [Fact]
    public void CallerDeclaredRowIdAndVersionColumnsAreKept()
    {
        var store = new BuilderStore();
        var tcb = new TableContextBuilder(store.Blocks, [(0x67F2, 3), (0x67F3, 3), (0x0E07, 3)]);
        Assert.Equal(3, tcb.Columns.Count);
        tcb.BeginRow(5);
        tcb.SetCell(tcb.FindColumn(0x67F3), U32(42));
        tcb.EndRow();
        using TableContext tc = OpenTc(tcb.Build(), store, out NdbFile f);
        using (f)
        {
            Assert.Equal(U32(42), Cell(tc, 5, 0x67F3));
            Assert.Equal(U32(5), Cell(tc, 5, 0x67F2));
        }
    }

    [Fact]
    public void RowMatrixStaysInTheHeapUpTo3580BytesAndMovesToASubnodeAbove()
    {
        // Integer32 columns only: the row is 8 bytes of ID and version, 4 per column and the bitmap.
        (ushort, ushort)[] cols = [(0x0E01, 3), (0x0E02, 3), (0x0E03, 3), (0x0E04, 3), (0x0E05, 3)];
        int rowSize;
        {
            var probe = new TableContextBuilder(new BuilderStore().Blocks, cols);
            rowSize = probe.RowSize;
        }

        int fits = HeapNodeBuilder.MaxAllocation / rowSize;
        foreach ((int rows, bool inHeap) in new[] { (fits, true), (fits + 1, false) })
        {
            var store = new BuilderStore();
            var tcb = new TableContextBuilder(store.Blocks, cols);
            for (int i = 0; i < rows; i++)
            {
                tcb.BeginRow((uint)(i + 1));
                tcb.SetCell(tcb.FindColumn(0x0E03), U32((uint)i));
                tcb.EndRow();
            }

            LtpNodeResult r = tcb.Build();
            Assert.Equal(inHeap ? 0 : 1, r.Subnodes.Count);
            using TableContext tc = OpenTc(r, store, out NdbFile f);
            using (f)
            {
                Assert.Equal(rows, tc.RowCount);
                Assert.Equal(U32((uint)(rows - 1)), Cell(tc, (uint)rows, 0x0E03));
                Assert.Equal(inHeap, new Hnid(HnidRows(tc)).IsHid);
                Assert.Empty(tc.Defects);
            }
        }
    }

    private static uint HnidRows(TableContext tc)
    {
        Assert.True(tc.Heap.TryGetAllocation(tc.Heap.UserRoot, out HeapAllocation a));
        return BinaryPrimitives.ReadUInt32LittleEndian(tc.Heap.GetBytes(a)[14..]);
    }

    [Theory]
    [InlineData(1)]       // the last block is exactly full
    [InlineData(0)]       // the last block is one row short
    [InlineData(2)]       // one row spills into another block
    public void RowMatrixBlocksHoldWholeRowsAndAllButTheLastAreFull(int extra)
    {
        (ushort, ushort)[] cols = [(0x0E01, 3), (0x0E02, 3), (0x0E03, 3), (0x0E04, 3), (0x0E05, 3), (0x0E06, 3), (0x0E07, 3)];
        var store = new BuilderStore();
        var tcb = new TableContextBuilder(store.Blocks, cols);
        int perBlock = tcb.RowsPerBlock;
        Assert.Equal(NdbBlockBuilder.MaxBlockData / tcb.RowSize, perBlock);
        int rows = (3 * perBlock) + extra - 1;
        for (int i = 0; i < rows; i++)
        {
            tcb.BeginRow((uint)((i * 4) + 4));
            tcb.SetCell(tcb.FindColumn(0x0E05), U32((uint)i));
            tcb.EndRow();
        }

        LtpNodeResult r = tcb.Build();
        Assert.Single(r.Subnodes);
        using TableContext tc = OpenTc(r, store, out NdbFile f);
        using (f)
        {
            Assert.Equal(rows, tc.RowCount);
            for (int i = 0; i < rows; i += Math.Max(1, rows / 50))
            {
                Assert.Equal(U32((uint)i), Cell(tc, (uint)((i * 4) + 4), 0x0E05));
            }

            Assert.Equal(U32((uint)(rows - 1)), Cell(tc, (uint)(((rows - 1) * 4) + 4), 0x0E05));
            Assert.True(tc.Heap.TryGetAllocation(tc.Heap.UserRoot, out _));
            SubnodeEntry sub = r.Subnodes[0];
            using NdbDataTree t = f.OpenDataTree(sub.Data)!;
            Assert.Equal((rows + perBlock - 1) / perBlock, t.LeafCount);
            for (int i = 0; i < t.LeafCount - 1; i++) { Assert.Equal(NdbBlockBuilder.MaxBlockData, t.GetLeafSize(i)); }   // full blocks: the rows and then zeros
            Assert.Equal(rows % perBlock == 0 ? NdbBlockBuilder.MaxBlockData : (rows % perBlock) * tcb.RowSize, t.GetLeafSize(t.LeafCount - 1));   // a last block that filled exactly was written full
            Assert.Empty(tc.Defects);
        }
    }

    [Fact]
    public void TablesRefuseBadColumnsAndBadCalls()
    {
        var blocks = new BuilderStore().Blocks;
        Assert.Throws<ArgumentException>(() => new TableContextBuilder(blocks, [(1, 3), (1, 3)]));
        Assert.Throws<ArgumentException>(() => new TableContextBuilder(blocks, [(0x67F2, 0x1F)]));
        Assert.Throws<ArgumentException>(() => new TableContextBuilder(blocks, Enumerable.Range(1, 254).Select(i => ((ushort)i, (ushort)3))));   // 254 + the two LTP columns
        var tcb = new TableContextBuilder(blocks, [(1, 3), (2, 0x1F), (3, 2)]);
        Assert.Throws<InvalidOperationException>(() => tcb.SetCell(0, U32(1)));
        tcb.BeginRow(1);
        Assert.Throws<InvalidOperationException>(() => tcb.BeginRow(2));
        Assert.Throws<InvalidOperationException>(() => tcb.Build());
        Assert.Throws<ArgumentException>(() => tcb.SetCell(tcb.FindColumn(1), [1]));          // wrong size
        Assert.Throws<ArgumentException>(() => tcb.SetCell(tcb.FindColumn(3), [1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => tcb.SetCell(99, [1]));
        tcb.SetCell(tcb.FindColumn(1), U32(1));
        Assert.Throws<ArgumentException>(() => tcb.SetCell(tcb.FindColumn(1), U32(2)));       // set twice
        Assert.Throws<ArgumentException>(() => tcb.SetCellNodeReference(tcb.FindColumn(1), new Nid(0x8045)));   // inline column
        tcb.EndRow();
        Assert.Throws<ArgumentException>(() => tcb.BeginRow(1));                              // repeated row ID
        tcb.Build();
        Assert.Throws<InvalidOperationException>(() => tcb.BeginRow(7));
    }

    [Fact]
    public void ACellMayNameACallerSubnode()
    {
        var store = new BuilderStore();
        var tcb = new TableContextBuilder(store.Blocks, [(0x3701, LtpPropertyTypes.PtypObject)]);
        tcb.BeginRow(1);
        tcb.SetCellNodeReference(tcb.FindColumn(0x3701), new Nid(0x8045));
        tcb.EndRow();
        LtpNodeResult r = tcb.Build();
        using TableContext tc = OpenTc(r, store, out NdbFile f);
        using (f)
        {
            Assert.True(tc.TryFindRow(1, out uint i));
            var buf = new byte[tc.RowSize];
            Assert.True(tc.TryReadRow(i, buf));
            Assert.True(tc.GetRow(buf).TryGetCell(tc.FindColumn(0x3701), out LtpValueRef v));
            Assert.Equal(0x8045u, v.Hnid);
            Assert.Equal(LtpValueKind.Subnode, v.Kind);
        }
    }

    [Fact]
    public void ManyRowsFlowThroughWithAFixedRowBuffer()
    {
        var store = new BuilderStore();
        var tcb = new TableContextBuilder(store.Blocks, SampleColumns);
        for (int i = 0; i < 20_000; i++)
        {
            tcb.BeginRow((uint)(i + 1) * 8);
            tcb.SetCell(tcb.FindColumn(0x0037), System.Text.Encoding.Unicode.GetBytes("Subject " + i));
            tcb.SetCell(tcb.FindColumn(0x0E07), U32((uint)i));
            tcb.EndRow();
        }

        // Row-matrix blocks are written as they fill: by now the sink already holds most of them, before Build.
        Assert.True(store.Written.Count > 20_000 * tcb.RowSize / NdbBlockBuilder.MaxBlockData - 2);
        LtpNodeResult r = tcb.Build();
        using TableContext tc = OpenTc(r, store, out NdbFile f);
        using (f)
        {
            Assert.Equal(20_000, tc.RowCount);
            Assert.Equal(System.Text.Encoding.Unicode.GetBytes("Subject 12345"), Cell(tc, 12346 * 8, 0x0037));
            Assert.Equal(U32(19_999), Cell(tc, 20_000 * 8, 0x0E07));
            Assert.Empty(tc.Defects);
        }
    }
}
