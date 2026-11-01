using RecursiveAI.OneMail.Pst.Ltp;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Pst.Messaging;

/// <summary>
/// A table of the committed file read as rows of cells (the raw bytes of each cell, or null when the cell is absent), aligned to a column list. The
/// PidTagLtpRowId and PidTagLtpRowVer columns are left out: the table builder makes them. A table that does not exist reads as empty.
/// </summary>
internal sealed class OldTable : IDisposable
{
    private readonly TableContext? _table;
    private readonly int[] _source;
    private readonly byte[] _buffer;

    private OldTable(TableContext? table, (ushort Id, ushort Type)[] columns, int[] source)
    {
        _table = table;
        Columns = columns;
        _source = source;
        _buffer = new byte[table?.RowSize ?? 0];
    }

    public (ushort Id, ushort Type)[] Columns { get; }

    public int RowCount => _table?.RowCount ?? 0;

    public static OldTable Open(NdbFile file, Nid nid, (ushort Id, ushort Type)[] fallback)
    {
        if (TableContext.TryOpen(file, nid, out TableContext? t, out _) && t is not null)
        {
            var cols = new List<(ushort, ushort)>();
            var src = new List<int>();
            for (int i = 0; i < t.Columns.Count; i++)
            {
                if (t.Columns[i].Id is 0x67F2 or 0x67F3)
                {
                    continue;
                }

                cols.Add((t.Columns[i].Id, t.Columns[i].Type));
                src.Add(i);
            }

            return new OldTable(t, [.. cols], [.. src]);
        }

        return new OldTable(null, [.. fallback.Where(static c => c.Id is not (0x67F2 or 0x67F3))], []);
    }

    public IEnumerable<uint> RowIds()
    {
        if (_table is null)
        {
            yield break;
        }

        foreach (TcRowIndexEntry e in _table.EnumerateRowIndex())
        {
            yield return e.RowId;
        }
    }

    public bool TryGetRow(uint rowId, out byte[]?[] cells)
    {
        cells = [];
        if (_table is null || !_table.TryFindRow(rowId, out uint index))
        {
            return false;
        }

        return TryReadCells(index, out cells);
    }

    public IEnumerable<(uint RowId, byte[]?[] Cells)> Rows()
    {
        if (_table is null)
        {
            yield break;
        }

        foreach (TcRowIndexEntry e in _table.EnumerateRowIndex())
        {
            if (TryReadCells(e.RowIndex, out byte[]?[] cells))
            {
                yield return (e.RowId, cells);
            }
        }
    }

    private bool TryReadCells(uint rowIndex, out byte[]?[] cells)
    {
        cells = new byte[]?[Columns.Length];
        if (_table is null || !_table.TryReadRow(rowIndex, _buffer))
        {
            return false;
        }

        TableRow row = _table.GetRow(_buffer);
        for (int i = 0; i < _source.Length; i++)
        {
            if (row.TryGetCell(_source[i], out LtpValueRef v))
            {
                cells[i] = _table.ReadValue(v) ?? [];
            }
        }

        return true;
    }

    public void Dispose() => _table?.Dispose();
}

/// <summary>What a transaction has done to one folder, and what the commit must rewrite because of it.</summary>
internal sealed class FolderState
{
    private readonly PstWritableStore _store;
    private (ushort Id, ushort Type)[]? _contentsColumns;
    private (ushort Id, ushort Type)[]? _assocColumns;
    private bool _prepared;
    private Dictionary<ushort, (ushort Type, byte[] Value)>? _props;

    public FolderState(PstWritableStore store, Nid nid, Nid parent, bool isNew)
    {
        _store = store;
        Nid = nid;
        Parent = parent;
        IsNew = isNew;
    }

    public Nid Nid { get; }

    public Nid Parent { get; set; }

    public bool IsNew { get; }

    public bool IsRoot => Parent == Nid;

    public string? Name { get; set; }

    public string? ContainerClass { get; set; }

    public bool Renamed { get; set; }

    public bool Moved { get; set; }

    public bool ChildrenChanged { get; set; }

    public bool LastDroppedUnread { get; private set; }

    public SortedDictionary<uint, byte[]?[]> Added { get; } = [];

    public SortedDictionary<uint, byte[]?[]> AddedAssoc { get; } = [];

    public HashSet<uint> AddedUnread { get; } = [];

    public HashSet<uint> Removed { get; } = [];

    public HashSet<uint> RemovedAssoc { get; } = [];

    public int RemovedUnread { get; private set; }

    /// <summary>Folders added under this one (or moved under it) and the parent they came from (default for a new folder).</summary>
    public Dictionary<uint, Nid> AddedChildren { get; } = [];

