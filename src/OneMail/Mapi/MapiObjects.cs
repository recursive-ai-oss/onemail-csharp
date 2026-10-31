using RecursiveAI.OneMail.Text;

namespace RecursiveAI.OneMail.Mapi;

/// <summary>
/// A MAPI object: a message, a recipient or an attachment. It is a property bag plus the defects found while reading it.
/// File and store layers (MSG, PST, TNEF) create these; they all present the same shape.
/// </summary>
/// <remarks>Objects are not thread-safe: read one from a single thread at a time. Reading values from the file behind them is serialised by the layer that owns the file.</remarks>
public abstract class MapiObject
{
    private readonly List<MapiDefect> _defects = [];
    private int _ownVersion = -1;
    private MailEncoding? _ownEncoding;
    private MapiCodePageSource _ownSource;
    private MailEncoding? _declared;

    /// <summary>Creates an object.</summary>
    /// <param name="parent">The enclosing object (an attachment for an embedded message, a message for its attachments and recipients), used for the code page; or null.</param>
    protected MapiObject(MapiObject? parent)
    {
        Parent = parent;
    }

    /// <summary>
    /// A code page the file format itself declared for this object's 8-bit strings (TNEF's attOemCodepage). It is consulted before the object's
    /// properties, because the format's rules put it first.
    /// </summary>
    internal MailEncoding? DeclaredString8Encoding
    {
        get => _declared;
        set
        {
            _declared = value;
            _ownVersion = -1;
        }
    }

    /// <summary>The enclosing object, or null for a top-level message.</summary>
    public MapiObject? Parent { get; }

    /// <summary>The properties, in the order the file listed them.</summary>
    public MapiPropertyBag Properties { get; } = new();

    /// <summary>What the reader repaired or guessed while reading this object (not its children).</summary>
    public IReadOnlyList<MapiDefect> Defects => _defects;

    /// <summary>Records a defect on this object.</summary>
    /// <param name="defect">The defect.</param>
    protected internal void AddDefect(MapiDefect defect) => _defects.Add(defect);

    /// <summary>
    /// The encoding for this object's PtypString8 values, from the first of these that gives one the library has:
    /// a code page the file format declared (TNEF's attOemCodepage); the object's own PidTagMessageCodepage; the Windows ANSI code page of the language in its PidTagMessageLocaleId; its own
    /// PidTagInternetCodepage unless that is a Unicode transformation format; the enclosing object's encoding; windows-1252.
    /// <see cref="String8EncodingSource"/> says which applied.
    /// </summary>
    public MailEncoding String8Encoding => ResolveString8Encoding(out _);

    /// <summary>Which rule gave <see cref="String8Encoding"/>.</summary>
    public MapiCodePageSource String8EncodingSource
    {
        get
        {
            ResolveString8Encoding(out MapiCodePageSource source);
            return source;
        }
    }

    private MailEncoding ResolveString8Encoding(out MapiCodePageSource source)
    {
        // The object's own properties only change when the bag does, so the lookup is cached against the bag's version.
        if (_ownVersion != Properties.Version)
        {
            _ownEncoding = ResolveOwn(out _ownSource);
            _ownVersion = Properties.Version;
        }

        if (_ownEncoding is { } own)
        {
            source = _ownSource;
            return own;
        }

        if (Parent is not null)
        {
            MailEncoding e = Parent.ResolveString8Encoding(out MapiCodePageSource inner);
            source = inner == MapiCodePageSource.Default ? MapiCodePageSource.Default : MapiCodePageSource.Enclosing;
            return e;
        }

        source = MapiCodePageSource.Default;
        return MailEncoding.Windows1252;
    }

