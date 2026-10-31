namespace RecursiveAI.OneMail.Tnef;

/// <summary>
/// What the TNEF reader found wrong or had to guess. Kinds named <c>Ambiguous*</c> are the documented single resolution of a known
/// ambiguity (see <c>docs/tnef.md</c> and <c>docs/ambiguities.md</c>); the rest are repairs or tolerated damage.
/// </summary>
public enum TnefDefectKind
{
    // ---- the attribute stream
    /// <summary>The input does not start with the TNEF signature 0x223E9F78 (MS-OXTNEF 2.1.3.2). Nothing was read.</summary>
    NotTnef,

    /// <summary>The input ends inside an attribute header, or inside an attribute's data or checksum. The cut attribute is kept as far as it goes and written back as it was.</summary>
    AttributeTruncated,

    /// <summary>Fewer bytes than an attribute header follow the last attribute; they are kept as trailing bytes.</summary>
    TrailingBytes,

    /// <summary>An attribute's level byte is neither 1 (message) nor 2 (attachment). The attribute is kept in the attribute list and not mapped to properties.</summary>
    InvalidLevel,

    /// <summary>The stored checksum differs from the sum of the data bytes. The data is used as it is.</summary>
    ChecksumMismatch,

    /// <summary>A resource limit was reached; the rest of that part was not read.</summary>
    LimitReached,

    /// <summary>More defects were found than the configured limit; the rest are only counted.</summary>
    DefectLimitReached,

    // ---- message and attachment attributes
    /// <summary>The attTnefVersion attribute is missing or does not hold 0x00010000 (MS-OXTNEF 2.3.3.1 says readers reject it; this reader continues).</summary>
    UnexpectedVersion,

    /// <summary>The attOemCodepage attribute is missing, shorter than 4 bytes or zero. The Internet code page or windows-1252 stands in.</summary>
    MissingCodePage,

    /// <summary>The code page number names no encoding the library has; windows-1252 is used for 8-bit strings.</summary>
    UnsupportedCodePage,

    /// <summary>A fixed-layout attribute has the wrong length or inner structure; it is not mapped to properties.</summary>
    AttributeMalformed,

    /// <summary>An attribute id the library does not map; it stays in the attribute list.</summary>
    UnknownAttribute,

    /// <summary>An attachment-level attribute appears before any attAttachRendData; an attachment is started for it.</summary>
    AttachmentWithoutRendData,

    /// <summary>A date attribute holds an impossible date or time; it is not mapped.</summary>
    InvalidDate,

    // ---- encapsulated properties
    /// <summary>A property list ends before its last property is complete, or a count promises more than the data holds; the properties read so far are kept.</summary>
    PropertyListTruncated,

    /// <summary>A property has a type MS-OXTNEF does not define, so its size cannot be known; the rest of that list is not read.</summary>
    UnknownPropertyType,

    /// <summary>A named property's name specification is invalid (kind other than number or string, or a bad string length); the rest of that list is not read.</summary>
    InvalidNamedProperty,

    /// <summary>A string value has no terminator; it is kept as it is.</summary>
    StringNotTerminated,

    /// <summary>A single-valued variable-length property carries a value count other than 1; the first value is kept and the others dropped.</summary>
    ValueCountMismatch,

    // ---- embedded messages and bodies
    /// <summary>A PtypObject attachment value is not a message object holding a TNEF stream; it is kept as a raw object property.</summary>
    EmbeddedMessageInvalid,

    /// <summary>Embedded messages are nested deeper than the limit; the deeper one is kept as a raw object property.</summary>
    EmbeddingTooDeep,

    /// <summary>A compressed RTF header announces a decompressed size over the limit, or one no LZ-compressed stream of that length could expand to. The bytes are kept raw.</summary>
    RtfExceedsLimit,

    // ---- documented resolutions of ambiguities
    /// <summary>The same message-level legacy attribute appears twice; the last one is used.</summary>
    AmbiguousDuplicateAttribute,

    /// <summary>A legacy attribute and an encapsulated property carry different values for the same thing; the encapsulated property wins (MS-OXTNEF 2.1.3).</summary>
    AmbiguousLegacyConflict,

    /// <summary>The legacy message class name was replaced by its modern equivalent (MS-OXTNEF 2.3.3.4).</summary>
    AmbiguousMessageClass,

    /// <summary>A message class attribute's checksum is wrong; it is ignored, as legacy writers produced bad ones (MS-OXTNEF 2.3.3.4).</summary>
    AmbiguousMessageClassChecksum,

    /// <summary>The attachment is an embedded message or OLE object and also carries attAttachData; the attribute is ignored (MS-OXTNEF 2.3.3.7).</summary>
    AmbiguousAttachDataIgnored,

    /// <summary>attOemCodepage and the encapsulated PidTagMessageCodepage differ; attOemCodepage is what 8-bit strings are read with (MS-OXTNEF 2.3.3.2).</summary>
    AmbiguousCodePage,

    /// <summary>attOwner has no meeting class to say which property group it maps to; PidTagSentRepresenting is used.</summary>
    AmbiguousOwner,
}

/// <summary>One thing the TNEF reader found wrong, and where.</summary>
/// <param name="Kind">What was wrong.</param>
/// <param name="Offset">The byte offset in the TNEF stream (the stream the defect was found in: an embedded message's offsets are relative to its own stream).</param>
/// <param name="Detail">A short description with the values involved.</param>
public readonly record struct TnefDefect(TnefDefectKind Kind, long Offset, string Detail)
{
    /// <summary>True for the documented resolutions of known ambiguities (kinds named <c>Ambiguous*</c>).</summary>
    public bool IsAmbiguity => Kind >= TnefDefectKind.AmbiguousDuplicateAttribute;

    /// <inheritdoc/>
    public override string ToString() => $"{Kind} at {Offset}: {Detail}";
}
