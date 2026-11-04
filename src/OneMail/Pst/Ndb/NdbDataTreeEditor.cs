using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace RecursiveAI.OneMail.Pst.Ndb;

/// <summary>
/// Changes a few leaves of an existing data tree (MS-PST 2.2.2.8.3.2) and writes the result as a new tree that shares everything it did not change with the old one.
/// A leaf that is replaced or added becomes a new data block; an XBLOCK none of whose leaves changed is named again as it is; any other XBLOCK is written anew over the
/// old leaves it keeps. Every block the new tree names that the old one also names gets a reference (<see cref="NdbEditor.AddReference"/>), so that releasing the old root,
/// which the caller does by replacing the node that held it, frees exactly the blocks the new tree no longer uses.
/// </summary>
internal sealed class NdbDataTreeEditor
{
    private readonly NdbEditor _editor;
    private readonly NdbDataTree? _old;
    private readonly List<Bid> _bids = [];
    private readonly List<int> _sizes = [];
    private readonly List<byte[]?> _pending = [];   // the data of a leaf that is new or replaced; null for one kept as it was
    private int _keptLeaves;                        // leaves of the old tree below this index are in the same place
    private bool _built;

    /// <summary>Starts from an existing tree; <paramref name="old"/> is null for a node with no data yet.</summary>
    public NdbDataTreeEditor(NdbEditor editor, NdbDataTree? old)
    {
        ArgumentNullException.ThrowIfNull(editor);
        _editor = editor;
        _old = old;
        if (old is not null)
        {
            for (int i = 0; i < old.LeafCount; i++)
            {
                _bids.Add(old.GetLeaf(i).Bid);
                _sizes.Add(old.GetLeafSize(i));
                _pending.Add(null);
            }
        }

        _keptLeaves = _bids.Count;
    }

    /// <summary>The leaves the new tree has.</summary>
    public int LeafCount => _bids.Count;

    /// <summary>The data length of leaf <paramref name="index"/> as the new tree has it.</summary>
    public int LeafSize(int index) => _sizes[index];

    /// <summary>Replaces the data of a leaf.</summary>
    public void Replace(int index, byte[] data)
    {
        Check(data);
        _pending[index] = data;
        _sizes[index] = data.Length;
    }

    /// <summary>Adds a leaf at the end.</summary>
    public void Append(byte[] data)
    {
        Check(data);
        _bids.Add(default);
        _sizes.Add(data.Length);
        _pending.Add(data);
    }

    /// <summary>Drops the leaves from <paramref name="count"/> on.</summary>
    public void Truncate(int count)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _bids.Count);
        _bids.RemoveRange(count, _bids.Count - count);
        _sizes.RemoveRange(count, _sizes.Count - count);
        _pending.RemoveRange(count, _pending.Count - count);
        _keptLeaves = Math.Min(_keptLeaves, count);
    }

    private static void Check(byte[] data)
    {
        if (data.Length is 0 or > NdbBlockBuilder.MaxBlockData)
        {
            throw new ArgumentException($"a data block holds 1 to {NdbBlockBuilder.MaxBlockData} bytes, not {data.Length}", nameof(data));
        }
    }

    /// <summary>Writes the new blocks and returns the new root.</summary>
    /// <returns>The root BID (null for a tree with no leaves) and the data length. The root carries the reference the node that names it will hold.</returns>
    public DataTreeResult Build()
    {
        if (_built)
        {
            throw new InvalidOperationException("the tree is already built");
        }

        _built = true;
        int n = _bids.Count;
        if (n == 0)
        {
            return DataTreeResult.Empty;
        }

        long total = 0;
        for (int i = 0; i < n; i++)
        {
            total += _sizes[i];
        }

        if (total > NdbBlockBuilder.MaxDataTreeBytes)
        {
            throw new InvalidOperationException("the data tree would exceed what an XXBLOCK over XBLOCKs can describe");
        }

        if (n == 1)
        {
            // A single block is its own tree.
            Bid only = _pending[0] is { } d ? _editor.Blocks.WriteDataBlock(d) : Keep(_bids[0]);
            return new DataTreeResult(only, total, 1);
        }

        int cap = NdbBlockBuilder.XBlockCapacity;
        int groups = (n + cap - 1) / cap;
        var xbids = new Bid[groups];
        for (int g = 0; g < groups; g++)
        {
            int first = g * cap;
            int count = Math.Min(cap, n - first);
            Bid? reuse = ReusableXBlock(first, count);
            if (reuse is { } x)
            {
                xbids[g] = Keep(x);
                continue;
            }

            long bytes = 0;
            for (int i = first; i < first + count; i++)
            {
                _bids[i] = _pending[i] is { } data ? _editor.Blocks.WriteDataBlock(data) : Keep(_bids[i]);
                bytes += _sizes[i];
            }

            // MS-PST 2.2.2.8.3.2.1: btype 1, cLevel 1, cEnt, lcbTotal (the bytes under this block), then the leaf BIDs.
            byte[] xb = new byte[8 + (count * 8)];
            xb[0] = 0x01;
            xb[1] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(xb.AsSpan(2), (ushort)count);
            BinaryPrimitives.WriteUInt32LittleEndian(xb.AsSpan(4), (uint)bytes);
            for (int i = 0; i < count; i++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(xb.AsSpan(8 + (i * 8)), _bids[first + i].Value);
            }

            xbids[g] = _editor.Blocks.EmitInternal(xb);
        }

        if (groups == 1)
        {
            return new DataTreeResult(xbids[0], total, n);
        }

        // MS-PST 2.2.2.8.3.2.2: btype 1, cLevel 2, cEnt, lcbTotal for the whole tree, then the XBLOCK BIDs.
        byte[] xx = new byte[8 + (groups * 8)];
        xx[0] = 0x01;
        xx[1] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(xx.AsSpan(2), (ushort)groups);
        BinaryPrimitives.WriteUInt32LittleEndian(xx.AsSpan(4), (uint)total);
        for (int g = 0; g < groups; g++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(xx.AsSpan(8 + (g * 8)), xbids[g].Value);
        }

        return new DataTreeResult(_editor.Blocks.EmitInternal(xx), total, n);
    }

    // The old XBLOCK that lists exactly the leaves [first, first + count), none of them replaced, so it can be named again.
    private Bid? ReusableXBlock(int first, int count)
    {
        if (_old is null || first + count > _keptLeaves)
        {
            return null;
        }

        for (int i = first; i < first + count; i++)
        {
            if (_pending[i] is not null)
            {
                return null;
            }
        }

        foreach ((Bid bid, int firstLeaf, int leaves, int entries) in _old.XBlocks)
        {
            if (firstLeaf == first && leaves == count && entries == count)
            {
                return bid;
            }
        }

        return null;
    }

    // The new tree names a block the old one names too.
    private Bid Keep(Bid bid)
    {
        _editor.AddReference(bid);
        return bid;
    }
}
