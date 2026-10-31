using RecursiveAI.OneMail.Mapi;

namespace RecursiveAI.OneMail.Contacts;

/// <summary>The three physical addresses a contact holds (MS-OXOCNTC 2.2.1.3).</summary>
public enum ContactAddressKind
{
    /// <summary>The home address (<c>PidTagHomeAddress*</c>).</summary>
    Home = 0,

    /// <summary>The business address (<c>PidLidWorkAddress*</c>).</summary>
    Business,

    /// <summary>The other address (<c>PidTagOtherAddress*</c>).</summary>
    Other,
}

/// <summary>The value of <c>PidLidPostalAddressId</c>: which address is the mailing address (MS-OXOCNTC 2.2.1.3.9).</summary>
public enum ContactMailingAddress
{
    /// <summary>No address is the mailing address (also the reading when the property is absent or holds a value outside 0 to 3).</summary>
    None = 0,

    /// <summary>The home address.</summary>
    Home = 1,

    /// <summary>The business address.</summary>
    Business = 2,

    /// <summary>The other address.</summary>
    Other = 3,
}

/// <summary>One physical address of a contact.</summary>
/// <param name="Kind">Which address.</param>
/// <param name="Street">Street (may be several lines).</param>
/// <param name="City">City.</param>
/// <param name="State">State or province.</param>
/// <param name="PostalCode">Postal code.</param>
/// <param name="Country">Country or region name.</param>
/// <param name="PostOfficeBox">Post office box.</param>
/// <param name="Formatted">The combined display string (<c>PidLidHomeAddress</c> and its siblings), or null.</param>
public sealed record ContactAddress(
    ContactAddressKind Kind, string? Street, string? City, string? State, string? PostalCode, string? Country, string? PostOfficeBox, string? Formatted)
{
    /// <summary>True when every part is missing or empty.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(Street) && string.IsNullOrEmpty(City) && string.IsNullOrEmpty(State) && string.IsNullOrEmpty(PostalCode)
        && string.IsNullOrEmpty(Country) && string.IsNullOrEmpty(PostOfficeBox);
}

/// <summary>The telephone properties of a contact (MS-OXOCNTC 2.2.1.4 and the fax numbers of 2.2.1.2.6).</summary>
public enum ContactPhoneKind
{
    /// <summary><c>PidTagBusinessTelephoneNumber</c>.</summary>
    Business = 0,

    /// <summary>The second business number (<c>PidTagBusiness2TelephoneNumber</c>; the first of its values when it is multi-valued).</summary>
    Business2,

    /// <summary><c>PidTagHomeTelephoneNumber</c>.</summary>
    Home,

    /// <summary>The second home number (<c>PidTagHome2TelephoneNumber</c>).</summary>
    Home2,

    /// <summary><c>PidTagMobileTelephoneNumber</c>.</summary>
    Mobile,

    /// <summary><c>PidTagCarTelephoneNumber</c>.</summary>
    Car,

    /// <summary><c>PidTagPagerTelephoneNumber</c>.</summary>
    Pager,

    /// <summary><c>PidTagRadioTelephoneNumber</c>.</summary>
    Radio,

    /// <summary><c>PidTagPrimaryTelephoneNumber</c>.</summary>
    Primary,

    /// <summary><c>PidTagOtherTelephoneNumber</c>.</summary>
    Other,

    /// <summary><c>PidTagCompanyMainTelephoneNumber</c>.</summary>
    Company,

    /// <summary><c>PidTagAssistantTelephoneNumber</c>.</summary>
    Assistant,

    /// <summary><c>PidTagCallbackTelephoneNumber</c>.</summary>
    Callback,

    /// <summary><c>PidTagIsdnNumber</c>.</summary>
    Isdn,

    /// <summary><c>PidTagTelexNumber</c>.</summary>
    Telex,

    /// <summary><c>PidTagTelecommunicationsDeviceForDeafTelephoneNumber</c>.</summary>
    TtyTdd,

    /// <summary><c>PidTagBusinessFaxNumber</c>.</summary>
    BusinessFax,

    /// <summary><c>PidTagHomeFaxNumber</c>.</summary>
    HomeFax,

    /// <summary><c>PidTagPrimaryFaxNumber</c>.</summary>
    PrimaryFax,
}

