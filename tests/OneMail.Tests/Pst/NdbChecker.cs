using System.Buffers.Binary;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Tests.Pst;

/// <summary>
/// An independent consistency checker for the node database of a Unicode PST. It parses the bytes itself (header, both B-trees, blocks, allocation maps,
/// density list) and recomputes from the trees what the file's bookkeeping must say: which bytes are in use, how many references each block has, how much
/// space is free, and the next BIDs and NID indexes. It shares no code with the reader or the editor except the CRC and signature functions.
/// </summary>
public static class NdbChecker
{
    private const int PageSize = 512;
    private const int Slot = 64;
    private const long FirstAMap = 0x4400;
    private const long RangeLength = 3968L * Slot;
    private const long DensityList = 0x4200;

    /// <summary>What the checker found.</summary>
    /// <param name="Errors">Things that make the file wrong: damaged pages or blocks, overlapping extents, referenced space the maps call free, a wrong free count, a block with fewer references than users.</param>
    /// <param name="Soft">Space the maps call used that nothing reaches (a leak), blocks with more references than users, and counters that lag.</param>
    /// <param name="Pages">B-tree pages walked.</param>
    /// <param name="Blocks">Blocks in the block tree.</param>
    /// <param name="Nodes">Nodes in the node tree.</param>
    /// <param name="Unique">dwUnique from the header.</param>
    public sealed record Report(List<string> Errors, List<string> Soft, int Pages, int Blocks, int Nodes, uint Unique);

    private sealed class State
    {
        public required byte[] File;
        public List<string> Errors = [];
        public List<string> Soft = [];
        public List<(long Ib, int Len, string What)> Extents = [];
        public HashSet<long> PageIbs = [];
        public List<(ulong Bid, long Ib, int Cb, int CRef)> BlockEntries = [];
        public List<(uint Nid, ulong Data, ulong Sub, uint Parent)> NodeEntries = [];
        public HashSet<ulong> PageBids = [];
        public ulong MaxPageBid;
    }

