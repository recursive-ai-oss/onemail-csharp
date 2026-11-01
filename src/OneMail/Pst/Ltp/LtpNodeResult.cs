using System;
using System.Collections.Generic;
using System.IO;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Pst.Ltp;

/// <summary>
/// What a property-context or table-context builder leaves behind once its blocks are written: the BID of the heap's data tree, for the
/// node's bidData, and the subnodes the builder had to create (values and row matrices too large for the heap).
/// </summary>
public sealed class LtpNodeResult
{
    internal LtpNodeResult(Bid data, long dataLength, int heapBlocks, List<SubnodeEntry> subnodes)
    {
        Data = data;
        DataLength = dataLength;
        HeapBlockCount = heapBlocks;
        Subnodes = subnodes;
    }

    /// <summary>The BID of the heap's data block or data tree: the node's bidData.</summary>
    public Bid Data { get; }

    /// <summary>The bytes in the heap's data blocks.</summary>
    public long DataLength { get; }

    /// <summary>The number of heap blocks (data blocks of the node's data tree).</summary>
    public int HeapBlockCount { get; }

    /// <summary>The subnodes the builder created, in the order it made them. Their data is already written; add them to the node's subnode tree.</summary>
    public IReadOnlyList<SubnodeEntry> Subnodes { get; }

    /// <summary>Writes the node's subnode tree from this result's subnodes plus any the caller adds (a recipient table, an attachment, say).</summary>
    /// <param name="blocks">The block builder to write the SLBLOCK or SIBLOCKs with.</param>
    /// <param name="additional">More subnodes. Their NIDs must differ from the ones in <see cref="Subnodes"/>.</param>
    /// <returns>The BID for the node's bidSub; null when there are no subnodes at all.</returns>
    /// <exception cref="ArgumentException">Two subnodes share a NID, or there are too many.</exception>
    public Bid WriteSubnodes(NdbBlockBuilder blocks, IEnumerable<SubnodeEntry>? additional = null)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        if (additional is null)
        {
            return blocks.WriteSubnodes(Subnodes);
        }

        var all = new List<SubnodeEntry>(Subnodes);
        all.AddRange(additional);
        return blocks.WriteSubnodes(all);
    }

    /// <summary>
    /// Writes the heap blocks as a data tree: each heap block is one data block, so the block indexes in the heap's HIDs are the leaf
    /// positions the reader uses.
    /// </summary>
    internal static LtpNodeResult WriteHeap(NdbBlockBuilder blocks, HeapNodeBuilder heap, List<SubnodeEntry> subnodes)
    {
        IReadOnlyList<ReadOnlyMemory<byte>> images = heap.Finish();
        NdbBlockBuilder.DataTreeWriter tree = blocks.BeginDataTree();
        foreach (ReadOnlyMemory<byte> image in images)
        {
            tree.AddBlock(image.Span);
        }

        DataTreeResult r = tree.Complete();
        return new LtpNodeResult(r.Root, r.Length, images.Count, subnodes);
    }
}

/// <summary>Places the variable-size values of a property or table context: in the heap when they fit one allocation, else in a subnode.</summary>
internal sealed class LtpValuePlacer
{
    private readonly NdbBlockBuilder _blocks;
    private readonly Func<Nid>? _nextNid;
    private uint _counter;

    public LtpValuePlacer(NdbBlockBuilder blocks, Func<Nid>? nextSubnodeNid)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        _blocks = blocks;
        _nextNid = nextSubnodeNid;
    }

    public List<SubnodeEntry> Subnodes { get; } = [];

    /// <summary>A fresh subnode NID: the caller's allocator, or NID_TYPE_LTP (0x1F) with a counter from 1.</summary>
    public Nid NextNid()
    {
        Nid nid;
        if (_nextNid is not null)
        {
            nid = _nextNid();
        }
        else
        {
            nid = new Nid((++_counter << 5) | (uint)NidType.Ltp);
        }

        // A zero type would read back as a heap ID, not a subnode (MS-PST 2.3.3.2).
        if ((nid.Value & 0x1F) == 0)
        {
            throw new InvalidOperationException($"subnode NID {nid} has type 0, which an HNID reads as a heap ID");
        }

        return nid;
    }

    /// <summary>Stores a variable-size value and returns its HNID: 0 for an empty value, an HID up to 3,580 bytes, else a subnode NID.</summary>
    public uint Place(HeapNodeBuilder heap, ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            return 0;
        }

        if (value.Length <= HeapNodeBuilder.MaxAllocation)
        {
            return heap.Allocate(value).Value;
        }

        return PlaceLarge(value);
    }

    /// <summary>Writes a value as the data of a new subnode, whatever its size, and returns the subnode's NID.</summary>
    public uint PlaceLarge(ReadOnlySpan<byte> value)
    {
        Nid nid = NextNid();
        return Register(nid, _blocks.WriteData(value));
    }

    /// <summary>Streams a large value into a subnode now. The caller has checked the length is above the heap limit.</summary>
    public uint PlaceStream(Stream source, long length)
    {
        Nid nid = NextNid();
        return Register(nid, _blocks.WriteData(source, length));
    }

    private uint Register(Nid nid, DataTreeResult r)
    {
        Subnodes.Add(new SubnodeEntry(nid, r.Root, default));
        return nid.Value;
    }
}
