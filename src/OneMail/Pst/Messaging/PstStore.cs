using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Pst.Ltp;
using RecursiveAI.OneMail.Pst.Ndb;
using RecursiveAI.OneMail.Sources;

namespace RecursiveAI.OneMail.Pst.Messaging;

/// <summary>Which messages of a folder to list.</summary>
[Flags]
public enum PstMessageSet
{
    /// <summary>The folder's ordinary messages (its contents table).</summary>
    Normal = 1,

    /// <summary>The folder-associated messages (rules, views, settings; the FAI contents table).</summary>
    Associated = 2,

    /// <summary>Both.</summary>
    All = Normal | Associated,
}

/// <summary>What <see cref="PstStore.ReadEverything"/> found.</summary>
/// <param name="Folders">Folders walked, the root included.</param>
/// <param name="Messages">Top-level messages loaded (normal and associated).</param>
/// <param name="Recipients">Recipients loaded, embedded messages' included.</param>
/// <param name="Attachments">Attachments loaded, embedded messages' included.</param>
/// <param name="EmbeddedMessages">Embedded messages loaded.</param>
/// <param name="Properties">Properties in every message, recipient and attachment loaded.</param>
public readonly record struct PstWalkCounts(int Folders, int Messages, int Recipients, int Attachments, int EmbeddedMessages, long Properties);

/// <summary>The object that holds the message store's own properties.</summary>
internal sealed class PstPropertySet : MapiObject
{
    internal PstPropertySet()
        : base(null)
    {
    }
}

/// <summary>
/// A PST or OST read through the messaging layer (MS-PST 2.4): the message store, the name-to-ID map, folders, messages,
/// recipients and attachments, all surfaced as the property model of <c>RecursiveAI.OneMail.Mapi</c>. ANSI, Unicode and 4 KiB
/// (OST 2013+) files read the same way.
/// </summary>
/// <remarks>
/// <para>Nothing is read at open but the header, the message store and the name map. Folders and messages are read as they are
/// reached; a message's properties are loaded when it is opened, its recipients and attachments when asked for, and attachment
/// data and any value stored in a subnode only when read. Enumeration holds one folder's table and one message at a time.</para>
/// <para>Reading never throws for malformed content: <see cref="Defects"/> (this layer), each object's <c>Defects</c> and
/// <see cref="DatabaseDefects"/> (the node database) say what was repaired or skipped. Objects stay valid until the store is
/// disposed; afterwards unread values throw <see cref="ObjectDisposedException"/>. Not thread-safe.</para>
/// <para>Search folders are read (their properties and result list) but never evaluated, and are skipped by
/// <see cref="EnumerateMessages"/> because their results are messages that live in other folders.</para>
/// </remarks>
public sealed class PstStore : IDisposable
{
    /// <summary>NID_MESSAGE_STORE (MS-PST 2.4.3).</summary>
    internal const uint NidMessageStore = 0x21;

    /// <summary>NID_ROOT_FOLDER (MS-PST 2.4.4).</summary>
    internal const uint NidRootFolder = 0x122;

    private readonly NdbFile _file;
    private readonly PstContext _ctx;

    private PstStore(NdbFile file, PstContext ctx)
    {
        _file = file;
        _ctx = ctx;
    }

    /// <summary>The node database underneath, for callers that want the pages, blocks and nodes themselves.</summary>
    public NdbFile Database => _file;

    /// <summary>ANSI, Unicode, or Unicode with 4 KiB pages.</summary>
    public NdbFormat Format => _file.Format;

    /// <summary>True for an OST (an offline cache): the header's client magic is <c>SO</c> rather than <c>SM</c>.</summary>
    public bool IsOffline => _file.Header.IsOffline;

    /// <summary>The message store's properties (PidTagDisplayName, the entry IDs of the special folders, and so on); empty when the store node is missing.</summary>
    public MapiPropertyBag Properties => _props.Properties;

    private readonly PstPropertySet _props = new();

    /// <summary>The store's display name (PidTagDisplayName), or null.</summary>
    public string? DisplayName => _props.GetString(MapiPid.DisplayName);

    /// <summary>
    /// The CRC-32 of the file's password (PidTagPstPassword), or null when none is set. It is read regardless of whether anyone knows
    /// the password and is not a protection: the data is readable without it, and the CRC is only what a client compares a typed password to.
    /// </summary>
    public uint? PasswordCrc => Properties.Get(0x67FF, MapiType.Integer32)?.AsInt32() is int v ? (uint)v : null;

    /// <summary>The root folder (NID 0x122, "Top of Personal Folders"), or null when the file has none.</summary>
    public PstFolder? Root { get; private set; }

    /// <summary>The number of named properties the name-to-ID map defines.</summary>
    public int NamedPropertyCount => _ctx.Names.Count;

