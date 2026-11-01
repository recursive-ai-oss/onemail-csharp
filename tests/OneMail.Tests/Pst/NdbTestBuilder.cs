using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Buffers.Binary;
using System.IO.Compression;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Tests.Pst;

/// <summary>A node to put in a generated test file: its data, and optionally subnodes.</summary>
public sealed class TestNode
{
    public required uint Nid { get; init; }
    /// <summary>The NBT entry's parent NID (a message's folder, a folder's parent folder); 0 for nodes that have none.</summary>
    public uint Parent { get; init; }
    public byte[] Data { get; init; } = [];
    public IReadOnlyList<TestNode> Subnodes { get; init; } = [];
    /// <summary>When set, the node's data blocks exactly as given (one block each, under an XBLOCK when there are several) and <see cref="Data"/> is ignored; for heaps, whose blocks are meaningful.</summary>
    public IReadOnlyList<byte[]>? Blocks { get; init; }
    /// <summary>When set, the node's bidData as written by the block builder (see <see cref="TestBuild.Prebuilt"/>); <see cref="Data"/> and <see cref="Blocks"/> are ignored.</summary>
    public ulong? DataBid { get; init; }
    /// <summary>When set, the node's bidSub as written by the block builder; <see cref="Subnodes"/> is ignored.</summary>
    public ulong? SubBid { get; init; }
}

/// <summary>Knobs for <see cref="NdbTestBuilder"/>.</summary>
public sealed class TestBuild
{
    /// <summary>Most entries per B-tree leaf page; a small value forces index pages.</summary>
    public int LeafCapacity { get; init; } = 1000;
    /// <summary>0 none, 1 permute, 2 cyclic.</summary>
    public byte Crypt { get; init; }
    /// <summary>4 KiB files only: deflate data blocks that get smaller.</summary>
    public bool Compress { get; init; }
    /// <summary>Unicode files only: blocks made by <see cref="NdbBlockBuilder"/>, placed as they are (the BIDs are theirs) before any block of the nodes' own data.</summary>
    public IReadOnlyList<NdbBlock>? Prebuilt { get; init; }
}

/// <summary>
/// Writes small, valid ANSI, Unicode and 4 KiB files straight from the MS-PST structures (header, AMap, density list, B-tree
/// pages, blocks, XBLOCKs, SLBLOCKs) so the reader can be tested without a real PST and then damaged byte by byte.
/// It has no messaging layer: the nodes are opaque.
/// </summary>
public static class NdbTestBuilder
{
    public sealed class Result
    {
        public required byte[] Bytes { get; init; }
        /// <summary>File offset of each block by BID value.</summary>
        public required Dictionary<ulong, long> BlockOffsets { get; init; }
        /// <summary>File offset of every B-tree page, NBT first.</summary>
        public required List<long> NbtPages { get; init; }
        public required List<long> BbtPages { get; init; }
        public required long AMapOffset { get; init; }
    }

    private sealed class Geometry
    {
        public required NdbFormat Format { get; init; }
        public required int Page { get; init; }
        public required int Trailer { get; init; }
        public required int Align { get; init; }
        public required int IdSize { get; init; }
        public required int MaxBlock { get; init; }
        public required long FirstData { get; init; }
        public required long AMap { get; init; }
        public required long Dl { get; init; }
        public required int AllocBytes { get; init; }
        public required int AllocOffset { get; init; }
        public required int Footer { get; init; }
        public bool Ansi => Format == NdbFormat.Ansi;
    }

    private static Geometry For(NdbFormat f) => f switch
    {
        NdbFormat.Ansi => new() { Format = f, Page = 512, Trailer = 12, Align = 64, IdSize = 4, MaxBlock = 8192 - 12, FirstData = 0x4800, AMap = 0x4400, Dl = 0x4200, AllocBytes = 496, AllocOffset = 4, Footer = 496 },
        NdbFormat.Unicode => new() { Format = f, Page = 512, Trailer = 16, Align = 64, IdSize = 8, MaxBlock = 8192 - 16, FirstData = 0x4800, AMap = 0x4400, Dl = 0x4200, AllocBytes = 496, AllocOffset = 0, Footer = 488 },
        _ => new() { Format = f, Page = 4096, Trailer = 24, Align = 512, IdSize = 8, MaxBlock = 65536 - 24, FirstData = 0x23000, AMap = 0x22000, Dl = 0x21000, AllocBytes = 4072, AllocOffset = 0, Footer = 4056 },
    };

