using System;
using System.Buffers;
using System.Collections.Generic;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Pst.Ltp;

/// <summary>
/// One allocation of a heap-on-node: where its bytes sit. Valid for the <see cref="HeapNode"/> that produced it.
/// </summary>
/// <param name="BlockIndex">The heap block (data block of the node, in stream order).</param>
/// <param name="Offset">Offset of the first byte in the block's data.</param>
/// <param name="Length">The allocation's size in bytes. May be zero.</param>
public readonly record struct HeapAllocation(int BlockIndex, int Offset, int Length);

/// <summary>
/// A heap-on-node (MS-PST 2.3.1): the data of a node read as blocks that each hold numbered allocations, addressed by HID.
/// It is the storage under every BTH, property context and table context.
/// </summary>
/// <remarks>
/// <para>Reading never throws for malformed content: a bad size, HID or NID is an <see cref="LtpDefect"/> in
/// <see cref="Defects"/> and the lookup fails. Problems in the blocks themselves (checksums, missing blocks) are
/// <see cref="NdbDefect"/>s on the <see cref="NdbFile"/>.</para>
/// <para>The heap keeps one decoded block; spans returned by <see cref="GetBytes"/> point into it and stay valid only until
/// the next call on this heap. Not thread-safe.</para>
/// </remarks>
public sealed class HeapNode : IDisposable
{
    private const byte HeapSignature = 0xEC;   // MS-PST 2.3.1.2 bSig
    private const int HnhdrSize = 12;          // ibHnpm, bSig, bClientSig, hidUserRoot, rgbFillLevel
    private const int BitmapHeaderSize = 66;   // HNBITMAPHDR: ibHnpm and 64 bytes of fill levels
    private const int PageHeaderSize = 2;      // HNPAGEHDR: ibHnpm

    private readonly NdbFile _file;
    private readonly NdbDataTree _data;
    private readonly Bid _sub;
    private SubnodeTree? _subnodes;
    private bool _subnodesTried;
    private List<LtpDefect>? _defects;
    private int _dropped;

    private byte[]? _buf;
    private int _cachedBlock = -1;
    private int _cachedLength;
    private bool _pageMapValid;
    private int _pageMapOffset;
    private int _allocCount;
    private int _headerSize;

    private NdbDataTree? _valueTree;
    private Nid _valueNid;

    private HeapNode(NdbFile file, NdbDataTree data, Bid sub, LtpOptions options)
    {
        _file = file;
        _data = data;
        _sub = sub;
        Options = options;
    }

    /// <summary>The node database the heap was read from.</summary>
    public NdbFile File => _file;

    /// <summary>The limits in force.</summary>
    public LtpOptions Options { get; }

    /// <summary>bClientSig: 0x7C for a table context, 0xB5 for a BTH, 0xBC for a property context.</summary>
    public byte ClientSignature { get; private set; }

    /// <summary>hidUserRoot: the allocation the client structure starts at (the BTHHEADER for a PC, the TCINFO for a TC).</summary>
    public Hid UserRoot { get; private set; }

    /// <summary>The number of heap blocks (data blocks of the node).</summary>
    public int BlockCount => _data.LeafCount;

    /// <summary>The total length of the node's data in bytes.</summary>
    public long Length => _data.Length;

    /// <summary>What was found wrong in this heap so far, in order. Capped by <see cref="LtpOptions.MaxDefects"/>.</summary>
    public IReadOnlyList<LtpDefect> Defects => _defects ?? (IReadOnlyList<LtpDefect>)[];

    /// <summary>Defects found after the cap was reached; counted but not kept.</summary>
    public int DroppedDefects => _dropped;

    internal void Report(LtpDefectKind kind, string detail)
    {
        long offset = 0;
        if (_cachedBlock >= 0 && _cachedBlock < _data.LeafCount)
        {
            offset = (long)_data.GetLeaf(_cachedBlock).Block.Ib;
        }

        ReportAt(kind, offset, detail);
    }

    internal void ReportAt(LtpDefectKind kind, long offset, string detail)
    {
        _defects ??= [];
        if (_defects.Count >= Options.MaxDefects)
        {
            _dropped++;
            return;
        }

        _defects.Add(new LtpDefect(kind, offset, detail));
    }

    // ---- opening ----

