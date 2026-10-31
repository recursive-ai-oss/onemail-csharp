using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RecursiveAI.OneMail.Pst.Ltp;
using RecursiveAI.OneMail.Pst.Ndb;

// Test-side commands for the LTP layer: read every property context and table context in a file, and walk the folder tree the
// way a messaging layer would so the result can be set beside what the libpff tools export. Nothing here is shipped.
internal static class LtpCommands
{
    // ---- ltp: read everything ----

    public static int Stats(NdbFile f)
    {
        var sw = Stopwatch.StartNew();
        long pcs = 0, tcs = 0, bths = 0, otherHeaps = 0, nonHeap = 0, props = 0, rows = 0, cells = 0, valueBytes = 0, subnodeEntries = 0, columns = 0;
        long inline = 0, heapValues = 0, subValues = 0, empty = 0, unreadable = 0;
        var kinds = new SortedDictionary<LtpDefectKind, int>();
        var notHeap = new SortedDictionary<string, int>();
        var byType = new SortedDictionary<string, int>();
        byte[] buf = new byte[65536];

        long Drain(HeapNode h, LtpValueRef v)
        {
            long len = h.GetValueLength(v);
            if (len < 0) { unreadable++; return 0; }
            switch (v.Kind) { case LtpValueKind.Inline: inline++; break; case LtpValueKind.Heap: heapValues++; break; case LtpValueKind.Subnode: subValues++; break; default: empty++; break; }
            long got = 0;
            while (got < len) { int n = h.ReadValue(v, got, buf); if (n <= 0) break; got += n; }
            if (got != len) unreadable++;
            return got;
        }

        int maxHeapBlocks = 0, multiBlockHeaps = 0;
        void Visit(HeapNode h, string label, int depth)
        {
            maxHeapBlocks = Math.Max(maxHeapBlocks, h.BlockCount);
            if (h.BlockCount > 1) multiBlockHeaps++;
            switch (h.ClientSignature)
            {
                case 0xBC:
                    if (PropertyContext.TryOpen(h, out PropertyContext? pc) && pc is not null)
                    {
                        pcs++; byType[label + " PC"] = byType.GetValueOrDefault(label + " PC") + 1;
                        foreach (PcProperty p in pc) { props++; valueBytes += Drain(h, p.Value); }
                    }
                    break;
                case 0x7C:
                    if (TableContext.TryOpen(h, out TableContext? tc) && tc is not null)
                    {
                        using (tc)
                        {
                            tcs++; byType[label + " TC"] = byType.GetValueOrDefault(label + " TC") + 1; columns += tc.Columns.Count;
                            var row = new byte[tc.RowSize];
                            int count = tc.RowCount;
                            for (uint r = 0; r < count; r++)
                            {
                                if (!tc.TryReadRow(r, row)) continue;
                                rows++;
                                TableRow tr = tc.GetRow(row);
                                for (int c = 0; c < tc.Columns.Count; c++) { if (tr.TryGetCell(c, out LtpValueRef v)) { cells++; valueBytes += Drain(h, v); } }
                            }
                        }
                    }
                    break;
                case 0xB5:
                    if (Bth.TryOpen(h, h.UserRoot, out Bth? bth) && bth is not null) { bths++; var c = bth.CreateCursor(); while (c.MoveNext()) { } }
                    break;
                default:
                    otherHeaps++;
                    break;
            }

            SubnodeTree? subs = h.Subnodes;
            if (subs is null) return;
            subnodeEntries += subs.Entries.Count;
            foreach (SubnodeEntry e in subs.Entries)
            {
                if (HeapNode.TryOpen(f, e, out HeapNode? sh, out _) && sh is not null)
                {
                    using (sh) { Visit(sh, "sub " + e.Nid.Type, depth + 1); Collect(sh); }
                }
                else nonHeap++;
            }
        }

        Nid current = default; int shown = 0;
        void Note(LtpDefect d) { kinds[d.Kind] = kinds.GetValueOrDefault(d.Kind) + 1; if (shown++ < 40) Console.WriteLine($"  ltp defect node {current} {current.Type}: {d}"); }
        void Collect(HeapNode h) { foreach (LtpDefect d in h.Defects) Note(d); }

        using (var c = f.Nodes.CreateCursor())
        {
            while (c.MoveNext())
            {
                NodeEntry n = c.Current;
                if (!HeapNode.TryOpen(f, n.Nid, out HeapNode? h, out IReadOnlyList<LtpDefect> d) || h is null)
                {
                    // A node that does not open as a heap is not LTP (a search queue, an internal list): counted by type and reason, not as a defect.
                    nonHeap++;
                    string why = d.Count > 0 ? d[0].Kind.ToString() : "?";
                    notHeap[$"{n.Nid.Type} {why}"] = notHeap.GetValueOrDefault($"{n.Nid.Type} {why}") + 1;
                    continue;
                }

                current = n.Nid; using (h) { Visit(h, n.Nid.Type.ToString(), 0); Collect(h); }
            }
        }

        Console.WriteLine($"ltp pcs {pcs} tcs {tcs} bths {bths} otherHeaps {otherHeaps} notHeaps {nonHeap}");
        Console.WriteLine($"ltp properties {props} columns {columns} rows {rows} cells {cells} subnodeEntries {subnodeEntries}");
        Console.WriteLine($"ltp values inline {inline} heap {heapValues} subnode {subValues} empty {empty} unreadable {unreadable} bytes {valueBytes}");
        Console.WriteLine($"ltp heaps multiBlock {multiBlockHeaps} maxBlocks {maxHeapBlocks}");
        foreach (var kv in byType) Console.WriteLine($"ltp nodes {kv.Key} {kv.Value}");
        foreach (var kv in notHeap) Console.WriteLine($"ltp notHeap {kv.Key} {kv.Value}");
        Console.WriteLine($"ltp defects {kinds.Values.Sum()} ndbDefects {f.Defects.Count}");
        foreach (var kv in kinds) Console.WriteLine($"ltp defect {kv.Key} {kv.Value}");
        foreach (NdbDefect d in f.Defects.Take(5)) Console.WriteLine("  ndb " + d);
        Console.WriteLine($"ltp ms {sw.ElapsedMilliseconds} gcAllocated {GC.GetTotalAllocatedBytes()} peakWorkingSet {Process.GetCurrentProcess().PeakWorkingSet64}");
        return 0;
    }

