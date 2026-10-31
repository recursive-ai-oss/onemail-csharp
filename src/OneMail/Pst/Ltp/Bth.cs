using System;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Pst.Ltp;

/// <summary>
/// A BTH (MS-PST 2.3.2): a B-tree of fixed-size keys and fixed-size entries whose nodes are allocations of a heap-on-node.
/// Keys are unsigned little-endian integers of 2, 4, 8 or 16 bytes, in increasing order.
/// </summary>
/// <remarks>Reading never throws for malformed content; problems are <see cref="LtpDefect"/>s on the heap. Not thread-safe.</remarks>
public sealed class Bth
{
    private const byte BthSignature = 0xB5;   // MS-PST 2.3.2.1 bType
    private const int HeaderSize = 8;
    private const int IndexPointerSize = 4;   // an index record is a key and the HID of the next level

    internal Bth(HeapNode heap, Hid header, int keySize, int dataSize, int indexLevels, Hid root)
    {
        Heap = heap;
        Header = header;
        KeySize = keySize;
        DataSize = dataSize;
        IndexLevels = indexLevels;
        Root = root;
    }

    /// <summary>The heap the tree lives in.</summary>
    public HeapNode Heap { get; }

    /// <summary>The HID of the BTHHEADER.</summary>
    public Hid Header { get; }

    /// <summary>cbKey: 2, 4, 8 or 16.</summary>
    public int KeySize { get; }

    /// <summary>cbEnt: the size of a leaf entry, 1 to 32.</summary>
    public int DataSize { get; }

    /// <summary>bIdxLevels: the number of index levels above the leaves; zero when the root is a leaf.</summary>
    public int IndexLevels { get; }

    /// <summary>hidRoot: the root node. Null for an empty tree.</summary>
    public Hid Root { get; }

    /// <summary>Reads the BTHHEADER at <paramref name="header"/>.</summary>
    /// <param name="heap">The heap.</param>
    /// <param name="header">The HID of the BTHHEADER (the heap's <see cref="HeapNode.UserRoot"/> for a bare BTH, or a HID stored in a structure).</param>
    /// <param name="bth">The tree, or null when the header is unusable.</param>
    /// <returns>False, with a defect on the heap, when the allocation is missing, too short, not a BTHHEADER, or describes sizes the format does not allow.</returns>
    public static bool TryOpen(HeapNode heap, Hid header, out Bth? bth)
    {
        ArgumentNullException.ThrowIfNull(heap);
        bth = null;
        if (!heap.TryGetAllocation(header, out HeapAllocation a))
        {
            return false;
        }

        ReadOnlySpan<byte> h = heap.GetBytes(a);
        if (h.Length < HeaderSize || h[0] != BthSignature)
        {
            heap.Report(LtpDefectKind.BthBadHeader, $"BTHHEADER {header}: {h.Length} bytes, bType 0x{(h.Length > 0 ? h[0] : 0):x2}");
            return false;
        }

        int cbKey = h[1], cbEnt = h[2], levels = h[3];
        if (cbKey is not (2 or 4 or 8 or 16) || cbEnt is < 1 or > 32)
        {
            heap.Report(LtpDefectKind.BthBadHeader, $"BTHHEADER {header}: cbKey {cbKey}, cbEnt {cbEnt} are not sizes the format allows");
            return false;
        }

        if (levels > heap.Options.MaxBthLevels)
        {
            heap.Report(LtpDefectKind.BthTooDeep, $"BTHHEADER {header}: {levels} index levels; the limit is {heap.Options.MaxBthLevels}");
            return false;
        }

        bth = new Bth(heap, header, cbKey, cbEnt, levels, new Hid(LtpBytes.U32(h, 4)));
        return true;
    }

    private int RecordSize(int level) => KeySize + (level == 0 ? DataSize : IndexPointerSize);

    // Resolves a node allocation and reports one that is not a whole number of records.
    private bool TryGetNode(Hid hid, int level, out HeapAllocation allocation, out int count)
    {
        count = 0;
        if (!Heap.TryGetAllocation(hid, out allocation))
        {
            return false;
        }

        int rec = RecordSize(level);
        count = allocation.Length / rec;
        if (allocation.Length % rec != 0)
        {
            Heap.Report(LtpDefectKind.BthNodeSize, $"BTH node {hid} is {allocation.Length} bytes, not a whole number of {rec}-byte records");
        }

        return true;
    }

    /// <summary>Finds the entry for a key.</summary>
    /// <param name="key">The key, exactly <see cref="KeySize"/> bytes, little-endian.</param>
    /// <param name="data">Receives the entry's <see cref="DataSize"/> bytes; must be at least that long.</param>
    /// <returns>True when the key is present.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> or <paramref name="data"/> has the wrong length.</exception>
    public bool TryFind(ReadOnlySpan<byte> key, Span<byte> data)
    {
        if (key.Length != KeySize || data.Length < DataSize)
        {
            throw new ArgumentException($"the key must be {KeySize} bytes and the buffer at least {DataSize}");
        }

        if (Root.IsNull)
        {
            return false;
        }

        Hid current = Root;
        for (int level = IndexLevels; ; level--)
        {
            if (!TryGetNode(current, level, out HeapAllocation a, out int count))
            {
                return false;
            }

            ReadOnlySpan<byte> node = Heap.GetBytes(a);
            int rec = RecordSize(level);
            if (level > 0)
            {
                // MS-PST 2.3.2.3: an index record's key is the smallest key in its subtree, so take the last record whose key is not above the target.
                int lo = 0, hi = count - 1, found = -1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) >>> 1;
                    if (LtpBytes.CompareKeys(node.Slice(mid * rec, KeySize), key) <= 0)
                    {
                        found = mid;
                        lo = mid + 1;
                    }
                    else
                    {
                        hi = mid - 1;
                    }
                }

                if (found < 0)
                {
                    return false;
                }

                current = new Hid(LtpBytes.U32(node, (found * rec) + KeySize));
                continue;
            }

