using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using RecursiveAI.OneMail.Pst.Ltp;
using RecursiveAI.OneMail.Pst.Ndb;
using RecursiveAI.OneMail.Tests.Pst;

// Test-side check of the LTP builders against a real file: every property context and table context in it (top-level nodes and,
// recursively, subnodes) is read, rebuilt with the builders, placed in a scratch file and read back, and the two read forms are compared
// value by value. A second pass reads the raw heap blocks of the original and reports how the original lays them out next to how the
// builders do (allocation order, alignment, fill levels, row layout, subnode NIDs, block reference counts). Nothing here is shipped.
internal static class LtpWriteCommands
{
    private sealed class ToolSink : INdbBlockSink
    {
        public readonly List<NdbBlock> Blocks = [];

        public void Place(NdbBlock block) => Blocks.Add(block);
    }

    private sealed record PcSnap(string Label, List<(ushort Id, ushort Type, byte[]? Value, uint NodeRef)> Props);

    private sealed record TcSnap(string Label, List<TcColumn> Columns, int RowSize, int[] Ends, List<(uint RowId, uint RowIndex, byte[]?[] Cells, uint[] NodeRefs)> Rows, bool Dense);

    private sealed class Counters
    {
        public int Heaps, NotHeap, Pcs, Tcs, PcsRebuilt, TcsRebuilt, Skipped;
        public long Props, Rows, Cells, Mismatches;
        public readonly SortedDictionary<string, int> Reasons = [];
        public readonly List<string> Diffs = [];
        public int LayoutSame, LayoutDiffers;
        public readonly List<string> LayoutSamples = [];

        public void Reason(string r) => Reasons[r] = Reasons.GetValueOrDefault(r) + 1;

        public void Diff(string s)
        {
            Mismatches++;
            if (Diffs.Count < 30)
            {
                Diffs.Add(s);
            }
        }
    }

    private static string Hex(uint v) => "0x" + v.ToString("x", CultureInfo.InvariantCulture);

    private static byte[]? ValueBytes(HeapNode h, in LtpValueRef v)
    {
        if (v.Kind == LtpValueKind.Inline)
        {
            var b = new byte[v.InlineSize];
            for (int i = 0; i < b.Length; i++)
            {
                b[i] = (byte)(v.InlineValue >> (8 * i));
            }

            return b;
        }

        if (v.Kind == LtpValueKind.Empty)
        {
            return [];
        }

        return h.ReadValue(v);
    }

    public static int Run(string path, bool verbose)
    {
        if (!NdbFile.TryOpen(path, out NdbFile? f, out var why) || f is null)
        {
            Console.WriteLine("open failed " + string.Join("; ", why));
            return 1;
        }

        using (f)
        {
            var c = new Counters();
            var pcs = new List<PcSnap>();
            var tcs = new List<TcSnap>();
            var shapes = new Shapes();
            foreach (NodeEntry n in f.Nodes)
            {
                if (HeapNode.TryOpen(f, n.Nid, out HeapNode? h, out _) && h is not null)
                {
                    using (h)
                    {
                        Visit(f, h, n.Data, $"nid {n.Nid}", 0, c, pcs, tcs, shapes);
                    }
                }
                else
                {
                    c.NotHeap++;
                }
            }

            Console.WriteLine($"ltpwrite source heaps {c.Heaps} notHeap {c.NotHeap} pcs {c.Pcs} tcs {c.Tcs}");
            Rebuild(f, c, pcs, tcs);
            shapes.Print(f);
            if (verbose)
            {
                foreach (string s in c.LayoutSamples)
                {
                    Console.WriteLine("layout " + s);
                }
            }

            return c.Mismatches == 0 ? 0 : 2;
        }
    }

    private sealed class CountingSink : INdbBlockSink
    {
        public long Blocks, Bytes;

        public void Place(NdbBlock block)
        {
            Blocks++;
            Bytes += block.TotalLength;
        }
    }

