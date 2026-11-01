using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace RecursiveAI.OneMail.Pst.Ndb;

/// <summary>
/// One block ready to be placed in a file: its BID, the block bytes (data, padding and trailer) and everything the block
/// B-tree entry needs. The only field that depends on where the block lands is the trailer's signature, which
/// <see cref="WriteTo"/> fills in for the offset the caller chose.
/// </summary>
/// <remarks>
/// A block is immutable after creation. Its image is at most 8,192 bytes, and a builder hands it to its
/// <see cref="INdbBlockSink"/> once, so a sink that places it and lets go keeps nothing alive.
/// </remarks>
public sealed class NdbBlock
{
    private readonly byte[] _image;

    internal NdbBlock(Bid bid, byte[] image, int dataLength)
    {
        Bid = bid;
        _image = image;
        DataLength = dataLength;
    }

    /// <summary>The block's BID.</summary>
    public Bid Bid { get; }

    /// <summary>True for an XBLOCK, XXBLOCK, SLBLOCK or SIBLOCK (BID bit 1).</summary>
    public bool IsInternal => Bid.IsInternal;

    /// <summary>cb: the data bytes in the block, as stored (after any encoding), without padding or trailer. This is the size for the block's BBTENTRY.</summary>
    public int DataLength { get; }

    /// <summary>The bytes the block occupies in the file: data, padding and trailer, a multiple of 64.</summary>
    public int TotalLength => _image.Length;

    /// <summary>
    /// The suggested cRef for the block's BBTENTRY: 2, which is the BBT entry plus the one reference from the node, subnode entry or data
    /// tree that names it (MS-PST 2.2.2.7.7.3.1). A block that is shared by more nodes needs a larger count; the caller owns that.
    /// </summary>
    public int RefCount { get; } = 2;

    /// <summary>The block's bytes with a zero signature: the data as stored, the padding and the trailer.</summary>
    public ReadOnlySpan<byte> Image => _image;

    /// <summary>Writes the block as it appears in a file when placed at <paramref name="offset"/>.</summary>
    /// <param name="destination">Receives <see cref="TotalLength"/> bytes; may be longer.</param>
    /// <param name="offset">ib: the absolute file offset of the block. It goes into wSig (MS-PST 5.5), nothing else.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than <see cref="TotalLength"/>.</exception>
    public void WriteTo(Span<byte> destination, ulong offset)
    {
        if (destination.Length < _image.Length)
        {
            throw new ArgumentException($"destination must hold {_image.Length} bytes", nameof(destination));
        }

        _image.CopyTo(destination);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[(_image.Length - NdbBlockBuilder.TrailerSize + 2)..], NdbChecksums.Signature(offset, Bid.Value));
    }
}

/// <summary>Receives the blocks a <see cref="NdbBlockBuilder"/> makes, in the order they are made.</summary>
public interface INdbBlockSink
{
    /// <summary>Takes a finished block. The builder keeps no reference to it afterwards.</summary>
    /// <param name="block">The block.</param>
    void Place(NdbBlock block);
}

/// <summary>Hands out block BIDs (MS-PST 2.2.2.2). The PST builder owns the counter, because the header records the next value.</summary>
public interface IBidAllocator
{
    /// <summary>Returns a BID that has not been used in the file.</summary>
    /// <param name="isInternal">True for a block that holds NDB metadata (XBLOCK, XXBLOCK, SLBLOCK, SIBLOCK): bit 1 of the BID is set.</param>
    /// <returns>The BID.</returns>
    Bid NextBlockBid(bool isInternal);
}

/// <summary>
/// A counter of block BIDs: the index starts where the caller says and rises by one for every block; the BID is the index
/// shifted left by two, with bit 1 set for internal blocks (MS-PST 2.2.2.2).
/// </summary>
public sealed class SequentialBidAllocator : IBidAllocator
{
    private ulong _nextIndex;