            int l = 0, h = count - 1;
            while (l <= h)
            {
                int mid = (l + h) >>> 1;
                int c = LtpBytes.CompareKeys(node.Slice(mid * rec, KeySize), key);
                if (c == 0)
                {
                    node.Slice((mid * rec) + KeySize, DataSize).CopyTo(data);
                    return true;
                }

                if (c < 0)
                {
                    l = mid + 1;
                }
                else
                {
                    h = mid - 1;
                }
            }

            return false;
        }
    }

    /// <summary>Creates a cursor over every leaf record in key order.</summary>
    /// <returns>The cursor, positioned before the first record.</returns>
    public BthCursor CreateCursor() => new(this);

    internal bool TryGetNodeForCursor(Hid hid, int level, out HeapAllocation allocation, out int count) => TryGetNode(hid, level, out allocation, out count);

    internal int RecordSizeFor(int level) => RecordSize(level);
}

/// <summary>
/// Walks the leaf records of a <see cref="Bth"/> in key order. Keys that do not increase are skipped with a defect, and the
/// walk is cut off if it visits more nodes than the heap can hold, so a damaged tree cannot loop or multiply the work.
/// </summary>
public sealed class BthCursor
{
    private struct Frame
    {
        public HeapAllocation Node;
        public int Count;
        public int Next;
    }

    private readonly Bth _bth;
    private readonly Frame[] _stack;
    private int _depth;           // frames in use; the top frame is _stack[_depth - 1] at level (IndexLevels - _depth + 1)
    private bool _started;
    private long _visits;
    private readonly long _visitLimit;
    private bool _hasLast;
    private UInt128 _last;
    private HeapAllocation _record;   // the allocation holding the current record
    private int _recordOffset;

    internal BthCursor(Bth bth)
    {
        _bth = bth;
        _stack = new Frame[bth.IndexLevels + 1];
        // Every node is its own allocation inside the heap, so a well-formed tree cannot have more nodes than the heap has bytes.
        _visitLimit = (bth.Heap.Length / 2) + 16;
    }

    /// <summary>The key of the current record, little-endian. Valid until the next call on the heap.</summary>
    public ReadOnlySpan<byte> Key => _bth.Heap.GetBytes(_record).Slice(_recordOffset, _bth.KeySize);

    /// <summary>The entry of the current record. Valid until the next call on the heap.</summary>
    public ReadOnlySpan<byte> Data => _bth.Heap.GetBytes(_record).Slice(_recordOffset + _bth.KeySize, _bth.DataSize);

    /// <summary>The low 64 bits of the current key as an unsigned integer.</summary>
    public ulong KeyValue => LtpBytes.ReadUInt64Partial(Key);

    /// <summary>Moves to the next record.</summary>
    /// <returns>False at the end of the tree.</returns>
    public bool MoveNext()
    {
        if (!_started)
        {
            _started = true;
            if (_bth.Root.IsNull || !Push(_bth.Root, _bth.IndexLevels))
            {
                return false;
            }
        }

        while (_depth > 0)
        {
            ref Frame top = ref _stack[_depth - 1];
            int level = _bth.IndexLevels - (_depth - 1);
            if (top.Next >= top.Count)
            {
                _depth--;
                continue;
            }

            int index = top.Next++;
            int rec = _bth.RecordSizeFor(level);
            if (level == 0)
            {
                _record = top.Node;
                _recordOffset = index * rec;
                UInt128 key = LtpBytes.KeyValue(Key);
                if (_hasLast && key <= _last)
                {
                    _bth.Heap.Report(LtpDefectKind.BthKeyOrder, $"BTH key 0x{key:x} does not follow 0x{_last:x}; the record is skipped");
                    continue;
                }

                _last = key;
                _hasLast = true;
                return true;
            }

            ReadOnlySpan<byte> node = _bth.Heap.GetBytes(top.Node);
            var child = new Hid(LtpBytes.U32(node, (index * rec) + _bth.KeySize));
            Push(child, level - 1);
        }

        return false;
    }

    private bool Push(Hid hid, int level)
    {
        if (++_visits > _visitLimit)
        {
            if (_visits == _visitLimit + 1)
            {
                _bth.Heap.Report(LtpDefectKind.BthTooManyNodes, $"BTH walk visited more than {_visitLimit} nodes; the rest is ignored");
            }

            return false;
        }

        if (!_bth.TryGetNodeForCursor(hid, level, out HeapAllocation a, out int count))
        {
            return false;
        }

        _stack[_depth++] = new Frame { Node = a, Count = count, Next = 0 };
        return true;
    }
}
