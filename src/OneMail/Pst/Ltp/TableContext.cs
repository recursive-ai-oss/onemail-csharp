using System;
using System.Collections.Generic;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Pst.Ltp;

/// <summary>One column of a table context (MS-PST 2.3.4.2 TCOLDESC).</summary>
/// <param name="Id">The property ID of the column (the high half of the column's tag).</param>
/// <param name="Type">The property type code of the column.</param>
/// <param name="Offset">ibData: where the cell starts in a row.</param>
/// <param name="Size">cbData: the cell's size in bytes (1, 2, 4 or 8 for an inline value; 4 for an HNID).</param>
/// <param name="BitIndex">iBit: the column's bit in the row's cell-existence bitmap.</param>
public readonly record struct TcColumn(ushort Id, ushort Type, int Offset, int Size, int BitIndex)
{
    /// <summary>The 32-bit property tag: ID in the high half, type in the low half.</summary>
    public uint Tag => ((uint)Id << 16) | Type;
}

/// <summary>One record of a table's row index: a row's ID and its position in the row matrix.</summary>
/// <param name="RowId">dwRowID: the row's identifier (for a contents table, the item's NID).</param>
/// <param name="RowIndex">dwRowIndex: the row's zero-based position in the row matrix.</param>
public readonly record struct TcRowIndexEntry(uint RowId, uint RowIndex);

/// <summary>
/// A table context (MS-PST 2.3.4): rows and columns over a heap-on-node. The TCINFO names the columns; a row index BTH maps
/// row IDs to row positions; the row matrix (in the heap or a subnode) holds fixed-size rows. Cell values are raw references
/// (<see cref="LtpValueRef"/>); fixed-size cells of up to eight bytes are inline in the row, the rest are HNIDs.
/// </summary>
/// <remarks>
/// <para>Reading a row copies its bytes into a buffer the caller supplies, so walking a table allocates nothing per row.</para>
/// <para>Reading never throws for malformed content; see <see cref="Defects"/>. Not thread-safe.</para>
/// </remarks>
public sealed class TableContext : IDisposable
{
    private const byte TcClientSignature = 0x7C;   // MS-PST 2.3.1.2 bTypeTC
    private const int TcInfoHeaderSize = 22;       // bType, cCols, rgib[4], hidRowIndex, hnidRows, hidIndex
    private const int ColumnDescriptorSize = 8;

    private readonly bool _ownsHeap;
    private readonly TcColumn[] _columns;
    private readonly int _endOfFixed4;   // TCI_4b
    private readonly int _endOfFixed2;   // TCI_2b
    private readonly int _endOfFixed1;   // TCI_1b: the cell-existence bitmap starts here
    private readonly Bth? _rowIndex;
    private readonly HeapAllocation _rowsInHeap;
    private readonly bool _rowsAreInHeap;
    private readonly NdbDataTree? _rowsTree;
    private readonly int _rowsPerBlock;
    private int _rowCount = -1;

    private TableContext(HeapNode heap, bool ownsHeap, TcColumn[] columns, int[] ends, Bth? rowIndex, bool rowsInHeap, HeapAllocation rowsAlloc, NdbDataTree? rowsTree)
    {
        Heap = heap;
        _ownsHeap = ownsHeap;
        _columns = columns;
        _endOfFixed4 = ends[0];
        _endOfFixed2 = ends[1];
        _endOfFixed1 = ends[2];
        RowSize = ends[3];
        _rowIndex = rowIndex;
        _rowsAreInHeap = rowsInHeap;
        _rowsInHeap = rowsAlloc;
        _rowsTree = rowsTree;
        // The rows of a subnode matrix never straddle a block, so every block but the last holds the same number of whole rows;
        // the first block's size says how many (MS-PST 2.3.4.4.1 describes the layout, and the block size differs by file generation).
        _rowsPerBlock = rowsTree is null || RowSize == 0 ? 0 : rowsTree.LeafCount <= 1 ? int.MaxValue : Math.Max(1, rowsTree.GetLeafSize(0) / RowSize);
    }

    /// <summary>The heap-on-node the context lives in.</summary>
    public HeapNode Heap { get; }

