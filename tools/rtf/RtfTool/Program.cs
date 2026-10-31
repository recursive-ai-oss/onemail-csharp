using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneArchive.Cfb;
using RecursiveAI.OneMail.Rtf;
using RecursiveAI.OneMail.Rtf.Compression;

// RtfTool extract <msg-root> <out-dir>   pull body streams out of every .msg below <msg-root>
// RtfTool run <out-dir>                  run OneMail over the extracted RTF bodies and write what it produced
//
// extract writes, per body stream, <out-dir>/<id>.<kind>: rtfz (PidTagRtfCompressed), html (PidTagHtml) and
// txt.u / txt.a (PidTagBody as UTF-16LE or 8-bit), plus items.tsv (id, source file, storage path, kind, bytes, SHA-256).
// run writes <id>.ours.rtf (decompressed), <id>.ours.rtfz (our compression), <id>.ours.deencap.<html|txt> (UTF-8) and
// report.tsv (one row per RTF body with sizes, CRC and defects).
// decompress-all decompresses every *.rtfz in <dir> (used for damaged streams) and writes <name>.ours.rtf and ours.tsv.
// roundtrip encapsulates the de-encapsulated bodies and the messages' own HTML and text bodies and takes them back out.
// throughput times decompression, compression and de-encapsulation over the extracted bodies (a light check, not a benchmark).
// verify reads <id>.oracle.rtfz (another program's compression of <id>.ours.rtf) and checks that OneMail decompresses it
// to the same bytes, cleanly, with the CRC verified.
return args.Length >= 1 ? args[0] switch
{
    "extract" when args.Length == 3 => Extract(args[1], args[2]),
    "run" when args.Length == 2 => Run(args[1]),
    "verify" when args.Length == 2 => Verify(args[1]),
    "decompress-all" when args.Length == 2 => DecompressAll(args[1]),
    "roundtrip" when args.Length == 2 => RoundTrip(args[1]),
    "throughput" when args.Length >= 2 => Throughput(args[1], args.Length > 2 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 5),
    _ => Usage(),
} : Usage();

static int Usage()
{
    Console.Error.WriteLine("usage: RtfTool extract <msg-root> <out-dir> | run <out-dir>");
    return 2;
}

