using System.Globalization;
using System.Text;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.ContentLines;
using RecursiveAI.OneMail.Calendar.VCard;
using RecursiveAI.OneMail.Contacts;
using RecursiveAI.OneMail.Mapi;

namespace RecursiveAI.OneMail.Conversion.OxVCard;

/// <summary>Options for <see cref="ContactToVCardConverter"/>.</summary>
public sealed class ContactToVCardOptions
{
    /// <summary>The vCard version to write: 3.0 (the default, what MS-OXVCARD specifies) or 4.0. Any other value is written as 3.0.</summary>
    public VCardVersion Version { get; init; } = VCardVersion.V30;

    /// <summary>Whether to write the contact photo. Default true. A photo can be large; turn it off for a text-only card.</summary>
    public bool IncludePhoto { get; init; } = true;
}

/// <summary>
/// Converts a contact item (<c>IPM.Contact</c>) or a personal distribution list to a vCard following MS-OXVCARD 2.1.3. The result is a
/// <c>VCARD</c> <see cref="Component"/>; add it to a <see cref="ContentDocument"/> to write it. Every property of the item is either
/// written, or listed in the loss report: <c>Dropped</c> when MS-OXVCARD maps it nowhere, <c>Internal</c> for store bookkeeping.
/// </summary>
/// <remarks>
/// <para>Text is escaped as vCard 3.0 and 4.0 require; a CR LF in a value becomes <c>\n</c>. Comma and semicolon inside a name or address part are escaped, so
/// they survive; <see cref="VCardToContactConverter"/> reads them back. Never throws for odd content. Dates before 1601 or after 9999 do not occur in the item's FILETIME values and are not written.</para>
/// <para>Codes in the report: <c>NoVCardMapping</c>, <c>StoreBookkeeping</c>, <c>DerivedValue</c>, <c>NotInThisVersion</c>, <c>AdditionalValuesNotCarried</c>,
/// <c>ExchangeAddressNotExported</c>, <c>EmptyValueNotWritten</c>, <c>PhoneSlotChanged</c>, <c>EmailSlotChanged</c>, <c>DefaultValueNotWritten</c>, <c>UnsupportedPhotoType</c>, <c>AttachmentNotCarried</c>, <c>MemberNotCarried</c>.</para>
/// </remarks>
public sealed class ContactToVCardConverter : IItemConversion<MapiMessage, Component>
{
    private readonly ContactToVCardOptions _options;

    /// <summary>Creates a converter.</summary>
    /// <param name="options">Options, or null for the defaults.</param>
    public ContactToVCardConverter(ContactToVCardOptions? options = null) => _options = options ?? new ContactToVCardOptions();

    /// <inheritdoc/>
    public ConversionResult<Component> Convert(MapiMessage source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var report = new LossReport();
        Component card = new Exporter(source, _options.Version == VCardVersion.V40, _options.IncludePhoto, report).Run();
        return new ConversionResult<Component>(card, report);
    }

    private sealed class Exporter(MapiMessage m, bool v40, bool includePhoto, LossReport report)
    {
        private readonly MapiContact _c = new(m);
        private readonly Component _card = new("VCARD");

        private ContentProperty Add(string name, string value, params (string Name, string[] Values)[] parameters)
        {
            var p = new ContentProperty(name, value);
            foreach ((string n, string[] vs) in parameters)
            {
                p.Parameters.Add(new ContentParameter(n, vs));
            }

            _card.Add(p);
            return p;
        }

        private static string E(string s) => ContentValues.Escape(s);

        // vCard 3.0 writes the preferred flag as a TYPE word, 4.0 as PREF=1 (RFC 6350 5.3).
        private (string, string[])[] Typed(bool pref, params string[] types)
        {
            List<string> t = [.. types];
            var ps = new List<(string, string[])>();
            if (pref && !v40)
            {
                t.Add("pref");
            }

            if (t.Count > 0)
            {
                ps.Add(("TYPE", [.. t]));
            }

            if (pref && v40)
            {
                ps.Add(("PREF", ["1"]));
            }

            return [.. ps];
        }

        private void Drop(string name, string code, string reason, string path = "") => report.Add(OxVCardTables.MapiFamily, name, LossKind.Dropped, code, reason, path);