    private MailEncoding? ResolveOwn(out MapiCodePageSource source)
    {
        if (_declared is { } declared)
        {
            source = MapiCodePageSource.Declared;
            return declared;
        }

        if (Properties.Get(MapiPid.MessageCodepage, MapiType.Integer32)?.AsInt32() is int mcp && mcp > 0 && MailEncoding.ForCodePage(mcp) is { } m)
        {
            source = MapiCodePageSource.MessageCodepage;
            return m;
        }

        if (Properties.Get(MapiPid.MessageLocaleId, MapiType.Integer32)?.AsInt32() is int lcid && MapiCodePages.AnsiCodePageForLocale(lcid) is int lcp and > 0 && MailEncoding.ForCodePage(lcp) is { } l)
        {
            source = MapiCodePageSource.LocaleId;
            return l;
        }

        if (Properties.Get(MapiPid.InternetCodepage, MapiType.Integer32)?.AsInt32() is int icp && icp > 0 && !MapiCodePages.IsUnicodeTransformation(icp) && MailEncoding.ForCodePage(icp) is { } i)
        {
            source = MapiCodePageSource.InternetCodepage;
            return i;
        }

        source = MapiCodePageSource.Default;
        return null;
    }

    /// <summary>A string property by ID (Unicode preferred over 8-bit), decoded with <see cref="String8Encoding"/>.</summary>
    /// <param name="id">The property ID.</param>
    /// <returns>The text, or null when absent or not a string.</returns>
    public string? GetString(ushort id) => Properties.Get(id)?.AsString(String8Encoding);

    /// <summary>A PtypInteger32 property by ID.</summary>
    /// <param name="id">The property ID.</param>
    /// <returns>The value, or null when absent or not an integer.</returns>
    public int? GetInt32(ushort id) => Properties.Get(id, MapiType.Integer32, MapiType.ErrorCode)?.AsInt32();

    /// <summary>A PtypBoolean property by ID.</summary>
    /// <param name="id">The property ID.</param>
    /// <returns>The value, or null when absent.</returns>
    public bool? GetBoolean(ushort id) => Properties.Get(id, MapiType.Boolean)?.AsBoolean();

    /// <summary>A PtypTime property by ID as a UTC instant.</summary>
    /// <param name="id">The property ID.</param>
    /// <returns>The instant, or null when absent or not a date.</returns>
    public DateTimeOffset? GetTime(ushort id) => Properties.Get(id, MapiType.Time)?.AsTime();
}

/// <summary>A message, appointment, contact, task or other item, with its recipients and attachments.</summary>
public class MapiMessage : MapiObject
{
    private readonly List<MapiRecipient> _recipientList = [];
    private readonly List<MapiAttachment> _attachmentList = [];
    private IReadOnlyList<MapiRecipient>? _recipients;
    private IReadOnlyList<MapiAttachment>? _attachments;

    /// <summary>Creates an empty message.</summary>
    /// <param name="parent">The attachment this message is embedded in, or null.</param>
    public MapiMessage(MapiObject? parent = null)
        : base(parent)
    {
    }

    /// <summary>The recipients in table order. A file layer may read them on first access.</summary>
    public IReadOnlyList<MapiRecipient> Recipients => _recipients ??= LoadRecipients();

    /// <summary>The attachments in table order, with embedded messages as items. A file layer may read them on first access.</summary>
    public IReadOnlyList<MapiAttachment> Attachments => _attachments ??= LoadAttachments();

    /// <summary>Adds a recipient (for builders and readers that have all rows up front).</summary>
    /// <param name="recipient">The recipient.</param>
    public void AddRecipient(MapiRecipient recipient) => _recipientList.Add(recipient);

    /// <summary>Adds an attachment (for builders and readers that have all rows up front).</summary>
    /// <param name="attachment">The attachment.</param>
    public void AddAttachment(MapiAttachment attachment) => _attachmentList.Add(attachment);

    /// <summary>Reads the recipients; a file layer overrides this to read them lazily. The default returns those added.</summary>
    /// <returns>The recipients.</returns>
    protected virtual IReadOnlyList<MapiRecipient> LoadRecipients() => _recipientList;

    /// <summary>Reads the attachments; a file layer overrides this to read them lazily. The default returns those added.</summary>
    /// <returns>The attachments.</returns>
    protected virtual IReadOnlyList<MapiAttachment> LoadAttachments() => _attachmentList;