    public HashSet<uint> RemovedChildren { get; } = [];

    /// <summary>Existing child folders whose row in this folder's hierarchy table needs new counts or a new name.</summary>
    public HashSet<uint> ChildUpdates { get; } = [];

    public bool HierarchyDirty => ChildrenChanged || ChildUpdates.Count > 0;

    // Values after the commit.
    public int Content { get; private set; }

    public int Unread { get; private set; }

    public int Assoc { get; private set; }

    public bool HasSubfolders { get; private set; }

    public bool PcDirty { get; private set; }

    public bool RowDirty { get; private set; }

    private int _oldContent;
    private int _oldUnread;
    private int _oldAssoc;
    private int _oldSubs;

    public (ushort Id, ushort Type)[] Columns(bool associated)
    {
        ref (ushort Id, ushort Type)[]? slot = ref (associated ? ref _assocColumns : ref _contentsColumns);
        if (slot is null)
        {
            (ushort Id, ushort Type)[] template = associated ? PstItemWriter.AssocColumns : PstItemWriter.ContentsColumns;
            if (IsNew)
            {
                slot = [.. template];
            }
            else
            {
                using OldTable t = OldTable.Open(_store.Committed, PstWritableStore.TableNid(Nid, associated ? NidType.AssocContentsTable : NidType.ContentsTable), template);
                slot = t.Columns;
            }
        }

        return slot;
    }

    public void AddRow(bool associated, uint id, byte[]?[] row, bool unread)
    {
        (associated ? AddedAssoc : Added)[id] = row;
        if (unread)
        {
            AddedUnread.Add(id);
        }
        else
        {
            AddedUnread.Remove(id);
        }
    }

    /// <summary>Takes back a row added in this transaction.</summary>
    public bool DropAddedRow(bool associated, uint id, out byte[]?[]? row)
    {
        if ((associated ? AddedAssoc : Added).Remove(id, out row))
        {
            LastDroppedUnread = AddedUnread.Remove(id);
            return true;
        }

        row = null;
        return false;
    }

    private static bool IsUnread((ushort Id, ushort Type)[] columns, byte[]?[] cells)
    {
        int i = Array.IndexOf(columns, ((ushort)0x0E07, (ushort)3));
        return i >= 0 && cells[i] is { Length: >= 4 } v && (v[0] & 1) == 0;
    }

    /// <summary>Notes that the committed row for <paramref name="id"/> goes away (or is replaced). Returns whether the item was unread.</summary>
    public bool RemoveCommittedRow(PstWritableStore store, bool associated, uint id)
    {
        byte[]?[]? cells = CommittedCells(store, associated, id);
        if (cells is null)
        {
            return false;
        }

        (associated ? RemovedAssoc : Removed).Add(id);
        bool unread = !associated && IsUnread(Columns(associated), cells);
        if (unread)
        {
            RemovedUnread++;
        }

        return unread;
    }

    public byte[]?[]? CommittedCells(PstWritableStore store, bool associated, uint id)
    {
        if (IsNew)
        {
            return null;
        }

        using OldTable t = OldTable.Open(store.Committed, PstWritableStore.TableNid(Nid, associated ? NidType.AssocContentsTable : NidType.ContentsTable),
            associated ? PstItemWriter.AssocColumns : PstItemWriter.ContentsColumns);
        return t.TryGetRow(id, out byte[]?[] cells) ? cells : null;
    }

    // ---- the commit ----------------------------------------------------------------------------------------------------------------------

    /// <summary>Reads the committed values and works out the new counts and what must be rewritten.</summary>
    public void Prepare()
    {
        if (_prepared)
        {
            return;
        }

        _prepared = true;
        if (!IsNew)
        {
            _props = ReadProperties(_store.Committed, Nid);
            _oldContent = Int(0x3602);
            _oldUnread = Int(0x3603);
            _oldAssoc = Int(0x3617);
            using OldTable h = OldTable.Open(_store.Committed, PstWritableStore.TableNid(Nid, NidType.HierarchyTable), PstItemWriter.HierarchyColumns);
            _oldSubs = h.RowCount;
            if (!Renamed && _props.TryGetValue(0x3001, out (ushort Type, byte[] Value) n))
            {
                Name = System.Text.Encoding.Unicode.GetString(n.Value);
            }

            if (_props.TryGetValue(0x3613, out (ushort Type, byte[] Value) c))
            {
                ContainerClass = System.Text.Encoding.Unicode.GetString(c.Value);
            }
        }

        Content = Math.Max(0, _oldContent + Added.Count - Removed.Count);
        Unread = Math.Max(0, _oldUnread - RemovedUnread + AddedUnread.Count);
        Assoc = Math.Max(0, _oldAssoc + AddedAssoc.Count - RemovedAssoc.Count);
        int subs = Math.Max(0, _oldSubs - RemovedChildren.Count + AddedChildren.Count);
        HasSubfolders = subs > 0;
        bool rows = Added.Count + Removed.Count + AddedAssoc.Count + RemovedAssoc.Count > 0;
        PcDirty = IsNew || Renamed || rows || ChildrenChanged;
        RowDirty = IsNew || Renamed || Content != _oldContent || Unread != _oldUnread || HasSubfolders != (_oldSubs > 0);
        _ = rows;
    }