    // ---- ltploop / ltpalloc: memory ----

    // Reads every PC and TC in the file `passes` times and reports the managed heap after a full collection and the peak working set
    // as it goes: both stay flat when the working set is constant.
    public static int Loop(NdbFile f, int passes)
    {
        var nodes = new List<Nid>();
        using (var c = f.Nodes.CreateCursor()) { while (c.MoveNext()) nodes.Add(c.Current.Nid); }
        byte[] buf = new byte[65536];
        long total = 0;
        for (int pass = 1; pass <= passes; pass++)
        {
            foreach (Nid n in nodes)
            {
                if (!HeapNode.TryOpen(f, n, out HeapNode? h, out _) || h is null) continue;
                using (h)
                {
                    if (h.ClientSignature == 0xBC && PropertyContext.TryOpen(h, out PropertyContext? pc) && pc is not null)
                    {
                        foreach (PcProperty p in pc) { long len = h.GetValueLength(p.Value); for (long o = 0; o < len; o += buf.Length) total += h.ReadValue(p.Value, o, buf); }
                    }
                    else if (h.ClientSignature == 0x7C && TableContext.TryOpen(h, out TableContext? tc) && tc is not null)
                    {
                        using (tc)
                        {
                            var row = new byte[tc.RowSize];
                            for (uint r = 0; r < tc.RowCount; r++)
                            {
                                if (!tc.TryReadRow(r, row)) continue;
                                TableRow tr = tc.GetRow(row);
                                for (int c2 = 0; c2 < tc.Columns.Count; c2++) { if (tr.TryGetCell(c2, out LtpValueRef v)) { long len = h.GetValueLength(v); for (long o = 0; o < len; o += buf.Length) total += h.ReadValue(v, o, buf); } }
                            }
                        }
                    }
                }
            }

            if (pass == 1 || pass % 25 == 0 || pass == passes)
                Console.WriteLine($"pass {pass} heapAfterGC {GC.GetTotalMemory(true)} peakWorkingSet {Process.GetCurrentProcess().PeakWorkingSet64} bytesRead {total} allocatedTotal {GC.GetTotalAllocatedBytes()}");
        }

        return 0;
    }