        public Component Run()
        {
            bool isList = MapiMailItem.KindOf(m.MessageClass) == MailItemKind.DistributionList;
            if (!isList && !MapiContact.IsContactClass(m.MessageClass))
            {
                report.Add(OxVCardTables.MapiFamily, "PidTagMessageClass", LossKind.Internal, "NotAContact", "The item is not a contact or a distribution list; its contact properties were converted anyway.");
            }

            Add("VERSION", v40 ? "4.0" : "3.0");
            Add("PRODID", OxVCardTables.ProductId);
            string fn = isList ? new MapiDistributionList(m).Name ?? string.Empty : FormattedName();
            Add("FN", E(fn));
            if (isList)
            {
                ExportList();
            }
            else
            {
                ExportContact(fn);
            }

            ReportRemainder(fn, isList);
            return _card;
        }

        private string FormattedName()
        {
            // 2.1.3.2.1: FN comes from PidTagDisplayName, else PidTagNormalizedSubject.
            if (_c.DisplayName is { } d)
            {
                return d;
            }

            if (m.GetString(ContactTags.NormalizedSubject) is { Length: > 0 } ns)
            {
                return ns;
            }

            // Neither is set: build a name from the parts so that the card still has the FN vCard requires.
            return string.Join(' ', new[] { _c.NamePrefix, _c.GivenName, _c.MiddleName, _c.Surname, _c.NameSuffix }.Where(s => !string.IsNullOrEmpty(s)));
        }

        private void ExportList()
        {
            var list = new MapiDistributionList(m);
            if (!v40)
            {
                if (list.Members.Count > 0)
                {
                    Drop("PidLidDistributionListMembers", "NotInThisVersion", "vCard 3.0 has no group; write version 4.0 to keep the members as MEMBER properties.");
                }

                return;
            }

            // RFC 6350 6.1.4 and 6.6.5: a group card has KIND:group and a MEMBER URI per member.
            Add("KIND", "group");
            foreach (DistributionListMember member in list.Members)
            {
                if (member.SmtpAddress is { } a)
                {
                    Add("MEMBER", "mailto:" + a);
                }
                else
                {
                    report.Add(OxVCardTables.MapiFamily, "PidLidDistributionListMembers", LossKind.Dropped, "MemberNotCarried", "A member without an SMTP address (" + member.Kind + ") has no mailto: form.");
                }
            }
        }

