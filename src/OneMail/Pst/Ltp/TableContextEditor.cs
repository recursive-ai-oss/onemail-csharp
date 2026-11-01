using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Pst.Ltp;

/// <summary>
/// Adds, replaces and removes rows of an existing table context (MS-PST 2.3.4) without writing the rest of it again: the row index BTH changes through
/// <see cref="BthEditor"/> and the heap through <see cref="HeapEditor"/>, and the row matrix changes only in the blocks that hold the rows touched. The new
/// tree shares every other block with the old one, and <see cref="Apply"/> replaces the node.
/// </summary>
/// <remarks>
/// <para>A removed row's place is taken by the last row of the matrix, so the matrix stays dense (2.3.4.4); the row index follows. The matrix is not kept in row ID
/// order, which the format does not ask for: the row index finds rows by ID.</para>
/// <para>Only a table whose row matrix is a subnode and whose values fit heap allocations is edited this way (a smaller table is cheap to write whole). Anything else
/// makes <see cref="TryOpen"/> return null, or a row operation throw <see cref="HeapEditUnsupported"/> before anything is written; the caller then rewrites the
/// table whole. The editor reads the committed file, so the node must be as committed.</para>
/// </remarks>
internal sealed class TableContextEditor : IDisposable
{
    private const ushort RowIdColumn = 0x67F2;
    private const ushort RowVerColumn = 0x67F3;
    private const int TcInfoHeaderSize = 22;
    private const int ColumnDescriptorSize = 8;

    private sealed class Blk
    {
        public readonly byte[] Buf = new byte[NdbBlockBuilder.MaxBlockData];
        public bool Dirty;
    }

    private readonly NdbEditor _ed;
    private readonly NdbFile _file;
    private readonly Nid _nid;
    private readonly NodeEntry _node;
    private readonly HeapEditor _heap;
    private readonly BthEditor _index;
    private readonly TcColumn[] _cols;
    private readonly int[] _cellColumns;         // TCINFO column index for each cell the caller supplies
    private readonly int _bitmapAt;              // TCI_1b
    private readonly int _rowIdOffset;
    private readonly NdbDataTree _matrix;
    private readonly Nid _matrixNid;
    private readonly IReadOnlyList<SubnodeEntry> _subnodes;
    private readonly int _fullLength;
    private readonly int _oldLeaves;
    private readonly Dictionary<int, Blk> _blocks = [];
    private readonly HashSet<uint> _droppedSubnodes = [];
    private int _leaves;
    private int _minLeaves;
    private bool _touched;

    private TableContextEditor(NdbEditor ed, NdbFile file, NodeEntry node, HeapEditor heap, BthEditor index, TcColumn[] cols, int[] ends, NdbDataTree matrix, Nid matrixNid,
        IReadOnlyList<SubnodeEntry> subnodes, int rowsPerBlock, int fullLength, int rowCount)
    {
        _ed = ed;
        _file = file;
        _node = node;
        _nid = node.Nid;
        _heap = heap;
        _index = index;
        _cols = cols;
        _bitmapAt = ends[2];
        RowSize = ends[3];
        _matrix = matrix;
        _matrixNid = matrixNid;
        _subnodes = subnodes;
        RowsPerBlock = rowsPerBlock;
        _fullLength = fullLength;
        _oldLeaves = _leaves = _minLeaves = matrix.LeafCount;
        RowCount = rowCount;

        var cells = new List<int>();
        for (int i = 0; i < cols.Length; i++)
        {
            if (cols[i].Id == RowIdColumn)
            {
                _rowIdOffset = cols[i].Offset;
            }

            if (cols[i].Id is not (RowIdColumn or RowVerColumn))
            {
                cells.Add(i);
            }
        }

        _cellColumns = [.. cells];
    }

    /// <summary>The bytes of one row.</summary>
    public int RowSize { get; }

    /// <summary>How many rows a full matrix block holds.</summary>
    public int RowsPerBlock { get; }

    /// <summary>The rows the table has now.</summary>
    public int RowCount { get; private set; }

    /// <summary>The columns the caller's cells are aligned to: the table's own, without PidTagLtpRowId and PidTagLtpRowVer.</summary>
    public IEnumerable<(ushort Id, ushort Type)> CellColumns
    {
        get
        {
            foreach (int c in _cellColumns)
            {
                yield return (_cols[c].Id, _cols[c].Type);
            }
        }
    }

