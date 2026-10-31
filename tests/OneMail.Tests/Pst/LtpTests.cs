using System.Buffers.Binary;
using System.Text;
using RecursiveAI.OneMail.Pst.Ltp;
using RecursiveAI.OneMail.Pst.Ndb;
using RecursiveAI.OneMail.Sources;

namespace RecursiveAI.OneMail.Tests.Pst;

public class LtpTests
{
    public static TheoryData<NdbFormat> Formats => NdbTests.Formats;

    internal static NdbFile Open(NdbFormat format, params TestNode[] nodes)
    {
        var built = NdbTestBuilder.Build(format, nodes, new TestBuild { Compress = format == NdbFormat.Unicode4K });
        Assert.True(NdbFile.TryOpen(built.Bytes, out NdbFile? f, out _));
        return f!;
    }

    private static byte[] Utf16(string s) => Encoding.Unicode.GetBytes(s);

    private static byte[] I32(int v) => BitConverter.GetBytes(v);

    private static List<PcSpec> SampleProps(int bigSize) =>
    [
        new PcSpec(0x0037, LtpPropertyTypes.PtypString, Utf16("Quarterly report")),
        new PcSpec(0x0E07, LtpPropertyTypes.PtypInteger32, I32(0x1234_5678)),
        new PcSpec(0x0E08, LtpPropertyTypes.PtypInteger16, BitConverter.GetBytes((short)-7)),
        new PcSpec(0x0E1B, LtpPropertyTypes.PtypBoolean, [1]),
        new PcSpec(0x3007, LtpPropertyTypes.PtypTime, BitConverter.GetBytes(133_000_000_000_000_000L)),
        new PcSpec(0x3008, LtpPropertyTypes.PtypInteger64, BitConverter.GetBytes(long.MinValue + 5)),
        new PcSpec(0x1000, LtpPropertyTypes.PtypBinary, []),
        new PcSpec(0x1013, LtpPropertyTypes.PtypBinary, NdbTests.Pattern(bigSize, 9), InSubnode: true),
        new PcSpec(0x3701, LtpPropertyTypes.PtypBinary, NdbTests.Pattern(5000, 10)),
        new PcSpec(0x3A00, LtpPropertyTypes.PtypString, Utf16("x")),
        new PcSpec(0x6800, (ushort)(LtpPropertyTypes.PtypInteger32 | LtpPropertyTypes.MultiValueFlag), [.. I32(1), .. I32(2), .. I32(3)]),
        new PcSpec(0x6801, (ushort)(LtpPropertyTypes.PtypString | LtpPropertyTypes.MultiValueFlag), MultiString("one", "two", "three")),
    ];

    private static byte[] MultiString(params string[] items)
    {
        var parts = items.Select(Utf16).ToList();
        var result = new List<byte>(I32(parts.Count));
        int at = 4 + (4 * parts.Count);
        foreach (byte[] p in parts) { result.AddRange(I32(at)); at += p.Length; }
        foreach (byte[] p in parts) { result.AddRange(p); }
        return [.. result];
    }