    // Allocation per operation on real data, measured on the current thread over many repetitions.
    public static int Alloc(NdbFile f)
    {
        Nid pcNode = default, tcNode = default;
        int bestRows = 0;
        using (var c = f.Nodes.CreateCursor())
        {
            while (c.MoveNext())
            {
                Nid n = c.Current.Nid;
                if (n.Type == NidType.NormalMessage && pcNode.Value == 0 && PropertyContext.TryOpen(f, n, out PropertyContext? p, out _) && p is not null) { using (p) { if (p.Count() > 20) pcNode = n; } }
                if (n.Type == NidType.HierarchyTable || n.Type == NidType.ContentsTable || n.Type == NidType.AssocContentsTable)
                {
                    if (TableContext.TryOpen(f, n, out TableContext? t, out _) && t is not null) { using (t) { if (t.RowCount > bestRows) { bestRows = t.RowCount; tcNode = n; } } }
                }
            }
        }

        const int reps = 2000;
        long A() => GC.GetAllocatedBytesForCurrentThread();
        PropertyContext.TryOpen(f, pcNode, out PropertyContext? pc0, out _); int props = pc0!.Count(); pc0.Dispose();
        long a0 = A();
        for (int i = 0; i < reps; i++) { PropertyContext.TryOpen(f, pcNode, out PropertyContext? pc, out _); pc!.Dispose(); }
        long openPc = (A() - a0) / reps;
        PropertyContext.TryOpen(f, pcNode, out PropertyContext? pcm, out _);
        a0 = A();
        long sink = 0;
        for (int i = 0; i < reps; i++) { foreach (PcProperty p in pcm!) sink += p.Id; }
        long enumPc = (A() - a0) / reps;
        a0 = A();
        for (int i = 0; i < reps; i++) { pcm!.TryGet(0x0037, out PcProperty p); sink += p.Id; }
        long getPc = (A() - a0) / reps;
        var big = new byte[65536];
        a0 = A();
        for (int i = 0; i < reps; i++) { pcm!.TryGet(0x0037, out PcProperty p); sink += pcm.ReadValue(p, 0, big); }
        long readPc = (A() - a0) / reps;
        pcm!.Dispose();

        TableContext.TryOpen(f, tcNode, out TableContext? t0, out _); int rows = t0!.RowCount; int cols = t0.Columns.Count; t0.Dispose();
        a0 = A();
        for (int i = 0; i < reps; i++) { TableContext.TryOpen(f, tcNode, out TableContext? tc, out _); tc!.Dispose(); }
        long openTc = (A() - a0) / reps;
        TableContext.TryOpen(f, tcNode, out TableContext? tcm, out _);
        var row = new byte[tcm!.RowSize];
        a0 = A();
        for (int i = 0; i < reps; i++) { for (uint r = 0; r < rows; r++) { tcm.TryReadRow(r, row); } }
        long rowRead = (A() - a0) / ((long)reps * rows);
        a0 = A();
        for (int i = 0; i < reps; i++)
        {
            for (uint r = 0; r < rows; r++)
            {
                tcm.TryReadRow(r, row);
                TableRow tr = tcm.GetRow(row);
                for (int c = 0; c < cols; c++) { if (tr.TryGetCell(c, out LtpValueRef v)) sink += tcm.ReadValue(v, 0, big); }
            }
        }

        long rowCells = (A() - a0) / ((long)reps * rows);
        a0 = A();
        for (int i = 0; i < reps; i++) { sink += tcm.TryFindRow(tcm.GetRow(row).RowId, out uint idx) ? idx : 0; }
        long find = (A() - a0) / reps;
        tcm.Dispose();
        Console.WriteLine($"alloc pcNode {pcNode} props {props}: open {openPc} B, enumerate-all {enumPc} B, TryGet {getPc} B, TryGet+ReadValue into buffer {readPc} B");
        Console.WriteLine($"alloc tcNode {tcNode} rows {rows} cols {cols}: open {openTc} B, TryReadRow {rowRead} B/row, TryReadRow + every cell read into buffer {rowCells} B/row, TryFindRow {find} B (sink {sink})");
        return 0;
    }