    /// <summary>
    /// Builds a table of <paramref name="rows"/> rows into a sink that keeps nothing, and reports what the builder itself holds: the managed bytes
    /// allocated, the live managed heap just before <c>Build</c> (the structure being built) and after it, and the process's peak working set.
    /// </summary>
    public static int Memory(int rows)
    {
        var sink = new CountingSink();
        var blocks = new NdbBlockBuilder(new SequentialBidAllocator(), sink);
        GC.Collect();
        long baseline = GC.GetTotalMemory(true);
        long allocated0 = GC.GetAllocatedBytesForCurrentThread();
        var tcb = new TableContextBuilder(blocks, [(0x0037, LtpPropertyTypes.PtypString), (0x0E07, LtpPropertyTypes.PtypInteger32), (0x0E0C, LtpPropertyTypes.PtypTime), (0x3001, LtpPropertyTypes.PtypBinary)]);
        byte[] binary = new byte[300];
        byte[] time = new byte[8];
        byte[] number = new byte[4];
        long liveMid = 0;
        for (int i = 1; i <= rows; i++)
        {
            tcb.BeginRow((uint)i * 4);
            tcb.SetCell(tcb.FindColumn(0x0037), Encoding.Unicode.GetBytes("Subject of row " + i));
            tcb.SetCell(tcb.FindColumn(0x0E07), number);
            tcb.SetCell(tcb.FindColumn(0x0E0C), time);
            tcb.SetCell(tcb.FindColumn(0x3001), binary);
            tcb.EndRow();
            if (i == rows / 2)
            {
                liveMid = GC.GetTotalMemory(true) - baseline;
            }
        }

        long liveBefore = GC.GetTotalMemory(true) - baseline;
        LtpNodeResult r = tcb.Build();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocated0;
        long liveAfter = GC.GetTotalMemory(true) - baseline;
        Console.WriteLine($"ltpmem rows {rows} rowSize {tcb.RowSize} blocks written {sink.Blocks} ({sink.Bytes / 1048576.0:F1} MiB) of which heap {r.HeapBlockCount} blocks ({r.DataLength / 1048576.0:F1} MiB)");
        Console.WriteLine($"ltpmem allocated {allocated / 1048576.0:F1} MiB total; live managed at half way {liveMid / 1048576.0:F1} MiB, before Build {liveBefore / 1048576.0:F1} MiB, after {liveAfter / 1048576.0:F1} MiB; peak working set {System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 / 1048576} MiB");
        return 0;
    }

    // ---- reading the source ----

    private static void Visit(NdbFile f, HeapNode h, Bid dataBid, string label, int depth, Counters c, List<PcSnap> pcs, List<TcSnap> tcs, Shapes shapes)
    {
        c.Heaps++;
        if (h.ClientSignature == 0xBC && PropertyContext.TryOpen(h, out PropertyContext? pc) && pc is not null)
        {
            c.Pcs++;
            var snap = new PcSnap(label, []);
            foreach (PcProperty p in pc)
            {
                uint nodeRef = p.Type == LtpPropertyTypes.PtypObject && p.Value.Kind == LtpValueKind.Subnode ? p.Value.Hnid : 0;
                snap.Props.Add((p.Id, p.Type, nodeRef != 0 ? null : ValueBytes(h, p.Value), nodeRef));
            }

            pcs.Add(snap);
            shapes.AddPc(f, h, pc, dataBid, label);
        }
        else if (h.ClientSignature == 0x7C && TableContext.TryOpen(h, out TableContext? tc) && tc is not null)
        {
            c.Tcs++;
            var snap = SnapTc(h, tc, label);
            tcs.Add(snap);
            shapes.AddTc(f, h, tc, dataBid, label);
        }

        if (depth < 4 && h.Subnodes is { } st)
        {
            foreach (SubnodeEntry e in st.Entries.ToArray())
            {
                if (HeapNode.TryOpen(f, e, out HeapNode? sh, out _) && sh is not null)
                {
                    using (sh)
                    {
                        Visit(f, sh, e.Data, label + "/" + e.Nid, depth + 1, c, pcs, tcs, shapes);
                    }
                }
            }
        }
    }

