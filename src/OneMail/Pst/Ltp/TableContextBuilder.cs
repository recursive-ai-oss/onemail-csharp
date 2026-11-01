using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Pst.Ltp;

/// <summary>
/// Builds a table context (MS-PST 2.3.4): the TCINFO with its column descriptors, a row-index BTH, and the row matrix, in the heap when
/// it fits one allocation and in a subnode when it does not. <see cref="TableContext"/> reads the result.
/// </summary>
/// <remarks>
/// <para><b>Columns.</b> They are sorted by tag, as the format requires. PidTagLtpRowId (0x67F2, Integer32) and PidTagLtpRowVer (0x67F3, Integer32)
/// are always present: they are added when the caller leaves them out, at offsets 0 and 4 with cell-existence bits 0 and 1 (2.3.4.4.1). The
/// row's ID fills the first; an added PidTagLtpRowVer is zero in every row. Cells of 8 and 4 bytes come first (with HNIDs, which are 4 bytes),
/// then 2-byte cells, then 1-byte cells, then the cell-existence bitmap. The format allows at most 255 columns and rows that fit a block.</para>
/// <para><b>Rows</b> are written in the order added, one at a time: <see cref="BeginRow"/>, <see cref="SetCell(int, ReadOnlySpan{byte})"/> for each
/// cell that exists, <see cref="EndRow"/>. A cell never set is absent (its bitmap bit is clear), which is different from an empty value.
/// Rows are packed into row-matrix blocks of whole rows as they fill and handed to the block builder at once, so a table of any size
/// needs one block of rows in memory (at most 8,176 bytes). What the heap holds is proportional to the table: the row index (8 bytes per row
/// plus its tree) and the variable-size cell values that fit an allocation. Larger values are written to subnodes as they are set.</para>
/// <para>Where the matrix goes (2.3.4.4): up to 3,580 bytes it is one heap allocation; more, and it is the data of a subnode whose blocks each
/// hold ⌊8,176 / row size⌋ whole rows; every block but the last is a full 8,176 bytes of data (the rows, then zeros), and the last is as long as its rows.</para>
/// <para>Not thread-safe; one builder builds one table.</para>
/// </remarks>
public sealed class TableContextBuilder
{
    private const byte TcSignature = 0x7C;      // bTypeTC
    private const ushort RowIdColumn = 0x67F2;  // PidTagLtpRowId
    private const ushort RowVerColumn = 0x67F3; // PidTagLtpRowVer
    private const int TcInfoHeaderSize = 22;
    private const int ColumnDescriptorSize = 8;

    private readonly NdbBlockBuilder _blocks;
    private readonly LtpValuePlacer _values;
    private readonly HeapNodeBuilder _heap = new(TcSignature);
    private readonly Hid _info;
    private readonly BthTreeBuilder _rowIndex;
    private readonly TcColumn[] _columns;
    private readonly int[] _ends;      // TCI_4b, TCI_2b, TCI_1b, TCI_bm
    private readonly int _rowsPerBlock;
    private readonly byte[] _row;
    private readonly byte[] _indexScratch = new byte[4];
    private readonly byte[] _matrix;   // the block of rows being filled
    private readonly HashSet<uint> _rowIds = [];
    private readonly bool _rowVerAdded;
    private NdbBlockBuilder.DataTreeWriter? _tree;
    private int _rowsInBlock;
    private long _rowCount;
    private bool _inRow;
    private bool _built;

    /// <summary>Creates a builder and allocates the table's row-index header and TCINFO, so they are the heap's first two allocations.</summary>
    /// <param name="blocks">Where the heap, the row matrix and any value subnodes are written.</param>
    /// <param name="columns">The columns by property ID and type. PidTagLtpRowId and PidTagLtpRowVer are added if missing.</param>
    /// <param name="nextSubnodeNid">Supplies the NIDs of subnodes (large values, the row matrix); null for NID_TYPE_LTP NIDs counting up from index 1.</param>
    /// <exception cref="ArgumentException">A property ID repeats, there are more than 255 columns, a column the format reserves has another type, or a row would be larger than a block.</exception>
    public TableContextBuilder(NdbBlockBuilder blocks, IEnumerable<(ushort Id, ushort Type)> columns, Func<Nid>? nextSubnodeNid = null)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(columns);
        _blocks = blocks;
        _values = new LtpValuePlacer(blocks, nextSubnodeNid);

