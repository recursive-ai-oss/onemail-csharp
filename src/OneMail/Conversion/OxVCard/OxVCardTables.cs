using System.Buffers.Binary;
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
/// The property tables and small helpers both vCard conversions share (MS-OXVCARD 2.1.3 and MS-OXOCNTC 2.2).
/// Nothing here is public: the conversions are the API.
/// </summary>
internal static class OxVCardTables
{
    public const string Family = "vcard";
    public const string MapiFamily = "mapi";

    /// <summary>The PRODID the converter writes (MS-OXVCARD 2.1.3.7.3 leaves the value to the implementer).</summary>
    public const string ProductId = "-//RecursiveAI//OneMail//EN";

    public static readonly MapiNamedProperty Keywords = new(MapiPropertySets.PublicStrings, "Keywords");

    /// <summary>The ADR TYPE words that name the other address on import (MS-OXVCARD 2.1.3.3.1: postal, dom, intl, parcel).</summary>
    public static bool IsOtherAddressType(string t) => t is "postal" or "dom" or "intl" or "parcel";

    /// <summary>The properties MS-OXVCARD carries: reading them is part of the conversion, so the loss report does not list them again.</summary>
    public static readonly HashSet<ushort> MappedTags =
    [
        0x3001, 0x0E1D, 0x0070, 0x0037, // FN
        0x3A06, 0x3A11, 0x3A44, 0x3A45, 0x3A05, // N
        0x3A4F, // NICKNAME
        0x3A42, 0x3A41, // BDAY, X-MS-ANNIVERSARY
        0x3A16, 0x3A18, 0x3A17, 0x3A46, // ORG, TITLE, ROLE
        0x3A50, 0x3A51, // URL
        0x1000, 0x3008, 0x0036, 0x3A70, // NOTE, REV, CLASS, KEY
        0x3A30, 0x3A4E, 0x3A48, 0x3A58, 0x3A43, // assistant, manager, spouse, children, interests
        0x3A4D, // GENDER (vCard 4.0)
        0x3A15, 0x3A26, 0x3A27, 0x3A28, 0x3A29, 0x3A2A, 0x3A2B, // the mailing address, a copy of the selected address
        0x3A59, 0x3A5A, 0x3A5B, 0x3A5C, 0x3A5D, 0x3A5E, // home address
        0x3A5F, 0x3A60, 0x3A61, 0x3A62, 0x3A63, 0x3A64, // other address
    ];

    /// <summary>Named properties of PSETID_Address that are carried (lids).</summary>
    public static readonly HashSet<uint> MappedAddressLids =
    [
        0x8005, // file under: derived from the name
        0x8015, // has picture: derived from the photo attachment
        0x801A, 0x801B, 0x801C, // the formatted addresses
        0x8022, // postal address id
        0x8045, 0x8046, 0x8047, 0x8048, 0x8049, 0x804A, // work address
        0x8062, // IM
        0x804F, 0x8050, 0x8051, 0x8052, // user fields
        0x80D8, // free/busy
        0x80DE, 0x80DF, // birthday / anniversary local
        0x8080, 0x8082, 0x8083, 0x8084, 0x8090, 0x8092, 0x8093, 0x8094, 0x80A0, 0x80A2, 0x80A3, 0x80A4, // email slots
    ];

    /// <summary>Store-internal properties that are listed as bookkeeping rather than as lost content.</summary>
    public static readonly HashSet<ushort> Bookkeeping =
    [
        0x001A, 0x0E07, 0x0E08, 0x0E17, 0x0E1B, 0x0E1F, 0x0E23, 0x0E27, 0x0E2D, 0x0E79, 0x0FF4, 0x0FF5, 0x0FF6, 0x0FF7, 0x0FF9, 0x0FFA, 0x0FFB, 0x0FFE, 0x0FFF,
        0x3007, 0x3FDE, 0x3FF1, 0x3FF8, 0x3FF9, 0x3FFA, 0x3FFB, 0x3FFD, 0x0E69, 0x0E6A, 0x6619, 0x6670, 0x66A1, 0x7FFD, 0x7FFE,
    ];

    public static string Label(MapiProperty p) => p.Name is { } n ? n.ToString() : MapiPropertyNames.GetName(p.Id) ?? p.Tag.ToString();

    public static string TypeOf(MapiContact c, ContactAddressKind k) => k switch { ContactAddressKind.Home => "home", ContactAddressKind.Business => "work", _ => "postal" };

    // 2.1.3.2.4: PHOTO types are .bmp, .gif, .jpeg and .png; a file extension or the image's first bytes name one.
    public static string? ImageTypeOf(string? extension, ReadOnlySpan<byte> data)
    {
        string e = (extension ?? string.Empty).TrimStart('.').ToLowerInvariant();
        string? fromExt = e switch { "jpg" or "jpeg" or "jpe" => "jpeg", "png" => "png", "gif" => "gif", "bmp" => "bmp", _ => null };
        if (fromExt is not null)
        {
            return fromExt;
        }

        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return "jpeg";
        }

        if (data.Length >= 8 && data[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "png";
        }

        if (data.Length >= 4 && data[..4].SequenceEqual("GIF8"u8))
        {
            return "gif";
        }

        return data.Length >= 2 && data[0] == 'B' && data[1] == 'M' ? "bmp" : null;
    }

    // ---- dates ----------------------------------------------------------------------------------------------------------

    // MS-OXOCNTC 2.2.1.5.1: PidTagBirthday is the birthday at 11:59 UTC; 2.2.1.5.2: PidLidBirthdayLocal is 0:00 local time, saved without conversion.
    public static readonly TimeSpan EventTimeUtc = new(11, 59, 0);

    public static string FormatDate(DateOnly d, bool extended) =>
        extended ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : d.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    public static DateTimeOffset FileTimeOf(DateOnly d, TimeSpan time) => new(d.Year, d.Month, d.Day, time.Hours, time.Minutes, 0, TimeSpan.Zero);

    // ---- property builders ----------------------------------------------------------------------------------------------

    public static MapiProperty Named(ushort id, MapiNamedProperty name, MapiType type, byte[] raw) =>
        MapiProperty.CreateOwned(new MapiTag(id, type), raw, name);

    public static byte[] Utf16(string s) => Encoding.Unicode.GetBytes(s);

    public static byte[] Int32(int v)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, v);
        return b;
    }

    public static byte[] FileTime(DateTimeOffset t)
    {
        byte[] b = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(b, t.UtcTicks - 504911232000000000L);
        return b;
    }

    /// <summary>The text of a vCard value for a MAPI string: line feeds become CR LF, which is how Outlook stores line breaks in contact text.</summary>
    public static string ToMapiText(string s) =>
        s.Contains('\n', StringComparison.Ordinal) ? s.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal) : s;

    /// <summary>A single-value tagged string property as it is stored.</summary>
    public static bool TryString(MapiMessage m, ushort id, out string value)
    {
        value = m.GetString(id) ?? string.Empty;
        return value.Length > 0;
    }
}
