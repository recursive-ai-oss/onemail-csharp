using System.Globalization;

namespace RecursiveAI.OneMail.Mapi;

/// <summary>
/// A property tag: a 16-bit property ID in the high half and a 16-bit property type in the low half (MS-OXCDATA 2.9).
/// IDs 0x8000 and above are named properties; the ID is only meaningful together with the mapping of the store or file it came from.
/// </summary>
/// <param name="Value">The 32-bit tag, as written in files (for example <c>0x0037001F</c> for PidTagSubject as a Unicode string).</param>
public readonly record struct MapiTag(uint Value)
{
    /// <summary>Builds a tag from its parts.</summary>
    /// <param name="id">The property ID.</param>
    /// <param name="type">The property type number.</param>
    public MapiTag(ushort id, ushort type)
        : this(((uint)id << 16) | type)
    {
    }

    /// <summary>Builds a tag from an ID and a known type.</summary>
    /// <param name="id">The property ID.</param>
    /// <param name="type">The property type.</param>
    public MapiTag(ushort id, MapiType type)
        : this(((uint)id << 16) | (ushort)type)
    {
    }

    /// <summary>The property ID (high 16 bits).</summary>
    public ushort Id => (ushort)(Value >> 16);

    /// <summary>The property type number (low 16 bits), including numbers the library does not know.</summary>
    public ushort Type => (ushort)Value;

    /// <summary>The type as <see cref="MapiType"/>; a number that is not defined is cast as it is.</summary>
    public MapiType PropertyType => (MapiType)Type;

    /// <summary>Whether the ID is in the named-property range, 0x8000 to 0xFFFE.</summary>
    public bool IsNamed => Id >= 0x8000 && Id != 0xFFFF;

    /// <summary>Whether the type is a multi-valued type.</summary>
    public bool IsMultiValued => MapiTypes.IsMultiValued(Type);

    /// <summary>The same ID with another type.</summary>
    /// <param name="type">The new type.</param>
    /// <returns>The new tag.</returns>
    public MapiTag WithType(MapiType type) => new(Id, type);

    /// <summary>The tag as eight upper-case hex digits, the form used in MSG stream names.</summary>
    /// <returns>For example <c>0037001F</c>.</returns>
    public string ToHex() => Value.ToString("X8", CultureInfo.InvariantCulture);

    /// <inheritdoc/>
    public override string ToString() => "0x" + ToHex();
}

/// <summary>
/// The name of a named property: a property set GUID plus either a 32-bit numeric ID (LID) or a string (MS-OXCDATA 2.6.1).
/// Two names are equal when the set and the numeric ID or the string (ordinal comparison) are equal.
/// </summary>
public readonly record struct MapiNamedProperty
{
    /// <summary>Creates a numeric name.</summary>
    /// <param name="propertySet">The property set GUID.</param>
    /// <param name="lid">The numeric ID.</param>
    public MapiNamedProperty(Guid propertySet, uint lid)
    {
        PropertySet = propertySet;
        Lid = lid;
        Name = null;
    }

    /// <summary>Creates a string name.</summary>
    /// <param name="propertySet">The property set GUID.</param>
    /// <param name="name">The name; not null.</param>
    public MapiNamedProperty(Guid propertySet, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        PropertySet = propertySet;
        Lid = 0;
        Name = name;
    }

    /// <summary>The property set GUID.</summary>
    public Guid PropertySet { get; }

    /// <summary>The numeric ID; zero (and meaningless) for a string name.</summary>
    public uint Lid { get; }

    /// <summary>The string name, or null for a numeric name.</summary>
    public string? Name { get; }

    /// <summary>Whether this is a string name.</summary>
    public bool IsString => Name is not null;

    /// <inheritdoc/>
    public override string ToString() => Name is null
        ? $"{{{PropertySet}}}/0x{Lid:X4}"
        : $"{{{PropertySet}}}/\"{Name}\"";
}

/// <summary>Property set GUIDs of MS-OXPROPS 1.3.2 that readers meet most.</summary>
public static class MapiPropertySets
{
    /// <summary>PS_MAPI: the set of the tagged (non-named) properties.</summary>
    public static readonly Guid Mapi = new("00020328-0000-0000-c000-000000000046");

    /// <summary>PS_PUBLIC_STRINGS: string-named properties such as Keywords.</summary>
    public static readonly Guid PublicStrings = new("00020329-0000-0000-c000-000000000046");

    /// <summary>PS_INTERNET_HEADERS: string-named properties holding Internet headers.</summary>
    public static readonly Guid InternetHeaders = new("00020386-0000-0000-c000-000000000046");

    /// <summary>PSETID_Common: reminders, categories, billing and other common item properties.</summary>
    public static readonly Guid Common = new("00062008-0000-0000-c000-000000000046");

    /// <summary>PSETID_Address: contact properties.</summary>
    public static readonly Guid Address = new("00062004-0000-0000-c000-000000000046");

    /// <summary>PSETID_Appointment: calendar item properties.</summary>
    public static readonly Guid Appointment = new("00062002-0000-0000-c000-000000000046");

    /// <summary>PSETID_Meeting: meeting request properties.</summary>
    public static readonly Guid Meeting = new("6ed8da90-450b-101b-98da-00aa003f1305");

    /// <summary>PSETID_Task: task properties.</summary>
    public static readonly Guid Task = new("00062003-0000-0000-c000-000000000046");

    /// <summary>PSETID_Log: journal properties.</summary>
    public static readonly Guid Log = new("0006200a-0000-0000-c000-000000000046");

    /// <summary>PSETID_Note: sticky note properties.</summary>
    public static readonly Guid Note = new("0006200e-0000-0000-c000-000000000046");

    /// <summary>PSETID_Attachment: attachment properties that have names.</summary>
    public static readonly Guid Attachment = new("96357f7f-59e1-47d0-99a7-46515c183b54");

    /// <summary>PSETID_Sharing: sharing message properties.</summary>
    public static readonly Guid Sharing = new("00062040-0000-0000-c000-000000000046");

    /// <summary>PSETID_Messaging: messaging properties such as the Exchange-only ones.</summary>
    public static readonly Guid Messaging = new("41f28f13-83f4-4114-a584-eedb5a6b0bff");
}
