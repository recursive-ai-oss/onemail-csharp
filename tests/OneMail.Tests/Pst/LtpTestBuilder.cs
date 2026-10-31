using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using RecursiveAI.OneMail.Pst.Ltp;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Tests.Pst;

/// <summary>
/// Writes heap-on-node, BTH, property-context and table-context structures straight from MS-PST 2.3 into <see cref="TestNode"/>s,
/// so the LTP reader can be tested on all three file generations and then damaged. Test-side only; the library has no LTP writer.
/// </summary>
public sealed class HeapBuilder(byte clientSignature, int blockCapacity = 3000)
{
    private readonly List<List<byte[]>> _blocks = [[]];
    public Hid UserRoot { get; set; }

    private static int HeaderSize(int block) => block == 0 ? 12 : block >= 8 && (block - 8) % 128 == 0 ? 66 : 2;

    private int Used(int block) => HeaderSize(block) + _blocks[block].Sum(a => a.Length) + 4 + (2 * (_blocks[block].Count + 1));

    /// <summary>Adds an allocation to the last block with room (or to <paramref name="block"/>), starting a new block when needed.</summary>
    public Hid Alloc(ReadOnlySpan<byte> data, int? block = null)
    {
        int b = block ?? _blocks.Count - 1;
        if (block is null && Used(b) + data.Length + 2 > blockCapacity)
        {
            _blocks.Add([]);
            b = _blocks.Count - 1;
        }

        while (_blocks.Count <= b) { _blocks.Add([]); }
        _blocks[b].Add(data.ToArray());
        return new Hid((uint)((b << 16) | (_blocks[b].Count << 5)));
    }

    /// <summary>Makes sure the heap has at least <paramref name="count"/> blocks (empty ones are valid).</summary>
    public void EnsureBlocks(int count) { while (_blocks.Count < count) { _blocks.Add([]); } }

    public List<byte[]> Build()
    {
        var result = new List<byte[]>();
        for (int b = 0; b < _blocks.Count; b++)
        {
            int hdr = HeaderSize(b);
            int dataLen = _blocks[b].Sum(a => a.Length);
            int mapAt = hdr + dataLen;
            var blk = new byte[mapAt + 4 + (2 * (_blocks[b].Count + 1))];
            BinaryPrimitives.WriteUInt16LittleEndian(blk, (ushort)mapAt);
            if (b == 0)
            {
                blk[2] = 0xEC; blk[3] = clientSignature;
                BinaryPrimitives.WriteUInt32LittleEndian(blk.AsSpan(4), UserRoot.Value);
            }

            int at = hdr;
            BinaryPrimitives.WriteUInt16LittleEndian(blk.AsSpan(mapAt), (ushort)_blocks[b].Count);
            for (int i = 0; i < _blocks[b].Count; i++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(blk.AsSpan(mapAt + 4 + (2 * i)), (ushort)at);
                _blocks[b][i].CopyTo(blk, at);
                at += _blocks[b][i].Length;
            }

            BinaryPrimitives.WriteUInt16LittleEndian(blk.AsSpan(mapAt + 4 + (2 * _blocks[b].Count)), (ushort)at);
            result.Add(blk);
        }

        return result;
    }
}

