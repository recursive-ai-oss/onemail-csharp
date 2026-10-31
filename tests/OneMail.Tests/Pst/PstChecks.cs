using System;
using System.Collections.Generic;
using System.Threading;
using System.Buffers.Binary;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Pst.Ltp;
using RecursiveAI.OneMail.Pst.Messaging;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Tests.Pst;

/// <summary>
/// Checks that the property bags the messaging layer builds are exactly the entries the lists-tables-properties layer reads: same
/// tags, same order, same raw bytes, same named-property names. Used by the tests and by the corpus oracle tool.
/// </summary>
public static class PstChecks
{
    public sealed class Result
    {
        public int Objects { get; set; }
        public long Properties { get; set; }
        public List<string> Differences { get; } = [];
    }

    public static Result PropertyExact(PstStore store, CancellationToken ct)
    {
        var r = new Result();
        NdbFile f = store.Database;
        if (f.Nodes.TryGet(new Nid(0x21), out NodeEntry se))
        {
            ComparePc(store, se.Data, se.Sub, store.Properties, "store", r);
        }

        foreach (PstFolder folder in store.EnumerateFolders(ct))
        {
            if (f.Nodes.TryGet(folder.Nid, out NodeEntry fe))
            {
                ComparePc(store, fe.Data, fe.Sub, folder.Properties, "folder " + folder.Nid, r);
            }

            foreach (PstMessage m in folder.EnumerateMessages(PstMessageSet.All, ct))
            {
                if (f.Nodes.TryGet(m.Nid, out NodeEntry me))
                {
                    Message(store, m, me.Data, me.Sub, "message " + m.Nid, r);
                }
            }
        }

        return r;
    }

    private static void Message(PstStore store, MapiMessage m, Bid data, Bid sub, string where, Result r)
    {
        ComparePc(store, data, sub, m.Properties, where, r);
        NdbFile f = store.Database;
        if (!HeapNode.TryOpen(f, data, sub, out HeapNode? heap, out _) || heap is null)
        {
            return;
        }

        using (heap)
        {
            if (heap.Subnodes is not { } subs)
            {
                if (m.Recipients.Count + m.Attachments.Count > 0) { r.Differences.Add(where + ": objects without subnodes"); }
                return;
            }

            if (subs.TryGet(new Nid(0x692), out SubnodeEntry rt) && HeapNode.TryOpen(f, rt, out HeapNode? th, out _) && th is not null)
            {
                using (th)
                {
                    if (TableContext.TryOpen(th, out TableContext? tc) && tc is not null)
                    {
                        using (tc)
                        {
                            if (tc.RowCount != m.Recipients.Count) { r.Differences.Add($"{where}: {tc.RowCount} recipient rows, {m.Recipients.Count} recipients"); }
                            byte[] row = new byte[tc.RowSize];
                            for (uint i = 0; i < Math.Min(tc.RowCount, m.Recipients.Count); i++)
                            {
                                tc.TryReadRow(i, row);
                                TableRow tr = tc.GetRow(row);
                                var expected = new List<(ushort Id, ushort Type, byte[]? Bytes)>();
                                for (int c = 0; c < tc.Columns.Count; c++)
                                {
                                    TcColumn col = tc.Columns[c];
                                    if (col.Id is 0x67F2 or 0x67F3 || !tr.TryGetCell(c, out LtpValueRef v)) { continue; }
                                    expected.Add((col.Id, col.Type, Bytes(th, v)));
                                }

                                Compare(store, m.Recipients[(int)i].Properties, expected, $"{where} recipient {i}", r);
                            }
                        }
                    }
                }
            }

            for (int i = 0; i < m.Attachments.Count; i++)
            {
                MapiAttachment a = m.Attachments[i];
                string aw = $"{where} attachment {i}";
                if (a is not PstAttachment pa || !subs.TryGet(pa.Nid, out SubnodeEntry ae))
                {
                    r.Differences.Add(aw + ": no subnode");
                    continue;
                }

                ComparePc(store, ae.Data, ae.Sub, a.Properties, aw, r);
                if (a.EmbeddedMessage is { } em && HeapNode.TryOpen(f, ae, out HeapNode? ah, out _) && ah is not null)
                {
                    using (ah)
                    {
                        if (PropertyContext.TryOpen(ah, out PropertyContext? apc) && apc is not null && apc.TryGet(0x3701, out PcProperty op)
                            && ah.Subnodes is { } asubs && asubs.TryGet(new Nid(ObjectNid(ah, op)), out SubnodeEntry ee))
                        {
                            Message(store, em, ee.Data, ee.Sub, aw + " embedded", r);
                        }
                        else
                        {
                            r.Differences.Add(aw + ": embedded message without object subnode");
                        }
                    }
                }
            }
        }
    }

