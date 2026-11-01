using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace RecursiveAI.OneMail.Pst.Ndb;

/// <summary>
/// An editable node or block B-tree of a Unicode PST (MS-PST 2.2.2.7.7). Every change copies the pages on its path, so the committed tree stays whole
/// until the header switches to the new root. Pages split when full and, when a deletion leaves a page under half full, merge with a neighbour that
/// has room; an empty page is dropped and a root with one child is replaced by it.
/// </summary>
internal sealed class EditorBTree
{
    private const int Footer = 488;     // BTPAGE: rgentries[488], then cEnt, cEntMax, cbEnt, cLevel (MS-PST 2.2.2.7.7.1)
    private const int IndexStride = 24; // BTENTRY: btkey, BREF
    private const int IndexMax = Footer / IndexStride;

    private readonly EditorPageStore _pages;
    private readonly byte _ptype;
    private readonly bool _isNbt;
    private readonly int _leafStride;
    private readonly int _leafMax;

    public EditorBTree(EditorPageStore pages, bool isNbt, Bref root)
    {
        _pages = pages;
        _isNbt = isNbt;
        _ptype = isNbt ? (byte)0x81 : (byte)0x80;
        _leafStride = isNbt ? 32 : 24;
        _leafMax = Footer / _leafStride;
        Root = root;
    }

    public Bref Root { get; private set; }

    public int LeafStride => _leafStride;

    private ulong Key(byte[] pg, int i)
    {
        ulong k = BinaryPrimitives.ReadUInt64LittleEndian(pg.AsSpan(i * pg[490]));
        return _isNbt ? (uint)k : k & ~1UL;
    }

    private static Bref ChildRef(byte[] pg, int i) =>
        new(new Bid(BinaryPrimitives.ReadUInt64LittleEndian(pg.AsSpan((i * pg[490]) + 8))), BinaryPrimitives.ReadUInt64LittleEndian(pg.AsSpan((i * pg[490]) + 16)));

    private static void SetIndex(byte[] pg, int i, ulong key, Bref child)
    {
        Span<byte> e = pg.AsSpan(i * IndexStride, IndexStride);
        BinaryPrimitives.WriteUInt64LittleEndian(e, key);
        BinaryPrimitives.WriteUInt64LittleEndian(e[8..], child.Bid.Value);
        BinaryPrimitives.WriteUInt64LittleEndian(e[16..], child.Ib);
    }

    // MS-PST 2.2.2.7.7.1: a page's cLevel is one below its parent's, and a leaf is level 0. Enforcing it on every step down is what makes each walk end: a crafted
    // tree whose index page names itself or an ancestor would otherwise be followed forever.
    private byte[] ReadChild(Bref at, byte[] parent)
    {
        byte[] pg = _pages.Read(at, _ptype);
        if (pg[491] != parent[491] - 1)
        {
            throw new InvalidDataException($"the B-tree page at 0x{at.Ib:x} is level {pg[491]} under a level {parent[491]} page");
        }

        return pg;
    }

    private int Max(byte[] pg) => pg[491] == 0 ? _leafMax : IndexMax;

