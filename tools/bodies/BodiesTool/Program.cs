using System.Text;
using RecursiveAI.OneHtml;
using RecursiveAI.OneHtml.Css;
using RecursiveAI.OneMail;
using RecursiveAI.OneMail.Bodies;

// Developer tool for the body checks (tools/bodies/README.md). Nothing in src/ references it.
//   html <out-dir> <prefix> <max> <stride> <path>...   write the HTML bodies of the items under each path as UTF-8 files
//   text <dir> [width]                                 convert every .html in <dir> to .ours.txt
//   bodies <path>...                                   best-body choices over the items under each path (one tsv row per item class)
//   hidden <dir>                                       write <name>.hidden.txt: the text of elements a screen would hide (for explaining oracle differences)
//   nobody <path> <max>                                list items that have no body, with what they do hold
//   alloc <dir>                                        time and allocation per KB of HTML for HtmlToText
return args.Length == 0 ? Usage() : args[0] switch
{
    "html" => ExtractHtml(args),
    "text" => ConvertAll(args),
    "bodies" => BodyStats(args),
    "hidden" => Hidden(args),
    "nobody" => NoBody(args),
    "alloc" => Alloc(args),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("usage: BodiesTool html|text|bodies|alloc ...");
    return 2;
}

// Items are lazy views over the open container, so they are used inside the callback; false from it stops the walk.
static void Each(string path, Func<IMailItem, bool> visit)
{
    try
    {
        using MailContainer c = Mail.Open(path);
        foreach (IMailItem item in c.Enumerate())
        {
            if (!visit(item))
            {
                return;
            }
        }
    }
    catch (Exception e) when (e is IOException or NotSupportedException or InvalidDataException)
    {
        Console.Error.WriteLine("skipped " + path + ": " + e.GetType().Name);
    }
}

static int ExtractHtml(string[] a)
{
    string dir = a[1];
    string prefix = a[2];
    int max = int.Parse(a[3]);
    int stride = Math.Max(int.Parse(a[4]), 1);
    Directory.CreateDirectory(dir);
    int n = 0;
    long seen = 0;
    using StreamWriter index = new(Path.Combine(dir, prefix + "index.tsv"), append: true);
    bool done = false;
    foreach (string path in a.Skip(5))
    {
        foreach (string file in Files(path))
        {
            if (done)
            {
                return 0;
            }

            Each(file, item =>
            {
                if (n >= max)
                {
                    done = true;
                    return false;
                }

                MailBody? h = item.HtmlBody;
                if (h is null || seen++ % stride != 0)
                {
                    return true;
                }

                string text = h.ReadText();
                string name = prefix + n.ToString("D5") + ".html";
                File.WriteAllText(Path.Combine(dir, name), text, new UTF8Encoding(false));
                index.WriteLine($"{name}\t{file}\t{item.Provenance.Index}\t{text.Length}");
                n++;
                return true;
            });
        }
    }

    return 0;
}

static IEnumerable<string> Files(string path) =>
    Directory.Exists(path) && !IsDirStore(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal) : new[] { path };

static bool IsDirStore(string p) => File.Exists(Path.Combine(p, "cur")) || Directory.Exists(Path.Combine(p, "cur")) || Directory.Exists(Path.Combine(p, "new"));

static int ConvertAll(string[] a)
{
    int width = a.Length > 2 ? int.Parse(a[2]) : 0;
    HtmlToTextOptions o = new() { LineWidth = width };
    long issues = 0;
    int count = 0;
    foreach (string f in Directory.EnumerateFiles(a[1], "*.html").Order(StringComparer.Ordinal))
    {
        BodyConversion c = HtmlToText.Convert(File.ReadAllText(f), o);
        File.WriteAllText(Path.ChangeExtension(f, ".ours.txt"), c.Text, new UTF8Encoding(false));
        issues += c.Issues.Count;
        count++;
    }

    Console.WriteLine($"converted {count} files, {issues} issues");
    return 0;
}

static int BodyStats(string[] a)
{
    SortedDictionary<string, int> rows = [];
    int items = 0;
    foreach (string path in a.Skip(1))
    {
        foreach (string file in Files(path))
        {
            Each(file, item =>
            {
                items++;
                SelectedBody s = BodySelector.Select(item);
                MailBody? t = BodySelector.GetText(item);
                MailBody? h = BodySelector.GetHtml(item);
                string flags = string.Join(',', s.Issues.Select(i => i.Kind.ToString()));
                string key = string.Join('\t', item.Provenance.Format, item.Kind, s.Kind, s.Reason, Deriv(s.Body), Deriv(t), Deriv(h), flags);
                rows[key] = rows.GetValueOrDefault(key) + 1;
                return true;
            });
        }
    }

    Console.WriteLine("format\tkind\tbest\treason\tbest-derivation\ttext-derivation\thtml-derivation\tissues\tcount");
    foreach ((string k, int v) in rows)
    {
        Console.WriteLine(k + "\t" + v);
    }

    Console.Error.WriteLine($"{items} items");
    return 0;
}

