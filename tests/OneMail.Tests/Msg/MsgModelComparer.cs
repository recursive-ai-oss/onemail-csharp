using RecursiveAI.OneMail.Mapi;

namespace RecursiveAI.OneMail.Tests.Msg;

/// <summary>Property-exact comparison of two MAPI models; free of the test framework so the fuzz target and smoke apps share it.</summary>
internal static class MsgModelComparer
{
    private static string Key(MapiProperty p) => (p.Name is { } n ? "N" + n + "/" + p.Tag.ToHex()[4..] : p.Tag.ToHex());

    /// <summary>
    /// Every difference in property (by name for named properties, by tag otherwise), value bytes, recipients, attachments, embedded messages and
    /// raw object storages between two messages; empty when property-exact. Ignores the order of properties and the local IDs of named properties.
    /// </summary>
    public static List<string> Differences(MapiObject a, MapiObject b, string path = "message", Func<string, bool>? ignore = null)
    {
        var diffs = new List<string>();
        Dictionary<string, MapiProperty> da = Index(a.Properties);
        Dictionary<string, MapiProperty> db = Index(b.Properties);
        foreach (string k in da.Keys.Union(db.Keys).Order(StringComparer.Ordinal))
        {
            if (ignore?.Invoke(k) == true)
            {
                continue;
            }

            // The object property of an attachment with an embedded message or storage stands for that storage; its bytes are a reference in other formats.
            if (k == "3701000D" && a is MapiAttachment { EmbeddedMessage: not null } or MapiAttachment { ObjectStorage: not null })
            {
                continue;
            }

            if (!da.TryGetValue(k, out MapiProperty? pa))
            {
                diffs.Add($"{path}: {k} only in the second");
            }
            else if (!db.TryGetValue(k, out MapiProperty? pb))
            {
                diffs.Add($"{path}: {k} only in the first");
            }
            else if (!SameValues(pa, pb))
            {
                diffs.Add($"{path}: {k} differs");
            }
        }

        if (a is MapiMessage ma && b is MapiMessage mb)
        {
            if (ma.Recipients.Count != mb.Recipients.Count)
            {
                diffs.Add($"{path}: recipients {ma.Recipients.Count} vs {mb.Recipients.Count}");
            }
            else
            {
                for (int i = 0; i < ma.Recipients.Count; i++)
                {
                    diffs.AddRange(Differences(ma.Recipients[i], mb.Recipients[i], path + "/recipient " + i, ignore));
                }
            }

            if (ma.Attachments.Count != mb.Attachments.Count)
            {
                diffs.Add($"{path}: attachments {ma.Attachments.Count} vs {mb.Attachments.Count}");
            }
            else
            {
                for (int i = 0; i < ma.Attachments.Count; i++)
                {
                    string ap = path + "/attachment " + i;
                    diffs.AddRange(Differences(ma.Attachments[i], mb.Attachments[i], ap, ignore));
                    MapiMessage? ea = ma.Attachments[i].EmbeddedMessage;
                    MapiMessage? eb = mb.Attachments[i].EmbeddedMessage;
                    if ((ea is null) != (eb is null))
                    {
                        diffs.Add(ap + ": embedded message presence differs");
                    }
                    else if (ea is not null)
                    {
                        diffs.AddRange(Differences(ea, eb!, ap + "/embedded", ignore));
                    }

                    MapiRawStorage? sa = ea is null ? ma.Attachments[i].ObjectStorage : null;
                    MapiRawStorage? sb = eb is null ? mb.Attachments[i].ObjectStorage : null;
                    diffs.AddRange(StorageDifferences(sa, sb, ap));
                }
            }
        }

        return diffs;
    }

    private static IEnumerable<string> StorageDifferences(MapiRawStorage? a, MapiRawStorage? b, string path)
    {
        if ((a is null) != (b is null))
        {
            yield return path + ": object storage presence differs";
            yield break;
        }

        if (a is null || b is null)
        {
            yield break;
        }

        if (a.ClassId != b.ClassId)
        {
            yield return path + ": storage class ID differs";
        }

        Dictionary<string, MapiRawStorageEntry> ea = a.Entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        foreach (MapiRawStorageEntry e in b.Entries)
        {
            if (!ea.Remove(e.Path, out MapiRawStorageEntry other) || other.IsStorage != e.IsStorage || other.Length != e.Length)
            {
                yield return $"{path}: storage entry {e.Path} differs";
            }
            else if (!e.IsStorage)
            {
                using Stream sa = a.OpenStream(e.Path);
                using Stream sb = b.OpenStream(e.Path);
                var x = new MemoryStream();
                var y = new MemoryStream();
                sa.CopyTo(x);
                sb.CopyTo(y);
                if (!x.ToArray().AsSpan().SequenceEqual(y.ToArray()))
                {
                    yield return $"{path}: storage stream {e.Path} bytes differ";
                }
            }
        }

        foreach (string left in ea.Keys)
        {
            yield return $"{path}: storage entry {left} missing";
        }
    }

    private static Dictionary<string, MapiProperty> Index(MapiPropertyBag bag)
    {
        var d = new Dictionary<string, MapiProperty>(StringComparer.Ordinal);
        foreach (MapiProperty p in bag)
        {
            d[Key(p)] = p;
        }

        return d;
    }

    private static bool SameValues(MapiProperty a, MapiProperty b)
    {
        if (a.ValueCount != b.ValueCount)
        {
            return false;
        }

        for (int i = 0; i < a.ValueCount; i++)
        {
            if (!a.GetBytes(i).Span.SequenceEqual(b.GetBytes(i).Span))
            {
                return false;
            }
        }

        return true;
    }

}
