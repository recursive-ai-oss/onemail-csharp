using RecursiveAI.OneMail.Mapi;

namespace RecursiveAI.OneMail.Msg;

/// <summary>Which MS-OXMSG storage an object was read from; decides the property stream header (MS-OXMSG 2.4.1).</summary>
public enum MsgObjectKind
{
    /// <summary>The top level of the file: a 32-byte header with the recipient and attachment counts.</summary>
    Message,

    /// <summary>An embedded message storage (<c>__substg1.0_3701000D</c> of an attachment): a 24-byte header with the counts.</summary>
    EmbeddedMessage,

    /// <summary>A <c>__recip_version1.0_#XXXXXXXX</c> storage: an 8-byte header.</summary>
    Recipient,

    /// <summary>An <c>__attach_version1.0_#XXXXXXXX</c> storage: an 8-byte header.</summary>
    Attachment,
}

/// <summary>Where a property's value lives in the storage (MS-OXMSG 2.1.2 to 2.1.4).</summary>
public enum MsgValueStorage
{
    /// <summary>In the property stream entry itself: the fixed-length types.</summary>
    Inline,

    /// <summary>In one stream named <c>__substg1.0_&lt;tag&gt;</c>.</summary>
    Stream,

    /// <summary>A fixed-length multi-valued property: all elements back to back in one stream named for the tag.</summary>
    MultiFixedStream,

    /// <summary>A variable-length multi-valued property: a length stream named for the tag and one stream per element, <c>&lt;tag&gt;-&lt;index&gt;</c>.</summary>
    MultiVariableStreams,

    /// <summary>An object property whose value is a storage named for the tag (the attachment data object); it has no value bytes.</summary>
    ObjectStorage,

    /// <summary>The entry exists but its value stream does not (see <see cref="Mapi.MapiDefectKind.ValueStreamMissing"/>); the property has no value bytes and none are written.</summary>
    Missing,
}

/// <summary>
/// What an MSG property stream entry says beyond the property's value, kept so a stream can be written again exactly.
/// </summary>
/// <param name="Flags">The entry's flags (PROPATTR_MANDATORY 1, PROPATTR_READABLE 2, PROPATTR_WRITABLE 4).</param>
/// <param name="Tail">Bytes 8 to 15 of the entry as a little-endian number. For a fixed-length property they hold the value followed by padding;
/// for the others the low 32 bits are the size field and the high 32 bits the reserved field (0x01 or 0x04 for embedded and storage attachments).</param>
/// <param name="Storage">Where the value lives.</param>
/// <param name="HasEntry">False for a property that came from a value stream with no entry in the property stream (see <see cref="MapiDefectKind.AmbiguousOrphanValueStream"/>); such a stream is written without an entry.</param>
public readonly record struct MsgEntryMeta(uint Flags, ulong Tail, MsgValueStorage Storage, bool HasEntry);

/// <summary>The facts about an MSG storage that are not properties but are needed to write it again byte for byte.</summary>
public sealed class MsgObjectLayout
{
    internal MsgObjectLayout(MsgObjectKind kind, string path, byte[] rawHeader, MsgEntryMeta[] entries)
    {
        Kind = kind;
        Path = path;
        RawHeader = rawHeader;
        Entries = entries;
    }

    /// <summary>Which kind of storage the object was read from.</summary>
    public MsgObjectKind Kind { get; }

    /// <summary>The storage path inside the compound file, stored names joined by <c>/</c>; empty for the top level.</summary>
    public string Path { get; }

    /// <summary>The property stream header as stored: 32, 24 or 8 bytes (fewer if the stream was cut short). Reserved fields are included.</summary>
    public byte[] RawHeader { get; }

    /// <summary>One element per property in <see cref="MapiObject.Properties"/> as it was when the object was opened, in the same order.</summary>
    public IReadOnlyList<MsgEntryMeta> Entries { get; }

    /// <summary>The next recipient number from the header (MS-OXMSG 2.4.1), or null for attachment and recipient storages and short headers.</summary>
    public int? NextRecipientId => Kind <= MsgObjectKind.EmbeddedMessage && RawHeader.Length >= 12 ? ReadInt(8) : null;

    /// <summary>The next attachment number from the header, or null when there is none.</summary>
    public int? NextAttachmentId => Kind <= MsgObjectKind.EmbeddedMessage && RawHeader.Length >= 16 ? ReadInt(12) : null;

    /// <summary>The recipient count the header declares, or null when there is none.</summary>
    public int? DeclaredRecipientCount => Kind <= MsgObjectKind.EmbeddedMessage && RawHeader.Length >= 20 ? ReadInt(16) : null;

    /// <summary>The attachment count the header declares, or null when there is none.</summary>
    public int? DeclaredAttachmentCount => Kind <= MsgObjectKind.EmbeddedMessage && RawHeader.Length >= 24 ? ReadInt(20) : null;

    private int ReadInt(int offset) => System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(RawHeader.AsSpan(offset));
}

/// <summary>An item read from an MSG file, with the layout needed to write its streams again.</summary>
public interface IMsgItem
{
    /// <summary>The storage facts of this object.</summary>
    MsgObjectLayout Layout { get; }
}

/// <summary>A stream of a compound file, by name inside its storage.</summary>
/// <param name="Name">The stream's name, for example <c>__properties_version1.0</c> or <c>__substg1.0_0037001F</c>.</param>
/// <param name="Data">The stream's bytes.</param>
public readonly record struct MsgStreamData(string Name, byte[] Data);
