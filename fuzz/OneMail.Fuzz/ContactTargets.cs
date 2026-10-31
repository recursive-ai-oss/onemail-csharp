using System.Text;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.ContentLines;
using RecursiveAI.OneMail.Calendar.VCard;
using RecursiveAI.OneMail.Contacts;
using RecursiveAI.OneMail.Conversion;
using RecursiveAI.OneMail.Conversion.OxVCard;
using RecursiveAI.OneMail.Mapi;

namespace RecursiveAI.OneMail.Fuzz;

// The vCard typed view, the Outlook contact view and the MS-OXVCARD conversions on hostile input. Byte 0 picks the input:
//   0: the rest is a vCard: every typed accessor is read, the card becomes a contact, the contact is read through its view and becomes a card again;
//   1: 4-byte records (property index, 16-bit offset, value) damage the values of a valid contact before it is read and converted both ways;
//   2: as 1, on a distribution list.
// Only typed results may escape: no exception, no hang, and the vCard written for any item must parse again.
internal static class ContactTargets
{
    public static Dictionary<string, Action<ReadOnlySpan<byte>>> Register(Dictionary<string, Action<ReadOnlySpan<byte>>> targets)
    {
        targets["vcard-view"] = static d => Run(d);
        return targets;
    }

    private static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        switch (data[0] % 3)
        {
            case 0:
                FromCard(data[1..].ToArray());
                break;
            case 1:
                Convert(Damaged(false, data[1..]));
                break;
            default:
                Convert(Damaged(true, data[1..]));
                break;
        }
    }

    private static void FromCard(byte[] bytes)
    {
        CalendarLimits limits = new() { MaxLineBytes = 8192, MaxDepth = 8, MaxComponents = 200, MaxProperties = 2000 };
        ContentDocument doc = ContentDocument.Parse(bytes, new ContentDocumentOptions { Limits = limits });
        foreach (Component c in doc.Components)
        {
            if (!c.IsNamed("VCARD"))
            {
                continue;
            }

            var v = new VCardView(c);
            Touch(v);
            ConversionResult<MapiMessage> contact = new VCardToContactConverter().Convert(c);
            Convert(contact.Value);
        }
    }

    private static void Touch(VCardView v)
    {
        _ = (v.Version, v.FormattedName, v.Name, v.Kind, v.Members.Count, v.Gender, v.RevisionTime, v.BirthdayText, v.AnniversaryText, v.Birthday, v.Anniversary,
            v.Names.Count, v.Titles.Count, v.Roles.Count, v.Notes.Count, v.Organizations.Count, v.Birthdays.Count, v.Anniversaries.Count, v.Extensions.Count,
            v.Languages.Count, v.TimeZones.Count, v.Geos.Count, v.Impps.Count, v.Related.Count, v.Emails.Count, v.Phones.Count, v.Addresses.Count, v.Urls.Count);
        foreach (VCardMedia m in v.Photos.Concat(v.Logos).Concat(v.Sounds).Concat(v.Keys))
        {
            _ = (m.IsInline, m.Data, m.Uri, m.MediaType);
        }

        foreach (VCardTyped t in v.Phones.Concat(v.Emails.Select(e => (VCardTyped)e)))
        {
            _ = (t.AltId, t.Pid, t.Language, t.ValueType, t.Label, t.HasType("work"));
        }
    }

    private static void Convert(MapiMessage m)
    {
        var view = new MapiMailItem(m, new MailProvenance(MailFormat.Msg, null, null, 0, 0, 0));
        _ = (view.Contact, view.DistributionList);
        if (view.Contact is { } c)
        {
            _ = (c.DisplayName, c.Phones.Count, c.EmailAddresses.Count, c.Addresses.Count, c.Categories.Count, c.BirthDate, c.AnniversaryDate, c.UserFields.Count,
                c.MailingAddress, c.Gender, c.X509Certificates.Count, c.PhotoAttachment?.OpenData()?.Length);
        }

        if (view.DistributionList is { } l)
        {
            foreach (DistributionListMember x in l.Members)
            {
                _ = (x.Kind, x.DisplayName, x.Address, x.SmtpAddress, x.ContactSlot);
            }
        }

        foreach (VCardVersion version in new[] { VCardVersion.V30, VCardVersion.V40 })
        {
            ConversionResult<Component> r = new ContactToVCardConverter(new ContactToVCardOptions { Version = version }).Convert(m);
            var doc = new ContentDocument();
            doc.Root.Add(r.Value);
            byte[] written = doc.ToArray();
            ContentDocument again = ContentDocument.Parse(written);
            if (again.Components.Count() != 1)
            {
                throw new InvalidOperationException("the written vCard does not parse back to one card");
            }

            Touch(new VCardView(again.Components.First()));
        }
    }

    // A valid contact (or list) whose property values are damaged by the records: index, offset (two bytes) and a value byte.
    private static MapiMessage Damaged(bool list, ReadOnlySpan<byte> patches)
    {
        var props = new List<(MapiTag Tag, byte[] Bytes, MapiNamedProperty? Name)>();
        ushort next = 0x8000;
        void S(ushort id, string v) => props.Add((new MapiTag(id, MapiType.UnicodeString), Encoding.Unicode.GetBytes(v), null));
        void N(uint lid, string v) => props.Add((new MapiTag(next++, MapiType.UnicodeString), Encoding.Unicode.GetBytes(v), new MapiNamedProperty(MapiPropertySets.Address, lid)));
        S(MapiPid.MessageClass, list ? "IPM.DistList" : "IPM.Contact");
        S(MapiPid.DisplayName, "Dan Fennell");
        if (list)
        {
            byte[] oneOff = [0, 0, 0, 0, 0x81, 0x2B, 0x1F, 0xA4, 0xBE, 0xA3, 0x10, 0x19, 0x9D, 0x6E, 0x00, 0xDD, 0x01, 0x0F, 0x54, 0x02, 0, 0, 0x90, 0x01, 65, 0, 0, 0, 83, 0, 77, 0, 84, 0, 80, 0, 0, 0, 97, 0, 64, 0, 98, 0, 0, 0];
            props.Add((new MapiTag(next++, MapiType.Binary), oneOff, new MapiNamedProperty(MapiPropertySets.Address, 0x8064)));
        }
        else
        {
            S(MapiPid.GivenName, "Dan");
            S(MapiPid.Surname, "Fennell");
            S(MapiPid.BusinessTelephoneNumber, "+1-206-555-0102");
            S(MapiPid.HomeFaxNumber, "+1-555-0100");
            S(MapiPid.Body, "Line one\r\nLine two");
            N(0x8045, "7890 Elm Street");
            N(0x8082, "SMTP");
            N(0x8083, "dan@example.com");
            N(0x8080, "Dan (dan@example.com)");
            props.Add((new MapiTag(next++, MapiType.Integer32), [2, 0, 0, 0], new MapiNamedProperty(MapiPropertySets.Address, 0x8022)));
            props.Add((new MapiTag(MapiPid.Birthday, MapiType.Time), BitConverter.GetBytes(new DateTime(1980, 3, 22, 11, 59, 0, DateTimeKind.Utc).ToFileTimeUtc()), null));
            props.Add((new MapiTag(0x3A4D, MapiType.Integer16), [1, 0], null));
        }

        for (int i = 0; i + 4 <= patches.Length; i += 4)
        {
            int index = patches[i] % props.Count;
            (MapiTag tag, byte[] bytes, MapiNamedProperty? name) = props[index];
            if (bytes.Length == 0)
            {
                continue;
            }

            byte[] copy = (byte[])bytes.Clone();
            copy[(patches[i + 1] | (patches[i + 2] << 8)) % copy.Length] = patches[i + 3];
            props[index] = (tag, copy, name);
        }

        var m = new MapiMessage();
        foreach ((MapiTag tag, byte[] bytes, MapiNamedProperty? name) in props)
        {
            m.Properties.Add(MapiProperty.CreateOwned(tag, bytes, name));
        }

        return m;
    }
}