    private static TcSnap SnapTc(HeapNode h, TableContext tc, string label)
    {
        var entries = tc.EnumerateRowIndex().OrderBy(e => e.RowIndex).ToList();
        bool dense = entries.Select((e, i) => e.RowIndex == (uint)i).All(x => x);
        var rows = new List<(uint, uint, byte[]?[], uint[])>(entries.Count);
        var buf = new byte[Math.Max(1, tc.RowSize)];
        foreach (TcRowIndexEntry e in entries)
        {
            var cells = new byte[]?[tc.Columns.Count];
            var refs = new uint[tc.Columns.Count];
            if (tc.TryReadRow(e.RowIndex, buf))
            {
                TableRow row = tc.GetRow(buf);
                for (int i = 0; i < cells.Length; i++)
                {
                    if (row.TryGetCell(i, out LtpValueRef v))
                    {
                        if (tc.Columns[i].Type == LtpPropertyTypes.PtypObject && v.Kind == LtpValueKind.Subnode)
                        {
                            refs[i] = v.Hnid;
                            cells[i] = [];
                        }
                        else
                        {
                            cells[i] = ValueBytes(h, v);
                        }
                    }
                }
            }

            rows.Add((e.RowId, e.RowIndex, cells, refs));
        }

        int[] ends = ReadEnds(h);
        return new TcSnap(label, [.. tc.Columns], tc.RowSize, ends, rows, dense);
    }

    private static int[] ReadEnds(HeapNode h)
    {
        h.TryGetAllocation(h.UserRoot, out HeapAllocation a);
        ReadOnlySpan<byte> info = h.GetBytes(a);
        return [info[2] | (info[3] << 8), info[4] | (info[5] << 8), info[6] | (info[7] << 8), info[8] | (info[9] << 8)];
    }

    // ---- rebuilding and comparing ----

