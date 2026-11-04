using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace RecursiveAI.OneMail.Pst.Ltp;

/// <summary>
/// Inserts, replaces and removes records of an existing BTH with 4-byte keys and 4-byte data (MS-PST 2.3.2, 2.6.2.2), the shape a table's row index has. A node is
/// one heap allocation, so a node that grows or shrinks is allocated again and the one place that names it (its parent's record, or the BTHHEADER) is changed in
/// place; a node that outgrows an allocation is split, and one that empties is removed. Nodes are never merged: the format asks for sorted keys, not for full nodes.
/// </summary>
internal sealed class BthEditor
{
    private const byte BthSignature = 0xB5;   // MS-PST 2.3.2.1 bType
    private const int Record = 8;             // a key and its data, or a key and the HID of a child
    private const int MaxNode = HeapNodeBuilder.MaxAllocation / Record * Record;

    private readonly HeapEditor _heap;
    private readonly Hid _header;

    private BthEditor(HeapEditor heap, Hid header)
    {
        _heap = heap;
        _header = header;
    }

    /// <summary>Opens the BTH whose header is at <paramref name="header"/>; null when it does not have 4-byte keys and data.</summary>
    public static BthEditor? TryOpen(HeapEditor heap, Hid header)
    {
        ReadOnlySpan<byte> h = heap.Read(header);
        return h.Length >= 8 && h[0] == BthSignature && h[1] == 4 && h[2] == 4 ? new BthEditor(heap, header) : null;
    }

    private int Levels => _heap.Read(_header)[3];

    private Hid Root => new(BinaryPrimitives.ReadUInt32LittleEndian(_heap.Read(_header)[4..]));

