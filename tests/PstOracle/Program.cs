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

if (args is ["ltpwrite", var wpath, ..])
{
    // ltpwrite <file> [-v]: rebuild every property and table context of the file with the LTP builders, read them back from a scratch
    // file and compare value by value; then report how the original lays out its heaps.
    return LtpWriteCommands.Run(wpath, args.Length > 2 && args[2] == "-v");
}

if (args is ["ltpmem", var memRows])
{
    return LtpWriteCommands.Memory(int.Parse(memRows, System.Globalization.CultureInfo.InvariantCulture));
}

if (args is ["pad", var padPath, var megabytes])
{
    // Extends a file with zeros (sparse) so a scan has a long way to go.
    using var fs = new FileStream(padPath, FileMode.Open, FileAccess.Write);
    fs.SetLength(fs.Length + (long.Parse(megabytes, System.Globalization.CultureInfo.InvariantCulture) << 20));
    return 0;
}

if (args is ["genpst", var gpath, var gfmt, var gpreset, ..])
{
    // genpst <out> ansi|unicode|4k sample|big [messagesPerFolder]: a PST/OST written by the test builder, for the pffexport comparison.
    NdbFormat gf = gfmt switch { "ansi" => NdbFormat.Ansi, "unicode" => NdbFormat.Unicode, _ => NdbFormat.Unicode4K };
    var tree = gpreset == "big" ? RecursiveAI.OneMail.Tests.Pst.PstFixtures.Big(args.Length > 4 ? int.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : 200, args.Length > 5 ? int.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture) : 300_000) : gpreset == "medium" ? RecursiveAI.OneMail.Tests.Pst.PstFixtures.Big(30, 12_000) : RecursiveAI.OneMail.Tests.Pst.PstFixtures.Sample();
    File.WriteAllBytes(gpath, new RecursiveAI.OneMail.Tests.Pst.PstFixtureBuilder(gf).Build(tree, "Generated store", compress: gf == NdbFormat.Unicode4K).Bytes);
    return 0;
}

if (args is ["pstalloc", var apath, var passes])
{
    // pstalloc <file> <passes>: walks the whole store several times (every folder, message, recipient and attachment read) and prints the bytes
    // allocated per message and the working set after each pass; a flat working set across passes is the constant-memory claim.
    if (!RecursiveAI.OneMail.Pst.Messaging.PstStore.TryOpen(apath, out var astore, out var awhy) || astore is null) { Console.WriteLine("open failed " + string.Join("; ", awhy)); return 1; }
    using (astore)
    {
        long peak = 0;
        for (int pass = 0; pass < int.Parse(passes, System.Globalization.CultureInfo.InvariantCulture); pass++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var c = astore.ReadEverything();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            long ws = Environment.WorkingSet;
            peak = Math.Max(peak, ws);
            Console.WriteLine($"pass {pass}: messages {c.Messages} recipients {c.Recipients} attachments {c.Attachments} allocated {allocated / 1024} KiB = {allocated / Math.Max(1, c.Messages)} B/message; working set {ws / 1048576} MiB; managed {GC.GetTotalMemory(true) / 1048576} MiB");
        }

        Console.WriteLine($"peak working set {peak / 1048576} MiB; file {new FileInfo(apath).Length / 1048576} MiB");
    }

    return 0;
}