    /// <summary>Looks up what a property ID above 0x8000 means in this file (the name-to-ID map, MS-PST 2.4.7).</summary>
    /// <param name="id">The property ID.</param>
    /// <param name="name">The property set and number or string, when mapped.</param>
    /// <returns>True when the map has the ID.</returns>
    public bool TryGetNamedProperty(ushort id, out MapiNamedProperty name)
    {
        MapiNamedProperty? n = _ctx.Names.Get(id);
        name = n.GetValueOrDefault();
        return n is not null;
    }

    /// <summary>Defects of this layer, in the order found (capped by <see cref="PstReaderOptions.MaxDefects"/>).</summary>
    public IReadOnlyList<MapiDefect> Defects => _ctx.Defects;

    /// <summary>Defects not listed because the cap was reached.</summary>
    public int DroppedDefects => _ctx.Dropped;

    /// <summary>Defects of the node database layer (checksums, missing blocks, bad compression).</summary>
    public IReadOnlyList<NdbDefect> DatabaseDefects => _file.Defects;

    /// <summary>Opens a file by path, read-only and shared.</summary>
    /// <param name="path">The file.</param>
    /// <param name="store">The store, or null when nothing can be read.</param>
    /// <param name="defects">Why the file cannot be read (empty on success; <see cref="Defects"/> has later findings).</param>
    /// <param name="options">Limits; null for the defaults.</param>
    /// <returns>True when the header was recognised and the store can be read.</returns>
    /// <exception cref="IOException">The file cannot be opened.</exception>
    public static bool TryOpen(string path, out PstStore? store, out IReadOnlyList<MapiDefect> defects, PstReaderOptions? options = null)
    {
        options ??= new PstReaderOptions();
        bool ok = NdbFile.TryOpen(path, out NdbFile? file, out IReadOnlyList<NdbDefect> why, options.Ndb);
        return Finish(ok, file, why, options, out store, out defects);
    }

    /// <summary>Opens a seekable stream.</summary>
    /// <param name="stream">The stream; it is seeked freely.</param>
    /// <param name="leaveOpen">True to leave it open when the store is disposed.</param>
    /// <param name="store">The store, or null.</param>
    /// <param name="defects">Why the file cannot be read.</param>
    /// <param name="options">Limits; null for the defaults.</param>
    /// <returns>True when the store can be read.</returns>
    public static bool TryOpen(Stream stream, bool leaveOpen, out PstStore? store, out IReadOnlyList<MapiDefect> defects, PstReaderOptions? options = null)
    {
        options ??= new PstReaderOptions();
        bool ok = NdbFile.TryOpen(stream, leaveOpen, out NdbFile? file, out IReadOnlyList<NdbDefect> why, options.Ndb);
        return Finish(ok, file, why, options, out store, out defects);
    }

    /// <summary>Opens a file held in memory.</summary>
    /// <param name="data">The whole file.</param>
    /// <param name="store">The store, or null.</param>
    /// <param name="defects">Why the file cannot be read.</param>
    /// <param name="options">Limits; null for the defaults.</param>
    /// <returns>True when the store can be read.</returns>
    public static bool TryOpen(ReadOnlyMemory<byte> data, out PstStore? store, out IReadOnlyList<MapiDefect> defects, PstReaderOptions? options = null)
    {
        options ??= new PstReaderOptions();
        bool ok = NdbFile.TryOpen(data, out NdbFile? file, out IReadOnlyList<NdbDefect> why, options.Ndb);
        return Finish(ok, file, why, options, out store, out defects);
    }

    /// <summary>Opens a file over a random-access source.</summary>
    /// <param name="source">The source; it must support synchronous reads.</param>
    /// <param name="leaveOpen">True to leave it undisposed when the store is disposed.</param>
    /// <param name="store">The store, or null.</param>
    /// <param name="defects">Why the file cannot be read.</param>
    /// <param name="options">Limits; null for the defaults.</param>
    /// <returns>True when the store can be read.</returns>
    /// <exception cref="ArgumentException">The source is asynchronous only.</exception>
    public static bool TryOpen(IRandomAccessSource source, bool leaveOpen, out PstStore? store, out IReadOnlyList<MapiDefect> defects, PstReaderOptions? options = null)
    {
        options ??= new PstReaderOptions();
        bool ok = NdbFile.TryOpen(source, leaveOpen, out NdbFile? file, out IReadOnlyList<NdbDefect> why, options.Ndb);
        return Finish(ok, file, why, options, out store, out defects);
    }