    /// <summary>Opens a table context node for editing.</summary>
    /// <returns>The editor, or null when the node is not a table this layer edits (see the remarks).</returns>
    public static TableContextEditor? TryOpen(NdbEditor ed, Nid nid)
    {
        ArgumentNullException.ThrowIfNull(ed);
        NdbFile file = ed.Committed;
        if (!file.Nodes.TryGet(nid, out NodeEntry committed) || !ed.TryGetNode(nid, out NodeEntry current) || committed.Data != current.Data || committed.Sub != current.Sub
            || committed.Sub.IsNull)
        {
            return null;
        }

        HeapEditor? heap = null;
        NdbDataTree? matrix = null;
        try
        {
            heap = HeapEditor.TryOpen(ed, file, committed.Data);
            if (heap is null || heap.ClientSignature != 0x7C)
            {
                heap?.Dispose();
                return null;
            }

            ReadOnlySpan<byte> info = heap.Read(heap.UserRoot);
            if (info.Length < TcInfoHeaderSize || info[0] != 0x7C)
            {
                heap.Dispose();
                return null;
            }

            int cCols = info[1];
            if (TcInfoHeaderSize + (cCols * ColumnDescriptorSize) > info.Length)
            {
                heap.Dispose();
                return null;
            }

            int[] ends = [BinaryPrimitives.ReadUInt16LittleEndian(info[2..]), BinaryPrimitives.ReadUInt16LittleEndian(info[4..]), BinaryPrimitives.ReadUInt16LittleEndian(info[6..]),
                BinaryPrimitives.ReadUInt16LittleEndian(info[8..])];
            var hidRowIndex = new Hid(BinaryPrimitives.ReadUInt32LittleEndian(info[10..]));
            var hnidRows = new Hnid(BinaryPrimitives.ReadUInt32LittleEndian(info[14..]));
            int rowSize = ends[3];
            if (hidRowIndex.IsNull || hnidRows.IsHid || rowSize == 0 || rowSize > NdbBlockBuilder.MaxBlockData || ends[0] > ends[1] || ends[1] > ends[2] || ends[2] > ends[3])
            {
                heap.Dispose();
                return null;
            }

            var cols = new TcColumn[cCols];
            bool hasRowId = false;
            for (int i = 0; i < cCols; i++)
            {
                ReadOnlySpan<byte> d = info.Slice(TcInfoHeaderSize + (i * ColumnDescriptorSize), ColumnDescriptorSize);
                uint tag = BinaryPrimitives.ReadUInt32LittleEndian(d);
                cols[i] = new TcColumn((ushort)(tag >> 16), (ushort)tag, BinaryPrimitives.ReadUInt16LittleEndian(d[4..]), d[6], d[7]);
                if (cols[i].Offset + cols[i].Size > ends[2] || (cols[i].BitIndex >> 3) >= ends[3] - ends[2])
                {
                    heap.Dispose();
                    return null;
                }

                hasRowId |= cols[i].Id == RowIdColumn && cols[i].Offset == 0 && cols[i].Size == 4;
            }

            BthEditor? index = BthEditor.TryOpen(heap, hidRowIndex);
            SubnodeTree? tree = file.OpenSubnodes(committed.Sub);
            if (!hasRowId || index is null || tree is null)
            {
                heap.Dispose();
                return null;
            }

            SubnodeEntry? matrixEntry = null;
            foreach (SubnodeEntry e in tree.Entries)
            {
                if (e.Nid.Value == hnidRows.Value)
                {
                    matrixEntry = e;
                }
            }

            matrix = matrixEntry is { } me && !me.Data.IsNull ? file.OpenDataTree(me.Data) : null;
            if (matrix is null || matrix.LeafCount == 0)
            {
                matrix?.Dispose();
                heap.Dispose();
                return null;
            }

            // MS-PST 2.3.4.4: every block but the last holds the same number of whole rows. The first block's size says how many; a table of one block fills a whole one.
            int leaves = matrix.LeafCount;
            int first = matrix.GetLeafSize(0);
            int rowsPerBlock = leaves > 1 ? first / rowSize : NdbBlockBuilder.MaxBlockData / rowSize;
            int last = matrix.GetLeafSize(leaves - 1);
            bool shape = rowsPerBlock > 0 && last > 0 && last % rowSize == 0 && last / rowSize <= rowsPerBlock;
            for (int i = 1; shape && i < leaves - 1; i++)
            {
                shape = matrix.GetLeafSize(i) == first;
            }

            if (!shape)
            {
                matrix.Dispose();
                heap.Dispose();
                return null;
            }

            int rows = ((leaves - 1) * rowsPerBlock) + (last / rowSize);
            int fullLength = leaves > 1 ? first : NdbBlockBuilder.MaxBlockData;
            var editor = new TableContextEditor(ed, file, committed, heap, index, cols, ends, matrix, new Nid(hnidRows.Value), tree.Entries, rowsPerBlock, fullLength, rows);

            // The row count comes from the matrix; the last row has to be the one the row index says is last, or the matrix is not what it seems.
            byte[] lastRow = new byte[rowSize];
            editor.ReadRow(rows - 1, lastRow);
            uint lastId = BinaryPrimitives.ReadUInt32LittleEndian(lastRow.AsSpan(editor._rowIdOffset));
            if (!index.TryFind(lastId, out uint at) || at != rows - 1)
            {
                editor.Dispose();
                return null;
            }

            return editor;
        }
        catch (HeapEditUnsupported)
        {
            matrix?.Dispose();
            heap?.Dispose();
            return null;
        }
    }