    /// <summary>The columns, in the order of the TCINFO (sorted by tag in a well-formed file).</summary>
    public IReadOnlyList<TcColumn> Columns => _columns;

    /// <summary>The size of one row in bytes (TCI_bm): the fixed cells followed by the cell-existence bitmap.</summary>
    public int RowSize { get; }

    /// <summary>The row index BTH (4-byte row IDs to 4-byte row positions); null when the table has none or it is unusable.</summary>
    public Bth? RowIndex => _rowIndex;

    /// <summary>What was found wrong in this context so far (the heap's list).</summary>
    public IReadOnlyList<LtpDefect> Defects => Heap.Defects;

    /// <summary>The number of rows: the records of the row index (walks the index on first use, then remembered).</summary>
    public int RowCount
    {
        get
        {
            if (_rowCount < 0)
            {
                int n = 0;
                if (_rowIndex is not null)
                {
                    BthCursor c = _rowIndex.CreateCursor();
                    while (c.MoveNext())
                    {
                        n++;
                    }
                }

                _rowCount = n;
            }

            return _rowCount;
        }
    }

    /// <summary>Opens the table context of a node, looked up in the node B-tree.</summary>
    /// <param name="file">The open file.</param>
    /// <param name="node">The node's NID.</param>
    /// <param name="table">The table, or null when it cannot be used.</param>
    /// <param name="defects">Why it cannot be used (empty on success; <see cref="Defects"/> has later findings).</param>
    /// <param name="options">Limits; null for defaults.</param>
    /// <returns>True when the node is a heap whose root is a TCINFO.</returns>
    public static bool TryOpen(NdbFile file, Nid node, out TableContext? table, out IReadOnlyList<LtpDefect> defects, LtpOptions? options = null)
    {
        table = null;
        return HeapNode.TryOpen(file, node, out HeapNode? heap, out defects, options) && TryOpenOwned(heap!, out table, out defects);
    }

    /// <summary>Opens the table context of a subnode (a recipient or attachment table under a message).</summary>
    /// <param name="file">The open file.</param>
    /// <param name="subnode">The subnode entry.</param>
    /// <param name="table">The table, or null when it cannot be used.</param>
    /// <param name="defects">Why it cannot be used.</param>
    /// <param name="options">Limits; null for defaults.</param>
    /// <returns>True when the subnode is a heap whose root is a TCINFO.</returns>
    public static bool TryOpen(NdbFile file, in SubnodeEntry subnode, out TableContext? table, out IReadOnlyList<LtpDefect> defects, LtpOptions? options = null)
    {
        table = null;
        return HeapNode.TryOpen(file, subnode, out HeapNode? heap, out defects, options) && TryOpenOwned(heap!, out table, out defects);
    }

    /// <summary>Opens a table context over a heap the caller already has. The context does not dispose the heap.</summary>
    /// <param name="heap">The heap.</param>
    /// <param name="table">The table, or null when it cannot be used.</param>
    /// <returns>True when the heap's root is a TCINFO; otherwise defects are on the heap.</returns>
    public static bool TryOpen(HeapNode heap, out TableContext? table)
    {
        ArgumentNullException.ThrowIfNull(heap);
        return TryOpenCore(heap, ownsHeap: false, out table);
    }

    private static bool TryOpenOwned(HeapNode heap, out TableContext? table, out IReadOnlyList<LtpDefect> defects)
    {
        if (TryOpenCore(heap, ownsHeap: true, out table))
        {
            defects = [];
            return true;
        }

        defects = [.. heap.Defects];
        heap.Dispose();
        return false;
    }

