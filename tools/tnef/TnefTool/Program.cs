using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Sources;
using RecursiveAI.OneMail.Tests.Tnef;
using RecursiveAI.OneMail.Tnef;

namespace TnefTool;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: TnefTool dump|stats|roundtrip|alloc <files...> | gen <directory>");
            return 2;
        }

        switch (args[0])
        {
            case "gen":
                return Gen(args[1]);
            case "dump":
                foreach (string f in args[1..])
                {
                    Console.WriteLine(Dump(f));
                }

                return 0;
            case "stats":
                foreach (string f in args[1..])
                {
                    Console.WriteLine(Stats(f));
                }

                return 0;
            case "roundtrip":
                return RoundTrip(args[1..]);
            case "alloc":
                foreach (string f in args[1..])
                {
                    byte[] b = File.ReadAllBytes(f);
                    TnefMessage.Read(b);
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    TnefMessage m = TnefMessage.Read(b);
                    long open = GC.GetAllocatedBytesForCurrentThread() - before;
                    Console.WriteLine($"{Path.GetFileName(f)}\tbytes={b.Length}\tattributes={m.Document.Attributes.Count}\tallocated_on_open={open}\tper_input_byte={open / (double)Math.Max(1, b.Length):F2}");
                }

                return 0;
            default:
                Console.Error.WriteLine("unknown command " + args[0]);
                return 2;
        }
    }

    private static int Gen(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "gen-spec-sample.tnef"), TnefTestBuilder.SpecSampleStart());
        File.WriteAllBytes(Path.Combine(dir, "gen-rich-1252.tnef"), TnefTestBuilder.Rich());
        File.WriteAllBytes(Path.Combine(dir, "gen-rich-1251.tnef"), TnefTestBuilder.Rich(1251));
        File.WriteAllBytes(Path.Combine(dir, "gen-plain-1252.tnef"), TnefTestBuilder.Rich(1252, everyType: false));
        File.WriteAllBytes(Path.Combine(dir, "gen-legacy-only.tnef"), TnefTestBuilder.LegacyOnly());

        // what our writer makes of the same messages, so that other readers can be run over it
        foreach (string name in new[] { "gen-rich-1252", "gen-rich-1251", "gen-plain-1252", "gen-legacy-only", "gen-spec-sample" })
        {
            TnefMessage m = TnefMessage.Read(File.ReadAllBytes(Path.Combine(dir, name + ".tnef")));
            using var ms = new MemoryStream();
            IReadOnlyList<string> warnings = TnefMessageWriter.Write(m, ms);
            File.WriteAllBytes(Path.Combine(dir, name.Replace("gen-", "written-", StringComparison.Ordinal) + ".tnef"), ms.ToArray());
            foreach (string w in warnings)
            {
                Console.Error.WriteLine(name + ": " + w);
            }
        }

        Console.WriteLine("wrote fixtures to " + dir);
        return 0;
    }

    private static string Hash(ReadOnlySpan<byte> b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static string HashProperty(MapiProperty p)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int i = 0; i < p.ValueCount; i++)
        {
            using Stream s = p.OpenRead(i);
            byte[] buf = new byte[65536];
            int n;
            long len = p.GetByteLength(i);
            h.AppendData(BitConverter.GetBytes(len));
            while ((n = s.Read(buf)) > 0)
            {
                h.AppendData(buf.AsSpan(0, n));
            }
        }

        return Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
    }

    private static string Dump(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        TnefMessage m = TnefMessage.Read(bytes);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("file", Path.GetFileName(path));
            w.WriteNumber("bytes", bytes.Length);
            WriteMessage(w, m, top: true);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteMessage(Utf8JsonWriter w, TnefMessage m, bool top)
    {
        w.WriteBoolean("signature", m.Document.HasSignature);
        w.WriteNumber("key", m.Document.LegacyKey);
        if (m.OemCodePage is int cp)
        {
            w.WriteNumber("oem", cp);
        }

        w.WriteStartArray("attributes");
        foreach (TnefAttributeRecord a in m.Document.Attributes)
        {
            w.WriteStartObject();
            w.WriteNumber("level", a.LevelValue);
            w.WriteNumber("id", a.Id & 0xFFFF);
            w.WriteNumber("length", a.Length);
            w.WriteBoolean("checksumOk", a.ChecksumValid);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteString("class", m.MessageClass);
        w.WriteString("subject", m.Subject);
        w.WriteString("body", m.Body);
        WriteProps(w, m.Properties, m);
        w.WriteStartArray("recipients");
        foreach (MapiRecipient r in m.Recipients)
        {
            w.WriteStartObject();
            w.WriteString("name", r.DisplayName);
            w.WriteString("email", r.EmailAddress);
            w.WriteNumber("props", r.Properties.Count);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteStartArray("attachments");
        foreach (MapiAttachment a in m.Attachments)
        {
            w.WriteStartObject();
            w.WriteString("name", a.FileName);
            w.WriteString("longName", a.GetString(MapiPid.AttachLongFilename));
            w.WriteNumber("method", a.AttachMethodValue ?? -1);
            MapiProperty? data = a.Properties.Get(MapiPid.AttachDataBinary, MapiType.Binary);
            if (data is not null)
            {
                w.WriteNumber("size", data.GetByteLength());
                using Stream s = data.OpenRead();
                w.WriteString("sha256", Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant());
            }

            WriteProps(w, a.Properties, a);
            if (a.EmbeddedMessage is TnefMessage inner)
            {
                // the nested stream's own bytes, which other readers hand out as the attachment
                byte[] nested = new byte[inner.Document.Source.Length];
                inner.Document.Source.ReadFully(0, nested);
                w.WriteNumber("embeddedSize", nested.Length);
                w.WriteString("embeddedSha256", Hash(nested));
                w.WriteStartObject("embedded");
                WriteMessage(w, inner, top: false);
                w.WriteEndObject();
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteStartArray("defects");
        foreach (TnefDefect d in m.TnefDefects)
        {
            w.WriteStartObject();
            w.WriteString("kind", d.Kind.ToString());
            w.WriteNumber("offset", d.Offset);
            w.WriteString("detail", d.Detail);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        _ = top;
    }

    private static void WriteProps(Utf8JsonWriter w, MapiPropertyBag bag, MapiObject owner)
    {
        w.WriteStartArray("props");
        foreach (MapiProperty p in bag)
        {
            w.WriteStartObject();
            w.WriteString("id", p.Id.ToString("X4"));
            w.WriteString("type", p.Tag.Type.ToString("X4"));
            if (p.Name is { } n)
            {
                w.WriteString("name", n.ToString());
            }

            w.WriteNumber("values", p.ValueCount);
            w.WriteNumber("length", p.GetByteLength());
            if (p.GetByteLength() < (1 << 16))
            {
                w.WriteString("hex", Convert.ToHexString(p.GetBytes().Span));
            }

            if (p.Type is MapiType.String8 or MapiType.UnicodeString && p.GetByteLength() < (1 << 16))
            {
                w.WriteString("text", p.AsString(owner.String8Encoding));
            }

            w.WriteString("sha256", HashProperty(p));
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static string Stats(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        TnefMessage m = TnefMessage.Read(bytes);
        var kinds = new SortedDictionary<string, int>();
        int props = 0;
        int recipients = 0;
        int attachments = 0;
        int embedded = 0;
        int named = 0;
        int multi = 0;
        void Visit(TnefMessage x)
        {
            foreach (TnefDefect d in x.TnefDefects)
            {
                kinds[d.Kind.ToString()] = kinds.GetValueOrDefault(d.Kind.ToString()) + 1;
            }

            foreach (MapiObject o in Collect(x))
            {
                foreach (MapiProperty p in o.Properties)
                {
                    props++;
                    named += p.Tag.IsNamed ? 1 : 0;
                    multi += p.IsMultiValued ? 1 : 0;
                }
            }

            recipients += x.Recipients.Count;
            attachments += x.Attachments.Count;
            foreach (MapiAttachment a in x.Attachments)
            {
                if (a.EmbeddedMessage is TnefMessage inner)
                {
                    embedded++;
                    Visit(inner);
                }
            }
        }

        Visit(m);
        string d = string.Join(", ", kinds.Select(kv => kv.Key + "=" + kv.Value));
        return $"{Path.GetFileName(path)}\tbytes={bytes.Length}\tattributes={m.Document.Attributes.Count}\tproperties={props} (named {named}, multi-valued {multi})\trecipients={recipients}\tattachments={attachments}\tembedded={embedded}\tdefects=[{d}]";
    }

    private static IEnumerable<MapiObject> Collect(TnefMessage m)
    {
        yield return m;
        foreach (MapiRecipient r in m.Recipients)
        {
            yield return r;
        }

        foreach (MapiAttachment a in m.Attachments)
        {
            yield return a;
        }
    }

    private static int RoundTrip(string[] files)
    {
        int bad = 0;
        foreach (string f in files)
        {
            byte[] bytes = File.ReadAllBytes(f);
            TnefDocument d = TnefDocument.Read(bytes);
            using var ms = new MemoryStream();
            string attr;
            if (!d.HasSignature)
            {
                attr = "not-tnef";
            }
            else
            {
                d.WriteTo(ms);
                attr = ms.ToArray().AsSpan().SequenceEqual(bytes) ? "identical" : "DIFFERENT";
            }

            TnefMessage first = TnefMessage.Read(d);
            using var w = new MemoryStream();
            IReadOnlyList<string> warnings = TnefMessageWriter.Write(first, w);
            TnefMessage second = TnefMessage.Read(w.ToArray());
            List<string> diffs = d.HasSignature ? Differences(first, second, "message") : [];
            string mapi = !d.HasSignature ? "n/a" : diffs.Count == 0 ? "property-exact" : "DIFFERENT (" + diffs[0] + ")";
            if (attr == "DIFFERENT" || mapi.StartsWith("DIFFERENT", StringComparison.Ordinal))
            {
                bad++;
            }

            Console.WriteLine($"{Path.GetFileName(f)}\tattribute-layer={attr}\tmapi={mapi}\twriter-warnings={warnings.Count}");
        }

        return bad == 0 ? 0 : 1;
    }

    private static List<string> Differences(MapiObject a, MapiObject b, string path)
    {
        var diffs = new List<string>();
        static string Key(MapiProperty p) => p.Tag.ToHex() + (p.Name is { } n ? "/" + n : "");
        List<MapiProperty> la = a.Properties.OrderBy(Key, StringComparer.Ordinal).ToList();
        List<MapiProperty> lb = b.Properties.OrderBy(Key, StringComparer.Ordinal).ToList();
        if (la.Count != lb.Count)
        {
            diffs.Add($"{path}: {la.Count} properties vs {lb.Count}");
        }
        else
        {
            for (int i = 0; i < la.Count; i++)
            {
                if (Key(la[i]) != Key(lb[i]) || HashProperty(la[i]) != HashProperty(lb[i]))
                {
                    diffs.Add($"{path}: {Key(la[i])}");
                }
            }
        }

        if (a is MapiMessage ma && b is MapiMessage mb)
        {
            if (ma.Recipients.Count != mb.Recipients.Count || ma.Attachments.Count != mb.Attachments.Count)
            {
                diffs.Add($"{path}: recipient/attachment counts differ");
                return diffs;
            }

            for (int i = 0; i < ma.Recipients.Count; i++)
            {
                diffs.AddRange(Differences(ma.Recipients[i], mb.Recipients[i], path + "/recipient " + i));
            }

            for (int i = 0; i < ma.Attachments.Count; i++)
            {
                diffs.AddRange(Differences(ma.Attachments[i], mb.Attachments[i], path + "/attachment " + i));
                MapiMessage? ea = ma.Attachments[i].EmbeddedMessage;
                MapiMessage? eb = mb.Attachments[i].EmbeddedMessage;
                if ((ea is null) != (eb is null))
                {
                    diffs.Add(path + "/attachment " + i + ": embedded presence differs");
                }
                else if (ea is not null)
                {
                    diffs.AddRange(Differences(ea, eb!, path + "/attachment " + i + "/embedded"));
                }
            }
        }

        return diffs;
    }
}