    /// <summary>PidTagMessageClass, such as <c>IPM.Note</c> or <c>IPM.Appointment</c>.</summary>
    public string? MessageClass => GetString(MapiPid.MessageClass);

    /// <summary>PidTagSubject, including the prefix control characters Outlook stores (see PidTagNormalizedSubject for the clean form).</summary>
    public string? Subject => GetString(MapiPid.Subject);

    /// <summary>PidTagBody, the plain-text body, when the message has one as a string property.</summary>
    public string? Body => GetString(MapiPid.Body);
}

/// <summary>The kind of recipient in PidTagRecipientType (MS-OXOMSG 2.2.1.2).</summary>
public enum MapiRecipientType
{
    /// <summary>The originator (0).</summary>
    Originator = 0,

    /// <summary>A primary recipient (1).</summary>
    To = 1,

    /// <summary>A carbon-copy recipient (2).</summary>
    Cc = 2,

    /// <summary>A blind carbon-copy recipient (3).</summary>
    Bcc = 3,
}

/// <summary>One row of a message's recipient table.</summary>
public class MapiRecipient : MapiObject
{
    /// <summary>Creates an empty recipient.</summary>
    /// <param name="parent">The message it belongs to.</param>
    public MapiRecipient(MapiObject? parent)
        : base(parent)
    {
    }

    /// <summary>PidTagRecipientType as the number the file holds (values other than 0 to 3 are possible in damaged data), or null when absent.</summary>
    public int? RecipientTypeValue => GetInt32(MapiPid.RecipientType);

    /// <summary>PidTagRecipientType when it is one of the defined kinds, else null.</summary>
    public MapiRecipientType? RecipientType => RecipientTypeValue is >= 0 and <= 3 and int v ? (MapiRecipientType)v : null;

    /// <summary>PidTagDisplayName.</summary>
    public string? DisplayName => GetString(MapiPid.DisplayName);

    /// <summary>PidTagAddressType, such as <c>SMTP</c> or <c>EX</c>.</summary>
    public string? AddressType => GetString(MapiPid.AddressType);

    /// <summary>PidTagEmailAddress: an SMTP address for <c>SMTP</c> recipients, a directory path for <c>EX</c> ones.</summary>
    public string? EmailAddress => GetString(MapiPid.EmailAddress);

    /// <summary>PidTagSmtpAddress, when the recipient has one.</summary>
    public string? SmtpAddress => GetString(MapiPid.SmtpAddress);
}

/// <summary>How an attachment's data is held (PidTagAttachMethod, MS-OXCMSG 2.2.2.9).</summary>
public enum MapiAttachMethod
{
    /// <summary>No attachment (0).</summary>
    None = 0,

    /// <summary>The data is in PidTagAttachDataBinary (1).</summary>
    ByValue = 1,

    /// <summary>A path to a file (2).</summary>
    ByReference = 2,

    /// <summary>A resolvable path (3).</summary>
    ByReferenceResolve = 3,

    /// <summary>A path to a file that is only a reference (4).</summary>
    ByReferenceOnly = 4,

    /// <summary>An embedded message (5).</summary>
    EmbeddedMessage = 5,

    /// <summary>An OLE object stored as a storage (6).</summary>
    Ole = 6,

    /// <summary>A link to a web resource (7).</summary>
    ByWebReference = 7,
}

/// <summary>One row of a message's attachment table. An embedded message is an item of its own.</summary>
public class MapiAttachment : MapiObject
{
    private MapiMessage? _embedded;
    private bool _embeddedLoaded;
    private MapiRawStorage? _storage;
    private bool _storageLoaded;

    /// <summary>Creates an empty attachment.</summary>
    /// <param name="parent">The message it belongs to.</param>
    public MapiAttachment(MapiObject? parent)
        : base(parent)
    {
    }

    /// <summary>PidTagAttachMethod as the number the file holds, or null when absent.</summary>
    public int? AttachMethodValue => GetInt32(MapiPid.AttachMethod);