    private static bool Finish(bool opened, NdbFile? file, IReadOnlyList<NdbDefect> why, PstReaderOptions options, out PstStore? store, out IReadOnlyList<MapiDefect> defects)
    {
        store = null;
        if (!opened || file is null)
        {
            defects = [.. why.Select(d => new MapiDefect(MapiDefectKind.LowerLayerDefect, "file", d.Offset, d.Kind + ": " + d.Detail))];
            return false;
        }

        var ctx = new PstContext(file, options);
        var s = new PstStore(file, ctx);
        ctx.Names = PstNameMap.Read(ctx);
        if (file.Nodes.TryGet(new Nid(NidMessageStore), out NodeEntry se))
        {
            PstFolderLoader.LoadProperties(ctx, s._props, PstNode.Of(se), "message store");
        }
        else
        {
            ctx.Report(null, MapiDefectKind.NodeMissing, "message store", 0, "the message store node (0x21) is not in the node tree");
        }

        if (file.Nodes.TryGet(new Nid(NidRootFolder), out _))
        {
            s.Root = PstFolder.Open(s, ctx, new Nid(NidRootFolder), null, 0);
        }
        else
        {
            ctx.Report(null, MapiDefectKind.NodeMissing, "root folder", 0, "the root folder node (0x122) is not in the node tree");
        }

        store = s;
        defects = [];
        return true;
    }

    /// <summary>Opens a folder by NID.</summary>
    /// <param name="nid">A normal or search folder NID.</param>
    /// <param name="folder">The folder, or null.</param>
    /// <returns>True when the node exists and is a property context. The folder's parent is not resolved.</returns>
    public bool TryGetFolder(Nid nid, out PstFolder? folder)
    {
        folder = nid.Type is NidType.NormalFolder or NidType.SearchFolder ? PstFolder.Open(this, _ctx, nid, null, 0) : null;
        return folder is not null;
    }

    /// <summary>Opens a message by NID (a normal or folder-associated message).</summary>
    /// <param name="nid">The message NID.</param>
    /// <param name="message">The message, or null.</param>
    /// <returns>True when the node exists and is a property context.</returns>
    public bool TryOpenMessage(Nid nid, out PstMessage? message)
    {
        message = OpenMessage(nid, null, "nid " + nid);
        return message is not null;
    }

    internal PstMessage? OpenMessage(Nid nid, PstFolder? folder, string location)
    {
        if (!_file.Nodes.TryGet(nid, out NodeEntry e))
        {
            _ctx.Report(null, MapiDefectKind.NodeMissing, location, 0, $"message node {nid} is listed by its folder but is not in the node tree");
            return null;
        }

        if (nid.Type is not (NidType.NormalMessage or NidType.AssocMessage))
        {
            _ctx.Report(null, MapiDefectKind.RowIdMismatch, location, 0, $"{nid} is listed as a message but its node type is {nid.Type}");
        }

        var m = new PstMessage(this, _ctx, nid, PstNode.Of(e), folder, 0, location);
        return m.Loaded ? m : null;
    }

    /// <summary>Walks every folder depth-first, the root first. Search folders are included.</summary>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>The folders; each valid until the store is disposed.</returns>
    public IEnumerable<PstFolder> EnumerateFolders(CancellationToken cancellationToken = default)
    {
        if (Root is not { } root)
        {
            yield break;
        }

        yield return root;
        var stack = new Stack<IEnumerator<PstFolder>>();
        try
        {
            stack.Push(root.EnumerateSubfolders(cancellationToken).GetEnumerator());
            while (stack.Count > 0)
            {
                IEnumerator<PstFolder> top = stack.Peek();
                if (top.MoveNext())
                {
                    PstFolder f = top.Current;
                    yield return f;
                    stack.Push(f.EnumerateSubfolders(cancellationToken).GetEnumerator());
                }
                else
                {
                    stack.Pop().Dispose();
                }
            }
        }
        finally
        {
            while (stack.Count > 0)
            {
                stack.Pop().Dispose();
            }
        }
    }