/// <summary>One telephone number of a contact.</summary>
/// <param name="Kind">Which property it came from.</param>
/// <param name="Number">The number as stored (free text).</param>
public sealed record ContactPhone(ContactPhoneKind Kind, string Number);

/// <summary>Whether an electronic address slot is an email address or a fax address.</summary>
public enum ContactElectronicKind
{
    /// <summary>An email slot (<c>PidLidEmail1…3</c>).</summary>
    Email = 0,

    /// <summary>A fax slot (<c>PidLidFax1…3</c>).</summary>
    Fax,
}

/// <summary>One electronic address slot (MS-OXOCNTC 2.2.1.2): an address, its type and the names Outlook keeps beside it.</summary>
/// <param name="Kind">Email or fax.</param>
/// <param name="Slot">1 to 3.</param>
/// <param name="DisplayName">The display name (email slots only; fax slots have none), for example <c>Ada (ada@example.com)</c>.</param>
/// <param name="AddressType">The address type, <c>SMTP</c> or <c>EX</c> usually.</param>
/// <param name="Address">The address, in the format its type defines.</param>
/// <param name="OriginalDisplayName">The display name the address had before the user edited it.</param>
public sealed record ContactElectronicAddress(
    ContactElectronicKind Kind, int Slot, string? DisplayName, string? AddressType, string? Address, string? OriginalDisplayName)
{
    /// <summary>The address when the type is <c>SMTP</c> (or absent and the text looks like an address); null for <c>EX</c> and other types.</summary>
    public string? SmtpAddress => Address is { Length: > 0 } a
        && (AddressType is null or { Length: 0 } ? a.Contains('@', StringComparison.Ordinal) : AddressType.Equals("SMTP", StringComparison.OrdinalIgnoreCase))
        ? a : null;
}

/// <summary>The contact's gender (<c>PidTagGender</c>, MS-OXOCNTC 2.2.1.1.7).</summary>
public enum ContactGender
{
    /// <summary>Not specified.</summary>
    Unspecified = 0,

    /// <summary>Female.</summary>
    Female = 1,

    /// <summary>Male.</summary>
    Male = 2,
}

/// <summary>
/// A typed read view over a contact item (<c>IPM.Contact</c>, MS-OXOCNTC). The property bag stays the truth; every accessor reads the bag
/// when asked and returns null, empty or <see cref="ContactMailingAddress.None"/> for what the item does not have. Nothing here normalises
/// names, addresses or numbers: text is returned as the item stores it.
/// </summary>
/// <remarks>
/// Named properties are found by their property set and ID, never by the local property ID a file happens to assign, so the same code
/// reads MSG, PST, OST and TNEF contacts. The view does not check <see cref="MapiMessage.MessageClass"/>: it reads whatever contact
/// properties the message carries. Not thread-safe, like the message.
/// </remarks>
public sealed class MapiContact
{
    private readonly MapiMessage _m;

    /// <summary>Wraps a message.</summary>
    /// <param name="message">A contact message.</param>
    public MapiContact(MapiMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _m = message;
    }

    /// <summary>The message behind the view.</summary>
    public MapiMessage Message => _m;

    /// <summary>Whether a message class names a contact (<c>IPM.Contact</c> or a subclass, whole segments, case-insensitive).</summary>
    /// <param name="messageClass">The class, or null.</param>
    /// <returns>True for contacts.</returns>
    public static bool IsContactClass(string? messageClass) => MapiMailItem.KindOf(messageClass) == MailItemKind.Contact;

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private string? Str(ushort id) => NonEmpty(_m.Properties.Get(id)?.AsString(_m.String8Encoding));

    private string? Str(uint lid) => NonEmpty(_m.Properties.Get(new MapiNamedProperty(MapiPropertySets.Address, lid))?.AsString(_m.String8Encoding));

    private static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private List<string> Strings(ushort id) => Strings(_m.Properties.Get(id));

    private List<string> Strings(MapiProperty? p)
    {
        if (p is null || (p.Type is not (MapiType.UnicodeString or MapiType.String8 or MapiType.MultipleString or MapiType.MultipleString8)))
        {
            return [];
        }

        List<string> result = [];
        for (int i = 0; i < p.ValueCount; i++)
        {
            if (p.AsString(_m.String8Encoding, null, i) is { Length: > 0 } s)
            {
                result.Add(s);
            }
        }

        return result;
    }