static string Deriv(MailBody? b) => b is null ? "-" : b is DerivedMailBody d ? d.Derivation.ToString() : "stored";

static int Alloc(string[] a)
{
    string[] files = Directory.EnumerateFiles(a[1], a.Length > 2 ? a[2] : "*.html").Where(f => !f.EndsWith(".oracle.html", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
    string[] docs = files.Select(File.ReadAllText).ToArray();
    long chars = docs.Sum(d => (long)d.Length);
    foreach (string d in docs.Take(20))
    {
        HtmlToText.Convert(d);
    }

    // Allocation only: deterministic, so it needs no quiet machine. (Timing belongs to the benchmark queue.)
    long before = GC.GetAllocatedBytesForCurrentThread();
    long outChars = 0;
    foreach (string d in docs)
    {
        outChars += HtmlToText.Convert(d).Text.Length;
    }

    long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
    Console.WriteLine($"{files.Length} docs, {chars / 1024.0:F0} KiChars of HTML in, {outChars / 1024.0:F0} KiChars of text out: {bytes / (chars / 1024.0):F0} B allocated per KiChar of HTML ({bytes / (double)files.Length:F0} B per document)");
    return 0;
}

// An independent reading of "hidden": the cascade's computed display and visibility, the hidden attribute, and mso-hide in a style attribute.
// The comparison script uses the text of these elements, plus script, style and title, to explain what other programs print and OneMail does not.
static int Hidden(string[] a)
{
    foreach (string f in Directory.EnumerateFiles(a[1], "*.html").Order(StringComparer.Ordinal))
    {
        Document doc = Document.Parse(File.ReadAllText(f));
        DocumentStyles styles = DocumentStyles.Create(doc);
        StringBuilder sb = new();
        StringBuilder alt = new();
        StringBuilder skipped = new();
        foreach (Node n in doc.Root.DescendantElements)
        {
            string tag = n.LocalName;
            if (tag == "img" && n.GetAttribute("alt") is { } altText)
            {
                alt.Append(altText).Append('\n');
            }

            if (tag == "input" && (n.GetAttribute("value") ?? n.GetAttribute("alt")) is { } controlText)
            {
                skipped.Append(controlText).Append('\n');
            }

            if (tag is "head" or "script" or "style" or "title" or "select" or "datalist" or "textarea" or "object" or "iframe" or "embed" or "audio" or "video" or "canvas" or "svg" or "math" or "rp" or "template")
            {
                skipped.Append(n.TextContent).Append('\n');
            }

            bool hide = false;
            if (!hide)
            {
                ComputedStyle cs = styles.GetStyle(n);
                string style = n.GetAttribute("style") ?? string.Empty;
                hide = n.HasAttribute("hidden") || cs.GetPropertyValue("display") == "none" || cs.GetPropertyValue("visibility") is "hidden" or "collapse"
                    || style.Replace(" ", string.Empty, StringComparison.Ordinal).Contains("mso-hide:all", StringComparison.OrdinalIgnoreCase);
            }

            if (hide)
            {
                sb.Append(n.TextContent).Append('\n');
            }
        }

        File.WriteAllText(Path.ChangeExtension(f, ".hidden.txt"), sb.ToString(), new UTF8Encoding(false));
        File.WriteAllText(Path.ChangeExtension(f, ".alt.txt"), alt.ToString(), new UTF8Encoding(false));
        File.WriteAllText(Path.ChangeExtension(f, ".skipped.txt"), skipped.ToString(), new UTF8Encoding(false));
    }

    return 0;
}

static int NoBody(string[] a)
{
    int shown = 0;
    int max = int.Parse(a[2]);
    foreach (string file in Files(a[1]))
    {
        Each(file, item =>
        {
            if (BodySelector.Select(item).Kind != BodyKind.None)
            {
                return true;
            }

            string types = string.Join(',', item.Attachments.Select(x => x.ContentType).Distinct());
            string mime = item is MimeMailItem m ? m.Entity.MediaType + " " + m.Entity.GetType().Name : item.GetType().Name;
            Console.WriteLine($"{item.Provenance.Format}\t{item.Provenance.Index}\t{item.Kind}\tsubject={(item.Subject ?? "-")}\t{mime}\tattachments=[{types}]\tdefects={string.Join(',', item.Defects.Select(d => d.Code).Distinct().Take(4))}");
            return ++shown < max;
        });
        if (shown >= max)
        {
            break;
        }
    }

    return 0;
}
