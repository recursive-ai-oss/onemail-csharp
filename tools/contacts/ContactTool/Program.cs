using System.Globalization;
using System.Text;
using System.Text.Json;
using RecursiveAI.OneMail;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.ContentLines;
using RecursiveAI.OneMail.Calendar.VCard;
using RecursiveAI.OneMail.Contacts;
using RecursiveAI.OneMail.Conversion;
using RecursiveAI.OneMail.Conversion.OxVCard;
using RecursiveAI.OneMail.Mapi;

// ContactTool: reads contact and distribution list items, converts them to vCards and back, and counts what carried over.
//   ContactTool dump IN [--limit N]                 one JSON line per contact / distribution list: the typed fields
//   ContactTool export IN OUT [--limit N]           writes OUT/NNNNNN.30.vcf and .40.vcf for the contacts, for the vCard oracles
//   ContactTool rt-contact IN [--limit N]           contact -> vCard -> contact: mapped properties come back exact, the rest is in the loss report
//   ContactTool rt-vcard DIR [--limit N]            vCard -> contact -> vCard: the same, in the other direction
//   ContactTool gen DIR [SEED [COUNT]]              writes generated contact and distribution list MSG files
//   ContactTool vfacts DIR OUT.jsonl                typed-view facts of every .vcf, for the oracle comparison
namespace ContactTool;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: ContactTool dump|export|rt-contact|rt-vcard|vfacts IN [OUT] [--limit N]");
            return 2;
        }

        int limit = int.MaxValue;
        var rest = new List<string>();
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--limit")
            {
                limit = int.Parse(args[++i], CultureInfo.InvariantCulture);
            }
            else
            {
                rest.Add(args[i]);
            }
        }

        return args[0] switch
        {
            "dump" => Dump(rest[0], limit),
            "export" => Export(rest[0], rest[1], limit),
            "rt-contact" => RoundTripContact(rest[0], limit),
            "rt-vcard" => RoundTripVCard(rest[0], limit),
            "vfacts" => VFacts.Run(rest[0], rest[1]),
            "photo" => Photo(rest[0]),
            "gen" => ContactGen.Run(rest[0], rest.Count > 1 ? int.Parse(rest[1], CultureInfo.InvariantCulture) : 20261031, rest.Count > 2 ? int.Parse(rest[2], CultureInfo.InvariantCulture) : 64),
            _ => 2,
        };
    }

    private static IEnumerable<string> StoreFiles(string input)
    {
        if (File.Exists(input))
        {
            yield return input;
            yield break;
        }

        foreach (string f in Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (Path.GetExtension(f).ToLowerInvariant() is ".msg" or ".oft" or ".pst" or ".ost")
            {
                yield return f;
            }
        }
    }

    private static IEnumerable<(string Origin, MapiMailItem Item)> Items(string input, int limit)
    {
        int n = 0;
        foreach (string file in StoreFiles(input))
        {
            MailContainer? c = null;
            try
            {
                c = Mail.Open(file);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.Error.WriteLine($"skip {file}: {ex.GetType().Name}");
            }

            if (c is null)
            {
                continue;
            }

            using (c)
            {
                int k = 0;
                foreach (IMailItem item in c.Enumerate())
                {
                    if (n >= limit)
                    {
                        yield break;
                    }

                    if (item is MapiMailItem m && m.Kind is MailItemKind.Contact or MailItemKind.DistributionList)
                    {
                        yield return (Path.GetFileName(file) + "#" + k, m);
                        n++;
                    }

                    k++;
                }
            }
        }
    }

    // ---- dump ---------------------------------------------------------------------------------------------------------

    private static string? S(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static Dictionary<string, object?> Fields(MapiMailItem item)
    {
        var f = new Dictionary<string, object?>();
        if (item.Contact is { } c)
        {
            f["displayName"] = c.DisplayName;
            f["fileUnder"] = c.FileUnder;
            f["given"] = c.GivenName;
            f["middle"] = c.MiddleName;
            f["surname"] = c.Surname;
            f["initials"] = c.Initials;
            f["prefix"] = c.NamePrefix;
            f["suffix"] = c.NameSuffix;
            f["nickname"] = c.Nickname;
            f["gender"] = c.Gender.ToString();
            f["company"] = c.CompanyName;
            f["department"] = c.Department;
            f["jobTitle"] = c.JobTitle;
            f["profession"] = c.Profession;
            f["office"] = c.OfficeLocation;
            f["manager"] = c.ManagerName;
            f["assistant"] = c.AssistantName;
            f["spouse"] = c.SpouseName;
            f["children"] = c.ChildrensNames;
            f["hobbies"] = c.Hobbies;
            f["notes"] = c.Notes;
            f["categories"] = c.Categories;
            f["birthDate"] = c.BirthDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            f["anniversary"] = c.AnniversaryDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            f["homePage"] = c.PersonalHomePage;
            f["businessPage"] = c.BusinessHomePage;
            f["im"] = c.InstantMessagingAddress;
            f["freeBusy"] = c.FreeBusyUrl;
            f["userFields"] = c.UserFields;
            f["mailing"] = c.MailingAddress.ToString();
            f["addresses"] = c.Addresses.Where(a => !a.IsEmpty).Select(a => new[] { a.Kind.ToString(), a.Street, a.City, a.State, a.PostalCode, a.Country, a.PostOfficeBox }).ToList();
            f["phones"] = c.Phones.Select(p => new[] { p.Kind.ToString(), p.Number }).ToList();
            f["emails"] = c.EmailAddresses.Select(e => new[] { e.Slot.ToString(CultureInfo.InvariantCulture), e.DisplayName, e.AddressType, e.Address }).ToList();
            f["faxes"] = c.FaxAddresses.Select(e => new[] { e.Slot.ToString(CultureInfo.InvariantCulture), e.AddressType, e.Address }).ToList();
            f["photoBytes"] = c.PhotoAttachment?.OpenData() is { } s ? CountAndDispose(s) : null;
            f["hasPicture"] = c.HasPicture;
            f["certificates"] = c.X509Certificates.Count;
        }
        else if (item.DistributionList is { } d)
        {
            f["name"] = d.Name;
            f["members"] = d.Members.Select(x => new[] { x.Kind.ToString(), x.DisplayName, x.AddressType, x.Address, x.ContactSlot?.ToString(CultureInfo.InvariantCulture) }).ToList();
        }

        return f;
    }

    private static long CountAndDispose(Stream s)
    {
        using (s)
        {
            long n = 0;
            byte[] buf = new byte[8192];
            int r;
            while ((r = s.Read(buf)) > 0)
            {
                n += r;
            }

            return n;
        }
    }

    private static int Dump(string input, int limit)
    {
        int n = 0;
        foreach ((string origin, MapiMailItem item) in Items(input, limit))
        {
            Console.WriteLine(JsonSerializer.Serialize(new Dictionary<string, object?> { ["origin"] = origin, ["class"] = item.Message.MessageClass, ["kind"] = item.Kind.ToString(), ["fields"] = Fields(item) }));
            n++;
        }

        Console.Error.WriteLine($"{n} contact and distribution list items");
        return 0;
    }

    // ---- export ----------------------------------------------------------------------------------------------------

    private static byte[] ToBytes(Component card)
    {
        var doc = new ContentDocument();
        doc.Root.Add(card);
        return doc.ToArray();
    }

    private static int Export(string input, string outDir, int limit)
    {
        Directory.CreateDirectory(outDir);
        int n = 0;
        foreach ((string origin, MapiMailItem item) in Items(input, limit))
        {
            foreach ((VCardVersion v, string tag) in new[] { (VCardVersion.V30, "30"), (VCardVersion.V40, "40") })
            {
                ConversionResult<Component> r = new ContactToVCardConverter(new ContactToVCardOptions { Version = v }).Convert(item.Message);
                File.WriteAllBytes(Path.Combine(outDir, n.ToString("D6", CultureInfo.InvariantCulture) + "." + tag + ".vcf"), ToBytes(r.Value));
            }

            n++;
        }

        Console.Error.WriteLine($"wrote {n} items x 2 versions");
        return 0;
    }

    // ---- contact -> vCard -> contact --------------------------------------------------------------------------------------

    private static string Key(MapiProperty p) => p.Name is { } n ? n.ToString() : p.Id.ToString("X4", CultureInfo.InvariantCulture);

    private static string Value(MapiMessage m, MapiProperty p)
    {
        switch (p.Type)
        {
            case MapiType.UnicodeString or MapiType.String8 or MapiType.MultipleString:
                var parts = new List<string>();
                for (int i = 0; i < p.ValueCount; i++)
                {
                    parts.Add(p.AsString(m.String8Encoding, null, i) ?? string.Empty);
                }

                return "s:" + string.Join("\u0001", parts);
            case MapiType.Integer32:
                return "i:" + p.AsInt32();
            case MapiType.Integer16:
                return "i:" + p.AsInt16();
            case MapiType.Boolean:
                return "b:" + p.AsBoolean();
            case MapiType.Time:
                return "t:" + p.AsTime()?.UtcTicks;
            default:
                var sb = new StringBuilder("x:");
                for (int i = 0; i < p.ValueCount; i++)
                {
                    sb.Append(Convert.ToHexString(p.GetBytes(i).Span)).Append('|');
                }

                return sb.ToString();
        }
    }

    // The properties MS-OXVCARD carries and that must come back equal (tagged IDs, then PSETID_Address lids).
    private static readonly HashSet<string> Exact = BuildExact();

    private static HashSet<string> BuildExact()
    {
        var s = new HashSet<string>();
        foreach (ushort id in new ushort[] { 0x3001, 0x3A06, 0x3A11, 0x3A44, 0x3A45, 0x3A05, 0x3A4F, 0x3A16, 0x3A18, 0x3A17, 0x3A46, 0x3A50, 0x3A51, 0x1000, 0x3A30, 0x3A4E, 0x3A48, 0x3A43, 0x3A58,
            0x3A59, 0x3A5A, 0x3A5B, 0x3A5C, 0x3A5D, 0x3A5E, 0x3A5F, 0x3A60, 0x3A61, 0x3A62, 0x3A63, 0x3A64, 0x0036 })
        {
            s.Add(id.ToString("X4", CultureInfo.InvariantCulture));
        }

        foreach (ushort id in new ushort[] { 0x3A08, 0x3A1B, 0x3A09, 0x3A2F, 0x3A1C, 0x3A1E, 0x3A21, 0x3A1D, 0x3A1A, 0x3A1F, 0x3A57, 0x3A2E, 0x3A02, 0x3A2D, 0x3A2C, 0x3A4B, 0x3A24, 0x3A25, 0x3A23 })
        {
            s.Add(id.ToString("X4", CultureInfo.InvariantCulture));
        }

        foreach (uint lid in new uint[] { 0x8045, 0x8046, 0x8047, 0x8048, 0x8049, 0x804A, 0x8062, 0x8022, 0x804F, 0x8050, 0x8051, 0x8052, 0x80D8, 0x8083, 0x8082, 0x8093, 0x8092, 0x80A3, 0x80A2 })
        {
            s.Add(new MapiNamedProperty(MapiPropertySets.Address, lid).ToString());
        }

        s.Add(new MapiNamedProperty(MapiPropertySets.PublicStrings, "Keywords").ToString());
        return s;
    }

    // Derived on import (documented in docs/contacts.md): copies of the display name, the formatted addresses, the mailing address, flags and display names.
    private static bool Derived(string key)
    {
        if (key is "0037" or "0E1D" or "0070" or "3A15" or "3A26" or "3A27" or "3A28" or "3A29" or "3A2A" or "3A2B" or "3A41" or "3A42" or "3A70" or "3008" or "3A4D")
        {
            return true;
        }

        foreach (uint lid in new uint[] { 0x8005, 0x8015, 0x801A, 0x801B, 0x801C, 0x80DE, 0x80DF, 0x8080, 0x8090, 0x80A0, 0x8084, 0x8094, 0x80A4 })
        {
            if (key == new MapiNamedProperty(MapiPropertySets.Address, lid).ToString())
            {
                return true;
            }
        }

        return false;
    }

    private sealed class Counts
    {
        public int Items, Exact, Reported, Derived, Mismatch, Silent, Added;
        public readonly SortedDictionary<string, int> MismatchNames = [], SilentNames = [], AddedNames = [], ReportCodes = [];
    }

    private static int RoundTripContact(string input, int limit)
    {
        foreach (VCardVersion version in new[] { VCardVersion.V30, VCardVersion.V40 })
        {
            var cn = new Counts();
            var conv = new ContactToVCardConverter(new ContactToVCardOptions { Version = version });
            foreach ((string origin, MapiMailItem item) in Items(input, limit))
            {
                if (item.Kind != MailItemKind.Contact)
                {
                    continue;
                }

                cn.Items++;
                MapiMessage src = item.Message;
                ConversionResult<Component> r = conv.Convert(src);
                byte[] bytes = ToBytes(r.Value);
                ContentDocument parsed = ContentDocument.Parse(bytes);
                Component card = parsed.Components.First(c => c.IsNamed("VCARD"));
                MapiMessage back = new VCardToContactConverter().Convert(card).Value;
                foreach (LossEntry e in r.Report)
                {
                    cn.ReportCodes[e.Kind + ":" + e.Code] = cn.ReportCodes.GetValueOrDefault(e.Kind + ":" + e.Code) + 1;
                }

                var backByKey = new Dictionary<string, MapiProperty>();
                foreach (MapiProperty p in back.Properties)
                {
                    backByKey[Key(p)] = p;
                }

                var srcKeys = new HashSet<string>();
                foreach (MapiProperty p in src.Properties)
                {
                    string key = Key(p);
                    srcKeys.Add(key);
                    string label = p.Name is { } nm ? nm.ToString() : MapiPropertyNames.GetName(p.Id) ?? p.Tag.ToString();
                    if (r.Report.Has(label) || r.Report.Has(p.Name is null ? (MapiPropertyNames.GetName(p.Id) ?? p.Tag.ToString()) : label))
                    {
                        cn.Reported++;
                        continue;
                    }

                    if (Exact.Contains(key))
                    {
                        if (backByKey.TryGetValue(key, out MapiProperty? b) && Value(back, b) == Value(src, p))
                        {
                            cn.Exact++;
                        }
                        else
                        {
                            cn.Mismatch++;
                            cn.MismatchNames[key + " " + origin + " src=" + Value(src, p) + " back=" + (backByKey.TryGetValue(key, out MapiProperty? bb) ? Value(back, bb) : "<absent>")] = 1;
                        }

                        continue;
                    }

                    if (Derived(key))
                    {
                        cn.Derived++;
                        continue;
                    }

                    cn.Silent++;
                    cn.SilentNames[key] = cn.SilentNames.GetValueOrDefault(key) + 1;
                }

                foreach (string key in backByKey.Keys)
                {
                    if (!srcKeys.Contains(key))
                    {
                        cn.Added++;
                        cn.AddedNames[key] = cn.AddedNames.GetValueOrDefault(key) + 1;
                    }
                }

                // The photo and the birthday are checked as values, not as raw properties.
                var cs = new MapiContact(src);
                var cb = new MapiContact(back);
                if (cs.BirthDate != cb.BirthDate && cs.BirthDate is not null)
                {
                    cn.Mismatch++;
                    cn.MismatchNames["birthdate " + origin] = 1;
                }

                if (cs.AnniversaryDate != cb.AnniversaryDate && cs.AnniversaryDate is not null)
                {
                    cn.Mismatch++;
                    cn.MismatchNames["anniversary " + origin] = 1;
                }
            }

            Console.WriteLine($"contact -> vCard {version} -> contact: items {cn.Items}, properties exact {cn.Exact}, reported {cn.Reported}, derived {cn.Derived}, MISMATCH {cn.Mismatch}, SILENT {cn.Silent}, added by import {cn.Added}");
            foreach (KeyValuePair<string, int> k in cn.MismatchNames.Take(2000))
            {
                Console.WriteLine("  mismatch " + k.Key);
            }

            foreach (KeyValuePair<string, int> k in cn.SilentNames.Take(20))
            {
                Console.WriteLine($"  silent {k.Key} x{k.Value}");
            }

            Console.WriteLine("  added: " + string.Join(", ", cn.AddedNames.Select(k => $"{k.Key}x{k.Value}")));
            Console.WriteLine("  report codes: " + string.Join(", ", cn.ReportCodes.Select(k => $"{k.Key}x{k.Value}")));
        }

        return 0;
    }

    private static int Photo(string file)
    {
        foreach (VCardView v in VCardDocument.Cards(ContentDocument.Parse(File.ReadAllBytes(file))))
        {
            foreach (VCardMedia p in v.Photos)
            {
                Console.WriteLine($"photo: inline {p.IsInline} data {p.Data?.Length} uri {p.Uri?.Length} type {p.MediaType} value length {p.Property.Value.Length} enc {p.Property.Encoding} tail [{p.Property.Value[^Math.Min(20, p.Property.Value.Length)..]}]");
            }
        }

        return 0;
    }

    // ---- vCard -> contact -> vCard ------------------------------------------------------------------------------------------

    private static int RoundTripVCard(string dir, int limit) => VCardRoundTrip.Run(dir, limit);
}