    /// <summary>Walks every folder asynchronously; see <see cref="EnumerateFolders"/>.</summary>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>The folders.</returns>
    public async IAsyncEnumerable<PstFolder> EnumerateFoldersAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await ValueTask.CompletedTask.ConfigureAwait(false);
        foreach (PstFolder f in EnumerateFolders(cancellationToken))
        {
            yield return f;
        }
    }

    /// <summary>Walks every message of every folder (search folders skipped), depth-first, one at a time. The working set does not grow with the store.</summary>
    /// <param name="set">Which messages of each folder.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>The messages; each valid until the store is disposed.</returns>
    public IEnumerable<PstMessage> EnumerateMessages(PstMessageSet set = PstMessageSet.Normal, CancellationToken cancellationToken = default)
    {
        foreach (PstFolder f in EnumerateFolders(cancellationToken))
        {
            if (f.IsSearchFolder)
            {
                continue;
            }

            foreach (PstMessage m in f.EnumerateMessages(set, cancellationToken))
            {
                yield return m;
            }
        }
    }

    /// <summary>Walks every message asynchronously; see <see cref="EnumerateMessages"/>.</summary>
    /// <param name="set">Which messages of each folder.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>The messages.</returns>
    public async IAsyncEnumerable<PstMessage> EnumerateMessagesAsync(PstMessageSet set = PstMessageSet.Normal, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await ValueTask.CompletedTask.ConfigureAwait(false);
        foreach (PstMessage m in EnumerateMessages(set, cancellationToken))
        {
            yield return m;
        }
    }

    /// <summary>
    /// Loads every folder, message, recipient, attachment and embedded message, one at a time, and counts them; the defects
    /// found are then in <see cref="Defects"/> and on the objects. A way to validate a file in one call.
    /// </summary>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>The counts.</returns>
    public PstWalkCounts ReadEverything(CancellationToken cancellationToken = default)
    {
        int folders = 0, messages = 0, recipients = 0, attachments = 0, embedded = 0;
        long props = 0;
        foreach (PstFolder f in EnumerateFolders(cancellationToken))
        {
            folders++;
            props += f.Properties.Count;
            if (f.IsSearchFolder)
            {
                continue;
            }

            foreach (PstMessage m in f.EnumerateMessages(PstMessageSet.All, cancellationToken))
            {
                messages++;
                Walk(m);
            }
        }

        return new PstWalkCounts(folders, messages, recipients, attachments, embedded, props);

        void Walk(MapiMessage m)
        {
            props += m.Properties.Count;
            foreach (MapiRecipient r in m.Recipients)
            {
                recipients++;
                props += r.Properties.Count;
            }

            foreach (MapiAttachment a in m.Attachments)
            {
                attachments++;
                props += a.Properties.Count;
                if (a.EmbeddedMessage is { } em)
                {
                    embedded++;
                    Walk(em);
                }
            }
        }
    }

    /// <summary>Closes the file (unless the caller's stream or source was passed with <c>leaveOpen</c>).</summary>
    public void Dispose() => _file.Dispose();
}

/// <summary>Shared reading steps for objects that are a property context (folders, the store, messages, attachments).</summary>
internal static class PstFolderLoader
{
    /// <summary>Reads the properties of the property context at <paramref name="node"/> into <paramref name="target"/>.</summary>
    public static bool LoadProperties(PstContext ctx, MapiObject target, PstNode node, string location)
    {
        if (!node.TryOpenHeap(ctx, out HeapNode? heap, out IReadOnlyList<LtpDefect> why) || heap is null)
        {
            ctx.ReportLtp(target, location, why);
            ctx.Report(target, MapiDefectKind.ContextUnreadable, location, 0, "the node is not a readable heap; the object has no properties");
            return false;
        }

        using (heap)
        {
            if (!PropertyContext.TryOpen(heap, out PropertyContext? pc) || pc is null)
            {
                ctx.ReportHeap(target, location, heap);
                ctx.Report(target, MapiDefectKind.ContextUnreadable, location, 0, "the node is not a property context; the object has no properties");
                return false;
            }

            int n = 0;
            foreach (PcProperty p in pc)
            {
                if (++n > ctx.Options.MaxProperties)
                {
                    ctx.Report(target, MapiDefectKind.LimitReached, location, 0, $"more than {ctx.Options.MaxProperties} properties; the rest are not read");
                    break;
                }

                target.Properties.Add(PstProperties.Make(ctx, node, heap, target, location, p.Id, p.Type, p.Value));
            }

            ctx.ReportHeap(target, location, heap);
            return true;
        }
    }

    /// <summary>Row IDs of a table, in matrix order. Missing table: no rows (the caller decides whether that is a defect).</summary>
    public static IEnumerable<uint> RowIds(PstContext ctx, MapiObject? owner, Nid table, string location, CancellationToken cancellationToken)
    {
        if (!ctx.File.Nodes.TryGet(table, out _))
        {
            yield break;
        }

        if (!TableContext.TryOpen(ctx.File, table, out TableContext? tc, out IReadOnlyList<LtpDefect> why, ctx.Ltp) || tc is null)
        {
            ctx.ReportLtp(owner, location, why);
            ctx.Report(owner, MapiDefectKind.ContextUnreadable, location, 0, $"table {table} is not a readable table context");
            yield break;
        }

        using (tc)
        {
            int rows = tc.RowCount;
            byte[] buffer = new byte[Math.Max(4, tc.RowSize)];
            for (uint r = 0; r < rows; r++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!tc.TryReadRow(r, buffer))
                {
                    continue;
                }

                yield return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
            }

            ctx.ReportHeap(owner, location, tc.Heap);
        }
    }
}
