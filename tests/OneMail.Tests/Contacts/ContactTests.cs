using System.Buffers.Binary;
using System.Text;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.ContentLines;
using RecursiveAI.OneMail.Calendar.VCard;
using RecursiveAI.OneMail.Contacts;
using RecursiveAI.OneMail.Conversion;
using RecursiveAI.OneMail.Conversion.OxVCard;
using RecursiveAI.OneMail.Mapi;

namespace RecursiveAI.OneMail.Tests.Contacts;

public class ContactTests
{
    private static ContentDocument Doc(string text) => ContentDocument.Parse(Encoding.UTF8.GetBytes(text));

    private static VCardView Card(string text) => VCardDocument.Cards(Doc(text)).Single();

    // ---- the vCard typed view ---------------------------------------------------------------------------------------------

    [Fact]
    public void TypedViewReadsTheRemainingRfc6350Properties()
    {
        VCardView c = Card("BEGIN:VCARD\r\nVERSION:4.0\r\nFN:Ann\r\nGENDER:F;she\r\nLANG;PREF=1:fr\r\nLANG;PREF=2:en\r\nTZ:-0500\r\nGEO:geo:37.386013,-122.082932\r\nIMPP;PREF=1:xmpp:ann@example.com\r\n"
            + "RELATED;TYPE=friend:urn:uuid:f81d4fae-7dec-11d0-a765-00a0c91e6bf6\r\nKEY:data:application/pgp-keys;base64,QUJD\r\nSOUND:https://example.com/a.ogg\r\nREV:20240102T030405Z\r\nTEL;VALUE=uri;ALTID=1;PID=1.1,2.1;TYPE=\"work,voice\":tel:+1-555\r\nX-CUSTOM:1\r\nEND:VCARD\r\n");
        Assert.Equal(new VCardGender("F", "she"), c.Gender);
        Assert.Equal(["fr", "en"], c.Languages.Select(l => l.Value));
        Assert.Equal("-0500", c.TimeZones.Single().Value);
        VCardGeo geo = c.Geos.Single();
        Assert.Equal(37.386013, geo.Latitude);
        Assert.Equal(-122.082932, geo.Longitude);
        Assert.Equal("xmpp:ann@example.com", c.Impps.Single().Value);
        Assert.Equal(["friend"], c.Related.Single().Types);
        Assert.Equal("ABC", Encoding.ASCII.GetString(c.Keys.Single().Data!));
        Assert.Equal("application/pgp-keys", c.Keys.Single().MediaType);
        Assert.Equal("https://example.com/a.ogg", c.Sounds.Single().Uri);
        Assert.Equal(new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero), c.RevisionTime);
        VCardTyped tel = c.Phones.Single();
        Assert.Equal("1", tel.AltId);
        Assert.Equal(["1.1", "2.1"], tel.Pid);
        Assert.Equal("uri", tel.ValueType);
        Assert.True(tel.HasType("WORK"));
        Assert.Equal("X-CUSTOM", c.Extensions.Single().Name);
    }

    [Fact]
    public void Version30GeoAndKeyAndPartialDates()
    {
        VCardView c = Card("BEGIN:VCARD\r\nVERSION:3.0\r\nGEO:37.386013;-122.082932\r\nKEY;ENCODING=b;TYPE=PGP:QUJD\r\nBDAY;VALUE=text:circa 1800\r\nANNIVERSARY:--0229\r\nEND:VCARD\r\n");
        Assert.Equal(37.386013, c.Geos.Single().Latitude);
        Assert.Equal("PGP", c.Keys.Single().MediaType);
        Assert.Null(c.Birthday);
        Assert.Equal("circa 1800", c.BirthdayText);
        Assert.Equal(new VCardDate(null, 2, 29, null, null, null, null), c.Anniversary);
    }

    [Fact]
    public void MediaDecodesOnFirstUseAndToleratesSurplusPadding()
    {
        VCardView c = Card("BEGIN:VCARD\r\nVERSION:2.1\r\nPHOTO;ENCODING=BASE64:QUJDRA==\r\nLOGO;ENCODING=BASE64:QUJD=\r\nSOUND;ENCODING=BASE64:Q*JD\r\nEND:VCARD\r\n");
        VCardMedia photo = c.Photos.Single();
        Assert.True(photo.IsInline);
        Assert.Equal("ABCD", Encoding.ASCII.GetString(photo.Data!));
        Assert.Equal("ABC", Encoding.ASCII.GetString(c.Logos.Single().Data!));
        Assert.Null(c.Sounds.Single().Data);
    }

    [Fact]
    public void Version3AndLaterDropsBackslashBeforeAnyCharacter()
    {
        VCardView v3 = Card("BEGIN:VCARD\r\nVERSION:3.0\r\nURL:http\\://example.com\r\nNOTE:say \\\"hi\\\"\\nnext\\, \\;\r\nEND:VCARD\r\n");
        Assert.Equal("http://example.com", v3.Urls.Single().Value);
        Assert.Equal("say \"hi\"\nnext, ;", v3.Note);
        VCardView v21 = Card("BEGIN:VCARD\r\nVERSION:2.1\r\nNOTE:a\\nb\\;c\r\nEND:VCARD\r\n");
        Assert.Equal("a\\nb;c", v21.Note);
    }

    [Fact]
    public void RepeatedPropertiesAreAllReachable()
    {
        VCardView c = Card("BEGIN:VCARD\r\nVERSION:3.0\r\nN:A;B\r\nN:C;D\r\nTITLE:one\r\nTITLE:two\r\nORG:X;Y\r\nORG:Z\r\nBDAY:2000-01-01\r\nBDAY:2001-02-03\r\nEND:VCARD\r\n");
        Assert.Equal(2, c.Names.Count);
        Assert.Equal(["one", "two"], c.Titles);
        Assert.Equal(2, c.Organizations.Count);
        Assert.Equal(2, c.Birthdays.Count);
    }

    // ---- Outlook contacts --------------------------------------------------------------------------------------------------

    private static MapiProperty S(ushort id, string v) => MapiProperty.CreateOwned(new MapiTag(id, MapiType.UnicodeString), Encoding.Unicode.GetBytes(v));

    private static MapiProperty I(ushort id, int v)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, v);
        return MapiProperty.CreateOwned(new MapiTag(id, MapiType.Integer32), b);
    }

    private static MapiProperty Time(ushort id, DateTime utc, MapiNamedProperty? name = null)
    {
        byte[] b = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(b, utc.ToFileTimeUtc());
        return MapiProperty.CreateOwned(new MapiTag(id, MapiType.Time), b, name);
    }

    private static MapiNamedProperty Addr(uint lid) => new(MapiPropertySets.Address, lid);

    private static ushort _next = 0x8000;

    private static MapiProperty N(uint lid, string v) => MapiProperty.CreateOwned(new MapiTag(_next++, MapiType.UnicodeString), Encoding.Unicode.GetBytes(v), Addr(lid));

    private static MapiProperty NI(uint lid, int v)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, v);
        return MapiProperty.CreateOwned(new MapiTag(_next++, MapiType.Integer32), b, Addr(lid));
    }

    /// <summary>The contact of MS-OXVCARD 3.2, with the other addresses and numbers a contact holds.</summary>
    private static MapiMessage Contact()
    {
        var m = new MapiMessage();
        m.Properties.Add(S(MapiPid.MessageClass, "IPM.Contact"));
        m.Properties.Add(S(MapiPid.DisplayName, "Dan Fennell"));
        m.Properties.Add(S(MapiPid.GivenName, "Dan"));
        m.Properties.Add(S(MapiPid.Surname, "Fennell"));
        m.Properties.Add(S(MapiPid.CompanyName, "Contoso, Ltd."));
        m.Properties.Add(S(MapiPid.DepartmentName, "R;D"));
        m.Properties.Add(S(MapiPid.Title, "Engineer"));
        m.Properties.Add(S(MapiPid.BusinessTelephoneNumber, "+1-206-555-0102"));
        m.Properties.Add(S(MapiPid.MobileTelephoneNumber, "+1-425-555-0199"));
        m.Properties.Add(S(MapiPid.HomeFaxNumber, "+1-555-0100"));
        m.Properties.Add(S(MapiPid.CompanyMainTelephoneNumber, "+1-206-555-0100"));
        m.Properties.Add(S(MapiPid.Assistant, "Jeff Price"));
        m.Properties.Add(S(MapiPid.AssistantTelephoneNumber, "+1-206-555-0188"));
        m.Properties.Add(S(MapiPid.BusinessHomePage, "http://www.contoso.com/"));
        m.Properties.Add(S(MapiPid.HomeAddressStreet, "345 Aspen Street"));
        m.Properties.Add(S(MapiPid.HomeAddressCity, "Boulder"));
        m.Properties.Add(S(MapiPid.HomeAddressStateOrProvince, "CO"));
        m.Properties.Add(S(MapiPid.HomeAddressPostalCode, "33041"));
        m.Properties.Add(S(MapiPid.HomeAddressCountry, "USA"));
        m.Properties.Add(N(0x8045, "7890 Elm Street\r\nSuite 5"));
        m.Properties.Add(N(0x8046, "Boulder"));
        m.Properties.Add(N(0x8047, "CO"));
        m.Properties.Add(N(0x8048, "33041"));
        m.Properties.Add(N(0x8049, "U.S."));
        m.Properties.Add(NI(0x8022, 2));
        m.Properties.Add(N(0x8082, "SMTP"));
        m.Properties.Add(N(0x8083, "dan.fennell@contoso.com"));
        m.Properties.Add(N(0x8080, "Dan Fennell (dan.fennell@contoso.com)"));
        m.Properties.Add(N(0x8092, "SMTP"));
        m.Properties.Add(N(0x8093, "dfennell@fabrikam.com"));
        m.Properties.Add(N(0x80A2, "EX"));
        m.Properties.Add(N(0x80A3, "/o=First/ou=Group/cn=Recipients/cn=dan"));
        m.Properties.Add(N(0x8062, "dan@im.example"));
        m.Properties.Add(S(MapiPid.Body, "Line one\r\nLine two, with; punctuation"));
        m.Properties.Add(Time(MapiPid.Birthday, new DateTime(1980, 3, 22, 11, 59, 0, DateTimeKind.Utc)));
        m.Properties.Add(Time(0x8100, new DateTime(1980, 3, 22, 0, 0, 0, DateTimeKind.Utc), Addr(0x80DE)));
        m.Properties.Add(MapiProperty.CreateMulti(new MapiTag(0x8200, MapiType.MultipleString), [Encoding.Unicode.GetBytes("red"), Encoding.Unicode.GetBytes("a,b")],
            new MapiNamedProperty(MapiPropertySets.PublicStrings, "Keywords")));
        m.Properties.Add(I(0x0036, 2));
        var photo = new MapiAttachment(m);
        photo.Properties.Add(I(0x3705, 1));
        photo.Properties.Add(MapiProperty.CreateOwned(new MapiTag(0x3701, MapiType.Binary), [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]));
        photo.Properties.Add(S(0x3703, ".jpg"));
        photo.Properties.Add(MapiProperty.CreateOwned(new MapiTag(0x7FFF, MapiType.Boolean), [1]));
        m.AddAttachment(photo);
        return m;
    }

    [Fact]
    public void ContactViewReadsNamedAndTaggedProperties()
    {
        var c = new MapiContact(Contact());
        Assert.Equal("Dan Fennell", c.DisplayName);
        Assert.Equal("Contoso, Ltd.", c.CompanyName);
        Assert.Equal(ContactMailingAddress.Business, c.MailingAddress);
        ContactAddress work = c.Address(ContactAddressKind.Business);
        Assert.Equal("7890 Elm Street\r\nSuite 5", work.Street);
        Assert.Equal("U.S.", work.Country);
        Assert.Equal("Boulder", c.Address(ContactAddressKind.Home).City);
        Assert.True(c.Address(ContactAddressKind.Other).IsEmpty);
        Assert.Equal("+1-206-555-0102", c.Phone(ContactPhoneKind.Business));
        Assert.Equal("+1-555-0100", c.Phone(ContactPhoneKind.HomeFax));
        Assert.Equal(["Dan Fennell (dan.fennell@contoso.com)"], c.EmailAddresses.Where(e => e.Slot == 1).Select(e => e.DisplayName));
        Assert.Equal(["dan.fennell@contoso.com", "dfennell@fabrikam.com", null], c.EmailAddresses.Select(e => e.SmtpAddress));
        Assert.Equal(new DateOnly(1980, 3, 22), c.BirthDate);
        Assert.Equal(["red", "a,b"], c.Categories);
        Assert.NotNull(c.PhotoAttachment);
        Assert.Equal("dan@im.example", c.InstantMessagingAddress);
    }

    [Fact]
    public void ContactToVCardFollowsMsOxVcard()
    {
        ConversionResult<Component> r = new ContactToVCardConverter().Convert(Contact());
        VCardView v = new(r.Value);
        Assert.Equal(VCardVersion.V30, v.Version);
        Assert.Equal("Dan Fennell", v.FormattedName);
        Assert.Equal(["Fennell"], v.Name!.Family);
        Assert.Equal(["Contoso, Ltd.", "R;D"], v.Organization);
        VCardAddress adr = v.Addresses.Single(a => a.Types.Contains("work"));
        Assert.Contains("pref", adr.Types);
        Assert.Equal(["7890 Elm Street\nSuite 5"], adr.Street);
        Assert.Equal(["home"], v.Addresses.Single(a => a.Types.Contains("home")).Types);
        Assert.Equal(["dan.fennell@contoso.com", "dfennell@fabrikam.com"], v.Emails.Select(e => e.Value));
        Assert.Equal(["internet", "pref"], v.Emails[0].Types);
        Assert.Equal(["work"], v.Phones.Single(p => p.Value == "+1-206-555-0102").Types);
        Assert.Equal(["cell"], v.Phones.Single(p => p.Value == "+1-425-555-0199").Types);
        Assert.Equal(["home", "fax"], v.Phones.Single(p => p.Value == "+1-555-0100").Types);
        Assert.Equal("COMPANY", v.Component.FindProperties("X-MS-TEL").Single(p => p.Value == "+1-206-555-0100").GetParameter("TYPE"));
        Assert.Equal("1980-03-22", r.Value.FindProperty("BDAY")!.Value);
        Assert.Equal("PRIVATE", r.Value.FindProperty("CLASS")!.Value);
        Assert.Equal(["red", "a,b"], v.Categories);
        Assert.Equal("ABC".Length + 4, v.Photos.Single().Data!.Length);
        Assert.Equal("2", r.Value.FindProperty("X-MS-OL-DEFAULT-POSTAL-ADDRESS")!.Value);

        // The Exchange address has no SMTP form and is listed, not guessed.
        Assert.Contains(r.Report, e => e.Name.Contains("Email3", StringComparison.Ordinal) && e.Code == "ExchangeAddressNotExported");
        Assert.DoesNotContain(r.Report, e => e.Code == "NoVCardMapping" && e.Name is "PidTagGivenName" or "PidTagSurname");
    }

    [Fact]
    public void ContactSurvivesAVCardRoundTripPropertyExact()
    {
        foreach (VCardVersion version in new[] { VCardVersion.V30, VCardVersion.V40 })
        {
            MapiMessage src = Contact();
            ConversionResult<Component> r = new ContactToVCardConverter(new ContactToVCardOptions { Version = version }).Convert(src);
            var doc = new ContentDocument();
            doc.Root.Add(r.Value);
            Component reparsed = ContentDocument.Parse(doc.ToArray()).Components.Single();
            MapiMessage back = new VCardToContactConverter().Convert(reparsed).Value;
            var a = new MapiContact(src);
            var b = new MapiContact(back);
            Assert.Equal(a.DisplayName, b.DisplayName);
            Assert.Equal(a.GivenName, b.GivenName);
            Assert.Equal(a.Surname, b.Surname);
            Assert.Equal(a.CompanyName, b.CompanyName);
            Assert.Equal(a.Department, b.Department);
            Assert.Equal(a.JobTitle, b.JobTitle);
            Assert.Equal(a.Notes, b.Notes);
            Assert.Equal(a.BirthDate, b.BirthDate);
            Assert.Equal(a.Categories, b.Categories);
            Assert.Equal(a.MailingAddress, b.MailingAddress);
            Assert.Equal(a.Address(ContactAddressKind.Business).Street, b.Address(ContactAddressKind.Business).Street);
            Assert.Equal(a.Address(ContactAddressKind.Home).PostalCode, b.Address(ContactAddressKind.Home).PostalCode);
            Assert.Equal(a.Phones.Select(p => (p.Kind, p.Number)), b.Phones.Select(p => (p.Kind, p.Number)));
            Assert.Equal(a.EmailAddresses.Where(e => e.SmtpAddress is not null).Select(e => e.SmtpAddress), b.EmailAddresses.Select(e => e.SmtpAddress));
            Assert.Equal(a.AssistantName, b.AssistantName);
            Assert.Equal(a.InstantMessagingAddress, b.InstantMessagingAddress);
            Assert.Equal(a.BusinessHomePage, b.BusinessHomePage);
            Assert.Equal(7, b.PhotoAttachment!.OpenData()!.ReadAllBytes().Length);
            if (version == VCardVersion.V30)
            {
                Assert.Equal(2, b.Message.Properties.Get(0x0036, MapiType.Integer32)!.AsInt32());
            }
        }
    }

    [Fact]
    public void VCardToContactFollowsTheMsOxVcardImportExample()
    {
        Component card = Doc("BEGIN:vCard\r\nVERSION:3.0\r\nFN:Dan Fennell\r\nORG:Contoso, Ltd.\r\nADR;TYPE=WORK,POSTAL,PARCEL:;;7890 Elm Street;Boulder;CO;33041;U.S.\r\nTEL;TYPE=VOICE,MSG,WORK:+1-206-555-0102\r\n"
            + "TEL;TYPE=FAX,WORK:+1-206-555-0162\r\nEMAIL;TYPE=INTERNET,PREF:dan.fennell@contoso.com\r\nEMAIL;TYPE=INTERNET:dfennell@fabrikam.com\r\nURL;TYPE=WORK:http://www.contoso.com/\r\nEND:vCard\r\n").Components.Single();
        ConversionResult<MapiMessage> r = new VCardToContactConverter().Convert(card);
        var c = new MapiContact(r.Value);
        Assert.Equal("IPM.Contact", r.Value.MessageClass);
        Assert.Equal("Dan Fennell", c.DisplayName);
        Assert.Equal("Contoso, Ltd.", c.CompanyName);
        Assert.Equal("7890 Elm Street", c.Address(ContactAddressKind.Business).Street);
        Assert.Equal("7890 Elm Street", c.Address(ContactAddressKind.Other).Street);
        Assert.Equal("+1-206-555-0102", c.Phone(ContactPhoneKind.Business));
        Assert.Equal("+1-206-555-0102", c.Phone(ContactPhoneKind.Other));
        Assert.Equal("+1-206-555-0162", c.Phone(ContactPhoneKind.BusinessFax));
        Assert.Equal(["dan.fennell@contoso.com", "dfennell@fabrikam.com"], c.EmailAddresses.Select(e => e.Address));
        Assert.Equal("http://www.contoso.com/", c.BusinessHomePage);
        Assert.Equal(0, r.Value.Properties.Get(0x0036, MapiType.Integer32)!.AsInt32());
        Assert.Contains(r.Report, e => e.Name == "VERSION" && e.Kind == LossKind.Internal);
        Assert.DoesNotContain(r.Report, e => e.IsContentLoss);
    }

    [Fact]
    public void VCardToContactListsWhatDoesNotCarry()
    {
        Component card = Doc("BEGIN:VCARD\r\nVERSION:4.0\r\nFN:Ann\r\nUID:urn:uuid:1\r\nBDAY:--0412\r\nGEO:geo:1,2\r\nTEL:1\r\nTEL:2\r\nEMAIL:a@x\r\nEMAIL:b@x\r\nEMAIL:c@x\r\nEMAIL:d@x\r\nX-MS-TEXT:1\r\nX-MS-TEXT:2\r\nX-MS-TEXT:3\r\nX-MS-TEXT:4\r\nX-MS-TEXT:5\r\nPHOTO:https://example.com/p.jpg\r\nEND:VCARD\r\n").Components.Single();
        LossReport report = new VCardToContactConverter().Convert(card).Report;
        Assert.Contains(report, e => e.Name == "UID" && e.Kind == LossKind.Dropped);
        Assert.Contains(report, e => e.Name == "BDAY" && e.Code == "PartialDate");
        Assert.Contains(report, e => e.Name == "GEO");
        Assert.Contains(report, e => e.Name == "EMAIL" && e.Code == "AdditionalValueNotMapped");
        Assert.Contains(report, e => e.Name == "X-MS-TEXT" && e.Code == "AdditionalValueNotMapped");
        Assert.Contains(report, e => e.Name == "PHOTO" && e.Code == "PhotoNotInline");
        Assert.Contains(report, e => e.Name == "TEL" && e.Code == "AdditionalValueNotMapped");
    }

    // ---- distribution lists ------------------------------------------------------------------------------------------------

    private static byte[] OneOff(string name, string type, string address)
    {
        var ms = new MemoryStream();
        ms.Write(new byte[4]);
        ms.Write([0x81, 0x2B, 0x1F, 0xA4, 0xBE, 0xA3, 0x10, 0x19, 0x9D, 0x6E, 0x00, 0xDD, 0x01, 0x0F, 0x54, 0x02]);
        ms.Write([0, 0, 0x90, 0x01]); // version 0; flags: Unicode, send as MIME
        foreach (string s in new[] { name, type, address })
        {
            ms.Write(Encoding.Unicode.GetBytes(s));
            ms.Write([0, 0]);
        }

        return ms.ToArray();
    }

    private static byte[] Wrapped(byte type, byte[] embedded)
    {
        var ms = new MemoryStream();
        ms.Write(new byte[4]);
        ms.Write([0xC0, 0x91, 0xAD, 0xD3, 0x51, 0x9D, 0xCF, 0x11, 0xA4, 0xA9, 0x00, 0xAA, 0x00, 0x47, 0xFA, 0xA4]);
        ms.WriteByte(type);
        ms.Write(embedded);
        return ms.ToArray();
    }

    [Fact]
    public void DistributionListReadsMembersAndTheStream()
    {
        byte[] a = OneOff("Ann", "SMTP", "ann@example.com");
        byte[] b = OneOff("Bob", "SMTP", "bob@example.com");
        var m = new MapiMessage();
        m.Properties.Add(S(MapiPid.MessageClass, "IPM.DistList"));
        m.Properties.Add(S(MapiPid.DisplayName, "Team"));
        m.Properties.Add(MapiProperty.CreateMulti(new MapiTag(_next++, MapiType.MultipleBinary), [a, Wrapped(0x03 | 0x80 | (4 << 4), new byte[30]), Wrapped(0, b)], Addr(0x8055)));
        m.Properties.Add(MapiProperty.CreateMulti(new MapiTag(_next++, MapiType.MultipleBinary), [a, b, b], Addr(0x8054)));
        var list = new MapiDistributionList(m);
        Assert.Equal("Team", list.Name);
        Assert.Equal(3, list.Members.Count);
        Assert.Equal(DistributionListMemberKind.OneOff, list.Members[0].Kind);
        Assert.Equal("ann@example.com", list.Members[0].SmtpAddress);
        Assert.Equal(DistributionListMemberKind.Contact, list.Members[1].Kind);
        Assert.Equal(4, list.Members[1].ContactSlot);
        Assert.Equal("Bob", list.Members[1].DisplayName);
        Assert.Equal("bob@example.com", list.Members[2].Address);

        // The stream, when there is one, replaces the member properties.
        var stream = new MemoryStream();
        Span<byte> w = stackalloc byte[4];
        stream.Write([1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        BinaryPrimitives.WriteInt32LittleEndian(w, 1);
        stream.Write(w);
        stream.Write(new byte[12]);
        BinaryPrimitives.WriteInt32LittleEndian(w, a.Length);
        stream.Write(w);
        stream.Write(a);
        BinaryPrimitives.WriteInt32LittleEndian(w, 0);
        stream.Write(w);
        stream.Write(w);
        stream.Write(new byte[8]);
        m.Properties.Add(MapiProperty.CreateOwned(new MapiTag(_next++, MapiType.Binary), stream.ToArray(), Addr(0x8064)));
        Assert.Equal(["ann@example.com"], new MapiDistributionList(m).Members.Select(x => x.Address));

        // A group card is a 4.0 card with KIND:group, and the members that have an SMTP address become MEMBER URIs.
        ConversionResult<Component> r = new ContactToVCardConverter(new ContactToVCardOptions { Version = VCardVersion.V40 }).Convert(m);
        Assert.Equal("group", new VCardView(r.Value).Kind);
        Assert.Equal(["mailto:ann@example.com"], new VCardView(r.Value).Members);
    }

    [Fact]
    public void HostileContactsDoNotThrow()
    {
        var m = new MapiMessage();
        m.Properties.Add(S(MapiPid.MessageClass, "IPM.Contact"));
        m.Properties.Add(S(MapiPid.DisplayName, "a\0b;\r\nc"));
        m.Properties.Add(MapiProperty.CreateOwned(new MapiTag(0x3A08, MapiType.Binary), [1, 2, 3]));
        m.Properties.Add(MapiProperty.CreateOwned(new MapiTag(_next++, MapiType.Binary), [1, 2, 3, 4, 5], Addr(0x8055)));
        m.Properties.Add(MapiProperty.CreateOwned(new MapiTag(_next++, MapiType.Integer32), [1, 2], Addr(0x8022)));
        ConversionResult<Component> r = new ContactToVCardConverter().Convert(m);
        var doc = new ContentDocument();
        doc.Root.Add(r.Value);
        Assert.NotEmpty(doc.ToArray());
        _ = new VCardToContactConverter().Convert(Doc("BEGIN:VCARD\r\nVERSION:3.0\r\nN:;;;;;;;\r\nADR:;;;;;;;;;;\r\nBDAY:99999999\r\nTEL;TYPE=,,:\r\nPHOTO;ENCODING=b:!!!!\r\nX-MS-TEXT\r\nEND:VCARD\r\n").Components.Single());
        Assert.Empty(new MapiDistributionList(m).Members);
    }
}

internal static class StreamExtensions
{
    public static byte[] ReadAllBytes(this Stream s)
    {
        using (s)
        {
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }
}