public static class BthBuilder
{
    /// <summary>Writes a BTH into the heap and returns the HID of its header. Records are (key, entry) and are sorted here.</summary>
    public static Hid Write(HeapBuilder heap, int keySize, int entSize, IEnumerable<(byte[] Key, byte[] Data)> records, int nodeCapacity = 100)
    {
        var sorted = records.OrderBy(r => BitConverter.ToString(r.Key.Reverse().ToArray())).ToList();
        Hid root = default;
        int levels = 0;
        if (sorted.Count > 0)
        {
            // Leaves first, then index levels of (first key, HID of child) until one node is left.
            var level = new List<(byte[] Key, Hid Hid)>();
            foreach (var chunk in sorted.Chunk(nodeCapacity))
            {
                var node = new byte[chunk.Length * (keySize + entSize)];
                for (int i = 0; i < chunk.Length; i++)
                {
                    chunk[i].Key.CopyTo(node, i * (keySize + entSize));
                    chunk[i].Data.CopyTo(node, (i * (keySize + entSize)) + keySize);
                }

                level.Add((chunk[0].Key, heap.Alloc(node)));
            }

            while (level.Count > 1)
            {
                levels++;
                var next = new List<(byte[] Key, Hid Hid)>();
                foreach (var chunk in level.Chunk(Math.Max(2, nodeCapacity)))
                {
                    var node = new byte[chunk.Length * (keySize + 4)];
                    for (int i = 0; i < chunk.Length; i++)
                    {
                        chunk[i].Key.CopyTo(node, i * (keySize + 4));
                        BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan((i * (keySize + 4)) + keySize), chunk[i].Hid.Value);
                    }

                    next.Add((chunk[0].Key, heap.Alloc(node)));
                }

                level = next;
            }

            root = level[0].Hid;
        }

        var header = new byte[8];
        header[0] = 0xB5; header[1] = (byte)keySize; header[2] = (byte)entSize; header[3] = (byte)levels;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), root.Value);
        return heap.Alloc(header);
    }
}

/// <summary>A property to put in a generated property context.</summary>
public sealed record PcSpec(ushort Id, ushort Type, byte[] Value, bool InSubnode = false, uint ForcedHnid = 0);

public static class PcBuilder
{
    public static TestNode Build(uint nid, IEnumerable<PcSpec> props, int nodeCapacity = 100, int blockCapacity = 3000)
    {
        var heap = new HeapBuilder(0xBC, blockCapacity);
        var subs = new List<TestNode>();
        var records = new List<(byte[] Key, byte[] Data)>();
        foreach (PcSpec p in props)
        {
            var entry = new byte[6];
            BinaryPrimitives.WriteUInt16LittleEndian(entry, p.Type);
            int fixedSize = LtpPropertyTypes.FixedSize(p.Type);
            uint hnid = 0;
            if (fixedSize is > 0 and <= 4)
            {
                Array.Copy(p.Value, 0, entry, 2, Math.Min(4, p.Value.Length));
                records.Add((BitConverter.GetBytes(p.Id), entry));
                continue;
            }

            if (p.ForcedHnid != 0)
            {
                hnid = p.ForcedHnid;   // the caller supplies the subnode (an embedded message, say)
            }
            else if (p.InSubnode)
            {
                hnid = (uint)(((subs.Count + 1) << 5) | 0x1F);
                subs.Add(new TestNode { Nid = hnid, Data = p.Value });
            }
            else if (p.Value.Length > 0)
            {
                hnid = heap.Alloc(p.Value).Value;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(2), hnid);
            records.Add((BitConverter.GetBytes(p.Id), entry));
        }

        heap.UserRoot = BthBuilder.Write(heap, 2, 6, records, nodeCapacity);
        return new TestNode { Nid = nid, Blocks = heap.Build(), Subnodes = subs };
    }
}

/// <summary>A table to put in a generated table context. A null cell is absent from its row; a cell is the raw value bytes.</summary>
public sealed class TcSpec
{
    public required List<(ushort Id, ushort Type)> Columns { get; init; }
    public required List<(uint RowId, byte[]?[] Cells)> Rows { get; init; }
    public bool RowsInSubnode { get; init; }
    public bool ValuesInSubnodes { get; init; }
    public int BlockCapacity { get; init; } = 3000;
    /// <summary>For a subnode matrix: the data block size the rows are packed into (rows per block = this / row size).</summary>
    public int MatrixBlockSize { get; init; } = 8176;
}

