using System.Buffers.Binary;
using RecursiveAI.OneMail.Pst.Ltp;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Tests.Pst;

/// <summary>Damaged heaps, trees and tables: every bad size, HID and NID is a typed defect, never an exception.</summary>
public class LtpHostileTests
{
    private static NdbFile Open(List<byte[]> blocks, params TestNode[] more)
    {
        var nodes = new List<TestNode> { new() { Nid = 0x31, Blocks = blocks } };
        nodes.AddRange(more);
        var built = NdbTestBuilder.Build(NdbFormat.Unicode, nodes);
        Assert.True(NdbFile.TryOpen(built.Bytes, out NdbFile? f, out _));
        return f!;
    }

    private static List<byte[]> PcBlocks(Action<List<byte[]>>? damage = null)
    {
        var heap = new HeapBuilder(0xBC, 400);
        var recs = Enumerable.Range(1, 12).Select(i => (BitConverter.GetBytes((ushort)i), new byte[] { 3, 0, (byte)i, 0, 0, 0 })).ToList();
        recs.Add((BitConverter.GetBytes((ushort)0x100), [0x1F, 0x00, .. BitConverter.GetBytes(heap.Alloc("hello"u8).Value)]));
        heap.UserRoot = BthBuilder.Write(heap, 2, 6, recs, nodeCapacity: 4);
        List<byte[]> blocks = heap.Build();
        damage?.Invoke(blocks);
        return blocks;
    }

