using System.Buffers;
using System.Buffers.Binary;
using RecursiveAI.OneMail.Pst.Ndb;
using RecursiveAI.OneMail.Recovery;

namespace RecursiveAI.OneMail.Pst.Recovery;

/// <summary>
/// Walks the blocks a node is made of: its data tree (MS-PST 2.2.2.8.3.2, XBLOCK and XXBLOCK) and its subnode tree (2.2.2.8.3.3, SLBLOCK and
/// SIBLOCK), with every data block of both read and checked. Records where each block lies and which ones are not there.
/// </summary>
internal sealed class PstNodeWalker(NdbFile file, int maxBlocks, bool readLeaves = true)
{
    private readonly HashSet<ulong> _seen = [];
    private int _count;

    /// <summary>Where the blocks that were read lie in the file.</summary>
    public List<ByteRange> Ranges { get; } = [];

    /// <summary>BIDs the node needs that are not in the index.</summary>
    public List<Bid> Missing { get; } = [];

    /// <summary>Where the blocks that failed a check lie.</summary>
    public List<ByteRange> Lost { get; } = [];

    /// <summary>BIDs whose block is in the index but failed a check when read.</summary>
    public List<Bid> Damaged { get; } = [];

    /// <summary>Every BID visited, found or not.</summary>
    public IReadOnlyCollection<ulong> Visited => _seen;

    /// <summary>True when the walk stopped at the block limit.</summary>
    public bool Truncated { get; private set; }

    public void Walk(NodeEntry node)
    {
        byte[] buf = ArrayPool<byte>.Shared.Rent(file.MaxBlockDataSize);
        try
        {
            Data(node.Data, buf, 0);
            Sub(node.Sub, buf, 0);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    // Returns the block's data length, or -1 when it is not usable.
    private int Visit(Bid bid, byte[] buf, out bool found)
    {
        found = false;
        if (bid.IsNull)
        {
            return -1;
        }

        if (!_seen.Add(bid.Normalized.Value))
        {
            return -1;
        }

        if (++_count > maxBlocks)
        {
            Truncated = true;
            return -1;
        }

        // Finding the blocks a node uses, without reading the data blocks themselves, is enough to know which blocks are accounted for.
        if (!readLeaves && !bid.IsInternal)
        {
            found = true;
            return -1;
        }

        if (!file.Blocks.TryGet(bid, out BlockEntry e))
        {
            Missing.Add(bid);
            return -1;
        }

        if (!file.TryReadBlock(e, buf, out int len, out NdbCheck check) || !(check.SignatureValid && check.CrcValid))
        {
            Damaged.Add(bid);
            Lost.Add(new ByteRange((long)e.Block.Ib, file.Layout.BlockTotal(e.Size)));
            return -1;
        }

        found = true;
        Ranges.Add(new ByteRange((long)e.Block.Ib, file.Layout.BlockTotal(e.Size)));
        return len;
    }

    private void Data(Bid bid, byte[] buf, int depth)
    {
        int len = Visit(bid, buf, out _);
        if (len < 0 || !bid.IsInternal || depth > 2 || len < 8 || buf[0] != 0x01)
        {
            return;
        }

        int cEnt = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(2));
        int idSize = file.Layout.IdSize;
        if (8L + ((long)cEnt * idSize) > len)
        {
            return;
        }

        // The children are read after this block's entries are copied out, since the walk reuses the buffer.
        var children = new Bid[cEnt];
        for (int i = 0; i < cEnt; i++)
        {
            children[i] = new Bid(idSize == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(8 + (i * 8))) : BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(8 + (i * 4))));
        }

        foreach (Bid c in children)
        {
            Data(c, buf, depth + 1);
        }
    }

    private void Sub(Bid bid, byte[] buf, int depth)
    {
        int len = Visit(bid, buf, out _);
        bool wide = file.Layout.IdSize == 8;
        int header = wide ? 8 : 4;
        if (len < header || buf[0] != 0x02 || buf[1] > 1 || depth > 8)
        {
            return;
        }

        int level = buf[1];
        int cEnt = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(2));
        int entrySize = level == 0 ? (wide ? 24 : 12) : (wide ? 16 : 8);
        if (header + ((long)cEnt * entrySize) > len)
        {
            return;
        }

        var data = new List<Bid>(cEnt);
        var sub = new List<Bid>(cEnt);
        for (int i = 0; i < cEnt; i++)
        {
            ReadOnlySpan<byte> e = buf.AsSpan(header + (i * entrySize), entrySize);
            if (level == 0)
            {
                data.Add(new Bid(wide ? BinaryPrimitives.ReadUInt64LittleEndian(e[8..]) : BinaryPrimitives.ReadUInt32LittleEndian(e[4..])));
                sub.Add(new Bid(wide ? BinaryPrimitives.ReadUInt64LittleEndian(e[16..]) : BinaryPrimitives.ReadUInt32LittleEndian(e[8..])));
            }
            else
            {
                sub.Add(new Bid(wide ? BinaryPrimitives.ReadUInt64LittleEndian(e[8..]) : BinaryPrimitives.ReadUInt32LittleEndian(e[4..])));
            }
        }

        foreach (Bid d in data)
        {
            Data(d, buf, 0);
        }

        foreach (Bid s in sub)
        {
            Sub(s, buf, depth + 1);
        }
    }
}
