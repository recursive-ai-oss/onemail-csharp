using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Pst.Messaging;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Tests.Pst;

/// <summary>Whole-store snapshots and consistency checks used by the in-place editing tests and the durability runner.</summary>
public static class StoreChecks
{
    private static string Hash(ReadOnlySpan<byte> b) => Convert.ToHexString(SHA256.HashData(b))[..16];

    private static string PropertySignature(MapiObject o)
    {
        var lines = new List<string>();
        foreach (MapiProperty p in o.Properties)
        {
            if (p.Id is 0x67F2 or 0x67F3)
            {
                continue;
            }

            string key = p.Name is { } n ? $"N:{n.PropertySet}:{(n.IsString ? "s" + n.Name : "l" + n.Lid)}:{p.Tag.Type:x4}" : $"{p.Id:x4}:{p.Tag.Type:x4}";
            var sb = new StringBuilder(key).Append('=');
            for (int i = 0; i < p.ValueCount; i++)
            {
                sb.Append(Hash(p.GetBytes(i).Span)).Append(':').Append(p.GetByteLength(i)).Append(',');
            }

            lines.Add(sb.ToString());
        }

        lines.Sort(StringComparer.Ordinal);
        return Hash(Encoding.UTF8.GetBytes(string.Join(";", lines)));
    }

    /// <summary>Every folder (with all its properties) and every item (with everything under it), by NID: two equal snapshots are the same store at property level.</summary>
    public static SortedDictionary<string, string> Snapshot(PstStore store)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (PstFolder f in store.EnumerateFolders())
        {
            map["folder/" + f.Nid.Value.ToString("x8")] = f.Path + "|" + PropertySignature(f);
            foreach (PstMessage m in f.EnumerateMessages(PstMessageSet.All))
            {
                map["msg/" + m.Nid.Value.ToString("x8")] = f.Nid.Value.ToString("x8") + "|" + PstStoreCompare.Signature(m);
            }
        }

        return map;
    }

    /// <summary>Checks that counts, hierarchy rows and contents rows agree with the folders and items they describe. Returns the problems.</summary>
    public static List<string> Consistency(PstStore store)
    {
        var problems = new List<string>();
        NdbFile file = store.Database;
        foreach (PstFolder f in store.EnumerateFolders())
        {
            if (f.IsSearchFolder)
            {
                continue;
            }

            string where = $"folder {f.Nid} '{f.Name}'";
            int content = 0, unread = 0, assoc = 0;
            using (OldTable contents = OldTable.Open(file, PstWritableStore.TableNid(f.Nid, NidType.ContentsTable), []))
            {
                int subjectColumn = Array.IndexOf(contents.Columns, ((ushort)0x0037, (ushort)0x1F));
                var rowIds = contents.RowIds().ToHashSet();
                foreach (PstMessage m in f.EnumerateMessages(PstMessageSet.Normal))
                {
                    content++;
                    if (((m.GetInt32(0x0E07) ?? 1) & 1) == 0)
                    {
                        unread++;
                    }

                    if (!rowIds.Remove(m.Nid.Value))
                    {
                        problems.Add($"{where}: item {m.Nid} has no contents row");
                    }
                    else if (subjectColumn >= 0 && contents.TryGetRow(m.Nid.Value, out byte[]?[] cells) && m.GetString(0x0037) is { } subject
                        && cells[subjectColumn] is { } cell && Encoding.Unicode.GetString(cell) != subject)
                    {
                        problems.Add($"{where}: item {m.Nid} has a different subject in its contents row");
                    }
                }

                if (rowIds.Count > 0)
                {
                    problems.Add($"{where}: {rowIds.Count} contents rows name no item");
                }
            }

            foreach (PstMessage unused in f.EnumerateMessages(PstMessageSet.Associated))
            {
                assoc++;
            }

            if (f.ContentCount != content)
            {
                problems.Add($"{where}: PidTagContentCount is {f.ContentCount}, the folder holds {content}");
            }

            if ((f.GetInt32(0x3603) ?? 0) != unread)
            {
                problems.Add($"{where}: PidTagContentUnreadCount is {f.GetInt32(0x3603)}, {unread} are unread");
            }

            if (f.GetInt32(0x3617) is { } a && a != assoc)
            {
                problems.Add($"{where}: PidTagAssociatedContentCount is {a}, the folder holds {assoc}");
            }

            var children = f.EnumerateSubfolders().ToList();
            if ((f.GetBoolean(0x360A) ?? false) != children.Count > 0)
            {
                problems.Add($"{where}: PidTagSubfolders is {f.GetBoolean(0x360A)}, it has {children.Count} subfolders");
            }

            using OldTable h = OldTable.Open(file, PstWritableStore.TableNid(f.Nid, NidType.HierarchyTable), []);
            var rows = h.RowIds().ToHashSet();
            foreach (PstFolder c in children)
            {
                if (!rows.Remove(c.Nid.Value) || !h.TryGetRow(c.Nid.Value, out byte[]?[] cells))
                {
                    problems.Add($"{where}: subfolder {c.Nid} has no hierarchy row");
                    continue;
                }

                Check(problems, where, c, h.Columns, cells, 0x3602, c.ContentCount ?? 0);
                Check(problems, where, c, h.Columns, cells, 0x3603, c.GetInt32(0x3603) ?? 0);
                int name = Array.IndexOf(h.Columns, ((ushort)0x3001, (ushort)0x1F));
                if (name >= 0 && cells[name] is { } nb && Encoding.Unicode.GetString(nb) != c.Name)
                {
                    problems.Add($"{where}: the hierarchy row for {c.Nid} has another name");
                }
            }

            if (rows.Count > 0)
            {
                problems.Add($"{where}: {rows.Count} hierarchy rows name no subfolder");
            }
        }

        return problems;
    }

    private static void Check(List<string> problems, string where, PstFolder child, (ushort Id, ushort Type)[] columns, byte[]?[] cells, ushort id, int expected)
    {
        int i = Array.IndexOf(columns, (id, (ushort)3));
        if (i >= 0 && cells[i] is { Length: >= 4 } v && BitConverter.ToInt32(v) != expected)
        {
            problems.Add($"{where}: the hierarchy row for {child.Nid} says 0x{id:x4} is {BitConverter.ToInt32(v)}, the folder says {expected}");
        }
    }
}
