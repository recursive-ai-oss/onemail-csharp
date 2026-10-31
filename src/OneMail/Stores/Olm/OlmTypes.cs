namespace RecursiveAI.OneMail.Stores.Olm;

/// <summary>What kind of item an OLM XML record is. The kind comes from the record's own element (<c>email</c>, <c>appointment</c> …), never from the file name.</summary>
public enum OlmItemKind
{
    /// <summary>An <c>email</c> record: a message of the Outlook for Mac mail store.</summary>
    Email = 0,

    /// <summary>An <c>appointment</c> record.</summary>
    Appointment,

    /// <summary>A <c>contact</c> record.</summary>
    Contact,

    /// <summary>A <c>task</c> record.</summary>
    Task,

    /// <summary>A <c>note</c> record (a sticky note).</summary>
    Note,

    /// <summary>A <c>group</c> record: a contact group (distribution list).</summary>
    Group,

    /// <summary>A <c>category</c> record from <c>Categories.xml</c>: a name and a colour.</summary>
    Category,
}

/// <summary>What was repaired, guessed or refused while reading an OLM archive. Readers never throw on malformed content: they record one of these and go on.</summary>
public enum OlmDefectKind
{
    /// <summary>The XML of an entry is not well-formed. Records read before the error are kept; the rest of the entry is skipped.</summary>
    XmlMalformed = 0,

    /// <summary>An XML entry is larger than <see cref="OlmReaderOptions.MaxXmlBytes"/> when decompressed. The rest of the entry is skipped.</summary>
    XmlTooLarge,

    /// <summary>XML nests deeper than <see cref="OlmReaderOptions.MaxXmlDepth"/>. The record is dropped.</summary>
    XmlTooDeep,

    /// <summary>A record has more elements than <see cref="OlmReaderOptions.MaxElementsPerItem"/>. The record is dropped.</summary>
    XmlTooManyElements,

    /// <summary>The root element of an XML entry is not one of the known containers (<c>emails</c>, <c>contacts</c> …). The entry is skipped.</summary>
    UnrecognisedRoot,

    /// <summary>The container's <c>elementCount</c> attribute does not match the number of records found. The records found are kept.</summary>
    ElementCountMismatch,

    /// <summary>A zip entry could not be read: damaged data, an unsupported method, a failed checksum or an exceeded limit of the zip reader.</summary>
    EntryUnreadable,

    /// <summary>The zip reader refused the archive because a safety limit was exceeded (entry count, total size, expansion ratio, path depth). Entries read before that are kept.</summary>
    ArchiveLimitExceeded,

    /// <summary>The zip directory could not be read. The archive has no items.</summary>
    ArchiveUnreadable,

    /// <summary>An attachment element names a zip entry that is not in the archive. The attachment is listed with no content.</summary>
    AttachmentMissing,

    /// <summary>A date or time value is not in the <c>yyyy-MM-ddTHH:mm:ss</c> form. The value is treated as absent in the shared view; the text stays in the XML tree.</summary>
    BadDate,

    /// <summary>A number or flag value could not be read (the OLM writes whole numbers as <c>1E0</c> and <c>5.0614E4</c>). The value is treated as absent.</summary>
    BadNumber,

    /// <summary>An address element has no address. The name alone is kept.</summary>
    AddressMissing,

    /// <summary>A file in the archive is not an XML record file, an attachment or a folder marker. It is not read.</summary>
    UnrecognisedEntry,

    /// <summary>Ambiguity: OLM dates carry no zone. They are read as UTC, flagged as having no known zone.</summary>
    AmbiguousDateZone,

    /// <summary>Ambiguity: the plain-text and HTML body elements hold the same text. The message's HTML flag decides which body it is.</summary>
    AmbiguousBodyKind,
}

/// <summary>One defect found while reading an OLM archive.</summary>
/// <param name="Kind">What was found.</param>
/// <param name="Entry">The zip entry the defect was found in, or an empty string for the archive itself.</param>
/// <param name="Detail">The element, attribute or value involved, or an empty string.</param>
public readonly record struct OlmDefect(OlmDefectKind Kind, string Entry, string Detail = "")
{
    /// <summary>True for the <c>Ambiguous*</c> kinds: places where OneMail took one documented reading of something clients disagree on.</summary>
    public bool IsAmbiguity => Kind is OlmDefectKind.AmbiguousDateZone or OlmDefectKind.AmbiguousBodyKind;

    /// <inheritdoc />
    public override string ToString() => Entry.Length == 0 ? Kind.ToString() : Kind + " in " + Entry + (Detail.Length == 0 ? "" : " (" + Detail + ")");
}

/// <summary>Limits and switches for reading an OLM archive. Every limit is on by default; a record or entry that exceeds one is skipped with a typed defect.</summary>
public sealed record OlmReaderOptions
{
    /// <summary>The defaults.</summary>
    public static OlmReaderOptions Default { get; } = new();

    /// <summary>Most decompressed bytes of one XML entry the reader will parse. Default 1 GiB. A mailbox's calendar or address book is one entry, so this is generous.</summary>
    public long MaxXmlBytes { get; init; } = 1L << 30;

    /// <summary>Deepest XML nesting allowed, counting the root as 1. Default 64; the real files nest to about 6.</summary>
    public int MaxXmlDepth { get; init; } = 64;

    /// <summary>Most elements in one record (one email, one appointment …). Default 1,000,000.</summary>
    public int MaxElementsPerItem { get; init; } = 1_000_000;

    /// <summary>Longest attribute value or text run kept for one element, in characters. Default 256 Mi characters (a 512 MiB body). Longer text drops the record.</summary>
    public int MaxTextLength { get; init; } = 256 << 20;

    /// <summary>
    /// Most decompressed bytes per compressed byte any entry may produce. Default 1000, the zip reader's own default; the zip reader checks it once an entry has
    /// produced more than a grace amount (32 MiB). Huge, highly repetitive XML is the usual way to trip it.
    /// </summary>
    public long MaxExpansionRatio { get; init; } = 1000;

    /// <summary>Most entries the zip directory may hold. Default 50,000,000.</summary>
    public int MaxEntryCount { get; init; } = 50_000_000;

    /// <summary>Most bytes of zip central directory held in memory. Default 4 GiB; about 100 bytes per entry on average, so 50 million entries.</summary>
    public long MaxCentralDirectorySize { get; init; } = 4L << 30;

    /// <summary>
    /// Most bytes all entries together may declare. Default 1 TiB. The zip reader counts declared sizes from the directory before any data is read.
    /// </summary>
    public long MaxTotalSize { get; init; } = 1L << 40;

    /// <summary>When true (the default) entry data is checked against the zip CRC-32 as it is read; a mismatch is an <see cref="OlmDefectKind.EntryUnreadable"/> defect.</summary>
    public bool VerifyChecksums { get; init; } = true;
}

/// <summary>A folder of the archive: a message folder, or one of the per-kind folders (<c>Calendar</c>, <c>Address Book</c> …).</summary>
/// <param name="Path">The folder's path with <c>/</c> separators, relative to the archive's <c>Local/</c> (and, for mail, <c>com.microsoft.__Messages/</c>) root. Empty for the root.</param>
/// <param name="Name">The last segment of <see cref="Path"/>.</param>
/// <param name="IsMail">True when the folder is in the message tree.</param>
public readonly record struct OlmFolder(string Path, string Name, bool IsMail)
{
    /// <summary>The path of the parent folder, empty for a top-level folder.</summary>
    public string Parent
    {
        get
        {
            int i = Path.LastIndexOf('/');
            return i < 0 ? string.Empty : Path[..i];
        }
    }
}
