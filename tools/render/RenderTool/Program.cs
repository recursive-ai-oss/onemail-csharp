using System.Diagnostics;
using System.Globalization;
using System.Text;
using RecursiveAI.OneHtml;
using RecursiveAI.OneHtml.Dom;
using RecursiveAI.OneMail;
using RecursiveAI.OneMail.Display;
using RecursiveAI.OneMail.Render;

// RenderTool gen OUTDIR                          write the generated hostile and script cases as .eml files
// RenderTool sweep OUTDIR [--per N] [--allow-remote] [--keep N] PATH...
//     render every item of the files and directories (any format Mail.Open reads) to PDF and PNG; compare each PDF's text (pdftotext
//     -raw) with the display HTML's text; write OUTDIR/results.jsonl and OUTDIR/docs/<id>.html + <id>.pdf for the first --keep items
// Environment: PDFTOTEXT (default pdftotext).
return args.Length < 2 ? Usage() : args[0] switch
{
    "gen" => Gen(args[1]),
    "sweep" => await Sweep(args[1..]),
    "doc" => Doc(args[1]),
    "rawpdf" when args.Length >= 3 => RawPdf(args[1], args[2]),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("RenderTool gen OUTDIR | sweep OUTDIR [--per N] [--allow-remote] [--keep N] PATH...");
    return 2;
}

static int Doc(string eml)
{
    Console.Write(MessageRenderer.BuildDocument(Mail.Parse(File.ReadAllBytes(eml)), null, out _));
    return 0;
}

// Lays an HTML file out with OneRender directly (no OneMail wrapper), to tell an engine behaviour from ours.
static int RawPdf(string html, string pdf)
{
    using var fs = File.Create(pdf);
    var r = OneRender.Pdf.HtmlPdf.ToPdf(File.ReadAllText(html), fs, new OneRender.Pdf.HtmlPdfOptions { Output = new OneRender.Pdf.PdfOutputOptions { LeaveOpen = true } });
    Console.WriteLine(r.PageCount + " pages");
    return 0;
}

static string Eml(string subject, string contentType, string body, string extra = "") =>
    "From: Ann <ann@example.com>\r\nTo: Bob <bob@example.com>\r\nSubject: " + subject + "\r\nDate: Mon, 14 May 2001 23:39:00 +0200\r\nMIME-Version: 1.0\r\n" + extra
    + "Content-Type: " + contentType + "\r\n\r\n" + body.Replace("\n", "\r\n", StringComparison.Ordinal);

