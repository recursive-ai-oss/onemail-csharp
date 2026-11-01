namespace RecursiveAI.OneMail.Msg;

/// <summary>How <see cref="MsgWriter"/> stores string properties (MS-OXMSG 2.1.3: a message holds either all Unicode or all 8-bit strings).</summary>
public enum MsgStringMode
{
    /// <summary>
    /// Every string is written as PtypString (UTF-16LE) and <c>STORE_UNICODE_OK</c> is set in PidTagStoreSupportMask of the top-level message.
    /// 8-bit strings in the model are decoded with their object's code page first.
    /// </summary>
    Unicode = 0,

    /// <summary>
    /// Every string is written as PtypString8 in <see cref="MsgWriterOptions.AnsiCodePage"/>, <c>STORE_UNICODE_OK</c> is cleared, and PidTagMessageCodepage is
    /// set on each message. Characters the code page cannot hold become <c>?</c> and are reported.
    /// </summary>
    Ansi,

    /// <summary>
    /// Properties are written with the types they have and nothing is added: the choice for writing a model back exactly as it was read.
    /// The caller is then responsible for the model following MS-OXMSG 2.1.3.
    /// </summary>
    AsGiven,
}

/// <summary>Choices and limits for <see cref="MsgWriter"/>. The defaults write what Outlook opens and refuse models that would not read back.</summary>
public sealed class MsgWriterOptions
{
    /// <summary>How strings are stored. Default <see cref="MsgStringMode.Unicode"/>.</summary>
    public MsgStringMode Strings { get; init; } = MsgStringMode.Unicode;

    /// <summary>The Windows code page for <see cref="MsgStringMode.Ansi"/>. Default 1252. It must be one the library has an encoder for.</summary>
    public int AnsiCodePage { get; init; } = 1252;

    /// <summary>
    /// Whether the file is a template (<c>.oft</c>): MSGFLAG_UNSENT (0x8) is set in PidTagMessageFlags of the top-level message, which is what makes
    /// Outlook treat a message file as a template to start from. The container is otherwise the same as for <c>.msg</c>. Default false.
    /// </summary>
    public bool Template { get; init; }

    /// <summary>
    /// Whether a string property with no characters is written (as a zero-length stream). MS-OXMSG 2.4.2.2 requires at least one character and
    /// Outlook 2010 and earlier refuse such files, so by default an empty string is left out and reported. Default false.
    /// </summary>
    public bool KeepEmptyStrings { get; init; }

    /// <summary>Most properties in one object (message, recipient or attachment). Default 1,048,576, the reader's limit.</summary>
    public int MaxProperties { get; init; } = 1 << 20;

    /// <summary>Most recipients in one message. Default 2,048, the most MS-OXMSG 2.2.1 allows.</summary>
    public int MaxRecipients { get; init; } = 2048;

    /// <summary>Most attachments in one message. Default 2,048, the most MS-OXMSG 2.2.2 allows.</summary>
    public int MaxAttachments { get; init; } = 2048;

    /// <summary>Most levels of embedded messages; the top-level message is level 0. Default 32, the reader's limit.</summary>
    public int MaxEmbeddingDepth { get; init; } = 32;

    /// <summary>Most values in one multi-valued property. Default 1,048,576, the reader's limit.</summary>
    public int MaxMultiValueCount { get; init; } = 1 << 20;

    /// <summary>Most named properties in the file (IDs 0x8000 to 0xFFFE). Default 32,767, all the ID space has.</summary>
    public int MaxNamedProperties { get; init; } = 0x7FFF;

    /// <summary>Largest single value in bytes. Default 1 GiB. A value over 2 GiB - 1 cannot be written in any case (compound file version 3).</summary>
    public long MaxValueBytes { get; init; } = 1L << 30;

    /// <summary>
    /// Most bytes of value data in the whole file. The compound file writer holds stream data in memory until the file is finished,
    /// so this is also the most memory a write can need for data. Default 1 GiB.
    /// </summary>
    public long MaxTotalBytes { get; init; } = 1L << 30;
}

