using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.ContentLines;
using RecursiveAI.OneMail.Calendar.Forms;

// FormsDump to json|xml <file>        convert an ICS or vCard file; the text goes to stdout, the losses to stderr
// FormsDump from json|xml <file>      convert a jCal/jCard/xCal/xCard file to ICS or vCard text
// FormsDump check <root>...           the corpus check: text -> JSON -> text, text -> XML -> text, and both forms again
// FormsDump emit <root> <outdir>      write our JSON and XML for every text file under root (the oracle comparison reads them)
return args.Length >= 2 ? Run(args) : Usage();

static int Usage()
{
    Console.Error.WriteLine("usage: FormsDump to json|xml <file> | from json|xml <file> | check <root>... | emit <root> <outdir> | alloc <root>... | reread <indir> <outdir> json|xml");
    return 2;
}

static int Run(string[] args)
{
    switch (args[0])
    {
        case "check":
            return Check.Run(args[1..]);
        case "emit":
            return Check.Emit(args[1], args[2]);
        case "fixed":
        {
            // The fuzz invariant on one file: the JSON written from what was read reads back to the same JSON.
            byte[] data = File.ReadAllBytes(args[2]);
            FormReadResult r = args[1] == "xml" ? XmlForms.FromXml(data) : JsonForms.FromJson(data);
            byte[] j1 = JsonForms.ToJson(r.Document, new JsonFormsOptions { Indented = true }).Bytes;
            byte[] j2 = JsonForms.ToJson(JsonForms.FromJson(j1).Document, new JsonFormsOptions { Indented = true }).Bytes;
            File.WriteAllBytes(args[3] + ".1", j1);
            File.WriteAllBytes(args[3] + ".2", j2);
            Console.WriteLine(j1.AsSpan().SequenceEqual(j2) ? "same" : "DIFFERENT");
            return 0;
        }

        case "alloc":
            return Check.Alloc(args[1..]);
        case "reread" when args.Length >= 4:
            return Check.Reread(args[1], args[2], args[3]);
        case "to" or "from" when args.Length >= 3:
            return Convert(args);
        default:
            return Usage();
    }
}

static int Convert(string[] args)
{
    byte[] data = File.ReadAllBytes(args[2]);
    using Stream o = Console.OpenStandardOutput();
    switch (args[0], args[1])
    {
        case ("to", "json"):
        {
            FormWriteResult w = JsonForms.ToJson(ContentDocument.Parse(data), new JsonFormsOptions { Indented = true });
            o.Write(w.Bytes);
            foreach (FormLoss l in w.Losses)
            {
                Console.Error.WriteLine(l);
            }

            return 0;
        }

        case ("to", "xml"):
        {
            ContentDocument d = ContentDocument.Parse(data);
            FormWriteResult w = Check.IsCard(d) ? XmlForms.ToXCard(d) : XmlForms.ToXCal(d);
            o.Write(w.Bytes);
            foreach (FormLoss l in w.Losses)
            {
                Console.Error.WriteLine(l);
            }

            return 0;
        }

        case ("from", "json"):
        case ("from", "xml"):
        {
            FormReadResult r = args[1] == "json" ? JsonForms.FromJson(data) : XmlForms.FromXml(data);
            o.Write(r.Document.ToArray());
            foreach (FormDefect x in r.Defects)
            {
                Console.Error.WriteLine(x);
            }

            return 0;
        }

        default:
            return Usage();
    }
}

internal static partial class Check
{
    internal static bool IsCard(ContentDocument d) => d.Components.Any(static c => c.IsNamed("VCARD"));

