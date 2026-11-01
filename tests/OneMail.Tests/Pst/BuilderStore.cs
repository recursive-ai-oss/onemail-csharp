using System;
using System.Collections.Generic;
using RecursiveAI.OneMail.Pst.Ltp;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Tests.Pst;

/// <summary>Collects the blocks a builder makes, so a test can place them in a file and read them back.</summary>
public sealed class CollectingSink : INdbBlockSink
{
    public List<NdbBlock> Blocks { get; } = [];

    public void Place(NdbBlock block) => Blocks.Add(block);
}

/// <summary>
/// A scratch Unicode file made from builder output: the blocks the builders wrote, plus the nodes that name them. Placement and the
/// B-trees come from the test file builder; the blocks are the builders' own.
/// </summary>
public sealed class BuilderStore
{
    private readonly CollectingSink _sink = new();
    private readonly List<TestNode> _nodes = [];
    private readonly NdbEncryption _encryption;

    public BuilderStore(NdbEncryption encryption = NdbEncryption.None)
    {
        _encryption = encryption;
        Bids = new SequentialBidAllocator();
        Blocks = new NdbBlockBuilder(Bids, _sink, encryption);
    }

    public SequentialBidAllocator Bids { get; }

    public NdbBlockBuilder Blocks { get; }

    public IReadOnlyList<NdbBlock> Written => _sink.Blocks;

    /// <summary>Adds a node over an LTP result (its heap, its subnodes and any more).</summary>
    public void AddNode(uint nid, LtpNodeResult result, IEnumerable<SubnodeEntry>? extra = null) =>
        _nodes.Add(new TestNode { Nid = nid, DataBid = result.Data.Value, SubBid = result.WriteSubnodes(Blocks, extra).Value });

    /// <summary>Adds a node over raw bids (a data tree from the block builder and a subnode tree).</summary>
    public void AddNode(uint nid, Bid data, Bid sub = default) =>
        _nodes.Add(new TestNode { Nid = nid, DataBid = data.Value, SubBid = sub.Value });

    public NdbFile Open(bool expectClean = true)
    {
        byte crypt = _encryption switch { NdbEncryption.Permute => 1, NdbEncryption.Cyclic => 2, _ => 0 };
        var built = NdbTestBuilder.Build(NdbFormat.Unicode, _nodes, new TestBuild { Crypt = crypt, Prebuilt = _sink.Blocks });
        Assert.True(NdbFile.TryOpen(built.Bytes, out NdbFile? f, out IReadOnlyList<NdbDefect> why), string.Join("; ", why));
        if (expectClean)
        {
            Assert.Empty(f!.Defects);
        }

        return f!;
    }
}