if (args is ["pstgen", var genPath, var genCount, ..])
{
    // pstgen <out.pst> <messages> [folders] [attachmentEvery]: builds a store of generated messages (subject, 1 KiB body, two recipients, an 8 KiB attachment
    // on every Nth message) spread over <folders> folders, printing the working set at tenths of the run so that growth shows.
    int total = int.Parse(genCount, System.Globalization.CultureInfo.InvariantCulture);
    int folderCount = args.Length > 3 ? int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 10;
    int attachEvery = args.Length > 4 ? int.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : 10;
    var sw = Stopwatch.StartNew();
    var proc = Process.GetCurrentProcess();
    byte[] attachment = new byte[8192];
    new Random(7).NextBytes(attachment);
    string bodyText = string.Join(" ", Enumerable.Range(0, 120).Select(i => "word" + i));
    using (var b = RecursiveAI.OneMail.Pst.Messaging.PstBuilder.Create(genPath))
    {
        var folders = Enumerable.Range(0, folderCount).Select(i => b.IpmSubtree.CreateFolder("Folder " + i)).ToArray();
        long peak = 0;
        for (int i = 0; i < total; i++)
        {
            var m = new RecursiveAI.OneMail.Mapi.MapiMessage();
            void S(ushort id, string v) => m.Properties.Add(RecursiveAI.OneMail.Mapi.MapiProperty.Create(new RecursiveAI.OneMail.Mapi.MapiTag(id, RecursiveAI.OneMail.Mapi.MapiType.UnicodeString), System.Text.Encoding.Unicode.GetBytes(v)));
            S(0x001A, "IPM.Note");
            S(0x0037, "Generated message number " + i);
            S(0x1000, bodyText + " " + i);
            S(0x0C1A, "Sender " + (i % 97));
            m.Properties.Add(RecursiveAI.OneMail.Mapi.MapiProperty.Create(new RecursiveAI.OneMail.Mapi.MapiTag(0x0039, RecursiveAI.OneMail.Mapi.MapiType.Time), BitConverter.GetBytes(133_500_000_000_000_000L + i)));
            for (int r = 0; r < 2; r++)
            {
                var rc = new RecursiveAI.OneMail.Mapi.MapiRecipient(m);
                rc.Properties.Add(RecursiveAI.OneMail.Mapi.MapiProperty.Create(new RecursiveAI.OneMail.Mapi.MapiTag(0x0C15, RecursiveAI.OneMail.Mapi.MapiType.Integer32), BitConverter.GetBytes(r + 1)));
                rc.Properties.Add(RecursiveAI.OneMail.Mapi.MapiProperty.Create(new RecursiveAI.OneMail.Mapi.MapiTag(0x3001, RecursiveAI.OneMail.Mapi.MapiType.UnicodeString), System.Text.Encoding.Unicode.GetBytes("Person " + ((i + r) % 1000))));
                rc.Properties.Add(RecursiveAI.OneMail.Mapi.MapiProperty.Create(new RecursiveAI.OneMail.Mapi.MapiTag(0x3003, RecursiveAI.OneMail.Mapi.MapiType.UnicodeString), System.Text.Encoding.Unicode.GetBytes($"p{(i + r) % 1000}@example.com")));
                m.AddRecipient(rc);
            }

            if (attachEvery > 0 && i % attachEvery == 0)
            {
                var a = new RecursiveAI.OneMail.Mapi.MapiAttachment(m);
                a.Properties.Add(RecursiveAI.OneMail.Mapi.MapiProperty.Create(new RecursiveAI.OneMail.Mapi.MapiTag(0x3705, RecursiveAI.OneMail.Mapi.MapiType.Integer32), BitConverter.GetBytes(1)));
                a.Properties.Add(RecursiveAI.OneMail.Mapi.MapiProperty.Create(new RecursiveAI.OneMail.Mapi.MapiTag(0x3701, RecursiveAI.OneMail.Mapi.MapiType.Binary), attachment));
                m.AddAttachment(a);
            }

            folders[i % folderCount].AddMessage(m);
            if (total >= 10 && i % (total / 10) == 0)
            {
                proc.Refresh();
                peak = Math.Max(peak, proc.WorkingSet64);
                Console.WriteLine($"  at {i,9} messages: working set {proc.WorkingSet64 / (1024 * 1024)} MiB, managed {GC.GetTotalMemory(false) / (1024 * 1024)} MiB, {sw.ElapsedMilliseconds} ms");
            }
        }

        Console.WriteLine("complete: " + b.Complete());
    }

    proc.Refresh();
    Console.WriteLine($"pstgen {total} messages in {folderCount} folders: {sw.ElapsedMilliseconds} ms, peak working set {proc.PeakWorkingSet64 / (1024 * 1024)} MiB, file {new FileInfo(genPath).Length / (1024 * 1024)} MiB");
    return 0;
}

