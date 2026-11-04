using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using RecursiveAI.OneMail.Pst.Messaging;

namespace RecursiveAI.OneMail.PstOracle;

// Writes what our reader sees in a PST in the shape tools/outlook/Check-Stores.ps1 reads from Outlook's object model: folders with their paths,
// declared and enumerated item counts, and a row per item (subject, class, recipient and attachment counts, unread). Test-side only.
//   PstOracle outlookview <file> [maxItemsPerFolder = 100000]
internal static class OutlookView
{
    public static int Run(string path, int cap)
    {
        if (!PstStore.TryOpen(path, out PstStore? store, out var why) || store is null)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { file = Path.GetFileName(path), opened = false, error = string.Join("; ", why ?? []) }));
            return 4;
        }

        using (store)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
            {
                w.WriteStartObject();
                w.WriteString("file", Path.GetFileName(path));
                w.WriteBoolean("opened", true);
                w.WriteStartArray("folders");
                if (store.Root is { } root)
                {
                    foreach (PstFolder c in root.EnumerateSubfolders())
                    {
                        Walk(w, c, c.Name ?? "", cap);
                    }
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            Console.WriteLine(Encoding.UTF8.GetString(ms.ToArray()));
        }

        return 0;
    }

    // Prints how each folder's contents table is laid out: row size, rows, and the sizes of the blocks of its row matrix.
    //   PstOracle tcinfo <file> [min rows = 1]
    public static int TcInfo(string path, int minRows)
    {
        if (!PstStore.TryOpen(path, out PstStore? store, out _) || store is null)
        {
            Console.WriteLine("open failed");
            return 4;
        }

        using (store)
        {
            foreach (PstFolder f in store.EnumerateFolders())
            {
                foreach (uint type in new uint[] { 0x0E, 0x0F })
                {
                    var nid = new RecursiveAI.OneMail.Pst.Ndb.Nid((f.Nid.Value & ~0x1Fu) | type);
                    if (!RecursiveAI.OneMail.Pst.Ltp.TableContext.TryOpen(store.Database, nid, out var tc, out _) || tc is null)
                    {
                        continue;
                    }

                    using (tc)
                    {
                        int rows = tc.RowCount;
                        if (rows < minRows)
                        {
                            continue;
                        }

                        string layout = "in heap";
                        var info = tc.Heap.TryGetAllocation(tc.Heap.UserRoot, out var alloc) ? tc.Heap.GetBytes(alloc) : default;
                        uint hnidRows = info.Length >= 18 ? BitConverter.ToUInt32(info.Slice(14, 4)) : 0;
                        if ((hnidRows & 0x1F) != 0)
                        {
                            using var tree = tc.Heap.OpenSubnodeData(new RecursiveAI.OneMail.Pst.Ndb.Nid(hnidRows));
                            layout = tree is null ? "subnode unreadable" : "subnode blocks " + string.Join(",", Enumerable.Range(0, tree.LeafCount).Select(tree.GetLeafSize)) + $" (total {tree.Length})";
                        }

                        Console.WriteLine($"{(type == 0x0E ? "contents" : "assoc")} {f.Path} rows {rows} rowSize {tc.RowSize} columns {tc.Columns.Count} heap {tc.Heap.Length} hnidRows 0x{hnidRows:x} {layout}");
                    }
                }
            }
        }

        return 0;
    }

    // Prints the heap-on-node blocks of a node: per block its size, ibHnpm, bSig/bClientSig/hidUserRoot/fill levels (first block), the page map's
    // counts and the first and last allocation offsets. For comparing our heaps with ones Outlook wrote.
    //   PstOracle hninfo <file> <nid hex>
    public static int HnInfo(string path, string nidHex)
    {
        if (!PstStore.TryOpen(path, out PstStore? store, out _) || store is null)
        {
            Console.WriteLine("open failed");
            return 4;
        }

        using (store)
        {
            var nid = new RecursiveAI.OneMail.Pst.Ndb.Nid(Convert.ToUInt32(nidHex, 16));
            if (!store.Database.Nodes.TryGet(nid, out var entry))
            {
                Console.WriteLine("no such node");
                return 1;
            }

            using var tree = store.Database.OpenDataTree(entry.Data);
            if (tree is null)
            {
                Console.WriteLine("unreadable");
                return 1;
            }

            Console.WriteLine($"node {nid} data {tree.LeafCount} blocks, {tree.Length} bytes; subnode bid {entry.Sub}");
            // The first allocations of the first block: the roles are the user root (TCINFO or PC BTHHEADER) and what it points at.
            {
                var b0 = new byte[tree.GetLeafSize(0)];
                tree.ReadLeaf(0, 0, b0);
                int pm = BitConverter.ToUInt16(b0, 0);
                int n = BitConverter.ToUInt16(b0, pm);
                for (int a = 0; a < Math.Min(n, 6); a++)
                {
                    int from = BitConverter.ToUInt16(b0, pm + 4 + (2 * a)), to = BitConverter.ToUInt16(b0, pm + 4 + (2 * (a + 1)));
                    Console.WriteLine($"  alloc {a + 1} (hid 0x{((a + 1) << 5):x}) offset {from} size {to - from}: {Convert.ToHexString(b0.AsSpan(from, Math.Min(to - from, 40)))}");
                }
            }

            for (int i = 0; i < tree.LeafCount; i++)
            {
                int size = tree.GetLeafSize(i);
                var b = new byte[size];
                tree.ReadLeaf(i, 0, b);
                int ibHnpm = BitConverter.ToUInt16(b, 0);
                string head = i == 0 ? $" bSig 0x{b[2]:x2} bClient 0x{b[3]:x2} hidUserRoot 0x{BitConverter.ToUInt32(b, 4):x8} fill {Convert.ToHexString(b.AsSpan(8, 4))}" : "";
                int cAlloc = ibHnpm + 4 <= size ? BitConverter.ToUInt16(b, ibHnpm) : -1;
                int cFree = ibHnpm + 4 <= size ? BitConverter.ToUInt16(b, ibHnpm + 2) : -1;
                string allocs = cAlloc > 0 && ibHnpm + 4 + (2 * (cAlloc + 1)) <= size
                    ? $" rgibAlloc[0]={BitConverter.ToUInt16(b, ibHnpm + 4)} [last]={BitConverter.ToUInt16(b, ibHnpm + 4 + (2 * cAlloc))}"
                    : "";
                Console.WriteLine($"  block {i}: size {size} ibHnpm {ibHnpm} (tail {size - ibHnpm - 4 - (2 * (cAlloc + 1))}) cAlloc {cAlloc} cFree {cFree}{allocs}{head}");
            }
        }

        return 0;
    }

    // Lists the recipients and attachments of every message with their property tags, to see what Outlook might not accept.
    //   PstOracle msginfo <file>
    public static int MsgInfo(string path)
    {
        if (!PstStore.TryOpen(path, out PstStore? store, out _) || store is null)
        {
            return 4;
        }

        using (store)
        {
            foreach (PstFolder f in store.EnumerateFolders())
            {
                foreach (PstMessage m in f.EnumerateMessages())
                {
                    Console.WriteLine($"{f.Path} :: {m.Subject} flags 0x{m.GetInt32(0x0E07) ?? 0:x} hasAttach {m.GetInt32(0x0E1B)}");
                    foreach (var r in m.Recipients)
                    {
                        Console.WriteLine("   recipient " + string.Join(" ", r.Properties.Select(p => $"{p.Tag}")));
                    }

                    foreach (var a in m.Attachments)
                    {
                        Console.WriteLine("   attachment " + string.Join(" ", a.Properties.Select(p => $"{p.Tag}")) + $" method {a.GetInt32(0x3705)} flags {a.GetInt32(0x3714)}");
                    }
                }
            }
        }

        return 0;
    }

    private static void Walk(Utf8JsonWriter w, PstFolder f, string path, int cap)
    {
        w.WriteStartObject();
        w.WriteString("path", path);
        w.WriteString("class", f.ContainerClass ?? "");
        if (f.ContentCount is int declared)
        {
            w.WriteNumber("declared", declared);
        }

        int count = 0;
        w.WriteStartArray("items");
        foreach (PstMessage m in f.EnumerateMessages())
        {
            count++;
            if (count > cap)
            {
                continue;
            }

            w.WriteStartObject();
            w.WriteString("subject", StripPrefix(m.Subject ?? ""));
            w.WriteString("class", m.MessageClass ?? "");
            w.WriteNumber("recipients", m.Recipients.Count);
            w.WriteNumber("attachments", m.Attachments.Count);
            w.WriteString("sender", m.GetString(0x0C1A) ?? "");
            w.WriteString("sentUtc", m.Properties.Get(0x0039, Mapi.MapiType.Time)?.AsTime()?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) ?? "");
            w.WriteNumber("size", m.GetInt32(0x0E08) ?? 0);
            w.WriteBoolean("unread", ((m.GetInt32(Mapi.MapiPid.MessageFlags) ?? 0) & 1) == 0);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteNumber("count", count);
        w.WriteEndObject();
        foreach (PstFolder c in f.EnumerateSubfolders())
        {
            Walk(w, c, path + "/" + (c.Name ?? ""), cap);
        }
    }

    // PidTagSubject may start with 0x01 and a prefix-length character (MS-OXPROPS PidTagSubjectPrefix); Outlook's Subject returns the joined text.
    private static string StripPrefix(string s) => s.Length >= 2 && s[0] == '\u0001' ? s[2..] : s;
}