    // ---- blockat: what is at a file offset ----

    public static int BlockAt(NdbFile f, string[] offsets)
    {
        foreach (string o in offsets)
        {
            long ib = long.Parse(o, CultureInfo.InvariantCulture);
            BlockEntry? hit = null;
            using (var c = f.Blocks.CreateCursor()) { while (c.MoveNext()) { if ((long)c.Current.Block.Ib == ib) hit = c.Current; } }
            if (hit is null) { Console.WriteLine($"{ib} (0x{ib:x}): no block in the block B-tree starts here"); continue; }
            BlockEntry b = hit.Value;
            var owners = new List<string>();
            using (var c = f.Nodes.CreateCursor())
            {
                while (c.MoveNext())
                {
                    NodeEntry n = c.Current;
                    using NdbDataTree? t = n.Data.IsNull ? null : f.OpenDataTree(n.Data);
                    if (t is null) continue;
                    for (int i = 0; i < t.LeafCount; i++) { if (t.GetLeaf(i).Bid == b.Bid) owners.Add($"{n.Nid}({n.Nid.Type}) leaf {i}/{t.LeafCount}"); }
                }
            }

            Console.WriteLine($"{ib} (0x{ib:x}): block {b.Bid} cb {b.Size} refs {b.RefCount} internal {b.Bid.IsInternal} owners [{string.Join("; ", owners)}]");
        }

        return 0;
    }

    // ---- tree: folders and items as a messaging layer would list them ----

    public sealed record Item(string Class, string Subject, string Sender, long Size, uint Flags, string Submit, string Delivery, string Creation, int Importance, string Topic, List<(string Name, string Email, string AddrType, string Type)> Recipients, int Attachments, List<long> AttachmentSizes);

    public sealed class Folder
    {
        public string Name = "";
        public Nid Nid;
        public List<Folder> Children = [];
        public List<Item> Items = [];
        public int ItemRows;
    }

    private static string? Str(PropertyContext pc, ushort id)
    {
        if (!pc.TryGet(id, out PcProperty p)) return null;
        byte[]? b = pc.ReadValue(p);
        if (b is null) return null;
        string text = p.Type == LtpPropertyTypes.PtypString ? Encoding.Unicode.GetString(b) : Encoding.Latin1.GetString(b);
        // MS-OXPROPS PidTagSubject: a leading U+0001 and one more character mark a prefix ("RE: " is 0x01 0x05); libpff drops both.
        return id == 0x0037 && text.Length >= 2 && text[0] == '' ? text[2..] : text;
    }

    private static long? Int(PropertyContext pc, ushort id)
    {
        if (!pc.TryGet(id, out PcProperty p)) return null;
        byte[]? b = pc.ReadValue(p);
        if (b is null) return null;
        return p.Type == LtpPropertyTypes.PtypInteger32 && LtpValues.TryGetInt32(b, out int i) ? (uint)i : p.Type == LtpPropertyTypes.PtypInteger64 && LtpValues.TryGetInt64(b, out long l) ? l : null;
    }