    private static bool TryOpenCore(HeapNode heap, bool ownsHeap, out TableContext? table)
    {
        table = null;
        if (heap.ClientSignature != TcClientSignature)
        {
            heap.Report(LtpDefectKind.HeapClientSignatureMismatch, $"bClientSig is 0x{heap.ClientSignature:x2}; a table context has 0x7C");
            return false;
        }

        if (!heap.TryGetAllocation(heap.UserRoot, out HeapAllocation infoAlloc))
        {
            return false;
        }

        ReadOnlySpan<byte> info = heap.GetBytes(infoAlloc);
        if (info.Length < TcInfoHeaderSize || info[0] != TcClientSignature)
        {
            heap.Report(LtpDefectKind.TcBadInfo, $"TCINFO is {info.Length} bytes with bType 0x{(info.Length > 0 ? info[0] : 0):x2}");
            return false;
        }

        // MS-PST 2.3.4.1: cCols column descriptors follow the 22-byte fixed part.
        int cCols = info[1];
        if (TcInfoHeaderSize + (cCols * ColumnDescriptorSize) > info.Length)
        {
            heap.Report(LtpDefectKind.TcBadInfo, $"TCINFO holds {info.Length} bytes, too few for {cCols} columns");
            return false;
        }

        int[] ends = [LtpBytes.U16(info, 2), LtpBytes.U16(info, 4), LtpBytes.U16(info, 6), LtpBytes.U16(info, 8)];
        var hidRowIndex = new Hid(LtpBytes.U32(info, 10));
        var hnidRows = new Hnid(LtpBytes.U32(info, 14));

        // The groups run 4/8-byte cells, 2-byte cells, 1-byte cells, then the bitmap, so the end offsets must not decrease,
        // and the bitmap needs one bit per column.
        int cebSize = ends[3] - ends[2];
        if (ends[0] > ends[1] || ends[1] > ends[2] || ends[2] > ends[3] || cebSize < (cCols + 7) / 8)
        {
            heap.Report(LtpDefectKind.TcBadLayout, $"TCINFO row groups end at {ends[0]}, {ends[1]}, {ends[2]}, {ends[3]}; the bitmap has {Math.Max(0, cebSize)} bytes for {cCols} columns");
        }

        var columns = new TcColumn[cCols];
        for (int i = 0; i < cCols; i++)
        {
            ReadOnlySpan<byte> d = info.Slice(TcInfoHeaderSize + (i * ColumnDescriptorSize), ColumnDescriptorSize);
            uint tag = LtpBytes.U32(d, 0);
            columns[i] = new TcColumn((ushort)(tag >> 16), (ushort)tag, LtpBytes.U16(d, 4), d[6], d[7]);
            if (columns[i].Offset + columns[i].Size > ends[3])
            {
                heap.Report(LtpDefectKind.TcBadLayout, $"column {i} (tag 0x{tag:x8}) spans {columns[i].Offset}..{columns[i].Offset + columns[i].Size}, outside the {ends[3]}-byte row");
            }
        }

        Bth? rowIndex = null;
        if (!hidRowIndex.IsNull && Bth.TryOpen(heap, hidRowIndex, out Bth? bth) && bth is not null)
        {
            if (bth.KeySize == 4 && bth.DataSize == 4)
            {
                rowIndex = bth;
            }
            else
            {
                heap.Report(LtpDefectKind.TcRowIndexShape, $"row index BTH has cbKey {bth.KeySize} and cbEnt {bth.DataSize}; expected 4 and 4");
            }
        }

        // MS-PST 2.3.4.4: hnidRows is a HID when the matrix fits in a heap allocation, otherwise the NID of a subnode.
        bool rowsInHeap = false;
        HeapAllocation rowsAlloc = default;
        NdbDataTree? rowsTree = null;
        if (hnidRows.IsHid)
        {
            if (!hnidRows.Hid.IsNull)
            {
                rowsInHeap = heap.TryGetAllocation(hnidRows.Hid, out rowsAlloc);
            }
        }
        else
        {
            rowsTree = heap.OpenSubnodeData(hnidRows.Nid);
        }

        table = new TableContext(heap, ownsHeap, columns, ends, rowIndex, rowsInHeap, rowsAlloc, rowsTree);
        return true;
    }

