namespace RecursiveAI.OneMail.Mapi;

/// <summary>What a MAPI reader found wrong or had to guess. Kinds named <c>Ambiguous*</c> are the documented single resolution of a known ambiguity (see <c>docs/msg.md</c>).</summary>
public enum MapiDefectKind
{
    // ---- the container
    /// <summary>The input is not a compound file (no signature). Nothing was read.</summary>
    NotACompoundFile,
    /// <summary>The compound file's structure is damaged (reported by the compound file reader). Nothing was read.</summary>
    CompoundFileInvalid,
    /// <summary>The compound file has no <c>__properties_version1.0</c> stream in its root, so it is not a message.</summary>
    NotAMessage,
    /// <summary>A resource limit was reached; the rest of that part was not read.</summary>
    LimitReached,
    /// <summary>More defects were found than the configured limit; the rest are only counted.</summary>
    DefectLimitReached,

    // ---- property streams
    /// <summary>An object storage has no property stream; it is read as having no properties.</summary>
    PropertyStreamMissing,
    /// <summary>The property stream is shorter than its header, or its data is not a whole number of 16-byte entries; the partial entry is ignored.</summary>
    PropertyStreamTruncated,
    /// <summary>The same property tag has more than one entry. Both are kept; lookups return the last.</summary>
    AmbiguousDuplicateProperty,
    /// <summary>A property's type is not defined in MS-OXCDATA. It is kept; a value stream of that name is used if one exists, else the 8 bytes of the entry.</summary>
    UnknownPropertyType,

    // ---- values
    /// <summary>A variable-length property has an entry but no value stream. It is kept with an empty value.</summary>
    ValueStreamMissing,
    /// <summary>The size in a property entry does not match the value stream (MS-OXMSG 2.4.2.2). The stream's bytes are the value.</summary>
    ValueSizeMismatch,
    /// <summary>A PtypString value stream has an odd number of bytes; the last byte is dropped when decoding.</summary>
    OddStringLength,
    /// <summary>A fixed-length multi-valued stream is not a whole number of elements; the partial element is kept as the last value.</summary>
    MultiValueTrailingBytes,
    /// <summary>The length stream of a variable-length multi-valued property disagrees with the value streams present.</summary>
    MultiValueLengthMismatch,
    /// <summary>A value stream of a multi-valued property is missing; that element is empty.</summary>
    MultiValueStreamMissing,
    /// <summary>A value stream has no entry in the property stream. It is read as a property (value streams are the data; the entry only adds flags).</summary>
    AmbiguousOrphanValueStream,
    /// <summary>A stream or storage inside an object is not part of the format and was not read.</summary>
    UnreferencedStream,
    /// <summary>The store support mask says strings are Unicode (or not), but string properties of the other kind are present.</summary>
    AmbiguousStringType,

    // ---- named properties
    /// <summary>A property in the named range has no mapping, or the file has no named property mapping storage at all.</summary>
    NamedPropertyUnmapped,
    /// <summary>The GUID stream is not a whole number of 16-byte GUIDs.</summary>
    NamedPropertyGuidStreamInvalid,
    /// <summary>The entry stream is not a whole number of 8-byte entries.</summary>
    NamedPropertyEntryStreamInvalid,
    /// <summary>A named property entry points at a GUID or string that does not exist.</summary>
    NamedPropertyReferenceInvalid,
    /// <summary>An entry's property index differs from its position in the entry stream; the position is the ID (MS-OXMSG 3.2.1).</summary>
    NamedPropertyIndexMismatch,
    /// <summary>An embedded message carries a named property mapping storage of its own; it is ignored (MS-OXMSG 2.2.2.1).</summary>
    NestedNamedPropertyMap,

    // ---- children
    /// <summary>The recipient count in the property stream header differs from the recipient storages found.</summary>
    RecipientCountMismatch,
    /// <summary>The attachment count in the property stream header differs from the attachment storages found.</summary>
    AttachmentCountMismatch,
    /// <summary>A recipient or attachment storage name is not <c>__recip_version1.0_#XXXXXXXX</c> with hexadecimal digits; it is ignored.</summary>
    StorageNameInvalid,
    /// <summary>An embedded message or object attachment has no <c>__substg1.0_3701000D</c> storage.</summary>
    EmbeddedObjectMissing,
    /// <summary>The <c>__substg1.0_3701000D</c> entry is a stream, not a storage; its bytes are the property value.</summary>
    EmbeddedObjectNotStorage,
    /// <summary>Embedded messages are nested deeper than the limit; the deeper one is kept as a raw storage.</summary>
    EmbeddingTooDeep,

    // ---- text
    /// <summary>
    /// The item has 8-bit strings but no PidTagMessageCodepage, so their code page is taken from the message locale, the Internet
    /// code page, the enclosing item or the default, in that order (see <see cref="MapiCodePageSource"/>).
    /// </summary>
    AmbiguousCodePage,

    /// <summary>The code page number names no encoding the library has; windows-1252 is used for 8-bit strings.</summary>
    UnsupportedCodePage,

    // ---- PST / OST objects

    /// <summary>A node the structure refers to (a folder, message, table) is not in the node tree; it is skipped.</summary>
    NodeMissing,

    /// <summary>A node is there but is not a property context or table context of the expected kind; it is skipped.</summary>
    ContextUnreadable,

    /// <summary>A value could not be read from its heap or subnode; the property is kept with an empty value.</summary>
    ValueUnreadable,

    /// <summary>A lower layer (node database or lists-tables-properties) reported a defect while the object was read; the detail names it.</summary>
    LowerLayerDefect,

    /// <summary>A folder is its own ancestor, or nesting is deeper than the limit; the loop is not followed.</summary>
    FolderLoop,

    /// <summary>A message has attachment subnodes but no attachment table; the subnodes found are used (ambiguity table).</summary>
    AmbiguousAttachmentTableMissing,

    /// <summary>An object property (an embedded message) names its subnode directly instead of through the NID-and-size structure; the NID is used (ambiguity table).</summary>
    AmbiguousObjectReference,

    /// <summary>A table's row identifier is not the NID the object it describes is stored under, or an object is listed twice.</summary>
    RowIdMismatch,
}

/// <summary>One thing a MAPI reader found wrong, and where.</summary>
/// <param name="Kind">What was wrong.</param>
/// <param name="Location">Where: for MSG, the storage and stream path inside the compound file; empty for the whole input.</param>
/// <param name="Offset">The byte offset inside the stream at <paramref name="Location"/>, or 0 when the problem concerns the stream as a whole.</param>
/// <param name="Detail">A short description with the values involved.</param>
public readonly record struct MapiDefect(MapiDefectKind Kind, string Location, long Offset, string Detail)
{
    /// <summary>True for the documented resolutions of known ambiguities (kinds named <c>Ambiguous*</c>).</summary>
    public bool IsAmbiguity => Kind is MapiDefectKind.AmbiguousDuplicateProperty or MapiDefectKind.AmbiguousOrphanValueStream
        or MapiDefectKind.AmbiguousStringType or MapiDefectKind.AmbiguousCodePage
        or MapiDefectKind.AmbiguousAttachmentTableMissing or MapiDefectKind.AmbiguousObjectReference;

    /// <inheritdoc/>
    public override string ToString() => $"{Kind} at {Location}+{Offset}: {Detail}";
}
