using System.Collections.Generic;

namespace RecursiveAI.OneMail.Pst.Ndb;

/// <summary>
/// A node and block index rebuilt from a scan of the file, standing in for the B-trees when those are damaged. Keys are the NID
/// (32 bits) and the BID with its reserved bit cleared, as the B-trees key them (MS-PST 2.2.2.7.7).
/// </summary>
internal sealed class NdbRecoveredIndex
{
    public Dictionary<uint, NodeEntry> Nodes { get; } = [];

    public Dictionary<ulong, BlockEntry> Blocks { get; } = [];
}