static int Gen(string dir)
{
    Directory.CreateDirectory(dir);
    const string gif = "R0lGODlhAQABAAAAACwAAAAAAQABAAACAkQBADs=";
    void W(string name, string text) => File.WriteAllText(Path.Combine(dir, name + ".eml"), text, Encoding.UTF8);

    var rows = new StringBuilder("<table border=1>");
    for (int i = 0; i < 3000; i++)
    {
        rows.Append("<tr><td>row ").Append(i).Append("</td><td>alpha</td><td>beta</td><td>gamma</td></tr>");
    }

    W("long-table", Eml("Long table", "text/html; charset=utf-8", rows + "</table>"));
    W("wide-table", Eml("Wide table", "text/html; charset=utf-8", "<table border=1><tr>" + string.Concat(Enumerable.Range(0, 60).Select(i => "<td>column" + i + "</td>")) + "</tr></table>"));
    var huge = new StringBuilder("<table>");
    for (int i = 0; i < 100_000; i++)
    {
        huge.Append("<tr><td>r").Append(i).Append("</td><td>a</td><td>b</td></tr>");
    }

    W("huge-table", Eml("Huge table", "text/html; charset=utf-8", huge + "</table>"));
    W("many-columns", Eml("Many columns", "text/html; charset=utf-8", "<table><tr>" + string.Concat(Enumerable.Range(0, 5000).Select(i => "<td>c" + i + "</td>")) + "</tr></table>"));
    W("wide-image", Eml("Wide image", "multipart/related; boundary=b", "--b\nContent-Type: text/html; charset=utf-8\n\n<p>before</p><img src=\"cid:i\" width=\"6000\" height=\"20\"><p>after</p>\n--b\nContent-Type: image/gif\nContent-ID: <i>\nContent-Transfer-Encoding: base64\n\n" + gif + "\n--b--\n"));
    W("rtl", Eml("RTL", "text/html; charset=utf-8", "<p dir=rtl>שלום עולם, זו הודעה בעברית</p><p dir=rtl>مرحبا بالعالم رسالة عربية</p><p>mixed: abc שלום def</p>"));
    W("cjk", Eml("CJK", "text/html; charset=utf-8", "<p>日本語のメッセージです。</p><p>中文消息 你好世界</p><p>한국어 메시지입니다</p>"));
    W("emoji", Eml("Emoji", "text/html; charset=utf-8", "<p>Party 🎉 time 😀 and ❤️ done</p>"));
    W("latin-greek-cyrillic", Eml("Scripts", "text/html; charset=utf-8", "<p>Grüße, Ελληνικά, Русский текст, café, naïve</p>"));
    var many = new StringBuilder("<p>");
    for (int i = 0; i < 10_000; i++)
    {
        many.Append("<img src=\"cid:i\" width=2 height=2>");
    }

    W("images-10000", Eml("10000 images", "multipart/related; boundary=b", "--b\nContent-Type: text/html; charset=utf-8\n\n" + many + "</p>\n--b\nContent-Type: image/gif\nContent-ID: <i>\nContent-Transfer-Encoding: base64\n\n" + gif + "\n--b--\n"));
    var nest = new StringBuilder();
    for (int i = 0; i < 3000; i++)
    {
        nest.Append("<div><blockquote>");
    }

    W("deep-nesting", Eml("Deep nesting", "text/html; charset=utf-8", nest + "bottom text"));
    var nestTables = new StringBuilder();
    for (int i = 0; i < 150; i++)
    {
        nestTables.Append("<table><tr><td>");
    }

    W("nested-tables", Eml("Nested tables", "text/html; charset=utf-8", nestTables + "innermost"));
    var text = new StringBuilder();
    for (int i = 0; i < 30_000; i++)
    {
        text.Append("This is line ").Append(i).Append(" of a very long plain text message that goes on and on.\n");
    }

    W("very-long-text", Eml("Very long", "text/plain; charset=utf-8", text.ToString()));
    W("long-line", Eml("One huge word", "text/plain; charset=utf-8", new string('x', 200_000)));
    W("remote-image", Eml("Remote", "text/html; charset=utf-8", "<p>tracking <img src=\"http://tracker.example/p.gif?id=1\" width=1 height=1> <img src=\"http://img.example/logo.png\"></p><link rel=stylesheet href=\"http://css.example/a.css\"><p style=\"background:url(http://bg.example/b.png)\">styled</p>"));
    W("plain", Eml("Plain", "text/plain; charset=utf-8", "line one\n\n> quoted\n\nlast line https://example.com/page\n"));
    return 0;
}

static async Task<int> Sweep(string[] a)
{
    string outDir = a[0];
    int per = 100, keep = 30;
    bool allow = false;
    var paths = new List<string>();
    for (int i = 1; i < a.Length; i++)
    {
        switch (a[i])
        {
            case "--per": per = int.Parse(a[++i], CultureInfo.InvariantCulture); break;
            case "--keep": keep = int.Parse(a[++i], CultureInfo.InvariantCulture); break;
            case "--allow-remote": allow = true; break;
            default: paths.Add(a[i]); break;
        }
    }

    Directory.CreateDirectory(Path.Combine(outDir, "docs"));
    using var results = new StreamWriter(Path.Combine(outDir, "results.jsonl"), false, new UTF8Encoding(false));
    var options = new RenderOptions
    {
        MaxDuration = TimeSpan.FromSeconds(120),
        MaxPngPages = 3,
        Display = allow ? new DisplayOptions { Sanitizer = new SanitizerOptions { RemoteContent = RemoteContentAction.Allow } } : DisplayOptions.Default,
    };
    int id = 0;
    var totals = new Dictionary<string, int>();
    void Count(string k) => totals[k] = totals.GetValueOrDefault(k) + 1;

    foreach (string root in paths)
    {
        IEnumerable<string> files = Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            : [root];
        foreach (string file in files)
        {
            MailContainer? c = null;
            try
            {
                c = Mail.Open(file);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Count("unreadable-file");
                Console.WriteLine("unreadable: " + file);
                continue;
            }

            using (c)
            {
                int n = 0;
                IEnumerator<IMailItem> en;
                try
                {
                    en = c.Enumerate().GetEnumerator();
                }
                catch (Exception)
                {
                    Count("unreadable-file");
                    continue;
                }

                while (n < per)
                {
                    IMailItem item;
                    try
                    {
                        if (!en.MoveNext())
                        {
                            break;
                        }

                        item = en.Current;
                    }
                    catch (Exception)
                    {
                        Count("enumeration-error");
                        break;
                    }

                    n++;
                    id++;
                    string label = Path.GetFileName(file) + "#" + n;
                    await RenderOne(id, label, item, options, outDir, keep, results, Count);
                }
            }
        }
    }

    foreach (KeyValuePair<string, int> kv in totals.OrderBy(k => k.Key, StringComparer.Ordinal))
    {
        Console.WriteLine($"{kv.Key,-28} {kv.Value}");
    }

    Console.WriteLine($"items {id}; peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024)} MB");
    return 0;
}