public static class TcBuilder
{
    public static TestNode Build(uint nid, TcSpec spec)
    {
        var heap = new HeapBuilder(0x7C, spec.BlockCapacity);
        // Column 0 and 1 are the row ID and row version the format puts first; the rest follow, sorted by tag.
        var cols = new List<(ushort Id, ushort Type)> { (0x67F2, 3), (0x67F3, 3) };
        cols.AddRange(spec.Columns.OrderBy(c => ((uint)c.Id << 16) | c.Type));
        int n = cols.Count;
        int SizeOf((ushort Id, ushort Type) c) { int f = LtpPropertyTypes.FixedSize(c.Type); return f is > 0 and <= 8 ? f : 4; }

        // MS-PST 2.3.4.1: 8- and 4-byte cells, then 2-byte cells, then 1-byte cells, then the existence bitmap.
        var offsets = new int[n];
        int at = 0;
        offsets[0] = 0; offsets[1] = 4; at = 8;   // the row ID and version always lead the row
        foreach (int s in new[] { 8, 4 }) { for (int i = 2; i < n; i++) { if (SizeOf(cols[i]) == s) { offsets[i] = at; at += s; } } }
        int end4 = at;
        for (int i = 0; i < n; i++) { if (SizeOf(cols[i]) == 2) { offsets[i] = at; at += 2; } }
        int end2 = at;
        for (int i = 0; i < n; i++) { if (SizeOf(cols[i]) == 1) { offsets[i] = at; at += 1; } }
        int end1 = at;
        int rowSize = end1 + ((n + 7) / 8);

        var subs = new List<TestNode>();
        int subSeq = 0;
        var rows = new List<byte[]>();
        var rowIndex = new List<(byte[] Key, byte[] Data)>();
        uint rowNo = 0;
        foreach (var (rowId, cells) in spec.Rows)
        {
            var row = new byte[rowSize];
            BinaryPrimitives.WriteUInt32LittleEndian(row, rowId);
            BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(offsets[1]), 1);
            row[end1] = 0xC0;   // the row ID and row version cells always exist
            for (int c = 0; c < spec.Columns.Count; c++)
            {
                byte[]? v = cells[c];
                if (v is null) { continue; }
                int ci = cols.FindIndex(2, x => x == spec.Columns[c]);
                row[end1 + (ci >> 3)] |= (byte)(0x80 >> (ci & 7));
                int size = SizeOf(cols[ci]);
                if (size == LtpPropertyTypes.FixedSize(cols[ci].Type) && LtpPropertyTypes.FixedSize(cols[ci].Type) is > 0 and <= 8)
                {
                    v.CopyTo(row, offsets[ci]);
                }
                else
                {
                    uint hnid = 0;
                    if (spec.ValuesInSubnodes && v.Length > 0)
                    {
                        hnid = (uint)(((++subSeq + 100) << 5) | 0x1F);
                        subs.Add(new TestNode { Nid = hnid, Data = v });
                    }
                    else if (v.Length > 0)
                    {
                        hnid = heap.Alloc(v).Value;
                    }

                    BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(offsets[ci]), hnid);
                }
            }

            rowIndex.Add((BitConverter.GetBytes(rowId), BitConverter.GetBytes(rowNo++)));
            rows.Add(row);
        }

        Hid rowIndexHid = rows.Count == 0 ? default : BthBuilder.Write(heap, 4, 4, rowIndex, 50);
        uint hnidRows = 0;
        if (rows.Count > 0)
        {
            if (spec.RowsInSubnode)
            {
                int perBlock = Math.Max(1, spec.MatrixBlockSize / rowSize);
                var blocks = rows.Chunk(perBlock).Select(ch => ch.SelectMany(r => r).ToArray()).ToList();
                uint subNid = 0x80 | 0x1F;
                subs.Add(new TestNode { Nid = subNid, Blocks = blocks });
                hnidRows = subNid;
            }
            else
            {
                hnidRows = heap.Alloc(rows.SelectMany(r => r).ToArray()).Value;
            }
        }

        var info = new byte[22 + (8 * n)];
        info[0] = 0x7C; info[1] = (byte)n;
        BinaryPrimitives.WriteUInt16LittleEndian(info.AsSpan(2), (ushort)end4);
        BinaryPrimitives.WriteUInt16LittleEndian(info.AsSpan(4), (ushort)end2);
        BinaryPrimitives.WriteUInt16LittleEndian(info.AsSpan(6), (ushort)end1);
        BinaryPrimitives.WriteUInt16LittleEndian(info.AsSpan(8), (ushort)rowSize);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(10), rowIndexHid.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(14), hnidRows);
        for (int i = 0; i < n; i++)
        {
            Span<byte> d = info.AsSpan(22 + (8 * i), 8);
            BinaryPrimitives.WriteUInt32LittleEndian(d, ((uint)cols[i].Id << 16) | cols[i].Type);
            BinaryPrimitives.WriteUInt16LittleEndian(d[4..], (ushort)offsets[i]);
            d[6] = (byte)SizeOf(cols[i]); d[7] = (byte)i;
        }

        heap.UserRoot = heap.Alloc(info);
        return new TestNode { Nid = nid, Blocks = heap.Build(), Subnodes = subs.OrderBy(s => s.Nid).ToList() };
    }
}