    private static string Time(PropertyContext pc, ushort id)
    {
        if (!pc.TryGet(id, out PcProperty p)) return "";
        byte[]? b = pc.ReadValue(p);
        if (b is null || !LtpValues.TryGetFileTime(b, out long ft)) return "";
        // libpff prints "Mar 23, 2015 17:29:27.000000000 UTC": ticks of 100 ns padded to nanoseconds.
        DateTime t = DateTime.FromFileTimeUtc(ft);
        return t.ToString("MMM d, yyyy HH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "00 UTC";
    }

    private static string CellStr(TableContext tc, TableRow row, int col)
    {
        if (col < 0 || !row.TryGetCell(col, out LtpValueRef v)) return "";
        byte[]? b = tc.ReadValue(v);
        if (b is null) return "";
        return tc.Columns[col].Type == LtpPropertyTypes.PtypString ? Encoding.Unicode.GetString(b) : Encoding.Latin1.GetString(b);
    }

    private static long CellInt(TableContext tc, TableRow row, int col)
    {
        if (col < 0 || !row.TryGetCell(col, out LtpValueRef v)) return -1;
        byte[]? b = tc.ReadValue(v);
        return b is not null && LtpValues.TryGetInt32(b, out int i) ? i : -1;
    }

    // Rows of a table: (row id, the table to read cells from) using a delegate so callers pick columns.
    private static void ForEachRow(NdbFile f, Nid tableNid, Action<TableContext, TableRow> each)
    {
        if (!TableContext.TryOpen(f, tableNid, out TableContext? tc, out _) || tc is null) return;
        using (tc)
        {
            var buf = new byte[tc.RowSize];
            for (uint r = 0; r < tc.RowCount; r++) { if (tc.TryReadRow(r, buf)) each(tc, tc.GetRow(buf)); }
        }
    }

    public static Folder? BuildTree(NdbFile f)
    {
        Folder? Build(Nid folder, int depth)
        {
            if (depth > 40 || !PropertyContext.TryOpen(f, folder, out PropertyContext? pc, out _) || pc is null) return null;
            var node = new Folder { Nid = folder };
            using (pc) { node.Name = Str(pc, 0x3001) ?? ""; }
            // MS-PST 2.4.4: a folder's hierarchy, contents and associated-contents tables are nodes with its index and their own types.
            var hier = new Nid((folder.Value & ~0x1Fu) | 0x0D);
            var contents = new Nid((folder.Value & ~0x1Fu) | 0x0E);
            var children = new List<uint>();
            ForEachRow(f, hier, (tc, row) => children.Add(row.RowId));
            foreach (uint c in children) { Folder? ch = Build(new Nid(c), depth + 1); if (ch is not null) node.Children.Add(ch); }
            var msgs = new List<uint>();
            ForEachRow(f, contents, (tc, row) => msgs.Add(row.RowId));
            node.ItemRows = msgs.Count;
            foreach (uint m in msgs) { Item? it = ReadItem(f, new Nid(m)); if (it is not null) node.Items.Add(it); }
            return node;
        }

        return Build(new Nid(0x122), 0);
    }

    private static Item? ReadItem(NdbFile f, Nid message)
    {
        if (!PropertyContext.TryOpen(f, message, out PropertyContext? pc, out _) || pc is null) return null;
        using (pc)
        {
            var recipients = new List<(string, string, string, string)>();
            int atts = 0;
            var sizes = new List<long>();
            SubnodeTree? subs = pc.Heap.Subnodes;
            if (subs is not null)
            {
                foreach (SubnodeEntry e in subs.Entries)
                {
                    // 0x692: the recipient table; 0x671: the attachment table (MS-PST 2.4.5.3, 2.4.6.1): the NID types say which, not the index.
                    if (e.Nid.Type == NidType.RecipientTable && TableContext.TryOpen(f, e, out TableContext? rt, out _) && rt is not null)
                    {
                        using (rt)
                        {
                            int cn = rt.FindColumn(0x3001), ce = rt.FindColumn(0x3003), ca = rt.FindColumn(0x3002), ct = rt.FindColumn(0x0C15);
                            var buf = new byte[rt.RowSize];
                            for (uint r = 0; r < rt.RowCount; r++)
                            {
                                if (!rt.TryReadRow(r, buf)) continue;
                                TableRow row = rt.GetRow(buf);
                                recipients.Add((CellStr(rt, row, cn), CellStr(rt, row, ce), CellStr(rt, row, ca), CellInt(rt, row, ct) switch { 1 => "To", 2 => "CC", 3 => "BCC", 0 => "Originator", var x => x.ToString(CultureInfo.InvariantCulture) }));
                            }
                        }
                    }
                    else if (e.Nid.Type == NidType.AttachmentTable && TableContext.TryOpen(f, e, out TableContext? at, out _) && at is not null)
                    {
                        using (at) { atts += at.RowCount; }
                    }
                    else if (e.Nid.Type == NidType.Attachment && PropertyContext.TryOpen(f, e, out PropertyContext? ap, out _) && ap is not null)
                    {
                        // The size libpff exports is the length of the data property; an embedded message has no such property (-1).
                        using (ap) { sizes.Add(ap.TryGet(0x3701, out PcProperty data) && data.Type == LtpPropertyTypes.PtypBinary ? ap.GetValueLength(data) : -1); }
                    }
                }
            }

            sizes.Sort();
            return new Item(Str(pc, 0x001A) ?? "", Str(pc, 0x0037) ?? "", Str(pc, 0x0C1A) ?? "", Int(pc, 0x0E08) ?? -1, (uint)(Int(pc, 0x0E07) ?? 0), Time(pc, 0x0039), Time(pc, 0x0E06), Time(pc, 0x3007), (int)(Int(pc, 0x0017) ?? -1), Str(pc, 0x0070) ?? "", recipients, atts, sizes);
        }
    }

    public static int Tree(NdbFile f)
    {
        Folder? root = BuildTree(f);
        if (root is null) { Console.WriteLine("tree none"); return 1; }
        void Print(Folder fo, string path)
        {
            string p = path + "/" + fo.Name;
            Console.WriteLine($"folder {p} items {fo.Items.Count}/{fo.ItemRows}");
            foreach (Item it in fo.Items)
            {
                Console.WriteLine($"  item class={it.Class} subject={it.Subject} size={it.Size} flags=0x{it.Flags:x8} submit={it.Submit} recipients={it.Recipients.Count} attachments={it.Attachments} sizes={string.Join(",", it.AttachmentSizes)}");
            }

            foreach (Folder c in fo.Children) Print(c, p);
        }

        Print(root, "");
        return 0;
    }

    // ---- compare: the same walk against a pffexport directory ----

    private static string Field(string text, string label)
    {
        Match m = Regex.Match(text, "^" + Regex.Escape(label) + @":[ \t]*(.*?)\r?$", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value : "";
    }

    public static int Compare(NdbFile f, string exportDir)
    {
        Folder? root = BuildTree(f);
        if (root is null) { Console.WriteLine("compare: no tree"); return 1; }
        int differences = 0, folders = 0, messages = 0, recipients = 0, attachments = 0;

        void Diff(string what) { differences++; Console.WriteLine("DIFF " + Regex.Replace(what, @"[\x00-\x1f]", m => $"\\x{(int)m.Value[0]:x2}")); }

        // pffexport writes <root>/<folder>/Message00001/... ; names are the display names with unsafe characters replaced.
        static string Safe(string s) => Regex.Replace(s, @"[^\w \.\-\{\}\(\)@,&!#$%'+=\[\]]", "_");

        void Walk(Folder fo, string dir, string logical)
        {
            folders++;
            if (!Directory.Exists(dir)) { Diff($"folder missing in export: {logical}"); return; }
            var exported = Directory.GetDirectories(dir, "Message*").OrderBy(d => d, StringComparer.Ordinal).ToList();
            if (exported.Count != fo.Items.Count) Diff($"{logical}: items ours {fo.Items.Count} (rows {fo.ItemRows}) libpff {exported.Count}");

            // Items are matched by content, not by order: libpff numbers them in its own enumeration order.
            var theirs = new List<string>();
            foreach (string m in exported)
            {
                string h = File.Exists(Path.Combine(m, "OutlookHeaders.txt")) ? File.ReadAllText(Path.Combine(m, "OutlookHeaders.txt")) : "";
                string recipientText = File.Exists(Path.Combine(m, "Recipients.txt")) ? File.ReadAllText(Path.Combine(m, "Recipients.txt")) : "";
                // Recipients.txt is one block per recipient, each starting with its "Display name:" line.
                var rlist = Regex.Split(recipientText, @"(?m)^(?=Display name:)").Where(b => b.StartsWith("Display name:", StringComparison.Ordinal))
                    .Select(b => $"{Field(b, "Display name")}/{Field(b, "Email address")}/{Field(b, "Address type")}/{Field(b, "Recipient type")}").Order(StringComparer.Ordinal).ToList();
                string rc = string.Join(";", rlist);
                uint flags = Regex.Match(Field(h, "Flags"), "0x([0-9a-fA-F]+)") is { Success: true } fm ? Convert.ToUInt32(fm.Groups[1].Value, 16) : 0;
                // An attachment is a file in Attachments/ (its length is the data size) or, for an embedded message, a directory.
                string ad = Path.Combine(m, "Attachments");
                var asizes = new List<long>();
                if (Directory.Exists(ad))
                {
                    asizes.AddRange(Directory.GetFiles(ad).Select(x => new FileInfo(x).Length));
                    asizes.AddRange(Directory.GetDirectories(ad).Select(_ => -1L));
                }

                asizes.Sort();
                theirs.Add($"{Field(h, "Subject")}|{Field(h, "Size")}|{flags:x8}|{Field(h, "Client submit time")}|{Field(h, "Delivery time")}|{Field(h, "Creation time")}|{Field(h, "Sender name")}|{Field(h, "Conversation topic")}|r[{rc}]|a{asizes.Count}:{string.Join(",", asizes)}");
            }

            var ours = fo.Items.Select(i => $"{i.Subject}|{i.Size}|{i.Flags:x8}|{i.Submit}|{i.Delivery}|{i.Creation}|{i.Sender}|{i.Topic}|r[{string.Join(";", i.Recipients.Select(r => $"{r.Name}/{r.Email}/{r.AddrType}/{r.Type}").Order(StringComparer.Ordinal))}]|a{i.Attachments}:{string.Join(",", i.AttachmentSizes)}").ToList();
            messages += ours.Count;
            recipients += fo.Items.Sum(i => i.Recipients.Count);
            attachments += fo.Items.Sum(i => i.Attachments);
            foreach (string o in ours.Except(theirs).Take(20)) Diff($"{logical}: only ours: {o}");
            foreach (string t in theirs.Except(ours).Take(20)) Diff($"{logical}: only libpff: {t}");

            var seen = new HashSet<string>();
            foreach (Folder c in fo.Children)
            {
                string n = Safe(c.Name);
                if (!seen.Add(n)) { Diff($"{logical}: duplicate folder name {n} (matched by name; not compared)"); continue; }
                Walk(c, Path.Combine(dir, n), logical + "/" + c.Name);
            }

            foreach (string sub in Directory.GetDirectories(dir).Where(d => !Path.GetFileName(d).StartsWith("Message", StringComparison.Ordinal)))
            {
                if (!fo.Children.Any(c => Safe(c.Name) == Path.GetFileName(sub))) Diff($"{logical}: folder only in libpff: {Path.GetFileName(sub)}");
            }
        }

        // The export has no directory for the unnamed root folder; its children are the export's top-level directories.
        foreach (Folder c in root.Children) Walk(c, Path.Combine(exportDir, Safe(c.Name)), c.Name);
        Console.WriteLine($"compare folders {folders} messages {messages} recipients {recipients} attachments {attachments} differences {differences}");
        return differences == 0 ? 0 : 3;
    }
}
