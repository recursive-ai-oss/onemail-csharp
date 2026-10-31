using RecursiveAI.OneMail.Pst.Ltp;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Pst.Messaging;

/// <summary>
/// Limits for reading the messaging layer of a PST or OST. Every limit is checked before anything is allocated for what it
/// bounds; reaching one is reported as a <see cref="Mapi.MapiDefectKind.LimitReached"/> defect and the rest is skipped, never an exception.
/// </summary>
public sealed class PstReaderOptions
{
    /// <summary>Limits of the node database layer; null for its defaults.</summary>
    public NdbOptions? Ndb { get; init; }

    /// <summary>Limits of the lists, tables and properties layer; null for its defaults.</summary>
    public LtpOptions? Ltp { get; init; }

    /// <summary>The deepest folder nesting that is followed. Default 256.</summary>
    public int MaxFolderDepth { get; init; } = 256;

    /// <summary>The most properties read from one object (message, recipient, attachment, folder). Default 1,048,576.</summary>
    public int MaxProperties { get; init; } = 1 << 20;

    /// <summary>The most recipients read from one message. Default 65,536 (the specification says 2,048; real files exceed it).</summary>
    public int MaxRecipients { get; init; } = 1 << 16;

    /// <summary>The most attachments read from one message. Default 65,536.</summary>
    public int MaxAttachments { get; init; } = 1 << 16;

    /// <summary>The deepest embedded-message nesting that is read as messages. Default 32.</summary>
    public int MaxEmbeddingDepth { get; init; } = 32;

    /// <summary>The most named properties read from the name-to-ID map. Default 32,768 (the property ID space above 0x8000).</summary>
    public int MaxNamedProperties { get; init; } = 0x8000;

    /// <summary>The most defects kept for the store; later ones are only counted. Default 10,000.</summary>
    public int MaxDefects { get; init; } = 10_000;
}