    private static void Rebuild(NdbFile source, Counters c, List<PcSnap> pcs, List<TcSnap> tcs)
    {
        var sink = new ToolSink();
        var blocks = new NdbBlockBuilder(new SequentialBidAllocator(), sink);
        var nodes = new List<TestNode>();
        var pcNid = new Dictionary<PcSnap, uint>();
        var tcNid = new Dictionary<TcSnap, uint>();
        var tcExpect = new Dictionary<TcSnap, (List<int> Skip, int Added)>();
        var pcSkipped = new Dictionary<PcSnap, HashSet<ushort>>();
        uint next = 0x100;
        long working = GC.GetTotalMemory(true);

        foreach (PcSnap s in pcs)
        {
            var pcb = new PropertyContextBuilder(blocks);
            var skipped = new HashSet<ushort>();
            foreach ((ushort id, ushort type, byte[]? value, uint nodeRef) in s.Props)
            {
                if (nodeRef != 0)
                {
                    pcb.SetNodeReference(id, type, new Nid(nodeRef));
                }
                else if (value is null)
                {
                    c.Reason("PC value unreadable or over the limit");
                    skipped.Add(id);
                }
                else if (LtpPropertyTypes.FixedSize(type) > 0 && value.Length != LtpPropertyTypes.FixedSize(type))
                {
                    c.Reason($"PC fixed type 0x{type:x4} stored with {value.Length} bytes");
                    skipped.Add(id);
                }
                else
                {
                    pcb.Set(id, type, value);
                }
            }

            LtpNodeResult r = pcb.Build();
            uint nid = (next += 32) | 4;
            nodes.Add(new TestNode { Nid = nid, DataBid = r.Data.Value, SubBid = r.WriteSubnodes(blocks).Value });
            pcNid[s] = nid;
            pcSkipped[s] = skipped;
            c.PcsRebuilt++;
        }

        foreach (TcSnap s in tcs)
        {
            try
            {
                var tcb = new TableContextBuilder(blocks, s.Columns.Select(x => (x.Id, x.Type)));
                var skip = new List<int>();
                int added = tcb.Columns.Count - s.Columns.Count;
                int[] map = s.Columns.Select(x => tcb.FindColumn(x.Id, x.Type)).ToArray();
                bool srcHasVer = s.Columns.Any(x => x.Id == 0x67F3);
                foreach ((uint rowId, _, byte[]?[] cells, uint[] refs) in s.Rows)
                {
                    tcb.BeginRow(rowId);
                    for (int i = 0; i < cells.Length; i++)
                    {
                        if (s.Columns[i].Id == 0x67F2 || cells[i] is null)
                        {
                            continue;
                        }

                        if (refs[i] != 0)
                        {
                            tcb.SetCellNodeReference(map[i], new Nid(refs[i]));
                        }
                        else
                        {
                            tcb.SetCell(map[i], cells[i]);
                        }
                    }

                    tcb.EndRow();
                }

                LtpNodeResult r = tcb.Build();
                uint nid = (next += 32) | 4;
                nodes.Add(new TestNode { Nid = nid, DataBid = r.Data.Value, SubBid = r.WriteSubnodes(blocks).Value });
                tcNid[s] = nid;
                tcExpect[s] = (skip, added);
                c.TcsRebuilt++;
                CompareLayout(c, s, tcb);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                c.Reason("TC cannot be rebuilt: " + ex.Message.Split('\n')[0]);
                c.Skipped++;
            }
        }

        Console.WriteLine($"ltpwrite rebuilt pcs {c.PcsRebuilt} tcs {c.TcsRebuilt} blocks {sink.Blocks.Count} (managed heap after building {(GC.GetTotalMemory(false) - working) / 1024} KiB over start)");
        var built = NdbTestBuilder.Build(NdbFormat.Unicode, nodes, new TestBuild { Prebuilt = sink.Blocks });
        if (!NdbFile.TryOpen(built.Bytes, out NdbFile? nf, out var why) || nf is null)
        {
            Console.WriteLine("scratch file failed " + string.Join("; ", why));
            c.Mismatches++;
            return;
        }

        using (nf)
        {
            foreach (PcSnap s in pcs)
            {
                ComparePc(nf, s, pcNid[s], pcSkipped[s], c);
            }

            foreach (TcSnap s in tcs)
            {
                if (tcNid.TryGetValue(s, out uint nid))
                {
                    CompareTc(nf, s, nid, tcExpect[s].Added, c);
                }
            }

            Console.WriteLine($"ltpwrite scratch file ndb defects {nf.Defects.Count}");
            c.Mismatches += nf.Defects.Count;
        }

        Console.WriteLine($"ltpwrite compared properties {c.Props} rows {c.Rows} cells {c.Cells} mismatches {c.Mismatches} skipped-tcs {c.Skipped}");
        foreach ((string k, int n) in c.Reasons)
        {
            Console.WriteLine($"ltpwrite explained {n} x {k}");
        }

        foreach (string d in c.Diffs)
        {
            Console.WriteLine("DIFF " + d);
        }

        Console.WriteLine($"ltpwrite tc layout identical to ours {c.LayoutSame}, different {c.LayoutDiffers}");
        foreach (string s in c.LayoutSamples.Take(6))
        {
            Console.WriteLine("ltpwrite layout-diff " + s);
        }
    }

