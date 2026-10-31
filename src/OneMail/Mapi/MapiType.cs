namespace RecursiveAI.OneMail.Mapi;

/// <summary>
/// The property types of MS-OXCDATA 2.11.1. The numeric values are the low 16 bits of a property tag.
/// A multi-valued type is its element type with 0x1000 added.
/// </summary>
public enum MapiType : ushort
{
    /// <summary>PtypUnspecified (0x0000): only valid in a request; kept when a file carries it.</summary>
    Unspecified = 0x0000,

    /// <summary>PtypNull (0x0001): no value.</summary>
    Null = 0x0001,

    /// <summary>PtypInteger16 (0x0002): a signed 16-bit integer.</summary>
    Integer16 = 0x0002,

    /// <summary>PtypInteger32 (0x0003): a signed 32-bit integer.</summary>
    Integer32 = 0x0003,

    /// <summary>PtypFloating32 (0x0004): an IEEE 754 single.</summary>
    Floating32 = 0x0004,

    /// <summary>PtypFloating64 (0x0005): an IEEE 754 double.</summary>
    Floating64 = 0x0005,

    /// <summary>PtypCurrency (0x0006): a signed 64-bit integer in units of 1/10,000.</summary>
    Currency = 0x0006,

    /// <summary>PtypFloatingTime (0x0007): a double counting days since 1899-12-30.</summary>
    FloatingTime = 0x0007,

    /// <summary>PtypErrorCode (0x000A): a 32-bit HRESULT-style error code.</summary>
    ErrorCode = 0x000A,

    /// <summary>PtypBoolean (0x000B): one byte, zero for false.</summary>
    Boolean = 0x000B,

    /// <summary>PtypObject (0x000D): an embedded object (an embedded message or a storage).</summary>
    EmbeddedObject = 0x000D,

    /// <summary>PtypInteger64 (0x0014): a signed 64-bit integer.</summary>
    Integer64 = 0x0014,

    /// <summary>PtypString8 (0x001E): a string in the item's code page.</summary>
    String8 = 0x001E,

    /// <summary>PtypString (0x001F): a UTF-16 little-endian string.</summary>
    UnicodeString = 0x001F,

    /// <summary>PtypTime (0x0040): a FILETIME, 100 ns ticks since 1601-01-01 UTC.</summary>
    Time = 0x0040,

    /// <summary>PtypGuid (0x0048): 16 bytes in the Windows GUID layout.</summary>
    GuidValue = 0x0048,

    /// <summary>PtypServerId (0x00FB): a counted, structured binary value.</summary>
    ServerId = 0x00FB,

    /// <summary>PtypRestriction (0x00FD): a serialized restriction.</summary>
    Restriction = 0x00FD,

    /// <summary>PtypRuleAction (0x00FE): serialized rule actions.</summary>
    RuleAction = 0x00FE,

    /// <summary>PtypBinary (0x0102): counted bytes.</summary>
    Binary = 0x0102,

    /// <summary>PtypMultipleInteger16 (0x1002).</summary>
    MultipleInteger16 = 0x1002,

    /// <summary>PtypMultipleInteger32 (0x1003).</summary>
    MultipleInteger32 = 0x1003,

    /// <summary>PtypMultipleFloating32 (0x1004).</summary>
    MultipleFloating32 = 0x1004,

    /// <summary>PtypMultipleFloating64 (0x1005).</summary>
    MultipleFloating64 = 0x1005,

    /// <summary>PtypMultipleCurrency (0x1006).</summary>
    MultipleCurrency = 0x1006,

    /// <summary>PtypMultipleFloatingTime (0x1007).</summary>
    MultipleFloatingTime = 0x1007,

    /// <summary>PtypMultipleInteger64 (0x1014).</summary>
    MultipleInteger64 = 0x1014,

    /// <summary>PtypMultipleString8 (0x101E).</summary>
    MultipleString8 = 0x101E,

    /// <summary>PtypMultipleString (0x101F).</summary>
    MultipleString = 0x101F,

    /// <summary>PtypMultipleTime (0x1040).</summary>
    MultipleTime = 0x1040,

    /// <summary>PtypMultipleGuid (0x1048).</summary>
    MultipleGuid = 0x1048,

    /// <summary>PtypMultipleBinary (0x1102).</summary>
    MultipleBinary = 0x1102,
}

/// <summary>Facts about property type numbers, including numbers that are not in <see cref="MapiType"/>.</summary>
public static class MapiTypes
{
    /// <summary>The flag that turns an element type into its multi-valued type.</summary>
    public const ushort MultiValuedFlag = 0x1000;

    /// <summary>Whether the type number has the multi-valued flag (and is not a larger, undefined range).</summary>
    /// <param name="type">The low 16 bits of a property tag.</param>
    /// <returns>True for 0x1000 to 0x1FFF.</returns>
    public static bool IsMultiValued(ushort type) => (type & 0xF000) == MultiValuedFlag;

    /// <summary>The element type of a multi-valued type, or the type itself when it is single-valued.</summary>
    /// <param name="type">The low 16 bits of a property tag.</param>
    /// <returns>The type number without the multi-valued flag.</returns>
    public static ushort ElementType(ushort type) => IsMultiValued(type) ? (ushort)(type & ~MultiValuedFlag) : type;

    /// <summary>
    /// The size in bytes of one value of a fixed-length type: 2, 4, 8 or 16 for the types MS-OXMSG 2.1.2 lists (and 16 for a GUID,
    /// which MS-OXMSG stores like a variable-length value), 1 for a boolean.
    /// </summary>
    /// <param name="elementType">A single-valued type number.</param>
    /// <returns>The size, or 0 when the type is variable-length or not known.</returns>
    public static int FixedSize(ushort elementType) => (MapiType)elementType switch
    {
        MapiType.Integer16 => 2,
        MapiType.Integer32 or MapiType.Floating32 or MapiType.ErrorCode => 4,
        MapiType.Floating64 or MapiType.Currency or MapiType.FloatingTime or MapiType.Integer64 or MapiType.Time => 8,
        MapiType.Boolean => 1,
        MapiType.GuidValue => 16,
        _ => 0,
    };

    /// <summary>Whether a single-valued type is one of the string types.</summary>
    /// <param name="elementType">A single-valued type number.</param>
    /// <returns>True for PtypString and PtypString8.</returns>
    public static bool IsString(ushort elementType) => elementType is (ushort)MapiType.UnicodeString or (ushort)MapiType.String8;

    /// <summary>Whether the library knows the type number (single-valued or multi-valued form).</summary>
    /// <param name="type">The low 16 bits of a property tag.</param>
    /// <returns>True when the type is defined in MS-OXCDATA 2.11.1 and has a multi-valued form where one exists.</returns>
    public static bool IsKnown(ushort type)
    {
        ushort e = ElementType(type);
        if (IsMultiValued(type))
        {
            return e is (ushort)MapiType.Integer16 or (ushort)MapiType.Integer32 or (ushort)MapiType.Floating32 or (ushort)MapiType.Floating64
                or (ushort)MapiType.Currency or (ushort)MapiType.FloatingTime or (ushort)MapiType.Integer64 or (ushort)MapiType.String8
                or (ushort)MapiType.UnicodeString or (ushort)MapiType.Time or (ushort)MapiType.GuidValue or (ushort)MapiType.Binary;
        }

        return e is 0 or 1 or 2 or 3 or 4 or 5 or 6 or 7 or 0x0A or 0x0B or 0x0D or 0x14 or 0x1E or 0x1F or 0x40 or 0x48 or 0xFB or 0xFD or 0xFE or 0x102;
    }
}
