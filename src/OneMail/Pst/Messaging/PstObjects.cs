using System.Runtime.CompilerServices;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Pst.Ltp;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Pst.Messaging;

/// <summary>
/// A folder of a PST or OST (MS-PST 2.4.4): its property context, and, through the hierarchy, contents and associated-contents
/// tables, its subfolders and messages. The folder's own properties are read when it is opened; its tables are read when enumerated.
/// </summary>
/// <remarks>
/// A search folder (<see cref="IsSearchFolder"/>) is read but not evaluated: <see cref="EnumerateMessages"/> lists the result table
/// the file stored, which may be stale, and its criteria are not interpreted. Folder loops and nesting beyond
/// <see cref="PstReaderOptions.MaxFolderDepth"/> are cut with a <see cref="MapiDefectKind.FolderLoop"/> defect.
/// </remarks>
public sealed class PstFolder : MapiObject
{
    private readonly PstContext _ctx;

    private PstFolder(PstStore store, PstContext ctx, Nid nid, PstFolder? parent, int depth)
        : base(parent)
    {
        Store = store;
        _ctx = ctx;
        Nid = nid;
        ParentFolder = parent;
        Depth = depth;
    }

    /// <summary>The store the folder belongs to.</summary>
    public PstStore Store { get; }

    /// <summary>The folder's NID.</summary>
    public Nid Nid { get; }

    /// <summary>The parent folder as it was reached, or null for the root and for a folder opened directly by NID.</summary>
    public PstFolder? ParentFolder { get; }

    /// <summary>How many folders lie between this one and the root (0 for the root).</summary>
    public int Depth { get; }

    /// <summary>The display name (PidTagDisplayName), or null.</summary>
    public string? Name => GetString(MapiPid.DisplayName);

    /// <summary>The container class (PidTagContainerClass, for example <c>IPF.Note</c>), or null.</summary>
    public string? ContainerClass => GetString(0x3613);

    /// <summary>True for a search folder (node type 3).</summary>
    public bool IsSearchFolder => Nid.Type == NidType.SearchFolder;

    /// <summary>The message count the folder's property context states (PidTagContentCount), or null. The tables are the truth.</summary>
    public int? ContentCount => GetInt32(0x3602);

    /// <summary>The names from below the root down to this folder, joined by <c>/</c>; empty for the root. A <c>/</c> inside a name is kept as it is.</summary>
    public string Path
    {
        get
        {
            if (ParentFolder is null)
            {
                return string.Empty;
            }

            string parent = ParentFolder.Path;
            return parent.Length == 0 ? Name ?? string.Empty : parent + "/" + (Name ?? string.Empty);
        }
    }

    internal static PstFolder? Open(PstStore store, PstContext ctx, Nid nid, PstFolder? parent, int depth)
    {
        string location = "folder " + nid;
        if (!ctx.File.Nodes.TryGet(nid, out NodeEntry e))
        {
            ctx.Report(parent, MapiDefectKind.NodeMissing, location, 0, $"folder node {nid} is listed by its parent but is not in the node tree");
            return null;
        }

        var f = new PstFolder(store, ctx, nid, parent, depth);
        return PstFolderLoader.LoadProperties(ctx, f, PstNode.Of(e), location) ? f : null;
    }

    private Nid TableNid(NidType type) => new((Nid.Value & ~0x1Fu) | (uint)type);

