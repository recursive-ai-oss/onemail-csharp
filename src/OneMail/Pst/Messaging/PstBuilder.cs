using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Pst.Ltp;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Pst.Messaging;

/// <summary>What the builder could not carry into the file as it was given.</summary>
public enum PstWriteDefectKind
{
    /// <summary>A named property whose name is not known (it has a file-local ID only), so it cannot be given an ID in this file. It was left out.</summary>
    NamedPropertyUnmapped,

    /// <summary>The file already holds the most named properties its ID space allows (32,767). The property was left out.</summary>
    NamedPropertyLimit,

    /// <summary>An object listed a property ID twice. The first was kept.</summary>
    DuplicateProperty,

    /// <summary>A property type a property context cannot hold (null, unspecified, server ID, restriction, rule or an object other than an embedded message). It was left out.</summary>
    UnsupportedType,

    /// <summary>An attachment's object storage (an OLE object, or an embedded message kept raw) has no place in a PST attachment. It was left out.</summary>
    ObjectStorageDropped,

    /// <summary>A property every PST item must have was missing, so the builder added it (see <c>PstBuilderOptions.AddRequiredProperties</c>).</summary>
    PropertyAdded,

    /// <summary>Embedded messages were nested deeper than the limit. The deeper message was left out.</summary>
    EmbeddingTooDeep,

    /// <summary>An item has more recipients or attachments than a table can describe. The rest were left out.</summary>
    TooManyChildren,
}

/// <summary>One thing the builder changed or left out while writing an item.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Location">Where: the folder path and item, such as <c>Inbox/message 12 recipient 3</c>.</param>
/// <param name="Detail">A sentence naming the property or object.</param>
public sealed record PstWriteDefect(PstWriteDefectKind Kind, string Location, string Detail);

/// <summary>Options for <see cref="PstBuilder"/>.</summary>
public sealed class PstBuilderOptions
{
    /// <summary>The store's display name (PidTagDisplayName of the message store). Default "Personal Folders".</summary>
    public string DisplayName { get; init; } = "Personal Folders";

    /// <summary>How data blocks are encoded. Default <see cref="NdbEncryption.Permute"/>, as Outlook writes; obfuscation, not protection.</summary>
    public NdbEncryption Encryption { get; init; } = NdbEncryption.Permute;

    /// <summary>
    /// The provider UID in entry IDs and PidTagRecordKey. Default null: a new GUID per store. Set it to write the same bytes twice.
    /// </summary>
    public Guid? StoreId { get; init; }

    /// <summary>
    /// The time used for PidTagCreationTime and PidTagLastModificationTime of items that lack them. Default null: the time the builder was created. Set it to write the same bytes twice.
    /// </summary>
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>
    /// True (the default) to add the properties MS-PST 2.4.5.1.1 and 2.4.6.2.1 say every message and attachment must have when the item does not:
    /// PidTagMessageFlags, PidTagMessageSize, PidTagMessageStatus, PidTagCreationTime, PidTagLastModificationTime and PidTagSearchKey; PidTagAttachSize,
    /// PidTagAttachMethod and PidTagRenderingPosition. Each addition is reported as <see cref="PstWriteDefectKind.PropertyAdded"/>. False writes
    /// exactly the properties the item has, which is property-exact but a store Outlook may reject.
    /// </summary>
    public bool AddRequiredProperties { get; init; } = true;

    /// <summary>The deepest nesting of embedded messages written. Default 32, as the reader's default.</summary>
    public int MaxEmbeddingDepth { get; init; } = 32;

    /// <summary>Where the sorting of node entries and the folder row spools go. Default: temporary files, deleted when closed; memory where files are not available.</summary>
    public Func<Stream>? CreateSpool { get; init; }
}