    /// <summary>The first entry whose key is not below <paramref name="key"/>.</summary>
    private int LowerBound(byte[] pg, ulong key)
    {
        int lo = 0, hi = pg[488];
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (Key(pg, mid) < key)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    /// <summary>The child that holds <paramref name="key"/>: the last entry at or below it, or the first when the key is below all of them.</summary>
    private int ChildIndex(byte[] pg, ulong key)
    {
        int n = pg[488];
        int i = LowerBound(pg, key);
        if (i < n && Key(pg, i) == key)
        {
            return i;
        }

        return i == 0 ? 0 : i - 1;
    }

    // ---- reading -----------------------------------------------------------------------------------------------------------------------

    /// <summary>Copies the leaf entry for <paramref name="key"/> into <paramref name="entry"/> (at least one stride long).</summary>
    public bool TryGet(ulong key, Span<byte> entry)
    {
        byte[] pg = _pages.Read(Root, _ptype);
        while (pg[491] != 0)
        {
            if (pg[488] == 0)
            {
                return false;
            }

            pg = ReadChild(ChildRef(pg, ChildIndex(pg, key)), pg);
        }

        int i = LowerBound(pg, key);
        if (i < pg[488] && Key(pg, i) == key)
        {
            pg.AsSpan(i * pg[490], Math.Min(_leafStride, entry.Length)).CopyTo(entry);
            return true;
        }

        return false;
    }

    /// <summary>Calls <paramref name="visit"/> for every leaf entry in key order.</summary>
    public void ForEach(Action<ulong, ReadOnlySpan<byte>> visit) => Walk(Root, null, visit, null, []);

    /// <summary>Calls <paramref name="page"/> for every page of the tree, leaves and index pages.</summary>
    public void ForEachPage(Action<Bref> page) => Walk(Root, null, null, page, []);

    // A page reached twice, or one at the wrong level, means the tree is not a tree (a crafted or damaged file); the walk stops with InvalidDataException.
    private void Walk(Bref at, byte[]? parent, Action<ulong, ReadOnlySpan<byte>>? leaf, Action<Bref>? page, HashSet<long> seen)
    {
        byte[] pg = parent is null ? _pages.Read(at, _ptype) : ReadChild(at, parent);
        if (!seen.Add((long)at.Ib))
        {
            throw new InvalidDataException($"the B-tree page at 0x{at.Ib:x} is reached twice");
        }

        page?.Invoke(at);
        int n = pg[488];
        if (pg[491] == 0)
        {
            if (leaf is not null)
            {
                for (int i = 0; i < n; i++)
                {
                    leaf(Key(pg, i), pg.AsSpan(i * pg[490], _leafStride));
                }
            }

            return;
        }

        for (int i = 0; i < n; i++)
        {
            Walk(ChildRef(pg, i), pg, leaf, page, seen);
        }
    }

    // ---- inserting ---------------------------------------------------------------------------------------------------------------------

    private readonly struct Outcome(Bref self, ulong firstKey, bool split = false, ulong separator = 0, Bref right = default)
    {
        public readonly Bref Self = self;
        public readonly ulong FirstKey = firstKey;
        public readonly bool Split = split;
        public readonly ulong Separator = separator;
        public readonly Bref Right = right;
    }

    /// <summary>Adds the entry, or replaces the one with the same key.</summary>
    public void Upsert(ulong key, ReadOnlySpan<byte> entry)
    {
        Outcome o = Insert(Root, null, key, entry);
        if (!o.Split)
        {
            Root = o.Self;
            return;
        }

        // The root split: a new root one level up holds the two halves.
        byte[] old = _pages.Read(o.Self, _ptype);
        (Bref rootRef, byte[] root) = _pages.New(_ptype, old[491] + 1, IndexStride, IndexMax);
        SetIndex(root, 0, o.FirstKey, o.Self);
        SetIndex(root, 1, o.Separator, o.Right);
        root[488] = 2;
        Root = rootRef;
    }

    private Outcome Insert(Bref at, byte[]? parent, ulong key, ReadOnlySpan<byte> entry)
    {
        byte[] pg = parent is null ? _pages.Read(at, _ptype) : ReadChild(at, parent);
        if (pg[491] == 0)
        {
            (Bref self, byte[] w) = _pages.Own(at, pg, _ptype);
            int n = w[488];
            int i = LowerBound(w, key);
            if (i < n && Key(w, i) == key)
            {
                WriteLeaf(w, i, entry);
                return new Outcome(self, Key(w, 0));
            }

            Span<byte> raw = stackalloc byte[32];
            raw.Clear();
            entry[..Math.Min(entry.Length, _leafStride)].CopyTo(raw);
            return InsertEntry(self, w, i, raw[..w[490]]);
        }

        int ci = ChildIndex(pg, key);
        Outcome c = Insert(ChildRef(pg, ci), pg, key, entry);
        (Bref me, byte[] mw) = _pages.Own(at, pg, _ptype);
        SetIndex(mw, ci, c.FirstKey, c.Self);
        if (!c.Split)
        {
            return new Outcome(me, Key(mw, 0));
        }

        Span<byte> idx = stackalloc byte[IndexStride];
        BinaryPrimitives.WriteUInt64LittleEndian(idx, c.Separator);
        BinaryPrimitives.WriteUInt64LittleEndian(idx[8..], c.Right.Bid.Value);
        BinaryPrimitives.WriteUInt64LittleEndian(idx[16..], c.Right.Ib);
        return InsertEntry(me, mw, ci + 1, idx);
    }

    private static void WriteLeaf(byte[] w, int i, ReadOnlySpan<byte> entry)
    {
        Span<byte> dest = w.AsSpan(i * w[490], w[490]);
        dest.Clear();
        entry[..Math.Min(entry.Length, dest.Length)].CopyTo(dest);
    }

    // Puts a stride-sized entry at position i. A full page is split in two around the entry, so the page never holds more than its capacity.
    private Outcome InsertEntry(Bref self, byte[] w, int i, ReadOnlySpan<byte> entry)
    {
        int n = w[488];
        int stride = w[490];
        if (n < Max(w))
        {
            Array.Copy(w, i * stride, w, (i + 1) * stride, (n - i) * stride);
            entry.CopyTo(w.AsSpan(i * stride, stride));
            w[488] = (byte)(n + 1);
            return new Outcome(self, Key(w, 0));
        }

        var all = new byte[(n + 1) * stride];
        Array.Copy(w, 0, all, 0, i * stride);
        entry.CopyTo(all.AsSpan(i * stride, stride));
        Array.Copy(w, i * stride, all, (i + 1) * stride, (n - i) * stride);
        int keep = (n + 2) / 2;
        (Bref rightRef, byte[] right) = _pages.New(_ptype, w[491], stride, Max(w));
        Array.Clear(w, 0, Footer);
        Array.Copy(all, 0, w, 0, keep * stride);
        w[488] = (byte)keep;
        Array.Copy(all, keep * stride, right, 0, (n + 1 - keep) * stride);
        right[488] = (byte)(n + 1 - keep);
        return new Outcome(self, Key(w, 0), split: true, separator: Key(right, 0), right: rightRef);
    }

    // ---- removing ----------------------------------------------------------------------------------------------------------------------

    private readonly struct Removal(bool found, Bref self, ulong firstKey, bool empty, int count)
    {
        public readonly bool Found = found;
        public readonly Bref Self = self;
        public readonly ulong FirstKey = firstKey;
        public readonly bool Empty = empty;
        public readonly int Count = count;
    }

    /// <summary>Removes the entry with <paramref name="key"/>; false when there is none.</summary>
    public bool Remove(ulong key)
    {
        Removal r = Delete(Root, null, key);
        if (!r.Found)
        {
            return false;
        }

        if (r.Empty)
        {
            (Bref empty, _) = _pages.New(_ptype, 0, _leafStride, _leafMax);
            Root = empty;
            return true;
        }

        Root = r.Self;
        // MS-PST 2.2.2.7.7: a root index page with one child adds nothing; the child takes its place.
        while (true)
        {
            byte[] pg = _pages.Read(Root, _ptype);
            if (pg[491] == 0 || pg[488] != 1)
            {
                break;
            }

            Bref only = ChildRef(pg, 0);
            _pages.Free(Root);
            Root = only;
        }

        return true;
    }

    private Removal Delete(Bref at, byte[]? parent, ulong key)
    {
        byte[] pg = parent is null ? _pages.Read(at, _ptype) : ReadChild(at, parent);
        int n = pg[488];
        if (pg[491] == 0)
        {
            int i = LowerBound(pg, key);
            if (i >= n || Key(pg, i) != key)
            {
                return default;
            }

            (Bref self, byte[] w) = _pages.Own(at, pg, _ptype);
            int stride = w[490];
            Array.Copy(w, (i + 1) * stride, w, i * stride, (n - i - 1) * stride);
            Array.Clear(w, (n - 1) * stride, stride);
            w[488] = (byte)(n - 1);
            if (n - 1 == 0)
            {
                _pages.Free(self);
                return new Removal(true, default, 0, true, 0);
            }

            return new Removal(true, self, Key(w, 0), false, n - 1);
        }

        if (n == 0)
        {
            return default;
        }

        int ci = ChildIndex(pg, key);
        Removal c = Delete(ChildRef(pg, ci), pg, key);
        if (!c.Found)
        {
            return default;
        }

        (Bref me, byte[] mw) = _pages.Own(at, pg, _ptype);
        if (c.Empty)
        {
            Array.Copy(mw, (ci + 1) * IndexStride, mw, ci * IndexStride, (n - ci - 1) * IndexStride);
            Array.Clear(mw, (n - 1) * IndexStride, IndexStride);
            mw[488] = (byte)(n - 1);
            if (n - 1 == 0)
            {
                _pages.Free(me);
                return new Removal(true, default, 0, true, 0);
            }

            return new Removal(true, me, Key(mw, 0), false, n - 1);
        }

        SetIndex(mw, ci, c.FirstKey, c.Self);
        if (c.Count < MinFill(mw))
        {
            TryMerge(mw, ci);
        }

        return new Removal(true, me, Key(mw, 0), false, mw[488]);
    }

    private int MinFill(byte[] parent)
    {
        // A child of an index page at level 1 is a leaf; deeper ones are index pages.
        return parent[491] == 1 ? _leafMax / 2 : IndexMax / 2;
    }

    // Joins the child at ci with a neighbour when their entries fit in one page.
    private void TryMerge(byte[] parent, int ci)
    {
        int n = parent[488];
        if (n < 2)
        {
            return;
        }

        int left = ci + 1 < n ? ci : ci - 1;
        int right = left + 1;
        Bref leftRef = ChildRef(parent, left);
        Bref rightRef = ChildRef(parent, right);
        byte[] lp = ReadChild(leftRef, parent);
        byte[] rp = ReadChild(rightRef, parent);
        int max = Max(lp);
        if (lp[488] + rp[488] > max)
        {
            return;
        }

        (Bref lself, byte[] lw) = _pages.Own(leftRef, lp, _ptype);
        int stride = lw[490];
        Array.Copy(rp, 0, lw, lw[488] * stride, rp[488] * stride);
        lw[488] = (byte)(lw[488] + rp[488]);
        _pages.Free(rightRef);
        SetIndex(parent, left, Key(lw, 0), lself);
        Array.Copy(parent, (right + 1) * IndexStride, parent, right * IndexStride, (n - right - 1) * IndexStride);
        Array.Clear(parent, (n - 1) * IndexStride, IndexStride);
        parent[488] = (byte)(n - 1);
    }
}
