using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace RecursiveAI.OneMail.Pst.Ndb;

/// <summary>
/// The stream of bytes behind a node or subnode: one data block, or a tree of XBLOCKs and XXBLOCKs over many data blocks
/// (MS-PST 2.2.2.8.3). Leaves are listed on open; their contents are read, checked and decoded on demand.
/// </summary>
/// <remarks>Not thread-safe. Holds one cached block, so sequential reads cost one block read per block.</remarks>
public sealed class NdbDataTree : IDisposable
{
    private readonly NdbFile _file;
    private readonly List<BlockEntry> _leaves = [];
    private readonly List<long> _starts = [];
    private byte[]? _cache;
    private int _cachedLeaf = -1;
    private int _cachedLength;

    internal NdbDataTree(NdbFile file, Bid root)
    {
        _file = file;
        Root = root;
    }

    /// <summary>The BID the tree was opened from (null for a node without data).</summary>
    public Bid Root { get; }

    /// <summary>The total number of data bytes, after any decompression: the sum of the leaf sizes.</summary>
    public long Length { get; private set; }

    /// <summary>The number of data blocks.</summary>
    public int LeafCount => _leaves.Count;

    /// <summary>The BBT entry of the <paramref name="index"/>th data block, in stream order.</summary>
    /// <param name="index">Zero-based leaf index.</param>
    /// <returns>The entry.</returns>
    public BlockEntry GetLeaf(int index) => _leaves[index];

    /// <summary>The number of data bytes in the <paramref name="index"/>th data block, after any decompression.</summary>
    /// <param name="index">Zero-based leaf index.</param>
    /// <returns>The block's logical size.</returns>
    public int GetLeafSize(int index) => (int)((index + 1 < _starts.Count ? _starts[index + 1] : Length) - _starts[index]);

    /// <summary>Copies bytes from inside one data block, for structures (heap blocks, table row blocks) that are laid out per block.</summary>
    /// <param name="leaf">Zero-based leaf index.</param>
    /// <param name="offset">Position inside the block's data.</param>
    /// <param name="destination">Receives the bytes.</param>
    /// <returns>The bytes copied: fewer than requested only at the end of the block, or zero where the block could not be read (defects say why) or the arguments are out of range.</returns>
    public int ReadLeaf(int leaf, int offset, Span<byte> destination)
    {
        if ((uint)leaf >= (uint)_leaves.Count || offset < 0 || (leaf != _cachedLeaf && !Load(leaf)))
        {
            return 0;
        }

        int n = Math.Min(_cachedLength - offset, destination.Length);
        if (n <= 0)
        {
            return 0;
        }

        _cache.AsSpan(offset, n).CopyTo(destination);
        return n;
    }

    internal void AddLeaf(in BlockEntry leaf, int logicalSize)
    {
        _leaves.Add(leaf);
        _starts.Add(Length);
        Length += logicalSize;
    }

    /// <summary>Copies bytes from the stream starting at <paramref name="offset"/>.</summary>
    /// <param name="offset">Position in the stream.</param>
    /// <param name="destination">Receives the bytes.</param>
    /// <returns>The bytes copied: fewer than requested only at the end, or where a block could not be read (defects say why). Zero at or past the end.</returns>
    public int Read(long offset, Span<byte> destination)
    {
        int copied = 0;
        while (copied < destination.Length && offset < Length)
        {
            int leaf = FindLeaf(offset);
            if (leaf != _cachedLeaf && !Load(leaf))
            {
                break;
            }

            int within = (int)(offset - _starts[leaf]);
            int n = Math.Min(_cachedLength - within, destination.Length - copied);
            if (n <= 0)
            {
                break;
            }

            _cache.AsSpan(within, n).CopyTo(destination[copied..]);
            copied += n;
            offset += n;
        }

        return copied;
    }

    private int FindLeaf(long offset)
    {
        int lo = 0, hi = _starts.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >>> 1;
            if (_starts[mid] <= offset)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }

    private bool Load(int leaf)
    {
        _cache ??= ArrayPool<byte>.Shared.Rent(_file.MaxBlockDataSize);
        _cachedLeaf = -1;
        if (!_file.TryReadBlock(_leaves[leaf], _cache, out int length, out _))
        {
            return false;
        }

        _cachedLength = length;
        _cachedLeaf = leaf;
        return true;
    }

    /// <summary>Returns the cache buffer.</summary>
    public void Dispose()
    {
        if (_cache is not null)
        {
            ArrayPool<byte>.Shared.Return(_cache);
            _cache = null;
        }
    }
}

/// <summary>The subnodes of one node: a tree of SIBLOCKs and SLBLOCKs flattened into a list (MS-PST 2.2.2.8.3.3).</summary>
public sealed class SubnodeTree
{
    private readonly List<SubnodeEntry> _entries = [];
    private bool _unsorted;

    internal SubnodeTree(Bid root)
    {
        Root = root;
    }

    /// <summary>The BID the tree was opened from.</summary>
    public Bid Root { get; }

    /// <summary>The subnode entries in stored order (sorted by NID in a well-formed file).</summary>
    public IReadOnlyList<SubnodeEntry> Entries => _entries;

    internal void Add(in SubnodeEntry e)
    {
        if (_entries.Count > 0 && _entries[^1].Nid.Value >= e.Nid.Value)
        {
            _unsorted = true;
        }

        _entries.Add(e);
    }

    /// <summary>Finds a subnode by its local NID.</summary>
    /// <param name="nid">The subnode's NID.</param>
    /// <param name="entry">The entry, when found.</param>
    /// <returns>True when the node has such a subnode.</returns>
    public bool TryGet(Nid nid, out SubnodeEntry entry)
    {
        if (!_unsorted)
        {
            int lo = 0, hi = _entries.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >>> 1;
                uint v = _entries[mid].Nid.Value;
                if (v == nid.Value)
                {
                    entry = _entries[mid];
                    return true;
                }

                if (v < nid.Value)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            entry = default;
            return false;
        }

        foreach (SubnodeEntry e in _entries)
        {
            if (e.Nid == nid)
            {
                entry = e;
                return true;
            }
        }

        entry = default;
        return false;
    }
}