    // MS-PST 2.3.3.5: the object property is an allocation of (subnode NID, size).
    private static uint ObjectNid(HeapNode heap, PcProperty p) =>
        p.Value.Kind == LtpValueKind.Heap && heap.ReadValue(p.Value) is { Length: >= 4 } b ? BinaryPrimitives.ReadUInt32LittleEndian(b) : p.Value.Hnid;

    private static byte[]? Bytes(HeapNode heap, LtpValueRef v)
    {
        switch (v.Kind)
        {
            case LtpValueKind.Empty: return [];
            case LtpValueKind.Inline:
                var b = new byte[v.InlineSize];
                for (int i = 0; i < b.Length; i++) { b[i] = (byte)(v.InlineValue >> (8 * i)); }
                return b;
            default: return heap.ReadValue(v);
        }
    }

    private static void ComparePc(PstStore store, Bid data, Bid sub, MapiPropertyBag bag, string where, Result r)
    {
        if (!HeapNode.TryOpen(store.Database, data, sub, out HeapNode? heap, out _) || heap is null)
        {
            r.Differences.Add(where + ": heap does not open");
            return;
        }

        using (heap)
        {
            if (!PropertyContext.TryOpen(heap, out PropertyContext? pc) || pc is null)
            {
                r.Differences.Add(where + ": not a property context");
                return;
            }

            var expected = new List<(ushort Id, ushort Type, byte[]? Bytes)>();
            foreach (PcProperty p in pc)
            {
                expected.Add((p.Id, p.Type, Bytes(heap, p.Value)));
            }

            Compare(store, bag, expected, where, r);
        }
    }

    private static void Compare(PstStore store, MapiPropertyBag bag, List<(ushort Id, ushort Type, byte[]? Bytes)> expected, string where, Result r)
    {
        r.Objects++;
        if (bag.Count != expected.Count)
        {
            r.Differences.Add($"{where}: {bag.Count} properties, LTP has {expected.Count}");
            return;
        }

        for (int i = 0; i < expected.Count; i++)
        {
            (ushort id, ushort type, byte[]? raw) = expected[i];
            MapiProperty p = bag[i];
            r.Properties++;
            if (p.Id != id || (ushort)p.Type != type) { r.Differences.Add($"{where}: property {i} is {p.Tag}, LTP has {id:X4}{type:X4}"); continue; }
            if (raw is null) { continue; }
            if (LtpPropertyTypes.IsMultiValue(type))
            {
                int n = LtpValues.TryGetMultiValueCount(type, raw, out int count) ? count : 0;
                if (p.ValueCount != n) { r.Differences.Add($"{where}: {p.Tag} has {p.ValueCount} elements, LTP {n}"); continue; }
                for (int k = 0; k < n; k++)
                {
                    LtpValues.TryGetMultiValueElement(type, raw, k, out int off, out int len);
                    if (!p.GetBytes(k).Span.SequenceEqual(raw.AsSpan(off, len))) { r.Differences.Add($"{where}: {p.Tag} element {k} differs"); }
                }
            }
            else if (!p.GetBytes().Span.SequenceEqual(raw))
            {
                r.Differences.Add($"{where}: {p.Tag} value differs");
            }

            if (id >= 0x8000)
            {
                bool mapped = store.TryGetNamedProperty(id, out MapiNamedProperty nm);
                if (mapped != p.Name.HasValue || (mapped && nm != p.Name)) { r.Differences.Add($"{where}: {p.Tag} name differs"); }
            }
        }
    }
}