/// <summary>
/// Writes a new Unicode PST (MS-PST) from folders and items handed to it one at a time, holding a bounded amount of memory however many there are.
/// Create folders under <see cref="IpmSubtree"/> and add MAPI messages to them; <see cref="Complete"/> writes the tables, the name-to-ID map and the
/// header and makes the file a store.
/// </summary>
/// <remarks>
/// <para><b>What is written.</b> The mandatory nodes of MS-PST 2.7.1: the message store, the name-to-ID map built from the items' named properties,
/// the root folder with the top-of-store folder (<see cref="IpmSubtree"/>), Deleted Items, the search root and the spam search folder, the template
/// tables, and the empty search management queue and activity list; per folder a property context and the hierarchy, contents and associated-contents
/// tables; per message a property context with recipient and attachment tables, attachment objects and embedded messages as subnodes, and large
/// values in subnodes.</para>
/// <para><b>Property-exact.</b> Every property of an item is written as it is, with the types, raw values and named-property names it has. Three kinds of change
/// are reported in <see cref="Defects"/> and nowhere silent: properties the format requires and the item lacks are added (<see cref="PstBuilderOptions.AddRequiredProperties"/>);
/// named properties get new file-local IDs (their names are kept); properties a property context cannot hold are left out.</para>
/// <para><b>Memory.</b> Blocks go to the file as they are made, B-tree pages are built from sorted runs, folders' table rows are spooled and their tables
/// are written when <see cref="Complete"/> closes the folders. A table's row index is the one structure that grows (about 12 bytes per row, in the folder being closed).</para>
/// <para><b>Failure.</b> The header is written last. A build that is stopped or disposed before <see cref="Complete"/> leaves a file with an empty header, which every reader refuses.</para>
/// <para>Not thread-safe.</para>
/// </remarks>
public sealed class PstBuilder : IDisposable, IAsyncDisposable
{
    // MS-PST 2.4.1: the special NIDs.
    internal const uint NidMessageStore = 0x21;
    internal const uint NidNameToIdMap = 0x61;
    internal const uint NidRootFolder = 0x122;
    internal const uint NidSearchManagementQueue = 0x1E1;
    internal const uint NidSearchActivityList = 0x201;
    internal const uint NidHierarchyTemplate = 0x60D;
    internal const uint NidContentsTemplate = 0x60E;
    internal const uint NidAssocContentsTemplate = 0x60F;
    internal const uint NidSearchContentsTemplate = 0x610;
    internal const uint NidRecipientTable = 0x692;
    internal const uint NidAttachmentTable = 0x671;
    private const uint NidIpmSubtree = 0x8022;
    private const uint NidSearchRoot = 0x8042;
    private const uint NidDeletedItems = 0x8062;
    private const uint NidSpamSearch = 0x2223;

    private readonly PstFileWriter _file;
    private readonly PstBuilderOptions _options;
    private readonly PstItemWriter _items;
    private readonly PstRowSpool _spool;
    private readonly List<PstFolderWriter> _folders = [];
    private readonly Guid _storeId;
    private readonly DateTimeOffset _timestamp;
    private uint _nextFolderIndex = 0x404;
    private uint _nextMessageIndex = 0x10000;
    private uint _nextAssocIndex = 0x8000;
    private bool _completed;
    private bool _disposed;

    private PstBuilder(Stream output, bool leaveOpen, PstBuilderOptions options)
    {
        _options = options;
        _file = new PstFileWriter(output, new PstFileWriterOptions { Encryption = options.Encryption, CreateSpool = options.CreateSpool }, leaveOpen);
        _storeId = options.StoreId ?? Guid.NewGuid();
        _timestamp = options.Timestamp ?? DateTimeOffset.UtcNow;
        _spool = new PstRowSpool(options.CreateSpool);
        _items = new PstItemWriter(_file, options, _timestamp, Defects);
        Root = new PstFolderWriter(this, null, NidRootFolder, string.Empty, null, isSearch: false);
        _folders.Add(Root);
        IpmSubtree = AddFixedFolder(Root, NidIpmSubtree, "Top of Personal Folders", null);
        SearchRoot = AddFixedFolder(Root, NidSearchRoot, "Search Root", null);
        DeletedItems = AddFixedFolder(IpmSubtree, NidDeletedItems, "Deleted Items", "IPF.Note");
        SpamSearchFolder = new PstFolderWriter(this, Root, NidSpamSearch, "SPAM Search Folder 2", null, isSearch: true);
        Root.AddChild(SpamSearchFolder);
        _folders.Add(SpamSearchFolder);
    }

    private PstFolderWriter AddFixedFolder(PstFolderWriter parent, uint nid, string name, string? containerClass)
    {
        var f = new PstFolderWriter(this, parent, nid, name, containerClass, isSearch: false);
        parent.AddChild(f);
        _folders.Add(f);
        return f;
    }

