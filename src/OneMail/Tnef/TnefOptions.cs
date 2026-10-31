namespace RecursiveAI.OneMail.Tnef;

/// <summary>
/// Limits and switches for reading a TNEF stream. The defaults suit hostile input: each limit bounds work or memory by a number no real
/// stream reaches. A limit that is reached stops that part of the read and records a <see cref="TnefDefectKind.LimitReached"/> defect.
/// </summary>
public sealed class TnefReaderOptions
{
    /// <summary>Largest attribute accepted, in bytes. An attribute over it ends the read of the stream. Default 1 GiB (attachment data is read lazily, never in one piece).</summary>
    public long MaxAttributeBytes { get; init; } = 1L << 30;

    /// <summary>Most attributes read from one stream. Default 1,048,576.</summary>
    public int MaxAttributes { get; init; } = 1 << 20;

    /// <summary>Most attachments built from one message. Default 65,536.</summary>
    public int MaxAttachments { get; init; } = 65_536;

    /// <summary>Most rows read from one recipient table. Default 65,536.</summary>
    public int MaxRecipients { get; init; } = 65_536;

    /// <summary>Most properties read from one property list. Default 1,048,576.</summary>
    public int MaxProperties { get; init; } = 1 << 20;

    /// <summary>Most values read for one multi-valued property. Default 1,048,576.</summary>
    public int MaxMultiValueCount { get; init; } = 1 << 20;

    /// <summary>Most levels of embedded messages (a message in an attachment of a message ...). The top-level message is level 0. Default 16.</summary>
    public int MaxEmbeddingDepth { get; init; } = 16;

    /// <summary>
    /// Largest decompressed size a PidTagRtfCompressed header may announce, in bytes. Compressed RTF is never decompressed here; a header over the limit
    /// is reported so that a caller that decompresses can refuse it. Default 256 MiB.
    /// </summary>
    public long MaxRtfDecompressedBytes { get; init; } = 256L << 20;

    /// <summary>Values up to this many bytes are read when their property list is read; larger ones stay in the stream and are read on demand. Default 64 KiB.</summary>
    public int EagerValueBytes { get; init; } = 64 * 1024;

    /// <summary>Whether to compute every attribute's checksum while opening. This reads the whole stream once, in small pieces. Default true.</summary>
    public bool VerifyChecksums { get; init; } = true;

    /// <summary>Most defects kept; later ones are only counted. Default 10,000.</summary>
    public int MaxDefects { get; init; } = 10_000;
}

/// <summary>Choices for writing a message as TNEF.</summary>
public sealed class TnefWriterOptions
{
    /// <summary>The 16-bit legacy key after the signature (MS-OXTNEF 2.1.3.2: any number will do). Default 1, as in the specification's example.</summary>
    public ushort LegacyKey { get; init; } = 1;

    /// <summary>Whether to write the recipient table (attRecipTable). MS-OXTNEF 2.1.3.5.2.2 leaves it out for TNEF carried in a MIME part except for legacy delivery reports. Default true.</summary>
    public bool IncludeRecipients { get; init; } = true;

    /// <summary>Most levels of embedded messages written. Default 16.</summary>
    public int MaxEmbeddingDepth { get; init; } = 16;
}
