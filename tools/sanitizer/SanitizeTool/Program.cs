using System.Text;
using RecursiveAI.OneHtml;
using RecursiveAI.OneHtml.Dom;
using RecursiveAI.OneMail;
using RecursiveAI.OneMail.Display;

// SanitizeTool show                         sanitise stdin, print the output and the report
// SanitizeTool pages VECTORS OUTDIR HOST    write page-sanitized.html (policy block), page-allow.html and page-raw.html (the unsanitised control)
// SanitizeTool corpus PATH...               sanitise the HTML bodies of .html/.htm/.eml files (directories are walked): text kept, output stable
// SanitizeTool display FILE                 display HTML of one message
return args.Length == 0 ? Usage() : args[0] switch
{
    "show" => Show(),
    "pages" when args.Length >= 4 => Pages(args[1], args[2], args[3]),
    "corpus" when args.Length >= 2 => Corpus(args[1..]),
    "display" when args.Length >= 2 => DisplayOne(args[1]),
    "json" when args.Length >= 4 => Json(args[1], args[2], args[3]),
    "seeds" when args.Length >= 3 => Seeds(args[1], args[2]),
    _ => Usage(),
};

static int Json(string vectors, string outFile, string host)
{
    // [{name, raw, ours}]: ours is this sanitiser's output with the default policy; compare-dompurify.sh adds the other sanitiser's.
    System.Text.Json.Nodes.JsonArray arr = [];
    foreach ((string name, string html) in ReadVectors(vectors, host))
    {
        arr.Add(new System.Text.Json.Nodes.JsonObject { ["name"] = name, ["raw"] = html, ["ours"] = HtmlSanitizer.Sanitize(html).Html });
    }

    File.WriteAllText(outFile, arr.ToJsonString());
    return 0;
}

static int Seeds(string vectors, string outDir)
{
    Directory.CreateDirectory(outDir);
    int i = 0;
    foreach ((string name, string html) in ReadVectors(vectors, "leak.invalid"))
    {
        // Byte 0 picks the options of the fuzz targets; each vector is written under two settings.
        File.WriteAllBytes(Path.Combine(outDir, $"{i:D3}-{name}-block"), [(byte)4, .. Encoding.UTF8.GetBytes(html)]);
        File.WriteAllBytes(Path.Combine(outDir, $"{i++:D3}-{name}-allow"), [(byte)0, .. Encoding.UTF8.GetBytes(html)]);
    }

    return 0;
}

static int Usage()
{
    Console.Error.WriteLine("usage: SanitizeTool show | pages VECTORS OUTDIR HOST | corpus PATH... | display FILE");
    return 2;
}

static int Show()
{
    string html = Console.In.ReadToEnd();
    SanitizerOptions? opts = Environment.GetEnvironmentVariable("SANITIZE_ALLOW") == "1" ? new SanitizerOptions { RemoteContent = RemoteContentAction.Allow } : null;
    SanitizedHtml r = HtmlSanitizer.Sanitize(html, opts);
    Console.WriteLine(r.Html);
    foreach (SanitizerChange c in r.Changes)
    {
        Console.Error.WriteLine($"change {c.Kind} {c.Detail}");
    }

    foreach (RemoteReference x in r.RemoteReferences)
    {
        Console.Error.WriteLine($"remote {x.Kind} {x.Outcome} {x.Source} {x.Url}");
    }

    foreach (TrackingIndication t in r.TrackingIndications)
    {
        Console.Error.WriteLine($"tracking {t.Reasons} {t.Url}");
    }

    return 0;
}

static List<(string Name, string Html)> ReadVectors(string path, string host)
{
    List<(string, string)> list = [];
    string name = "";
    StringBuilder body = new();
    bool have = false;
    foreach (string line in File.ReadAllLines(path))
    {
        if (line == "====")
        {
            if (have)
            {
                list.Add((name, body.ToString().Replace("{HOST}", host, StringComparison.Ordinal)));
            }

            have = false;
            body.Clear();
        }
        else if (!have && line.StartsWith("# ", StringComparison.Ordinal) && line[2..].All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            name = line[2..];
            have = true;
        }
        else if (have)
        {
            body.Append(line).Append('\n');
        }
    }

    if (have)
    {
        list.Add((name, body.ToString().Replace("{HOST}", host, StringComparison.Ordinal)));
    }

    return list;
}