    public static Result Build(NdbFormat format, IReadOnlyList<TestNode> nodes, TestBuild? options = null)
    {
        options ??= new TestBuild();
        Geometry g = For(format);
        var file = new List<byte>();
        void PadTo(long n) { if (file.Count < n) { file.AddRange(new byte[n - file.Count]); } }
        PadTo(g.FirstData);

        // MS-PST 2.2.2.7.2: an AMap page heads each range of AllocBytes * 8 * Align bytes, the first at g.AMap. Larger files keep
        // those slots free of data, so a block or page that would touch one starts after it; the pages are written at the end.
        long span = (long)g.AllocBytes * 8 * g.Align;
        var amapSlots = new List<long>();
        void Wedge(int length)
        {
            while (true)
            {
                long pos = file.Count;
                long k = (pos - g.AMap) / span;
                if (k >= 1 && pos < g.AMap + (k * span) + g.Page) { PadTo(g.AMap + (k * span) + g.Page); if (!amapSlots.Contains(g.AMap + (k * span))) { amapSlots.Add(g.AMap + (k * span)); } continue; }
                long next = g.AMap + ((k + 1) * span);
                if (pos + length <= next) { return; }
                PadTo(next + g.Page);
                amapSlots.Add(next);
            }
        }

        ulong nextBlock = 4;     // external BIDs count in steps of 4; internal ones add 2 (MS-PST 2.2.2.2)
        ulong nextPage = 0x100;
        var blocks = new SortedDictionary<ulong, (long Ib, int Cb)>();
        var offsets = new Dictionary<ulong, long>();

        if (options.Prebuilt is { } prebuilt)
        {
            foreach (NdbBlock pb in prebuilt)
            {
                Wedge(pb.TotalLength);
                long pib = file.Count;
                var image = new byte[pb.TotalLength];
                pb.WriteTo(image, (ulong)pib);
                file.AddRange(image);
                blocks[pb.Bid.Value] = (pib, pb.DataLength);
                offsets[pb.Bid.Value] = pib;
                nextBlock = Math.Max(nextBlock, ((pb.Bid.Value >> 2) + 1) << 2);
            }
        }

        ulong WriteBlock(byte[] data, bool isInternal)
        {
            ulong bid = nextBlock | (isInternal ? 2UL : 0);
            nextBlock += 4;
            byte[] stored = data;
            int uncompressed = 0;
            if (format == NdbFormat.Unicode4K && options.Compress && !isInternal && data.Length > 32)
            {
                using var ms = new MemoryStream();
                using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true)) { z.Write(data); }
                if (ms.Length < data.Length) { stored = ms.ToArray(); uncompressed = data.Length; }
            }
            else if (options.Crypt != 0 && !isInternal)
            {
                stored = (byte[])data.Clone();
                if (options.Crypt == 1) { NdbChecksums.Permute(stored, encode: true); }
                else { NdbChecksums.Cyclic(stored, (uint)bid); }
            }

