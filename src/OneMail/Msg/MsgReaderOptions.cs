namespace RecursiveAI.OneMail.Msg;

/// <summary>
/// Limits for reading an MSG or OFT file. The defaults suit hostile input: each bounds work or memory by a number that no real file reaches.
/// A limit that is reached stops that part of the read and records a <see cref="Mapi.MapiDefectKind.LimitReached"/> defect.
/// </summary>
public sealed class MsgReaderOptions
{
    /// <summary>Most properties read from one object's property stream. Default 1,048,576.</summary>
    public int MaxProperties { get; init; } = 1 << 20;

    /// <summary>Most recipient storages read from one message. MS-OXMSG allows 2,048; the default is 65,536 because real files exceed the specified limit.</summary>
    public int MaxRecipients { get; init; } = 65_536;

    /// <summary>Most attachment storages read from one message. Default 65,536 (the specification allows 2,048).</summary>
    public int MaxAttachments { get; init; } = 65_536;

    /// <summary>Most levels of embedded messages (a message in an attachment of a message ...). The top-level message is level 0. Default 32.</summary>
    public int MaxEmbeddingDepth { get; init; } = 32;

    /// <summary>Most values read for one multi-valued property. Default 1,048,576.</summary>
    public int MaxMultiValueCount { get; init; } = 1 << 20;

    /// <summary>
    /// Largest stream <see cref="Mapi.MapiProperty.GetBytes"/> reads into memory, in bytes; a larger value reads as empty and records a defect.
    /// <see cref="Mapi.MapiProperty.OpenRead"/> has no such limit because it reads piece by piece. Default 256 MiB.
    /// </summary>
    public long MaxValueBytes { get; init; } = 256L << 20;

    /// <summary>Most defects kept in <see cref="MsgFile.Defects"/> and on the objects; later ones are only counted in <see cref="MsgFile.DroppedDefects"/>. Default 10,000.</summary>
    public int MaxDefects { get; init; } = 10_000;

    /// <summary>
    /// True to open a damaged compound file by recovery instead of refusing it. Default false: a file that breaks the compound file format is not opened
    /// (<see cref="Mapi.MapiDefectKind.CompoundFileInvalid"/>). With recovery, a file that passes the strict checks is read exactly as without it; otherwise the
    /// message is read from what survives and every loss is a defect: <see cref="Mapi.MapiDefectKind.CompoundFileRecovered"/>, one
    /// <see cref="Mapi.MapiDefectKind.CompoundFileDamage"/> per repair and <see cref="Mapi.MapiDefectKind.StreamDamaged"/> per shortened stream.
    /// A property stream cut short yields the properties it holds. Damage inside a stream's bytes is not detected. A file with no usable header, allocation table
    /// or directory still cannot be opened.
    /// </summary>
    public bool Recovery { get; init; }

    /// <summary>Most bytes recovery examines while searching for lost directory sectors. Default 1 GiB. Only used with <see cref="Recovery"/>.</summary>
    public long MaxRecoveryBytes { get; init; } = 1L << 30;
}