/// <summary>Reads every heap, property context and table context in a file, the way a damaged-input test or a fuzz target wants it: everything, never throwing.</summary>
public static class LtpWalk
{
    private static readonly (byte[] File, long From, long To)[] Bases = [FuzzBase(NdbFormat.Ansi), FuzzBase(NdbFormat.Unicode), FuzzBase(NdbFormat.Unicode4K)];

    // Small multi-block heaps, a multi-level BTH, a subnode value, a multi-valued value, and tables with rows in the heap and in a subnode.
    private static (byte[], long, long) FuzzBase(NdbFormat format)
    {
        var big = new byte[9000];
        new Random(5).NextBytes(big);
        var nodes = new List<TestNode>
        {
            PcBuilder.Build(0x2004, [new PcSpec(0x37, LtpPropertyTypes.PtypString, "subject"u8.ToArray()), new PcSpec(0x0E07, LtpPropertyTypes.PtypInteger32, [1, 0, 0, 0]), new PcSpec(0x1013, LtpPropertyTypes.PtypBinary, big, InSubnode: true), new PcSpec(0x3701, LtpPropertyTypes.PtypBinary, big[..3000]), new PcSpec(0x6801, (ushort)(LtpPropertyTypes.PtypString | LtpPropertyTypes.MultiValueFlag), [2, 0, 0, 0, 12, 0, 0, 0, 14, 0, 0, 0, 65, 0, 66, 0])], nodeCapacity: 2, blockCapacity: 120),
            TcBuilder.Build(0x80E, new TcSpec { Columns = [(0x3001, LtpPropertyTypes.PtypString), (0x0E08, LtpPropertyTypes.PtypInteger32), (0x3FF9, LtpPropertyTypes.PtypGuid)], Rows = Enumerable.Range(0, 40).Select(i => ((uint)(0x2004 + (i * 32)), new byte[]?[] { i % 4 == 0 ? null : "abcdefgh"u8.ToArray(), BitConverter.GetBytes(i), new byte[16] })).ToList(), RowsInSubnode = true, MatrixBlockSize = 400, BlockCapacity = 300 }),
            TcBuilder.Build(0x80F, new TcSpec { Columns = [(0x3001, LtpPropertyTypes.PtypString)], Rows = Enumerable.Range(0, 6).Select(i => ((uint)(0x2004 + (i * 32)), new byte[]?[] { "xyz"u8.ToArray() })).ToList() }),
        };
        var built = NdbTestBuilder.Build(format, nodes, new TestBuild { Compress = format == NdbFormat.Unicode4K });
        return (built.Bytes, built.BlockOffsets.Values.Min(), built.BlockOffsets.Values.Max() + 64);
    }