    private bool IsSelfOrAncestor(Nid candidate)
    {
        for (PstFolder? f = this; f is not null; f = f.ParentFolder)
        {
            if (f.Nid == candidate)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Lists the subfolders in the order of the hierarchy table, one at a time.</summary>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>The subfolders; a subfolder whose node is missing or unreadable is skipped with a defect.</returns>
    public IEnumerable<PstFolder> EnumerateSubfolders(CancellationToken cancellationToken = default)
    {
        if (Depth >= _ctx.Options.MaxFolderDepth)
        {
            _ctx.Report(this, MapiDefectKind.FolderLoop, "folder " + Nid, 0, $"folders are nested deeper than {_ctx.Options.MaxFolderDepth}; the subfolders are not read");
            yield break;
        }

        foreach (uint id in PstFolderLoader.RowIds(_ctx, this, TableNid(NidType.HierarchyTable), "hierarchy of " + Nid, cancellationToken))
        {
            var child = new Nid(id);
            if (IsSelfOrAncestor(child))
            {
                _ctx.Report(this, MapiDefectKind.FolderLoop, "folder " + Nid, 0, $"the hierarchy table lists {child}, which is this folder or one of its ancestors; it is skipped");
                continue;
            }

            if (PstFolder.Open(Store, _ctx, child, this, Depth + 1) is { } f)
            {
                yield return f;
            }
        }
    }

    /// <summary>Lists the subfolders asynchronously; see <see cref="EnumerateSubfolders"/>.</summary>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>The subfolders.</returns>
    public async IAsyncEnumerable<PstFolder> EnumerateSubfoldersAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await ValueTask.CompletedTask.ConfigureAwait(false);
        foreach (PstFolder f in EnumerateSubfolders(cancellationToken))
        {
            yield return f;
        }
    }

    /// <summary>Lists the NIDs of the folder's messages without opening them.</summary>
    /// <param name="set">Which messages. A search folder has one result table; <see cref="PstMessageSet.Associated"/> adds nothing there.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>The NIDs in table order.</returns>
    public IEnumerable<Nid> EnumerateMessageIds(PstMessageSet set = PstMessageSet.Normal, CancellationToken cancellationToken = default)
    {
        if ((set & PstMessageSet.Normal) != 0)
        {
            Nid table = TableNid(IsSearchFolder ? NidType.SearchContentsTable : NidType.ContentsTable);
            foreach (uint id in PstFolderLoader.RowIds(_ctx, this, table, "contents of " + Nid, cancellationToken))
            {
                yield return new Nid(id);
            }
        }

        if ((set & PstMessageSet.Associated) != 0 && !IsSearchFolder)
        {
            foreach (uint id in PstFolderLoader.RowIds(_ctx, this, TableNid(NidType.AssocContentsTable), "associated contents of " + Nid, cancellationToken))
            {
                yield return new Nid(id);
            }
        }
    }

    /// <summary>Lists the folder's messages, opening each when it is reached. The working set does not grow with the folder.</summary>
    /// <param name="set">Which messages.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>The messages; one whose node is missing or unreadable is skipped with a defect.</returns>
    public IEnumerable<PstMessage> EnumerateMessages(PstMessageSet set = PstMessageSet.Normal, CancellationToken cancellationToken = default)
    {
        foreach (Nid id in EnumerateMessageIds(set, cancellationToken))
        {
            if (Store.OpenMessage(id, this, "message " + id) is { } m)
            {
                yield return m;
            }
        }
    }

    /// <summary>Lists the folder's messages asynchronously; see <see cref="EnumerateMessages"/>.</summary>
    /// <param name="set">Which messages.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>The messages.</returns>
    public async IAsyncEnumerable<PstMessage> EnumerateMessagesAsync(PstMessageSet set = PstMessageSet.Normal, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await ValueTask.CompletedTask.ConfigureAwait(false);
        foreach (PstMessage m in EnumerateMessages(set, cancellationToken))
        {
            yield return m;
        }
    }
}

/// <summary>
/// A message of a PST or OST, or a message embedded in an attachment (MS-PST 2.4.5): the message property context is read when the
/// message is opened; recipients (the recipient table) and attachments (the attachment table and attachment property contexts)
/// are read on first use. Values stored in subnodes (large bodies, attachment data) are read only when asked for.
/// </summary>
public sealed class PstMessage : MapiMessage
{
    /// <summary>NID_RECIPIENT_TABLE (MS-PST 2.4.5.3): the subnode holding a message's recipient table.</summary>
    internal const uint NidRecipientTable = 0x692;