static int Extract(string root, string outDir)
{
    Directory.CreateDirectory(outDir);
    var items = new StringBuilder("id\tfile\tstorage\tkind\tbytes\tsha256\n");
    int msgs = 0;
    int rtfs = 0;
    foreach (string path in Directory.EnumerateFiles(root, "*.msg", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
    {
        msgs++;
        string set = Path.GetFileName(Path.GetDirectoryName(path)!);
        string stem = set + "__" + Path.GetFileNameWithoutExtension(path);
        try
        {
            using var reader = new CompoundFileReader(File.ReadAllBytes(path));
            Walk(reader, reader.Root, "", stem, path, outDir, items, ref rtfs);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or RecursiveAI.OneCompress.CompressionException)
        {
            Console.Error.WriteLine("skip " + path + ": " + e.Message);
        }
    }

    File.WriteAllText(Path.Combine(outDir, "items.tsv"), items.ToString());
    Console.WriteLine($"{msgs} msg files, {rtfs} RTF bodies");
    return 0;
}

static void Walk(CompoundFileReader reader, CfbEntry storage, string path, string stem, string file, string outDir, StringBuilder items, ref int rtfs)
{
    foreach (CfbEntry e in reader.GetChildren(storage))
    {
        if (e.ObjectType == CfbObjectType.Storage)
        {
            Walk(reader, e, path + "/" + e.Name, stem, file, outDir, items, ref rtfs);
            continue;
        }

        string? kind = e.Name switch
        {
            "__substg1.0_10090102" => "rtfz",
            "__substg1.0_10130102" or "__substg1.0_1013001E" => "html",
            "__substg1.0_1013001F" => "html.u",
            "__substg1.0_1000001F" => "txt.u",
            "__substg1.0_1000001E" => "txt.a",
            _ => null,
        };
        if (kind is null || e.ObjectType != CfbObjectType.Stream)
        {
            continue;
        }

        byte[] data = new byte[(int)e.Size];
        using (Stream s = reader.OpenEntryStream(e))
        {
            s.ReadExactly(data);
        }

        string id = stem + (path.Length == 0 ? string.Empty : "__" + path.Replace('/', '_').Trim('_').Replace("__attach_version1.0_#", "att", StringComparison.Ordinal));
        File.WriteAllBytes(Path.Combine(outDir, id + "." + kind), data);
        items.Append(id).Append('\t').Append(Path.GetFileName(file)).Append('\t').Append(path.Length == 0 ? "/" : path).Append('\t').Append(kind).Append('\t')
            .Append(data.Length).Append('\t').Append(Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant()).Append('\n');
        if (kind == "rtfz")
        {
            rtfs++;
        }
    }
}

static int Run(string dir)
{
    var report = new StringBuilder("id\tcompressed\tcomptype\traw\tcrc_ok\tdefects\tours_recompressed\tdeencap\tdeencap_chars\tdeencap_defects\tdefault_cp\n");
    int n = 0;
    foreach (string z in Directory.EnumerateFiles(dir, "*.rtfz").Where(f => !f.Contains(".ours.", StringComparison.Ordinal) && !f.Contains(".oracle.", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
    {
        n++;
        string id = Path.GetFileNameWithoutExtension(z);
        byte[] input = File.ReadAllBytes(z);
        RtfDecompressionResult r = RtfDecompressor.Decompress(input);
        File.WriteAllBytes(Path.Combine(dir, id + ".ours.rtf"), r.Data);
        byte[] again = RtfCompressor.Compress(r.Data);
        File.WriteAllBytes(Path.Combine(dir, id + ".ours.rtfz"), again);
        RtfDeencapsulationResult d = RtfDeencapsulator.Deencapsulate(r.Data);
        if (d.Kind != RtfEncapsulation.None)
        {
            File.WriteAllText(Path.Combine(dir, id + ".ours.deencap." + (d.Kind == RtfEncapsulation.Html ? "html" : "txt")), d.Text, new UTF8Encoding(false));
        }

        report.Append(id).Append('\t').Append(input.Length).Append('\t').Append(r.Header?.Type).Append('\t').Append(r.Data.Length).Append('\t')
            .Append(r.CrcVerified ? 1 : 0).Append('\t').Append(string.Join(',', r.Defects.Select(x => x.Kind))).Append('\t').Append(again.Length).Append('\t')
            .Append(d.Kind).Append('\t').Append(d.Text.Length).Append('\t').Append(string.Join(',', d.Defects.Select(x => x.Kind))).Append('\t').Append(d.DefaultCodePage).Append('\n');
    }

    File.WriteAllText(Path.Combine(dir, "report.tsv"), report.ToString());
    Console.WriteLine(n + " RTF bodies processed");
    return 0;
}

static int Verify(string dir)
{
    int ok = 0;
    int bad = 0;
    foreach (string z in Directory.EnumerateFiles(dir, "*.oracle.rtfz").Order(StringComparer.Ordinal))
    {
        string id = Path.GetFileName(z)[..^".oracle.rtfz".Length];
        byte[] raw = File.ReadAllBytes(Path.Combine(dir, id + ".ours.rtf"));
        RtfDecompressionResult r = RtfDecompressor.Decompress(File.ReadAllBytes(z));
        if (r.IsClean && r.CrcVerified && r.Data.AsSpan().SequenceEqual(raw))
        {
            ok++;
        }
        else
        {
            bad++;
            Console.WriteLine("MISMATCH " + id + " " + string.Join(',', r.Defects.Select(x => x.Kind)));
        }
    }

    Console.WriteLine($"verify: {ok} identical, {bad} different");
    return bad == 0 ? 0 : 1;
}

static int Throughput(string dir, int seconds)
{
    var packed = Directory.EnumerateFiles(dir, "*.rtfz").Where(f => !f.Contains(".ours.", StringComparison.Ordinal) && !f.Contains(".oracle.", StringComparison.Ordinal))
        .Select(File.ReadAllBytes).ToList();
    var raws = packed.Select(p => RtfDecompressor.Decompress(p).Data).ToList();
    long compBytes = packed.Sum(p => (long)p.Length);
    long rawBytes = raws.Sum(p => (long)p.Length);
    Console.WriteLine($"{packed.Count} bodies, {compBytes} compressed bytes, {rawBytes} raw bytes");
    Measure("decompress", seconds, rawBytes, () => packed.ForEach(p => RtfDecompressor.Decompress(p)));
    Measure("compress", seconds, rawBytes, () => raws.ForEach(r => RtfCompressor.Compress(r)));
    Measure("deencapsulate", seconds, rawBytes, () => raws.ForEach(r => RtfDeencapsulator.Deencapsulate(r)));
    return 0;
}

static void Measure(string name, int seconds, long bytesPerPass, Action pass)
{
    pass();
    long a0 = GC.GetAllocatedBytesForCurrentThread();
    var sw = System.Diagnostics.Stopwatch.StartNew();
    int passes = 0;
    while (sw.Elapsed.TotalSeconds < seconds)
    {
        pass();
        passes++;
    }

    double s = sw.Elapsed.TotalSeconds;
    long alloc = (GC.GetAllocatedBytesForCurrentThread() - a0) / passes;
    Console.WriteLine($"{name}: {bytesPerPass * passes / s / 1e6:F1} MB/s of RTF, {alloc} allocated bytes per pass ({alloc / (double)bytesPerPass:F2} per RTF byte), {passes} passes");
}

static int DecompressAll(string dir)
{
    var rows = new StringBuilder("name\tlength\tsha256\tcrc_ok\tdefects\n");
    foreach (string z in Directory.EnumerateFiles(dir, "*.rtfz").Order(StringComparer.Ordinal))
    {
        RtfDecompressionResult r = RtfDecompressor.Decompress(File.ReadAllBytes(z));
        rows.Append(Path.GetFileName(z)).Append('\t').Append(r.Data.Length).Append('\t').Append(Convert.ToHexString(SHA256.HashData(r.Data)).ToLowerInvariant()).Append('\t')
            .Append(r.CrcVerified ? 1 : 0).Append('\t').Append(string.Join(',', r.Defects.Select(x => x.Kind))).Append('\n');
    }

    File.WriteAllText(Path.Combine(dir, "ours.tsv"), rows.ToString());
    return 0;
}

static string CrLf(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\n", "\r\n", StringComparison.Ordinal);

static int RoundTrip(string dir)
{
    var utf8 = new UTF8Encoding(false, true);
    int exact = 0;
    int normalised = 0;
    int bad = 0;
    foreach (string f in Directory.EnumerateFiles(dir).Order(StringComparer.Ordinal))
    {
        string name = Path.GetFileName(f);
        bool html = name.EndsWith(".ours.deencap.html", StringComparison.Ordinal) || name.EndsWith(".html", StringComparison.Ordinal) && !name.Contains(".ours.", StringComparison.Ordinal)
            || name.EndsWith(".html.u", StringComparison.Ordinal);
        bool text = name.EndsWith(".ours.deencap.txt", StringComparison.Ordinal) || name.EndsWith(".txt.u", StringComparison.Ordinal) || name.EndsWith(".txt.a", StringComparison.Ordinal);
        if (!html && !text)
        {
            continue;
        }

        byte[] bytes = File.ReadAllBytes(f);
        string original;
        if (name.EndsWith(".u", StringComparison.Ordinal))
        {
            original = Encoding.Unicode.GetString(bytes);
        }
        else
        {
            try
            {
                original = utf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                original = Encoding.Latin1.GetString(bytes);
            }
        }

        byte[] rtf = html ? RtfEncapsulator.EncapsulateHtml(original) : RtfEncapsulator.EncapsulateText(original);
        RtfDeencapsulationResult r = RtfDeencapsulator.Deencapsulate(rtf);
        if (r.Text == original && r.Defects.Count == 0)
        {
            exact++;
        }
        else if (r.Text == CrLf(original) && r.Defects.Count == 0)
        {
            normalised++;
        }
        else
        {
            bad++;
            Console.WriteLine("MISMATCH " + name);
        }
    }

    Console.WriteLine($"roundtrip: {exact} exact, {normalised} equal after line endings became CRLF, {bad} different");
    return bad == 0 ? 0 : 1;
}