    // ---- names ----------------------------------------------------------------------------------------------------------

    /// <summary><c>PidTagDisplayName</c>.</summary>
    public string? DisplayName => Str(MapiPid.DisplayName);

    /// <summary><c>PidLidFileUnder</c>: the name the contact is filed under.</summary>
    public string? FileUnder => Str(0x8005u);

    /// <summary><c>PidTagGivenName</c>.</summary>
    public string? GivenName => Str(MapiPid.GivenName);

    /// <summary><c>PidTagMiddleName</c>.</summary>
    public string? MiddleName => Str(MapiPid.MiddleName);

    /// <summary><c>PidTagSurname</c>.</summary>
    public string? Surname => Str(MapiPid.Surname);

    /// <summary><c>PidTagInitials</c>.</summary>
    public string? Initials => Str(MapiPid.Initials);

    /// <summary><c>PidTagDisplayNamePrefix</c> (Mr., Dr.).</summary>
    public string? NamePrefix => Str(MapiPid.DisplayNamePrefix);

    /// <summary><c>PidTagGeneration</c> (Jr., III).</summary>
    public string? NameSuffix => Str(ContactTags.Generation);

    /// <summary><c>PidTagNickname</c>.</summary>
    public string? Nickname => Str(MapiPid.Nickname);

    /// <summary><c>PidTagGender</c>; unspecified when absent or outside 0 to 2.</summary>
    public ContactGender Gender => _m.Properties.Get(MapiPid.Gender, MapiType.Integer16)?.AsInt16() is short g and (1 or 2) ? (ContactGender)g : ContactGender.Unspecified;

    // ---- work -----------------------------------------------------------------------------------------------------------

    /// <summary><c>PidTagCompanyName</c>.</summary>
    public string? CompanyName => Str(MapiPid.CompanyName);

    /// <summary><c>PidTagDepartmentName</c>.</summary>
    public string? Department => Str(MapiPid.DepartmentName);

    /// <summary><c>PidTagTitle</c>: the job title.</summary>
    public string? JobTitle => Str(MapiPid.Title);

    /// <summary><c>PidTagProfession</c>.</summary>
    public string? Profession => Str(MapiPid.Profession);

    /// <summary><c>PidTagOfficeLocation</c>.</summary>
    public string? OfficeLocation => Str(MapiPid.OfficeLocation);

    /// <summary><c>PidTagManagerName</c>.</summary>
    public string? ManagerName => Str(MapiPid.ManagerName);

    /// <summary><c>PidTagAssistant</c>: the assistant's name.</summary>
    public string? AssistantName => Str(MapiPid.Assistant);

    // ---- personal ---------------------------------------------------------------------------------------------------

    /// <summary><c>PidTagSpouseName</c>.</summary>
    public string? SpouseName => Str(MapiPid.SpouseName);

    /// <summary><c>PidTagChildrensNames</c>.</summary>
    public IReadOnlyList<string> ChildrensNames => Strings(MapiPid.ChildrensNames);

    /// <summary><c>PidTagHobbies</c>.</summary>
    public string? Hobbies => Str(MapiPid.Hobbies);

    /// <summary>The body text (<c>PidTagBody</c>), the contact's notes.</summary>
    public string? Notes => Str(MapiPid.Body);

    /// <summary>The categories (<c>Keywords</c> in <c>PS_PUBLIC_STRINGS</c>).</summary>
    public IReadOnlyList<string> Categories => Strings(_m.Properties.Get(new MapiNamedProperty(MapiPropertySets.PublicStrings, "Keywords")));

    /// <summary><c>PidTagBirthday</c> as stored: an instant, by the specification 11:59 UTC on the birthday.</summary>
    public DateTimeOffset? Birthday => _m.GetTime(MapiPid.Birthday);

    /// <summary>
    /// The birthday as a calendar date. <c>PidLidBirthdayLocal</c> (0:00 local time, saved without conversion) is used when present, because
    /// its date is the one the user typed; otherwise the UTC date of <see cref="Birthday"/>.
    /// </summary>
    public DateOnly? BirthDate => DateOf(0x80DEu, MapiPid.Birthday);

    /// <summary><c>PidTagWeddingAnniversary</c> as stored.</summary>
    public DateTimeOffset? Anniversary => _m.GetTime(MapiPid.WeddingAnniversary);

