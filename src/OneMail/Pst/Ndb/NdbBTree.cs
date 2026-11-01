using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;

namespace RecursiveAI.OneMail.Pst.Ndb;

/// <summary>
/// Walks a B-tree page by page with one page buffer per level (at most nine), so memory does not depend on the number of
/// entries. Both the NBT and the BBT use it.
/// </summary>
internal sealed class BTreeWalker : IDisposable
{
    // MS-PST 2.2.2.7.7: "a general BTree implementation that supports up to 8 intermediate levels". Level 0 is the leaf.
    private const int MaxLevel = 8;

    private readonly NdbFile _file;
    private readonly bool _isNbt;
    private readonly NdbPageType _pageType;
    private readonly byte[]?[] _pages = new byte[MaxLevel + 1][];
    private readonly int[] _count = new int[MaxLevel + 1];
    private readonly int[] _index = new int[MaxLevel + 1];
    private readonly int[] _entrySize = new int[MaxLevel + 1];
    private readonly int[] _level = new int[MaxLevel + 1];
    private int _depth = -1;
    private bool _done;
    private ulong _lastKey;
    private bool _haveLast;
    private long _visited;
    private readonly long _visitLimit;

    public BTreeWalker(NdbFile file, bool isNbt)
    {
        _file = file;
        _isNbt = isNbt;
        _pageType = isNbt ? NdbPageType.NodeBTree : NdbPageType.BlockBTree;
        // A tree cannot have more distinct pages than the file has page slots. Two parents may point at the same child in a
        // damaged file; the cap keeps that from multiplying work.
        _visitLimit = (file.Length / file.PageSize) + 8;
    }

    private Bref Root => _isNbt ? _file.Header.Root.NodeBTree : _file.Header.Root.BlockBTree;

    public void Reset()
    {
        _depth = -1;
        _done = false;
        _haveLast = false;
        _visited = 0;
    }

    public void Dispose()
    {
        for (int i = 0; i < _pages.Length; i++)
        {
            if (_pages[i] is { } p)
            {
                ArrayPool<byte>.Shared.Return(p);
                _pages[i] = null;
            }
        }
    }

    private ulong KeyOf(ReadOnlySpan<byte> entry)
    {
        NdbLayout l = _file.Layout;
        ulong k = l.IdSize == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(entry) : BinaryPrimitives.ReadUInt32LittleEndian(entry);
        // NBT keys are 32-bit NIDs even where the field is 8 bytes. BBT keys are compared with the reserved bit cleared.
        return _isNbt ? (uint)k : k & ~1UL;
    }

