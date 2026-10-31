using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneArchive.Cfb;
using RecursiveAI.OneMail;
using RecursiveAI.OneMail.Rtf;
using RecursiveAI.OneMail.Rtf.Compression;

// RtfTool extract <msg-root> <out-dir>   pull body streams out of every .msg below <msg-root>
// RtfTool run <out-dir>                  run OneMail over the extracted RTF bodies and write what it produced
//
// extract writes, per body stream, <out-dir>/<id>.<kind>: rtfz (PidTagRtfCompressed), html (PidTagHtml) and
// txt.u / txt.a (PidTagBody as UTF-16LE or 8-bit), plus items.tsv (id, source file, storage path, kind, bytes, SHA-256).
// run writes <id>.ours.rtf (decompressed), <id>.ours.rtfz (our compression), <id>.ours.deencap.<html|txt> (UTF-8) and
// report.tsv (one row per RTF body with sizes, CRC and defects).
// native <out-dir> <path>... opens each MSG, TNEF, PST or OST (a file, or a directory searched recursively) and writes the RTF body of every item
// whose RTF is not an HTML or text encapsulation to <out-dir>/<id>.rtf, with native.tsv (id, source, items, RTF bodies, encapsulated).
// html <dir> converts every <id>.rtf in <dir> with RtfHtmlConverter and writes <id>.ours.html, <id>.ours.txt and html.tsv.
// alloc <dir> reports the bytes RtfHtmlConverter allocates per KB of RTF (HTML, then HTML and text) over every <id>.rtf in <dir>.
// decompress-all decompresses every *.rtfz in <dir> (used for damaged streams) and writes <name>.ours.rtf and ours.tsv.
// roundtrip encapsulates the de-encapsulated bodies and the messages' own HTML and text bodies and takes them back out.
// throughput times decompression, compression and de-encapsulation over the extracted bodies (a light check, not a benchmark).
// verify reads <id>.oracle.rtfz (another program's compression of <id>.ours.rtf) and checks that OneMail decompresses it
// to the same bytes, cleanly, with the CRC verified.
return args.Length >= 1 ? await (args[0] switch
{
    "extract" when args.Length == 3 => Task.FromResult(Extract(args[1], args[2])),
    "run" when args.Length == 2 => Task.FromResult(Run(args[1])),
    "native" when args.Length >= 3 => Native(args[1], args[2..]),
    "html" when args.Length == 2 => Task.FromResult(Html(args[1])),
    "alloc" when args.Length == 2 => Task.FromResult(Alloc(args[1])),
    "verify" when args.Length == 2 => Task.FromResult(Verify(args[1])),
    "decompress-all" when args.Length == 2 => Task.FromResult(DecompressAll(args[1])),
    "roundtrip" when args.Length == 2 => Task.FromResult(RoundTrip(args[1])),
    "throughput" when args.Length >= 2 => Task.FromResult(Throughput(args[1], args.Length > 2 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 5)),
    _ => Task.FromResult(Usage()),
}) : Usage();

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

static async Task<int> Native(string outDir, string[] paths)
{
    Directory.CreateDirectory(outDir);
    var tsv = new StringBuilder("id\tsource\titems\trtf\tencapsulated\tnative\n");
    int totalItems = 0, totalRtf = 0, totalEncap = 0, totalNative = 0;
    var files = new List<string>();
    foreach (string path in paths)
    {
        if (Directory.Exists(path))
        {
            files.AddRange(Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories));
        }
        else
        {
            files.Add(path);
        }
    }

    files.Sort(StringComparer.Ordinal);
    foreach (string file in files)
    {
        string ext = Path.GetExtension(file).ToLowerInvariant();
        if (ext is not (".msg" or ".pst" or ".ost" or ".dat" or ".tnef" or ".oft"))
        {
            continue;
        }

        int items = 0, rtf = 0, encap = 0, native = 0;
        string stem = Path.GetFileNameWithoutExtension(file);
        string parent = Path.GetFileName(Path.GetDirectoryName(file) ?? "");
        try
        {
            await using MailContainer c = Mail.Open(file);
            await foreach (IMailItem item in c)
            {
                items++;
                MailBody? body = item.RtfBody;
                if (body is null)
                {
                    continue;
                }

                rtf++;
                using Stream s = body.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                byte[] data = ms.ToArray();
                if (RtfDeencapsulator.Detect(data) != RtfEncapsulation.None)
                {
                    encap++;
                    continue;
                }

                native++;
                string id = parent + "__" + stem + "__" + items.ToString(System.Globalization.CultureInfo.InvariantCulture);
                File.WriteAllBytes(Path.Combine(outDir, id + ".rtf"), data);
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(file + ": " + e.GetType().Name + ": " + e.Message);
        }

        totalItems += items;
        totalRtf += rtf;
        totalEncap += encap;
        totalNative += native;
        tsv.Append(parent + "__" + stem).Append('\t').Append(Path.GetFileName(file)).Append('\t').Append(items).Append('\t').Append(rtf).Append('\t').Append(encap).Append('\t').Append(native).Append('\n');
    }

    File.WriteAllText(Path.Combine(outDir, "native.tsv"), tsv.ToString());
    Console.WriteLine($"files {files.Count} items {totalItems} with RTF {totalRtf} encapsulated {totalEncap} native {totalNative}");
    return 0;
}