static async Task RenderOne(int id, string label, IMailItem item, RenderOptions options, string outDir, int keep, StreamWriter results, Action<string> count)
{
    var rec = new System.Text.Json.Nodes.JsonObject { ["id"] = id, ["item"] = label };
    try
    {
        string html = MessageRenderer.BuildDocument(item, options, out DisplayResult display);
        string expected = ExpectedText(html);
        rec["htmlBytes"] = html.Length;
        rec["expectedChars"] = expected.Length;

        string pdfPath = Path.Combine(outDir, "docs", id.ToString(CultureInfo.InvariantCulture) + ".pdf");
        bool kept = id <= keep;
        string tmp = kept ? pdfPath : Path.Combine(outDir, "tmp.pdf");
        var sw = Stopwatch.StartNew();
        long before = GC.GetAllocatedBytesForCurrentThread();
        RenderResult pdf;
        await using (var fs = File.Create(tmp))
        {
            pdf = await item.ToPdfAsync(fs, options);
        }

        rec["pdfMs"] = sw.ElapsedMilliseconds;
        rec["pages"] = pdf.PageCount;
        rec["pdfBytes"] = new FileInfo(tmp).Length;
        rec["unserved"] = pdf.UnservedRequests.Count;
        rec["defects"] = string.Join(",", pdf.Defects.Select(d => d.Kind).Distinct());
        rec["diagnostics"] = pdf.Diagnostics.Count;
        count("pdf-ok");
        if (pdf.UnservedRequests.Count > 0)
        {
            count("pdf-with-unserved-requests");
            rec["unservedFirst"] = pdf.UnservedRequests[0];
        }

        string got = PdfText(tmp);
        Compare(expected, got, rec, count);

        sw.Restart();
        long pngBytes = 0;
        int pngPages = 0;
        RenderResult png = await item.ToPngAsync(p => kept && p == 1
            ? File.Create(Path.Combine(outDir, "docs", id.ToString(CultureInfo.InvariantCulture) + ".png"))
            : new CountingStream(l => { pngBytes += l; pngPages = Math.Max(pngPages, p); }), options);
        if (kept)
        {
            pngBytes = new FileInfo(Path.Combine(outDir, "docs", id.ToString(CultureInfo.InvariantCulture) + ".png")).Length;
        }
        rec["pngMs"] = sw.ElapsedMilliseconds;
        rec["pngPages"] = png.PagesWritten;
        rec["pngBytes"] = pngBytes;
        count("png-ok");
        if (expected.Length >= 40 && pngBytes < 1500 * png.PagesWritten)
        {
            count("png-suspiciously-small");
        }

        if (kept)
        {
            File.WriteAllText(Path.Combine(outDir, "docs", id.ToString(CultureInfo.InvariantCulture) + ".html"), html);
        }
    }
    catch (Exception ex)
    {
        rec["exception"] = ex.GetType().Name + ": " + ex.Message;
        count(ex is MailRenderException m ? "limit-" + m.Failure : "EXCEPTION");
    }

    results.WriteLine(rec.ToJsonString());
}

