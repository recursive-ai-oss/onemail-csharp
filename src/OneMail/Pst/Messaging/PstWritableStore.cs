using System.Buffers.Binary;
using System.Text;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Pst.Ltp;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Pst.Messaging;

/// <summary>Options for <see cref="PstStore.OpenForWriting(string, PstWriteOptions?)"/>.</summary>
public sealed class PstWriteOptions
{
    /// <summary>The kinds of suspicious finding to accept when opening. The default accepts none: such a file is refused with the reasons.</summary>
    public NdbOpenRisks Allow { get; init; } = NdbOpenRisks.None;

    /// <summary>True (the default) to overwrite freed blocks and pages with zeros once the commit that freed them is done. False leaves them.</summary>
    public bool ZeroFreedSpace { get; init; } = true;

    /// <summary>The time written as the last-modified time of items. Null (the default) uses the clock.</summary>
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>True (the default) to add the properties a PST item needs and the item lacks, as <see cref="PstBuilderOptions.AddRequiredProperties"/> does.</summary>
    public bool AddRequiredProperties { get; init; } = true;

    /// <summary>How deep embedded messages may nest before they are left out with a defect. The default is 32.</summary>
    public int MaxEmbeddingDepth { get; init; } = 32;

    /// <summary>
    /// How many table rows may be waiting before the store commits by itself at the end of an operation; this bounds memory for long runs of changes.
    /// 0 turns it off, and the caller commits. The default is 20,000.
    /// </summary>
    public int AutoCommitRows { get; init; } = 20_000;

    /// <summary>Limits for the node database reader; null for the defaults.</summary>
    public NdbOptions? Ndb { get; init; }
}

/// <summary>
/// An existing Unicode PST open for changes: add, replace, move and delete items and folders, and commit them crash-consistently. Changes wait until
/// <see cref="Commit"/>; disposing without committing drops them. <see cref="View"/> shows the file as of the last commit.
/// </summary>
/// <remarks>
/// In-place writing is new and has not been through an independent review of its commit path. Keep a copy of files you cannot lose.
/// Search folders' criteria and update queues are not maintained: a client rebuilds a search folder when it opens it (MS-PST 2.4.8).
/// </remarks>
public sealed partial class PstWritableStore : IDisposable, IAsyncDisposable
{
    private readonly NdbEditor _ed;
    private readonly PstWriteOptions _options;
    private readonly List<PstWriteDefect> _defects = [];
    private readonly Dictionary<uint, FolderState> _folders = [];
    private readonly HashSet<uint> _touched = [];
    private PstStore _view = null!;
    private PstItemWriter _items = null!;
    private int _seededNames;
    private int _pendingRows;
    private bool _disposed;

    internal PstWritableStore(NdbEditor editor, PstWriteOptions options)
    {
        _ed = editor;
        _options = options;
        Reload();
    }

    /// <summary>The file as of the last commit, for reading. It is replaced by each commit; do not keep folders or messages from it across one.</summary>
    public PstStore View => _view;

    /// <summary>The node database editor underneath.</summary>
    public NdbEditor Database => _ed;

    /// <summary>What the item writer changed or left out of the items added so far.</summary>
    public IReadOnlyList<PstWriteDefect> Defects => _defects;

    /// <summary>True when changes are waiting for <see cref="Commit"/>.</summary>
    public bool HasChanges => _ed.HasChanges || _folders.Count > 0;