    /// <summary>NID_ATTACHMENT_TABLE (MS-PST 2.4.6.1): the subnode holding a message's attachment table.</summary>
    internal const uint NidAttachmentTable = 0x671;

    private readonly PstContext _ctx;
    private readonly PstNode _node;
    private readonly string _location;
    private readonly int _depth;

    internal PstMessage(PstStore store, PstContext ctx, Nid nid, PstNode node, PstFolder? folder, MapiObject? parent, int depth, string location)
        : base(parent)
    {
        Store = store;
        _ctx = ctx;
        Nid = nid;
        _node = node;
        Folder = folder;
        _depth = depth;
        _location = location;
        Loaded = PstFolderLoader.LoadProperties(ctx, this, node, location);
    }

    internal PstMessage(PstStore store, PstContext ctx, Nid nid, PstNode node, PstFolder? folder, int depth, string location)
        : this(store, ctx, nid, node, folder, null, depth, location)
    {
    }

    internal bool Loaded { get; }

    /// <summary>The store the message belongs to.</summary>
    public PstStore Store { get; }

    /// <summary>The message's NID; for an embedded message, the NID of its subnode.</summary>
    public Nid Nid { get; }

    /// <summary>The folder it was listed by, or null for an embedded message and for a message opened directly by NID.</summary>
    public PstFolder? Folder { get; }

    /// <summary>True when the message is embedded in an attachment of another message.</summary>
    public bool IsEmbedded => Parent is PstAttachment;

    /// <summary>True for a folder-associated message (node type 8): a rule, a view or a setting rather than mail.</summary>
    public bool IsAssociated => Nid.Type == NidType.AssocMessage;

    internal int Depth => _depth;

    /// <inheritdoc/>
    protected override IReadOnlyList<MapiRecipient> LoadRecipients()
    {
        var list = new List<MapiRecipient>();
        if (!_node.TryOpenHeap(_ctx, out HeapNode? heap, out _) || heap is null)
        {
            return list;
        }

        using (heap)
        {
            if (heap.Subnodes is not { } subs || !subs.TryGet(new Nid(NidRecipientTable), out SubnodeEntry entry))
            {
                return list;
            }

            string loc = _location + " recipients";
            if (!HeapNode.TryOpen(_ctx.File, entry, out HeapNode? th, out IReadOnlyList<LtpDefect> why, _ctx.Ltp) || th is null)
            {
                _ctx.ReportLtp(this, loc, why);
                _ctx.Report(this, MapiDefectKind.ContextUnreadable, loc, 0, "the recipient table's heap cannot be read; the message has no recipients");
                return list;
            }

            using (th)
            {
                if (!TableContext.TryOpen(th, out TableContext? tc) || tc is null)
                {
                    _ctx.ReportHeap(this, loc, th);
                    _ctx.Report(this, MapiDefectKind.ContextUnreadable, loc, 0, "the recipient table is not a table context; the message has no recipients");
                    return list;
                }

                using (tc)
                {
                    PstNode tnode = PstNode.Of(entry);
                    byte[] row = new byte[Math.Max(4, tc.RowSize)];
                    int rows = tc.RowCount;
                    for (uint r = 0; r < rows; r++)
                    {
                        if (list.Count >= _ctx.Options.MaxRecipients)
                        {
                            _ctx.Report(this, MapiDefectKind.LimitReached, loc, 0, $"more than {_ctx.Options.MaxRecipients} recipients; the rest are not read");
                            break;
                        }

                        if (!tc.TryReadRow(r, row))
                        {
                            continue;
                        }

                        var rcp = new PstRecipient(this);
                        FillFromRow(tc, tnode, th, row, rcp, loc + " " + r);
                        list.Add(rcp);
                    }

                    _ctx.ReportHeap(this, loc, th);
                }
            }
        }

        return list;
    }