    private Bref ChildOf(ReadOnlySpan<byte> entry)
    {
        NdbLayout l = _file.Layout;
        return l.IdSize == 8
            ? new Bref(new Bid(BinaryPrimitives.ReadUInt64LittleEndian(entry[8..])), BinaryPrimitives.ReadUInt64LittleEndian(entry[16..]))
            : new Bref(new Bid(BinaryPrimitives.ReadUInt32LittleEndian(entry[4..])), BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]));
    }

    /// <summary>Reads a page into the buffer for <paramref name="depth"/> and validates its footer. False means the page cannot be walked.</summary>
    private bool Load(int depth, Bref bref, int expectedLevel)
    {
        if (++_visited > _visitLimit)
        {
            _file.Report(NdbDefectKind.BTreeTooManyPages, (long)bref.Ib, "the walk visited more pages than the file holds; the tree refers to pages more than once");
            _done = true;
            return false;
        }

        NdbLayout l = _file.Layout;
        byte[] buf = _pages[depth] ??= ArrayPool<byte>.Shared.Rent(l.PageSize);
        if (!_file.ReadPage((long)bref.Ib, buf, _pageType, bref.Bid.Value, out _))
        {
            return false;
        }

        ReadOnlySpan<byte> page = buf.AsSpan(0, l.PageSize);
        int f = l.BTreeFooterOffset;
        int cEnt, cEntMax, cbEnt, cLevel;
        if (l.Format == NdbFormat.Unicode4K)
        {
            cEnt = BinaryPrimitives.ReadUInt16LittleEndian(page[f..]);
            cEntMax = BinaryPrimitives.ReadUInt16LittleEndian(page[(f + 2)..]);
            cbEnt = page[f + 4];
            cLevel = page[f + 5];
        }
        else
        {
            cEnt = page[f];
            cEntMax = page[f + 1];
            cbEnt = page[f + 2];
            cLevel = page[f + 3];
        }

        long ib = (long)bref.Ib;
        if (cLevel > MaxLevel)
        {
            _file.Report(NdbDefectKind.BTreeTooDeep, ib, $"cLevel {cLevel} exceeds the format's {MaxLevel} levels");
            return false;
        }

        if (expectedLevel >= 0 && cLevel != expectedLevel)
        {
            _file.Report(NdbDefectKind.BTreeLevelMismatch, ib, $"page is level {cLevel}, its parent expects {expectedLevel}");
            return false;
        }

        // Entries are cbEnt bytes apart even when the entry itself is shorter (MS-PST 2.2.2.7.7.1), so cbEnt is the stride.
        int min = cLevel > 0 ? l.BTreeIndexMin : _isNbt ? l.NbtLeafMin : l.BbtLeafMin;
        if (cbEnt < min || (long)cEnt * cbEnt > f)
        {
            _file.Report(NdbDefectKind.BTreeBadGeometry, ib, $"cEnt {cEnt} x cbEnt {cbEnt} does not fit in {f} bytes (entries need at least {min})");
            return false;
        }

        if (cEnt > cEntMax)
        {
            _file.Report(NdbDefectKind.BTreeBadGeometry, ib, $"cEnt {cEnt} exceeds cEntMax {cEntMax}");
        }

        _count[depth] = cEnt;
        _index[depth] = 0;
        _entrySize[depth] = cbEnt;
        _level[depth] = cLevel;
        return true;
    }

    private bool LoadRoot()
    {
        _depth = -1;
        if (!Load(0, Root, -1))
        {
            _done = true;
            return false;
        }

        _depth = 0;
        return true;
    }

    private ReadOnlySpan<byte> Entry(int depth, int i) => _pages[depth].AsSpan((i * _entrySize[depth]), _entrySize[depth]);

    /// <summary>Positions so that the next <see cref="MoveNext"/> returns the first leaf entry with key greater than or equal to <paramref name="key"/>.</summary>
    public void Seek(ulong key)
    {
        Reset();
        if (!LoadRoot())
        {
            return;
        }

        while (_level[_depth] > 0)
        {
            int d = _depth;
            int pick = -1;
            for (int i = 0; i < _count[d]; i++)
            {
                if (KeyOf(Entry(d, i)) <= key)
                {
                    pick = i;
                }
                else
                {
                    break;
                }
            }

            if (pick < 0)
            {
                pick = 0;
            }

            // Leave the parent pointing past the child we descend into, so a following MoveNext continues with its sibling.
            Bref child = ChildOf(Entry(d, pick));
            _index[d] = pick + 1;
            if (!Load(d + 1, child, _level[d] - 1))
            {
                // The child is unusable: carry on with the next sibling from here.
                return;
            }

            _depth = d + 1;
        }

        int leaf = _depth;
        int j = 0;
        while (j < _count[leaf] && KeyOf(Entry(leaf, j)) < key)
        {
            j++;
        }

        _index[leaf] = j;
        _haveLast = false;
    }

    /// <summary>Advances to the next leaf entry in key order. The entry span is valid until the next call.</summary>
    public bool MoveNext(out ReadOnlySpan<byte> entry, out ulong key)
    {
        entry = default;
        key = 0;
        if (_done)
        {
            return false;
        }

        if (_depth < 0 && !LoadRoot())
        {
            return false;
        }

        while (true)
        {
            int d = _depth;
            if (_level[d] == 0)
            {
                if (_index[d] < _count[d])
                {
                    ReadOnlySpan<byte> e = Entry(d, _index[d]++);
                    ulong k = KeyOf(e);
                    if (_haveLast && k < _lastKey)
                    {
                        _file.Report(NdbDefectKind.BTreeKeyOrder, 0, $"leaf key 0x{k:x} follows 0x{_lastKey:x}; entry skipped");
                        continue;
                    }

                    _lastKey = k;
                    _haveLast = true;
                    entry = e;
                    key = k;
                    return true;
                }
            }
            else if (_index[d] < _count[d])
            {
                Bref child = ChildOf(Entry(d, _index[d]++));
                if (Load(d + 1, child, _level[d] - 1))
                {
                    _depth = d + 1;
                }

                if (_done)
                {
                    return false;
                }

                continue;
            }

            // This page is used up: go back to the parent.
            if (--_depth < 0)
            {
                _done = true;
                return false;
            }
        }
    }
}