/// <summary>What <see cref="MsgWriter"/> found in a model that it repaired, left out or refused.</summary>
public enum MsgWriteDefectKind
{
    // ---- errors: nothing is written
    /// <summary>An object has more properties than <see cref="MsgWriterOptions.MaxProperties"/>. Error.</summary>
    TooManyProperties,
    /// <summary>A message has more recipients than <see cref="MsgWriterOptions.MaxRecipients"/>. Error.</summary>
    TooManyRecipients,
    /// <summary>A message has more attachments than <see cref="MsgWriterOptions.MaxAttachments"/>. Error.</summary>
    TooManyAttachments,
    /// <summary>Embedded messages nest deeper than <see cref="MsgWriterOptions.MaxEmbeddingDepth"/>, or an embedded message contains itself. Error.</summary>
    EmbeddingTooDeep,
    /// <summary>A multi-valued property has more values than <see cref="MsgWriterOptions.MaxMultiValueCount"/>. Error.</summary>
    TooManyValues,
    /// <summary>A value is larger than <see cref="MsgWriterOptions.MaxValueBytes"/>. Error.</summary>
    ValueTooLarge,
    /// <summary>The values add up to more than <see cref="MsgWriterOptions.MaxTotalBytes"/>. Error.</summary>
    TotalTooLarge,
    /// <summary>More distinct named properties than <see cref="MsgWriterOptions.MaxNamedProperties"/>. Error.</summary>
    TooManyNamedProperties,
    /// <summary>The options name a code page the library has no encoder for. Error.</summary>
    UnsupportedCodePage,
    /// <summary>A value could not be read from its source while the model was checked. Error.</summary>
    ValueUnreadable,

    // ---- warnings: written, with the change described
    /// <summary>A named property has no name (its mapping was missing in the source), so it cannot be stored in a file that holds names. Left out.</summary>
    NamedPropertyUnmapped,
    /// <summary>Two properties had the same tag after named IDs were assigned and strings converted; the later (or the original Unicode one) is kept.</summary>
    DuplicateProperty,
    /// <summary>A string property with no characters was left out (see <see cref="MsgWriterOptions.KeepEmptyStrings"/>).</summary>
    EmptyStringDropped,
    /// <summary>A fixed-size value had the wrong number of bytes and was padded with zeros or cut.</summary>
    FixedValueResized,
    /// <summary>A string was converted between 8-bit and Unicode because of <see cref="MsgWriterOptions.Strings"/>.</summary>
    StringConverted,
    /// <summary>A character could not be stored in the chosen 8-bit code page and became <c>?</c>.</summary>
    StringNotRepresentable,
    /// <summary>A Unicode string value has an odd number of bytes; it is written as it is and reads back with a defect.</summary>
    OddUnicodeLength,
    /// <summary>An embedded message and a raw object storage were both set on an attachment; the message is written.</summary>
    EmbeddedAndStorage,
    /// <summary>PidTagAttachMethod says an embedded message or object but the attachment has none.</summary>
    EmbeddedObjectMissing,
    /// <summary>PidTagStoreSupportMask, PidTagMessageCodepage, PidTagMessageFlags or PidTagAttachMethod was added or changed to match the options or the attachment.</summary>
    PropertyAdjusted,
    /// <summary>A stream or storage of a raw object storage has a name the compound file cannot hold and was left out.</summary>
    RawStorageEntryDropped,
}

/// <summary>One finding of the writer.</summary>
/// <param name="Kind">What was found.</param>
/// <param name="Location">Where: the object's path in the file (empty for the top level, <c>/__attach_version1.0_#00000000</c> ...) and the property tag when one applies.</param>
/// <param name="Detail">A sentence describing it.</param>
public readonly record struct MsgWriteDefect(MsgWriteDefectKind Kind, string Location, string Detail)
{
    /// <summary>Whether the finding stops the write (the kinds documented as errors).</summary>
    public bool IsError => Kind <= MsgWriteDefectKind.ValueUnreadable;

    /// <inheritdoc/>
    public override string ToString() => $"{Kind} at {(Location.Length == 0 ? "/" : Location)}: {Detail}";
}

/// <summary>The model cannot be written; nothing was written to the output.</summary>
public sealed class MsgWriteException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="defects">Every finding, errors first.</param>
    public MsgWriteException(IReadOnlyList<MsgWriteDefect> defects)
        : base("The message cannot be written as an MSG file: " + (defects.Count > 0 ? defects[0].ToString() : "unknown reason"))
    {
        Defects = defects;
    }

    /// <summary>Every finding of the check, errors first.</summary>
    public IReadOnlyList<MsgWriteDefect> Defects { get; }
}

/// <summary>What a successful write did.</summary>
public sealed class MsgWriteResult
{
    internal MsgWriteResult(IReadOnlyList<MsgWriteDefect> defects, long length, int properties, int recipients, int attachments, int namedProperties)
    {
        Defects = defects;
        Length = length;
        Properties = properties;
        Recipients = recipients;
        Attachments = attachments;
        NamedProperties = namedProperties;
    }

    /// <summary>What was repaired, left out or changed, in the order found. Empty when the model was written exactly.</summary>
    public IReadOnlyList<MsgWriteDefect> Defects { get; }

    /// <summary>The length of the file in bytes, or 0 when the output stream could not report it.</summary>
    public long Length { get; }

    /// <summary>The properties written, over all objects.</summary>
    public int Properties { get; }

    /// <summary>The recipients written, over all messages.</summary>
    public int Recipients { get; }

    /// <summary>The attachments written, over all messages.</summary>
    public int Attachments { get; }

    /// <summary>The named properties in the mapping.</summary>
    public int NamedProperties { get; }
}