static void Compare(string expected, string got, System.Text.Json.Nodes.JsonObject rec, Action<string> count)
{
    string e = Squeeze(expected), g = Squeeze(got);
    if (e == g)
    {
        rec["text"] = "equal";
        count("text-equal");
        return;
    }

    // The same characters in a different order are a reading-order difference; anything else is listed with the first mismatch.
    var bag = new Dictionary<int, int>();
    foreach (Rune r in e.EnumerateRunes())
    {
        bag[r.Value] = bag.GetValueOrDefault(r.Value) + 1;
    }

    foreach (Rune r in g.EnumerateRunes())
    {
        bag[r.Value] = bag.GetValueOrDefault(r.Value) - 1;
    }

    string missing = string.Concat(bag.Where(kv => kv.Value > 0).OrderBy(kv => kv.Key).Select(kv => new string(char.ConvertFromUtf32(kv.Key)[0], 1) + "x" + kv.Value));
    string extra = string.Concat(bag.Where(kv => kv.Value < 0).OrderBy(kv => kv.Key).Select(kv => new string(char.ConvertFromUtf32(kv.Key)[0], 1) + "x" + -kv.Value));
    int i = 0;
    while (i < e.Length && i < g.Length && e[i] == g[i])
    {
        i++;
    }

    rec["missing"] = missing.Length > 200 ? missing[..200] : missing;
    rec["extra"] = extra.Length > 200 ? extra[..200] : extra;
    rec["firstDiff"] = i;
    rec["expectedAt"] = e.Substring(Math.Max(0, i - 20), Math.Min(60, e.Length - Math.Max(0, i - 20)));
    rec["gotAt"] = g.Substring(Math.Max(0, i - 20), Math.Min(60, g.Length - Math.Max(0, i - 20)));
    if (missing.Length == 0 && extra.Length == 0)
    {
        rec["text"] = "reordered";
        count("text-reordered");
    }
    else if (missing.Length == 0)
    {
        rec["text"] = "extra-only";
        count("text-extra-only");
    }
    else
    {
        rec["text"] = "missing";
        count("text-missing");
    }
}

// Whitespace, soft hyphens and zero-width marks do not count; compatibility forms (ligatures) are folded.
static string Squeeze(string s)
{
    var sb = new StringBuilder(s.Length);
    foreach (char ch in s.Normalize(NormalizationForm.FormKC))
    {
        if (!char.IsWhiteSpace(ch) && ch != '\u00AD' && ch != '\u200B' && ch != '\u200C' && ch != '\u200D' && ch != '\u200E' && ch != '\u200F' && ch != '\uFEFF' && ch != '\u2060' && (ch < '\u202A' || ch > '\u202E') && (ch < '\u2066' || ch > '\u2069') && ch != '\f')
        {
            sb.Append(ch);
        }
    }

    return sb.ToString();
}

static string PdfText(string path)
{
    var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("PDFTOTEXT") ?? "pdftotext", ["-raw", "-enc", "UTF-8", path, "-"])
    {
        RedirectStandardOutput = true,
        StandardOutputEncoding = Encoding.UTF8,
    };
    using Process p = Process.Start(psi)!;
    string text = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    return text;
}

// What a browser would show: text nodes outside style/script/title/head, skipping elements hidden by inline style or attribute.
static string ExpectedText(string html)
{
    Document doc = Document.Parse(html);
    var sb = new StringBuilder();
    Walk(doc.DocumentElement, sb, 0);
    return sb.ToString();
}

static void Walk(Node n, StringBuilder sb, int depth)
{
    if (depth > 5000)
    {
        return;
    }

    for (Node c = n.FirstChild; !c.IsNull; c = c.NextSibling)
    {
        if (c.IsElement)
        {
            string name = c.LocalName;
            if (name is "style" or "script" or "title" or "head" or "template" or "noscript")
            {
                continue;
            }

            string? style = c.GetAttribute("style");
            if (c.GetAttribute("hidden") is not null || (style is not null && style.Replace(" ", "", StringComparison.Ordinal).Contains("display:none", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            sb.Append(' ');
            Walk(c, sb, depth + 1);
            sb.Append(' ');
        }
        else if (c.Data is { } d)
        {
            sb.Append(d);
        }
    }
}

sealed class CountingStream(Action<long> onClose) : Stream
{
    private long _len;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _len;
    public override long Position { get => _len; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count)
    {
        _len += count;
        onClose(count);
    }
}