        var list = new List<(ushort Id, ushort Type)>(columns);
        // A column is a property tag, so one ID may appear with two types (a String8 and a String column, say) but never twice with the same one.
        var seenTags = new HashSet<uint>();
        var seen = new HashSet<ushort>();
        foreach ((ushort id, ushort type) in list)
        {
            if (!seenTags.Add(((uint)id << 16) | type))
            {
                throw new ArgumentException($"column 0x{id:x4}/0x{type:x4} is listed twice", nameof(columns));
            }

            seen.Add(id);
        }

        foreach ((ushort id, ushort type) in list)
        {
            if (id is RowIdColumn or RowVerColumn && type != LtpPropertyTypes.PtypInteger32)
            {
                throw new ArgumentException($"column 0x{id:x4} must be Integer32", nameof(columns));
            }
        }

        if (!seen.Contains(RowIdColumn))
        {
            list.Add((RowIdColumn, LtpPropertyTypes.PtypInteger32));
        }

        if (!seen.Contains(RowVerColumn))
        {
            list.Add((RowVerColumn, LtpPropertyTypes.PtypInteger32));
            _rowVerAdded = true;
        }

        if (list.Count > 255)
        {
            throw new ArgumentException($"a table has at most 255 columns, not {list.Count}", nameof(columns));
        }

        list.Sort(static (a, b) => ((a.Id << 16) | a.Type).CompareTo((b.Id << 16) | b.Type));
        _columns = Layout(list, out _ends);
        RowSize = _ends[3];
        if (RowSize > NdbBlockBuilder.MaxBlockData)
        {
            throw new ArgumentException($"a row of {RowSize} bytes does not fit a block of {NdbBlockBuilder.MaxBlockData}", nameof(columns));
        }

        _rowsPerBlock = NdbBlockBuilder.MaxBlockData / RowSize;
        _row = new byte[RowSize];
        _matrix = new byte[NdbBlockBuilder.MaxBlockData];   // the rows, then dead space that stays zero

