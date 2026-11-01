using System.Buffers.Binary;
using System.Text;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Pst.Ltp;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Pst.Messaging;

/// <summary>
/// A folder being written by a <see cref="PstBuilder"/>. Add messages and create subfolders; the folder's property context and its hierarchy,
/// contents and associated-contents tables are written when the builder is completed, from counts and rows kept along the way.
/// </summary>
/// <remarks>Not thread-safe.</remarks>
public sealed class PstFolderWriter
{
    private readonly PstBuilder _builder;
    private readonly List<PstFolderWriter> _children = [];
    private readonly PstRowSpool.Channel _contents;
    private readonly PstRowSpool.Channel _assoc;
    private int _unread;
    private bool _closed;

    internal PstFolderWriter(PstBuilder builder, PstFolderWriter? parent, uint nid, string name, string? containerClass, bool isSearch)
    {
        _builder = builder;
        Parent = parent;
        Nid = new Nid(nid);
        Name = name;
        ContainerClass = containerClass;
        IsSearchFolder = isSearch;
        _contents = PstRowSpool.Open();
        _assoc = PstRowSpool.Open();
    }

    /// <summary>The folder's NID.</summary>
    public Nid Nid { get; }

    /// <summary>The folder's display name.</summary>
    public string Name { get; }

    /// <summary>The container class (such as <c>IPF.Note</c>), or null for none.</summary>
    public string? ContainerClass { get; }

    /// <summary>The parent folder; null for the root.</summary>
    public PstFolderWriter? Parent { get; }

    /// <summary>True for the spam search folder, which holds nothing.</summary>
    public bool IsSearchFolder { get; }

    /// <summary>The subfolders created so far, in creation order.</summary>
    public IReadOnlyList<PstFolderWriter> Children => _children;

    /// <summary>The messages added to the folder (not the folder-associated ones).</summary>
    public long MessageCount => _contents.Count;

    /// <summary>The folder-associated messages added.</summary>
    public long AssociatedCount => _assoc.Count;

    internal void AddChild(PstFolderWriter child) => _children.Add(child);

    /// <summary>Creates a subfolder.</summary>
    /// <param name="name">The display name; any text, including <c>/</c>.</param>
    /// <param name="containerClass">PidTagContainerClass, such as <c>IPF.Note</c> (the default), <c>IPF.Appointment</c> or <c>IPF.Contact</c>; null for none.</param>
    /// <returns>The folder.</returns>
    /// <exception cref="InvalidOperationException">The store is complete, or this is a search folder.</exception>
    public PstFolderWriter CreateFolder(string name, string? containerClass = "IPF.Note")
    {
        ArgumentNullException.ThrowIfNull(name);
        _builder.ThrowIfDone();
        if (IsSearchFolder)
        {
            throw new InvalidOperationException("a search folder has no subfolders");
        }

        PstFolderWriter f = _builder.NewFolder(this, name, containerClass);
        _children.Add(f);
        return f;
    }

    /// <summary>Finds a subfolder of this folder by name, or creates it.</summary>
    /// <param name="name">The display name, compared exactly.</param>
    /// <param name="containerClass">The container class for a new folder.</param>
    /// <returns>The folder.</returns>
    public PstFolderWriter GetOrCreateFolder(string name, string? containerClass = "IPF.Note")
    {
        foreach (PstFolderWriter c in _children)
        {
            if (c.Name == name && !c.IsSearchFolder)
            {
                return c;
            }
        }

        return CreateFolder(name, containerClass);
    }

    /// <summary>Finds or creates the folder at a <c>/</c>-separated path below this one.</summary>
    /// <param name="path">Folder names separated by <c>/</c>; empty for this folder.</param>
    /// <param name="containerClass">The container class for folders that are created.</param>
    /// <returns>The folder.</returns>
    public PstFolderWriter GetOrCreatePath(string path, string? containerClass = "IPF.Note")
    {
        ArgumentNullException.ThrowIfNull(path);
        PstFolderWriter f = this;
        if (path.Length == 0)
        {
            return f;
        }

        foreach (string part in path.Split('/'))
        {
            f = f.GetOrCreateFolder(part, containerClass);
        }

        return f;
    }

    /// <summary>
    /// Writes a message (and its recipients, attachments and embedded messages) now and records its row for the folder's tables. Large values are
    /// streamed; nothing of the message is kept afterwards but a table row.
    /// </summary>
    /// <param name="message">The message, appointment, contact or other item, as a property bag.</param>
    /// <param name="associated">True for a folder-associated message (a rule, view or setting): it goes in the associated-contents table.</param>
    /// <returns>The message's NID in the new file.</returns>
    /// <exception cref="InvalidOperationException">The store is complete, or this is a search folder.</exception>
    public Nid AddMessage(MapiMessage message, bool associated = false)
    {
        ArgumentNullException.ThrowIfNull(message);
        _builder.ThrowIfDone();
        if (IsSearchFolder)
        {
            throw new InvalidOperationException("a search folder holds no messages");
        }

        Nid nid = _builder.NewMessageNid(associated);
        string location = (Path.Length == 0 ? "" : Path + "/") + "message " + nid;
        PstWrittenMessage w = _builder.Items.WriteMessage(message, nid, Nid, associated, location);
        if (associated)
        {
            _builder.Spool.Append(_assoc, nid.Value, w.AssocRow);
        }
        else
        {
            _builder.Spool.Append(_contents, nid.Value, w.ContentsRow);
            if ((w.Flags & 1) == 0)
            {
                _unread++;
            }
        }

        return nid;
    }

