using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using RecursiveAI.OneMail.Pst.Ndb;

// Dumps what the NDB layer sees in a PST/OST as "key value" lines, so the numbers can be set beside what the libpff tools report.
//   PstOracle dump <file>      header, node and block counts, data-tree sizes, allocation, defects, scan agreement
//   PstOracle nodes <file>     one line per node: nid, type, data bid, data length, subnode bid
if (args.Length < 2) { Console.Error.WriteLine("usage: PstOracle dump|nodes <file>"); return 2; }
if (!NdbFile.TryOpen(args[1], out NdbFile? f, out IReadOnlyList<NdbDefect> openDefects) || f is null)
{
    Console.WriteLine("open failed");
    foreach (NdbDefect d in openDefects) Console.WriteLine("defect " + d);
    return 1;
}

using (f)
{
    return args[0] switch { "dump" => Dump(f, args[1]), "nodes" => Nodes(f), "orphans" => Orphans(f), "pages" => Pages(f), _ => 2 };
}

static int Pages(NdbFile f)
{
    foreach (NdbScanItem it in f.Scan())
    {
        if (it.Kind == NdbScanKind.Page) Console.WriteLine($"0x{it.Ib:x} type=0x{it.PageType:x2} bid=0x{it.Bid:x}");
    }

    return 0;
}

static int Orphans(NdbFile f)
{
    var bbt = new HashSet<long>();
    using (var c = f.Blocks.CreateCursor()) { while (c.MoveNext()) bbt.Add((long)c.Current.Block.Ib); }
    foreach (NdbScanItem it in f.Scan())
    {
        if (it.Kind == NdbScanKind.Block && !bbt.Contains(it.Ib)) Console.WriteLine($"0x{it.Ib:x} bid=0x{it.Bid:x} dec={it.Bid} cb={it.DataSize}");
    }

    return 0;
}

static int Nodes(NdbFile f)
{
    using var c = f.Nodes.CreateCursor();
    while (c.MoveNext())
    {
        NodeEntry n = c.Current;
        long len = -1;
        if (!n.Data.IsNull)
        {
            using NdbDataTree? t = f.OpenDataTree(n.Data);
            len = t?.Length ?? -1;
        }

        string stored = "";
        if (!n.Data.IsNull && f.Blocks.TryGet(n.Data, out BlockEntry be)) stored = $" stored={be.Size}";
        Console.WriteLine($"{n.Nid} dec={n.Nid.Value} {n.Nid.Type} data={n.Data} dec={n.Data.Value}{stored} len={len} sub={n.Sub} dec={n.Sub.Value} parent={n.Parent}");
    }

    return 0;
}

static int Dump(NdbFile f, string path)
{
    var sw = Stopwatch.StartNew();
    NdbHeader h = f.Header;
    Console.WriteLine($"format {f.Format} pageSize {f.PageSize} version {h.Version} clientMagic 0x{h.ClientMagic:x} offline {h.IsOffline} encryption {h.Encryption}");
    Console.WriteLine($"length {f.Length} headerCrc partial {h.PartialCrcValid} full {h.FullCrcValid}");
    Console.WriteLine($"root fileEof {h.Root.FileEof} ampValid {h.Root.AMapValid} nbt {h.Root.NodeBTree} bbt {h.Root.BlockBTree}");

    var byType = new SortedDictionary<NidType, int>();
    long nodes = 0, dataBytes = 0, subnodes = 0, dataFail = 0;
    var inNbt = new HashSet<ulong>();
    using (var c = f.Nodes.CreateCursor())
    {
        while (c.MoveNext())
        {
            NodeEntry n = c.Current;
            nodes++;
            byType[n.Nid.Type] = byType.GetValueOrDefault(n.Nid.Type) + 1;
            if (!n.Data.IsNull)
            {
                inNbt.Add(n.Data.Normalized.Value);
                using NdbDataTree? t = f.OpenDataTree(n.Data);
                if (t is null) dataFail++; else dataBytes += t.Length;
            }

            if (!n.Sub.IsNull)
            {
                SubnodeTree? s = f.OpenSubnodes(n.Sub);
                if (s is null) dataFail++; else subnodes += s.Entries.Count;
            }
        }
    }

    Console.WriteLine($"nbt nodes {nodes} dataBytes {dataBytes} subnodeEntries {subnodes} unreadable {dataFail}");
    foreach (var kv in byType) Console.WriteLine($"nbt type {kv.Key} {kv.Value}");

    long blocks = 0, blockBytes = 0, internalBlocks = 0, compressed = 0, unreadable = 0, inflatedBytes = 0, zlibHdr = 0;
    byte[] buf = new byte[f.MaxBlockDataSize];
    var bbt = new HashSet<long>();
    using (var c = f.Blocks.CreateCursor())
    {
        while (c.MoveNext())
        {
            BlockEntry b = c.Current;
            blocks++;
            blockBytes += b.Size;
            if (b.Bid.IsInternal) internalBlocks++;
            bbt.Add((long)b.Block.Ib);
            if (!f.TryReadBlock(b, buf, out int len, out _)) unreadable++;
            else if (len != b.Size) { compressed++; inflatedBytes += len; byte[] two = new byte[2]; using var fs = File.OpenRead(path); fs.Seek((long)b.Block.Ib, SeekOrigin.Begin); fs.ReadExactly(two); if (two[0] == 0x78) zlibHdr++; }
        }
    }

    Console.WriteLine($"bbt blocks {blocks} internal {internalBlocks} dataBytes {blockBytes} compressed {compressed} (inflated {inflatedBytes}) unreadable {unreadable} zlibHeader {zlibHdr}");

    NdbAllocationSummary a = f.ValidateAllocation();
    Console.WriteLine($"amaps {a.AMapCount} invalid {a.InvalidCount} freeBytes {a.FreeBytes} headerFreeBytes {a.HeaderFreeBytes}");
    NdbDensityList? dl = f.ReadDensityList();
    Console.WriteLine(dl is null ? "densityList none" : $"densityList entries {dl.Entries.Count} flags {dl.Flags} clean {dl.Check.IsClean}");

    long pages = 0, scanBlocks = 0, scanBlocksInBbt = 0;
    var pageTypes = new SortedDictionary<byte, int>();
    foreach (NdbScanItem it in f.Scan())
    {
        if (it.Kind == NdbScanKind.Page) { pages++; pageTypes[it.PageType] = pageTypes.GetValueOrDefault(it.PageType) + 1; }
        else { scanBlocks++; if (bbt.Contains(it.Ib)) scanBlocksInBbt++; }
    }

    Console.WriteLine($"scan pages {pages} blocks {scanBlocks} blocksInBbt {scanBlocksInBbt} bbtNotFoundByScan {blocks - scanBlocksInBbt}");
    foreach (var kv in pageTypes) Console.WriteLine($"scan pageType 0x{kv.Key:x2} {kv.Value}");

    var kinds = new SortedDictionary<NdbDefectKind, int>();
    foreach (NdbDefect d in f.Defects) kinds[d.Kind] = kinds.GetValueOrDefault(d.Kind) + 1;
    Console.WriteLine($"defects {f.Defects.Count} dropped {f.DroppedDefects}");
    foreach (var kv in kinds) Console.WriteLine($"defect {kv.Key} {kv.Value}");
    foreach (NdbDefect d in f.Defects.Take(10)) Console.WriteLine("  " + d);
    Console.WriteLine($"ms {sw.ElapsedMilliseconds} peakWorkingSet {Process.GetCurrentProcess().PeakWorkingSet64}");
    return 0;
}