    // MS-PST 2.3.4: every cell that exists in a row is a property of the row's object. The table's own row ID and row version
    // columns are bookkeeping and are left out.
    private void FillFromRow(TableContext tc, PstNode tnode, HeapNode th, byte[] rowBytes, MapiObject target, string loc)
    {
        TableRow row = tc.GetRow(rowBytes);
        for (int c = 0; c < tc.Columns.Count; c++)
        {
            TcColumn col = tc.Columns[c];
            if (PstProperties.IsRowBookkeeping(col.Id) || !row.TryGetCell(c, out LtpValueRef v))
            {
                continue;
            }

            target.Properties.Add(PstProperties.Make(_ctx, tnode, th, target, loc, col.Id, col.Type, v));
        }
    }

    /// <inheritdoc/>
    protected override IReadOnlyList<MapiAttachment> LoadAttachments()
    {
        var list = new List<MapiAttachment>();
        if (!_node.TryOpenHeap(_ctx, out HeapNode? heap, out _) || heap is null)
        {
            return list;
        }

        using (heap)
        {
            if (heap.Subnodes is not { } subs)
            {
                return list;
            }

            string loc = _location + " attachments";
            var nids = new List<Nid>();
            if (subs.TryGet(new Nid(NidAttachmentTable), out SubnodeEntry tableEntry))
            {
                if (HeapNode.TryOpen(_ctx.File, tableEntry, out HeapNode? th, out IReadOnlyList<LtpDefect> why, _ctx.Ltp) && th is not null)
                {
                    using (th)
                    {
                        if (TableContext.TryOpen(th, out TableContext? tc) && tc is not null)
                        {
                            using (tc)
                            {
                                byte[] row = new byte[Math.Max(4, tc.RowSize)];
                                int rows = tc.RowCount;
                                for (uint r = 0; r < rows && nids.Count < _ctx.Options.MaxAttachments; r++)
                                {
                                    if (tc.TryReadRow(r, row))
                                    {
                                        nids.Add(new Nid(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(row)));
                                    }
                                }

                                if (rows > nids.Count && nids.Count >= _ctx.Options.MaxAttachments)
                                {
                                    _ctx.Report(this, MapiDefectKind.LimitReached, loc, 0, $"more than {_ctx.Options.MaxAttachments} attachments; the rest are not read");
                                }

                                _ctx.ReportHeap(this, loc, th);
                            }
                        }
                        else
                        {
                            _ctx.ReportHeap(this, loc, th);
                            _ctx.Report(this, MapiDefectKind.ContextUnreadable, loc, 0, "the attachment table is not a table context; the attachment subnodes are used instead");
                            CollectAttachmentSubnodes(subs, nids);
                        }
                    }
                }
                else
                {
                    _ctx.ReportLtp(this, loc, why);
                    _ctx.Report(this, MapiDefectKind.ContextUnreadable, loc, 0, "the attachment table's heap cannot be read; the attachment subnodes are used instead");
                    CollectAttachmentSubnodes(subs, nids);
                }
            }
            else
            {
                // MS-PST 2.4.6.1 has the table list every attachment; one without a table is damage. The subnodes are the attachments.
                CollectAttachmentSubnodes(subs, nids);
                if (nids.Count > 0)
                {
                    _ctx.Report(this, MapiDefectKind.AmbiguousAttachmentTableMissing, loc, 0, $"the message has {nids.Count} attachment subnodes but no attachment table; the subnodes are used");
                }
            }

            foreach (Nid nid in nids)
            {
                if (!subs.TryGet(nid, out SubnodeEntry e))
                {
                    _ctx.Report(this, MapiDefectKind.NodeMissing, loc, 0, $"the attachment table lists {nid}, which is not a subnode of the message");
                    continue;
                }

                var a = new PstAttachment(Store, _ctx, this, e, _depth, loc + " " + nid);
                if (a.Loaded)
                {
                    list.Add(a);
                }
            }
        }

        return list;
    }