static int Html(string dir)
{
    var tsv = new StringBuilder("id\trtf_bytes\thtml_chars\ttext_chars\timages\tdefects\n");
    foreach (string file in Directory.EnumerateFiles(dir, "*.rtf").Where(f => !f.EndsWith(".ours.rtf", StringComparison.Ordinal)).OrderBy(f => f, StringComparer.Ordinal))
    {
        byte[] data = File.ReadAllBytes(file);
        RtfHtmlResult r = RtfHtmlConverter.Convert(data);
        string id = Path.GetFileNameWithoutExtension(file);
        File.WriteAllText(Path.Combine(dir, id + ".ours.html"), r.Html, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, id + ".ours.txt"), r.Text, new UTF8Encoding(false));
        tsv.Append(id).Append('\t').Append(data.Length).Append('\t').Append(r.Html.Length).Append('\t').Append(r.Text.Length).Append('\t').Append(r.Images.Count).Append('\t')
            .Append(string.Join(',', r.Defects.Select(d => d.Kind.ToString()).Distinct())).Append('\n');
    }

    File.WriteAllText(Path.Combine(dir, "html.tsv"), tsv.ToString());
    return 0;
}

static int Alloc(string dir)
{
    var files = Directory.EnumerateFiles(dir, "*.rtf").Where(f => !f.EndsWith(".ours.rtf", StringComparison.Ordinal)).OrderBy(f => f, StringComparer.Ordinal).Select(File.ReadAllBytes).ToList();
    long inputBytes = files.Sum(f => (long)f.Length);
    foreach (byte[] f in files)
    {
        RtfHtmlConverter.Convert(f).Text.GetHashCode(); // warm up, including the text writer
    }

    long htmlOnly = 0, withText = 0, outputChars = 0, plainInput = 0, plainText = 0, plainHtml = 0;
    foreach (byte[] f in files)
    {
        long best = long.MaxValue, bestText = long.MaxValue;
        for (int rep = 0; rep < 5; rep++)
        {
            long a = GC.GetAllocatedBytesForCurrentThread();
            RtfHtmlResult r = RtfHtmlConverter.Convert(f);
            long b = GC.GetAllocatedBytesForCurrentThread();
            _ = r.Text;
            long c = GC.GetAllocatedBytesForCurrentThread();
            best = Math.Min(best, b - a);
            bestText = Math.Min(bestText, c - a);
            outputChars = rep == 0 ? outputChars + r.Html.Length : outputChars;
        }

        htmlOnly += best;
        withText += bestText;
        if (RtfHtmlConverter.Convert(f).Images.Count == 0)
        {
            plainInput += f.Length;
            plainHtml += best;
            plainText += bestText;
        }
    }

    double kb = inputBytes / 1024.0;
    Console.WriteLine($"{files.Count} documents, {kb:F1} KB of RTF, {outputChars / 1024.0:F1} K characters of HTML");
    Console.WriteLine($"allocated per KB of RTF: HTML {htmlOnly / kb:F0} B, HTML and text {withText / kb:F0} B");
    double pkb = plainInput / 1024.0;
    Console.WriteLine($"documents without pictures: {pkb:F1} KB of RTF, HTML {plainHtml / pkb:F0} B per KB, HTML and text {plainText / pkb:F0} B per KB");
    return 0;
}