    private int RowsIn(int leaf) => leaf < _leaves - 1 ? RowsPerBlock : RowCount - ((_leaves - 1) * RowsPerBlock);

    // The block as it stands: read from the file the first time, blank when the leaf is new.
    private Blk Block(int leaf)
    {
        if (_blocks.TryGetValue(leaf, out Blk? b))
        {
            return b;
        }

        b = new Blk();
        if (leaf < _minLeaves && leaf < _oldLeaves)
        {
            if (!_file.TryReadBlock(_matrix.GetLeaf(leaf), b.Buf, out _, out _))
            {
                throw new HeapEditUnsupported($"row matrix block {leaf} cannot be read");
            }
        }

        _blocks[leaf] = b;
        return b;
    }

    private void ReadRow(int index, Span<byte> row) => Block(index / RowsPerBlock).Buf.AsSpan((index % RowsPerBlock) * RowSize, RowSize).CopyTo(row);

    private Span<byte> RowSpan(int index)
    {
        Blk b = Block(index / RowsPerBlock);
        b.Dirty = true;
        return b.Buf.AsSpan((index % RowsPerBlock) * RowSize, RowSize);
    }

    /// <summary>Finds a row by its ID.</summary>
    public bool TryFindRow(uint rowId, out int rowIndex)
    {
        rowIndex = 0;
        if (!_index.TryFind(rowId, out uint at))
        {
            return false;
        }

        rowIndex = (int)at;
        return at < RowCount;
    }

    private static bool IsInline(in TcColumn c) => LtpPropertyTypes.FixedSize(c.Type) is 1 or 2 or 4 or 8;

    // Frees what a row's cells point to: heap allocations now, subnodes when the table is applied.
    private void ReleaseCells(ReadOnlySpan<byte> row)
    {
        foreach (TcColumn c in _cols)
        {
            if (IsInline(c) || c.Size != 4 || (row[_bitmapAt + (c.BitIndex >> 3)] & (0x80 >> (c.BitIndex & 7))) == 0)
            {
                continue;
            }

            uint hnid = BinaryPrimitives.ReadUInt32LittleEndian(row[c.Offset..]);
            if (hnid == 0)
            {
                continue;
            }

            if ((hnid & 0x1F) == 0)
            {
                _heap.Free(new Hid(hnid));
            }
            else
            {
                _droppedSubnodes.Add(hnid);
            }
        }
    }

    /// <summary>Removes a row; nothing happens when there is none with that ID.</summary>
    /// <exception cref="HeapEditUnsupported">The table is not laid out as this editor handles; nothing has been written.</exception>
    public void Remove(uint rowId)
    {
        if (!TryFindRow(rowId, out int at))
        {
            return;
        }

        _touched = true;
        byte[] row = new byte[RowSize];
        ReadRow(at, row);
        ReleaseCells(row);
        _index.Remove(rowId);
        int last = RowCount - 1;
        if (at != last)
        {
            byte[] moved = new byte[RowSize];
            ReadRow(last, moved);
            moved.CopyTo(RowSpan(at));
            _index.Upsert(BinaryPrimitives.ReadUInt32LittleEndian(moved.AsSpan(_rowIdOffset)), (uint)at);
        }

        RowSpan(last).Clear();
        RowCount--;
        if (RowCount == (_leaves - 1) * RowsPerBlock && _leaves > 0)
        {
            // The last block has no rows left.
            _blocks.Remove(_leaves - 1);
            _leaves--;
            _minLeaves = Math.Min(_minLeaves, _leaves);
        }
    }