/// <summary>Common behaviour of the two B-trees: enumeration in key order with constant memory.</summary>
/// <typeparam name="TEntry">The leaf entry type.</typeparam>
public abstract class NdbBTree<TEntry> : IEnumerable<TEntry>
{
    private protected NdbBTree(NdbFile file)
    {
        File = file;
    }

    private protected NdbFile File { get; }

    private BTreeWalker? _lookup;

    private protected abstract bool IsNbt { get; }

    private protected abstract TEntry Decode(ReadOnlySpan<byte> entry);

    private protected abstract ulong KeyOfEntry(TEntry entry);

    /// <summary>Creates an independent cursor over all leaf entries in key order. Dispose it to release its page buffers.</summary>
    /// <returns>A cursor positioned before the first entry.</returns>
    public Cursor CreateCursor() => new(this, new BTreeWalker(File, IsNbt));

    /// <summary>Enumerates every leaf entry in key order. Memory use is a few page buffers, whatever the entry count.</summary>
    /// <returns>An enumerator; dispose it when done.</returns>
    public Cursor GetEnumerator() => CreateCursor();

    IEnumerator<TEntry> IEnumerable<TEntry>.GetEnumerator() => CreateCursor();

    IEnumerator IEnumerable.GetEnumerator() => CreateCursor();

    private protected bool TryGetByKey(ulong key, out TEntry entry)
    {
        _lookup ??= new BTreeWalker(File, IsNbt);
        _lookup.Seek(key);
        if (_lookup.MoveNext(out ReadOnlySpan<byte> raw, out ulong found) && found == key)
        {
            entry = Decode(raw);
            return true;
        }

        entry = default!;
        return false;
    }

    /// <summary>A forward cursor over the leaf entries of a B-tree.</summary>
    public sealed class Cursor : IEnumerator<TEntry>
    {
        private readonly NdbBTree<TEntry> _tree;
        private readonly BTreeWalker _walker;

        internal Cursor(NdbBTree<TEntry> tree, BTreeWalker walker)
        {
            _tree = tree;
            _walker = walker;
            Current = default!;
        }

        /// <summary>The entry the cursor is on.</summary>
        public TEntry Current { get; private set; }

        object? IEnumerator.Current => Current;

        /// <summary>Moves to the next entry in key order.</summary>
        /// <returns>False at the end, or when the tree cannot be walked further (see <see cref="NdbFile.Defects"/>).</returns>
        public bool MoveNext()
        {
            if (_walker.MoveNext(out ReadOnlySpan<byte> raw, out _))
            {
                Current = _tree.Decode(raw);
                return true;
            }

            return false;
        }

        /// <summary>Positions the cursor so the next <see cref="MoveNext"/> returns the first entry whose key is greater than or equal to <paramref name="key"/>.</summary>
        /// <param name="key">For the node tree the NID value; for the block tree the BID value.</param>
        public void Seek(ulong key) => _walker.Seek(key);

        /// <summary>Starts again from the first entry.</summary>
        public void Reset() => _walker.Reset();