        private void ExportContact(string fn)
        {
            // N (2.1.3.2.2): family; given; middle; prefixes; suffixes.
            string[] n = [_c.Surname ?? string.Empty, _c.GivenName ?? string.Empty, _c.MiddleName ?? string.Empty, _c.NamePrefix ?? string.Empty, _c.NameSuffix ?? string.Empty];
            if (n.Any(s => s.Length > 0))
            {
                Add("N", string.Join(';', n.Select(E)));
            }

            if (_c.Nickname is { } nick)
            {
                Add("NICKNAME", E(nick));
            }

            ExportPhoto();
            ExportDates();
            ExportAddresses();
            ExportPhones();
            ExportEmails();

            if (_c.JobTitle is { } title)
            {
                Add("TITLE", E(title));
            }

            if (_c.Profession is { } role)
            {
                Add("ROLE", E(role));
            }

            if (_c.CompanyName is not null || _c.Department is not null)
            {
                Add("ORG", E(_c.CompanyName ?? string.Empty) + (_c.Department is { } dep ? ";" + E(dep) : string.Empty));
            }

            if (_c.Categories.Count > 0)
            {
                Add("CATEGORIES", string.Join(',', _c.Categories.Select(E)));
            }

            if (_c.Notes is { } note)
            {
                Add("NOTE", E(note));
            }

            if (_c.PersonalHomePage is { } home)
            {
                Add("URL", E(home), ("TYPE", ["home"]));
            }

            if (_c.BusinessHomePage is { } work)
            {
                Add("URL", E(work), ("TYPE", ["work"]));
            }

            if (_c.FreeBusyUrl is { } fb)
            {
                Add("FBURL", E(fb));
            }

            if (m.GetTime(ContactTags.LastModificationTime) is { } rev)
            {
                Add("REV", rev.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
            }

            ExportClass();
            foreach (byte[] cert in _c.X509Certificates)
            {
                string b64 = System.Convert.ToBase64String(cert);
                if (v40)
                {
                    Add("KEY", "data:application/pkix-cert;base64," + b64);
                }
                else
                {
                    Add("KEY", b64, ("ENCODING", ["b"]), ("TYPE", ["X509"]));
                }
            }

            // The Outlook extensions (2.1.3.9).
            foreach (string child in _c.ChildrensNames)
            {
                Add("X-MS-CHILD", E(child));
            }

            IReadOnlyList<string?> fields = _c.UserFields;
            int last = -1;
            for (int i = 0; i < fields.Count; i++)
            {
                if (fields[i] is not null)
                {
                    last = i;
                }
            }

            for (int i = 0; i <= last; i++)
            {
                // Empty slots are written so that the fields keep their positions: import fills them in order.
                Add("X-MS-TEXT", E(fields[i] ?? string.Empty));
            }

            if (_c.InstantMessagingAddress is { } im)
            {
                Add("X-MS-IMADDRESS", E(im));
            }

            if (_c.SpouseName is { } spouse)
            {
                Add("X-MS-SPOUSE", E(spouse), ("TYPE", ["N"]));
            }

            if (_c.ManagerName is { } mgr)
            {
                Add("X-MS-MANAGER", E(mgr), ("TYPE", ["N"]));
            }

            if (_c.AssistantName is { } asst)
            {
                Add("X-MS-ASSISTANT", E(asst), ("TYPE", ["N"]));
            }

            if (_c.Hobbies is { } hobbies)
            {
                Add("X-MS-INTERESTS", E(hobbies));
            }

            if (_c.MailingAddress == ContactMailingAddress.None && m.Properties.Get(new MapiNamedProperty(MapiPropertySets.Address, 0x8022u)) is { } none)
            {
                report.Add(OxVCardTables.MapiFamily, OxVCardTables.Label(none), LossKind.Internal, "DefaultValueNotWritten", "No mailing address is selected; the card has no property for that and import leaves the selection unset.");
            }

            if (_c.MailingAddress != ContactMailingAddress.None)
            {
                Add("X-MS-OL-DEFAULT-POSTAL-ADDRESS", ((int)_c.MailingAddress).ToString(CultureInfo.InvariantCulture));
            }

            if (_c.Gender != ContactGender.Unspecified)
            {
                if (v40)
                {
                    Add("GENDER", _c.Gender == ContactGender.Female ? "F" : "M");
                }
                else
                {
                    Drop("PidTagGender", "NotInThisVersion", "vCard 3.0 has no GENDER property; write version 4.0 to keep it.");
                }
            }

            _ = fn;
        }

        private void ExportClass()
        {
            if (m.Properties.Get(ContactTags.Sensitivity, MapiType.Integer32)?.AsInt32() is not int s)
            {
                return;
            }

            // 2.1.3.8.1: Public 0, Private 2, Confidential 3. CLASS is a vCard 3.0 property and was dropped from 4.0.
            string? word = s switch { 0 => "PUBLIC", 2 => "PRIVATE", 3 => "CONFIDENTIAL", _ => null };
            if (word is null)
            {
                Drop("PidTagSensitivity", "NoVCardMapping", "MS-OXVCARD maps only the values Public, Private and Confidential.");
            }
            else if (v40)
            {
                Drop("PidTagSensitivity", "NotInThisVersion", "vCard 4.0 has no CLASS property.");
            }
            else
            {
                Add("CLASS", word);
            }
        }

        private void ExportPhoto()
        {
            if (!includePhoto)
            {
                return;
            }

            MapiAttachment? a = _c.PhotoAttachment;
            if (a is null)
            {
                return;
            }

            byte[] data;
            using (Stream? s = a.OpenData())
            {
                if (s is null)
                {
                    return;
                }

                using var ms = new MemoryStream();
                s.CopyTo(ms);
                data = ms.ToArray();
            }

            string index = m.Attachments.ToList().IndexOf(a).ToString(CultureInfo.InvariantCulture);
            string? type = OxVCardTables.ImageTypeOf(a.GetString(ContactTags.AttachExtension), data);
            if (type is null || data.Length == 0)
            {
                Drop("PidTagAttachDataBinary", "UnsupportedPhotoType", "MS-OXVCARD 2.1.3.2.4 exports bmp, gif, jpeg and png photos only.", "attachment[" + index + "]");
                return;
            }

            string b64 = System.Convert.ToBase64String(data);
            if (v40)
            {
                Add("PHOTO", "data:image/" + type + ";base64," + b64);
            }
            else
            {
                Add("PHOTO", b64, ("ENCODING", ["b"]), ("TYPE", [type.ToUpperInvariant()]));
            }
        }

        private void ExportDates()
        {
            // 2.1.3.2.5: BDAY from PidTagBirthday. 3.0 writes the extended form, 4.0 the basic form.
            if (_c.BirthDate is { } b)
            {
                Add("BDAY", OxVCardTables.FormatDate(b, !v40));
            }

            if (_c.AnniversaryDate is { } a)
            {
                // 2.1.3.9.6 names X-MS-ANNIVERSARY; vCard 4.0 has ANNIVERSARY itself (RFC 6350 6.2.6).
                Add(v40 ? "ANNIVERSARY" : "X-MS-ANNIVERSARY", OxVCardTables.FormatDate(a, !v40));
            }
        }

        private void ExportAddresses()
        {
            ContactMailingAddress mailing = _c.MailingAddress;
            foreach (ContactAddressKind kind in new[] { ContactAddressKind.Business, ContactAddressKind.Home, ContactAddressKind.Other })
            {
                ContactAddress a = _c.Address(kind);
                if (a.IsEmpty)
                {
                    continue;
                }

                bool pref = (int)mailing == (kind switch { ContactAddressKind.Home => 1, ContactAddressKind.Business => 2, _ => 3 });
                string word = kind switch { ContactAddressKind.Home => "home", ContactAddressKind.Business => "work", _ => "postal" };
                // 2.1.3.3.1: PO box; extended address; street; locality; region; postal code; country. The extended part is empty: Outlook keeps one street field.
                string value = string.Join(';', new[] { a.PostOfficeBox, null, a.Street, a.City, a.State, a.PostalCode, a.Country }.Select(s => E(s ?? string.Empty)));
                Add("ADR", value, Typed(pref, word));
                if (!v40)
                {
                    // 2.1.3.3.2: LABEL is built from the same fields.
                    Add("LABEL", E(Label(a)), Typed(pref, word));
                }
            }
        }

        private static string Label(ContactAddress a)
        {
            var lines = new List<string>();
            if (!string.IsNullOrEmpty(a.Street))
            {
                lines.Add(a.Street);
            }

            string region = string.Join(' ', new[] { string.Join(", ", new[] { a.City, a.State }.Where(s => !string.IsNullOrEmpty(s))), a.PostalCode }.Where(s => !string.IsNullOrEmpty(s)));
            if (region.Length > 0)
            {
                lines.Add(region);
            }

            if (!string.IsNullOrEmpty(a.Country))
            {
                lines.Add(a.Country);
            }

            return string.Join("\r\n", lines);
        }

        private void ExportPhones()
        {
            foreach (ContactPhone p in _c.Phones)
            {
                string[]? types = p.Kind switch
                {
                    ContactPhoneKind.Business or ContactPhoneKind.Business2 => ["work"],
                    ContactPhoneKind.Home or ContactPhoneKind.Home2 => ["home"],
                    ContactPhoneKind.Mobile => ["cell"],
                    ContactPhoneKind.Car => ["car"],
                    ContactPhoneKind.Pager => ["pager"],
                    ContactPhoneKind.Isdn => ["isdn"],
                    ContactPhoneKind.HomeFax => ["home", "fax"],
                    ContactPhoneKind.BusinessFax => ["work", "fax"],
                    ContactPhoneKind.PrimaryFax => ["fax"],
                    ContactPhoneKind.Other => ["voice"],
                    ContactPhoneKind.Primary => [],
                    _ => null,
                };
                if (p.Kind is ContactPhoneKind.Business2 or ContactPhoneKind.Home2 && _c.Phone(p.Kind == ContactPhoneKind.Business2 ? ContactPhoneKind.Business : ContactPhoneKind.Home) is null)
                {
                    // Import numbers the first work (home) telephone as the business (home) one and the second as the second, so a lone second number moves up.
                    ushort id = ContactTags.PhoneTags.First(t => t.Kind == p.Kind).Id;
                    report.Add(OxVCardTables.MapiFamily, MapiPropertyNames.GetName(id) ?? "0x" + id.ToString("X4", CultureInfo.InvariantCulture), LossKind.Approximated, "PhoneSlotChanged",
                        "The contact has a second number but no first one of its type; a vCard has no order to keep, so import files it as the first.");
                }

                if (types is not null)
                {
                    Add("TEL", E(p.Number), Typed(p.Kind == ContactPhoneKind.Primary, types));
                    continue;
                }

                string? x = p.Kind switch
                {
                    ContactPhoneKind.Company => "COMPANY",
                    ContactPhoneKind.Assistant => "ASSISTANT",
                    ContactPhoneKind.Callback => "CALLBACK",
                    ContactPhoneKind.Radio => "RADIO",
                    ContactPhoneKind.TtyTdd => "TTYTDD",
                    _ => null,
                };
                if (x is not null)
                {
                    Add("X-MS-TEL", E(p.Number), ("TYPE", [x]));
                }
                else if (p.Kind == ContactPhoneKind.Telex)
                {
                    // 2.1.3.4.2 imports EMAIL;TYPE=tlx as the telex number; the same form is written so that it comes back.
                    Add("EMAIL", E(p.Number), ("TYPE", ["tlx"]));
                }
            }
        }

        private void ExportEmails()
        {
            bool first = true;
            int written = 0;
            foreach (ContactElectronicAddress e in _c.EmailAddresses)
            {
                if (e.SmtpAddress is not { } smtp)
                {
                    if (e.Address is not null)
                    {
                        string why = "The address type is " + (e.AddressType ?? "unknown") + ", and the item has no SMTP form of it.";
                        Drop("Email" + e.Slot.ToString(CultureInfo.InvariantCulture) + "EmailAddress", "ExchangeAddressNotExported", why);
                        ReportSlot(e.Slot, LossKind.Dropped, "ExchangeAddressNotExported", why);
                    }

                    continue;
                }

                // Import fills the email slots in card order, so an address whose slot is not its place among the written ones moves.
                written++;
                if (e.Slot != written)
                {
                    ReportSlot(e.Slot, LossKind.Approximated, "EmailSlotChanged", "An earlier slot is empty or holds an address that is not written, and a vCard has no slot order, so import files this address in slot " + written.ToString(CultureInfo.InvariantCulture) + ".");
                }

                // 2.1.3.4.2: TYPE=internet; the first address is the preferred one.
                Add("EMAIL", E(smtp), Typed(first, "internet"));
                first = false;
            }
        }

        // The address type and address properties of an email slot (MS-OXOCNTC: 0x8082/0x8083, 0x8092/0x8093, 0x80A2/0x80A3).
        private void ReportSlot(int slot, LossKind kind, string code, string reason)
        {
            uint b = 0x8080u + (uint)((slot - 1) * 0x10);
            foreach (uint lid in new[] { b + 2, b + 3 })
            {
                if (m.Properties.Get(new MapiNamedProperty(MapiPropertySets.Address, lid)) is { } p)
                {
                    report.Add(OxVCardTables.MapiFamily, OxVCardTables.Label(p), kind, code, reason);
                }
            }
        }

        // ---- everything the item has that the card does not carry ----------------------------------------------------

        private void ReportRemainder(string fn, bool isList)
        {
            var phoneIds = new HashSet<ushort>(ContactTags.PhoneTags.Select(t => t.Id));
            foreach (MapiProperty p in m.Properties)
            {
                // An empty text value and an absent one mean the same to a vCard, so no empty property is written. The report says so, because
                // the property comes back absent rather than empty.
                if (p.Type is MapiType.UnicodeString or MapiType.String8 && !p.IsMultiValued && (p.AsString(m.String8Encoding) ?? string.Empty).Length == 0)
                {
                    report.Add(OxVCardTables.MapiFamily, OxVCardTables.Label(p), LossKind.Internal, "EmptyValueNotWritten", "The value is the empty string; a vCard property is not written for it, and the property is absent after import.");
                    continue;
                }

                if (p.Name is { } name)
                {
                    ReportNamed(p, name, fn, isList);
                    continue;
                }

                if (p.Id is 0x0037 or 0x0E1D or 0x0070)
                {
                    // The subject, normalised subject and conversation topic are copies of the display name (2.1.3.2.1).
                    string v = p.AsString(m.String8Encoding) ?? string.Empty;
                    if (v.Length == 0 || v == fn)
                    {
                        continue;
                    }

                    Drop(OxVCardTables.Label(p), "NoVCardMapping", "The value differs from the display name, which is the only name MS-OXVCARD writes.");
                    continue;
                }

                if (phoneIds.Contains(p.Id))
                {
                    if (p.IsMultiValued && p.ValueCount > 1)
                    {
                        Drop(OxVCardTables.Label(p), "AdditionalValuesNotCarried", "Only the first of the property's values is written.");
                    }

                    continue;
                }

                if (OxVCardTables.MappedTags.Contains(p.Id) && !(isList && p.Id != 0x3001 && p.Id != 0x0E1D && p.Id != 0x0037 && p.Id != 0x0070))
                {
                    continue;
                }

                if (OxVCardTables.Bookkeeping.Contains(p.Id))
                {
                    report.Add(OxVCardTables.MapiFamily, OxVCardTables.Label(p), LossKind.Internal, "StoreBookkeeping", "A store-internal property with no meaning outside the store.");
                }
                else
                {
                    Drop(OxVCardTables.Label(p), "NoVCardMapping", "MS-OXVCARD maps no vCard property from this property.");
                }
            }

            // Attachments: the photo is carried, nothing else can be.
            IReadOnlyList<MapiAttachment> attachments = m.Attachments;
            for (int i = 0; i < attachments.Count; i++)
            {
                if (ReferenceEquals(attachments[i], _c.PhotoAttachment) && !isList)
                {
                    continue;
                }

                string path = "attachment[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                Drop("attachment", "AttachmentNotCarried", "A vCard has no place for an attachment other than the photo.", path);
            }

            if (m.Recipients.Count > 0)
            {
                Drop("recipient", "NoVCardMapping", "A contact's recipient table has no vCard form.");
            }
        }

        private void ReportNamed(MapiProperty p, MapiNamedProperty n, string fn, bool isList)
        {
            bool mapped = n == OxVCardTables.Keywords
                || (!n.IsString && n.PropertySet == MapiPropertySets.Address && OxVCardTables.MappedAddressLids.Contains(n.Lid));
            if (isList && !n.IsString && n.PropertySet == MapiPropertySets.Address && n.Lid is 0x8053 or 0x8054 or 0x8055 or 0x8064 or 0x804C)
            {
                // The list name and members are written (or reported above); the member blobs and their checksum are one fact.
                return;
            }

            if (mapped)
            {
                if (!isList || n == OxVCardTables.Keywords || n.Lid is 0x8005)
                {
                    DerivedEmailParts(p, n, fn);
                }

                return;
            }

            if (!n.IsString && n.PropertySet == MapiPropertySets.Common && n.Lid is 0x8552 or 0x8554 or 0x8585 or 0x8586 or 0x8584)
            {
                report.Add(OxVCardTables.MapiFamily, OxVCardTables.Label(p), LossKind.Internal, "StoreBookkeeping", "A store-internal property with no meaning outside the store.");
                return;
            }

            Drop(OxVCardTables.Label(p), "NoVCardMapping", "MS-OXVCARD maps no vCard property from this named property.");
        }

        // The display names Outlook keeps beside an email address are derived from the name and the address; the original display name and the
        // entry ID are bookkeeping. A display name that is neither is the user's own text and is a loss.
        private void DerivedEmailParts(MapiProperty p, MapiNamedProperty n, string fn)
        {
            if (n.IsString || n.PropertySet != MapiPropertySets.Address)
            {
                return;
            }

            uint slotBase = n.Lid & 0xFFF0;
            bool email = slotBase is 0x8080 or 0x8090 or 0x80A0;
            if (!email)
            {
                return;
            }

            uint off = n.Lid & 0xF;
            string label = OxVCardTables.Label(p);
            if (off == 0)
            {
                string dn = p.AsString(m.String8Encoding) ?? string.Empty;
                string addr = m.Properties.Get(new MapiNamedProperty(MapiPropertySets.Address, slotBase + 3))?.AsString(m.String8Encoding) ?? string.Empty;
                if (dn.Length > 0 && dn != addr && dn != fn && dn != fn + " (" + addr + ")")
                {
                    Drop(label, "NoVCardMapping", "The email display name is not the contact name or the address, and a vCard EMAIL has no display name.");
                }
                else
                {
                    report.Add(OxVCardTables.MapiFamily, label, LossKind.Internal, "DerivedValue", "The display name Outlook derives from the contact name and the address.");
                }
            }
            else if (off == 4)
            {
                report.Add(OxVCardTables.MapiFamily, label, LossKind.Internal, "DerivedValue", "The display name the address had before the user edited it.");
            }
        }
    }
}