    [Theory, MemberData(nameof(Formats))]
    public void PropertyContextReadsEveryKindOfValue(NdbFormat format)
    {
        int big = 70_000;
        var props = SampleProps(big);
        using NdbFile f = Open(format, PcBuilder.Build(0x2223, props, nodeCapacity: 4));
        Assert.True(PropertyContext.TryOpen(f, new Nid(0x2223), out PropertyContext? pc, out IReadOnlyList<LtpDefect> early));
        Assert.Empty(early);
        using (pc)
        {
            Assert.Equal(props.Count, pc!.Count());
            Assert.Equal(props.Select(p => p.Id).Order(), pc.Select(p => p.Id));
            foreach (PcSpec want in props)
            {
                Assert.True(pc.TryGet(want.Id, out PcProperty got));
                Assert.Equal(want.Type, got.Type);
                Assert.Equal(((uint)want.Id << 16) | want.Type, got.Tag);
                Assert.Equal(want.Value, pc.ReadValue(got) ?? []);
                Assert.Equal(want.Value.Length, pc.GetValueLength(got));
            }

            Assert.False(pc.TryGet(0x0001, out _));
            Assert.Equal(LtpValueKind.Inline, Get(pc, 0x0E07).Value.Kind);
            Assert.Equal(LtpValueKind.Heap, Get(pc, 0x0037).Value.Kind);
            Assert.Equal(LtpValueKind.Empty, Get(pc, 0x1000).Value.Kind);
            Assert.Equal(LtpValueKind.Subnode, Get(pc, 0x1013).Value.Kind);

            // Scalars decode; ranges read in pieces; the 70,000-byte subnode value crosses data blocks.
            Assert.True(LtpValues.TryGetInt32(pc.ReadValue(Get(pc, 0x0E07))!, out int i32));
            Assert.Equal(0x1234_5678, i32);
            Assert.True(LtpValues.TryGetInt16(pc.ReadValue(Get(pc, 0x0E08))!, out short i16));
            Assert.Equal(-7, i16);
            Assert.True(LtpValues.TryGetBoolean(pc.ReadValue(Get(pc, 0x0E1B))!, out bool b));
            Assert.True(b);
            Assert.True(LtpValues.TryGetInt64(pc.ReadValue(Get(pc, 0x3008))!, out long i64));
            Assert.Equal(long.MinValue + 5, i64);
            var piece = new byte[1000];
            Assert.Equal(1000, pc.ReadValue(Get(pc, 0x1013), 65_000, piece));
            Assert.Equal(props.First(p => p.Id == 0x1013).Value.AsSpan(65_000, 1000).ToArray(), piece);
            Assert.Equal(500, pc.ReadValue(Get(pc, 0x1013), big - 500, piece));

            byte[] mv = pc.ReadValue(Get(pc, 0x6801))!;
            Assert.True(LtpValues.TryGetMultiValueCount(LtpPropertyTypes.PtypString | LtpPropertyTypes.MultiValueFlag, mv, out int n));
            Assert.Equal(3, n);
            Assert.True(LtpValues.TryGetMultiValueElement(LtpPropertyTypes.PtypString | LtpPropertyTypes.MultiValueFlag, mv, 1, out int off, out int len));
            Assert.Equal("two", Encoding.Unicode.GetString(mv, off, len));
            byte[] mvi = pc.ReadValue(Get(pc, 0x6800))!;
            Assert.True(LtpValues.TryGetMultiValueElement(LtpPropertyTypes.PtypInteger32 | LtpPropertyTypes.MultiValueFlag, mvi, 2, out off, out len));
            Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(mvi.AsSpan(off, len)));
            Assert.Empty(pc.Defects);
        }