    private void Reload()
    {
        _view?.Dispose();
        if (!PstStore.TryOpen(_ed.Source, leaveOpen: true, out PstStore? view, out IReadOnlyList<MapiDefect> why, _options.Ndb is null ? null : new PstReaderOptions { Ndb = _options.Ndb }) || view is null)
        {
            throw new IOException("the store cannot be read after the commit: " + string.Join("; ", why));
        }

        _view = view;
        _items = new PstItemWriter(_ed, new PstBuilderOptions { AddRequiredProperties = _options.AddRequiredProperties, MaxEmbeddingDepth = _options.MaxEmbeddingDepth },
            _options.Timestamp ?? DateTimeOffset.UtcNow, _defects);
        _items.SeedNames(_view.Context.Names.Entries);
        _seededNames = _items.NameCount;
        _folders.Clear();
        _touched.Clear();
        _pendingRows = 0;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    // ---- items -------------------------------------------------------------------------------------------------------------------------

    /// <summary>Adds an item to a folder (MS-PST 2.4.5 and 2.4.6). The item is streamed into the file; the folder's tables are rewritten at commit.</summary>
    /// <param name="folder">The folder's NID (a normal folder).</param>
    /// <param name="message">The item as a property bag, with its recipients and attachments.</param>
    /// <param name="associated">True for a folder-associated item (a rule, view or setting).</param>
    /// <returns>The new item's NID.</returns>
    /// <exception cref="ArgumentException">There is no such folder.</exception>
    /// <exception cref="ObjectDisposedException">The store is disposed.</exception>
    public Nid AddMessage(Nid folder, MapiMessage message, bool associated = false)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(message);
        FolderState f = FolderOrThrow(folder);
        Nid nid = _ed.AllocateNid(associated ? NidType.AssocMessage : NidType.NormalMessage);
        PstWrittenMessage w = _items.WriteMessage(message, nid, folder, associated, "message " + nid);
        byte[]?[] row = PstItemWriter.MakeRow(message, f.Columns(associated), w.Effective);
        f.AddRow(associated, nid.Value, row, unread: !associated && (w.Flags & 1) == 0);
        _touched.Add(nid.Value);
        Pending(1);
        return nid;
    }