    /// <summary>PidTagAttachMethod when it is one of the defined values, else null.</summary>
    public MapiAttachMethod? AttachMethod => AttachMethodValue is >= 0 and <= 7 and int v ? (MapiAttachMethod)v : null;

    /// <summary>PidTagAttachLongFilename, else PidTagAttachFilename (the 8.3 name), else PidTagDisplayName.</summary>
    public string? FileName => GetString(MapiPid.AttachLongFilename) ?? GetString(MapiPid.AttachFilename) ?? GetString(MapiPid.DisplayName);

    /// <summary>PidTagAttachMimeTag, the content type, when present.</summary>
    public string? MimeType => GetString(MapiPid.AttachMimeTag);

    /// <summary>PidTagAttachContentId, the Content-ID that HTML bodies refer to with <c>cid:</c>, when present.</summary>
    public string? ContentId => GetString(MapiPid.AttachContentId);

    /// <summary>The embedded message when the attachment is one, else null. A file layer reads it on first access.</summary>
    public MapiMessage? EmbeddedMessage
    {
        get
        {
            if (!_embeddedLoaded)
            {
                _embedded = LoadEmbeddedMessage();
                _embeddedLoaded = true;
            }

            return _embedded;
        }
    }

    /// <summary>The attachment's object storage (an OLE object, or an embedded message kept raw), else null. A file layer reads it on first access.</summary>
    public MapiRawStorage? ObjectStorage
    {
        get
        {
            if (!_storageLoaded)
            {
                _storage = LoadObjectStorage();
                _storageLoaded = true;
            }

            return _storage;
        }
    }

    /// <summary>Sets the embedded message (for builders and readers that have it up front).</summary>
    /// <param name="message">The message.</param>
    public void SetEmbeddedMessage(MapiMessage? message)
    {
        _embedded = message;
        _embeddedLoaded = true;
    }

    /// <summary>Opens PidTagAttachDataBinary as a stream, reading from the file piece by piece when the layer can.</summary>
    /// <returns>A readable, seekable stream, or null when the attachment has no binary data property.</returns>
    public Stream? OpenData() => Properties.Get(MapiPid.AttachDataBinary, MapiType.Binary)?.OpenRead();

    /// <summary>Reads the embedded message; a file layer overrides this. The default returns the message set with <see cref="SetEmbeddedMessage"/>.</summary>
    /// <returns>The message or null.</returns>
    protected virtual MapiMessage? LoadEmbeddedMessage() => null;

    /// <summary>Reads the object storage; a file layer overrides this.</summary>
    /// <returns>The storage or null.</returns>
    protected virtual MapiRawStorage? LoadObjectStorage() => null;
}

/// <summary>One stream or storage inside a <see cref="MapiRawStorage"/>.</summary>
/// <param name="Path">The stored names from the storage's root, joined with <c>/</c>, not sanitised (they can hold control characters).</param>
/// <param name="IsStorage">True for a storage, false for a stream.</param>
/// <param name="Length">The stream's size in bytes; zero for a storage.</param>
public readonly record struct MapiRawStorageEntry(string Path, bool IsStorage, long Length);

/// <summary>
/// An opaque nested storage kept as it is: the content of an OLE attachment, or an embedded message that is not read as an item.
/// Nothing in it is interpreted.
/// </summary>
public abstract class MapiRawStorage
{
    /// <summary>The storage's class ID (CLSID), which identifies the OLE server; <see cref="Guid.Empty"/> when none is set.</summary>
    public abstract Guid ClassId { get; }

    /// <summary>Every stream and storage below this one, parents before children.</summary>
    public abstract IReadOnlyList<MapiRawStorageEntry> Entries { get; }

    /// <summary>Opens a stream by its path.</summary>
    /// <param name="path">A path as in <see cref="MapiRawStorageEntry.Path"/>, compared case-insensitively.</param>
    /// <returns>A readable, seekable stream.</returns>
    /// <exception cref="FileNotFoundException">There is no stream at the path.</exception>
    public abstract Stream OpenStream(string path);
}