            int total = ((stored.Length + g.Trailer + g.Align - 1) / g.Align) * g.Align;
            Wedge(total);
            long ib = file.Count;
            var blk = new byte[total];
            stored.CopyTo(blk, 0);
            Span<byte> tr = blk.AsSpan(total - g.Trailer);
            BinaryPrimitives.WriteUInt16LittleEndian(tr, (ushort)stored.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(tr[2..], NdbChecksums.Signature((ulong)ib, bid));
            uint crc = NdbChecksums.Crc32(stored);
            if (g.Ansi) { BinaryPrimitives.WriteUInt32LittleEndian(tr[4..], (uint)bid); BinaryPrimitives.WriteUInt32LittleEndian(tr[8..], crc); }
            else { BinaryPrimitives.WriteUInt32LittleEndian(tr[4..], crc); BinaryPrimitives.WriteUInt64LittleEndian(tr[8..], bid); }
            if (format == NdbFormat.Unicode4K)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(tr[16..], 2);
                BinaryPrimitives.WriteUInt16LittleEndian(tr[18..], (ushort)(uncompressed != 0 ? uncompressed : stored.Length));
            }
            file.AddRange(blk);
            blocks[bid] = (ib, stored.Length);
            offsets[bid] = ib;
            return bid;
        }

        void PutId(Span<byte> s, ulong v) { if (g.Ansi) { BinaryPrimitives.WriteUInt32LittleEndian(s, (uint)v); } else { BinaryPrimitives.WriteUInt64LittleEndian(s, v); } }

        // MS-PST 2.2.2.8.3.2: data larger than one block goes into an XBLOCK listing the data blocks.
        ulong WriteData(byte[] data)
        {
            if (data.Length == 0) { return 0; }  // an empty stream has no block
            if (data.Length <= g.MaxBlock) { return WriteBlock(data, false); }
            var ids = new List<ulong>();
            for (int o = 0; o < data.Length; o += g.MaxBlock) { ids.Add(WriteBlock(data[o..Math.Min(data.Length, o + g.MaxBlock)], false)); }
            var x = new byte[8 + (ids.Count * g.IdSize)];
            x[0] = 1; x[1] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(x.AsSpan(2), (ushort)ids.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(x.AsSpan(4), (uint)data.Length);
            for (int i = 0; i < ids.Count; i++) { PutId(x.AsSpan(8 + (i * g.IdSize)), ids[i]); }
            return WriteBlock(x, true);
        }

        ulong WriteNodeData(TestNode n)
        {
            if (n.Blocks is null) { return WriteData(n.Data); }
            if (n.Blocks.Count == 0) { return 0; }
            var ids = n.Blocks.Select(b => WriteBlock(b, false)).ToList();
            if (ids.Count == 1) { return ids[0]; }
            var x = new byte[8 + (ids.Count * g.IdSize)];
            x[0] = 1; x[1] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(x.AsSpan(2), (ushort)ids.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(x.AsSpan(4), (uint)n.Blocks.Sum(b => b.Length));
            for (int i = 0; i < ids.Count; i++) { PutId(x.AsSpan(8 + (i * g.IdSize)), ids[i]); }
            return WriteBlock(x, true);
        }

        // MS-PST 2.2.2.8.3.3: an SLBLOCK lists (nid, bidData, bidSub) for each subnode.
        ulong WriteSubnodes(IReadOnlyList<TestNode> subs)
        {
            if (subs.Count == 0) { return 0; }
            int head = g.Ansi ? 4 : 8, entry = g.IdSize * 3;
            var sl = new byte[head + (subs.Count * entry)];
            sl[0] = 2; sl[1] = 0;
            BinaryPrimitives.WriteUInt16LittleEndian(sl.AsSpan(2), (ushort)subs.Count);
            int i = 0;
            foreach (TestNode s in subs.OrderBy(n => n.Nid))
            {
                ulong d = WriteNodeData(s), sub = WriteSubnodes(s.Subnodes);
                Span<byte> e = sl.AsSpan(head + (i++ * entry));
                PutId(e, s.Nid); PutId(e[g.IdSize..], d); PutId(e[(2 * g.IdSize)..], sub);
            }
            return WriteBlock(sl, true);
        }

        var nbtEntries = new List<(ulong Key, byte[] Entry)>();
        foreach (TestNode n in nodes.OrderBy(n => n.Nid))
        {
            ulong d = n.DataBid ?? WriteNodeData(n), sub = n.SubBid ?? WriteSubnodes(n.Subnodes);
            int sz = g.Ansi ? 16 : 32;
            var e = new byte[sz];
            PutId(e, n.Nid); PutId(e.AsSpan(g.IdSize), d); PutId(e.AsSpan(2 * g.IdSize), sub);
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(3 * g.IdSize), n.Parent);
            nbtEntries.Add((n.Nid, e));
        }

        var bbtEntries = new List<(ulong Key, byte[] Entry)>();
        foreach (var (bid, (ib, cb)) in blocks)
        {
            var e = new byte[g.Ansi ? 12 : 24];
            PutId(e, bid); PutId(e.AsSpan(g.IdSize), (ulong)ib);
            BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(g.IdSize * 2), (ushort)cb);
            BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan((g.IdSize * 2) + 2), 2);
            bbtEntries.Add((bid, e));
        }

        // Pages: leaves, then index levels until one page remains. Each page's BID comes from the page counter.
        (ulong Bid, long Ib) WritePage(byte ptype, byte level, List<(ulong Key, byte[] Entry)> items, int cbEnt)
        {
            var page = new byte[g.Page];
            for (int i = 0; i < items.Count; i++) { items[i].Entry.CopyTo(page, i * cbEnt); }
            int f = g.Footer;
            if (format == NdbFormat.Unicode4K)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(f), (ushort)items.Count);
                BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(f + 2), (ushort)(f / cbEnt));
                page[f + 4] = (byte)cbEnt; page[f + 5] = level;
            }
            else
            {
                page[f] = (byte)items.Count; page[f + 1] = (byte)Math.Min(255, f / cbEnt); page[f + 2] = (byte)cbEnt; page[f + 3] = level;
            }

            ulong bid = nextPage; nextPage += 4;
            PadTo(((file.Count + g.Page - 1) / g.Page) * g.Page);  // pages sit on page boundaries, blocks only on block ones
            Wedge(g.Page);
            long ib = file.Count;
            FinishPage(g, page, ptype, bid, (ulong)ib);
            file.AddRange(page);
            return (bid, ib);
        }

        (ulong Bid, long Ib) BuildTree(byte ptype, List<(ulong Key, byte[] Entry)> entries, int leafEnt, int idxEnt, List<long> pages)
        {
            int cap = Math.Max(1, Math.Min(options.LeafCapacity, g.Footer / leafEnt));
            List<List<(ulong Key, byte[] Entry)>> cur = entries.Count == 0 ? [[]] : entries.Chunk(cap).Select(c => c.ToList()).ToList();
            byte lvl = 0;
            while (true)
            {
                var parents = new List<(ulong Key, byte[] Entry)>();
                (ulong Bid, long Ib) last = default;
                foreach (var c in cur)
                {
                    last = WritePage(ptype, lvl, c, lvl == 0 ? leafEnt : idxEnt);
                    pages.Add(last.Ib);
                    var pe = new byte[idxEnt];
                    ulong key = c.Count > 0 ? c[0].Key : 0;
                    PutId(pe, key); PutId(pe.AsSpan(g.IdSize), last.Bid); PutId(pe.AsSpan(2 * g.IdSize), (ulong)last.Ib);
                    parents.Add((key, pe));
                }

                if (parents.Count == 1) { return last; }
                lvl++;
                int icap = Math.Max(2, Math.Min(options.LeafCapacity, g.Footer / idxEnt));
                cur = parents.Chunk(icap).Select(c => c.ToList()).ToList();
            }
        }

        var nbtPages = new List<long>(); var bbtPages = new List<long>();
        int nbtLeaf = g.Ansi ? 16 : 32, bbtLeaf = g.Ansi ? 12 : 24, idx = g.Ansi ? 12 : 24;
        // The BBT is built last because the NBT pages' own blocks do not exist (pages are not blocks), so its entries are complete.
        var nbtRoot = BuildTree(0x81, nbtEntries, nbtLeaf, idx, nbtPages);
        var bbtRoot = BuildTree(0x80, bbtEntries, bbtLeaf, idx, bbtPages);

        long eof = file.Count;
        // Allocation: everything from each AMap on is allocated up to the end of the file; the rest of the last map is free.
        long lastAMap = g.AMap;
        long freeBytes = 0;
        foreach (long at in new[] { g.AMap }.Concat(amapSlots.Order()))
        {
            int usedBits = (int)Math.Min(g.AllocBytes * 8L, (eof - at + g.Align - 1) / g.Align);
            var amap = new byte[g.Page];
            for (int b = 0; b < usedBits; b++) { amap[g.AllocOffset + (b >> 3)] |= (byte)(0x80 >> (b & 7)); }
            freeBytes = ((long)g.AllocBytes * 8 - usedBits) * g.Align;
            lastAMap = at;
            FinishPage(g, amap, 0x84, (ulong)at, (ulong)at, allocation: true);
            amap.CopyTo(CollectionsMarshalSpan(file, at, g.Page));
        }

        // Density list page, empty. 512-byte files also get the (obsolete but customary) PMap at 0x4600.
        var dl = new byte[g.Page];
        FinishPage(g, dl, 0x86, nextPage, (ulong)g.Dl);
        dl.CopyTo(CollectionsMarshalSpan(file, g.Dl, g.Page));
        if (format != NdbFormat.Unicode4K)
        {
            var pmap = new byte[g.Page];
            FinishPage(g, pmap, 0x83, 0x4600, 0x4600, allocation: true);
            pmap.CopyTo(CollectionsMarshalSpan(file, 0x4600, g.Page));
        }

        eof = file.Count;
        byte[] bytes = file.ToArray();
        WriteHeader(g, bytes, options.Crypt, nextBlock, nextPage, (ulong)eof, (ulong)lastAMap, (ulong)freeBytes, nbtRoot, bbtRoot);
        return new Result { Bytes = bytes, BlockOffsets = offsets, NbtPages = nbtPages, BbtPages = bbtPages, AMapOffset = g.AMap };
    }

    private static Span<byte> CollectionsMarshalSpan(List<byte> list, long at, int len) =>
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list).Slice((int)at, len);

    private static void FinishPage(Geometry g, byte[] page, byte ptype, ulong bid, ulong ib, bool allocation = false)
    {
        int t = g.Page - g.Trailer;
        page[t] = ptype; page[t + 1] = ptype;
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(t + 2), allocation ? (ushort)0 : NdbChecksums.Signature(ib, bid));
        uint crc = NdbChecksums.Crc32(page.AsSpan(0, t));
        if (g.Ansi) { BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(t + 4), (uint)bid); BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(t + 8), crc); }
        else { BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(t + 4), crc); BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(t + 8), bid); }
    }

    private static void WriteHeader(Geometry g, byte[] f, byte crypt, ulong nextBlock, ulong nextPage, ulong eof, ulong amapLast, ulong free,
        (ulong Bid, long Ib) nbt, (ulong Bid, long Ib) bbt)
    {
        f[0] = 0x21; f[1] = 0x42; f[2] = 0x44; f[3] = 0x4E;
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(8), 0x4D53);
        ushort ver = g.Format switch { NdbFormat.Ansi => 14, NdbFormat.Unicode => 23, _ => 36 };
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(10), ver);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(12), 19);
        f[14] = 1; f[15] = 1;
        if (g.Ansi)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(24), (uint)nextBlock);
            BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(28), (uint)nextPage);
            BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(32), 1);
            Span<byte> r = f.AsSpan(164);
            BinaryPrimitives.WriteUInt32LittleEndian(r[4..], (uint)eof);
            BinaryPrimitives.WriteUInt32LittleEndian(r[8..], (uint)amapLast);
            BinaryPrimitives.WriteUInt32LittleEndian(r[12..], (uint)free);
            BinaryPrimitives.WriteUInt32LittleEndian(r[20..], (uint)nbt.Bid); BinaryPrimitives.WriteUInt32LittleEndian(r[24..], (uint)nbt.Ib);
            BinaryPrimitives.WriteUInt32LittleEndian(r[28..], (uint)bbt.Bid); BinaryPrimitives.WriteUInt32LittleEndian(r[32..], (uint)bbt.Ib);
            r[36] = 2;
            f[460] = 0x80; f[461] = crypt;
            BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(4), NdbChecksums.Crc32(f.AsSpan(8, 471)));
        }
        else
        {
            BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(32), nextPage);
            BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(40), 1);
            Span<byte> r = f.AsSpan(180);
            BinaryPrimitives.WriteUInt64LittleEndian(r[4..], eof);
            BinaryPrimitives.WriteUInt64LittleEndian(r[12..], amapLast);
            BinaryPrimitives.WriteUInt64LittleEndian(r[20..], free);
            BinaryPrimitives.WriteUInt64LittleEndian(r[36..], nbt.Bid); BinaryPrimitives.WriteUInt64LittleEndian(r[44..], (ulong)nbt.Ib);
            BinaryPrimitives.WriteUInt64LittleEndian(r[52..], bbt.Bid); BinaryPrimitives.WriteUInt64LittleEndian(r[60..], (ulong)bbt.Ib);
            r[68] = 2;
            f[512] = 0x80; f[513] = crypt;
            BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(516), nextBlock);
            BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(4), NdbChecksums.Crc32(f.AsSpan(8, 471)));
            BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(524), NdbChecksums.Crc32(f.AsSpan(8, 516)));
        }
    }
}