    private static void CompareLayout(Counters c, TcSnap s, TableContextBuilder tcb)
    {
        bool same = tcb.RowSize == s.RowSize && tcb.Columns.Count == s.Columns.Count;
        if (same)
        {
            foreach (TcColumn col in s.Columns)
            {
                TcColumn mine = tcb.Columns[tcb.FindColumn(col.Id, col.Type)];
                if (mine.Offset != col.Offset || mine.Size != col.Size || mine.BitIndex != col.BitIndex || mine.Type != col.Type)
                {
                    same = false;
                }
            }
        }

        if (same)
        {
            c.LayoutSame++;
            return;
        }

        c.LayoutDiffers++;
        if (c.LayoutSamples.Count < 12)
        {
            string src = string.Join(" ", s.Columns.Select(x => $"{x.Id:x4}:{x.Type:x}@{x.Offset}+{x.Size}b{x.BitIndex}"));
            string ours = string.Join(" ", tcb.Columns.Select(x => $"{x.Id:x4}:{x.Type:x}@{x.Offset}+{x.Size}b{x.BitIndex}"));
            c.LayoutSamples.Add($"{s.Label} src row {s.RowSize} ends [{string.Join(",", s.Ends)}]: {src}\n        ours row {tcb.RowSize}: {ours}");
        }
    }

    private static void ComparePc(NdbFile nf, PcSnap s, uint nid, HashSet<ushort> skipped, Counters c)
    {
        if (!PropertyContext.TryOpen(nf, new Nid(nid), out PropertyContext? pc, out var why) || pc is null)
        {
            c.Diff($"{s.Label}: rebuilt PC does not open: {string.Join("; ", why)}");
            return;
        }

        using (pc)
        {
            var got = pc.ToList();
            var want = s.Props.Where(p => !skipped.Contains(p.Id)).ToList();
            if (got.Count != want.Count)
            {
                c.Diff($"{s.Label}: {got.Count} properties, expected {want.Count}");
                return;
            }

            for (int i = 0; i < got.Count; i++)
            {
                c.Props++;
                (ushort id, ushort type, byte[]? value, uint nodeRef) = want[i];
                if (got[i].Id != id || got[i].Type != type)
                {
                    c.Diff($"{s.Label}: property {i} is {got[i].Id:x4}/{got[i].Type:x4}, expected {id:x4}/{type:x4}");
                    continue;
                }

                if (nodeRef != 0)
                {
                    if (got[i].Value.Hnid != nodeRef)
                    {
                        c.Diff($"{s.Label}: {id:x4} node reference {Hex(got[i].Value.Hnid)}, expected {Hex(nodeRef)}");
                    }

                    continue;
                }

                byte[]? now = ValueBytes(pc.Heap, got[i].Value);
                if (now is null || !now.AsSpan().SequenceEqual(value))
                {
                    c.Diff($"{s.Label}: {id:x4} value differs ({now?.Length} vs {value?.Length} bytes)");
                }
            }

            if (pc.Defects.Count > 0)
            {
                c.Diff($"{s.Label}: rebuilt PC reports {pc.Defects[0]}");
            }
        }
    }