    /// <summary>The wedding anniversary as a calendar date, with <c>PidLidWeddingAnniversaryLocal</c> preferred as for <see cref="BirthDate"/>.</summary>
    public DateOnly? AnniversaryDate => DateOf(0x80DFu, MapiPid.WeddingAnniversary);

    private DateOnly? DateOf(uint localLid, ushort tagId)
    {
        if (_m.Properties.Get(new MapiNamedProperty(MapiPropertySets.Address, localLid), MapiType.Time)?.AsTime() is { } local)
        {
            return DateOnly.FromDateTime(local.UtcDateTime);
        }

        return _m.GetTime(tagId) is { } t ? DateOnly.FromDateTime(t.UtcDateTime) : null;
    }

    // ---- web and messaging ---------------------------------------------------------------------------------------

    /// <summary><c>PidTagPersonalHomePage</c>.</summary>
    public string? PersonalHomePage => Str(MapiPid.PersonalHomePage);

    /// <summary><c>PidTagBusinessHomePage</c>.</summary>
    public string? BusinessHomePage => Str(MapiPid.BusinessHomePage);

    /// <summary><c>PidLidInstantMessagingAddress</c>.</summary>
    public string? InstantMessagingAddress => Str(0x8062u);

    /// <summary><c>PidLidFreeBusyLocation</c>: the URL of the contact's free/busy data.</summary>
    public string? FreeBusyUrl => Str(0x80D8u);

    /// <summary><c>PidLidContactUserField1…4</c> (empty slots are null).</summary>
    public IReadOnlyList<string?> UserFields => [Str(0x804Fu), Str(0x8050u), Str(0x8051u), Str(0x8052u)];

    // ---- addresses ----------------------------------------------------------------------------------------------------

    /// <summary>The home, business and other addresses, always three entries in that order (parts the item lacks are null).</summary>
    public IReadOnlyList<ContactAddress> Addresses => [Address(ContactAddressKind.Home), Address(ContactAddressKind.Business), Address(ContactAddressKind.Other)];

    /// <summary>One physical address.</summary>
    /// <param name="kind">Which one.</param>
    /// <returns>The address, possibly <see cref="ContactAddress.IsEmpty"/>.</returns>
    public ContactAddress Address(ContactAddressKind kind) => kind switch
    {
        ContactAddressKind.Home => new ContactAddress(kind, Str(MapiPid.HomeAddressStreet), Str(MapiPid.HomeAddressCity), Str(MapiPid.HomeAddressStateOrProvince),
            Str(MapiPid.HomeAddressPostalCode), Str(MapiPid.HomeAddressCountry), Str(MapiPid.HomeAddressPostOfficeBox), Str(0x801Au)),
        // The business address is the PidLidWorkAddress* group; PidTagStreetAddress and its siblings (0x3A26 to 0x3A2B) are the mailing address, see MailingAddressFields.
        ContactAddressKind.Business => new ContactAddress(kind, Str(0x8045u), Str(0x8046u), Str(0x8047u), Str(0x8048u), Str(0x8049u), Str(0x804Au), Str(0x801Bu)),
        _ => new ContactAddress(kind, Str(MapiPid.OtherAddressStreet), Str(MapiPid.OtherAddressCity), Str(MapiPid.OtherAddressStateOrProvince),
            Str(MapiPid.OtherAddressPostalCode), Str(MapiPid.OtherAddressCountry), Str(MapiPid.OtherAddressPostOfficeBox), Str(0x801Cu)),
    };

    /// <summary>
    /// The mailing address fields (<c>PidTagStreetAddress</c>, <c>PidTagLocality</c> and the others, 0x3A26 to 0x3A2B, with <c>PidTagPostalAddress</c> as the display string).
    /// By MS-OXOCNTC 2.2.1.3.9 they are a copy of the address <see cref="MailingAddress"/> selects; <see cref="ContactAddress.Kind"/> is that address, or Other when none is selected.
    /// </summary>
    public ContactAddress MailingAddressFields => new(
        MailingAddress switch { ContactMailingAddress.Home => ContactAddressKind.Home, ContactMailingAddress.Business => ContactAddressKind.Business, _ => ContactAddressKind.Other },
        Str(MapiPid.BusinessAddressStreet), Str(MapiPid.BusinessAddressCity), Str(MapiPid.BusinessAddressStateOrProvince), Str(MapiPid.BusinessAddressPostalCode),
        Str(MapiPid.BusinessAddressCountry), Str(MapiPid.BusinessAddressPostOfficeBox), Str(MapiPid.PostalAddress));