    private void SetRoot(Hid root, int levels)
    {
        Span<byte> h = _heap.Write(_header);
        h[3] = (byte)levels;
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], root.Value);
    }

    // Where a key is: the index nodes from the root down, each with the record taken, and the leaf.
    private sealed class Path
    {
        public readonly List<(Hid Node, int Entry)> Index = [];
        public Hid Leaf;
    }

    private bool Descend(uint key, out Path path)
    {
        path = new Path();
        Hid node = Root;
        if (node.IsNull)
        {
            return false;
        }

        for (int level = Levels; level > 0; level--)
        {
            ReadOnlySpan<byte> n = _heap.Read(node);
            int count = n.Length / Record;
            // The last record whose key is not above the target; the first when the target is below them all.
            int lo = 0, hi = count - 1, e = 0;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (BinaryPrimitives.ReadUInt32LittleEndian(n[(mid * Record)..]) <= key)
                {
                    e = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            path.Index.Add((node, e));
            node = new Hid(BinaryPrimitives.ReadUInt32LittleEndian(n[((e * Record) + 4)..]));
        }

        path.Leaf = node;
        return true;
    }

    // The position of the first record with a key not below the target, and whether that record has the key.
    private static int Search(ReadOnlySpan<byte> leaf, uint key, out bool found)
    {
        int lo = 0, hi = leaf.Length / Record;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (BinaryPrimitives.ReadUInt32LittleEndian(leaf[(mid * Record)..]) < key)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        found = lo < leaf.Length / Record && BinaryPrimitives.ReadUInt32LittleEndian(leaf[(lo * Record)..]) == key;
        return lo;
    }

    /// <summary>Finds a record.</summary>
    public bool TryFind(uint key, out uint data)
    {
        data = 0;
        if (!Descend(key, out Path p))
        {
            return false;
        }

        ReadOnlySpan<byte> leaf = _heap.Read(p.Leaf);
        int at = Search(leaf, key, out bool found);
        if (found)
        {
            data = BinaryPrimitives.ReadUInt32LittleEndian(leaf[((at * Record) + 4)..]);
        }

        return found;
    }

    /// <summary>Sets the data of a record, adding the record when the key is new.</summary>
    public void Upsert(uint key, uint data)
    {
        if (!Descend(key, out Path p))
        {
            Hid only = _heap.Allocate(Record, _heap.LastBlock);
            Span<byte> s = _heap.Write(only);
            BinaryPrimitives.WriteUInt32LittleEndian(s, key);
            BinaryPrimitives.WriteUInt32LittleEndian(s[4..], data);
            SetRoot(only, 0);
            return;
        }

        ReadOnlySpan<byte> leaf = _heap.Read(p.Leaf);
        int at = Search(leaf, key, out bool found);
        if (found)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_heap.Write(p.Leaf)[((at * Record) + 4)..], data);
            return;
        }

        byte[] bytes = new byte[leaf.Length + Record];
        leaf[..(at * Record)].CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at * Record), key);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((at * Record) + 4), data);
        leaf[(at * Record)..].CopyTo(bytes.AsSpan((at + 1) * Record));

        if (bytes.Length <= MaxNode)
        {
            Reallocate(p, p.Index.Count, p.Leaf, bytes);
            if (at == 0)
            {
                SetFirstKey(p, p.Index.Count, key);
            }

            return;
        }

        // Too big for one allocation: two leaves, the second named in the parent by its first key.
        int half = bytes.Length / Record / 2 * Record;
        Hid left = Reallocate(p, p.Index.Count, p.Leaf, bytes.AsSpan(0, half).ToArray());
        if (at == 0)
        {
            SetFirstKey(p, p.Index.Count, key);
        }

        Hid right = NewNode(bytes.AsSpan(half), left.BlockIndex);
        InsertIndexRecord(p, p.Index.Count, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(half)), right);
    }

    /// <summary>Removes a record.</summary>
    /// <returns>False when there is no such key.</returns>
    public bool Remove(uint key)
    {
        if (!Descend(key, out Path p))
        {
            return false;
        }

        ReadOnlySpan<byte> leaf = _heap.Read(p.Leaf);
        int at = Search(leaf, key, out bool found);
        if (!found)
        {
            return false;
        }

        if (leaf.Length == Record)
        {
            _heap.Free(p.Leaf);
            RemoveIndexRecord(p, p.Index.Count);
            return true;
        }

        byte[] bytes = new byte[leaf.Length - Record];
        leaf[..(at * Record)].CopyTo(bytes);
        leaf[((at + 1) * Record)..].CopyTo(bytes.AsSpan(at * Record));
        Reallocate(p, p.Index.Count, p.Leaf, bytes);
        if (at == 0)
        {
            SetFirstKey(p, p.Index.Count, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        }

        return true;
    }

    // Replaces the node at depth `depth` (0 is the root; p.Index.Count is the leaf) with new contents in a new allocation, and updates whatever names it.
    private Hid Reallocate(Path p, int depth, Hid old, byte[] bytes)
    {
        int block = old.BlockIndex;
        _heap.Free(old);
        Hid hid = _heap.Allocate(bytes.Length, block);
        bytes.CopyTo(_heap.Write(hid));
        if (depth == 0)
        {
            SetRoot(hid, Levels);
        }
        else
        {
            (Hid parent, int entry) = p.Index[depth - 1];
            BinaryPrimitives.WriteUInt32LittleEndian(_heap.Write(parent)[((entry * Record) + 4)..], hid.Value);
        }

        if (depth == p.Index.Count)
        {
            p.Leaf = hid;
        }
        else
        {
            p.Index[depth] = (hid, p.Index[depth].Entry);
        }

        return hid;
    }

    private Hid NewNode(ReadOnlySpan<byte> bytes, int nearBlock)
    {
        Hid hid = _heap.Allocate(bytes.Length, nearBlock);
        bytes.CopyTo(_heap.Write(hid));
        return hid;
    }

    // A node's first key changed: the records above that name it by that key follow, as far up as the node is the first child.
    private void SetFirstKey(Path p, int depth, uint key)
    {
        for (int d = depth - 1; d >= 0; d--)
        {
            (Hid node, int entry) = p.Index[d];
            BinaryPrimitives.WriteUInt32LittleEndian(_heap.Write(node)[(entry * Record)..], key);
            if (entry != 0)
            {
                break;
            }
        }
    }

    // Adds a record for a new child to the index node at depth - 1, just after the record taken on the way down.
    private void InsertIndexRecord(Path p, int depth, uint key, Hid child)
    {
        if (depth == 0)
        {
            // The root itself was split: a new root over the two halves.
            Hid old = Root;
            byte[] root = new byte[2 * Record];
            BinaryPrimitives.WriteUInt32LittleEndian(root, FirstKey(old, Levels));
            BinaryPrimitives.WriteUInt32LittleEndian(root.AsSpan(4), old.Value);
            BinaryPrimitives.WriteUInt32LittleEndian(root.AsSpan(8), key);
            BinaryPrimitives.WriteUInt32LittleEndian(root.AsSpan(12), child.Value);
            SetRoot(NewNode(root, old.BlockIndex), Levels + 1);
            return;
        }

        (Hid node, int entry) = p.Index[depth - 1];
        ReadOnlySpan<byte> n = _heap.Read(node);
        byte[] bytes = new byte[n.Length + Record];
        int after = (entry + 1) * Record;
        n[..after].CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(after), key);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(after + 4), child.Value);
        n[after..].CopyTo(bytes.AsSpan(after + Record));
        if (bytes.Length <= MaxNode)
        {
            Reallocate(p, depth - 1, node, bytes);
            return;
        }

        int half = bytes.Length / Record / 2 * Record;
        Hid left = Reallocate(p, depth - 1, node, bytes.AsSpan(0, half).ToArray());
        Hid right = NewNode(bytes.AsSpan(half), left.BlockIndex);
        InsertIndexRecord(p, depth - 1, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(half)), right);
    }

    private uint FirstKey(Hid node, int level) => BinaryPrimitives.ReadUInt32LittleEndian(_heap.Read(node));

    // The child at depth `depth` is gone (already freed): its record leaves the index node above it, and an index node left with no records goes too.
    private void RemoveIndexRecord(Path p, int depth)
    {
        if (depth == 0)
        {
            SetRoot(default, 0);
            return;
        }

        (Hid node, int entry) = p.Index[depth - 1];
        ReadOnlySpan<byte> n = _heap.Read(node);
        if (n.Length == Record)
        {
            _heap.Free(node);
            RemoveIndexRecord(p, depth - 1);
            return;
        }

        byte[] bytes = new byte[n.Length - Record];
        n[..(entry * Record)].CopyTo(bytes);
        n[((entry + 1) * Record)..].CopyTo(bytes.AsSpan(entry * Record));
        Reallocate(p, depth - 1, node, bytes);
        if (entry == 0)
        {
            // The records above name this node by its first key, which is now the next child's.
            SetFirstKey(p, depth - 1, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        }

        // A root with a single record is only a pointer: the tree is one level shorter.
        if (depth - 1 == 0 && bytes.Length == Record)
        {
            Hid only = new(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
            _heap.Free(Root);
            SetRoot(only, Levels - 1);
        }
    }
}