    /// <summary>Adds a row, or replaces the row with that ID.</summary>
    /// <param name="rowId">dwRowID.</param>
    /// <param name="cells">One entry per column of <see cref="CellColumns"/>; null leaves the cell absent.</param>
    /// <exception cref="HeapEditUnsupported">A value does not fit a heap allocation or has the wrong length for its type, or the table is not laid out as this editor handles; nothing has been written.</exception>
    public void Upsert(uint rowId, ReadOnlySpan<byte[]?> cells)
    {
        if (cells.Length != _cellColumns.Length)
        {
            throw new ArgumentException($"the table has {_cellColumns.Length} cells per row, {cells.Length} given", nameof(cells));
        }

        // Checked before anything changes, so a refusal leaves the editor as it was.
        for (int i = 0; i < cells.Length; i++)
        {
            TcColumn c = _cols[_cellColumns[i]];
            if (cells[i] is not { } v)
            {
                continue;
            }

            int fixedSize = LtpPropertyTypes.FixedSize(c.Type);
            if (IsInline(c) ? v.Length != c.Size : c.Size != 4 || (fixedSize > 0 && v.Length != fixedSize) || v.Length > HeapNodeBuilder.MaxAllocation)
            {
                throw new HeapEditUnsupported($"the value of column 0x{c.Tag:x8} cannot be written in place");
            }
        }

        _touched = true;
        bool replace = TryFindRow(rowId, out int at);
        if (replace)
        {
            byte[] old = new byte[RowSize];
            ReadRow(at, old);
            ReleaseCells(old);
        }
        else
        {
            at = RowCount;
            if (at / RowsPerBlock == _leaves)
            {
                _leaves++;
            }

            RowCount++;
        }

        byte[] row = new byte[RowSize];
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(_rowIdOffset), rowId);
        foreach (TcColumn c in _cols)
        {
            if (c.Id == RowIdColumn || c.Id == RowVerColumn)
            {
                // PidTagLtpRowVer is zero in every row, as the builder writes it.
                row[_bitmapAt + (c.BitIndex >> 3)] |= (byte)(0x80 >> (c.BitIndex & 7));
            }
        }

        for (int i = 0; i < cells.Length; i++)
        {
            if (cells[i] is not { } v)
            {
                continue;
            }

            TcColumn c = _cols[_cellColumns[i]];
            row[_bitmapAt + (c.BitIndex >> 3)] |= (byte)(0x80 >> (c.BitIndex & 7));
            if (IsInline(c))
            {
                v.CopyTo(row.AsSpan(c.Offset));
            }
            else if (v.Length > 0)
            {
                Hid hid = _heap.Allocate(v.Length, _heap.LastBlock);
                v.CopyTo(_heap.Write(hid));
                BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(c.Offset), hid.Value);
            }
        }

        row.CopyTo(RowSpan(at));
        _index.Upsert(rowId, (uint)at);
    }

    /// <summary>Writes the new blocks and replaces the node.</summary>
    /// <returns>False when nothing was written because the table would be left without rows; the caller writes it whole. True otherwise, including when nothing changed.</returns>
    public bool Apply()
    {
        if (!_touched)
        {
            return true;
        }

        if (RowCount == 0)
        {
            return false;
        }

        // A block that was the last and stops being it is written full length; the last block is as long as its rows (MS-PST 2.3.4.4).
        if (_leaves > _oldLeaves && _minLeaves >= _oldLeaves)
        {
            Block(_oldLeaves - 1).Dirty = true;
        }

        int lastLeaf = _leaves - 1;
        if (lastLeaf < _oldLeaves && lastLeaf < _minLeaves && _matrix.GetLeafSize(lastLeaf) != RowsIn(lastLeaf) * RowSize)
        {
            Block(lastLeaf).Dirty = true;
        }

        var tree = new NdbDataTreeEditor(_ed, _matrix);
        if (_minLeaves < _oldLeaves)
        {
            tree.Truncate(_minLeaves);
        }

        for (int leaf = 0; leaf < _leaves; leaf++)
        {
            if (!_blocks.TryGetValue(leaf, out Blk? b) || !b.Dirty)
            {
                continue;
            }

            byte[] data = b.Buf.AsSpan(0, leaf == lastLeaf ? RowsIn(leaf) * RowSize : _fullLength).ToArray();
            if (leaf < tree.LeafCount)
            {
                tree.Replace(leaf, data);
            }
            else if (leaf == tree.LeafCount)
            {
                tree.Append(data);
            }
            else
            {
                throw new InvalidOperationException("row matrix blocks were added out of order");
            }
        }

        // Everything that can refuse has run: from here on blocks are written.
        Bid heapData = _node.Data;
        if (_heap.Changed)
        {
            heapData = _heap.Finish().Root;
        }

        DataTreeResult rows = tree.Build();

        // The node's subnodes: the matrix's entry names the new tree, entries for values that went with removed rows are dropped, the others are kept as they are.
        var entries = new List<SubnodeEntry>(_subnodes.Count);
        foreach (SubnodeEntry e in _subnodes)
        {
            if (e.Nid == _matrixNid)
            {
                _ed.AddReference(e.Sub);
                entries.Add(e with { Data = rows.Root });
            }
            else if (!_droppedSubnodes.Contains(e.Nid.Value))
            {
                _ed.AddReference(e.Data);
                _ed.AddReference(e.Sub);
                entries.Add(e);
            }
        }

        Bid sub = _ed.Blocks.WriteSubnodes(entries);
        _ed.UpsertNode(new NodeEntry(_nid, heapData, sub, _node.Parent));
        return true;
    }

    /// <summary>Releases the readers.</summary>
    public void Dispose()
    {
        _matrix.Dispose();
        _heap.Dispose();
    }
}