    private int Int(ushort id) =>
        _props is not null && _props.TryGetValue(id, out (ushort Type, byte[] Value) p) && p.Value.Length >= 4 ? System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(p.Value) : 0;

    public bool ContentsDirty(bool associated) => IsNew || (associated ? AddedAssoc.Count + RemovedAssoc.Count : Added.Count + Removed.Count) > 0;

    public static Dictionary<ushort, (ushort Type, byte[] Value)> ReadProperties(NdbFile file, Nid nid)
    {
        var props = new Dictionary<ushort, (ushort, byte[])>();
        if (PropertyContext.TryOpen(file, nid, out PropertyContext? pc, out _) && pc is not null)
        {
            using (pc)
            {
                foreach (PcProperty p in pc)
                {
                    props[p.Id] = (p.Type, pc.ReadValue(p) ?? []);
                }
            }
        }

        return props;
    }

    /// <summary>Writes this folder's property context.</summary>
    public void WriteProperties(NdbEditor ed)
    {
        Dictionary<ushort, (ushort Type, byte[] Value)> props = IsNew ? [] : new Dictionary<ushort, (ushort, byte[])>(_props!);
        props[0x3001] = (LtpPropertyTypes.PtypString, PstWritableStore.Utf16(Name ?? string.Empty));
        props[0x3602] = (LtpPropertyTypes.PtypInteger32, PstWritableStore.I32(Content));
        props[0x3603] = (LtpPropertyTypes.PtypInteger32, PstWritableStore.I32(Unread));
        props[0x360A] = (LtpPropertyTypes.PtypBoolean, [(byte)(HasSubfolders ? 1 : 0)]);
        if (ContainerClass is not null && (IsNew || !props.ContainsKey(0x3613)))
        {
            props[0x3613] = (LtpPropertyTypes.PtypString, PstWritableStore.Utf16(ContainerClass));
        }

        if (Assoc > 0 || props.ContainsKey(0x3617))
        {
            props[0x3617] = (LtpPropertyTypes.PtypInteger32, PstWritableStore.I32(Assoc));
        }

        var pc = new PropertyContextBuilder(ed.Blocks);
        foreach (KeyValuePair<ushort, (ushort Type, byte[] Value)> e in props)
        {
            pc.Set(e.Key, e.Value.Type, e.Value.Value);
        }

        LtpNodeResult r = pc.Build();
        Nid parentRef = IsNew ? Parent : (ed.TryGetNode(Nid, out NodeEntry old) ? old.Parent : Parent);
        ed.UpsertNode(new NodeEntry(Nid, r.Data, r.WriteSubnodes(ed.Blocks), parentRef));
    }

    /// <summary>The row this folder has in its parent's hierarchy table, after the commit (MS-PST 2.4.4.4.1); starts from the row it had when it has one.</summary>
    public byte[]?[] HierarchyCells((ushort Id, ushort Type)[] columns, byte[]?[]? existing)
    {
        byte[]?[] cells = existing is not null && existing.Length == columns.Length ? (byte[]?[])existing.Clone() : new byte[]?[columns.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            switch (columns[i].Id)
            {
                case 0x3001:
                    cells[i] = PstWritableStore.Utf16(Name ?? string.Empty);
                    break;
                case 0x3602:
                    cells[i] = PstWritableStore.I32(Content);
                    break;
                case 0x3603:
                    cells[i] = PstWritableStore.I32(Unread);
                    break;
                case 0x360A:
                    cells[i] = [(byte)(HasSubfolders ? 1 : 0)];
                    break;
                case 0x3613 when ContainerClass is not null:
                    cells[i] = PstWritableStore.Utf16(ContainerClass);
                    break;
                case 0x6635 or 0x6636 when existing is null:
                    cells[i] = PstWritableStore.I32(0);
                    break;
            }
        }

        return cells;
    }
}