    /// <summary>Creates a PST at a path, replacing any file there. The file is opened exclusively.</summary>
    /// <param name="path">The file to write.</param>
    /// <param name="options">Options, or null for the defaults.</param>
    /// <returns>The builder; dispose it (after <see cref="Complete"/>) to close the file.</returns>
    /// <exception cref="IOException">The file cannot be created.</exception>
    public static PstBuilder Create(string path, PstBuilderOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        try
        {
            return new PstBuilder(fs, leaveOpen: false, options ?? new PstBuilderOptions());
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    /// <summary>Creates a PST on a stream.</summary>
    /// <param name="output">A seekable, writable, empty stream.</param>
    /// <param name="leaveOpen">True to leave it open on dispose.</param>
    /// <param name="options">Options, or null for the defaults.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="ArgumentException">The stream is not seekable and writable.</exception>
    public static PstBuilder Create(Stream output, bool leaveOpen = false, PstBuilderOptions? options = null) =>
        new(output, leaveOpen, options ?? new PstBuilderOptions());

    /// <summary>The root folder (nameless). Its children are the top-of-store folder, the search root and the spam search folder; mail goes under <see cref="IpmSubtree"/>.</summary>
    public PstFolderWriter Root { get; }

    /// <summary>"Top of Personal Folders" (NID 0x8022): the folder whose subfolders Outlook shows as the store's folders. Create the store's folders here.</summary>
    public PstFolderWriter IpmSubtree { get; }

    /// <summary>"Deleted Items" (NID 0x8062), under <see cref="IpmSubtree"/>.</summary>
    public PstFolderWriter DeletedItems { get; }

    /// <summary>"Search Root" (NID 0x8042), under the root.</summary>
    public PstFolderWriter SearchRoot { get; }

    /// <summary>The spam search folder (NID 0x2223, a search folder), under the root. Present because the format requires it; it holds nothing.</summary>
    public PstFolderWriter SpamSearchFolder { get; }

    /// <summary>What was changed or left out so far, at most 10,000 entries.</summary>
    public List<PstWriteDefect> Defects { get; } = [];

    /// <summary>The blocks written so far; for progress reports.</summary>
    public long BlockCount => _file.BlockCount;

    internal PstFileWriter File => _file;

    internal PstBuilderOptions Options => _options;

    internal PstItemWriter Items => _items;

    internal PstRowSpool Spool => _spool;

    internal Guid StoreId => _storeId;

    internal DateTimeOffset Timestamp => _timestamp;

    internal PstFolderWriter NewFolder(PstFolderWriter parent, string name, string? containerClass)
    {
        var f = new PstFolderWriter(this, parent, (_nextFolderIndex++ << 5) | (uint)NidType.NormalFolder, name, containerClass, isSearch: false);
        _folders.Add(f);
        return f;
    }

    internal Nid NewMessageNid(bool associated) =>
        associated ? new Nid((_nextAssocIndex++ << 5) | (uint)NidType.AssocMessage) : new Nid((_nextMessageIndex++ << 5) | (uint)NidType.NormalMessage);

    internal void ThrowIfDone()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            throw new InvalidOperationException("the store is complete");
        }
    }

    /// <summary>Closes every folder, writes the store's own nodes and the name-to-ID map, and finishes the file.</summary>
    /// <returns>What the file writer wrote.</returns>
    /// <exception cref="InvalidOperationException">Called twice.</exception>
    public PstFileSummary Complete()
    {
        ThrowIfDone();
        // Children before parents: a parent's hierarchy table describes them as they ended up.
        for (int i = _folders.Count - 1; i >= 0; i--)
        {
            _folders[i].Close();
        }

        _items.Finish(this);
        _completed = true;
        // MS-PST 2.2.2.6 rgnid: the last nidIndex allocated for each type (the next allocation adds one), as files written by Outlook hold it. The _next fields
        // hold the next index to hand out, so the last one allocated is one less.
        uint[] n = _file.NodeCounters;
        n[(int)NidType.NormalFolder] = _nextFolderIndex - 1;
        n[(int)NidType.NormalMessage] = _nextMessageIndex - 1;
        n[(int)NidType.AssocMessage] = _nextAssocIndex - 1;
        n[(int)NidType.SearchFolder] = Math.Max(n[(int)NidType.SearchFolder], 0x112);
        return _file.Complete();
    }

    /// <summary>Releases the file. A store that was not completed is left unusable (its header is empty), which readers refuse.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _spool.Dispose();
        _file.Dispose();
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    internal static byte[] EntryId(Guid store, uint nid)
    {
        // MS-PST 2.4.3.2: four zero flag bytes, the 16-byte provider UID, the NID.
        byte[] e = new byte[24];
        store.TryWriteBytes(e.AsSpan(4));
        BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(20), nid);
        return e;
    }

    // The message store (MS-PST 2.4.3.1) and the search queue nodes; called when the folders are closed.
    internal void WriteStoreNodes()
    {
        var pc = new PropertyContextBuilder(_file.Blocks);
        byte[] name = Encoding.Unicode.GetBytes(_options.DisplayName);
        pc.Set(0x0FF9, LtpPropertyTypes.PtypBinary, _storeId.ToByteArray());
        pc.Set(0x3001, LtpPropertyTypes.PtypString, name);
        pc.Set(0x35E0, LtpPropertyTypes.PtypBinary, EntryId(_storeId, NidIpmSubtree));
        pc.Set(0x35E3, LtpPropertyTypes.PtypBinary, EntryId(_storeId, NidDeletedItems));
        pc.Set(0x35E7, LtpPropertyTypes.PtypBinary, EntryId(_storeId, NidSearchRoot));
        pc.Set(0x67FF, LtpPropertyTypes.PtypInteger32, new byte[4]);   // PidTagPstPassword: no password
        LtpNodeResult r = pc.Build();
        _file.AddNode(new NodeEntry(new Nid(NidMessageStore), r.Data, r.WriteSubnodes(_file.Blocks), default));
        // MS-PST 2.7.3.5: the queue and the activity list are nodes without data.
        _file.AddNode(new NodeEntry(new Nid(NidSearchManagementQueue), default, default, default));
        _file.AddNode(new NodeEntry(new Nid(NidSearchActivityList), default, default, default));
    }
}
