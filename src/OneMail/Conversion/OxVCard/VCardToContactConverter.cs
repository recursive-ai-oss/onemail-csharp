using System.Globalization;
using System.Text;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.ContentLines;
using RecursiveAI.OneMail.Calendar.VCard;
using RecursiveAI.OneMail.Contacts;
using RecursiveAI.OneMail.Conversion.OxcMail;
using RecursiveAI.OneMail.Mapi;

namespace RecursiveAI.OneMail.Conversion.OxVCard;

/// <summary>
/// Converts a vCard (2.1, 3.0 or 4.0) to a contact item (<c>IPM.Contact</c>) following MS-OXVCARD 2.1.3. Every property of the card is
/// either carried or listed in the loss report: <c>Dropped</c> where MS-OXVCARD ignores it on import or the contact has nowhere to put
/// it (a fifth <c>X-MS-TEXT</c>, a second photo), <c>Approximated</c> where the value was reduced, <c>Internal</c> for the card's own
/// bookkeeping (<c>VERSION</c>, <c>PRODID</c>, <c>MAILER</c>).
/// </summary>
/// <remarks>
/// <para>The report's <c>Family</c> is <c>vcard</c> and its <c>Name</c> is the property as written, with its group. Text with a line feed gets CR LF, which is
/// how Outlook stores line breaks. A name part written as a list (<c>N:A,B;…</c>) is stored as the text <c>A,B</c>, since MS-OXVCARD
/// 2.1.3.2.2 assigns "the entire text component".</para>
/// <para>Codes: <c>NoContactMapping</c>, <c>VCardBookkeeping</c>, <c>AdditionalValueNotMapped</c>, <c>PartialDate</c>, <c>TimeOfDayDropped</c>, <c>OrgUnitsMerged</c>,
/// <c>AddressExtendedMerged</c>, <c>UnsupportedPhotoType</c>, <c>PhotoNotInline</c>, <c>KeyNotX509</c>, <c>ValueOutOfRange</c>.</para>
/// <para>Never throws for malformed content.</para>
/// </remarks>
public sealed class VCardToContactConverter : IItemConversion<Component, MapiMessage>
{
    /// <inheritdoc/>
    public ConversionResult<MapiMessage> Convert(Component source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var report = new LossReport();
        MapiMessage m = new Importer(source, report).Run();
        return new ConversionResult<MapiMessage>(m, report);
    }

    private sealed class Importer(Component card, LossReport report)
    {
        private readonly VCardView _v = new(card);
        private readonly MapiMessage _m = new();
        private readonly HashSet<ContentProperty> _used = [];
        private readonly Dictionary<uint, ushort> _lidIds = [];
        private readonly Dictionary<string, ushort> _stringIds = [];
        private int _nextId = 0x8000;
        private string _fn = string.Empty;

        // ---- building blocks ----------------------------------------------------------------------------------------

        private ushort IdFor(MapiNamedProperty name)
        {
            if (name.IsString)
            {
                if (!_stringIds.TryGetValue(name.Name!, out ushort s))
                {
                    _stringIds[name.Name!] = s = (ushort)_nextId++;
                }

                return s;
            }

            if (!_lidIds.TryGetValue(name.Lid, out ushort id))
            {
                _lidIds[name.Lid] = id = (ushort)_nextId++;
            }

            return id;
        }

        private static MapiNamedProperty Addr(uint lid) => new(MapiPropertySets.Address, lid);

        private void Str(ushort id, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                _m.Properties.Set(MapiProps.Str(id, OxVCardTables.ToMapiText(value)));
            }
        }