static int Pages(string vectors, string outDir, string host)
{
    Directory.CreateDirectory(outDir);
    List<(string Name, string Html)> vs = ReadVectors(vectors, host);
    // Every vector gets its own frame, so a vector that swallows the rest of its document (plaintext, frameset, a refresh) cannot hide the next one.
    // The frame's alert/confirm/prompt/write stand-ins report to the page, which prints the count; the frame's fragment is parsed by the browser's
    // document parser exactly as it would be in a mail view.
    const string Head = "<!doctype html><html><head><meta charset=\"utf-8\"><title>t</title><script>window.__fired=[];</script></head><body>";
    const string Tail = "<pre id=\"result\">pending</pre><script>setTimeout(function(){document.getElementById('result').textContent="
        + "'FIRED='+window.__fired.length+' '+JSON.stringify(window.__fired.slice(0,40));},4000);</script></body></html>";
    const string FrameHead = "<!doctype html><html><head><meta charset=\"utf-8\"><script>(function(){var n=NAME;var f=function(k){return function(x){parent.__fired.push(n+':'+k+':'+String(x));};};"
        + "window.alert=f('alert');window.confirm=f('confirm');window.prompt=f('prompt');window.write=f('write');window.msgbox=f('msgbox');})();</script></head><body>";
    foreach ((string file, Func<string, string> render) in new (string, Func<string, string>)[]
    {
        ("page-sanitized.html", h => HtmlSanitizer.Sanitize(h).Html),
        ("page-allow.html", h => HtmlSanitizer.Sanitize(h, new SanitizerOptions { RemoteContent = RemoteContentAction.Allow }).Html),
        ("page-raw.html", h => h),
    })
    {
        // The raw control is written in pieces of 20 frames: some raw vectors (media, refresh) keep a browser busy for a long time.
        int piece = 0;
        StringBuilder sb = new(Head);
        int i = 0;
        foreach ((string name, string html) in vs)
        {
            if (file == "page-raw.html" && i > 0 && i % 20 == 0)
            {
                sb.Append(Tail);
                File.WriteAllText(Path.Combine(outDir, $"page-raw-{piece++:D2}.html"), sb.ToString());
                sb = new StringBuilder(Head);
            }

            string doc = FrameHead.Replace("NAME", "'" + name + "'", StringComparison.Ordinal) + render(html) + "</body></html>";
            sb.Append("<iframe data-i=\"").Append(i++).Append("\" width=\"200\" height=\"40\" srcdoc=\"")
                .Append(doc.Replace("&", "&amp;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal)).Append("\"></iframe>\n");
        }

        sb.Append(Tail);
        File.WriteAllText(Path.Combine(outDir, file == "page-raw.html" ? $"page-raw-{piece:D2}.html" : file), sb.ToString());
    }

    Console.WriteLine($"vectors: {vs.Count}");
    return 0;
}

static IEnumerable<string> Files(IEnumerable<string> roots)
{
    foreach (string r in roots)
    {
        if (Directory.Exists(r))
        {
            foreach (string f in Directory.EnumerateFiles(r, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                yield return f;
            }
        }
        else
        {
            yield return r;
        }
    }
}

static IEnumerable<string> HtmlsOf(string file)
{
    string ext = Path.GetExtension(file).ToLowerInvariant();
    if (ext is ".html" or ".htm")
    {
        yield return File.ReadAllText(file);
        yield break;
    }

    MailContainer? c = null;
    try
    {
        c = Mail.Open(file);
    }
    catch (Exception)
    {
    }

    if (c is null)
    {
        yield break;
    }

    using (c)
    {
        foreach (IMailItem item in c.Enumerate())
        {
            string? h = null;
            try
            {
                h = item.HtmlBody?.ReadText();
            }
            catch (Exception)
            {
            }

            if (!string.IsNullOrEmpty(h))
            {
                yield return h;
            }
        }
    }
}

static string VisibleText(string html, bool normalizeOnly)
{
    Document doc = Document.Parse(html, new ParseOptions { ScriptingEnabled = true });
    StringBuilder sb = new();
    Walk(doc.Root, sb);
    return string.Join(' ', sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    static void Walk(Node n, StringBuilder sb)
    {
        for (Node c = n.FirstChild; !c.IsNull; c = c.NextSibling)
        {
            if (c.Kind == NodeKind.Text)
            {
                sb.Append(c.Data);
            }
            else if (c.IsElement && c.Namespace == ElementNamespace.Html && !D.Dropped.Contains(c.LocalName))
            {
                // Inline-level breaks do not matter: words are compared, not layout.
                sb.Append(' ');
                Walk(c, sb);
                sb.Append(' ');
            }
        }
    }
}

static int Corpus(string[] roots)
{
    int files = 0, withHtml = 0, same = 0, differ = 0, unstable = 0, tracking = 0, remote = 0;
    long inChars = 0;
    List<string> diffs = [];
    foreach (string f in Files(roots))
    {
        files++;
        foreach (string html in HtmlsOf(f))
        {
        withHtml++;
        inChars += html.Length;
        SanitizedHtml r = HtmlSanitizer.Sanitize(html);
        if (r.Changes.Any(c => c.Detail.Contains("unstable", StringComparison.Ordinal)))
        {
            unstable++;
        }

        remote += r.RemoteReferences.Count(x => x.Kind != RemoteContentKind.Link);
        tracking += r.TrackingIndications.Count;
        string want = VisibleText(html, true);
        string got = VisibleText(r.Html, true);
        if (r.Truncated || want == got)
        {
            same++;
        }
        else
        {
            differ++;
            if (diffs.Count < 20)
            {
                diffs.Add(f);
            }
        }
        }
    }

    Console.WriteLine($"files={files} withHtml={withHtml} textPreserved={same} textDiffers={differ} unstable={unstable} remoteRefs={remote} trackingIndications={tracking} inputChars={inChars}");
    foreach (string d in diffs)
    {
        Console.WriteLine("differs: " + d);
    }

    return 0;
}

static int DisplayOne(string file)
{
    IMailItem m = Mail.Parse(File.ReadAllBytes(file));
    DisplayResult r = m.ToDisplayHtml();
    Console.WriteLine(r.Html);
    Console.Error.WriteLine($"remote={r.RemoteReferences.Count} tracking={r.TrackingIndications.Count} resolved={r.ResolvedAttachments.Count} unresolved={r.UnresolvedReferences.Count}");
    return 0;
}

internal static class D
{
    public static readonly HashSet<string> Dropped = new(StringComparer.Ordinal)
    {
        "script", "style", "noscript", "template", "iframe", "object", "embed", "applet", "frame", "frameset", "noframes", "noembed", "title", "xmp",
        "plaintext", "textarea", "select", "datalist", "audio", "video", "track", "canvas", "dialog", "meta", "link", "base", "basefont", "param",
        "bgsound", "keygen", "map", "area", "input", "svg", "math", "listing", "head",
    };
    
}