    /// <summary>Creates a counter.</summary>
    /// <param name="firstIndex">bidIndex of the first block. Default 1: zero would give the null BID.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="firstIndex"/> is zero or too large to shift.</exception>
    public SequentialBidAllocator(ulong firstIndex = 1)
    {
        ArgumentOutOfRangeException.ThrowIfZero(firstIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(firstIndex, ulong.MaxValue >> 2);
        _nextIndex = firstIndex;
    }

    /// <summary>The bidIndex the next block gets (what the header's bidNextB would record, shifted back).</summary>
    public ulong NextIndex => _nextIndex;

    /// <inheritdoc/>
    public Bid NextBlockBid(bool isInternal)
    {
        if (_nextIndex > ulong.MaxValue >> 2)
        {
            throw new InvalidOperationException("the BID space is used up");
        }

        return new Bid((_nextIndex++ << 2) | (isInternal ? 2UL : 0));
    }
}

/// <summary>
/// Builds the blocks of a Unicode file: data blocks with their trailers (MS-PST 2.2.2.8.1), data trees (XBLOCK and XXBLOCK,
/// 2.2.2.8.3.2) for streams longer than one block, and subnode trees (SLBLOCK and SIBLOCK, 2.2.2.8.3.3).
/// It decides nothing about placement: every block goes to the <see cref="INdbBlockSink"/> with a BID from the
/// <see cref="IBidAllocator"/>, and the caller puts it in the file and in the block B-tree.
/// </summary>
/// <remarks>
/// <para>Output is what <see cref="NdbFile"/> reads: the blocks of a stream read back byte for byte, and checks on every trailer pass.
/// Only files with 512-byte pages are written; ANSI and 4 KiB files are read-only.</para>
/// <para>Working set: one block image at a time, plus eight bytes per data block of the stream being written (the BIDs the XBLOCK
/// will list). A stream is never held whole.</para>
/// <para>Not thread-safe.</para>
/// </remarks>
public sealed class NdbBlockBuilder
{
    /// <summary>The size of a Unicode BLOCKTRAILER (cb, wSig, dwCRC, bid), 16 bytes.</summary>
    public const int TrailerSize = 16;

    /// <summary>The most data bytes one block holds: 8,192 minus the trailer.</summary>
    public const int MaxBlockData = 8192 - TrailerSize;

    /// <summary>The most BIDs an XBLOCK lists: its data area less the 8-byte header, over 8-byte BIDs (MS-PST 2.2.2.8.3.2.1).</summary>
    public const int XBlockCapacity = (MaxBlockData - 8) / 8;

    /// <summary>The most data blocks one data tree holds: one XXBLOCK over full XBLOCKs.</summary>
    public const long MaxDataTreeBlocks = (long)XBlockCapacity * XBlockCapacity;

    /// <summary>The most bytes one data tree can describe: lcbTotal is 32 bits.</summary>
    public const long MaxDataTreeBytes = uint.MaxValue;

    /// <summary>The most subnode entries in one SLBLOCK: its data area less the 8-byte header, over 24-byte SLENTRYs (MS-PST 2.2.2.8.3.3.2.1).</summary>
    public const int SlBlockCapacity = (MaxBlockData - 8) / 24;

    /// <summary>The most child SLBLOCKs in one SIBLOCK: its data area less the 8-byte header, over 16-byte SIENTRYs.</summary>
    public const int SiBlockCapacity = (MaxBlockData - 8) / 16;

    /// <summary>The most subnode entries one node can have: a full SIBLOCK of full SLBLOCKs.</summary>
    public const int MaxSubnodes = SlBlockCapacity * SiBlockCapacity;

    private readonly IBidAllocator _bids;
    private readonly INdbBlockSink _sink;

    /// <summary>Creates a builder.</summary>
    /// <param name="bids">Where BIDs come from.</param>
    /// <param name="sink">Where finished blocks go.</param>
    /// <param name="encryption">How data blocks are encoded: <see cref="NdbEncryption.None"/> (default) or <see cref="NdbEncryption.Permute"/> or <see cref="NdbEncryption.Cyclic"/>. Internal blocks are never encoded. This must match the file header's bCryptMethod.</param>
    /// <exception cref="ArgumentNullException"><paramref name="bids"/> or <paramref name="sink"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="encryption"/> is not one of the three encodings that can be written (Windows Information Protection is read-only).</exception>
    public NdbBlockBuilder(IBidAllocator bids, INdbBlockSink sink, NdbEncryption encryption = NdbEncryption.None)
    {
        ArgumentNullException.ThrowIfNull(bids);
        ArgumentNullException.ThrowIfNull(sink);
        if (encryption is not (NdbEncryption.None or NdbEncryption.Permute or NdbEncryption.Cyclic))
        {
            throw new ArgumentException("only none, permute and cyclic encodings can be written", nameof(encryption));
        }

        _bids = bids;
        _sink = sink;
        Encryption = encryption;
    }