        private void NamedStr(uint lid, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                MapiNamedProperty n = Addr(lid);
                _m.Properties.Set(OxVCardTables.Named(IdFor(n), n, MapiType.UnicodeString, OxVCardTables.Utf16(OxVCardTables.ToMapiText(value))));
            }
        }

        private void NamedInt(uint lid, int value)
        {
            MapiNamedProperty n = Addr(lid);
            _m.Properties.Set(OxVCardTables.Named(IdFor(n), n, MapiType.Integer32, OxVCardTables.Int32(value)));
        }

        private void NamedTime(uint lid, DateTimeOffset value)
        {
            MapiNamedProperty n = Addr(lid);
            _m.Properties.Set(OxVCardTables.Named(IdFor(n), n, MapiType.Time, OxVCardTables.FileTime(value)));
        }

        private void Drop(ContentProperty p, string code, string reason, LossKind kind = LossKind.Dropped)
        {
            _used.Add(p);
            report.Add(OxVCardTables.Family, (p.Group is null ? string.Empty : p.Group + ".") + p.Name.ToUpperInvariant(), kind, code, reason);
        }

        private string? Text(ContentProperty p)
        {
            _used.Add(p);
            string t = _v.GetText(p);
            return t.Length == 0 ? null : t;
        }

        // The first non-empty value of a property that has room for one; later ones are listed.
        private string? First(string name)
        {
            string? result = null;
            foreach (ContentProperty p in card.FindProperties(name))
            {
                string? t = Text(p);
                if (t is null)
                {
                    continue;
                }

                if (result is null)
                {
                    result = t;
                }
                else
                {
                    Drop(p, "AdditionalValueNotMapped", "The contact holds one value for " + name + "; the first was kept.");
                }
            }

            return result;
        }

        private static string Join(IReadOnlyList<string> parts) => string.Join(",", parts);

        // ---- the conversion -----------------------------------------------------------------------------------------------

        public MapiMessage Run()
        {
            _m.Properties.Set(MapiProps.Str(MapiPid.MessageClass, "IPM.Contact"));

            // 2.1.3.2.1: FN is PidTagDisplayName, PidTagNormalizedSubject and PidTagConversationTopic.
            _fn = First("FN") ?? string.Empty;
            if (_fn.Length > 0)
            {
                Str(0x3001, _fn);
                Str(ContactTags.NormalizedSubject, _fn);
                Str(ContactTags.ConversationTopic, _fn);
                NamedStr(0x8005, _fn);
            }

            ImportName();
            Str(0x3A4F, string.Join(",", _v.Nicknames).Length == 0 ? null : string.Join(",", _v.Nicknames));
            MarkUsed("NICKNAME");
            ImportPhoto();
            ImportDates();
            ImportAddresses();
            ImportPhones();
            ImportEmails();
            ImportOrganization();

            Str(0x3A17, First("TITLE"));
            Str(0x3A46, First("ROLE"));
            Str(0x1000, First("NOTE"));
            Str(0x3A43, First("X-MS-INTERESTS") ?? First("X-INTERESTS"));
            Str(0x3A48, First("X-MS-SPOUSE"));
            Str(0x3A4E, First("X-MS-MANAGER"));
            Str(0x3A30, First("X-MS-ASSISTANT") ?? First("X-ASSISTANT"));
            NamedStr(0x80D8, First("FBURL"));
            ImportCategories();
            ImportUrls();
            ImportClass();
            ImportRevision();
            ImportKeys();
            ImportCustom();
            ImportGender();

            foreach (ContentProperty p in card.Properties)
            {
                if (_used.Contains(p))
                {
                    continue;
                }

                string name = p.Name.ToUpperInvariant();
                if (name is "VERSION" or "PRODID" or "MAILER" or "BEGIN" or "END")
                {
                    Drop(p, "VCardBookkeeping", "Describes the card, not the contact.", LossKind.Internal);
                }
                else
                {
                    Drop(p, "NoContactMapping", name is "UID" or "TZ" or "GEO" or "LOGO" or "SOUND" or "SORT-STRING" or "LABEL"
                        ? "MS-OXVCARD 2.1.3 does not import " + name + "."
                        : "MS-OXVCARD maps no contact property from " + name + ".");
                }
            }

            return _m;
        }

        private void MarkUsed(string name)
        {
            foreach (ContentProperty p in card.FindProperties(name))
            {
                _used.Add(p);
            }
        }

        private void ImportName()
        {
            if (card.FindProperty("N") is not { } p)
            {
                return;
            }

            _used.Add(p);
            VCardName n = _v.Name!;
            Str(0x3A11, Join(n.Family));
            Str(0x3A06, Join(n.Given));
            Str(0x3A44, Join(n.Additional));
            Str(0x3A45, Join(n.Prefixes));
            Str(ContactTags.Generation, Join(n.Suffixes));
            foreach (ContentProperty extra in card.FindProperties("N").Skip(1))
            {
                Drop(extra, "AdditionalValueNotMapped", "The contact holds one name; the first N was kept.");
            }
        }

        // ---- photo ------------------------------------------------------------------------------------------------------

        private void ImportPhoto()
        {
            bool done = false;
            foreach (VCardMedia photo in _v.Photos)
            {
                ContentProperty p = photo.Property;
                _used.Add(p);
                if (done)
                {
                    Drop(p, "AdditionalValueNotMapped", "The contact holds one photo; the first was kept.");
                    continue;
                }

                byte[]? data = photo.Data;
                if (data is null || data.Length == 0)
                {
                    Drop(p, "PhotoNotInline", "MS-OXVCARD 2.1.3.2.4 imports only photos with ENCODING=b; a linked photo is not fetched.");
                    continue;
                }

                string? type = OxVCardTables.ImageTypeOf(photo.MediaType is { } mt && mt.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? mt[6..] : null, data);
                if (type is null)
                {
                    Drop(p, "UnsupportedPhotoType", "MS-OXVCARD 2.1.3.2.4 imports bmp, gif, jpeg and png photos only.");
                    continue;
                }

                string ext = type == "jpeg" ? ".jpeg" : "." + type;
                var a = new MapiAttachment(_m);
                a.Properties.Set(MapiProps.Int(0x3705, 1)); // PidTagAttachMethod: by value
                a.Properties.Set(MapiProps.Bin(0x3701, data));
                a.Properties.Set(MapiProps.Str(ContactTags.AttachExtension, ext));
                a.Properties.Set(MapiProps.Str(0x3707, "ContactPhoto" + ext));
                a.Properties.Set(MapiProps.Bool(ContactTags.AttachmentContactPhoto, true));
                _m.AddAttachment(a);
                MapiNamedProperty has = Addr(0x8015);
                _m.Properties.Set(OxVCardTables.Named(IdFor(has), has, MapiType.Boolean, [1]));
                done = true;
            }

        }

        // ---- dates ------------------------------------------------------------------------------------------------------

        private void ImportDates()
        {
            if (card.FindProperty("BDAY") is { } b)
            {
                SetEvent(b, 0x3A42, 0x80DE);
            }

            ContentProperty? ann = card.FindProperty("X-MS-ANNIVERSARY") ?? card.FindProperty("X-ANNIVERSARY") ?? card.FindProperty("ANNIVERSARY");
            if (ann is not null)
            {
                SetEvent(ann, 0x3A41, 0x80DF);
            }
        }

        private void SetEvent(ContentProperty p, ushort tag, uint localLid)
        {
            _used.Add(p);
            if (VCardDate.TryParse(p.Value) is not { } d || d.Year is not int y || d.Month is not int mo || d.Day is not int day)
            {
                // A date without a year (--1022) or free text (circa 1800) has no PtypTime form.
                Drop(p, "PartialDate", "A contact's birthday and anniversary need a year, a month and a day.");
                return;
            }

            if (y < 1601 || mo is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(Math.Min(y, 9999), mo) || y > 9999)
            {
                Drop(p, "ValueOutOfRange", "The date does not exist or is outside what a FILETIME holds.");
                return;
            }

            var date = new DateOnly(y, mo, day);
            if (d.Hour is not null && !(d.Hour == 0 && (d.Minute ?? 0) == 0 && (d.Second ?? 0) == 0))
            {
                report.Add(OxVCardTables.Family, p.Name.ToUpperInvariant(), LossKind.Approximated, "TimeOfDayDropped", "Only the date is kept; the time of day is not.");
            }

            _m.Properties.Set(MapiProps.Time(tag, OxVCardTables.FileTimeOf(date, OxVCardTables.EventTimeUtc)));
            NamedTime(localLid, OxVCardTables.FileTimeOf(date, TimeSpan.Zero));
        }

        // ---- addresses ------------------------------------------------------------------------------------------------

        private readonly Dictionary<ContactSlot, bool> _addressTaken = [];

        private enum ContactSlot { Home, Business, Other }

        private void ImportAddresses()
        {
            ContactSlot? mailing = null;
            foreach (VCardAddress a in _v.Addresses)
            {
                _used.Add(a.Property);
                List<string> types = a.Types.Select(t => t.ToLowerInvariant()).ToList();
                List<ContactSlot> slots = [];
                ContactSlot? firstListed = null;
                foreach (string t in types)
                {
                    ContactSlot? s = t == "work" ? ContactSlot.Business : t == "home" ? ContactSlot.Home : OxVCardTables.IsOtherAddressType(t) ? ContactSlot.Other : null;
                    if (s is { } slot)
                    {
                        firstListed ??= slot;
                        if (!slots.Contains(slot))
                        {
                            slots.Add(slot);
                        }
                    }
                }

                if (slots.Count == 0)
                {
                    // 2.1.3.3.1: the default TYPE is "intl, postal, parcel, work".
                    slots = [ContactSlot.Business, ContactSlot.Other];
                    firstListed = ContactSlot.Business;
                }

                List<ContactSlot> filled = [];
                foreach (ContactSlot slot in slots)
                {
                    if (_addressTaken.TryAdd(slot, true))
                    {
                        SetAddress(slot, a);
                        filled.Add(slot);
                    }
                }

                if (filled.Count == 0)
                {
                    Drop(a.Property, "AdditionalValueNotMapped", "The contact has one home, one business and one other address; they were already filled.");
                }
                else if (types.Contains("pref") && mailing is null)
                {
                    // The first type the card lists names the address; if that one was already filled by an earlier ADR, the first one this ADR filled.
                    ContactSlot wanted = firstListed ?? slots[0];
                    mailing = filled.Contains(wanted) ? wanted : filled[0];
                }
            }

            if (mailing is null && card.FindProperty("X-MS-OL-DEFAULT-POSTAL-ADDRESS") is { } dp)
            {
                if (int.TryParse(dp.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int id) && id is >= 1 and <= 3)
                {
                    mailing = id switch { 1 => ContactSlot.Home, 2 => ContactSlot.Business, _ => ContactSlot.Other };
                }
            }

            MarkUsed("X-MS-OL-DEFAULT-POSTAL-ADDRESS");
            if (mailing is { } sel && _addressTaken.ContainsKey(sel))
            {
                // 2.2.1.3.9: PidLidPostalAddressId selects the mailing address, and the mailing address properties repeat it.
                NamedInt(0x8022, sel switch { ContactSlot.Home => 1, ContactSlot.Business => 2, _ => 3 });
                CopyMailing(sel);
            }
        }

        private string Part(MapiNamedProperty? n, ushort tag) =>
            (n is { } nn ? _m.Properties.Get(nn)?.AsString() : _m.Properties.Get(tag)?.AsString()) ?? string.Empty;

        private void SetAddress(ContactSlot slot, VCardAddress a)
        {
            string street = Join(a.Street);
            if (a.Extended.Count > 0)
            {
                street = street.Length == 0 ? Join(a.Extended) : Join(a.Extended) + "\r\n" + street;
                report.Add(OxVCardTables.Family, "ADR", LossKind.Approximated, "AddressExtendedMerged", "The extended address (apartment, suite) is stored in the street field, which has no separate part.");
            }

            (string?, string?, string?, string?, string?, string?) v = (Join(a.PoBox), street, Join(a.Locality), Join(a.Region), Join(a.PostalCode), Join(a.Country));
            switch (slot)
            {
                case ContactSlot.Home:
                    Str(0x3A5E, v.Item1);
                    Str(0x3A5D, v.Item2);
                    Str(0x3A59, v.Item3);
                    Str(0x3A5C, v.Item4);
                    Str(0x3A5B, v.Item5);
                    Str(0x3A5A, v.Item6);
                    break;
                case ContactSlot.Business:
                    NamedStr(0x804A, v.Item1);
                    NamedStr(0x8045, v.Item2);
                    NamedStr(0x8046, v.Item3);
                    NamedStr(0x8047, v.Item4);
                    NamedStr(0x8048, v.Item5);
                    NamedStr(0x8049, v.Item6);
                    break;
                default:
                    Str(0x3A64, v.Item1);
                    Str(0x3A63, v.Item2);
                    Str(0x3A5F, v.Item3);
                    Str(0x3A62, v.Item4);
                    Str(0x3A61, v.Item5);
                    Str(0x3A60, v.Item6);
                    break;
            }
        }

        private void CopyMailing(ContactSlot slot)
        {
            (ushort Mail, ushort Home, ushort Other, uint Work)[] map =
            [
                (0x3A2B, 0x3A5E, 0x3A64, 0x804A), (0x3A29, 0x3A5D, 0x3A63, 0x8045), (0x3A27, 0x3A59, 0x3A5F, 0x8046),
                (0x3A28, 0x3A5C, 0x3A62, 0x8047), (0x3A2A, 0x3A5B, 0x3A61, 0x8048), (0x3A26, 0x3A5A, 0x3A60, 0x8049),
            ];
            var vals = new List<string>();
            foreach ((ushort mail, ushort home, ushort other, uint work) in map)
            {
                string v = slot switch
                {
                    ContactSlot.Home => Part(null, home),
                    ContactSlot.Other => Part(null, other),
                    _ => Part(Addr(work), 0),
                };
                vals.Add(v);
                Str(mail, v);
            }

            // PidTagPostalAddress is the mailing address as one display string.
            string region = string.Join(' ', new[] { string.Join(", ", new[] { vals[2], vals[3] }.Where(s => s.Length > 0)), vals[4] }.Where(s => s.Length > 0));
            string postal = string.Join("\r\n", new[] { vals[1], region, vals[5] }.Where(s => s.Length > 0));
            Str(0x3A15, postal);
        }

        // ---- telephones ----------------------------------------------------------------------------------------------

        private readonly HashSet<ContactPhoneKind> _phoneTaken = [];

        private void SetPhone(ContentProperty p, ContactPhoneKind kind, string number)
        {
            ushort id = ContactTags.PhoneTags.First(t => t.Kind == kind).Id;
            if (_phoneTaken.Add(kind))
            {
                Str(id, number);
            }
            else
            {
                Drop(p, "AdditionalValueNotMapped", "The contact's " + kind + " telephone number was already set.");
            }
        }

        private static string StripTel(string v) => v.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ? v[4..] : v;

        private void ImportPhones()
        {
            int homes = 0, works = 0;
            bool otherDone = false;
            foreach (VCardTyped t in _v.Phones)
            {
                ContentProperty p = t.Property;
                _used.Add(p);
                string number = StripTel(t.Value);
                if (number.Length == 0)
                {
                    continue;
                }

                var types = t.Types.Select(x => x.ToLowerInvariant()).ToHashSet();
                bool pref = t.Preference is not null || types.Contains("pref");
                bool placed = false;

                // 2.1.3.4.1 TEL table. Every rule that applies sets its property: the specification's example puts "VOICE,MSG,WORK" in both the business and the other number.
                if (types.Contains("fax"))
                {
                    ContactPhoneKind k = types.Contains("home") ? ContactPhoneKind.HomeFax : types.Contains("work") ? ContactPhoneKind.BusinessFax : ContactPhoneKind.PrimaryFax;
                    SetPhone(p, k, number);
                    continue;
                }

                if (types.Contains("home"))
                {
                    SetPhone(p, ++homes == 1 ? ContactPhoneKind.Home : ContactPhoneKind.Home2, number);
                    placed = true;
                }

                if (types.Contains("work"))
                {
                    SetPhone(p, ++works == 1 ? ContactPhoneKind.Business : ContactPhoneKind.Business2, number);
                    placed = true;
                }

                foreach ((string word, ContactPhoneKind kind) in new[] { ("cell", ContactPhoneKind.Mobile), ("pager", ContactPhoneKind.Pager), ("car", ContactPhoneKind.Car), ("isdn", ContactPhoneKind.Isdn) })
                {
                    if (types.Contains(word))
                    {
                        SetPhone(p, kind, number);
                        placed = true;
                    }
                }

                if (types.Contains("pcs"))
                {
                    report.Add(OxVCardTables.Family, "TEL", LossKind.Dropped, "NoContactMapping", "MS-OXVCARD 2.1.3.4.1 drops the pcs type.");
                    placed = true;
                }

                if (pref)
                {
                    SetPhone(p, ContactPhoneKind.Primary, number);
                    placed = true;
                }

                bool voice = types.Overlaps(["msg", "voice", "video", "bbs", "modem"]) || !placed;
                if (voice)
                {
                    if (!otherDone)
                    {
                        SetPhone(p, ContactPhoneKind.Other, number);
                        otherDone = true;
                    }
                    else if (!placed)
                    {
                        Drop(p, "AdditionalValueNotMapped", "MS-OXVCARD 2.1.3.4.1 imports the first voice number only.");
                    }
                }
            }

            foreach (ContentProperty p in card.FindProperties("X-MS-TEL"))
            {
                _used.Add(p);
                string number = _v.GetText(p);
                ContactPhoneKind? k = (p.GetParameter("TYPE") ?? (VCardView.GetTypes(p) is { Count: > 0 } ts ? ts[0] : null))?.ToUpperInvariant() switch
                {
                    "ASSISTANT" => ContactPhoneKind.Assistant,
                    "CALLBACK" => ContactPhoneKind.Callback,
                    "COMPANY" => ContactPhoneKind.Company,
                    "RADIO" => ContactPhoneKind.Radio,
                    "TTYTDD" => ContactPhoneKind.TtyTdd,
                    _ => null,
                };
                if (k is { } kind)
                {
                    SetPhone(p, kind, number);
                }
                else
                {
                    Drop(p, "NoContactMapping", "The X-MS-TEL type is not one MS-OXVCARD 2.1.3.9.5 lists.");
                }
            }
        }

        // ---- email ----------------------------------------------------------------------------------------------------

        private void ImportEmails()
        {
            int slot = 0;
            bool imDone = false;
            bool telexDone = false;
            foreach (VCardTyped e in _v.Emails)
            {
                ContentProperty p = e.Property;
                _used.Add(p);
                string address = e.Value;
                if (address.Length == 0)
                {
                    continue;
                }

                // 2.1.3.4.2: the first recognised TYPE decides; with none recognised it is "internet".
                string kind = e.Types.Select(x => x.ToLowerInvariant()).FirstOrDefault(x => x is "internet" or "im" or "tlx") ?? "internet";
                if (kind == "im")
                {
                    if (imDone)
                    {
                        Drop(p, "AdditionalValueNotMapped", "The contact holds one instant messaging address.");
                    }
                    else
                    {
                        NamedStr(0x8062, address);
                        imDone = true;
                    }
                }
                else if (kind == "tlx")
                {
                    if (telexDone)
                    {
                        Drop(p, "AdditionalValueNotMapped", "The contact holds one telex number.");
                    }
                    else
                    {
                        Str(0x3A2C, address);
                        telexDone = true;
                    }
                }
                else if (slot >= 3)
                {
                    Drop(p, "AdditionalValueNotMapped", "The contact holds three email addresses; later ones are ignored (MS-OXVCARD 2.1.3.4.2).");
                }
                else
                {
                    slot++;
                    uint b = slot == 3 ? 0x80A0u : 0x8080u + (uint)((slot - 1) * 0x10);
                    NamedStr(b, _fn.Length > 0 ? _fn + " (" + address + ")" : address);
                    NamedStr(b + 2, "SMTP");
                    NamedStr(b + 3, address);
                }
            }

            foreach (ContentProperty p in card.FindProperties("X-MS-IMADDRESS").Concat(card.FindProperties("X-MS-RM-IMACCOUNT")).Concat(card.FindProperties("IMPP")))
            {
                string? t = Text(p);
                if (t is null)
                {
                    continue;
                }

                if (!imDone)
                {
                    NamedStr(0x8062, t);
                    imDone = true;
                }
                else
                {
                    Drop(p, "AdditionalValueNotMapped", "The contact holds one instant messaging address.");
                }
            }
        }

        // ---- the rest -------------------------------------------------------------------------------------------------

        private void ImportOrganization()
        {
            ContentProperty? p = card.FindProperty("ORG");
            if (p is null)
            {
                return;
            }

            _used.Add(p);
            IReadOnlyList<string> org = _v.Organization;
            Str(0x3A16, org.Count > 0 && org[0].Length > 0 ? org[0] : null);
            string[] units = org.Skip(1).Where(u => u.Length > 0).ToArray();
            if (units.Length > 0)
            {
                Str(0x3A18, string.Join(", ", units));
                if (units.Length > 1)
                {
                    report.Add(OxVCardTables.Family, "ORG", LossKind.Approximated, "OrgUnitsMerged", "The contact has one department field; the units are joined with a comma.");
                }
            }

            foreach (ContentProperty extra in card.FindProperties("ORG").Skip(1))
            {
                Drop(extra, "AdditionalValueNotMapped", "The contact holds one organization.");
            }
        }

        private void ImportCategories()
        {
            List<string> cats = [];
            foreach (ContentProperty p in card.FindProperties("CATEGORIES"))
            {
                _used.Add(p);
                cats.AddRange(ContentValues.SplitUnescaped(p.GetDecodedValue(), ',').Select(x => _v.Unescape(x)).Where(x => x.Length > 0));
            }

            if (cats.Count > 0)
            {
                MapiNamedProperty kw = OxVCardTables.Keywords;
                _m.Properties.Set(MapiProps.NamedStrings(IdFor(kw), kw, cats));
            }
        }

        private void ImportUrls()
        {
            bool home = false, work = false;
            foreach (VCardTyped u in _v.Urls)
            {
                ContentProperty p = u.Property;
                _used.Add(p);
                string url = u.Value;
                if (url.Length == 0)
                {
                    continue;
                }

                var types = u.Types.Select(x => x.ToLowerInvariant()).ToHashSet();
                // 2.1.3.7.8: home, work; with no type the first is the personal page and one more is the business page.
                bool? isWork = types.Contains("work") ? true : types.Contains("home") ? false : null;
                if (isWork is null)
                {
                    isWork = home;
                }

                if (isWork == true && !work)
                {
                    Str(0x3A51, url);
                    work = true;
                }
                else if (isWork == false && !home)
                {
                    Str(0x3A50, url);
                    home = true;
                }
                else
                {
                    Drop(p, "AdditionalValueNotMapped", "The contact holds one personal and one business web page.");
                }
            }
        }

        private void ImportClass()
        {
            // 2.1.3.8.1: Public 0, Private 2, Confidential 3; anything else, or no CLASS, is 0.
            int v = 0;
            if (card.FindProperty("CLASS") is { } p)
            {
                _used.Add(p);
                v = p.Value.Trim().ToUpperInvariant() switch { "PRIVATE" => 2, "CONFIDENTIAL" => 3, _ => 0 };
            }

            _m.Properties.Set(MapiProps.Int(ContactTags.Sensitivity, v));
        }

        private void ImportRevision()
        {
            if (card.FindProperty("REV") is { } p)
            {
                _used.Add(p);
                if (_v.RevisionTime is { } t && MapiProps.FitsFileTime(t))
                {
                    _m.Properties.Set(MapiProps.Time(ContactTags.LastModificationTime, t));
                }
                else
                {
                    Drop(p, "ValueOutOfRange", "The revision is not a date and time.");
                }
            }
        }

        private void ImportKeys()
        {
            var certs = new List<byte[]>();
            foreach (VCardMedia k in _v.Keys)
            {
                _used.Add(k.Property);
                bool x509 = (k.MediaType?.Contains("x509", StringComparison.OrdinalIgnoreCase) ?? false) || (k.MediaType?.Contains("pkix-cert", StringComparison.OrdinalIgnoreCase) ?? false)
                    || (k.MediaType?.Contains("x-x509", StringComparison.OrdinalIgnoreCase) ?? false);
                if (x509 && k.Data is { Length: > 0 } d)
                {
                    certs.Add(d);
                }
                else
                {
                    Drop(k.Property, "KeyNotX509", "MS-OXVCARD 2.1.3.8.2 imports X.509 certificates only.");
                }
            }

            if (certs.Count > 0)
            {
                _m.Properties.Set(MapiProperty.CreateMulti(new MapiTag(ContactTags.UserX509Certificate, MapiType.MultipleBinary), certs));
            }
        }

        private void ImportCustom()
        {
            // 2.1.3.9.2: X-MS-CHILD and X-CHILD, one name each.
            List<string> kids = [];
            foreach (ContentProperty p in card.FindProperties("X-MS-CHILD").Concat(card.FindProperties("X-CHILD")))
            {
                if (Text(p) is { } t)
                {
                    kids.Add(t);
                }
            }

            if (kids.Count > 0)
            {
                _m.Properties.Set(MapiProperty.CreateMulti(new MapiTag(0x3A58, MapiType.MultipleString), kids.Select(OxVCardTables.Utf16).ToList()));
            }

            // 2.1.3.9.3: up to four X-MS-TEXT (or X-CUSTOM) fields, in order; the slot an empty one holds stays empty.
            int slot = 0;
            foreach (ContentProperty p in card.FindProperties("X-MS-TEXT").Concat(card.FindProperties("X-CUSTOM")))
            {
                _used.Add(p);
                if (slot >= 4)
                {
                    Drop(p, "AdditionalValueNotMapped", "MS-OXVCARD 2.1.3.9.3 keeps four user text fields.");
                    continue;
                }

                string t = _v.GetText(p);
                if (t.Length > 0)
                {
                    NamedStr(0x804Fu + (uint)slot, t);
                }

                slot++;
            }
        }

        private void ImportGender()
        {
            if (card.FindProperty("GENDER") is { } p)
            {
                _used.Add(p);
                string sex = _v.Gender?.Sex ?? string.Empty;
                if (sex is "F" or "M")
                {
                    if (_v.Gender?.Identity is not null)
                    {
                        report.Add(OxVCardTables.Family, "GENDER", LossKind.Approximated, "GenderIdentityDropped", "The contact's gender is female or male; the free-text identity is not kept.");
                    }

                    _m.Properties.Set(MapiProperty.CreateOwned(new MapiTag(0x3A4D, MapiType.Integer16), BitConverter.GetBytes((short)(sex == "F" ? 1 : 2))));
                }
                else
                {
                    Drop(p, "NoContactMapping", "A contact's gender is female or male only.");
                }
            }
        }
    }
}
