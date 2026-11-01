namespace RecursiveAI.OneMail.Pst.Ndb;

/// <summary>Where the object writers put the nodes they make: a new file's writer, or an editor changing an existing file.</summary>
public interface INdbNodeSink
{
    /// <summary>The block builder whose blocks land in the same file as the nodes.</summary>
    NdbBlockBuilder Blocks { get; }

    /// <summary>Adds a node whose blocks are already placed. An editor replaces a node with the same NID; a new file's writer refuses one.</summary>
    /// <param name="entry">The node.</param>
    void AddNode(NodeEntry entry);
}