        /// <summary>Releases the page buffers.</summary>
        public void Dispose() => _walker.Dispose();
    }
}

/// <summary>The node B-tree (NBT): maps NIDs to the data and subnode blocks of each node (MS-PST 2.2.2.7.7.4).</summary>
public sealed class NodeBTree : NdbBTree<NodeEntry>
{
    internal NodeBTree(NdbFile file) : base(file)
    {
    }

    private protected override bool IsNbt => true;

    private protected override ulong KeyOfEntry(NodeEntry entry) => entry.Nid.Value;

    private protected override NodeEntry Decode(ReadOnlySpan<byte> e)
    {
        if (File.Layout.IdSize == 8)
        {
            // Unicode NBTENTRY: nid (8, only 32 bits used), bidData (8), bidSub (8), nidParent (4), padding (4).
            return new NodeEntry(
                new Nid((uint)BinaryPrimitives.ReadUInt64LittleEndian(e)),
                new Bid(BinaryPrimitives.ReadUInt64LittleEndian(e[8..])),
                new Bid(BinaryPrimitives.ReadUInt64LittleEndian(e[16..])),
                new Nid(BinaryPrimitives.ReadUInt32LittleEndian(e[24..])));
        }

        return new NodeEntry(
            new Nid(BinaryPrimitives.ReadUInt32LittleEndian(e)),
            new Bid(BinaryPrimitives.ReadUInt32LittleEndian(e[4..])),
            new Bid(BinaryPrimitives.ReadUInt32LittleEndian(e[8..])),
            new Nid(BinaryPrimitives.ReadUInt32LittleEndian(e[12..])));
    }

    /// <summary>Finds a node by NID.</summary>
    /// <param name="nid">The NID.</param>
    /// <param name="entry">The entry, when found.</param>
    /// <returns>True when the NBT has the NID.</returns>
    public bool TryGet(Nid nid, out NodeEntry entry) =>
        File.Recovered is { } r ? r.Nodes.TryGetValue(nid.Value, out entry) : TryGetByKey(nid.Value, out entry);
}

/// <summary>The block B-tree (BBT): maps BIDs to file offset, size and reference count (MS-PST 2.2.2.7.7.3).</summary>
public sealed class BlockBTree : NdbBTree<BlockEntry>
{
    internal BlockBTree(NdbFile file) : base(file)
    {
    }

    private protected override bool IsNbt => false;

    private protected override ulong KeyOfEntry(BlockEntry entry) => entry.Bid.Normalized.Value;

    private protected override BlockEntry Decode(ReadOnlySpan<byte> e)
    {
        if (File.Layout.IdSize == 8)
        {
            // Unicode BBTENTRY: BREF (16), cb (2), cRef (2), padding (4).
            return new BlockEntry(
                new Bref(new Bid(BinaryPrimitives.ReadUInt64LittleEndian(e)), BinaryPrimitives.ReadUInt64LittleEndian(e[8..])),
                BinaryPrimitives.ReadUInt16LittleEndian(e[16..]),
                BinaryPrimitives.ReadUInt16LittleEndian(e[18..]));
        }

        return new BlockEntry(
            new Bref(new Bid(BinaryPrimitives.ReadUInt32LittleEndian(e)), BinaryPrimitives.ReadUInt32LittleEndian(e[4..])),
            BinaryPrimitives.ReadUInt16LittleEndian(e[8..]),
            BinaryPrimitives.ReadUInt16LittleEndian(e[10..]));
    }

    /// <summary>Finds a block by BID. The reserved bit 0 of <paramref name="bid"/> is ignored.</summary>
    /// <param name="bid">The BID.</param>
    /// <param name="entry">The entry, when found.</param>
    /// <returns>True when the BBT has the BID.</returns>
    public bool TryGet(Bid bid, out BlockEntry entry) =>
        File.Recovered is { } r ? r.Blocks.TryGetValue(bid.Normalized.Value, out entry) : TryGetByKey(bid.Normalized.Value, out entry);
}