    private static void CompareTc(NdbFile nf, TcSnap s, uint nid, int addedColumns, Counters c)
    {
        if (!TableContext.TryOpen(nf, new Nid(nid), out TableContext? tc, out var why) || tc is null)
        {
            c.Diff($"{s.Label}: rebuilt TC does not open: {string.Join("; ", why)}");
            return;
        }

        using (tc)
        {
            if (tc.Columns.Count != s.Columns.Count + addedColumns)
            {
                c.Diff($"{s.Label}: {tc.Columns.Count} columns, expected {s.Columns.Count}+{addedColumns}");
                return;
            }

            if (tc.RowCount != s.Rows.Count)
            {
                c.Diff($"{s.Label}: {tc.RowCount} rows, expected {s.Rows.Count}");
                return;
            }

            var buf = new byte[tc.RowSize];
            for (int r = 0; r < s.Rows.Count; r++)
            {
                (uint rowId, uint rowIndex, byte[]?[] cells, uint[] refs) = s.Rows[r];
                c.Rows++;
                if (!tc.TryFindRow(rowId, out uint idx) || (s.Dense && idx != rowIndex) || !tc.TryReadRow(idx, buf))
                {
                    c.Diff($"{s.Label}: row {Hex(rowId)} not found or misplaced");
                    continue;
                }

                TableRow row = tc.GetRow(buf);
                for (int i = 0; i < cells.Length; i++)
                {
                    c.Cells++;
                    int col = tc.Columns.ToList().FindIndex(x => x.Tag == s.Columns[i].Tag);
                    bool present = row.TryGetCell(col, out LtpValueRef v);
                    if (s.Columns[i].Id == 0x67F2)
                    {
                        if (!present || ValueBytes(tc.Heap, v) is not { } idb || BitConverter.ToUInt32(idb) != rowId)
                        {
                            c.Diff($"{s.Label}: row {Hex(rowId)} ID cell wrong");
                        }

                        continue;
                    }

                    if (present != (cells[i] is not null))
                    {
                        c.Diff($"{s.Label}: row {Hex(rowId)} column {s.Columns[i].Id:x4} presence {present}, expected {cells[i] is not null}");
                        continue;
                    }

                    if (!present)
                    {
                        continue;
                    }

                    if (refs[i] != 0)
                    {
                        if (v.Hnid != refs[i])
                        {
                            c.Diff($"{s.Label}: row {Hex(rowId)} column {s.Columns[i].Id:x4} node reference differs");
                        }

                        continue;
                    }

                    byte[]? now = ValueBytes(tc.Heap, v);
                    if (now is null || !now.AsSpan().SequenceEqual(cells[i]))
                    {
                        c.Diff($"{s.Label}: row {Hex(rowId)} column {s.Columns[i].Id:x4} value differs ({now?.Length} vs {cells[i]?.Length} bytes)");
                    }
                }
            }

            if (tc.Defects.Count > 0)
            {
                c.Diff($"{s.Label}: rebuilt TC reports {tc.Defects[0]}");
            }
        }
    }

    // ---- how the original lays its heaps out ----

    private sealed class Shapes
    {
        private readonly SortedDictionary<string, long> _n = [];
        private readonly List<string> _samples = [];
        private int _pcSamples, _tcSamples;

        private void Count(string k, long by = 1) => _n[k] = _n.GetValueOrDefault(k) + by;

        public void AddPc(NdbFile f, HeapNode h, PropertyContext pc, Bid data, string label)
        {
            Count("pc");
            Count($"pc.bthHeaderHid={Hex(pc.Index.Header.Value)}");
            Count($"pc.bthLevels={pc.Index.IndexLevels}");
            int firstValue = int.MaxValue;
            var subNids = new List<uint>();
            foreach (PcProperty p in pc)
            {
                if (p.Value.Kind == LtpValueKind.Heap)
                {
                    firstValue = Math.Min(firstValue, (int)p.Value.Hnid);
                }
                else if (p.Value.Kind == LtpValueKind.Subnode && p.Type != LtpPropertyTypes.PtypObject)
                {
                    subNids.Add(p.Value.Hnid);
                }
            }

            if (firstValue != int.MaxValue && !pc.Index.Root.IsNull)
            {
                Count(firstValue > (int)pc.Index.Root.Value ? "pc.recordsAllocatedBeforeValues" : "pc.valuesAllocatedBeforeRecords");
            }

            foreach (uint n in subNids)
            {
                Count($"pc.valueSubnodeNidType={n & 0x1F:x}");
                if (_samples.Count < 400 && n < 0x400)
                {
                    Count($"pc.valueSubnodeNid={Hex(n)}");
                }
            }

            Raw(f, h, data, "PC", label, ref _pcSamples);
        }