    private static int AllocAt(byte[] block, int index)
    {
        int mapAt = BinaryPrimitives.ReadUInt16LittleEndian(block);
        return BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(mapAt + 4 + (2 * (index - 1))));
    }

    private static (bool Opened, List<LtpDefect> Defects) OpenPc(List<byte[]> blocks)
    {
        using NdbFile f = Open(blocks);
        bool ok = PropertyContext.TryOpen(f, new Nid(0x31), out PropertyContext? pc, out IReadOnlyList<LtpDefect> early);
        var all = new List<LtpDefect>(early);
        if (pc is not null)
        {
            using (pc)
            {
                foreach (PcProperty p in pc) { pc.ReadValue(p); }
                pc.TryGet(0x100, out _);
                all.AddRange(pc.Defects);
            }
        }

        return (ok, all);
    }

    [Fact]
    public void CleanBaseHasNoDefects()
    {
        (bool ok, List<LtpDefect> d) = OpenPc(PcBlocks());
        Assert.True(ok);
        Assert.Empty(d);
    }

    [Fact]
    public void RootHidWithTypeBitsIsRefused()
    {
        (bool ok, List<LtpDefect> d) = OpenPc(PcBlocks(b => b[0][4] |= 1));
        Assert.False(ok);
        Assert.Contains(d, x => x.Kind == LtpDefectKind.HidInvalid);
    }

    [Fact]
    public void RootHidWithIndexZeroOrPastTheEndIsRefused()
    {
        foreach (uint hid in new uint[] { 0, 0x20 * 100, (5u << 16) | 0x20 })
        {
            (bool ok, List<LtpDefect> d) = OpenPc(PcBlocks(b => BinaryPrimitives.WriteUInt32LittleEndian(b[0].AsSpan(4), hid)));
            Assert.False(ok);
            Assert.NotEmpty(d);
        }
    }

    [Fact]
    public void PageMapOffsetOutsideTheBlockIsRefused()
    {
        foreach (ushort mapAt in new ushort[] { 0, 5, 11, 0xFFFF })
        {
            (bool ok, List<LtpDefect> d) = OpenPc(PcBlocks(b => BinaryPrimitives.WriteUInt16LittleEndian(b[0], mapAt)));
            Assert.False(ok);
            Assert.Contains(d, x => x.Kind is LtpDefectKind.HeapPageMapInvalid or LtpDefectKind.HeapBadHeader);
        }
    }

    [Fact]
    public void AllocationCountPastTheBlockIsRefused()
    {
        (bool ok, List<LtpDefect> d) = OpenPc(PcBlocks(b =>
        {
            int mapAt = BinaryPrimitives.ReadUInt16LittleEndian(b[0]);
            BinaryPrimitives.WriteUInt16LittleEndian(b[0].AsSpan(mapAt), 0xFFFF);
        }));
        Assert.False(ok);
        Assert.Contains(d, x => x.Kind == LtpDefectKind.HeapPageMapInvalid);
    }

    [Fact]
    public void ReversedAllocationOffsetsAreRefused()
    {
        (bool ok, List<LtpDefect> d) = OpenPc(PcBlocks(b =>
        {
            int mapAt = BinaryPrimitives.ReadUInt16LittleEndian(b[0]);
            // Allocation 1 (the BTH header, the root of the user structure) now ends before it starts.
            BinaryPrimitives.WriteUInt16LittleEndian(b[0].AsSpan(mapAt + 6), 5);
        }));
        Assert.Contains(d, x => x.Kind == LtpDefectKind.HeapPageMapInvalid);
        _ = ok;
    }

    [Fact]
    public void SignatureAndClientTypeAreChecked()
    {
        (bool ok, List<LtpDefect> d) = OpenPc(PcBlocks(b => b[0][2] = 0x00));
        Assert.False(ok);
        Assert.Equal(LtpDefectKind.HeapBadSignature, d[0].Kind);
        (ok, d) = OpenPc(PcBlocks(b => b[0][3] = 0x7C));
        Assert.False(ok);
        Assert.Equal(LtpDefectKind.HeapClientSignatureMismatch, d[0].Kind);
    }

    // The user root of PcBlocks is the BTH header: bType, cbKey, cbEnt, bIdxLevels, hidRoot. Find it through the page map.
    private static int HeaderOffset(List<byte[]> blocks)
    {
        uint userRoot = BinaryPrimitives.ReadUInt32LittleEndian(blocks[0].AsSpan(4));
        int index = (int)((userRoot >> 5) & 0x7FF);
        return AllocAt(blocks[(int)(userRoot >> 16)], index);
    }

    [Fact]
    public void BadBthHeadersAreRefused()
    {
        foreach ((int at, byte value, LtpDefectKind kind) in new[] { (0, (byte)0x00, LtpDefectKind.BthBadHeader), (1, (byte)3, LtpDefectKind.BthBadHeader), (2, (byte)0, LtpDefectKind.BthBadHeader), (2, (byte)33, LtpDefectKind.BthBadHeader), (3, (byte)200, LtpDefectKind.BthTooDeep) })
        {
            (bool ok, List<LtpDefect> d) = OpenPc(PcBlocks(b => b[0][HeaderOffset(b) + at] = value));
            Assert.False(ok);
            Assert.Contains(d, x => x.Kind == kind);
        }

        // 2-byte keys and 6-byte entries are what makes a BTH a property context.
        (bool shape, List<LtpDefect> ds) = OpenPc(PcBlocks(b => b[0][HeaderOffset(b) + 1] = 4));
        Assert.False(shape);
        Assert.Contains(ds, x => x.Kind is LtpDefectKind.PcBadShape or LtpDefectKind.BthNodeSize);
    }

    [Fact]
    public void WrongIndexLevelsAreSurvived()
    {
        for (byte levels = 0; levels < 6; levels++)
        {
            (bool ok, List<LtpDefect> d) = OpenPc(PcBlocks(b => b[0][HeaderOffset(b) + 3] = levels));
            _ = ok;
            _ = d;
        }
    }

    [Fact]
    public void AnIndexNodeThatPointsAtItselfIsCutOff()
    {
        var heap = new HeapBuilder(0xB5, 4000);
        // One allocation that is both the root and every child: 100 records of (2-byte key, HID of itself) at 7 index levels.
        Hid self = new((1u << 5) | 0);
        var node = new byte[100 * 6];
        for (int i = 0; i < 100; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(i * 6), (ushort)(i + 1));
            BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan((i * 6) + 2), (1u << 5));
        }

        Hid root = heap.Alloc(node);
        Assert.Equal(1u << 5, root.Value);
        _ = self;
        var header = new byte[8];
        header[0] = 0xB5; header[1] = 2; header[2] = 4; header[3] = 7;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), root.Value);
        heap.UserRoot = heap.Alloc(header);
        using NdbFile f = Open(heap.Build());
        Assert.True(HeapNode.TryOpen(f, new Nid(0x31), out HeapNode? h, out _));
        using (h)
        {
            Assert.True(Bth.TryOpen(h!, h!.UserRoot, out Bth? bth));
            int n = 0;
            BthCursor c = bth!.CreateCursor();
            while (c.MoveNext()) { n++; }
            Assert.Contains(h.Defects, x => x.Kind is LtpDefectKind.BthTooManyNodes or LtpDefectKind.BthKeyOrder);
            Assert.True(n < 100_000);
        }
    }

    [Fact]
    public void KeysOutOfOrderAreSkippedWithADefect()
    {
        var heap = new HeapBuilder(0xB5, 4000);
        var leaf = new byte[5 * 6];
        foreach ((int i, ushort key) in new[] { (0, (ushort)10), (1, (ushort)20), (2, (ushort)15), (3, (ushort)20), (4, (ushort)30) })
        {
            BinaryPrimitives.WriteUInt16LittleEndian(leaf.AsSpan(i * 6), key);
        }

        Hid root = heap.Alloc(leaf);
        var header = new byte[8];
        header[0] = 0xB5; header[1] = 2; header[2] = 4;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), root.Value);
        heap.UserRoot = heap.Alloc(header);
        using NdbFile f = Open(heap.Build());
        Assert.True(HeapNode.TryOpen(f, new Nid(0x31), out HeapNode? h, out _));
        using (h)
        {
            Assert.True(Bth.TryOpen(h!, h!.UserRoot, out Bth? bth));
            var keys = new List<ulong>();
            BthCursor c = bth!.CreateCursor();
            while (c.MoveNext()) { keys.Add(c.KeyValue); }
            Assert.Equal(new ulong[] { 10, 20, 30 }, keys);
            Assert.Equal(2, h.Defects.Count(x => x.Kind == LtpDefectKind.BthKeyOrder));
        }
    }

    [Fact]
    public void MissingSubnodeValueIsATypedDefect()
    {
        var heap = new HeapBuilder(0xBC, 4000);
        var entry = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(entry, LtpPropertyTypes.PtypBinary);
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(2), 0x6F1);   // a subnode NID that is not there
        heap.UserRoot = BthBuilder.Write(heap, 2, 6, [(BitConverter.GetBytes((ushort)0x3701), entry)]);
        using NdbFile f = Open(heap.Build());
        Assert.True(PropertyContext.TryOpen(f, new Nid(0x31), out PropertyContext? pc, out _));
        using (pc)
        {
            Assert.True(pc!.TryGet(0x3701, out PcProperty p));
            Assert.Equal(LtpValueKind.Subnode, p.Value.Kind);
            Assert.Equal(-1, pc.GetValueLength(p));
            Assert.Null(pc.ReadValue(p));
            Assert.Equal(0, pc.ReadValue(p, 0, new byte[10]));
            Assert.Contains(pc.Defects, x => x.Kind == LtpDefectKind.SubnodeMissing);
        }
    }

    [Fact]
    public void ValueHidOutsideTheHeapIsATypedDefect()
    {
        var heap = new HeapBuilder(0xBC, 4000);
        var entry = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(entry, LtpPropertyTypes.PtypString);
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(2), (9u << 16) | (3u << 5));
        heap.UserRoot = BthBuilder.Write(heap, 2, 6, [(BitConverter.GetBytes((ushort)0x37), entry)]);
        using NdbFile f = Open(heap.Build());
        Assert.True(PropertyContext.TryOpen(f, new Nid(0x31), out PropertyContext? pc, out _));
        using (pc)
        {
            Assert.True(pc!.TryGet(0x37, out PcProperty p));
            Assert.Null(pc.ReadValue(p));
            Assert.Contains(pc.Defects, x => x.Kind == LtpDefectKind.HeapBlockMissing);
        }
    }

    [Fact]
    public void MultiValueNonsenseIsRefusedNotThrown()
    {
        ushort mvString = LtpPropertyTypes.PtypString | LtpPropertyTypes.MultiValueFlag;
        Assert.False(LtpValues.TryGetMultiValueCount(mvString, [], out _));
        Assert.False(LtpValues.TryGetMultiValueCount(mvString, [0xFF, 0xFF, 0xFF, 0xFF, 1, 2, 3, 4], out _));
        Assert.False(LtpValues.TryGetMultiValueCount(LtpPropertyTypes.PtypString, [1, 2, 3, 4], out _));
        Assert.False(LtpValues.TryGetMultiValueCount((ushort)(LtpPropertyTypes.PtypInteger32 | LtpPropertyTypes.MultiValueFlag), [1, 2, 3, 4, 5], out _));
        // Two elements, offsets going backwards.
        byte[] backwards = [2, 0, 0, 0, 20, 0, 0, 0, 12, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        Assert.True(LtpValues.TryGetMultiValueCount(mvString, backwards, out int n));
        Assert.Equal(2, n);
        Assert.False(LtpValues.TryGetMultiValueElement(mvString, backwards, 0, out _, out _));
        Assert.False(LtpValues.TryGetMultiValueElement(mvString, backwards, 5, out _, out _));
        Assert.False(LtpValues.TryGetMultiValueElement(mvString, backwards, -1, out _, out _));
        // An offset into the offset table itself.
        byte[] into = [1, 0, 0, 0, 2, 0, 0, 0, 9, 9];
        Assert.False(LtpValues.TryGetMultiValueElement(mvString, into, 0, out _, out _));
    }

    // ---- tables ----

    private static List<byte[]> TcBlocks(bool rowsInSubnode, out TestNode node, Action<List<byte[]>>? damage = null)
    {
        var spec = new TcSpec
        {
            Columns = [(0x3001, LtpPropertyTypes.PtypString), (0x0E08, LtpPropertyTypes.PtypInteger32)],
            Rows = Enumerable.Range(0, 5).Select(i => ((uint)(0x2004 + (i * 32)), new byte[]?[] { "name"u8.ToArray(), BitConverter.GetBytes(i) })).ToList(),
            RowsInSubnode = rowsInSubnode,
        };
        node = TcBuilder.Build(0x31, spec);
        var blocks = node.Blocks!.Select(b => (byte[])b.Clone()).ToList();
        damage?.Invoke(blocks);
        return blocks;
    }

    private static (bool Opened, List<LtpDefect> Defects) OpenTc(bool rowsInSubnode, Action<List<byte[]>>? damage = null)
    {
        List<byte[]> blocks = TcBlocks(rowsInSubnode, out TestNode node, damage);
        var built = NdbTestBuilder.Build(NdbFormat.Unicode, [new TestNode { Nid = 0x31, Blocks = blocks, Subnodes = node.Subnodes }]);
        Assert.True(NdbFile.TryOpen(built.Bytes, out NdbFile? g, out _));
        using (g)
        {
            bool ok = TableContext.TryOpen(g!, new Nid(0x31), out TableContext? tc, out IReadOnlyList<LtpDefect> early);
            var all = new List<LtpDefect>(early);
            if (tc is not null)
            {
                using (tc)
                {
                    var row = new byte[Math.Max(1, tc.RowSize)];
                    int rows = tc.RowCount;
                    for (uint r = 0; r < (damage is null ? rows : rows + 3); r++)
                    {
                        if (!tc.TryReadRow(r, row)) { continue; }
                        TableRow tr = tc.GetRow(row);
                        for (int c = 0; c < tc.Columns.Count; c++) { if (tr.TryGetCell(c, out LtpValueRef v)) { tc.ReadValue(v); } }
                    }

                    tc.TryFindRow(0x2004, out _);
                    all.AddRange(tc.Defects);
                }
            }

            return (ok, all);
        }
    }

    private static int TcInfoOffset(List<byte[]> b)
    {
        uint userRoot = BinaryPrimitives.ReadUInt32LittleEndian(b[0].AsSpan(4));
        return AllocAt(b[(int)(userRoot >> 16)], (int)((userRoot >> 5) & 0x7FF));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CleanTableHasNoDefects(bool sub)
    {
        (bool ok, List<LtpDefect> d) = OpenTc(sub);
        Assert.True(ok);
        Assert.True(d.Count == 0, string.Join("; ", d));
    }

    [Fact]
    public void BadTableInfoIsRefused()
    {
        (bool ok, List<LtpDefect> d) = OpenTc(false, b => b[0][TcInfoOffset(b)] = 0x00);
        Assert.False(ok);
        Assert.Contains(d, x => x.Kind == LtpDefectKind.TcBadInfo);
        (ok, d) = OpenTc(false, b => b[0][TcInfoOffset(b) + 1] = 255);   // 255 columns do not fit
        Assert.False(ok);
        Assert.Contains(d, x => x.Kind == LtpDefectKind.TcBadInfo);
    }

    [Fact]
    public void ImpossibleRowLayoutIsReportedAndSurvived()
    {
        // The end offsets run backwards.
        (bool ok, List<LtpDefect> d) = OpenTc(false, b => BinaryPrimitives.WriteUInt16LittleEndian(b[0].AsSpan(TcInfoOffset(b) + 2), 0x7000));
        Assert.True(ok);
        Assert.Contains(d, x => x.Kind == LtpDefectKind.TcBadLayout);
        // A zero-size row.
        (ok, d) = OpenTc(false, b => { for (int i = 2; i < 10; i++) { b[0][TcInfoOffset(b) + i] = 0; } });
        Assert.True(ok);
        Assert.Contains(d, x => x.Kind is LtpDefectKind.TcBadLayout or LtpDefectKind.TcRowOutOfRange);
        // A column far outside the row.
        (ok, d) = OpenTc(false, b => BinaryPrimitives.WriteUInt16LittleEndian(b[0].AsSpan(TcInfoOffset(b) + 22 + 8 + 4), 0xF000));
        Assert.True(ok);
        Assert.Contains(d, x => x.Kind == LtpDefectKind.TcBadLayout);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RowMatrixPointingNowhereGivesRowDefects(bool sub)
    {
        // hnidRows becomes a HID in block 7 / a subnode that does not exist.
        (bool ok, List<LtpDefect> d) = OpenTc(sub, b => BinaryPrimitives.WriteUInt32LittleEndian(b[0].AsSpan(TcInfoOffset(b) + 14), sub ? 0x7FF1Fu : (7u << 16) | 0x20));
        Assert.True(ok);
        Assert.Contains(d, x => x.Kind == LtpDefectKind.TcRowOutOfRange);
    }

    [Fact]
    public void RowIndexOfTheWrongShapeIsReported()
    {
        (bool ok, List<LtpDefect> d) = OpenTc(false, b =>
        {
            uint rowIndexHid = BinaryPrimitives.ReadUInt32LittleEndian(b[0].AsSpan(TcInfoOffset(b) + 10));
            b[0][AllocAt(b[0], (int)((rowIndexHid >> 5) & 0x7FF)) + 2] = 8;   // cbEnt 8 instead of 4
        });
        Assert.True(ok);
        Assert.Contains(d, x => x.Kind == LtpDefectKind.TcRowIndexShape);
    }

    [Fact]
    public void FuzzReproCorruptDeflateStreamIsATypedDefect()
    {
        // Found by the ltp fuzz target: the framework's inflater threw an IOException ("unexpected error code") on a corrupt
        // compressed block instead of InvalidDataException.
        byte[] repro = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", "..", "..", "testdata", "fuzz", "ltp-corrupt-deflate-stream.bin"));
        byte[] file = LtpWalk.FuzzFile(repro);
        Assert.True(NdbFile.TryOpen(file, out NdbFile? f, out _));
        using (f)
        {
            LtpWalk.ReadEverything(f!);
            Assert.Contains(f!.Defects, d => d.Kind == NdbDefectKind.BlockDecompressionFailed);
        }
    }

    private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    // ---- random damage ----

    private static (byte[] File, long From, long To) BaseFile(NdbFormat format)
    {
        var big = NdbTests.Pattern(9000, 4);
        var nodes = new List<TestNode>
        {
            PcBuilder.Build(0x2004, [new PcSpec(0x37, LtpPropertyTypes.PtypString, "subject"u8.ToArray()), new PcSpec(0x0E07, LtpPropertyTypes.PtypInteger32, [1, 0, 0, 0]), new PcSpec(0x1013, LtpPropertyTypes.PtypBinary, big, InSubnode: true), new PcSpec(0x3701, LtpPropertyTypes.PtypBinary, big[..3000]), new PcSpec(0x6801, (ushort)(LtpPropertyTypes.PtypString | LtpPropertyTypes.MultiValueFlag), [2, 0, 0, 0, 12, 0, 0, 0, 14, 0, 0, 0, 65, 0, 66, 0])], nodeCapacity: 2, blockCapacity: 120),
            TcBuilder.Build(0x80E, new TcSpec { Columns = [(0x3001, LtpPropertyTypes.PtypString), (0x0E08, LtpPropertyTypes.PtypInteger32), (0x3FF9, LtpPropertyTypes.PtypGuid)], Rows = Enumerable.Range(0, 40).Select(i => ((uint)(0x2004 + (i * 32)), new byte[]?[] { i % 4 == 0 ? null : "abcdefgh"u8.ToArray(), BitConverter.GetBytes(i), Guid.NewGuid().ToByteArray() })).ToList(), RowsInSubnode = true, MatrixBlockSize = 400, BlockCapacity = 300 }),
            TcBuilder.Build(0x80F, new TcSpec { Columns = [(0x3001, LtpPropertyTypes.PtypString)], Rows = Enumerable.Range(0, 6).Select(i => ((uint)(0x2004 + (i * 32)), new byte[]?[] { "xyz"u8.ToArray() })).ToList() }),
        };
        var built = NdbTestBuilder.Build(format, nodes, new TestBuild { Compress = format == NdbFormat.Unicode4K });
        return (built.Bytes, built.BlockOffsets.Values.Min(), built.BlockOffsets.Values.Max() + 64);
    }

    [Theory]
    [InlineData(NdbFormat.Ansi)]
    [InlineData(NdbFormat.Unicode)]
    [InlineData(NdbFormat.Unicode4K)]
    public void RandomDamageToBlocksNeverThrows(NdbFormat format)
    {
        (byte[] file, long from, long to) = BaseFile(format);
        using (NdbFile.TryOpen(file, out NdbFile? clean, out _) ? clean : null)
        {
            (int contexts, int values) = LtpWalk.ReadEverything(clean!);
            Assert.True(contexts == 3 && values > 100, $"contexts {contexts} values {values}");
            Assert.Empty(clean!.Defects);
        }

        var rng = new Random(20261031 + (int)format);
        int opened = 0;
        for (int iteration = 0; iteration < 400; iteration++)
        {
            byte[] damaged = (byte[])file.Clone();
            int hits = 1 + rng.Next(8);
            for (int h = 0; h < hits; h++)
            {
                damaged[from + rng.NextInt64(to - from)] = (byte)rng.Next(256);
            }

            if (NdbFile.TryOpen(damaged, out NdbFile? f, out _) && f is not null)
            {
                using (f)
                {
                    LtpWalk.ReadEverything(f);
                    opened++;
                }
            }
        }

        Assert.True(opened > 300);
    }
}