    /// <summary>Checks a file image. <paramref name="expectValidMaps"/> false skips the map comparisons (a leftover whose maps are marked invalid).</summary>
    public static Report Check(byte[] file, bool expectValidMaps = true)
    {
        var s = new State { File = file };
        if (file.Length < 564 || file[0] != (byte)'!' || file[1] != (byte)'B' || file[2] != (byte)'D' || file[3] != (byte)'N')
        {
            s.Errors.Add("not a PST header");
            return new Report(s.Errors, s.Soft, 0, 0, 0, 0);
        }

        ReadOnlySpan<byte> h = file;
        if (BinaryPrimitives.ReadUInt32LittleEndian(h[4..]) != NdbChecksums.Crc32(h.Slice(8, 471)))
        {
            s.Errors.Add("header partial CRC");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(h[524..]) != NdbChecksums.Crc32(h.Slice(8, 516)))
        {
            s.Errors.Add("header full CRC");
        }

        if (h[512] != 0x80)
        {
            s.Errors.Add("header sentinel");
        }

        ulong bidNextP = BinaryPrimitives.ReadUInt64LittleEndian(h[32..]);
        uint unique = BinaryPrimitives.ReadUInt32LittleEndian(h[40..]);
        ulong bidNextB = BinaryPrimitives.ReadUInt64LittleEndian(h[516..]);
        ReadOnlySpan<byte> r = h[180..];
        long eof = (long)BinaryPrimitives.ReadUInt64LittleEndian(r[4..]);
        long amapLast = (long)BinaryPrimitives.ReadUInt64LittleEndian(r[12..]);
        long freeBytes = (long)BinaryPrimitives.ReadUInt64LittleEndian(r[20..]);
        (ulong nbtBid, long nbtIb) = (BinaryPrimitives.ReadUInt64LittleEndian(r[36..]), (long)BinaryPrimitives.ReadUInt64LittleEndian(r[44..]));
        (ulong bbtBid, long bbtIb) = (BinaryPrimitives.ReadUInt64LittleEndian(r[52..]), (long)BinaryPrimitives.ReadUInt64LittleEndian(r[60..]));
        byte valid = r[68];
        if (file.Length < eof)
        {
            s.Errors.Add($"file is {file.Length} bytes, header says {eof}");
        }

        if (expectValidMaps && valid is not (1 or 2))
        {
            s.Errors.Add($"fAMapValid is {valid}");
        }

        int pages = 0;
        WalkPage(s, nbtBid, nbtIb, true, -1, ref pages);
        WalkPage(s, bbtBid, bbtIb, false, -1, ref pages);

        // Blocks: trailer, CRC, and the references each makes.
        var cref = new Dictionary<ulong, int>();
        var entryByBid = new Dictionary<ulong, (long Ib, int Cb, int CRef)>();
        foreach ((ulong bid, long ib, int cb, int c) in s.BlockEntries)
        {
            if (!entryByBid.TryAdd(bid & ~1UL, (ib, cb, c)))
            {
                s.Errors.Add($"block {bid:x} appears twice in the block tree");
            }
        }

        void Ref(ulong bid, string from)
        {
            if (bid == 0)
            {
                return;
            }

            ulong key = bid & ~1UL;
            if (!entryByBid.ContainsKey(key))
            {
                s.Errors.Add($"{from} names block {bid:x}, which is not in the block tree");
                return;
            }

            cref[key] = cref.GetValueOrDefault(key) + 1;
        }

        foreach ((uint nid, ulong data, ulong sub, _) in s.NodeEntries)
        {
            Ref(data, $"node {nid:x}");
            Ref(sub, $"node {nid:x}");
        }

        foreach ((ulong bid, long ib, int cb, int c) in s.BlockEntries)
        {
            int total = ((cb + 16 + Slot - 1) / Slot) * Slot;
            if (ib < 0 || ib + total > file.Length)
            {
                s.Errors.Add($"block {bid:x} lies outside the file");
                continue;
            }

            s.Extents.Add((ib, total, $"block {bid:x}"));
            ReadOnlySpan<byte> t = file.AsSpan((int)(ib + total - 16), 16);
            if (BinaryPrimitives.ReadUInt16LittleEndian(t) != cb || BinaryPrimitives.ReadUInt64LittleEndian(t[8..]) != bid
                || BinaryPrimitives.ReadUInt32LittleEndian(t[4..]) != NdbChecksums.Crc32(file.AsSpan((int)ib, cb)))
            {
                s.Errors.Add($"block {bid:x} at {ib:x}: trailer (size, BID or CRC) does not match");
                continue;
            }

            if ((bid & 2) != 0)
            {
                InternalRefs(file.AsSpan((int)ib, cb), bid, Ref, s);
            }
        }

        foreach ((ulong bid, _, _, int c) in s.BlockEntries)
        {
            int refs = cref.GetValueOrDefault(bid & ~1UL);
            if (refs == 0)
            {
                s.Soft.Add($"block {bid:x} is in the block tree and nothing names it");
            }
            else if (c < refs + 1)
            {
                s.Errors.Add($"block {bid:x} has reference count {c}, {refs} users hold it (it would be freed too soon)");
            }
            else if (c > refs + 1)
            {
                s.Soft.Add($"block {bid:x} has reference count {c}, {refs} users hold it");
            }
        }

        // Extents must not overlap.
        s.Extents.Sort(static (a, b) => a.Ib.CompareTo(b.Ib));
        for (int i = 1; i < s.Extents.Count; i++)
        {
            if (s.Extents[i].Ib < s.Extents[i - 1].Ib + s.Extents[i - 1].Len)
            {
                s.Errors.Add($"{s.Extents[i - 1].What} at {s.Extents[i - 1].Ib:x}+{s.Extents[i - 1].Len} overlaps {s.Extents[i].What} at {s.Extents[i].Ib:x}");
            }
        }

        // BIDs and NID counters.
        ulong maxBlock = 0;
        foreach ((ulong bid, _, _, _) in s.BlockEntries)
        {
            maxBlock = Math.Max(maxBlock, bid >> 2);
        }

        if (s.BlockEntries.Count > 0 && (bidNextB >> 2) <= maxBlock)
        {
            s.Errors.Add($"bidNextB {bidNextB:x} does not pass the highest block BID {maxBlock << 2:x}");
        }

        if (s.PageBids.Count > 0 && bidNextP <= s.MaxPageBid)
        {
            s.Errors.Add($"bidNextP {bidNextP:x} does not pass the highest page BID {s.MaxPageBid:x}");
        }

        for (int i = 0; i < s.NodeEntries.Count; i++)
        {
            uint nid = s.NodeEntries[i].Nid;
            int type = (int)(nid & 0x1F);
            uint index = nid >> 5;
            uint counter = BinaryPrimitives.ReadUInt32LittleEndian(h[(44 + (type * 4))..]);
            if (index >= 0x400 && counter < index)
            {
                // MS-PST 2.2.2.6: rgnid is the last index allocated, so an index above it is one the next allocation could hand out again.
                s.Soft.Add($"NID {nid:x} (type {type:x}) is above the header's counter {counter:x}");
                break;
            }
        }

        // Allocation maps and density list.
        if (expectValidMaps)
        {
            CheckMaps(s, eof, amapLast, freeBytes);
        }

        return new Report(s.Errors, s.Soft, pages, s.BlockEntries.Count, s.NodeEntries.Count, unique);
    }

