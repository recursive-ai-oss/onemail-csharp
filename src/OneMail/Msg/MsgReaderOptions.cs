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
}