    /// <summary>
    /// The file a fuzz input stands for. Byte 0, bits 0-1: which valid file (0 ANSI, 1 Unicode, 2 4 KiB; 3 = the rest of the input is the file);
    /// then 4-byte records of a 24-bit offset (taken modulo the data region) and the byte to put there. A patched block keeps its old
    /// checksum, which the block reader reports and carries on from, so patches reach the LTP code.
    /// </summary>
    public static byte[] FuzzFile(ReadOnlySpan<byte> input)
    {
        int which = input[0] & 3;
        if (which == 3)
        {
            return input[1..].ToArray();
        }

        (byte[] baseFile, long from, long to) = Bases[which];
        byte[] file = (byte[])baseFile.Clone();
        for (int i = 1; i + 3 < input.Length; i += 4)
        {
            file[from + ((input[i] | (input[i + 1] << 8) | (input[i + 2] << 16)) % (to - from))] = input[i + 3];
        }

        return file;
    }

    /// <summary>Opens each node, then each subnode, as a PC or TC by its client signature and reads every value. Stops after <paramref name="budget"/> operations.</summary>
    public static (int Contexts, int Values) ReadEverything(NdbFile f, int budget = 20000)
    {
        int contexts = 0, values = 0;
        var buf = new byte[4096];

        void ReadValue(HeapNode h, LtpValueRef v)
        {
            long len = h.GetValueLength(v);
            // Only the start of a huge value: the point is that reading is safe, not that it is complete.
            for (long o = 0; o < Math.Min(len, 3 * buf.Length) && budget-- > 0; o += buf.Length) { if (h.ReadValue(v, o, buf) <= 0) { break; } }
            values++;
        }

        void Visit(HeapNode h, int depth)
        {
            contexts++;
            if (h.ClientSignature == 0xBC && PropertyContext.TryOpen(h, out PropertyContext? pc) && pc is not null)
            {
                foreach (PcProperty p in pc)
                {
                    if (budget-- <= 0) { return; }
                    ReadValue(h, p.Value);
                    if (LtpPropertyTypes.IsMultiValue(p.Type) && h.ReadValue(p.Value) is { } mv && LtpValues.TryGetMultiValueCount(p.Type, mv, out int n))
                    {
                        for (int i = 0; i < Math.Min(n, 50); i++) { LtpValues.TryGetMultiValueElement(p.Type, mv, i, out _, out _); }
                    }
                }

                pc.TryGet(0x3001, out _);
            }
            else if (h.ClientSignature == 0x7C && TableContext.TryOpen(h, out TableContext? tc) && tc is not null)
            {
                using (tc)
                {
                    var row = new byte[tc.RowSize];
                    int rows = tc.RowCount;
                    for (uint r = 0; r < rows + 2 && budget > 0; r++)
                    {
                        if (!tc.TryReadRow(r, row)) { continue; }
                        TableRow tr = tc.GetRow(row);
                        for (int c = 0; c < tc.Columns.Count; c++) { if (budget-- > 0 && tr.TryGetCell(c, out LtpValueRef v)) { ReadValue(h, v); } }
                    }

                    foreach (TcRowIndexEntry e in tc.EnumerateRowIndex().Take(100)) { tc.TryFindRow(e.RowId, out _); }
                }
            }
            else if (h.ClientSignature == 0xB5 && Bth.TryOpen(h, h.UserRoot, out Bth? bth) && bth is not null)
            {
                BthCursor c = bth.CreateCursor();
                while (budget-- > 0 && c.MoveNext()) { _ = c.Key.Length + c.Data.Length; }
            }

            if (depth < 3 && h.Subnodes is { } subs)
            {
                foreach (SubnodeEntry e in subs.Entries.Take(200))
                {
                    if (budget <= 0) { return; }
                    if (HeapNode.TryOpen(f, e, out HeapNode? sh, out _) && sh is not null) { using (sh) { Visit(sh, depth + 1); } }
                }
            }
        }

        foreach (NodeEntry n in f.Nodes)
        {
            if (budget-- <= 0) { break; }
            if (HeapNode.TryOpen(f, n.Nid, out HeapNode? h, out _) && h is not null) { using (h) { Visit(h, 0); } }
        }

        return (contexts, values);
    }
}