    /// <summary>The names from below the root down to this folder, joined by <c>/</c>; "Top of Personal Folders" is part of the path.</summary>
    public string Path => Parent is null ? string.Empty : Parent.Path.Length == 0 ? Name : Parent.Path + "/" + Name;

    // ---- closing: the property context and the three tables ----------------------------------------------------------------------------

    internal void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        PstFileWriter file = _builder.File;
        NdbBlockBuilder blocks = file.Blocks;
        uint baseNid = Nid.Value & ~0x1Fu;
        Nid parentNid = Parent?.Nid ?? Nid;   // MS-PST 2.7.3.4.1: the root's parent is itself

        // MS-PST 2.4.4.1.1: PidTagDisplayName, PidTagContentCount, PidTagContentUnreadCount, PidTagSubfolders.
        var pc = new PropertyContextBuilder(blocks);
        pc.Set(0x3001, LtpPropertyTypes.PtypString, Encoding.Unicode.GetBytes(Name));
        pc.Set(0x3602, LtpPropertyTypes.PtypInteger32, I32((int)Math.Min(int.MaxValue, MessageCount)));
        pc.Set(0x3603, LtpPropertyTypes.PtypInteger32, I32(_unread));
        pc.Set(0x360A, LtpPropertyTypes.PtypBoolean, [(byte)(_children.Count > 0 ? 1 : 0)]);
        if (ContainerClass is not null)
        {
            pc.Set(0x3613, LtpPropertyTypes.PtypString, Encoding.Unicode.GetBytes(ContainerClass));
        }

        if (AssociatedCount > 0)
        {
            pc.Set(0x3617, LtpPropertyTypes.PtypInteger32, I32((int)Math.Min(int.MaxValue, AssociatedCount)));   // PidTagAssocContentCount
        }

        LtpNodeResult r = pc.Build();
        file.AddNode(new NodeEntry(Nid, r.Data, r.WriteSubnodes(blocks), parentNid));

        if (IsSearchFolder)
        {
            // The search contents table (MS-PST 2.4.8.6.2), empty. The hierarchy and associated tables are written empty as well so that a reader finds all three.
            PstItemWriter.WriteTable(file, new Nid(baseNid | (uint)NidType.SearchContentsTable), PstItemWriter.SearchContentsColumns, []);
            PstItemWriter.WriteTable(file, new Nid(baseNid | (uint)NidType.HierarchyTable), PstItemWriter.HierarchyColumns, []);
            PstItemWriter.WriteTable(file, new Nid(baseNid | (uint)NidType.AssocContentsTable), PstItemWriter.AssocColumns, []);
            return;
        }

        // Hierarchy table: one row per subfolder, keyed by the subfolder's NID, in NID order.
        var hierarchy = new List<(uint, byte[]?[])>();
        foreach (PstFolderWriter c in _children)
        {
            hierarchy.Add((c.Nid.Value, c.HierarchyRow()));
        }

        hierarchy.Sort(static (a, b) => a.Item1.CompareTo(b.Item1));
        PstItemWriter.WriteTable(file, new Nid(baseNid | (uint)NidType.HierarchyTable), PstItemWriter.HierarchyColumns, hierarchy);

        PstItemWriter.WriteTable(file, new Nid(baseNid | (uint)NidType.ContentsTable), PstItemWriter.ContentsColumns,
            _builder.Spool.ReadAll(_contents, PstItemWriter.ContentsColumns.Length), streamHeap: true);
        PstItemWriter.WriteTable(file, new Nid(baseNid | (uint)NidType.AssocContentsTable), PstItemWriter.AssocColumns,
            _builder.Spool.ReadAll(_assoc, PstItemWriter.AssocColumns.Length), streamHeap: true);
    }

    // The row a parent's hierarchy table holds for this folder (MS-PST 2.4.4.4.1): name, counts, whether it has subfolders, container class.
    private byte[]?[] HierarchyRow()
    {
        var cells = new byte[]?[PstItemWriter.HierarchyColumns.Length];
        for (int i = 0; i < cells.Length; i++)
        {
            cells[i] = PstItemWriter.HierarchyColumns[i].Id switch
            {
                0x3001 => Encoding.Unicode.GetBytes(Name),
                0x3602 => I32((int)Math.Min(int.MaxValue, MessageCount)),
                0x3603 => I32(_unread),
                0x360A => [(byte)(_children.Count > 0 ? 1 : 0)],
                0x3613 when ContainerClass is not null => Encoding.Unicode.GetBytes(ContainerClass),
                0x6635 or 0x6636 => I32(0),
                _ => null,
            };
        }

        return cells;
    }

    private static byte[] I32(int v)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, v);
        return b;
    }
}