        // Real files allocate the row index header first and the TCINFO second (HIDs 0x20 and 0x40); the format does not care.
        _rowIndex = new BthTreeBuilder(_heap, 4, 4);
        _info = _heap.Allocate(TcInfoHeaderSize + (_columns.Length * ColumnDescriptorSize));
        _heap.UserRoot = _info;
    }

    /// <summary>The columns as laid out, in tag order: the offset, size and bitmap position of each cell.</summary>
    public IReadOnlyList<TcColumn> Columns => _columns;

    /// <summary>TCI_bm: the size of one row in bytes.</summary>
    public int RowSize { get; }

    /// <summary>How many rows fit one row-matrix block.</summary>
    public int RowsPerBlock => _rowsPerBlock;

    /// <summary>The rows finished so far.</summary>
    public long RowCount => _rowCount;

    /// <summary>The position of a column in <see cref="Columns"/>, which is what <c>SetCell</c> takes.</summary>
    /// <param name="id">The property ID. When two columns share it, the one with the lower type code.</param>
    /// <returns>The index, or -1.</returns>
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

    /// <summary>The position of the column with this exact tag in <see cref="Columns"/>.</summary>
    /// <param name="id">The property ID.</param>
    /// <param name="type">The property type code.</param>
    /// <returns>The index, or -1.</returns>
    public int FindColumn(ushort id, ushort type)
    {
        for (int i = 0; i < _columns.Length; i++)
        {
            if (_columns[i].Id == id && _columns[i].Type == type)
            {
                return i;
            }
        }

        return -1;
    }

    // MS-PST 2.3.4.4.1: PidTagLtpRowId at offset 0 with bit 0, PidTagLtpRowVer at offset 4 with bit 1, then the 8- and 4-byte cells,
    // the 2-byte cells and the 1-byte cells, each group in tag order, then the cell-existence bitmap.
    private static TcColumn[] Layout(List<(ushort Id, ushort Type)> sorted, out int[] ends)
    {
        var result = new TcColumn[sorted.Count];
        int[] size = new int[sorted.Count];
        for (int i = 0; i < sorted.Count; i++)
        {
            int fixedSize = LtpPropertyTypes.FixedSize(sorted[i].Type);
            // 1, 2, 4 and 8-byte values are in the row; anything else (strings, binary, GUIDs, multi-valued) is a 4-byte HNID.
            size[i] = fixedSize is 1 or 2 or 4 or 8 ? fixedSize : 4;
        }

        int[] offset = new int[sorted.Count];
        for (int i = 0; i < sorted.Count; i++)
        {
            if (sorted[i].Id == RowIdColumn)
            {
                offset[i] = 0;
            }
            else if (sorted[i].Id == RowVerColumn)
            {
                offset[i] = 4;
            }
        }

        int at = 8;
        foreach (int group in new[] { 8, 4 })
        {
            for (int i = 0; i < sorted.Count; i++)
            {
                if (sorted[i].Id is RowIdColumn or RowVerColumn || size[i] != group)
                {
                    continue;
                }

                offset[i] = at;
                at += group;
            }
        }

        int end4 = at;
        for (int i = 0; i < sorted.Count; i++)
        {
            if (size[i] == 2)
            {
                offset[i] = at;
                at += 2;
            }
        }

        int end2 = at;
        for (int i = 0; i < sorted.Count; i++)
        {
            if (size[i] == 1)
            {
                offset[i] = at;
                at++;
            }
        }

        int end1 = at;
        ends = [end4, end2, end1, end1 + ((sorted.Count + 7) / 8)];

        // The format fixes only two bit positions: PidTagLtpRowId is bit 0 and PidTagLtpRowVer bit 1 (MS-PST 2.3.4.4.1). The rest are numbered
        // in the order the cells sit in the row, so they follow from the layout.
        int[] byOffset = [.. Enumerable.Range(0, sorted.Count).OrderBy(i => offset[i])];
        for (int rank = 0; rank < byOffset.Length; rank++)
        {
            int i = byOffset[rank];
            result[i] = new TcColumn(sorted[i].Id, sorted[i].Type, offset[i], size[i], rank);
        }

        return result;
    }

    /// <summary>Starts a row.</summary>
    /// <param name="rowId">dwRowID: unique in the table (a contents table uses the item's NID).</param>
    /// <exception cref="InvalidOperationException">A row is open, or the table is built.</exception>
    /// <exception cref="ArgumentException">The row ID is already used.</exception>
    public void BeginRow(uint rowId)
    {
        if (_built || _inRow)
        {
            throw new InvalidOperationException(_built ? "the table is built" : "the previous row is not ended");
        }

        if (!_rowIds.Add(rowId))
        {
            throw new ArgumentException($"row ID 0x{rowId:x} is already in the table", nameof(rowId));
        }

        Array.Clear(_row);
        _inRow = true;
        SetInline(FindColumn(RowIdColumn), BitConverterBytes(rowId));
        if (_rowVerAdded)
        {
            SetInline(FindColumn(RowVerColumn), BitConverterBytes(0));
        }
    }

    private static byte[] BitConverterBytes(uint v)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return b;
    }

    private void SetBit(in TcColumn c) => _row[_ends[2] + (c.BitIndex >> 3)] |= (byte)(0x80 >> (c.BitIndex & 7));

    private void SetInline(int column, ReadOnlySpan<byte> value)
    {
        TcColumn c = _columns[column];
        value.CopyTo(_row.AsSpan(c.Offset));
        SetBit(c);
    }

    private void SetHnid(int column, uint hnid)
    {
        TcColumn c = _columns[column];
        BinaryPrimitives.WriteUInt32LittleEndian(_row.AsSpan(c.Offset), hnid);
        SetBit(c);
    }

    private TcColumn OpenCell(int column)
    {
        if (!_inRow)
        {
            throw new InvalidOperationException("no row is open");
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)column, (uint)_columns.Length);
        return _columns[column];
    }

    private static bool IsInline(in TcColumn c) => LtpPropertyTypes.FixedSize(c.Type) is 1 or 2 or 4 or 8;

    /// <summary>Sets a cell of the open row.</summary>
    /// <param name="column">The column's position in <see cref="Columns"/>.</param>
    /// <param name="value">The value bytes: exactly the type's size for fixed-size types of up to eight bytes, any length otherwise (an empty value is stored as a zero HNID).</param>
    /// <exception cref="ArgumentException">A fixed-size value has the wrong length, or the cell was already set in this row.</exception>
    /// <exception cref="InvalidOperationException">No row is open.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The column index is out of range.</exception>
    public void SetCell(int column, ReadOnlySpan<byte> value)
    {
        TcColumn c = OpenCell(column);
        CheckNotSet(c);
        if (IsInline(c))
        {
            if (value.Length != c.Size)
            {
                throw new ArgumentException($"column 0x{c.Tag:x8} is {c.Size} bytes, not {value.Length}", nameof(value));
            }

            SetInline(column, value);
            return;
        }

        int fixedSize = LtpPropertyTypes.FixedSize(c.Type);
        if (fixedSize > 0 && value.Length != fixedSize)
        {
            throw new ArgumentException($"column 0x{c.Tag:x8} is {fixedSize} bytes, not {value.Length}", nameof(value));
        }

        SetHnid(column, _values.Place(_heap, value));
    }

    /// <summary>Sets a cell from a stream. Values above the heap limit are copied block by block into a subnode.</summary>
    /// <param name="column">The column's position in <see cref="Columns"/>.</param>
    /// <param name="source">A readable stream positioned at the value's first byte.</param>
    /// <param name="length">The value's length, declared so limits are checked before reading.</param>
    /// <exception cref="ArgumentException">A fixed-size type has another length, or the cell was already set.</exception>
    /// <exception cref="EndOfStreamException">The stream ends early.</exception>
    public void SetCell(int column, Stream source, long length)
    {
        ArgumentNullException.ThrowIfNull(source);
        TcColumn c = OpenCell(column);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, NdbBlockBuilder.MaxDataTreeBytes);
        int fixedSize = LtpPropertyTypes.FixedSize(c.Type);
        if (fixedSize > 0 && length != fixedSize)
        {
            // Checked before anything is allocated or read.
            throw new ArgumentException($"column 0x{c.Tag:x8} is {fixedSize} bytes, not {length}", nameof(length));
        }

        if (length <= HeapNodeBuilder.MaxAllocation)
        {
            byte[] small = new byte[length];
            source.ReadExactly(small);
            SetCell(column, small);
            return;
        }

        CheckNotSet(c);
        SetHnid(column, _values.PlaceStream(source, length));
    }

    /// <summary>Sets a cell whose HNID is the NID of a subnode the caller writes itself (an embedded message, say).</summary>
    /// <param name="column">The column's position in <see cref="Columns"/>.</param>
    /// <param name="subnode">The subnode's NID; type bits must not be zero.</param>
    /// <exception cref="ArgumentException">The column holds its value in the row, the NID's type is zero, or the cell was already set.</exception>
    public void SetCellNodeReference(int column, Nid subnode)
    {
        TcColumn c = OpenCell(column);
        CheckNotSet(c);
        if (IsInline(c) || (subnode.Value & 0x1F) == 0)
        {
            throw new ArgumentException("a node reference needs an HNID column and a NID with a non-zero type", nameof(subnode));
        }

        SetHnid(column, subnode.Value);
    }

    private void CheckNotSet(in TcColumn c)
    {
        if ((_row[_ends[2] + (c.BitIndex >> 3)] & (0x80 >> (c.BitIndex & 7))) != 0)
        {
            throw new ArgumentException($"column 0x{c.Tag:x8} is already set in this row");
        }
    }

    /// <summary>Ends the open row and adds it to the matrix and the row index.</summary>
    /// <exception cref="InvalidOperationException">No row is open, or the table has more rows than a data tree can hold.</exception>
    public void EndRow()
    {
        if (!_inRow)
        {
            throw new InvalidOperationException("no row is open");
        }

        _inRow = false;
        // The row ID is the first cell of the row (offset 0).
        uint rowId = BinaryPrimitives.ReadUInt32LittleEndian(_row);
        BinaryPrimitives.WriteUInt32LittleEndian(_indexScratch, checked((uint)_rowCount));
        _rowIndex.Add(rowId, _indexScratch);

        _row.CopyTo(_matrix.AsSpan(_rowsInBlock * RowSize));
        _rowsInBlock++;
        _rowCount++;
        if (_rowsInBlock == _rowsPerBlock)
        {
            // MS-PST 2.3.4.4: every block but the last holds the same number of whole rows and is a full 8,192 bytes, so it is written as
            // 8,176 bytes of data with the zero "dead space" after the last row that readers must ignore. A full block is always more than
            // 3,580 bytes, so the matrix is a subnode from here on.
            _tree ??= _blocks.BeginDataTree();
            _tree.AddBlock(_matrix);
            _rowsInBlock = 0;
        }
    }

    /// <summary>Adds a whole row.</summary>
    /// <param name="rowId">dwRowID.</param>
    /// <param name="cells">One entry per column in <see cref="Columns"/> order; null leaves the cell absent. The row-ID cell and an added row-version cell are filled in; entries for them are ignored.</param>
    /// <exception cref="ArgumentException">The number of cells differs from the number of columns, or a value has the wrong length.</exception>
    public void AddRow(uint rowId, ReadOnlySpan<byte[]?> cells)
    {
        if (cells.Length != _columns.Length)
        {
            throw new ArgumentException($"the table has {_columns.Length} columns, {cells.Length} cells given", nameof(cells));
        }

        BeginRow(rowId);
        for (int i = 0; i < cells.Length; i++)
        {
            if (cells[i] is { } v && !(_columns[i].Id == RowIdColumn || (_columns[i].Id == RowVerColumn && _rowVerAdded)))
            {
                SetCell(i, v);
            }
        }

        EndRow();
    }

    /// <summary>Writes the heap and the row matrix and returns where they are.</summary>
    /// <returns>The heap's BID and the subnodes (large values, the row matrix).</returns>
    /// <exception cref="InvalidOperationException">A row is open, or the table is already built.</exception>
    public LtpNodeResult Build()
    {
        if (_built || _inRow)
        {
            throw new InvalidOperationException(_built ? "the table is already built" : "a row is still open");
        }

        _built = true;
        _rowIndex.Build();

        uint hnidRows = 0;
        if (_tree is not null)
        {
            if (_rowsInBlock > 0)
            {
                _tree.AddBlock(_matrix.AsSpan(0, _rowsInBlock * RowSize));
            }

            DataTreeResult r = _tree.Complete();
            Nid nid = _values.NextNid();
            _values.Subnodes.Add(new SubnodeEntry(nid, r.Root, default));
            hnidRows = nid.Value;
        }
        else if (_rowsInBlock > 0)
        {
            int bytes = _rowsInBlock * RowSize;
            hnidRows = bytes <= HeapNodeBuilder.MaxAllocation
                ? _heap.Allocate(_matrix.AsSpan(0, bytes)).Value
                : _values.PlaceLarge(_matrix.AsSpan(0, bytes));
        }

        // TCINFO (MS-PST 2.3.4.1): bType, cCols, rgib[4], hidRowIndex, hnidRows, hidIndex (deprecated, zero), then the column descriptors.
        Span<byte> info = _heap.GetSpan(_info);
        info[0] = TcSignature;
        info[1] = (byte)_columns.Length;
        for (int i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(info[(2 + (2 * i))..], (ushort)_ends[i]);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(info[10..], _rowIndex.Header.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(info[14..], hnidRows);
        BinaryPrimitives.WriteUInt32LittleEndian(info[18..], 0);
        for (int i = 0; i < _columns.Length; i++)
        {
            Span<byte> d = info[(TcInfoHeaderSize + (i * ColumnDescriptorSize))..];
            BinaryPrimitives.WriteUInt32LittleEndian(d, _columns[i].Tag);
            BinaryPrimitives.WriteUInt16LittleEndian(d[4..], (ushort)_columns[i].Offset);
            d[6] = (byte)_columns[i].Size;
            d[7] = (byte)_columns[i].BitIndex;
        }

        return LtpNodeResult.WriteHeap(_blocks, _heap, _values.Subnodes);
    }
}
