using System.Globalization;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.ContentLines;
using RecursiveAI.OneMail.Calendar.VCard;
using RecursiveAI.OneMail.Contacts;
using RecursiveAI.OneMail.Conversion;
using RecursiveAI.OneMail.Conversion.OxVCard;
using RecursiveAI.OneMail.Mapi;

namespace ContactTool;

// vCard -> contact -> vCard over a directory of .vcf files.
//   idempotent: contact1 (from the card) and contact2 (from the card we write from contact1) hold the same properties, apart from the documented derived ones;
//   facts: every fact of the typed view of the first card is either in the second card, or its property is in the loss report.
internal static class VCardRoundTrip
{
    public static int Run(string dir, int limit)
    {
        int cards = 0, idem = 0, notIdem = 0, factsOk = 0, factsMissing = 0, silent = 0;
        var examples = new List<string>();
        var codes = new SortedDictionary<string, int>();
        foreach (string f in Directory.EnumerateFiles(dir, "*.vcf", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            ContentDocument doc;
            try
            {
                doc = ContentDocument.Parse(File.ReadAllBytes(f));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                continue;
            }

            foreach (VCardView v in VCardDocument.Cards(doc))
            {
                if (cards >= limit)
                {
                    goto done;
                }

                cards++;
                ConversionResult<MapiMessage> c1 = new VCardToContactConverter().Convert(v.Component);
                foreach (LossEntry e in c1.Report)
                {
                    codes[e.Kind + ":" + e.Code] = codes.GetValueOrDefault(e.Kind + ":" + e.Code) + 1;
                }

                ConversionResult<Component> out1 = new ContactToVCardConverter(new ContactToVCardOptions { Version = v.Version == VCardVersion.V40 ? VCardVersion.V40 : VCardVersion.V30 }).Convert(c1.Value);
                var d2 = new ContentDocument();
                d2.Root.Add(out1.Value);
                Component card2 = ContentDocument.Parse(d2.ToArray()).Components.First();
                MapiMessage c2 = new VCardToContactConverter().Convert(card2).Value;

                if (Same(c1.Value, c2, out string? why))
                {
                    idem++;
                }
                else
                {
                    notIdem++;
                    if (examples.Count < 15)
                    {
                        examples.Add(Path.GetFileName(f) + ": " + why);
                    }
                }

                // Every property of the card is carried or reported: the report names the ones that are not.
                var reported = new HashSet<string>(c1.Report.Select(e => e.Name.ToUpperInvariant()));
                var second = new VCardView(card2);
                foreach (ContentProperty p in v.Component.Properties)
                {
                    string name = ((p.Group is null ? string.Empty : p.Group + ".") + p.Name).ToUpperInvariant();
                    if (reported.Contains(name))
                    {
                        continue;
                    }

                    if (Carried(p, second))
                    {
                        factsOk++;
                    }
                    else if (p.Name.ToUpperInvariant() is "VERSION" or "PRODID")
                    {
                        factsOk++;
                    }
                    else
                    {
                        factsMissing++;
                        if (examples.Count < 30)
                        {
                            examples.Add(Path.GetFileName(f) + ": not carried and not reported: " + p.Name + ":" + (p.Value.Length > 40 ? p.Value[..40] : p.Value));
                        }
                    }
                }

                _ = silent;
            }
        }

    done:
        Console.WriteLine($"vCard -> contact -> vCard: cards {cards}; contact == contact after one more round {idem}, differs {notIdem}; properties carried {factsOk}, NOT carried and NOT reported {factsMissing}");
        Console.WriteLine("  loss codes: " + string.Join(", ", codes.Select(k => $"{k.Key}x{k.Value}")));
        foreach (string e in examples)
        {
            Console.WriteLine("  " + e);
        }

        return 0;
    }

    // The value is somewhere in the second card, in whatever property it ended up in.
    private static bool Carried(ContentProperty p, VCardView second)
    {
        string name = p.Name.ToUpperInvariant();
        if (name is "BEGIN" or "END")
        {
            return true;
        }

        string text = p.GetText(TextEscapeStyle.Rfc5545);
        if (text.Length == 0)
        {
            return true;
        }

        // A backslash before an unlisted character (\: in a URL) is dropped by the typed view (docs/ambiguities.md CV1), so it is not compared.
        string norm(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal).Replace(";", ",", StringComparison.Ordinal).Replace("\\", string.Empty, StringComparison.Ordinal);
        string want = norm(text.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ? text[4..] : text);
        foreach (ContentProperty q in second.Component.Properties)
        {
            if (q.IsNamed("VERSION") || q.IsNamed("PRODID"))
            {
                continue;
            }

            string have = norm(q.GetText(TextEscapeStyle.Rfc5545));
            if (have.Length > 0 && (have == want || have.Contains(want, StringComparison.Ordinal)))
            {
                return true;
            }

            if (name is "N" or "ADR" or "ORG")
            {
                // a structured value is carried when each non-empty component is
                if (want.Split(',', StringSplitOptions.RemoveEmptyEntries).All(part => second.Component.Properties.Any(z => norm(z.GetText()).Contains(part, StringComparison.Ordinal))))
                {
                    return true;
                }
            }
        }

        return name is "PHOTO" or "KEY" && second.Component.FindProperty(name) is not null
            || name is "BDAY" or "ANNIVERSARY" or "X-MS-ANNIVERSARY" && second.Component.Properties.Any(q => q.IsNamed("BDAY") || q.IsNamed("ANNIVERSARY") || q.IsNamed("X-MS-ANNIVERSARY"))
            || name == "REV" && second.Component.FindProperty("REV") is not null
            || name == "CLASS";
    }

    private static bool Same(MapiMessage a, MapiMessage b, out string? why)
    {
        why = null;
        var ma = a.Properties.ToDictionary(p => p.Name is { } n ? n.ToString() : p.Id.ToString("X4", CultureInfo.InvariantCulture), p => p);
        var mb = b.Properties.ToDictionary(p => p.Name is { } n ? n.ToString() : p.Id.ToString("X4", CultureInfo.InvariantCulture), p => p);
        foreach (string k in ma.Keys.Union(mb.Keys))
        {
            // the revision time and the derived copies are recomputed
            if (!ma.TryGetValue(k, out MapiProperty? pa) || !mb.TryGetValue(k, out MapiProperty? pb))
            {
                why = "property " + k + (ma.ContainsKey(k) ? " lost" : " added");
                return false;
            }

            if (!pa.GetBytes().Span.SequenceEqual(pb.GetBytes().Span) || pa.ValueCount != pb.ValueCount)
            {
                why = "property " + k + " differs";
                return false;
            }
        }

        MapiContact ca = new(a), cb = new(b);
        if (ca.Phones.Count != cb.Phones.Count || a.Attachments.Count != b.Attachments.Count)
        {
            why = "phones or attachments differ";
            return false;
        }

        return true;
    }
}