    /// <summary>Finds a column by property ID.</summary>
    /// <param name="id">The property ID.</param>
    /// <returns>The column's position in <see cref="Columns"/>, or -1.</returns>
    public int FindColumn(ushort id)
    {
        for (int i = 0; i < _columns.Length; i++)
        {
            if (_columns[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Looks a row ID up in the row index.</summary>
    /// <param name="rowId">The row ID.</param>
    /// <param name="rowIndex">The row's position in the matrix, when found.</param>
    /// <returns>True when the row exists.</returns>
    public bool TryFindRow(uint rowId, out uint rowIndex)
    {
        rowIndex = 0;
        if (_rowIndex is null)
        {
            return false;
        }

        Span<byte> key = [(byte)rowId, (byte)(rowId >> 8), (byte)(rowId >> 16), (byte)(rowId >> 24)];
        Span<byte> data = [0, 0, 0, 0];
        if (!_rowIndex.TryFind(key, data))
        {
            return false;
        }

        rowIndex = LtpBytes.U32(data, 0);
        return true;
    }

    /// <summary>Enumerates the row index in row-ID order.</summary>
    /// <returns>The records.</returns>
    public IEnumerable<TcRowIndexEntry> EnumerateRowIndex()
    {
        if (_rowIndex is null)
        {
            yield break;
        }

        BthCursor c = _rowIndex.CreateCursor();
        while (c.MoveNext())
        {
            yield return new TcRowIndexEntry((uint)c.KeyValue, LtpBytes.U32(c.Data, 0));
        }
    }

    /// <summary>Copies one row of the matrix into <paramref name="buffer"/>.</summary>
    /// <param name="rowIndex">The row's zero-based position in the matrix (not its row ID; see <see cref="TryFindRow"/>).</param>
    /// <param name="buffer">Receives <see cref="RowSize"/> bytes; must be at least that long.</param>
    /// <returns>False, with a defect, when the matrix is missing or the row lies outside it.</returns>
    /// <exception cref="ArgumentException"><paramref name="buffer"/> is shorter than <see cref="RowSize"/>.</exception>
    public bool TryReadRow(uint rowIndex, Span<byte> buffer)
    {
        if (buffer.Length < RowSize)
        {
            throw new ArgumentException($"the buffer must hold {RowSize} bytes", nameof(buffer));
        }

        if (RowSize == 0)
        {
            Heap.Report(LtpDefectKind.TcRowOutOfRange, "the table has zero-byte rows");
            return false;
        }

        if (_rowsAreInHeap)
        {
            long start = (long)rowIndex * RowSize;
            if (start + RowSize > _rowsInHeap.Length)
            {
                Heap.Report(LtpDefectKind.TcRowOutOfRange, $"row {rowIndex} ends at {start + RowSize}, past the {_rowsInHeap.Length}-byte row matrix");
                return false;
            }

            ReadOnlySpan<byte> m = Heap.GetBytes(_rowsInHeap);
            if (m.Length < start + RowSize)
            {
                return false;
            }

            m.Slice((int)start, RowSize).CopyTo(buffer);
            return true;
        }

        if (_rowsTree is not null)
        {
            long block = rowIndex / _rowsPerBlock;
            int within = (int)(rowIndex % _rowsPerBlock) * RowSize;
            if (block >= _rowsTree.LeafCount || _rowsTree.ReadLeaf((int)block, within, buffer[..RowSize]) != RowSize)
            {
                Heap.Report(LtpDefectKind.TcRowOutOfRange, $"row {rowIndex} is not in the row matrix ({_rowsTree.LeafCount} blocks)");
                return false;
            }

            return true;
        }

        Heap.Report(LtpDefectKind.TcRowOutOfRange, $"row {rowIndex} requested but the table has no row matrix");
        return false;
    }

    /// <summary>Wraps row bytes read by <see cref="TryReadRow"/> so cells can be looked up.</summary>
    /// <param name="rowBytes">The row's bytes.</param>
    /// <returns>The row view.</returns>
    public TableRow GetRow(ReadOnlySpan<byte> rowBytes) => new(this, rowBytes);

    internal bool IsPresent(ReadOnlySpan<byte> row, in TcColumn c)
    {
        // MS-PST 2.3.4.4.2: bit iBit of the bitmap, counting from the high bit of the first byte, says whether the cell holds a value.
        int at = _endOfFixed1 + (c.BitIndex >> 3);
        return at < row.Length && at < RowSize && (row[at] & (0x80 >> (c.BitIndex & 7))) != 0;
    }

    internal bool TryGetCell(ReadOnlySpan<byte> row, int column, out LtpValueRef value)
    {
        value = default;
        if ((uint)column >= (uint)_columns.Length)
        {
            return false;
        }

        TcColumn c = _columns[column];
        if (!IsPresent(row, c) || c.Offset + c.Size > row.Length)
        {
            return false;
        }

        ReadOnlySpan<byte> cell = row.Slice(c.Offset, c.Size);
        int fixedSize = LtpPropertyTypes.FixedSize(c.Type);

        // MS-PST 2.3.4.4.1: a cell of 1, 2, 4 or 8 bytes holds the value; a variable-size value or one larger than eight bytes
        // is a 4-byte HNID. A four-byte cell for a type of another size can only be an HNID.
        if (fixedSize is > 0 and <= 8 && c.Size == fixedSize)
        {
            value = LtpValueRef.FromInline(LtpBytes.ReadUInt64Partial(cell), c.Size);
            return true;
        }

        if (c.Size == 4)
        {
            value = LtpValueRef.FromHnid(LtpBytes.U32(cell, 0));
            return true;
        }

        if (fixedSize == 0 && c.Size is 1 or 2 or 8)
        {
            // A type this layer does not know, stored in a size that cannot be an HNID: keep the bytes.
            value = LtpValueRef.FromInline(LtpBytes.ReadUInt64Partial(cell), c.Size);
            return true;
        }

        Heap.Report(LtpDefectKind.TcCellShape, $"column 0x{c.Tag:x8} has a {c.Size}-byte cell, which does not fit its type");
        return false;
    }

    /// <summary>The length of a cell value in bytes; -1, with a defect, when it cannot be found.</summary>
    /// <param name="value">The cell reference.</param>
    /// <returns>The length.</returns>
    public long GetValueLength(in LtpValueRef value) => Heap.GetValueLength(value);

    /// <summary>Copies cell value bytes starting at <paramref name="offset"/>.</summary>
    /// <param name="value">The cell reference.</param>
    /// <param name="offset">Position in the value.</param>
    /// <param name="destination">Receives the bytes.</param>
    /// <returns>The bytes copied.</returns>
    public int ReadValue(in LtpValueRef value, long offset, Span<byte> destination) => Heap.ReadValue(value, offset, destination);

    /// <summary>Reads a whole cell value into a new array.</summary>
    /// <param name="value">The cell reference.</param>
    /// <returns>The bytes, or null if unreadable or longer than <see cref="LtpOptions.MaxValueBytes"/>.</returns>
    public byte[]? ReadValue(in LtpValueRef value) => Heap.ReadValue(value);

    /// <summary>Disposes the row matrix and, when the context opened it, the heap.</summary>
    public void Dispose()
    {
        _rowsTree?.Dispose();
        if (_ownsHeap)
        {
            Heap.Dispose();
        }
    }
}

/// <summary>A row of a <see cref="TableContext"/> over a caller's buffer. Cheap to create; holds no state of its own.</summary>
public readonly ref struct TableRow
{
    private readonly TableContext _table;
    private readonly ReadOnlySpan<byte> _bytes;

    internal TableRow(TableContext table, ReadOnlySpan<byte> bytes)
    {
        _table = table;
        _bytes = bytes;
    }

    /// <summary>The row's bytes.</summary>
    public ReadOnlySpan<byte> Bytes => _bytes;

    /// <summary>The row ID: the first four bytes of the row (PidTagLtpRowId).</summary>
    public uint RowId => _bytes.Length >= 4 ? LtpBytes.U32(_bytes, 0) : 0;

    /// <summary>Whether the cell-existence bitmap says the column has a value in this row.</summary>
    /// <param name="column">The position in <see cref="TableContext.Columns"/>.</param>
    /// <returns>True when the cell exists.</returns>
    public bool HasCell(int column) => (uint)column < (uint)_table.Columns.Count && _table.IsPresent(_bytes, _table.Columns[column]);

    /// <summary>Gets the reference to a cell's value.</summary>
    /// <param name="column">The position in <see cref="TableContext.Columns"/>.</param>
    /// <param name="value">The reference; nothing has been read.</param>
    /// <returns>False when the column does not exist, the cell is absent in this row, or it lies outside the row.</returns>
    public bool TryGetCell(int column, out LtpValueRef value) => _table.TryGetCell(_bytes, column, out value);
}