    /// <summary>The encoding applied to data blocks.</summary>
    public NdbEncryption Encryption { get; }

    /// <summary>Makes one block. <paramref name="data"/> is the data as it should be stored (already encoded when it is a data block).</summary>
    private Bid Emit(ReadOnlySpan<byte> data, bool isInternal) => EmitWithBid(data, _bids.NextBlockBid(isInternal));

    /// <summary>Writes one data block (MS-PST 2.2.2.8.1).</summary>
    /// <param name="data">1 to <see cref="MaxBlockData"/> bytes of node data, unencoded; the builder applies the file's encoding.</param>
    /// <returns>The block's BID. The block's size for the BBT is <c>data.Length</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="data"/> is empty or longer than <see cref="MaxBlockData"/>.</exception>
    public Bid WriteDataBlock(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || data.Length > MaxBlockData)
        {
            throw new ArgumentException($"a data block holds 1 to {MaxBlockData} bytes, not {data.Length}", nameof(data));
        }

        if (Encryption == NdbEncryption.None)
        {
            return Emit(data, isInternal: false);
        }

        // The cyclic key is the block's BID, so the BID is taken first. Encode into a scratch copy, then emit with that BID.
        byte[] rented = ArrayPool<byte>.Shared.Rent(data.Length);
        try
        {
            Span<byte> encoded = rented.AsSpan(0, data.Length);
            data.CopyTo(encoded);
            Bid bid = _bids.NextBlockBid(isInternal: false);
            if (Encryption == NdbEncryption.Permute)
            {
                NdbChecksums.Permute(encoded, encode: true);
            }
            else
            {
                // MS-PST 5.2: the key is the low 32 bits of the block's BID.
                NdbChecksums.Cyclic(encoded, (uint)bid.Value);
            }

            return EmitWithBid(encoded, bid);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private Bid EmitWithBid(ReadOnlySpan<byte> data, Bid bid)
    {
        // MS-PST 2.2.2.8: data, then padding so that the block ends on a 64-byte boundary with the trailer last.
        // wSig (trailer bytes 2..3) depends on the offset and is filled in by NdbBlock.WriteTo.
        int total = (data.Length + TrailerSize + 63) / 64 * 64;
        byte[] image = new byte[total];
        data.CopyTo(image);
        Span<byte> trailer = image.AsSpan(total - TrailerSize);
        BinaryPrimitives.WriteUInt16LittleEndian(trailer, (ushort)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[4..], NdbChecksums.Crc32(data));
        BinaryPrimitives.WriteUInt64LittleEndian(trailer[8..], bid.Value);
        _sink.Place(new NdbBlock(bid, image, data.Length));
        return bid;
    }

    /// <summary>Starts a data tree whose leaves the caller supplies one block at a time (the blocks of a heap, or of a table's row matrix).</summary>
    /// <returns>The writer. Add every leaf, then call <see cref="DataTreeWriter.Complete"/>.</returns>
    public DataTreeWriter BeginDataTree() => new(this);

    /// <summary>Writes a byte string as a data tree of full blocks: one data block when it fits, else an XBLOCK, else an XXBLOCK.</summary>
    /// <param name="data">The bytes. An empty span writes nothing and gives <see cref="DataTreeResult.Empty"/>: a node without data has a null BID.</param>
    /// <returns>The root BID and the length.</returns>
    public DataTreeResult WriteData(ReadOnlySpan<byte> data)
    {
        DataTreeWriter tree = BeginDataTree();
        for (int o = 0; o < data.Length; o += MaxBlockData)
        {
            tree.AddBlock(data.Slice(o, Math.Min(MaxBlockData, data.Length - o)));
        }

        return tree.Complete();
    }

    /// <summary>Writes the next <paramref name="length"/> bytes of a stream as a data tree, reading one block at a time.</summary>
    /// <param name="source">A readable stream positioned at the first byte.</param>
    /// <param name="length">The number of bytes to copy. Declared up front so the limit is checked before anything is read.</param>
    /// <returns>The root BID and the length.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative or above <see cref="MaxDataTreeBytes"/>.</exception>
    /// <exception cref="EndOfStreamException">The stream ends before <paramref name="length"/> bytes.</exception>
    public DataTreeResult WriteData(Stream source, long length)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, MaxDataTreeBytes);
        DataTreeWriter tree = BeginDataTree();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(MaxBlockData);
        try
        {
            for (long left = length; left > 0;)
            {
                int want = (int)Math.Min(MaxBlockData, left);
                source.ReadExactly(buffer.AsSpan(0, want));
                tree.AddBlock(buffer.AsSpan(0, want));
                left -= want;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return tree.Complete();
    }

    /// <summary>
    /// Writes a node's subnode tree: one SLBLOCK, or when there are more entries than one holds, an SIBLOCK over SLBLOCKs
    /// (MS-PST 2.2.2.8.3.3).
    /// </summary>
    /// <param name="entries">The subnodes. Their NIDs must be distinct; they are written in NID order, which is what lookups assume.</param>
    /// <returns>The BID for the node's bidSub, or the null BID when there are no entries.</returns>
    /// <exception cref="ArgumentException">Two entries share a NID, or there are more than <see cref="MaxSubnodes"/>.</exception>
    public Bid WriteSubnodes(IReadOnlyList<SubnodeEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return default;
        }

        if (entries.Count > MaxSubnodes)
        {
            throw new ArgumentException($"a node has at most {MaxSubnodes} subnodes, not {entries.Count}", nameof(entries));
        }

        SubnodeEntry[] sorted = [.. entries];
        Array.Sort(sorted, static (a, b) => a.Nid.Value.CompareTo(b.Nid.Value));
        for (int i = 1; i < sorted.Length; i++)
        {
            if (sorted[i].Nid == sorted[i - 1].Nid)
            {
                throw new ArgumentException($"subnode {sorted[i].Nid} is listed twice", nameof(entries));
            }
        }

        if (sorted.Length <= SlBlockCapacity)
        {
            return WriteSlBlock(sorted);
        }

        // MS-PST 2.2.2.8.3.3.2: an SIBLOCK's SIENTRYs hold the smallest NID of each child SLBLOCK and its BID.
        int children = (sorted.Length + SlBlockCapacity - 1) / SlBlockCapacity;
        var keys = new Nid[children];
        var bids = new Bid[children];
        for (int c = 0; c < children; c++)
        {
            int start = c * SlBlockCapacity;
            int count = Math.Min(SlBlockCapacity, sorted.Length - start);
            keys[c] = sorted[start].Nid;
            bids[c] = WriteSlBlock(sorted.AsSpan(start, count));
        }

        byte[] si = new byte[8 + (children * 16)];
        si[0] = 0x02;   // btype: subnode block
        si[1] = 1;      // cLevel: SIBLOCK
        BinaryPrimitives.WriteUInt16LittleEndian(si.AsSpan(2), (ushort)children);
        for (int c = 0; c < children; c++)
        {
            Span<byte> e = si.AsSpan(8 + (c * 16));
            BinaryPrimitives.WriteUInt64LittleEndian(e, keys[c].Value);
            BinaryPrimitives.WriteUInt64LittleEndian(e[8..], bids[c].Value);
        }

        return Emit(si, isInternal: true);
    }