    private static void InternalRefs(ReadOnlySpan<byte> d, ulong bid, Action<ulong, string> reference, State s)
    {
        if (d.Length < 8)
        {
            s.Errors.Add($"internal block {bid:x} is too short");
            return;
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(d[2..]);
        string from = $"block {bid:x}";
        if (d[0] == 1)
        {
            // MS-PST 2.2.2.8.3.2: XBLOCK / XXBLOCK, rgbid at byte 8.
            for (int i = 0; i < count && 8 + (i * 8) + 8 <= d.Length; i++)
            {
                reference(BinaryPrimitives.ReadUInt64LittleEndian(d[(8 + (i * 8))..]), from);
            }
        }
        else if (d[0] == 2 && d[1] == 0)
        {
            // MS-PST 2.2.2.8.3.3.1: SLBLOCK, SLENTRY = nid, bidData, bidSub from byte 8.
            for (int i = 0; i < count && 8 + (i * 24) + 24 <= d.Length; i++)
            {
                reference(BinaryPrimitives.ReadUInt64LittleEndian(d[(8 + (i * 24) + 8)..]), from);
                reference(BinaryPrimitives.ReadUInt64LittleEndian(d[(8 + (i * 24) + 16)..]), from);
            }
        }
        else if (d[0] == 2)
        {
            for (int i = 0; i < count && 8 + (i * 16) + 16 <= d.Length; i++)
            {
                reference(BinaryPrimitives.ReadUInt64LittleEndian(d[(8 + (i * 16) + 8)..]), from);
            }
        }
        else
        {
            s.Errors.Add($"internal block {bid:x} has block type {d[0]}");
        }
    }

    // MS-PST 2.2.2.7.7: BTPAGE. level is the level the parent expects (-1 for a root).
    private static void WalkPage(State s, ulong bid, long ib, bool isNbt, int level, ref int pages)
    {
        string name = (isNbt ? "NBT" : "BBT") + $" page {bid:x} at {ib:x}";
        if (ib < 0 || ib + PageSize > s.File.Length || ib % PageSize != 0)
        {
            s.Errors.Add(name + " is outside the file or not 512-aligned");
            return;
        }

        if (!s.PageIbs.Add(ib))
        {
            s.Errors.Add(name + " is reached twice");
            return;
        }

        pages++;
        s.Extents.Add((ib, PageSize, name));
        s.PageBids.Add(bid);
        s.MaxPageBid = Math.Max(s.MaxPageBid, bid);
        byte[] pg = s.File.AsSpan((int)ib, PageSize).ToArray();
        byte ptype = isNbt ? (byte)0x81 : (byte)0x80;
        if (pg[496] != ptype || pg[497] != ptype)
        {
            s.Errors.Add(name + ": wrong page type");
            return;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(pg.AsSpan(500)) != NdbChecksums.Crc32(pg.AsSpan(0, 496)) || BinaryPrimitives.ReadUInt64LittleEndian(pg.AsSpan(504)) != bid
            || BinaryPrimitives.ReadUInt16LittleEndian(pg.AsSpan(498)) != NdbChecksums.Signature((ulong)ib, bid))
        {
            s.Errors.Add(name + ": trailer (CRC, BID or signature) does not match");
            return;
        }

        int cEnt = pg[488], cEntMax = pg[489], cbEnt = pg[490], cLevel = pg[491];
        if (level >= 0 && cLevel != level)
        {
            s.Errors.Add(name + $": level {cLevel}, parent expects {level}");
        }

        int stride = cLevel == 0 ? (isNbt ? 32 : 24) : 24;
        int max = cLevel == 0 ? (isNbt ? 15 : 20) : 20;
        if (cbEnt != stride || cEntMax != max || cEnt > cEntMax)
        {
            s.Errors.Add(name + $": cEnt {cEnt}, cEntMax {cEntMax}, cbEnt {cbEnt} (expected max {max}, stride {stride})");
            return;
        }

        ulong prev = 0;
        for (int i = 0; i < cEnt; i++)
        {
            ReadOnlySpan<byte> e = pg.AsSpan(i * stride, stride);
            ulong key = BinaryPrimitives.ReadUInt64LittleEndian(e);
            if (i > 0 && key <= prev)
            {
                s.Errors.Add(name + ": keys are not strictly ascending");
                return;
            }

            prev = key;
            if (cLevel == 0)
            {
                if (isNbt)
                {
                    s.NodeEntries.Add(((uint)key, BinaryPrimitives.ReadUInt64LittleEndian(e[8..]), BinaryPrimitives.ReadUInt64LittleEndian(e[16..]), BinaryPrimitives.ReadUInt32LittleEndian(e[24..])));
                }
                else
                {
                    s.BlockEntries.Add((BinaryPrimitives.ReadUInt64LittleEndian(e), (long)BinaryPrimitives.ReadUInt64LittleEndian(e[8..]),
                        BinaryPrimitives.ReadUInt16LittleEndian(e[16..]), BinaryPrimitives.ReadUInt16LittleEndian(e[18..])));
                }
            }
            else
            {
                ulong childBid = BinaryPrimitives.ReadUInt64LittleEndian(e[8..]);
                long childIb = (long)BinaryPrimitives.ReadUInt64LittleEndian(e[16..]);
                int before = isNbt ? s.NodeEntries.Count : s.BlockEntries.Count;
                WalkPage(s, childBid, childIb, isNbt, cLevel - 1, ref pages);
                int after = isNbt ? s.NodeEntries.Count : s.BlockEntries.Count;
                if (after > before)
                {
                    ulong first = isNbt ? s.NodeEntries[before].Nid : s.BlockEntries[before].Bid;
                    if (first != key)
                    {
                        s.Errors.Add(name + $": index key {key:x} is not the first key {first:x} of its child");
                    }
                }
            }
        }
    }

    // MS-PST 2.2.2.7.2 AMAP, 2.2.2.7.4 DLISTPAGE.
    private static void CheckMaps(State s, long eof, long amapLast, long freeBytes)
    {
        byte[] file = s.File;
        long ranges = eof > FirstAMap ? (eof - FirstAMap + RangeLength - 1) / RangeLength : 0;
        if (ranges > 0 && FirstAMap + ((ranges - 1) * RangeLength) != amapLast)
        {
            s.Errors.Add($"ibAMapLast {amapLast:x} is not the start of the last range ({FirstAMap + ((ranges - 1) * RangeLength):x})");
        }

        var expected = new bool[ranges][];
        for (long k = 0; k < ranges; k++)
        {
            expected[k] = new bool[3968];
        }

        foreach ((long ib, int len, string what) in s.Extents)
        {
            for (long a = ib; a < ib + len; a += Slot)
            {
                if (a < FirstAMap)
                {
                    continue;
                }

                long k = (a - FirstAMap) / RangeLength;
                if (k >= ranges)
                {
                    s.Errors.Add($"{what} at {ib:x} lies beyond the last range");
                    break;
                }

                expected[k][(int)((a - FirstAMap - (k * RangeLength)) / Slot)] = true;
            }
        }

        long freeSlots = 0;
        var free = new int[ranges];
        for (long k = 0; k < ranges; k++)
        {
            long ib = FirstAMap + (k * RangeLength);
            if (ib + PageSize > file.Length)
            {
                s.Errors.Add($"allocation map {k} is outside the file");
                continue;
            }

            ReadOnlySpan<byte> p = file.AsSpan((int)ib, PageSize);
            if (p[496] != 0x84 || p[497] != 0x84 || BinaryPrimitives.ReadUInt32LittleEndian(p[500..]) != NdbChecksums.Crc32(p[..496]) || BinaryPrimitives.ReadUInt64LittleEndian(p[504..]) != (ulong)ib)
            {
                s.Errors.Add($"allocation map {k} fails its trailer checks");
                continue;
            }

            // The map pages at the start of a range (AMap, and PMap, FMap, FPMap where they fall) are reserved.
            int reserved = (1 + (k % 8 == 0 ? 1 : 0) + (k >= 128 && (k - 128) % 496 == 0 ? 1 : 0) + (k >= 8192 && (k - 8192) % 3968 == 0 ? 1 : 0)) * 8;
            int missing = 0, leaked = 0;
            for (int i = 0; i < 3968; i++)
            {
                bool used = (p[i >> 3] & (0x80 >> (i & 7))) != 0;
                if (!used)
                {
                    freeSlots++;
                    free[k]++;
                }

                if (i < reserved)
                {
                    if (!used)
                    {
                        s.Errors.Add($"allocation map {k}: reserved slot {i} is marked free");
                    }

                    continue;
                }

                if (expected[k][i] && !used)
                {
                    missing++;
                }
                else if (!expected[k][i] && used)
                {
                    leaked++;
                }
            }

            if (missing > 0)
            {
                s.Errors.Add($"allocation map {k}: {missing} slots in use by a page or block are marked free");
            }

            if (leaked > 0)
            {
                s.Soft.Add($"allocation map {k}: {leaked} slots are marked used and nothing reaches them");
            }
        }

        if (freeSlots * Slot != freeBytes)
        {
            s.Errors.Add($"cbAMapFree is {freeBytes}, the maps have {freeSlots * Slot} bytes free");
        }

        if (DensityList + PageSize <= file.Length)
        {
            ReadOnlySpan<byte> d = file.AsSpan((int)DensityList, PageSize);
            if (d[496] != 0x86 || BinaryPrimitives.ReadUInt32LittleEndian(d[500..]) != NdbChecksums.Crc32(d[..496]))
            {
                s.Errors.Add("density list page fails its checks");
            }
            else
            {
                int n = d[1];
                for (int i = 0; i < n && i < 119; i++)
                {
                    uint v = BinaryPrimitives.ReadUInt32LittleEndian(d[(8 + (i * 4))..]);
                    long page = v & 0xFFFFF;
                    int slots = (int)(v >> 20);
                    if (page >= ranges || free[page] != slots)
                    {
                        s.Errors.Add($"density list entry {i}: range {page} has {slots} free slots, the map has {(page < ranges ? free[page] : -1)}");
                    }
                }
            }
        }
    }
}
