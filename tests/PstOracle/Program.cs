using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using RecursiveAI.OneMail.Pst.Ndb;

// Dumps what the NDB layer sees in a PST/OST as "key value" lines, so the numbers can be set beside what the libpff tools report.
//   PstOracle dump <file>      header, node and block counts, data-tree sizes, allocation, defects, scan agreement
//   PstOracle nodes <file>     one line per node: nid, type, data bid, data length, subnode bid
if (args is ["gen", var outPath, var count, ..] && args.Length <= 4)
{
    // A file (ansi, unicode or 4k; default 4k) with <count> small nodes, for the working-set runs.
    var nodes = Enumerable.Range(1, int.Parse(count, System.Globalization.CultureInfo.InvariantCulture)).Select(i => new RecursiveAI.OneMail.Tests.Pst.TestNode { Nid = ((uint)i * 32) + 4, Data = new byte[200 + (i % 50)] }).ToList();
    NdbFormat fmt = args.Length == 4 ? args[3] switch { "ansi" => NdbFormat.Ansi, "unicode" => NdbFormat.Unicode, _ => NdbFormat.Unicode4K } : NdbFormat.Unicode4K;
    File.WriteAllBytes(outPath, RecursiveAI.OneMail.Tests.Pst.NdbTestBuilder.Build(fmt, nodes).Bytes);
    return 0;
}

if (args is ["pad", var padPath, var megabytes])
{
    // Extends a file with zeros (sparse) so a scan has a long way to go.
    using var fs = new FileStream(padPath, FileMode.Open, FileAccess.Write);
    fs.SetLength(fs.Length + (long.Parse(megabytes, System.Globalization.CultureInfo.InvariantCulture) << 20));
    return 0;
}

if (args.Length < 2) { Console.Error.WriteLine("usage: PstOracle dump|nodes <file>"); return 2; }
if (!NdbFile.TryOpen(args[1], out NdbFile? f, out IReadOnlyList<NdbDefect> openDefects) || f is null)
{
    Console.WriteLine("open failed");
    foreach (NdbDefect d in openDefects) Console.WriteLine("defect " + d);
    return 1;
}

using (f)
{
    return args[0] switch { "dump" => Dump(f, args[1]), "nodes" => Nodes(f), "scan" => ScanOnly(f), "walk" => Walk(f), "orphans" => Orphans(f), "pages" => Pages(f), "ltp" => LtpCommands.Stats(f), "tree" => LtpCommands.Tree(f), "ltploop" => LtpCommands.Loop(f, int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture)), "ltpalloc" => LtpCommands.Alloc(f), "blockat" => LtpCommands.BlockAt(f, args[2..]), "compare" when args.Length > 2 => LtpCommands.Compare(f, args[2]), _ => 2 };
}

static int Walk(NdbFile f)
{
    // Enumerates both trees and reads every node's data, keeping nothing: the library's own working set.
    long nodes = 0, bytes = 0, blocks = 0;
    byte[] buf = new byte[65536];
    using (var c = f.Nodes.CreateCursor())
    {
        while (c.MoveNext())
        {
            nodes++;
            using NdbDataTree? t = f.OpenDataTree(c.Current.Data);
            for (long off = 0; t is not null && off < t.Length; off += buf.Length) bytes += t.Read(off, buf);
        }
    }

    using (var c = f.Blocks.CreateCursor()) { while (c.MoveNext()) blocks++; }
    long heap = GC.GetTotalMemory(true);
    Console.WriteLine($"walk heapAfterGC {heap} gcs {GC.CollectionCount(0)}/{GC.CollectionCount(2)} allocated {GC.GetTotalAllocatedBytes()}");
    Console.WriteLine($"walk nodes {nodes} bytes {bytes} blocks {blocks} defects {f.Defects.Count} peakWorkingSet {Process.GetCurrentProcess().PeakWorkingSet64}");
    return 0;
}

static int ScanOnly(NdbFile f)
{
    long n = 0, blocks = 0;
    foreach (NdbScanItem it in f.Scan()) { n++; if (it.Kind == NdbScanKind.Block) blocks++; }
    Console.WriteLine($"scan items {n} blocks {blocks} length {f.Length} peakWorkingSet {Process.GetCurrentProcess().PeakWorkingSet64}");
    return 0;
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
    var scanPageOffsets = new HashSet<long>();
    var pageTypes = new SortedDictionary<byte, int>();
    foreach (NdbScanItem it in f.Scan())
    {
        if (it.Kind == NdbScanKind.Page) { pages++; scanPageOffsets.Add(it.Ib); pageTypes[it.PageType] = pageTypes.GetValueOrDefault(it.PageType) + 1; }
        else { scanBlocks++; if (bbt.Contains(it.Ib)) scanBlocksInBbt++; }
    }

    Console.WriteLine($"scan pages {pages} blocks {scanBlocks} blocksInBbt {scanBlocksInBbt} bbtNotFoundByScan {blocks - scanBlocksInBbt}");
    Console.WriteLine($"scan rootsFound nbt {scanPageOffsets.Contains((long)h.Root.NodeBTree.Ib)} bbt {scanPageOffsets.Contains((long)h.Root.BlockBTree.Ib)}");
    foreach (var kv in pageTypes) Console.WriteLine($"scan pageType 0x{kv.Key:x2} {kv.Value}");

    var kinds = new SortedDictionary<NdbDefectKind, int>();
    foreach (NdbDefect d in f.Defects) kinds[d.Kind] = kinds.GetValueOrDefault(d.Kind) + 1;
    Console.WriteLine($"defects {f.Defects.Count} dropped {f.DroppedDefects}");
    foreach (var kv in kinds) Console.WriteLine($"defect {kv.Key} {kv.Value}");
    foreach (NdbDefect d in f.Defects.Take(10)) Console.WriteLine("  " + d);
    Console.WriteLine($"ms {sw.ElapsedMilliseconds} peakWorkingSet {Process.GetCurrentProcess().PeakWorkingSet64}");
    return 0;
}