    private Bid WriteSlBlock(ReadOnlySpan<SubnodeEntry> entries)
    {
        byte[] sl = new byte[8 + (entries.Length * 24)];
        sl[0] = 0x02;   // btype: subnode block
        sl[1] = 0;      // cLevel: SLBLOCK
        BinaryPrimitives.WriteUInt16LittleEndian(sl.AsSpan(2), (ushort)entries.Length);
        for (int i = 0; i < entries.Length; i++)
        {
            Span<byte> e = sl.AsSpan(8 + (i * 24));
            // SLENTRY.nid is 8 bytes in a Unicode file: the 32-bit NID and four zero bytes.
            BinaryPrimitives.WriteUInt64LittleEndian(e, entries[i].Nid.Value);
            BinaryPrimitives.WriteUInt64LittleEndian(e[8..], entries[i].Data.Value);
            BinaryPrimitives.WriteUInt64LittleEndian(e[16..], entries[i].Sub.Value);
        }

        return Emit(sl, isInternal: true);
    }

    internal Bid EmitInternal(ReadOnlySpan<byte> data) => Emit(data, isInternal: true);

    /// <summary>
    /// Builds a data tree leaf by leaf: the leaves are blocks the caller sized (heap blocks, row-matrix blocks), listed by an XBLOCK, or by an
    /// XXBLOCK over XBLOCKs once there are more than one XBLOCK holds. Memory is eight bytes per leaf.
    /// </summary>
    public sealed class DataTreeWriter
    {
        private readonly NdbBlockBuilder _owner;
        private readonly List<ulong> _leaves = [];          // BIDs of the leaves of the XBLOCK being filled
        private long _leavesBytes;                          // their total size
        private readonly List<(ulong Bid, long Bytes)> _xblocks = [];   // finished XBLOCKs, once there is more than one
        private long _total;
        private long _count;
        private bool _done;