        Assert.Empty(f.Defects);
    }

    private static PcProperty Get(PropertyContext pc, ushort id)
    {
        Assert.True(pc.TryGet(id, out PcProperty p));
        return p;
    }

    [Theory, MemberData(nameof(Formats))]
    public void HeapSpansBlocksIncludingBitmapBlocks(NdbFormat format)
    {
        // Allocations in blocks 0, 3, 8 (the first HNBITMAPHDR block) and 136 (the second one).
        var heap = new HeapBuilder(0xB5, 400);
        heap.EnsureBlocks(140);
        var hids = new Dictionary<int, Hid>();
        foreach (int block in new[] { 0, 3, 8, 9, 136, 137 })
        {
            hids[block] = heap.Alloc(Encoding.ASCII.GetBytes($"block-{block}"), block);
        }

        heap.UserRoot = hids[0];
        using NdbFile f = Open(format, new TestNode { Nid = 0x31, Blocks = heap.Build() });
        Assert.True(HeapNode.TryOpen(f, new Nid(0x31), out HeapNode? h, out _));
        using (h)
        {
            Assert.Equal(140, h!.BlockCount);
            Assert.Equal(0xB5, h.ClientSignature);
            foreach ((int block, Hid hid) in hids)
            {
                Assert.True(h.TryGetAllocation(hid, out HeapAllocation a));
                Assert.Equal(block, a.BlockIndex);
                Assert.Equal($"block-{block}", Encoding.ASCII.GetString(h.GetBytes(a)));
            }

            Assert.Empty(h.Defects);
        }
    }

    [Fact]
    public void BthFindsKeysAcrossLevels()
    {
        var heap = new HeapBuilder(0xB5, 500);
        var records = Enumerable.Range(0, 300).Select(i => (BitConverter.GetBytes((ulong)(i * 7 + 3)), BitConverter.GetBytes((uint)i))).Select(r => (r.Item1, r.Item2)).ToList();
        heap.UserRoot = BthBuilder.Write(heap, 8, 4, records, nodeCapacity: 5);
        using NdbFile f = Open(NdbFormat.Unicode, new TestNode { Nid = 0x31, Blocks = heap.Build() });
        Assert.True(HeapNode.TryOpen(f, new Nid(0x31), out HeapNode? h, out _));
        using (h)
        {
            Assert.True(Bth.TryOpen(h!, h!.UserRoot, out Bth? bth));
            Assert.True(bth!.IndexLevels >= 2);
            var data = new byte[4];
            foreach (int i in new[] { 0, 1, 77, 299 })
            {
                Assert.True(bth.TryFind(BitConverter.GetBytes((ulong)(i * 7 + 3)), data));
                Assert.Equal((uint)i, BitConverter.ToUInt32(data));
            }

            Assert.False(bth.TryFind(BitConverter.GetBytes(4UL), data));
            Assert.False(bth.TryFind(BitConverter.GetBytes(ulong.MaxValue), data));
            Assert.False(bth.TryFind(BitConverter.GetBytes(0UL), data));
            BthCursor c = bth.CreateCursor();
            var seen = new List<ulong>();
            while (c.MoveNext()) { seen.Add(c.KeyValue); }
            Assert.Equal(records.Select(r => BitConverter.ToUInt64(r.Item1)).Order(), seen);
            Assert.Empty(h.Defects);
        }
    }

    [Fact]
    public void EmptyBthEnumeratesNothing()
    {
        var heap = new HeapBuilder(0xBC, 500);
        heap.UserRoot = BthBuilder.Write(heap, 2, 6, []);
        using NdbFile f = Open(NdbFormat.Unicode, new TestNode { Nid = 0x31, Blocks = heap.Build() });
        Assert.True(PropertyContext.TryOpen(f, new Nid(0x31), out PropertyContext? pc, out _));
        using (pc)
        {
            Assert.Equal(0, pc!.Count());
            Assert.False(pc.TryGet(0x37, out _));
        }
    }

    private static TcSpec SampleTable(bool subnodeRows, bool subnodeValues, int rows, int matrixBlock = 8176) => new()
    {
        Columns = [(0x3001, LtpPropertyTypes.PtypString), (0x0E08, LtpPropertyTypes.PtypInteger32), (0x3007, LtpPropertyTypes.PtypTime), (0x0E1B, LtpPropertyTypes.PtypBoolean), (0x0E02, LtpPropertyTypes.PtypInteger16), (0x300B, LtpPropertyTypes.PtypBinary), (0x3FF9, LtpPropertyTypes.PtypGuid)],
        Rows = Enumerable.Range(0, rows).Select(i => ((uint)((i * 32) + 0x2004), new byte[]?[]
        {
            i % 5 == 0 ? null : Utf16($"name {i}"),
            I32(i * 3),
            BitConverter.GetBytes(130_000_000_000_000_000L + i),
            [(byte)(i & 1)],
            BitConverter.GetBytes((short)i),
            i % 7 == 0 ? [] : NdbTests.Pattern(10 + (i % 90), i),
            i % 3 == 0 ? null : Guid.NewGuid().ToByteArray(),
        })).ToList(),
        RowsInSubnode = subnodeRows,
        ValuesInSubnodes = subnodeValues,
        MatrixBlockSize = matrixBlock,
    };

    [Theory, MemberData(nameof(Formats))]
    public void TableContextReadsRowsInHeapAndInSubnode(NdbFormat format)
    {
        foreach ((bool sub, bool vals, int rows) in new[] { (false, false, 20), (true, false, 400), (true, true, 100), (false, true, 30) })
        {
            TcSpec spec = SampleTable(sub, vals, rows, matrixBlock: 2000);
            using NdbFile f = Open(format, TcBuilder.Build(0x80E, spec));
            Assert.True(TableContext.TryOpen(f, new Nid(0x80E), out TableContext? tc, out IReadOnlyList<LtpDefect> early), string.Join(";", early));
            using (tc)
            {
                Assert.Equal(rows, tc!.RowCount);
                Assert.Equal(spec.Columns.Count + 2, tc.Columns.Count);
                var buf = new byte[tc.RowSize];
                for (int r = 0; r < rows; r++)
                {
                    var (rowId, cells) = spec.Rows[r];
                    Assert.True(tc.TryFindRow(rowId, out uint idx));
                    Assert.Equal((uint)r, idx);
                    Assert.True(tc.TryReadRow(idx, buf), $"{sub}/{vals}/{rows} row {r}: " + string.Join(";", tc.Defects) + string.Join(";", f.Defects.Take(3)));
                    TableRow row = tc.GetRow(buf);
                    Assert.Equal(rowId, row.RowId);
                    for (int c = 0; c < spec.Columns.Count; c++)
                    {
                        int col = tc.FindColumn(spec.Columns[c].Id);
                        Assert.True(col >= 2);
                        Assert.Equal(spec.Columns[c].Type, tc.Columns[col].Type);
                        if (cells[c] is null)
                        {
                            Assert.False(row.HasCell(col));
                            Assert.False(row.TryGetCell(col, out _));
                            continue;
                        }

                        Assert.True(row.TryGetCell(col, out LtpValueRef v), $"row {r} col {c}");
                        Assert.Equal(cells[c], tc.ReadValue(v) ?? [0xFF]);
                    }
                }

                Assert.Equal(Enumerable.Range(0, rows).Select(i => (uint)((i * 32) + 0x2004)), tc.EnumerateRowIndex().Select(e => e.RowId));
                Assert.False(tc.TryFindRow(0x9999, out _));
                Assert.False(tc.TryReadRow((uint)rows + 1000, buf));
                Assert.Contains(tc.Defects, d => d.Kind == LtpDefectKind.TcRowOutOfRange);
            }

            Assert.Empty(f.Defects);
        }
    }

    [Fact]
    public void EmptyTableHasNoRows()
    {
        using NdbFile f = Open(NdbFormat.Unicode, TcBuilder.Build(0x80E, SampleTable(false, false, 0)));
        Assert.True(TableContext.TryOpen(f, new Nid(0x80E), out TableContext? tc, out _));
        using (tc)
        {
            Assert.Equal(0, tc!.RowCount);
            Assert.Empty(tc.EnumerateRowIndex());
        }
    }

    [Fact]
    public void SubnodeContextsOpenFromTheirParent()
    {
        // A message-like node whose heap is a PC and which has a recipient-like table as a subnode.
        TestNode pc = PcBuilder.Build(0x2004, SampleProps(100));
        TestNode table = TcBuilder.Build(0x692, SampleTable(false, false, 3));
        var message = new TestNode { Nid = 0x2004, Blocks = pc.Blocks, Subnodes = [.. pc.Subnodes, table] };
        using NdbFile f = Open(NdbFormat.Unicode, message);
        Assert.True(HeapNode.TryOpen(f, new Nid(0x2004), out HeapNode? parent, out _));
        using (parent)
        {
            Assert.True(parent!.TryOpenSubnode(new Nid(0x692), out HeapNode? child, out _));
            using (child)
            {
                Assert.True(TableContext.TryOpen(child!, out TableContext? tc));
                using (tc) { Assert.Equal(3, tc!.RowCount); }
            }

            Assert.False(parent.TryOpenSubnode(new Nid(0x9999), out _, out IReadOnlyList<LtpDefect> d));
            Assert.Equal(LtpDefectKind.SubnodeMissing, d[0].Kind);
        }
    }

    [Fact]
    public void WrongKindOfContextIsRefusedWithATypedDefect()
    {
        using NdbFile f = Open(NdbFormat.Unicode, PcBuilder.Build(0x2004, SampleProps(10)), TcBuilder.Build(0x80E, SampleTable(false, false, 2)));
        Assert.False(TableContext.TryOpen(f, new Nid(0x2004), out _, out IReadOnlyList<LtpDefect> d1));
        Assert.Equal(LtpDefectKind.HeapClientSignatureMismatch, d1[0].Kind);
        Assert.False(PropertyContext.TryOpen(f, new Nid(0x80E), out _, out IReadOnlyList<LtpDefect> d2));
        Assert.Equal(LtpDefectKind.HeapClientSignatureMismatch, d2[0].Kind);
        Assert.False(PropertyContext.TryOpen(f, new Nid(0x7777), out _, out IReadOnlyList<LtpDefect> d3));
        Assert.Equal(LtpDefectKind.NodeNotFound, d3[0].Kind);
    }

    [Fact]
    public void NdbRunsOverAnyRandomAccessSource()
    {
        var built = NdbTestBuilder.Build(NdbFormat.Unicode, [PcBuilder.Build(0x2004, SampleProps(10))]);
        // Short reads are part of the source contract; this one returns at most 100 bytes at a time.
        var ranged = new RangedSource(built.Bytes.Length, (o, m, _) => new ValueTask<int>(Copy(built.Bytes, o, m.Span)), (o, s) => Copy(built.Bytes, o, s));
        Assert.True(NdbFile.TryOpen(ranged, leaveOpen: true, out NdbFile? f, out _));
        using (f)
        {
            Assert.True(PropertyContext.TryOpen(f!, new Nid(0x2004), out PropertyContext? pc, out _));
            using (pc) { Assert.True(pc!.Count() > 5); }
        }

        var asyncOnly = new RangedSource(built.Bytes.Length, (o, m, _) => new ValueTask<int>(Copy(built.Bytes, o, m.Span)));
        Assert.Throws<ArgumentException>(() => NdbFile.TryOpen(asyncOnly, leaveOpen: true, out _, out _));
    }

    private static int Copy(byte[] data, long offset, Span<byte> dest)
    {
        int n = (int)Math.Min(Math.Min(100, dest.Length), data.Length - offset);
        data.AsSpan((int)offset, n).CopyTo(dest);
        return n;
    }
}