    /// <summary>Opens the heap of a node, looked up in the node B-tree.</summary>
    /// <param name="file">The open file.</param>
    /// <param name="node">The node's NID.</param>
    /// <param name="heap">The heap, or null when it cannot be used.</param>
    /// <param name="defects">Why it cannot be used (empty on success; see <see cref="Defects"/> for later findings).</param>
    /// <param name="options">Limits; null for defaults.</param>
    /// <returns>True when the first block is a heap and later reads can proceed.</returns>
    public static bool TryOpen(NdbFile file, Nid node, out HeapNode? heap, out IReadOnlyList<LtpDefect> defects, LtpOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!file.Nodes.TryGet(node, out NodeEntry entry))
        {
            heap = null;
            defects = [new LtpDefect(LtpDefectKind.NodeNotFound, 0, $"node {node} is not in the node B-tree")];
            return false;
        }

        return TryOpen(file, entry.Data, entry.Sub, out heap, out defects, options);
    }

    /// <summary>Opens the heap of a subnode.</summary>
    /// <param name="file">The open file.</param>
    /// <param name="subnode">The subnode entry from a <see cref="SubnodeTree"/>.</param>
    /// <param name="heap">The heap, or null when it cannot be used.</param>
    /// <param name="defects">Why it cannot be used.</param>
    /// <param name="options">Limits; null for defaults.</param>
    /// <returns>True when the first block is a heap.</returns>
    public static bool TryOpen(NdbFile file, in SubnodeEntry subnode, out HeapNode? heap, out IReadOnlyList<LtpDefect> defects, LtpOptions? options = null) =>
        TryOpen(file, subnode.Data, subnode.Sub, out heap, out defects, options);

    /// <summary>Opens a heap from the data and subnode BIDs of a node or subnode.</summary>
    /// <param name="file">The open file.</param>
    /// <param name="data">The node's data BID.</param>
    /// <param name="sub">The node's subnode BID; null if it has none.</param>
    /// <param name="heap">The heap, or null when it cannot be used.</param>
    /// <param name="defects">Why it cannot be used.</param>
    /// <param name="options">Limits; null for defaults.</param>
    /// <returns>True when the first block is a heap.</returns>
    public static bool TryOpen(NdbFile file, Bid data, Bid sub, out HeapNode? heap, out IReadOnlyList<LtpDefect> defects, LtpOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        heap = null;
        defects = [];
        NdbDataTree? tree = data.IsNull ? null : file.OpenDataTree(data);
        if (tree is null || tree.LeafCount == 0)
        {
            tree?.Dispose();
            defects = [new LtpDefect(LtpDefectKind.HeapUnreadable, 0, "the node has no readable data blocks")];
            return false;
        }

        var h = new HeapNode(file, tree, sub, options ?? new LtpOptions());
        if (!h.EnsureBlock(0))
        {
            defects = h.Defects.Count > 0 ? [.. h.Defects] : [new LtpDefect(LtpDefectKind.HeapUnreadable, 0, "the first heap block cannot be read")];
            h.Dispose();
            return false;
        }

        h.ClientSignature = h._buf![3];
        h.UserRoot = new Hid(LtpBytes.U32(h._buf, 4));
        heap = h;
        return true;
    }

    // ---- blocks and allocations ----

    // Loads heap block `index` and checks its HNPAGEMAP (MS-PST 2.3.1.5): the offset has to leave room for the map, and the
    // map has to fit inside the block. Returns false for a block that is missing, unreadable or has an unusable map.
    private bool EnsureBlock(int index)
    {
        if (index == _cachedBlock)
        {
            return _pageMapValid;
        }

        if ((uint)index >= (uint)_data.LeafCount)
        {
            Report(LtpDefectKind.HeapBlockMissing, $"heap block {index} does not exist; the heap has {_data.LeafCount}");
            return false;
        }

        _buf ??= ArrayPool<byte>.Shared.Rent(_file.MaxBlockDataSize);
        _cachedBlock = -1;
        if (!_file.TryReadBlock(_data.GetLeaf(index), _buf, out int length, out _))
        {
            return false;
        }

        _cachedBlock = index;
        _cachedLength = length;
        _pageMapValid = false;

        // MS-PST 2.3.1.2: the first block starts with an HNHDR whose bSig is 0xEC. Checking it before the page map keeps a node
        // that is not a heap at all (a search queue, say) from being reported as a heap with a broken page map.
        if (index == 0)
        {
            if (length < HnhdrSize)
            {
                Report(LtpDefectKind.HeapBadHeader, $"the first block holds {length} bytes, less than an HNHDR");
                return false;
            }

            if (_buf[2] != HeapSignature)
            {
                Report(LtpDefectKind.HeapBadSignature, $"bSig is 0x{_buf[2]:x2}, expected 0xEC");
                return false;
            }
        }

        // MS-PST 2.3.1.3 to 2.3.1.5: block 0 starts with an HNHDR, blocks 8, 136, 264... with an HNBITMAPHDR, the rest with an HNPAGEHDR.
        _headerSize = index == 0 ? HnhdrSize : index >= 8 && (index - 8) % 128 == 0 ? BitmapHeaderSize : PageHeaderSize;
        if (length < _headerSize)
        {
            Report(LtpDefectKind.HeapPageMapInvalid, $"heap block {index} holds {length} bytes, less than its header");
            return false;
        }

        int mapAt = LtpBytes.U16(_buf, 0);
        if (mapAt < _headerSize || mapAt + 4 > length)
        {
            Report(LtpDefectKind.HeapPageMapInvalid, $"heap block {index}: ibHnpm {mapAt} is outside the block's {length} bytes");
            return false;
        }

        int cAlloc = LtpBytes.U16(_buf, mapAt);
        if (mapAt + 4 + (2 * (cAlloc + 1)) > length)
        {
            Report(LtpDefectKind.HeapPageMapInvalid, $"heap block {index}: {cAlloc} allocations do not fit the page map");
            return false;
        }

        _pageMapOffset = mapAt;
        _allocCount = cAlloc;
        _pageMapValid = true;
        return true;
    }

    /// <summary>Resolves a HID to the allocation it names.</summary>
    /// <param name="hid">The heap ID.</param>
    /// <param name="allocation">The allocation, when found.</param>
    /// <returns>False, with a defect, for a null or malformed HID, a missing block, or an allocation that lies outside its block.</returns>
    public bool TryGetAllocation(Hid hid, out HeapAllocation allocation)
    {
        allocation = default;
        if (hid.Type != 0 || hid.Index == 0)
        {
            Report(LtpDefectKind.HidInvalid, $"HID {hid} is not a heap ID (type {hid.Type}, index {hid.Index})");
            return false;
        }

        if (!EnsureBlock(hid.BlockIndex))
        {
            return false;
        }

        if (hid.Index > _allocCount)
        {
            Report(LtpDefectKind.HidInvalid, $"HID {hid}: allocation {hid.Index} but block {hid.BlockIndex} has {_allocCount}");
            return false;
        }

        // MS-PST 2.3.1.5: allocation i spans rgibAlloc[i-1] up to rgibAlloc[i].
        int start = LtpBytes.U16(_buf!, _pageMapOffset + 4 + (2 * (hid.Index - 1)));
        int end = LtpBytes.U16(_buf!, _pageMapOffset + 4 + (2 * hid.Index));
        if (start < _headerSize || end < start || end > _pageMapOffset)
        {
            Report(LtpDefectKind.HeapPageMapInvalid, $"HID {hid}: allocation spans {start}..{end}, outside the block's data area {_headerSize}..{_pageMapOffset}");
            return false;
        }

        allocation = new HeapAllocation(hid.BlockIndex, start, end - start);
        return true;
    }

    /// <summary>The bytes of an allocation obtained from <see cref="TryGetAllocation"/>.</summary>
    /// <param name="allocation">The allocation.</param>
    /// <returns>The bytes, valid until the next call on this heap; empty if the block can no longer be read.</returns>
    public ReadOnlySpan<byte> GetBytes(in HeapAllocation allocation) =>
        EnsureBlock(allocation.BlockIndex) && allocation.Offset + allocation.Length <= _cachedLength
            ? _buf.AsSpan(allocation.Offset, allocation.Length)
            : default;

    // ---- subnodes and values ----

    /// <summary>The node's subnode tree, opened on first use. Null when the node has none or the tree cannot be read (a defect says why).</summary>
    public SubnodeTree? Subnodes
    {
        get
        {
            if (!_subnodesTried)
            {
                _subnodesTried = true;
                if (!_sub.IsNull)
                {
                    _subnodes = _file.OpenSubnodes(_sub);
                    if (_subnodes is null)
                    {
                        Report(LtpDefectKind.SubnodesUnreadable, $"subnode tree {_sub} cannot be read");
                    }
                }
            }

            return _subnodes;
        }
    }

    private bool TryGetSubnode(Nid nid, out SubnodeEntry entry)
    {
        SubnodeTree? tree = Subnodes;
        if (tree is not null && tree.TryGet(nid, out entry))
        {
            return true;
        }

        entry = default;
        Report(LtpDefectKind.SubnodeMissing, $"subnode {nid} is not in the node's subnode tree");
        return false;
    }

    /// <summary>Opens a subnode as a heap of its own (a recipient table under a message, say).</summary>
    /// <param name="nid">The subnode's NID.</param>
    /// <param name="heap">The subnode's heap, or null.</param>
    /// <param name="defects">Why it could not be opened.</param>
    /// <returns>True when the subnode exists and starts with a heap.</returns>
    public bool TryOpenSubnode(Nid nid, out HeapNode? heap, out IReadOnlyList<LtpDefect> defects)
    {
        if (!TryGetSubnode(nid, out SubnodeEntry entry))
        {
            heap = null;
            defects = [new LtpDefect(LtpDefectKind.SubnodeMissing, 0, $"subnode {nid} is not in the node's subnode tree")];
            return false;
        }

        return TryOpen(_file, entry.Data, entry.Sub, out heap, out defects, Options);
    }

    /// <summary>Opens the data stream of a subnode. The caller disposes it.</summary>
    /// <param name="nid">The subnode's NID.</param>
    /// <returns>The data tree, or null (with a defect) when the subnode is missing or its data cannot be opened.</returns>
    public NdbDataTree? OpenSubnodeData(Nid nid)
    {
        if (!TryGetSubnode(nid, out SubnodeEntry entry))
        {
            return null;
        }

        NdbDataTree? tree = _file.OpenDataTree(entry.Data);
        if (tree is null)
        {
            Report(LtpDefectKind.ValueUnreadable, $"data of subnode {nid} cannot be opened");
        }

        return tree;
    }

    /// <summary>The length of a value in bytes.</summary>
    /// <param name="value">The reference.</param>
    /// <returns>The length, or -1 (with a defect) when the value cannot be found.</returns>
    public long GetValueLength(in LtpValueRef value)
    {
        switch (value.Kind)
        {
            case LtpValueKind.Empty:
                return 0;
            case LtpValueKind.Inline:
                return value.InlineSize;
            case LtpValueKind.Heap:
                return TryGetAllocation(new Hid(value.Hnid), out HeapAllocation a) ? a.Length : -1;
            default:
                NdbDataTree? t = GetValueTree(new Nid(value.Hnid));
                return t?.Length ?? -1;
        }
    }

    /// <summary>Copies value bytes starting at <paramref name="offset"/>.</summary>
    /// <param name="value">The reference.</param>
    /// <param name="offset">Position in the value.</param>
    /// <param name="destination">Receives the bytes.</param>
    /// <returns>The bytes copied: fewer than asked only at the end of the value or where its data cannot be read (a defect says why).</returns>
    public int ReadValue(in LtpValueRef value, long offset, Span<byte> destination)
    {
        if (offset < 0 || destination.IsEmpty)
        {
            return 0;
        }

        switch (value.Kind)
        {
            case LtpValueKind.Inline:
                int n = 0;
                while (n < destination.Length && offset + n < value.InlineSize)
                {
                    destination[n] = (byte)(value.InlineValue >> (8 * (int)(offset + n)));
                    n++;
                }

                return n;
            case LtpValueKind.Heap:
                if (!TryGetAllocation(new Hid(value.Hnid), out HeapAllocation a) || offset >= a.Length)
                {
                    return 0;
                }

                ReadOnlySpan<byte> bytes = GetBytes(a)[(int)offset..];
                int c = Math.Min(bytes.Length, destination.Length);
                bytes[..c].CopyTo(destination);
                return c;
            case LtpValueKind.Subnode:
                return GetValueTree(new Nid(value.Hnid))?.Read(offset, destination) ?? 0;
            default:
                return 0;
        }
    }

    /// <summary>Reads a whole value into a new array.</summary>
    /// <param name="value">The reference.</param>
    /// <returns>The bytes, or null when the value cannot be read or is longer than <see cref="LtpOptions.MaxValueBytes"/>.</returns>
    public byte[]? ReadValue(in LtpValueRef value)
    {
        long length = GetValueLength(value);
        if (length < 0 || length > Options.MaxValueBytes || length > Array.MaxLength)
        {
            return null;
        }

        var result = new byte[length];
        return ReadValue(value, 0, result) == length ? result : null;
    }

    // One subnode value stays open between calls, so reading a large value in pieces reads each block once.
    private NdbDataTree? GetValueTree(Nid nid)
    {
        if (_valueTree is not null && _valueNid == nid)
        {
            return _valueTree;
        }

        _valueTree?.Dispose();
        _valueTree = null;
        _valueTree = OpenSubnodeData(nid);
        _valueNid = nid;
        return _valueTree;
    }

    /// <summary>Returns the pooled buffers and closes the data trees this heap opened.</summary>
    public void Dispose()
    {
        if (_buf is not null)
        {
            ArrayPool<byte>.Shared.Return(_buf);
            _buf = null;
            _cachedBlock = -1;
        }

        _valueTree?.Dispose();
        _valueTree = null;
        _data.Dispose();
    }
}