    /// <summary><c>PidLidPostalAddressId</c>: which address is the mailing address.</summary>
    public ContactMailingAddress MailingAddress =>
        _m.Properties.Get(new MapiNamedProperty(MapiPropertySets.Address, 0x8022u), MapiType.Integer32)?.AsInt32() is int v and >= 1 and <= 3 ? (ContactMailingAddress)v : ContactMailingAddress.None;

    // ---- telephones --------------------------------------------------------------------------------------------------

    /// <summary>Every telephone and fax number the item has, in <see cref="ContactPhoneKind"/> order. A multi-valued property (the second business and home numbers can be) yields its first value.</summary>
    public IReadOnlyList<ContactPhone> Phones
    {
        get
        {
            List<ContactPhone> result = [];
            foreach ((ContactPhoneKind kind, ushort id) in ContactTags.PhoneTags)
            {
                if (Strings(id) is { Count: > 0 } v)
                {
                    result.Add(new ContactPhone(kind, v[0]));
                }
            }

            return result;
        }
    }

    /// <summary>One telephone number.</summary>
    /// <param name="kind">Which property.</param>
    /// <returns>The number, or null.</returns>
    public string? Phone(ContactPhoneKind kind)
    {
        foreach ((ContactPhoneKind k, ushort id) in ContactTags.PhoneTags)
        {
            if (k == kind)
            {
                return Strings(id) is { Count: > 0 } v ? v[0] : null;
            }
        }

        return null;
    }

    // ---- electronic addresses ---------------------------------------------------------------------------------------

    /// <summary>The email slots that are in use (1 to 3), in slot order.</summary>
    public IReadOnlyList<ContactElectronicAddress> EmailAddresses
    {
        get
        {
            List<ContactElectronicAddress> result = [];
            for (int slot = 1; slot <= 3; slot++)
            {
                uint b = 0x8080u + (uint)((slot - 1) * 0x10);
                if (slot == 3)
                {
                    b = 0x80A0u;
                }

                ContactElectronicAddress e = new(ContactElectronicKind.Email, slot, Str(b), Str(b + 2), Str(b + 3), Str(b + 4));
                if (e.Address is not null || e.DisplayName is not null)
                {
                    result.Add(e);
                }
            }

            return result;
        }
    }

    /// <summary>The fax slots that are in use (1 to 3), in slot order.</summary>
    public IReadOnlyList<ContactElectronicAddress> FaxAddresses
    {
        get
        {
            List<ContactElectronicAddress> result = [];
            for (int slot = 1; slot <= 3; slot++)
            {
                uint b = 0x80B0u + (uint)((slot - 1) * 0x10);
                ContactElectronicAddress e = new(ContactElectronicKind.Fax, slot, null, Str(b + 2), Str(b + 3), Str(b + 4));
                if (e.Address is not null)
                {
                    result.Add(e);
                }
            }

            return result;
        }
    }

    // ---- photo and certificates ----------------------------------------------------------------------------------

    /// <summary>The attachment marked as the contact photo (<c>PidTagAttachmentContactPhoto</c>), or null. If several are marked, the first.</summary>
    public MapiAttachment? PhotoAttachment => _m.Attachments.FirstOrDefault(a => a.Properties.Get(ContactTags.AttachmentContactPhoto, MapiType.Boolean)?.AsBoolean() == true);

    /// <summary>Whether <c>PidLidHasPicture</c> is true.</summary>
    public bool HasPicture => _m.Properties.Get(new MapiNamedProperty(MapiPropertySets.Address, 0x8015u), MapiType.Boolean)?.AsBoolean() == true;

    /// <summary><c>PidTagUserX509Certificate</c>: the contact's certificates, DER encoded.</summary>
    public IReadOnlyList<byte[]> X509Certificates
    {
        get
        {
            MapiProperty? p = _m.Properties.Get(ContactTags.UserX509Certificate, MapiType.MultipleBinary);
            if (p is null)
            {
                return [];
            }

            List<byte[]> result = [];
            for (int i = 0; i < p.ValueCount; i++)
            {
                result.Add(p.GetBytes(i).ToArray());
            }

            return result;
        }
    }
}