if (args is ["pstcheck", var written, ..])
{
    // pstcheck <written.pst> <source>...: every message of the sources (files, or directories of files; converted to Outlook items as the
    // PST conversion does) against every message of the written store, property by property.
    if (!RecursiveAI.OneMail.Pst.Messaging.PstStore.TryOpen(written, out var wstore, out var wwhy) || wstore is null) { Console.WriteLine("open failed " + string.Join("; ", wwhy)); return 1; }
    using (wstore)
    {
        var openContainers = new List<RecursiveAI.OneMail.MailContainer>();   // kept open: the messages read their values lazily
        IEnumerable<RecursiveAI.OneMail.Mapi.MapiMessage> Sources()
        {
            foreach (string input in args.Skip(2))
            {
                IEnumerable<string> files = Directory.Exists(input) ? Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal) : [input];
                foreach (string file in files)
                {
                    RecursiveAI.OneMail.MailContainer? container = null;
                    try { container = RecursiveAI.OneMail.MailContainer.Open(file); } catch (Exception e) when (e is NotSupportedException or IOException) { continue; }
                    openContainers.Add(container);
                    {
                        foreach (RecursiveAI.OneMail.IMailItem item in container.Enumerate())
                        {
                            RecursiveAI.OneMail.Mapi.MapiMessage? m = item switch
                            {
                                RecursiveAI.OneMail.MapiMailItem mi => mi.Message,
                                RecursiveAI.OneMail.MimeMailItem mm => new RecursiveAI.OneMail.Conversion.OxcMail.MimeToMapiConverter().Convert(mm.Entity).Value,
                                _ => null,
                            };
                            if (m is not null) { yield return m; }
                        }
                    }
                }
            }
        }

        var cr = RecursiveAI.OneMail.Tests.Pst.PstStoreCompare.Compare(Sources(), wstore);
        Console.WriteLine($"pstcheck source {cr.Source} written {cr.Written} exact {cr.Exact} withRequiredAdded {cr.WithAdditions} unmatched {cr.Unmatched} propertiesAdded {cr.PropertiesAdded} notCarried {cr.PropertiesNotCarried}");
        foreach (string n in cr.Notes) { Console.WriteLine("DIFF " + n); }
        foreach (var oc in openContainers) { oc.Dispose(); }
        return cr.Unmatched == 0 && cr.Source == cr.Written ? 0 : 3;
    }
}

if (args is ["messaging" or "propexact" or "props", var mpath, ..])
{
    // The messaging layer over a whole file: "messaging <file> <pffexport .export dir>" compares with pffexport; "propexact <file>" checks every
    // property bag against the raw lists-tables-properties entries; both end by printing counts and the first differences.
    if (!RecursiveAI.OneMail.Pst.Messaging.PstStore.TryOpen(mpath, out var store, out var why) || store is null) { Console.WriteLine("open failed " + string.Join("; ", why)); return 1; }
    using (store)
    {
        if (args[0] == "props")
        {
            // props <file> <nid hex>: every property of one message, with value lengths and a short text/hex preview.
            store.TryOpenMessage(new RecursiveAI.OneMail.Pst.Ndb.Nid(Convert.ToUInt32(args[2], 16)), out var pm);
            foreach (var pr in pm!.Properties) Console.WriteLine($"{pr.Tag} {RecursiveAI.OneMail.Mapi.MapiPropertyNames.GetName(pr.Id)} len={pr.GetByteLength()} {Convert.ToHexString(pr.GetBytes().Span[..Math.Min(24, pr.GetBytes().Length)])}");
            foreach (var d in pm.Defects) Console.WriteLine("defect " + d);
            return 0;
        }

        if (args[0] == "messaging") return MessagingCompare.Run(store, args[2], args.Length > 3);
        var r = RecursiveAI.OneMail.Tests.Pst.PstChecks.PropertyExact(store, default);
        foreach (string d in r.Differences.Take(40)) Console.WriteLine("DIFF " + d);
        var counts = store.ReadEverything();
        Console.WriteLine($"propexact objects {r.Objects} properties {r.Properties} differences {r.Differences.Count}; folders {counts.Folders} messages {counts.Messages} recipients {counts.Recipients} attachments {counts.Attachments} embedded {counts.EmbeddedMessages}; defects {store.Defects.Count} dropped {store.DroppedDefects} ndbDefects {store.DatabaseDefects.Count}");
        foreach (var g in store.Defects.GroupBy(d => d.Kind)) Console.WriteLine($"defect {g.Key} {g.Count()} first: {g.First()}");
        return r.Differences.Count == 0 ? 0 : 3;
    }
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