        public void AddTc(NdbFile f, HeapNode h, TableContext tc, Bid data, string label)
        {
            Count("tc");
            h.TryGetAllocation(h.UserRoot, out HeapAllocation a);
            ReadOnlySpan<byte> info = h.GetBytes(a);
            uint hidRowIndex = BitConverter.ToUInt32(info[10..]);
            uint hnidRows = BitConverter.ToUInt32(info[14..]);
            Count($"tc.tcinfoHid={Hex(h.UserRoot.Value)}");
            Count($"tc.rowIndexHeaderHid={Hex(hidRowIndex)}");
            Count(hnidRows == 0 ? "tc.matrix=none" : new Hnid(hnidRows).IsHid ? "tc.matrix=heap" : "tc.matrix=subnode");
            if (hnidRows != 0 && !new Hnid(hnidRows).IsHid)
            {
                Count($"tc.matrixSubnodeNidType={hnidRows & 0x1F:x}");
                if (_n.Count < 4000)
                {
                    Count($"tc.matrixSubnodeNid={Hex(hnidRows)}");
                }

                using NdbDataTree? t = h.OpenSubnodeData(new Nid(hnidRows));
                if (t is not null && t.LeafCount > 1 && _samples.Count < 40)
                {
                    int per = Math.Max(1, t.GetLeafSize(0) / Math.Max(1, tc.RowSize));
                    _samples.Add($"matrix leaves {t.LeafCount}, row {tc.RowSize}, first leaf {t.GetLeafSize(0)} (8176/row = {8176 / Math.Max(1, tc.RowSize)} rows, {8176 / Math.Max(1, tc.RowSize) * tc.RowSize} bytes), last {t.GetLeafSize(t.LeafCount - 1)}, rows/leaf {per}");
                }
            }

            // Bit and offset conventions.
            bool ok = true;
            int expectBit = 2;
            foreach (TcColumn col in tc.Columns)
            {
                if (col.Id == 0x67F2 ? col.BitIndex != 0 || col.Offset != 0 : col.Id == 0x67F3 ? col.BitIndex != 1 || col.Offset != 4 : col.BitIndex != expectBit++)
                {
                    ok = false;
                }
            }

            Count(ok ? "tc.bitsSequentialInTagOrderFrom2" : "tc.bitsOther");
            Count(tc.Columns.OrderBy(x => x.Offset).Select((x, i) => x.BitIndex == i).All(x => x) ? "tc.bitsFollowCellOrder" : "tc.bitsDoNotFollowCellOrder");
            Count(tc.Columns.Any(x => x.Id == 0x67F3) ? "tc.hasRowVer" : "tc.noRowVer");
            Raw(f, h, data, "TC", label, ref _tcSamples);
        }