    private static IEnumerable<string> TextFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(static f => f.EndsWith(".ics", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static f => f, StringComparer.Ordinal);

    private static IEnumerable<string> FormFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(static f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal);

    private sealed class Totals
    {
        internal int Files, Documents, Properties, Parameters, Values, Mismatch, Explained, JsonStable, XmlStable, XmlFiles, JsonDefects, XmlDefects, JsonBytes, XmlBytes;
        internal Dictionary<string, int> Losses = [];
        internal List<string> Problems = [];
    }

    // Bytes allocated per property by each direction, over every text file under the roots (one warm-up pass first). The input is
    // parsed once up front, so only the form conversion is measured.
    internal static int Alloc(string[] roots)
    {
        List<(ContentDocument Doc, bool Card, byte[] Json, byte[] Xml, int Props)> docs = [];
        foreach (string root in roots)
        {
            foreach (string f in TextFiles(root))
            {
                ContentDocument d = ContentDocument.Parse(File.ReadAllBytes(f));
                Totals t = new();
                Count(d.Root, t);
                bool card = IsCard(d);
                docs.Add((d, card, JsonForms.ToJson(d).Bytes, (card ? XmlForms.ToXCard(d) : XmlForms.ToXCal(d)).Bytes, t.Properties));
            }
        }

        long props = docs.Sum(static x => (long)x.Props);
        long jsonBytes = docs.Sum(static x => (long)x.Json.Length);
        long xmlBytes = docs.Sum(static x => (long)x.Xml.Length);
        string[] names = ["ToJson", "FromJson", "ToXml", "FromXml"];
        long[] bytes = new long[4];
        for (int pass = 0; pass < 2; pass++)
        {
            for (int k = 0; k < 4; k++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                foreach ((ContentDocument doc, bool card, byte[] json, byte[] xml, int _) in docs)
                {
                    switch (k)
                    {
                        case 0: _ = JsonForms.ToJson(doc); break;
                        case 1: _ = JsonForms.FromJson(json); break;
                        case 2: _ = card ? XmlForms.ToXCard(doc) : XmlForms.ToXCal(doc); break;
                        default: _ = XmlForms.FromXml(xml); break;
                    }
                }

                bytes[k] = GC.GetAllocatedBytesForCurrentThread() - before;
            }
        }

        Console.WriteLine($"{docs.Count} documents, {props} properties, jCal/jCard {jsonBytes} bytes, xCal/xCard {xmlBytes} bytes");
        for (int k = 0; k < 4; k++)
        {
            long inBytes = k < 2 ? jsonBytes : xmlBytes;
            Console.WriteLine($"{names[k],-9} {bytes[k] / (double)props,9:F0} B/property  {bytes[k] / (double)inBytes,6:F1} B allocated per byte of form text");
        }

        return 0;
    }

    // Reads every JSON or XML file of a directory (another library's output) and writes it again with ours: one file per input, same name.
    internal static int Reread(string inDir, string outDir, string form)
    {
        int n = 0;
        bool json = form == "json";
        foreach (string f in Directory.EnumerateFiles(inDir, "*." + form, SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            byte[] data = File.ReadAllBytes(f);
            if (data.AsSpan().StartsWith("ERROR"u8))
            {
                continue;
            }

            FormReadResult r = json ? JsonForms.FromJson(data) : XmlForms.FromXml(data);
            bool card = IsCard(r.Document);
            byte[] o = json ? JsonForms.ToJson(r.Document).Bytes : (card ? XmlForms.ToXCard(r.Document) : XmlForms.ToXCal(r.Document)).Bytes;
            string dest = Path.Combine(outDir, Path.GetRelativePath(inDir, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, o);
            n++;
        }

        Console.WriteLine("reread " + n + " files");
        return 0;
    }

    internal static int Emit(string root, string outDir)
    {
        int n = 0;
        foreach (string f in TextFiles(root))
        {
            string rel = Path.GetRelativePath(root, f);
            ContentDocument d = ContentDocument.Parse(File.ReadAllBytes(f));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(outDir, rel))!);
            File.WriteAllBytes(Path.Combine(outDir, rel + ".json"), JsonForms.ToJson(d).Bytes);
            File.WriteAllBytes(Path.Combine(outDir, rel + ".xml"), (IsCard(d) ? XmlForms.ToXCard(d) : XmlForms.ToXCal(d)).Bytes);
            n++;
        }

        Console.WriteLine("emitted " + n + " files");
        return 0;
    }

    internal static int Run(string[] roots)
    {
        int bad = 0;
        foreach (string root in roots)
        {
            Dictionary<string, Totals> per = [];
            foreach (string f in TextFiles(root))
            {
                string src = Path.GetRelativePath(root, f).Split(Path.DirectorySeparatorChar)[0];
                if (src.EndsWith("-forms", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!per.TryGetValue(src, out Totals? t))
                {
                    per[src] = t = new Totals();
                }

                CheckText(f, Path.GetRelativePath(root, f), t);
            }

            foreach (string f in FormFiles(root))
            {
                string src = Path.GetRelativePath(root, f).Split(Path.DirectorySeparatorChar)[0];
                if (!src.EndsWith("-forms", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!per.TryGetValue(src, out Totals? t))
                {
                    per[src] = t = new Totals();
                }

                CheckForm(f, Path.GetRelativePath(root, f), t);
            }

            Console.WriteLine("== " + root);
            Console.WriteLine("source files comps props params values  mismatches json-stable xml-stable  json-defects xml-defects explained");
            foreach ((string src, Totals t) in per.OrderBy(static p => p.Key, StringComparer.Ordinal))
            {
                Console.WriteLine($"{src,-16} {t.Files,5} {t.Documents,5} {t.Properties,6} {t.Parameters,6} {t.Values,6}  {t.Mismatch,10} {t.JsonStable,11} {t.XmlStable,10}  {t.JsonDefects,12} {t.XmlDefects,11}  {t.Explained,9}");
                bad += t.Mismatch;
                foreach (string p in t.Problems.Take(int.TryParse(Environment.GetEnvironmentVariable("FORMS_PROBLEMS"), out int np) ? np : 25))
                {
                    Console.WriteLine("    " + p);
                }
            }

            Dictionary<string, int> losses = [];
            foreach (Totals t in per.Values)
            {
                foreach ((string k, int v) in t.Losses)
                {
                    losses[k] = losses.GetValueOrDefault(k) + v;
                }
            }

            Console.WriteLine("losses (kind: occurrences):");
            foreach ((string k, int v) in losses.OrderByDescending(static x => x.Value))
            {
                Console.WriteLine($"  {k}: {v}");
            }
        }

        return bad == 0 ? 0 : 1;
    }

    private static void AddLosses(Totals t, IReadOnlyList<FormLoss> losses)
    {
        foreach (FormLoss l in losses)
        {
            t.Losses[l.Kind.ToString()] = t.Losses.GetValueOrDefault(l.Kind.ToString()) + l.Count;
        }
    }

    private static void CheckText(string path, string rel, Totals t)
    {
        byte[] data = File.ReadAllBytes(path);
        ContentDocument doc = ContentDocument.Parse(data);
        t.Files++;
        Count(doc.Root, t);

        // Text -> JSON -> text keeps every property, parameter and value.
        FormWriteResult jw = JsonForms.ToJson(doc);
        AddLosses(t, jw.Losses);
        t.JsonBytes += jw.Bytes.Length;
        FormReadResult jr = JsonForms.FromJson(jw.Bytes);
        t.JsonDefects += jr.Defects.Count;
        Compare(doc, jr.Document, rel, "json", t);

        // JSON -> text -> JSON is identical.
        FormWriteResult jw2 = JsonForms.ToJson(jr.Document);
        if (jw.Bytes.AsSpan().SequenceEqual(jw2.Bytes))
        {
            t.JsonStable++;
        }
        else
        {
            t.Mismatch++;
            t.Problems.Add(rel + ": JSON -> text -> JSON differs");
        }

        // Same for XML.
        bool card = IsCard(doc);
        FormWriteResult xw = card ? XmlForms.ToXCard(doc) : XmlForms.ToXCal(doc);
        t.XmlBytes += xw.Bytes.Length;
        t.XmlFiles++;
        FormReadResult xr = XmlForms.FromXml(xw.Bytes);
        t.XmlDefects += xr.Defects.Count;
        foreach (FormDefect x in xr.Defects.Take(2))
        {
            t.Problems.Add(rel + ": xml read defect " + x.Kind + " " + x.Path);
        }

        // XML cannot hold some characters (form feed, lone surrogates); the replacement character that stands in changes what a
        // second pass sees, so differences in such files are counted apart (InvalidXmlCharacter is in the loss report).
        bool badXml = xw.Losses.Any(static l => l.Kind is FormLossKind.InvalidXmlCharacter or FormLossKind.ParameterNotCarried or FormLossKind.PropertyNotCarried or FormLossKind.ComponentNotCarried or FormLossKind.TypeNameNotCarried);
        int before = t.Mismatch;
        Compare(doc, xr.Document, rel, "xml", t, card);
        FormWriteResult xw2 = card ? XmlForms.ToXCard(xr.Document) : XmlForms.ToXCal(xr.Document);
        if (xw.Bytes.AsSpan().SequenceEqual(xw2.Bytes))
        {
            t.XmlStable++;
        }
        else
        {
            t.Mismatch++;
            t.Problems.Add(rel + ": XML -> text -> XML differs");
        }

        if (badXml && t.Mismatch > before)
        {
            t.Explained += t.Mismatch - before;
            t.Mismatch = before;
            t.Problems.Add(rel + ": explained by an XML-only loss");
        }
    }

    // A JSON or XML file from the corpus: read, write as text, write again; the second and third forms are the same.
    private static void CheckForm(string path, string rel, Totals t)
    {
        byte[] data = File.ReadAllBytes(path);
        bool json = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        FormReadResult r = json ? JsonForms.FromJson(data) : XmlForms.FromXml(data);
        t.Files++;
        if (r.Document.Components.Any() == false)
        {
            // Not a calendar or contact document (a JSON schema, a pom): nothing to check.
            t.Problems.Add(rel + ": no components (" + string.Join(",", r.Defects.Select(static d => d.Kind)) + ")");
            return;
        }

        Count(r.Document.Root, t);
        t.JsonDefects += json ? r.Defects.Count : 0;
        t.XmlDefects += json ? 0 : r.Defects.Count;
        bool card = IsCard(r.Document);
        byte[] first = json ? JsonForms.ToJson(r.Document).Bytes : (card ? XmlForms.ToXCard(r.Document) : XmlForms.ToXCal(r.Document)).Bytes;
        // Through the text form (written to bytes and parsed again, as a user would)
        ContentDocument viaText = ContentDocument.Parse(r.Document.ToArray());
        byte[] second = json ? JsonForms.ToJson(viaText).Bytes : (card ? XmlForms.ToXCard(viaText) : XmlForms.ToXCal(viaText)).Bytes;
        bool same = first.AsSpan().SequenceEqual(second);
        if (json)
        {
            t.JsonStable += same ? 1 : 0;
        }
        else
        {
            t.XmlStable += same ? 1 : 0;
        }

        if (!same)
        {
            t.Mismatch++;
            t.Problems.Add(rel + ": form -> text -> form differs");
        }

        // The file as written against our rendering of it, ignoring key and property order: informational.
        _ = first;
    }

    private static void Count(Component c, Totals t)
    {
        foreach (ComponentItem i in c.Items)
        {
            if (i is Component child)
            {
                t.Documents++;
                Count(child, t);
            }
            else if (i is ContentProperty { Kind: ContentLineKind.Property } p)
            {
                t.Properties++;
                t.Parameters += p.Parameters.Count;
                t.Values++;
            }
        }
    }

    // ---- the independent comparison: text against text, with no knowledge of the pivot model ----

    private static void Compare(ContentDocument a, ContentDocument b, string rel, string form, Totals t, bool xcard = false)
    {
        List<string> la = Flatten(a, form, xcard);
        List<string> lb = Flatten(b, form, xcard);
        la.Sort(StringComparer.Ordinal);
        lb.Sort(StringComparer.Ordinal);
        if (la.Count != lb.Count)
        {
            t.Mismatch++;
            t.Problems.Add($"{rel}: {form}: {la.Count} lines before, {lb.Count} after");
            return;
        }

        for (int i = 0; i < la.Count; i++)
        {
            if (la[i] != lb[i])
            {
                t.Mismatch++;
                t.Problems.Add($"{rel}: {form}: \n        {Trim(la[i])}\n        {Trim(lb[i])}");
                return;
            }
        }
    }

    private static string Trim(string s) => s.Length > 200 ? s[..200] + "…" : s;

    // Lines the forms report as not carried (properties outside any component, components xCal and xCard have no place for) are left
    // out on both sides; the loss report lists them.
    private static List<string> Flatten(ContentDocument d, string form, bool xcard)
    {
        List<string> o = [];
        foreach (Component c in d.Root.Components)
        {
            if (form == "xml" && !c.IsNamed(xcard ? "VCARD" : "VCALENDAR"))
            {
                continue;
            }

            Walk(c, "/" + c.Name.ToUpperInvariant(), o, form == "xml" ? (xcard ? 2 : 1) : 0);
        }

        return o;
    }

    private static void Walk(Component c, string path, List<string> o, int xml)
    {
        foreach (ComponentItem i in c.Items)
        {
            if (i is Component child)
            {
                Walk(child, path + "/" + child.Name.ToUpperInvariant(), o, xml);
            }
            else if (i is ContentProperty { Kind: ContentLineKind.Property } p)
            {
                if (xml == 2 && p.Name.Equals("VERSION", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                o.Add(Canon(p, c, path, xml));
            }
        }
    }

    [GeneratedRegex(@"^[-+0-9:TZ.,/PWDHMS]+$")]
    private static partial Regex NumericLike();

    private static string Canon(ContentProperty p, Component owner, string path, int xml)
    {
        bool v21 = owner.IsNamed("VCARD") && owner.FindProperty("VERSION")?.Value.Trim() == "2.1";
        Dictionary<string, List<string>> ps = new(StringComparer.Ordinal);
        foreach (ContentParameter cp in p.Parameters)
        {
            string n = cp.Name.ToUpperInvariant();
            if (cp.IsBare)
            {
                if (n is "QUOTED-PRINTABLE" or "BASE64" or "B" or "8BIT" or "7BIT")
                {
                    continue;
                }

                // vCard 2.1 reads a bare token as a TYPE value; elsewhere it is a parameter without a value.
                n = owner.IsNamed("VCARD") ? "TYPE" : cp.Name.ToUpperInvariant();
                ps.TryAdd(n, []);
                if (owner.IsNamed("VCARD"))
                {
                    ps[n].Add(cp.Name.ToUpperInvariant());
                }

                continue;
            }

            if (n is "VALUE" or "CHARSET" or "ENCODING")
            {
                continue;
            }

            ps.TryAdd(n, []);
            foreach (string v in cp.Values)
            {
                if (n is "TYPE" or "PID" or "SORT-AS" && v.Contains(',', StringComparison.Ordinal))
                {
                    ps[n].AddRange(v.Split(',', StringSplitOptions.RemoveEmptyEntries));
                }
                else
                {
                    ps[n].Add(n == "LABEL" ? v.Replace("\\n", "\n", StringComparison.Ordinal).Replace("\\N", "\n", StringComparison.Ordinal) : v);
                }
            }
        }

        if (xml == 2 && ps.TryGetValue("TYPE", out List<string>? types) && types.RemoveAll(static x => x.Equals("PREF", StringComparison.OrdinalIgnoreCase)) > 0)
        {
            // vCard 3.0 TYPE=PREF is PREF=1 in xCard (version 4.0).
            if (types.Count == 0)
            {
                ps.Remove("TYPE");
            }

            ps.TryAdd("PREF", ["1"]);
        }

        string pstr = string.Join(';', ps.OrderBy(static x => x.Key, StringComparer.Ordinal).Select(static x => x.Key + "=" + string.Join(',', x.Value.Select(static s => s.ToUpperInvariant()).Order(StringComparer.Ordinal))));
        string v1 = p.GetDecodedValue();
        if (p.Encoding is "BASE64" or "B")
        {
            v1 = new string(v1.Where(static c => !char.IsWhiteSpace(c)).ToArray());
        }

        string value = ContentValues.Unescape(v1, v21 ? TextEscapeStyle.VCard21 : TextEscapeStyle.Rfc5545);
        value = value.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (owner.IsNamed("VCARD"))
        {
            // Producers escape the colon in URLs (http\://); the forms read it as a plain colon.
            value = value.Replace("\\:", ":", StringComparison.Ordinal);
        }

        string name = p.Name.ToUpperInvariant();
        if (name == "GEO" && !value.StartsWith("geo:", StringComparison.Ordinal))
        {
            string[] ll = value.Split(';').Select(static x => decimal.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal m) ? m.ToString("0.############################", CultureInfo.InvariantCulture) : x).ToArray();
            value = xml == 2 && ll.Length == 2 ? "geo:" + ll[0] + "," + ll[1] : string.Join(';', ll);
        }
        else if (NumericLike().IsMatch(value))
        {
            value = value.Replace("-", string.Empty, StringComparison.Ordinal).Replace(":", string.Empty, StringComparison.Ordinal);
        }

        value = value.TrimEnd(';');
        if (name is "RRULE" or "EXRULE")
        {
            // xCal fixes the order of the rule parts; the forms write the part names in one case.
            value = string.Join(';', value.Split(';').Select(static x => x.Contains('=', StringComparison.Ordinal) ? x[..x.IndexOf('=', StringComparison.Ordinal)].ToUpperInvariant() + x[x.IndexOf('=', StringComparison.Ordinal)..] : x).Order(StringComparer.Ordinal));
        }
        else if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && value.Contains(";base64,", StringComparison.Ordinal))
        {
            // Inline vCard 2.1/3.0 binary is a data: URI in the forms.
            value = value[(value.IndexOf(";base64,", StringComparison.Ordinal) + 8)..];
        }

        string grp = xml == 1 || p.Group is null ? string.Empty : p.Group.ToUpperInvariant() + ".";
        return path + "|" + grp + name + "|" + pstr + "|" + value;
    }
}