    /// <summary>Replaces an item's content, keeping its NID and folder. Its old blocks are freed at commit.</summary>
    /// <param name="message">The item's NID.</param>
    /// <param name="replacement">The new content.</param>
    /// <exception cref="ArgumentException">There is no such item.</exception>
    public void ReplaceMessage(Nid message, MapiMessage replacement)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(replacement);
        CommitIfTouched(message.Value);
        NodeEntry node = MessageNodeOrThrow(message);
        bool associated = message.Type == NidType.AssocMessage;
        FolderState f = FolderOrThrow(node.Parent);
        f.RemoveCommittedRow(this, associated, message.Value);
        PstWrittenMessage w = _items.WriteMessage(replacement, message, node.Parent, associated, "message " + message);
        byte[]?[] row = PstItemWriter.MakeRow(replacement, f.Columns(associated), w.Effective);
        f.AddRow(associated, message.Value, row, unread: !associated && (w.Flags & 1) == 0);
        _touched.Add(message.Value);
        Pending(1);
    }

    /// <summary>Deletes an item, freeing its blocks (and zeroing them, unless the options say not to) at commit. There is no Deleted Items step: it is gone.</summary>
    /// <param name="message">The item's NID.</param>
    /// <exception cref="ArgumentException">There is no such item.</exception>
    public void DeleteMessage(Nid message)
    {
        ThrowIfDisposed();
        NodeEntry node = MessageNodeOrThrow(message);
        bool associated = message.Type == NidType.AssocMessage;
        FolderState f = FolderOrThrow(node.Parent);
        if (!f.DropAddedRow(associated, message.Value, out _))
        {
            f.RemoveCommittedRow(this, associated, message.Value);
        }

        _ed.RemoveNode(message);
        _touched.Remove(message.Value);
        Pending(1);
    }

    /// <summary>Moves an item to another folder: the item's node keeps its blocks, and only the two folders' tables and its parent change.</summary>
    /// <param name="message">The item's NID.</param>
    /// <param name="destination">The destination folder's NID.</param>
    /// <exception cref="ArgumentException">There is no such item or folder.</exception>
    public void MoveMessage(Nid message, Nid destination)
    {
        ThrowIfDisposed();
        NodeEntry node = MessageNodeOrThrow(message);
        if (node.Parent == destination)
        {
            return;
        }

        bool associated = message.Type == NidType.AssocMessage;
        FolderState from = FolderOrThrow(node.Parent);
        FolderState to = FolderOrThrow(destination);
        byte[]?[]? cells;
        bool unread;
        if (from.DropAddedRow(associated, message.Value, out byte[]?[]? added))
        {
            cells = added;
            unread = !associated && from.LastDroppedUnread;
        }
        else
        {
            cells = from.CommittedCells(this, associated, message.Value);
            unread = from.RemoveCommittedRow(this, associated, message.Value);
        }

        byte[]?[] row = cells is null ? new byte[]?[to.Columns(associated).Length] : Remap(cells, from.Columns(associated), to.Columns(associated));
        to.AddRow(associated, message.Value, row, unread);
        _ed.SetNodeParent(message, destination);
        Pending(1);
    }

    // ---- folders -----------------------------------------------------------------------------------------------------------------------

    /// <summary>Creates a folder under another (MS-PST 2.4.4): its property context and its hierarchy, contents and associated-contents tables.</summary>
    /// <param name="parent">The parent folder's NID.</param>
    /// <param name="name">The display name.</param>
    /// <param name="containerClass">The container class (such as <c>IPF.Note</c>), or null for none.</param>
    /// <returns>The new folder's NID.</returns>
    /// <exception cref="ArgumentException">There is no such parent.</exception>
    public Nid CreateFolder(Nid parent, string name, string? containerClass = "IPF.Note")
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(name);
        FolderState p = FolderOrThrow(parent);
        Nid nid = _ed.AllocateNid(NidType.NormalFolder);
        var f = new FolderState(this, nid, parent, isNew: true) { Name = name, ContainerClass = containerClass };
        _folders[nid.Value] = f;
        p.AddedChildren[nid.Value] = default;
        p.ChildrenChanged = true;
        _touched.Add(nid.Value);
        Pending(1);
        return nid;
    }

    /// <summary>Changes a folder's display name.</summary>
    /// <param name="folder">The folder's NID.</param>
    /// <param name="name">The new name.</param>
    /// <exception cref="ArgumentException">There is no such folder.</exception>
    public void RenameFolder(Nid folder, string name)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(name);
        FolderState f = FolderOrThrow(folder);
        f.Name = name;
        f.Renamed = true;
        Pending(1);
    }

    /// <summary>Moves a folder, with everything in it, under another: only the folder's parent and the two hierarchy tables change.</summary>
    /// <param name="folder">The folder's NID.</param>
    /// <param name="destination">The new parent's NID.</param>
    /// <exception cref="ArgumentException">There is no such folder, or the destination is the folder itself or inside it.</exception>
    public void MoveFolder(Nid folder, Nid destination)
    {
        ThrowIfDisposed();
        FolderState f = FolderOrThrow(folder);
        FolderState to = FolderOrThrow(destination);
        if (f.Parent == folder)
        {
            throw new ArgumentException("the root folder cannot be moved", nameof(folder));
        }

        for (Nid at = destination; ; )
        {
            if (at == folder)
            {
                throw new ArgumentException("a folder cannot be moved into itself", nameof(destination));
            }

            Nid up = FolderOrThrow(at).Parent;
            if (up == at)
            {
                break;
            }

            at = up;
        }

        if (f.Parent == destination)
        {
            return;
        }

        FolderState from = FolderOrThrow(f.Parent);
        // A folder moved twice in one transaction was never in the middle parent's committed table; one moved back is simply where it was.
        Nid origin = f.Parent;
        if (from.AddedChildren.Remove(folder.Value, out Nid cameFrom))
        {
            origin = cameFrom;
        }
        else if (!f.IsNew)
        {
            from.RemovedChildren.Add(folder.Value);
        }

        from.ChildrenChanged = true;
        if (!f.IsNew && origin == destination)
        {
            to.RemovedChildren.Remove(folder.Value);
        }
        else
        {
            to.AddedChildren[folder.Value] = f.IsNew ? default : origin;
        }

        to.ChildrenChanged = true;
        f.Parent = destination;
        f.Moved = true;
        if (!f.IsNew)
        {
            _ed.SetNodeParent(folder, destination);
        }

        Pending(1);
    }

    /// <summary>
    /// Deletes a folder with its subfolders and items. The folder is unlinked and committed first, so a stop part-way leaves a consistent file;
    /// the items are then freed in batches, each committed.
    /// </summary>
    /// <param name="folder">The folder's NID.</param>
    /// <exception cref="ArgumentException">There is no such folder, or it is the root.</exception>
    public void DeleteFolder(Nid folder)
    {
        ThrowIfDisposed();
        Commit();
        FolderState f = FolderOrThrow(folder);
        if (f.Parent == folder)
        {
            throw new ArgumentException("the root folder cannot be deleted", nameof(folder));
        }

        FolderState parent = FolderOrThrow(f.Parent);
        var orphans = new List<Nid>();
        UnlinkFolder(folder, orphans);
        parent.RemovedChildren.Add(folder.Value);
        parent.ChildrenChanged = true;
        Commit();
        foreach (Nid item in orphans.Where(static n => n.Type is NidType.NormalMessage or NidType.AssocMessage))
        {
            _ed.RemoveNode(item);
            if (_ed.PendingBytes > 4 << 20)
            {
                _ed.Commit();
            }
        }

        _ed.Commit();
        Reload();
    }

    // Removes a folder's own nodes and, recursively, its subfolders'; collects the items (and leaves them for the caller to free after the commit).
    private void UnlinkFolder(Nid folder, List<Nid> orphans)
    {
        using var contents = OldTable.Open(_ed.Committed, TableNid(folder, NidType.ContentsTable), PstItemWriter.ContentsColumns);
        using var assoc = OldTable.Open(_ed.Committed, TableNid(folder, NidType.AssocContentsTable), PstItemWriter.AssocColumns);
        using var hierarchy = OldTable.Open(_ed.Committed, TableNid(folder, NidType.HierarchyTable), PstItemWriter.HierarchyColumns);
        orphans.AddRange(contents.RowIds().Select(static id => new Nid(id)));
        orphans.AddRange(assoc.RowIds().Select(static id => new Nid(id)));
        foreach (uint child in hierarchy.RowIds().ToList())
        {
            UnlinkFolder(new Nid(child), orphans);
        }

        foreach (NidType t in new[] { NidType.HierarchyTable, NidType.ContentsTable, NidType.AssocContentsTable, NidType.SearchContentsTable })
        {
            _ed.RemoveNode(TableNid(folder, t));
        }

        _ed.RemoveNode(folder);
    }

    // ---- commit ------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Writes the tables and counts that the changes touched, then commits crash-consistently: after a stop at any point the file opens with all of the
    /// changes since the last commit or none of them. Does nothing when there are no changes.
    /// </summary>
    /// <exception cref="IOException">A write failed; the store can only be disposed, and the file holds the last commit.</exception>
    public void Commit()
    {
        ThrowIfDisposed();
        if (!HasChanges)
        {
            return;
        }

        FlushFolders();
        _ed.Commit();
        Reload();
    }

    /// <summary>Drops every change since the last commit.</summary>
    public void Rollback()
    {
        ThrowIfDisposed();
        _ed.Rollback();
        Reload();
    }

    private void Pending(int rows)
    {
        _pendingRows += rows;
        if (_options.AutoCommitRows > 0 && (_pendingRows >= _options.AutoCommitRows || _ed.PendingBytes > 16 << 20))
        {
            Commit();
        }
    }

    private void CommitIfTouched(uint nid)
    {
        if (_touched.Contains(nid))
        {
            Commit();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _view?.Dispose();
        _ed.Dispose();
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    // ---- lookups -----------------------------------------------------------------------------------------------------------------------

    internal static Nid TableNid(Nid folder, NidType type) => new((folder.Value & ~0x1Fu) | (uint)type);

    private FolderState FolderOrThrow(Nid nid)
    {
        if (_folders.TryGetValue(nid.Value, out FolderState? f))
        {
            return f;
        }

        if (nid.Type != NidType.NormalFolder || !_ed.TryGetNode(nid, out NodeEntry node))
        {
            throw new ArgumentException($"there is no folder {nid}", nameof(nid));
        }

        f = new FolderState(this, nid, node.Parent, isNew: false);
        _folders[nid.Value] = f;
        return f;
    }

    private NodeEntry MessageNodeOrThrow(Nid nid)
    {
        if (nid.Type is not (NidType.NormalMessage or NidType.AssocMessage) || !_ed.TryGetNode(nid, out NodeEntry node))
        {
            throw new ArgumentException($"there is no item {nid}", nameof(nid));
        }

        return node;
    }

    internal NdbFile Committed => _ed.Committed;

    // Maps a row's cells from one column list to another by property tag; cells the destination has no column for are dropped.
    internal static byte[]?[] Remap(byte[]?[] cells, (ushort Id, ushort Type)[] from, (ushort Id, ushort Type)[] to)
    {
        var result = new byte[]?[to.Length];
        for (int i = 0; i < to.Length; i++)
        {
            int j = Array.IndexOf(from, to[i]);
            if (j >= 0 && j < cells.Length)
            {
                result[i] = cells[j];
            }
        }

        return result;
    }

    internal static byte[] I32(int v)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, v);
        return b;
    }

    internal static byte[] Utf16(string s) => Encoding.Unicode.GetBytes(s);
}