        private void Raw(NdbFile f, HeapNode h, Bid data, string kind, string label, ref int samples)
        {
            // The layout facts below are for 512-byte-page files; the 4 KiB generation is outside what is written.
            using NdbDataTree? t = f.Format == NdbFormat.Unicode ? f.OpenDataTree(data) : null;
            if (t is null)
            {
                return;
            }

            Count($"heap.blocks={Math.Min(t.LeafCount, 10)}");
            var buf = new byte[8192];
            var levels = new List<int>();
            var sample = new StringBuilder();
            for (int b = 0; b < t.LeafCount; b++)
            {
                int cb = t.GetLeafSize(b);
                if (t.ReadLeaf(b, 0, buf.AsSpan(0, cb)) != cb)
                {
                    return;
                }

                int hdr = b == 0 ? 12 : b >= 8 && (b - 8) % 128 == 0 ? 66 : 2;
                int mapAt = buf[0] | (buf[1] << 8);
                if (mapAt + 4 > cb)
                {
                    return;
                }

                int n = buf[mapAt] | (buf[mapAt + 1] << 8);
                int cFree = buf[mapAt + 2] | (buf[mapAt + 3] << 8);
                var starts = new int[n + 1];
                for (int i = 0; i <= n && mapAt + 4 + (2 * i) + 2 <= cb; i++)
                {
                    starts[i] = buf[mapAt + 4 + (2 * i)] | (buf[mapAt + 5 + (2 * i)] << 8);
                }

                Count(cFree == 0 ? "heap.cFree=0" : "heap.cFree>0");
                Count(starts[0] == hdr ? "heap.firstAllocRightAfterHeader" : "heap.firstAllocGap");
                Count(mapAt == ((starts[n] + 1) & ~1) ? "heap.pageMapAtAlign2(end)" : mapAt == starts[n] ? "heap.pageMapAtEnd" : "heap.pageMapElsewhere");
                int expected = mapAt + 4 + (2 * (n + 1));
                Count(cb == expected ? "heap.cb=mapEnd" : cb > expected ? "heap.cb>mapEnd" : "heap.cb<mapEnd");
                for (int i = 0; i < n; i++)
                {
                    int len = starts[i + 1] - starts[i];
                    Count($"heap.allocStart%8={starts[i] % 8}");
                    Count($"heap.allocLen%8={len % 8}");
                    Count($"heap.allocLen%4={len % 4}");
                    Count($"heap.allocLen%2={len % 2}");
                    if (len > 3580)
                    {
                        Count("heap.allocOver3580");
                    }
                }

                int free = 8176 - cb;
                levels.Add(Level(8176 - expected));
                if (b < t.LeafCount - 1 && b < 6)
                {
                    Count($"heap.nonLastBlockFreeBytes<{(free < 16 ? 16 : free < 64 ? 64 : free < 256 ? 256 : free < 1024 ? 1024 : 99999)}");
                }

                if (b == 0 && t.LeafCount == 1)
                {
                    int actual = buf[8] & 0xF;
                    Count($"heap.singleBlockFillLevel actual={actual} byFormula(8176-mapEnd)={Level(8176 - expected)} byFormula(8176-cb)={Level(8176 - cb)}");
                }

                if (sample.Length < 700)
                {
                    sample.Append(CultureInfo.InvariantCulture, $" b{b}[cb{cb} n{n}]:");
                    for (int i = 0; i < Math.Min(n, 14); i++)
                    {
                        sample.Append(CultureInfo.InvariantCulture, $" {starts[i + 1] - starts[i]}");
                    }
                }
            }

            if (t.LeafCount > 1)
            {
                // Which nibble order the HNHDR uses for blocks 0..7, judged against the formula over the first block's data.
                int actual0 = ReadNibble(f, t, 0, 0), actual1 = ReadNibble(f, t, 0, 1);
                Count($"heap.multi hdrNibble0={actual0} hdrNibble1={actual1} formula0={levels[0]} formula1={levels[1]}");
            }

            if (samples++ < 4)
            {
                _samples.Add($"{kind} {label} blocks {t.LeafCount}:{sample}");
            }

        }

        private static int ReadNibble(NdbFile f, NdbDataTree t, int block, int k)
        {
            var one = new byte[1];
            t.ReadLeaf(block, 8 + (k >> 1), one);
            return (one[0] >> (4 * (k & 1))) & 0xF;
        }

        private static int Level(int free) => free switch
        {
            >= 3584 => 0, >= 2560 => 1, >= 2048 => 2, >= 1792 => 3, >= 1536 => 4, >= 1280 => 5, >= 1024 => 6, >= 768 => 7,
            >= 512 => 8, >= 256 => 9, >= 128 => 10, >= 64 => 11, >= 32 => 12, >= 16 => 13, >= 8 => 14, _ => 15,
        };

        public void Print(NdbFile f)
        {
            // Block reference counts and BID low bits.
            var cref = new SortedDictionary<string, int>();
            foreach (BlockEntry b in f.Blocks)
            {
                string k = $"cRef={b.RefCount} {(b.Bid.IsInternal ? "internal" : "data")} bidLow2={b.Bid.Value & 3}";
                cref[k] = cref.GetValueOrDefault(k) + 1;
            }

            foreach ((string k, int n) in cref)
            {
                Console.WriteLine($"shape blocks {k}: {n}");
            }

            foreach ((string k, long n) in _n)
            {
                Console.WriteLine($"shape {k}: {n}");
            }

            foreach (string s in _samples)
            {
                Console.WriteLine("shape sample " + s);
            }
        }
    }
}