    private void CollectAttachmentSubnodes(SubnodeTree subs, List<Nid> into)
    {
        foreach (SubnodeEntry e in subs.Entries)
        {
            if (e.Nid.Type == NidType.Attachment && into.Count < _ctx.Options.MaxAttachments)
            {
                into.Add(e.Nid);
            }
        }
    }
}

/// <summary>A recipient of a <see cref="PstMessage"/>: one row of its recipient table. Its properties are the row's cells.</summary>
public sealed class PstRecipient : MapiRecipient
{
    internal PstRecipient(PstMessage parent)
        : base(parent)
    {
    }
}

/// <summary>
/// An attachment of a <see cref="PstMessage"/> (MS-PST 2.4.6): the attachment object's property context. Its data
/// (PidTagAttachDataBinary) is lazy when stored in a subnode and is read through <see cref="MapiAttachment.OpenData"/> as a stream.
/// A message attached to a message is <see cref="MapiAttachment.EmbeddedMessage"/>.
/// </summary>
public sealed class PstAttachment : MapiAttachment
{
    private readonly PstStore _store;
    private readonly PstContext _ctx;
    private readonly PstNode _node;
    private readonly string _location;
    private readonly int _depth;

    internal PstAttachment(PstStore store, PstContext ctx, PstMessage parent, SubnodeEntry entry, int parentDepth, string location)
        : base(parent)
    {
        _store = store;
        _ctx = ctx;
        _node = PstNode.Of(entry);
        _location = location;
        _depth = parentDepth + 1;
        Nid = entry.Nid;
        Loaded = PstFolderLoader.LoadProperties(ctx, this, _node, location);
    }

    internal bool Loaded { get; }

    /// <summary>The attachment's NID: a subnode of its message.</summary>
    public Nid Nid { get; }

    /// <summary>The size the attachment states (PidTagAttachSize), or null.</summary>
    public int? Size => GetInt32(MapiPid.AttachSize);

    /// <inheritdoc/>
    protected override MapiMessage? LoadEmbeddedMessage()
    {
        // PidTagAttachDataObject (0x3701, type Object) names a subnode of this attachment that is the embedded message's own node:
        // its data is a message property context, its subnodes the message's recipient and attachment tables (MS-PST 2.4.6.2.2).
        if (Properties.Get(MapiPid.AttachDataBinary, MapiType.EmbeddedObject) is null)
        {
            return null;
        }

        bool declared = AttachMethodValue == 5;
        if (!_node.TryOpenHeap(_ctx, out HeapNode? heap, out _) || heap is null)
        {
            return null;
        }

        using (heap)
        {
            if (!PropertyContext.TryOpen(heap, out PropertyContext? pc) || pc is null || !pc.TryGet(MapiPid.AttachDataBinary, out PcProperty p)
                || p.Value.Kind != LtpValueKind.Subnode || heap.Subnodes is not { } subs || !subs.TryGet(new Nid(p.Value.Hnid), out SubnodeEntry e))
            {
                if (declared)
                {
                    _ctx.Report(this, MapiDefectKind.EmbeddedObjectMissing, _location, 0, "the attachment method says an embedded message, but the object subnode is missing");
                }

                return null;
            }

            if (_depth > _ctx.Options.MaxEmbeddingDepth)
            {
                _ctx.Report(this, MapiDefectKind.EmbeddingTooDeep, _location, 0, $"embedded messages are nested deeper than {_ctx.Options.MaxEmbeddingDepth}; the object stays a raw value");
                return null;
            }

            // An object attachment that is not a message (OLE data) is not a property context; that is not a defect unless it was declared a message.
            if (!declared && !(HeapNode.TryOpen(_ctx.File, e, out HeapNode? probe, out _, _ctx.Ltp) && probe is not null && Dispose(probe) == 0xBC))
            {
                return null;
            }

            var m = new PstMessage(_store, _ctx, e.Nid, PstNode.Of(e), null, this, _depth, _location + " embedded");
            if (!m.Loaded || (!declared && m.MessageClass is null))
            {
                return null;
            }

            return m;
        }

        static int Dispose(HeapNode h)
        {
            int sig = h.ClientSignature;
            h.Dispose();
            return sig;
        }
    }
}