        internal DataTreeWriter(NdbBlockBuilder owner) => _owner = owner;

        /// <summary>The number of data blocks added.</summary>
        public long BlockCount => _count;

        /// <summary>The bytes added.</summary>
        public long Length => _total;

        /// <summary>Adds the next leaf.</summary>
        /// <param name="data">1 to <see cref="MaxBlockData"/> bytes, unencoded.</param>
        /// <exception cref="InvalidOperationException">The tree is complete, or it would hold more than <see cref="MaxDataTreeBlocks"/> blocks or <see cref="MaxDataTreeBytes"/> bytes.</exception>
        /// <exception cref="ArgumentException">The block is empty or too long.</exception>
        public void AddBlock(ReadOnlySpan<byte> data)
        {
            if (_done)
            {
                throw new InvalidOperationException("the data tree is already complete");
            }

            if (_count >= MaxDataTreeBlocks || _total + data.Length > MaxDataTreeBytes)
            {
                throw new InvalidOperationException("the data tree would exceed what an XXBLOCK over XBLOCKs can describe");
            }

            if (_leaves.Count == XBlockCapacity)
            {
                FlushXBlock();
            }

            _leaves.Add(_owner.WriteDataBlock(data).Value);
            _leavesBytes += data.Length;
            _total += data.Length;
            _count++;
        }

        private void FlushXBlock()
        {
            // MS-PST 2.2.2.8.3.2.1: btype 1, cLevel 1, cEnt, lcbTotal (the bytes under this block), then the leaf BIDs.
            byte[] x = new byte[8 + (_leaves.Count * 8)];
            x[0] = 0x01;
            x[1] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(x.AsSpan(2), (ushort)_leaves.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(x.AsSpan(4), (uint)_leavesBytes);
            for (int i = 0; i < _leaves.Count; i++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(x.AsSpan(8 + (i * 8)), _leaves[i]);
            }

            _xblocks.Add((_owner.EmitInternal(x).Value, _leavesBytes));
            _leaves.Clear();
            _leavesBytes = 0;
        }

        /// <summary>Writes the tree's internal blocks.</summary>
        /// <returns>The BID for the node's bidData (the data block itself when there is only one) and the total length.</returns>
        public DataTreeResult Complete()
        {
            if (_done)
            {
                throw new InvalidOperationException("the data tree is already complete");
            }

            _done = true;
            if (_count == 0)
            {
                return DataTreeResult.Empty;
            }

            if (_count == 1)
            {
                // A single block is its own tree: the reader treats a non-internal root as the only leaf.
                return new DataTreeResult(new Bid(_leaves[0]), _total, 1);
            }

            if (_xblocks.Count == 0)
            {
                // The XBLOCK is the root; its lcbTotal is the whole stream.
                FlushXBlock();
                return new DataTreeResult(new Bid(_xblocks[0].Bid), _total, _count);
            }

            FlushXBlock();
            // MS-PST 2.2.2.8.3.2.2: btype 1, cLevel 2, cEnt, lcbTotal for the whole tree, then the XBLOCK BIDs.
            byte[] xx = new byte[8 + (_xblocks.Count * 8)];
            xx[0] = 0x01;
            xx[1] = 2;
            BinaryPrimitives.WriteUInt16LittleEndian(xx.AsSpan(2), (ushort)_xblocks.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(xx.AsSpan(4), (uint)_total);
            for (int i = 0; i < _xblocks.Count; i++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(xx.AsSpan(8 + (i * 8)), _xblocks[i].Bid);
            }

            return new DataTreeResult(_owner.EmitInternal(xx), _total, _count);
        }
    }
}

/// <summary>What a finished data tree is: the BID to put in an NBT entry or subnode entry, and how much it holds.</summary>
/// <param name="Root">The BID of the data block, XBLOCK or XXBLOCK at the top; the null BID for no data.</param>
/// <param name="Length">The data bytes in the tree.</param>
/// <param name="BlockCount">The data blocks in the tree.</param>
public readonly record struct DataTreeResult(Bid Root, long Length, long BlockCount)
{
    /// <summary>No data: no block, the null BID.</summary>
    public static DataTreeResult Empty => default;
}