public sealed partial class NdbFile
{
    /// <summary>
    /// Opens the data stream of a node or subnode from its data BID: a single block, or the XBLOCK/XXBLOCK tree.
    /// </summary>
    /// <param name="data">The data BID from an <see cref="NodeEntry"/> or <see cref="SubnodeEntry"/>. Null gives an empty stream.</param>
    /// <returns>
    /// The tree, or null when the root block is missing from the BBT or unreadable. Problems inside the tree (a missing
    /// child, an inconsistent size) are defects and the readable part is returned.
    /// </returns>
    public NdbDataTree? OpenDataTree(Bid data)
    {
        var tree = new NdbDataTree(this, data);
        if (data.IsNull)
        {
            return tree;
        }

        if (!Blocks.TryGet(data, out BlockEntry root))
        {
            Report(NdbDefectKind.BlockNotFound, 0, $"data block {data} is not in the block B-tree");
            return null;
        }

        if (!data.IsInternal)
        {
            tree.AddLeaf(root, LogicalSize(root));
            return tree;
        }

        byte[] buf = ArrayPool<byte>.Shared.Rent(Layout.MaxBlockData);
        try
        {
            if (!TryReadBlock(root, buf, out int len, out _))
            {
                return null;
            }

            long declared = WalkDataTree(tree, root, buf.AsSpan(0, len), expectedLevel: -1);
            if (declared >= 0 && declared != tree.Length)
            {
                Report(NdbDefectKind.DataTreeSizeMismatch, (long)root.Block.Ib, $"lcbTotal says {declared} bytes, the data blocks hold {tree.Length}");
            }

            return tree;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    // On a 4 KiB file a compressed block stores its uncompressed size in the block trailer; elsewhere the size is cb.
    private int LogicalSize(in BlockEntry entry)
    {
        if (Layout.Format != NdbFormat.Unicode4K || entry.Bid.IsInternal)
        {
            return entry.Size;
        }

        int total = Layout.BlockTotal(entry.Size);
        long ib = (long)entry.Block.Ib;
        if (ib < 0 || ib + total > Length)
        {
            return entry.Size;
        }

        Span<byte> trailer = stackalloc byte[24];
        if (ReadRaw(ib + total - Layout.BlockTrailerSize, trailer) != trailer.Length)
        {
            return entry.Size;
        }

        int ucb = BinaryPrimitives.ReadUInt16LittleEndian(trailer[18..]);
        return ucb != 0 && ucb != entry.Size ? ucb : entry.Size;
    }

    // MS-PST 2.2.2.8.3.2: XBLOCK (level 1) lists data blocks; XXBLOCK (level 2) lists XBLOCKs. Returns lcbTotal of this block, or -1 when the header is bad.
    private long WalkDataTree(NdbDataTree tree, in BlockEntry block, ReadOnlySpan<byte> data, int expectedLevel)
    {
        long ib = (long)block.Block.Ib;
        int idSize = Layout.IdSize;
        if (data.Length < 8 || data[0] != 0x01)
        {
            Report(NdbDefectKind.DataTreeBadHeader, ib, $"block {block.Bid} is internal but its btype is not 1 (XBLOCK/XXBLOCK)");
            return -1;
        }

        int level = data[1];
        int cEnt = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        uint lcbTotal = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        if (level is not (1 or 2) || (expectedLevel >= 0 && level != expectedLevel))
        {
            Report(level > 2 || (expectedLevel >= 0 && level >= expectedLevel) ? NdbDefectKind.DataTreeTooDeep : NdbDefectKind.DataTreeBadHeader, ib, $"data tree block {block.Bid} has cLevel {level}");
            return -1;
        }

        if (8L + ((long)cEnt * idSize) > data.Length)
        {
            Report(NdbDefectKind.DataTreeBadHeader, ib, $"cEnt {cEnt} entries do not fit in the {data.Length}-byte block");
            return -1;
        }

        byte[]? child = null;
        try
        {
            for (int i = 0; i < cEnt; i++)
            {
                ulong raw = idSize == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(data[(8 + (i * 8))..]) : BinaryPrimitives.ReadUInt32LittleEndian(data[(8 + (i * 4))..]);
                var bid = new Bid(raw);
                if (!Blocks.TryGet(bid, out BlockEntry entry))
                {
                    Report(NdbDefectKind.DataTreeChildMissing, ib, $"data tree child {bid} is not in the block B-tree");
                    continue;
                }

                if (level == 1)
                {
                    if (bid.IsInternal)
                    {
                        Report(NdbDefectKind.DataTreeTooDeep, ib, $"XBLOCK child {bid} is itself an internal block");
                        continue;
                    }

                    if (tree.LeafCount >= Options.MaxDataTreeLeaves)
                    {
                        Report(NdbDefectKind.DataTreeTooLarge, ib, $"more than {Options.MaxDataTreeLeaves} data blocks; the rest is ignored");
                        return lcbTotal;
                    }

                    tree.AddLeaf(entry, LogicalSize(entry));
                }
                else
                {
                    if (!bid.IsInternal)
                    {
                        Report(NdbDefectKind.DataTreeBadHeader, ib, $"XXBLOCK child {bid} is a data block, not an XBLOCK");
                        continue;
                    }

                    child ??= ArrayPool<byte>.Shared.Rent(Layout.MaxBlockData);
                    if (TryReadBlock(entry, child, out int len, out _))
                    {
                        WalkDataTree(tree, entry, child.AsSpan(0, len), expectedLevel: 1);
                    }
                }
            }
        }
        finally
        {
            if (child is not null)
            {
                ArrayPool<byte>.Shared.Return(child);
            }
        }

        return lcbTotal;
    }

    /// <summary>Opens a node's subnode tree from its subnode BID (SLBLOCK, or SIBLOCK over SLBLOCKs).</summary>
    /// <param name="sub">The BID from <see cref="NodeEntry.Sub"/> or <see cref="SubnodeEntry.Sub"/>. Null gives an empty tree.</param>
    /// <returns>The tree, or null when the root block is missing from the BBT or unreadable.</returns>
    public SubnodeTree? OpenSubnodes(Bid sub)
    {
        var tree = new SubnodeTree(sub);
        if (sub.IsNull)
        {
            return tree;
        }

        if (!Blocks.TryGet(sub, out BlockEntry root))
        {
            Report(NdbDefectKind.BlockNotFound, 0, $"subnode block {sub} is not in the block B-tree");
            return null;
        }

        byte[] buf = ArrayPool<byte>.Shared.Rent(Layout.MaxBlockData);
        try
        {
            if (!TryReadBlock(root, buf, out int len, out _))
            {
                return null;
            }

            WalkSubnodes(tree, root, buf.AsSpan(0, len), siblockAllowed: true);
            return tree;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    // MS-PST 2.2.2.8.3.3: SLBLOCK (btype 2, level 0) holds SLENTRYs; SIBLOCK (level 1) holds (nid, bid) pairs pointing at SLBLOCKs.
    private void WalkSubnodes(SubnodeTree tree, in BlockEntry block, ReadOnlySpan<byte> data, bool siblockAllowed)
    {
        long ib = (long)block.Block.Ib;
        bool wide = Layout.IdSize == 8;
        int header = wide ? 8 : 4;
        if (data.Length < header || data[0] != 0x02 || data[1] > 1 || (data[1] == 1 && !siblockAllowed))
        {
            Report(NdbDefectKind.SubnodeBadHeader, ib, $"block {block.Bid}: btype/cLevel {(data.Length >= 2 ? data[0] : -1)}/{(data.Length >= 2 ? data[1] : -1)} is not a subnode block here");
            return;
        }

        int level = data[1];
        int cEnt = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        int entrySize = level == 0 ? (wide ? 24 : 12) : (wide ? 16 : 8);
        if (header + ((long)cEnt * entrySize) > data.Length)
        {
            Report(NdbDefectKind.SubnodeBadHeader, ib, $"cEnt {cEnt} entries do not fit in the {data.Length}-byte block");
            return;
        }

        byte[]? child = null;
        try
        {
            for (int i = 0; i < cEnt; i++)
            {
                ReadOnlySpan<byte> e = data.Slice(header + (i * entrySize), entrySize);
                if (level == 0)
                {
                    if (tree.Entries.Count >= Options.MaxSubnodeEntries)
                    {
                        Report(NdbDefectKind.SubnodeTooLarge, ib, $"more than {Options.MaxSubnodeEntries} subnodes; the rest is ignored");
                        return;
                    }

                    tree.Add(wide
                        ? new SubnodeEntry(new Nid((uint)BinaryPrimitives.ReadUInt64LittleEndian(e)), new Bid(BinaryPrimitives.ReadUInt64LittleEndian(e[8..])), new Bid(BinaryPrimitives.ReadUInt64LittleEndian(e[16..])))
                        : new SubnodeEntry(new Nid(BinaryPrimitives.ReadUInt32LittleEndian(e)), new Bid(BinaryPrimitives.ReadUInt32LittleEndian(e[4..])), new Bid(BinaryPrimitives.ReadUInt32LittleEndian(e[8..]))));
                }
                else
                {
                    var bid = new Bid(wide ? BinaryPrimitives.ReadUInt64LittleEndian(e[8..]) : BinaryPrimitives.ReadUInt32LittleEndian(e[4..]));
                    if (!Blocks.TryGet(bid, out BlockEntry entry))
                    {
                        Report(NdbDefectKind.SubnodeChildMissing, ib, $"SIBLOCK child {bid} is not in the block B-tree");
                        continue;
                    }

                    child ??= ArrayPool<byte>.Shared.Rent(Layout.MaxBlockData);
                    if (TryReadBlock(entry, child, out int len, out _))
                    {
                        WalkSubnodes(tree, entry, child.AsSpan(0, len), siblockAllowed: false);
                    }
                }
            }
        }
        finally
        {
            if (child is not null)
            {
                ArrayPool<byte>.Shared.Return(child);
            }
        }
    }
}
