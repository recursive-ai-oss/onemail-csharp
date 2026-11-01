using System.Text;
using RecursiveAI.OneArchive.Cfb;
using RecursiveAI.OneMail.Conversion.OxcMail;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Msg;
using static RecursiveAI.OneMail.Tests.Msg.MsgWriterTestHelpers;

namespace RecursiveAI.OneMail.Tests.Msg;

/// <summary>
/// The writer against the public data files fetched by tools/corpus (MSG, PST, OST, TNEF, mail), when they are present: whatever the readers
/// give, written as MSG and read again, has the same properties. Counts go to a report file beside the test results.
/// </summary>
public class MsgWriterCorpusTests
{
    private static string? Corpus(string sub)
    {
        string root = Environment.GetEnvironmentVariable("ONEMAIL_CORPUS") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "onemail", "corpus");
        string dir = Path.Combine(root, sub);
        return Directory.Exists(dir) ? dir : null;
    }

    private static void Report(string name, string text)
    {
        string dir = Environment.GetEnvironmentVariable("ONEMAIL_REPORT_DIR") ?? Path.GetTempPath();
        File.WriteAllText(Path.Combine(dir, name + ".txt"), text);
    }

    private sealed class Tally
    {
        public int Items;
        public int Exact;
        public int Unicode;
        public int Props;
        public readonly List<string> Failures = [];
        public readonly SortedDictionary<MsgWriteDefectKind, int> Warnings = [];
    }

    private static void RoundTrip(MapiMessage m, string name, Tally t)
    {
        t.Items++;
        // As given: property-exact.
        byte[] exact = MsgWriter.ToArray(m, Exact);
        using (MsgFile g = OpenAll(exact))
        {
            List<string> diffs = Differences(m, g.Message);
            if (diffs.Count == 0)
            {
                t.Exact++;
            }
            else
            {
                t.Failures.Add(name + " (as given): " + string.Join("; ", diffs.Take(3)));
            }

            t.Props += g.Message.Properties.Count;
            if (g.Defects.Any(d => d.Kind is not (MapiDefectKind.AmbiguousStringType or MapiDefectKind.AmbiguousCodePage or MapiDefectKind.UnknownPropertyType or MapiDefectKind.AmbiguousDuplicateProperty or MapiDefectKind.OddStringLength)))
            {
                t.Failures.Add(name + " (as given) reads back with defects: " + string.Join("; ", g.Defects.Select(d => d.Kind).Distinct()));
            }
        }

        // The default: Unicode strings. Every string reads back as the same text; only the regenerated mask differs.
        using var ms = new MemoryStream();
        MsgWriteResult r = MsgWriter.Write(m, ms, new MsgWriterOptions { MaxRecipients = 65_536, MaxAttachments = 65_536, KeepEmptyStrings = true });
        foreach (MsgWriteDefect d in r.Defects)
        {
            t.Warnings[d.Kind] = t.Warnings.GetValueOrDefault(d.Kind) + 1;
        }

        using MsgFile u = OpenAll(ms.ToArray());
        List<string> udiffs = TextualDifferences(m, u.Message);
        if (udiffs.Count == 0)
        {
            t.Unicode++;
        }
        else
        {
            t.Failures.Add(name + " (unicode): " + string.Join("; ", udiffs.Take(3)));
        }
    }

    // The same comparison with strings compared as text, whatever their type, and the regenerated properties left out.
    private static List<string> TextualDifferences(MapiObject a, MapiObject b)
    {
        var diffs = new List<string>();
        static string Key(MapiProperty p)
        {
            ushort e = MapiTypes.ElementType(p.Tag.Type);
            string cls = MapiTypes.IsString(e) ? (MapiTypes.IsMultiValued(p.Tag.Type) ? "MS" : "S") : p.Tag.Type.ToString("X4");
            return (p.Name is { } n ? "N" + n : p.Id.ToString("X4")) + "/" + cls;
        }

        Dictionary<string, MapiProperty> da = a.Properties.GroupBy(Key).ToDictionary(g => g.Key, g => g.Last());
        Dictionary<string, MapiProperty> db = b.Properties.GroupBy(Key).ToDictionary(g => g.Key, g => g.Last());
        foreach (string k in da.Keys.Union(db.Keys))
        {
            if (k.StartsWith("340D", StringComparison.Ordinal) || (k.StartsWith("3701/", StringComparison.Ordinal) && a is MapiAttachment { EmbeddedMessage: not null } or MapiAttachment { ObjectStorage: not null }))
            {
                continue;
            }

            if (!da.TryGetValue(k, out MapiProperty? pa) || !db.TryGetValue(k, out MapiProperty? pb))
            {
                diffs.Add(k + (da.ContainsKey(k) ? " missing after" : " appeared"));
                continue;
            }

            if (pa.ValueCount != pb.ValueCount)
            {
                diffs.Add(k + " value count");
                continue;
            }

            for (int i = 0; i < pa.ValueCount; i++)
            {
                bool same = MapiTypes.IsString(MapiTypes.ElementType(pa.Tag.Type))
                    ? pa.AsString(a.String8Encoding, null, i) == pb.AsString(b.String8Encoding, null, i)
                    : pa.GetBytes(i).Span.SequenceEqual(pb.GetBytes(i).Span);
                if (!same)
                {
                    diffs.Add(k + " value " + i);
                }
            }
        }

        if (a is MapiMessage ma && b is MapiMessage mb)
        {
            if (ma.Recipients.Count != mb.Recipients.Count || ma.Attachments.Count != mb.Attachments.Count)
            {
                diffs.Add("counts differ");
                return diffs;
            }

            for (int i = 0; i < ma.Recipients.Count; i++)
            {
                diffs.AddRange(TextualDifferences(ma.Recipients[i], mb.Recipients[i]).Select(x => "recipient " + i + ": " + x));
            }

            for (int i = 0; i < ma.Attachments.Count; i++)
            {
                diffs.AddRange(TextualDifferences(ma.Attachments[i], mb.Attachments[i]).Select(x => "attachment " + i + ": " + x));
                if ((ma.Attachments[i].EmbeddedMessage is null) != (mb.Attachments[i].EmbeddedMessage is null))
                {
                    diffs.Add("attachment " + i + ": embedded presence");
                }
                else if (ma.Attachments[i].EmbeddedMessage is { } ea)
                {
                    diffs.AddRange(TextualDifferences(ea, mb.Attachments[i].EmbeddedMessage!).Select(x => "attachment " + i + "/embedded: " + x));
                }
            }
        }

        return diffs;
    }

    private static string Summary(string what, Tally t) =>
        $"{what}: items {t.Items}, as-given property-exact {t.Exact}, unicode text-exact {t.Unicode}, properties {t.Props}, failures {t.Failures.Count}\n"
        + string.Join("\n", t.Warnings.Select(w => $"  warning {w.Key}: {w.Value}")) + "\n" + string.Join("\n", t.Failures.Take(20)) + "\n";

    [Fact]
    public void MsgCorpusFilesWriteBackPropertyExact()
    {
        string? dir = Corpus("msg");
        Assert.SkipWhen(dir is null, "the MSG corpus is not fetched (tools/corpus/fetch-msg.sh)");
        var t = new Tally();
        int refused = 0;
        int explained = 0;
        foreach (string path in Directory.EnumerateFiles(dir!, "*.msg", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (!MsgFile.TryOpen(File.ReadAllBytes(path), out MsgFile? f, out _))
            {
                refused++;
                continue;
            }

            using (f)
            {
                f.ReadEverything();
                // A duplicated tag cannot be written twice (its value stream has one name): one of the two is kept, as lookups return it anyway.
                if (f.Defects.Any(d => d.Kind == MapiDefectKind.AmbiguousDuplicateProperty))
                {
                    explained++;
                    continue;
                }

                RoundTrip(f.Message, Path.GetFileName(path), t);
            }
        }

        Report("msgwrite-msg", Summary("MSG corpus", t) + $"refused by the reader {refused}, skipped for duplicate tags {explained}\n");
        Assert.True(t.Items > 40, "items " + t.Items);
        Assert.Empty(t.Failures);
    }

    [Fact]
    public void PstAndOstItemsWriteBackPropertyExact()
    {
        string? dir = Corpus("pst");
        Assert.SkipWhen(dir is null, "the PST corpus is not fetched");
        var t = new Tally();
        foreach (string path in Directory.EnumerateFiles(dir!, "*", SearchOption.AllDirectories).Where(p => p.EndsWith(".pst", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".ost", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
        {
            using MailContainer c = Mail.Open(path);
            int n = 0;
            foreach (IMailItem item in c.Enumerate())
            {
                if (item is MapiMailItem mi)
                {
                    RoundTrip(mi.Message, Path.GetFileName(path) + "#" + n, t);
                }

                n++;
            }
        }

        Report("msgwrite-pst", Summary("PST/OST corpus", t));
        Assert.True(t.Items > 0);
        Assert.Empty(t.Failures);
    }

    [Fact]
    public void TnefItemsWriteBackPropertyExact()
    {
        string? dir = Corpus("tnef");
        Assert.SkipWhen(dir is null, "the TNEF corpus is not fetched");
        var t = new Tally();
        foreach (string path in Directory.EnumerateFiles(dir!, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            try
            {
                using MailContainer c = Mail.Open(path);
                foreach (IMailItem item in c.Enumerate())
                {
                    if (item is MapiMailItem mi)
                    {
                        RoundTrip(mi.Message, Path.GetFileName(path), t);
                    }
                }
            }
            catch (UnsupportedMailFormatException)
            {
                // Not a TNEF stream (a data file kept beside them).
            }
        }

        Report("msgwrite-tnef", Summary("TNEF corpus", t));
        Assert.True(t.Items > 0);
        Assert.Empty(t.Failures);
    }

    [Fact]
    public void MimeMessagesConvertToMsgAndReadBackExactlyAsTheirMapiForm()
    {
        string? dir = Corpus("enron-sample") ?? Corpus("mime4j");
        Assert.SkipWhen(dir is null, "no mail corpus is fetched");
        var t = new Tally();
        int skipped = 0;
        foreach (string path in Directory.EnumerateFiles(dir!, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Take(3000))
        {
            RecursiveAI.OneMail.Mime.MimeEntity e = RecursiveAI.OneMail.Mime.MimeEntity.Parse(File.ReadAllBytes(path));
            MapiMessage m = new MimeToMapiConverter().Convert(e).Value;
            if (m.Properties.Count == 0)
            {
                skipped++;
                continue;
            }

            RoundTrip(m, Path.GetFileName(path), t);
        }

        Report("msgwrite-mime", Summary("MIME corpus", t) + $"no properties: {skipped}\n");
        Assert.True(t.Items > 100, "items " + t.Items);
        Assert.Empty(t.Failures);
    }

    [Fact]
    public void NamedPropertyHashStreamsMatchWhatOutlookWroteAndWhatWeWrite()
    {
        string? dir = Corpus("msg");
        Assert.SkipWhen(dir is null, "the MSG corpus is not fetched (tools/corpus/fetch-msg.sh)");
        var originals = new List<byte[]>();
        var rewritten = new List<byte[]>();
        foreach (string path in Directory.EnumerateFiles(dir!, "*.msg", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (!MsgFile.TryOpen(bytes, out MsgFile? f, out _))
            {
                continue;
            }

            using (f)
            {
                originals.Add(bytes);
                f.ReadEverything();
                if (f.Defects.All(d => d.Kind != MapiDefectKind.AmbiguousDuplicateProperty))
                {
                    rewritten.Add(MsgWriter.ToArray(f.Message, Exact));
                }
            }
        }

        (int files, int names, int bad, string notes) = CheckHashes(originals, lenient: true);
        (int files2, int names2, int bad2, string notes2) = CheckHashes(rewritten, lenient: false);
        Report("msgwrite-hash", $"Outlook-written and other public files: with a name map {files}, names {names}, not where the formula puts them {bad}\n{notes}ours: files {files2}, names {names2}, not where the formula puts them {bad2}\n{notes2}");
        Assert.True(names > 0 && names2 > 0);
        Assert.Equal(0, bad);
        Assert.Equal(0, bad2);
    }

    // MS-OXMSG 2.2.3.2: for every name in the entry stream, the stream whose ID the formula gives holds an entry with the name's key (the ID, or the CRC-32 of a string name).
    private static (int Files, int Names, int Bad, string Notes) CheckHashes(IEnumerable<byte[]> msgFiles, bool lenient)
    {
        int files = 0;
        int names = 0;
        int bad = 0;
        var notes = new StringBuilder();
        foreach (byte[] bytes in msgFiles)
        {
            using var r = new CompoundFileReader(bytes);
            CfbEntry? nameid = r.GetChildren(r.Root).FirstOrDefault(e => e.RawName == "__nameid_version1.0");
            if (nameid is null)
            {
                continue;
            }

            Dictionary<string, byte[]> streams = r.GetChildren(nameid).Where(e => e.ObjectType == CfbObjectType.Stream).ToDictionary(e => e.RawName.ToUpperInvariant(), e =>
            {
                var buf = new byte[e.Size];
                using Stream s = r.OpenEntryStream(e);
                s.ReadExactly(buf);
                return buf;
            });
            byte[] guids = streams.GetValueOrDefault("__SUBSTG1.0_00020102") ?? [];
            byte[] entries = streams.GetValueOrDefault("__SUBSTG1.0_00030102") ?? [];
            byte[] strings = streams.GetValueOrDefault("__SUBSTG1.0_00040102") ?? [];
            files++;
            for (int i = 0; i + 8 <= entries.Length; i += 8)
            {
                uint idOrOffset = BitConverter.ToUInt32(entries, i);
                uint info = BitConverter.ToUInt32(entries, i + 4);
                bool isString = (info & 1) != 0;
                int guidIndex = (int)((info >> 1) & 0x7FFF);
                uint key = idOrOffset;
                if (isString)
                {
                    int len = BitConverter.ToInt32(strings, (int)idOrOffset);
                    byte[] name = strings.AsSpan((int)idOrOffset + 4, len).ToArray();
                    Guid set = guidIndex == 1 ? MapiPropertySets.Mapi : guidIndex == 2 ? MapiPropertySets.PublicStrings : new Guid(guids.AsSpan((guidIndex - 3) * 16, 16));
                    // MS-OXMSG 2.2.3.2.2.1 footnote 1: Outlook lower-cases the name of a PS_INTERNET_HEADERS property first. Files from other writers
                    // hash the name as it is (six such names in the public MSG files), which is accepted when reading someone else's file.
                    key = Crc(set == MapiPropertySets.InternetHeaders ? Encoding.Unicode.GetBytes(Encoding.Unicode.GetString(name).ToLowerInvariant()) : name);
                    if (lenient && set == MapiPropertySets.InternetHeaders && !Present(streams, key, info, guidIndex, isString))
                    {
                        key = Crc(name);
                    }
                }

                names++;
                bool found = Present(streams, key, info, guidIndex, isString);
                if (!found)
                {
                    bad++;
                    notes.Append("name ").Append(i / 8).Append(" key ").Append(key.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)).Append(" not found").Append('\n');
                }
            }
        }

        return (files, names, bad, notes.ToString());
    }

    private static bool Present(Dictionary<string, byte[]> streams, uint key, uint info, int guidIndex, bool isString)
    {
        int streamId = 0x1000 + (int)((key ^ (uint)((guidIndex << 1) | (isString ? 1 : 0))) % 0x1F);
        string streamName = "__SUBSTG1.0_" + (((uint)streamId << 16) | 0x102).ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
        byte[]? hashed = streams.GetValueOrDefault(streamName);
        for (int j = 0; hashed is not null && j + 8 <= hashed.Length; j += 8)
        {
            if (BitConverter.ToUInt32(hashed, j) == key && BitConverter.ToUInt32(hashed, j + 4) == info)
            {
                return true;
            }
        }

        return false;
    }

    // The checksum Outlook's files carry: reflected 0xEDB88320, start value 0, no final complement (see MsgPlanner.Crc32).
    private static uint Crc(byte[] data)
    {
        uint crc = 0;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        return crc;
    }
}