public sealed partial class PstWritableStore
{
    // Rewrites, for every folder a transaction touched, the property context, the tables and the parent's row for it.
    private void FlushFolders()
    {
        if (_folders.Count > 0)
        {
            foreach (FolderState f in _folders.Values.ToList())
            {
                f.Prepare();
            }

            foreach (FolderState f in _folders.Values.ToList())
            {
                if (f.RowDirty && !f.IsRoot)
                {
                    FolderOrThrow(f.Parent).ChildUpdates.Add(f.Nid.Value);
                }
            }

            foreach (FolderState f in _folders.Values.ToList())
            {
                f.Prepare();
            }

            foreach (FolderState f in _folders.Values.OrderBy(static f => f.Nid.Value).ToList())
            {
                WriteFolder(f);
            }
        }

        if (_items.NameCount > _seededNames)
        {
            // MS-PST 2.4.7: the name-to-ID map is one property context; it is written again with the new names added after the old ones.
            LtpNodeResult map = _items.BuildNameMapNode();
            Nid mapNid = new(PstBuilder.NidNameToIdMap);
            Nid parent = _ed.TryGetNode(mapNid, out NodeEntry old) ? old.Parent : default;
            _ed.UpsertNode(new NodeEntry(mapNid, map.Data, map.WriteSubnodes(_ed.Blocks), parent));
        }
    }

    private void WriteFolder(FolderState f)
    {
        if (f.PcDirty)
        {
            f.WriteProperties(_ed);
        }

        if (f.ContentsDirty(false))
        {
            WriteContents(f, associated: false);
        }

        if (f.ContentsDirty(true))
        {
            WriteContents(f, associated: true);
        }

        if (f.IsNew || f.HierarchyDirty)
        {
            WriteHierarchy(f);
        }
    }

    private void WriteContents(FolderState f, bool associated)
    {
        NidType type = associated ? NidType.AssocContentsTable : NidType.ContentsTable;
        (ushort Id, ushort Type)[] template = associated ? PstItemWriter.AssocColumns : PstItemWriter.ContentsColumns;
        using OldTable old = f.IsNew ? OldTable.Open(_ed.Committed, new Nid(0), template) : OldTable.Open(_ed.Committed, TableNid(f.Nid, type), template);
        HashSet<uint> removed = associated ? f.RemovedAssoc : f.Removed;
        SortedDictionary<uint, byte[]?[]> added = associated ? f.AddedAssoc : f.Added;
        PstItemWriter.WriteTable(_ed, TableNid(f.Nid, type), old.Columns, MergeRows(old, removed, added), streamHeap: true);
    }

    // The committed rows without the removed and replaced ones, with the added ones merged in by row ID.
    private static IEnumerable<(uint RowId, byte[]?[] Cells)> MergeRows(OldTable old, HashSet<uint> removed, SortedDictionary<uint, byte[]?[]> added)
    {
        using SortedDictionary<uint, byte[]?[]>.Enumerator a = added.GetEnumerator();
        bool haveAdded = a.MoveNext();
        foreach ((uint id, byte[]?[] cells) in old.Rows())
        {
            while (haveAdded && a.Current.Key < id)
            {
                yield return (a.Current.Key, a.Current.Value);
                haveAdded = a.MoveNext();
            }

            if (removed.Contains(id) || added.ContainsKey(id))
            {
                continue;
            }

            yield return (id, cells);
        }

        while (haveAdded)
        {
            yield return (a.Current.Key, a.Current.Value);
            haveAdded = a.MoveNext();
        }
    }

    private void WriteHierarchy(FolderState f)
    {
        using OldTable old = f.IsNew ? OldTable.Open(_ed.Committed, new Nid(0), PstItemWriter.HierarchyColumns) : OldTable.Open(_ed.Committed, TableNid(f.Nid, NidType.HierarchyTable), PstItemWriter.HierarchyColumns);
        var rows = new SortedDictionary<uint, byte[]?[]>();
        foreach ((uint id, byte[]?[] cells) in old.Rows())
        {
            if (f.RemovedChildren.Contains(id))
            {
                continue;
            }

            rows[id] = f.ChildUpdates.Contains(id) ? _folders[id].HierarchyCells(old.Columns, cells) : cells;
        }

        foreach ((uint child, Nid from) in f.AddedChildren)
        {
            byte[]?[]? existing = null;
            if (from.Value != 0)
            {
                using OldTable src = OldTable.Open(_ed.Committed, TableNid(from, NidType.HierarchyTable), PstItemWriter.HierarchyColumns);
                if (src.TryGetRow(child, out byte[]?[] cells))
                {
                    existing = Remap(cells, src.Columns, old.Columns);
                }
            }

            rows[child] = FolderOrThrow(new Nid(child)).HierarchyCells(old.Columns, existing);
        }

        PstItemWriter.WriteTable(_ed, TableNid(f.Nid, NidType.HierarchyTable), old.Columns, rows.Select(static r => (r.Key, r.Value)));
    }
}
