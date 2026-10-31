using System.Globalization;
using System.Text;
using System.Text.Json;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.ContentLines;
using RecursiveAI.OneMail.Calendar.Ics;
using RecursiveAI.OneMail.Calendar.VCard;

// CalendarDump roundtrip <root>...        parse every file, write it back, compare bytes; counts per top-level directory
// CalendarDump facts <root> <out.jsonl>    one JSON line per file with the normalised facts the oracle comparison reads
// CalendarDump alloc <file> [repeat]       bytes allocated per content line on the reader path
return args.Length > 0 ? args[0] switch
{
    "roundtrip" => RoundTrip(args[1..]),
    "facts" => Facts(args[1], args[2]),
    "alloc" => Alloc(args[1], args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 200),
    _ => Usage(),
} : Usage();

static int Usage()
{
    Console.Error.WriteLine("usage: CalendarDump roundtrip <root>... | facts <root> <out.jsonl> | alloc <file> [repeat]");
    return 2;
}

static IEnumerable<string> Files(string root) =>
    Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(f => f.EndsWith(".ics", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ifb", StringComparison.OrdinalIgnoreCase))
        .OrderBy(f => f, StringComparer.Ordinal);

static int RoundTrip(string[] roots)
{
    int bad = 0;
    foreach (string root in roots)
    {
        Dictionary<string, (int Files, int Identical, long Bytes, int Defective)> per = [];
        Dictionary<CalendarDefectKind, int> kinds = [];
        List<string> nonIdentical = [];
        foreach (string f in Files(root))
        {
            string src = Path.GetRelativePath(root, f).Split(Path.DirectorySeparatorChar)[0];
            byte[] data = File.ReadAllBytes(f);
            (int n, int ok, long bytes, int defective) = per.GetValueOrDefault(src);
            try
            {
                ContentDocument d = ContentDocument.Parse(data);
                bool same = d.ToArray().AsSpan().SequenceEqual(data);
                if (!same)
                {
                    nonIdentical.Add(Path.GetRelativePath(root, f));
                }

                foreach (CalendarDefect x in d.Defects)
                {
                    kinds[x.Kind] = kinds.GetValueOrDefault(x.Kind) + 1;
                }

                per[src] = (n + 1, ok + (same ? 1 : 0), bytes + data.Length, defective + (d.Defects.Count > 0 ? 1 : 0));
            }
            catch (Exception e)
            {
                nonIdentical.Add(Path.GetRelativePath(root, f) + " EXCEPTION " + e.GetType().Name);
                per[src] = (n + 1, ok, bytes + data.Length, defective);
            }
        }

        Console.WriteLine($"root {root}");
        foreach ((string s, (int n, int ok, long b, int dd)) in per.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"  {s,-14} files {n,5}  byte-identical {ok,5}  bytes {b,10}  with-defects {dd,5}");
        }

        Console.WriteLine("  defects: " + string.Join(", ", kinds.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")));
        foreach (string x in nonIdentical)
        {
            Console.WriteLine("  NOT IDENTICAL: " + x);
        }

        bad += nonIdentical.Count;
    }

    return bad == 0 ? 0 : 1;
}

static int Alloc(string file, int repeat)
{
    byte[] data = File.ReadAllBytes(file);
    long lines = 0;
    long Pass()
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        ContentLineReader r = new(data);
        long n = 0;
        while (r.TryRead(out ContentLine l))
        {
            n++;
            foreach (ContentParameterSpan p in l.EnumerateParameters())
            {
                n += p.Name.Length == 0 ? 1 : 0;
            }
        }

        lines = n;
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    Pass();
    long total = 0;
    for (int i = 0; i < repeat; i++)
    {
        total += Pass();
    }

    Console.WriteLine($"{file}: {lines} lines/pass, {total / (double)repeat:F1} bytes/pass, {total / (double)repeat / Math.Max(1, lines):F3} bytes/line");
    return 0;
}

static int Facts(string root, string outFile)
{
    using StreamWriter w = new(outFile, false, new UTF8Encoding(false));
    foreach (string f in Files(root))
    {
        List<object[]> facts = [];
        try
        {
            ContentDocument d = ContentDocument.Parse(File.ReadAllBytes(f));
            foreach (Component c in d.Components)
            {
                Walk(c, string.Empty, facts);
            }

            w.WriteLine(JsonSerializer.Serialize(new Dictionary<string, object> { ["file"] = Path.GetRelativePath(root, f), ["facts"] = facts }));
        }
        catch (Exception e)
        {
            w.WriteLine(JsonSerializer.Serialize(new Dictionary<string, object> { ["file"] = Path.GetRelativePath(root, f), ["error"] = e.GetType().Name }));
        }
    }

    return 0;
}

static void Walk(Component c, string parent, List<object[]> facts)
{
    string path = parent.Length == 0 ? c.Name.ToUpperInvariant() : parent + "/" + c.Name.ToUpperInvariant();
    facts.Add([path, string.Empty, Array.Empty<object>(), string.Empty, string.Empty]);
    bool vcard = c.IsNamed("VCARD");
    string version = c.FindProperty("VERSION")?.Value.Trim() ?? string.Empty;
    foreach (ContentProperty p in c.Properties)
    {
        if (p.IsNamed("BEGIN") || p.IsNamed("END"))
        {
            continue;
        }

        facts.Add([path, (p.Group is null ? string.Empty : p.Group.ToUpperInvariant() + ".") + p.Name.ToUpperInvariant(), Params(p), vcard ? Facts_VCardValue(p, version) : IcsValue(p), p.Value]);
    }

    foreach (Component child in c.Components)
    {
        Walk(child, path, facts);
    }
}

static object[] Params(ContentProperty p)
{
    SortedDictionary<string, SortedSet<string>> m = new(StringComparer.Ordinal);
    foreach (ContentParameter q in p.Parameters)
    {
        string n = q.Name.ToUpperInvariant();
        string[] vals = q.IsBare ? [] : [.. q.Values];
        if (q.IsBare)
        {
            if (n is "QUOTED-PRINTABLE" or "BASE64" or "B" or "8BIT" or "7BIT")
            {
                continue;
            }

            n = "TYPE";
            vals = [q.Name];
        }

        if (n is "ENCODING" or "CHARSET" or "VALUE")
        {
            continue;
        }

        if (!m.TryGetValue(n, out SortedSet<string>? set))
        {
            m[n] = set = new SortedSet<string>(StringComparer.Ordinal);
        }

        foreach (string v in vals)
        {
            if (n == "TYPE")
            {
                foreach (string t in v.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    set.Add(t.ToLowerInvariant());
                }
            }
            else
            {
                set.Add(v);
            }
        }
    }

    return [.. m.Select(kv => (object)new object[] { kv.Key, kv.Value.ToArray() })];
}

static string Dt(IcsTime t) => t.Kind == IcsTimeKind.Date ? "d:" + t.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
    : "dt:" + t.Value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + (t.Kind == IcsTimeKind.Utc ? "Z" : string.Empty);

static string Dur(IcsDuration d) => "dur:" + ((long)d.ToTimeSpan().TotalSeconds).ToString(CultureInfo.InvariantCulture);

static string IcsValue(ContentProperty p)
{
    string name = p.Name.ToUpperInvariant();
    string? tzid = p.GetParameter("TZID");
    string v = p.Value;
    // RFC 5545 §3.2.20: VALUE overrides the property's default type; otherwise the default type follows the name.
    string type = p.GetParameter("VALUE")?.ToUpperInvariant() ?? name switch
    {
        "DTSTART" or "DTEND" or "DUE" or "RECURRENCE-ID" or "EXDATE" or "RDATE" => "DATE-TIME",
        "CREATED" or "DTSTAMP" or "LAST-MODIFIED" or "COMPLETED" => "DATE-TIME",
        "FREEBUSY" => "PERIOD",
        "DURATION" or "REFRESH-INTERVAL" => "DURATION",
        "TRIGGER" => "DURATION",
        "RRULE" or "EXRULE" => "RECUR",
        "TZOFFSETFROM" or "TZOFFSETTO" => "UTC-OFFSET",
        "CATEGORIES" or "RESOURCES" => "TEXTLIST",
        _ => "TEXT",
    };
    switch (type)
    {
        case "DATE" or "DATE-TIME" or "PERIOD":
            {
                List<string> parts = [];
                foreach (string s in v.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (IcsTime.TryParse(s, tzid, out IcsTime t))
                    {
                        parts.Add(Dt(t));
                    }
                    else if (IcsPeriod.TryParse(s, tzid, out IcsPeriod per))
                    {
                        parts.Add("per:" + Dt(per.Start) + "/" + (per.End is { } e ? Dt(e) : Dur(per.Duration!.Value)));
                    }
                    else
                    {
                        return "t:" + p.GetText();
                    }
                }

                return string.Join(",", parts);
            }

        case "TEXTLIST":
            return "l:" + string.Join(",", ContentValues.SplitUnescaped(v, ',').Select(x => ContentValues.Unescape(x)));
        case "DURATION":
            // TRIGGER may also hold a date-time when VALUE=DATE-TIME; that case is handled by the branch above.
            return IcsDuration.TryParse(v, out IcsDuration d) ? Dur(d) : "t:" + p.GetText();
        case "RECUR":
            return RecurrenceRule.TryParse(v, tzid, out RecurrenceRule r) ? "rr:" + Rule(r) : "t:" + p.GetText();
        case "UTC-OFFSET":
            return IcsUtcOffset.TryParse(v, out IcsUtcOffset o) ? "off:" + (long)o.ToTimeSpan().TotalSeconds : "t:" + p.GetText();
        default:
            return "t:" + p.GetText();
    }
}

static string Rule(RecurrenceRule r)
{
    List<string> p = [$"FREQ={r.Frequency.ToString().ToUpperInvariant()}"];
    if (r.Interval != 1) { p.Add("INTERVAL=" + r.Interval); }
    if (r.Count is { } c) { p.Add("COUNT=" + c); }
    if (r.Until is { } u) { p.Add("UNTIL=" + Dt(u)); }
    void L(string n, IEnumerable<object> v) { object[] a = [.. v]; if (a.Length > 0) { p.Add(n + "=" + string.Join(",", a)); } }
    L("BYSECOND", r.BySecond.Cast<object>());
    L("BYMINUTE", r.ByMinute.Cast<object>());
    L("BYHOUR", r.ByHour.Cast<object>());
    L("BYDAY", r.ByDay.Select(x => (object)x.ToString()));
    L("BYMONTHDAY", r.ByMonthDay.Cast<object>());
    L("BYYEARDAY", r.ByYearDay.Cast<object>());
    L("BYWEEKNO", r.ByWeekNo.Cast<object>());
    L("BYMONTH", r.ByMonth.Cast<object>());
    L("BYSETPOS", r.BySetPos.Cast<object>());
    if (r.WeekStart is { } w) { p.Add("WKST=" + new WeekdayNumber(null, w)); }
    return string.Join(";", p.Order(StringComparer.Ordinal));
}

static string Facts_VCardValue(ContentProperty p, string version)
{
    TextEscapeStyle style = version == "2.1" ? TextEscapeStyle.VCard21 : TextEscapeStyle.Rfc5545;
    string name = p.Name.ToUpperInvariant();
    string? enc = p.Encoding;
    if (enc is "BASE64" or "B" && p.TryGetBytes(out byte[] bytes))
    {
        return "b:" + bytes.Length;
    }

    if (name is "N" or "ADR" or "ORG" or "GENDER" or "CLIENTPIDMAP")
    {
        string dec = p.GetDecodedValue();
        return "s:" + string.Join(";", ContentValues.SplitUnescaped(dec, ';').Select(c =>
            version == "2.1" || name is "ORG" or "CLIENTPIDMAP" ? ContentValues.Unescape(c, style) : string.Join(",", ContentValues.SplitUnescaped(c, ',').Where(x => x.Length > 0).Select(x => ContentValues.Unescape(x, style)))));
    }

    if (name is "CATEGORIES" or "NICKNAME")
    {
        return "l:" + string.Join(",", ContentValues.SplitUnescaped(p.GetDecodedValue(), ',').Select(x => ContentValues.Unescape(x, style)));
    }

    return "t:" + p.GetText(style);
}
