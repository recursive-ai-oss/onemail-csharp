using RecursiveAI.OneMail.Pst.Messaging;
using RecursiveAI.OneMail.Pst.Ndb;
using RecursiveAI.OneMail.Recovery;

namespace RecursiveAI.OneMail.Pst.Recovery;

/// <summary>What recovery had to do or could not do while it rebuilt a damaged store's index, or what makes a recovered item less than a live one.</summary>
/// <remarks>Item-level kinds also appear as <see cref="MailDefect"/>s (family <c>pst-recovery</c>) on the item's <see cref="RecoveryProvenance"/>.</remarks>
public enum PstRecoveryDefectKind
{
    /// <summary>The header is unusable. The generation and the encoding were inferred from the pages and blocks found in the body.</summary>
    HeaderUnusable = 0,

    /// <summary>The node tree could not be walked cleanly from its root, so the node index was rebuilt from the leaf pages a scan found. Entries the walk did not reach are tagged <see cref="RecoveryStatus.Orphaned"/>.</summary>
    NodeIndexRebuilt,

    /// <summary>A leaf page of the node tree failed its checksum or signature. Entries that agree with blocks found elsewhere in the file were kept; the others were dropped.</summary>
    DamagedPageEntriesKept,

    /// <summary>A block failed its checksum or signature and was left out. Anything that needed it is missing.</summary>
    BlockDamaged,

    /// <summary>A block the item needs is not in the file (or was left out as damaged). The item is <see cref="RecoveryStatus.Partial"/>.</summary>
    BlockMissing,

    /// <summary>The item was rebuilt from a current-looking entry found by scan, not reached from the node tree's root. Deleted nodes whose pages survive cannot be told apart from live ones once the tree's root is gone.</summary>
    IndexEntryRebuilt,

    /// <summary>The item was read from a block no node refers to. Its node ID, recipients, attachments and any value kept in a subnode were not recovered.</summary>
    SubnodesNotFound,

    /// <summary>The message reads, but the messaging layer repaired or skipped something while it did (see the message's own defects).</summary>
    MessageDamaged,

    /// <summary>The message is not listed by its folder's contents table, though the node tree still holds it.</summary>
    NotListedByFolder,

    /// <summary>The folder that held the message is no longer in the folder hierarchy.</summary>
    FolderNotInHierarchy,

    /// <summary>An earlier version of a node that the live tree holds under the same node ID (its data block is not the live one).</summary>
    SupersededVersion,

    /// <summary>The node tree or a table of the folder could not be read cleanly, so whether the item is still listed is unknown.</summary>
    ListingUnknown,

    /// <summary>The node is in the index but the messaging layer could not read it as a message (its property context is gone or unreadable). It is not returned.</summary>
    NodeUnreadable,
}

/// <summary>One thing recovery found wrong or did, with the file offset it concerns.</summary>
/// <param name="Kind">What.</param>
/// <param name="Offset">Absolute offset in the source file; 0 when the defect concerns the file as a whole.</param>
/// <param name="Detail">One line for reports.</param>
public sealed record PstRecoveryDefect(PstRecoveryDefectKind Kind, long Offset, string Detail);

/// <summary>Switches of <see cref="PstRecovery"/>.</summary>
public sealed class PstRecoveryOptions
{
    /// <summary>
    /// Also return what the live structure does not show: messages the node tree holds but their folder no longer lists
    /// (<see cref="RecoveryStatus.SoftDeleted"/>), nodes only stale pages of the node tree still name or blocks nothing refers to
    /// (<see cref="RecoveryStatus.Orphaned"/>), and messages read out of space the store has freed (<see cref="RecoveryStatus.FreeSpace"/>).
    /// Default false: only what a damaged store still holds as live content.
    /// </summary>
    public bool Forensic { get; init; }

    /// <summary>
    /// With <see cref="Forensic"/>, also return earlier versions of live nodes (stale entries under a node ID the live tree holds).
    /// Default false: they are counted in <see cref="PstRecoveryStats.SupersededVersions"/> and left out.
    /// </summary>
    public bool IncludeSuperseded { get; init; }

    /// <summary>The most items to return. Default 10 million.</summary>
    public int MaxItems { get; init; } = 10_000_000;

    /// <summary>The most blocks (data tree and subnode tree) read for one item. Default 1,048,576.</summary>
    public int MaxBlocksPerItem { get; init; } = 1 << 20;

    /// <summary>Limits of the node database layer; null for defaults.</summary>
    public NdbOptions? Ndb { get; init; }

    /// <summary>Limits of the messaging layer; null for defaults.</summary>
    public PstReaderOptions? Reader { get; init; }
}

/// <summary>Counts from opening a store for recovery.</summary>
/// <param name="Pages">Pages the scan found whole.</param>
/// <param name="Blocks">Blocks the scan found whole.</param>
/// <param name="DamagedBlocks">Blocks whose trailer was plausible but whose signature or checksum failed (left out).</param>
/// <param name="DamagedPages">Pages of the node tree's leaf level or the block tree that failed a check.</param>
/// <param name="Nodes">Entries in the node index recovery works from.</param>
/// <param name="ReachedNodes">Of those, the entries the node tree reaches from its root.</param>
/// <param name="StaleNodes">Entries only older pages of the node tree name, under node IDs the index does not hold.</param>
/// <param name="SupersededVersions">Stale entries under a node ID the live tree holds with another data block.</param>
public readonly record struct PstRecoveryStats(long Pages, long Blocks, long DamagedBlocks, long DamagedPages, long Nodes, long ReachedNodes, long StaleNodes, long SupersededVersions);

/// <summary>A message recovery returns, with where it came from and how far to trust it.</summary>
public sealed class PstRecoveredItem
{
    internal PstRecoveredItem(PstMessage message, RecoveryProvenance provenance, string folderPath, bool inFolder, bool associated)
    {
        Message = message;
        Provenance = provenance;
        FolderPath = folderPath;
        PlaceInFolder = inFolder;
        Associated = associated;
    }

    /// <summary>The message, read lazily from the source; valid until the <see cref="PstRecovery"/> is disposed.</summary>
    public PstMessage Message { get; }

    /// <summary>The tag, byte ranges, locators and qualifying defects.</summary>
    public RecoveryProvenance Provenance { get; }

    /// <summary>The folder the item belonged to, as <see cref="PstFolder.Path"/> writes it ("Top of Personal Folders/Inbox"); empty when unknown.</summary>
    public string FolderPath { get; }

    /// <summary>
    /// True when a repaired copy files the item in <see cref="FolderPath"/>: live and partial items, and items rebuilt from a damaged index.
    /// False for deleted, stale and freed items, which a repaired copy files under a "Recovered" folder per tag.
    /// </summary>
    public bool PlaceInFolder { get; }

    /// <summary>True for a folder-associated message (a rule, view or setting).</summary>
    public bool Associated { get; }

    /// <summary>The shared-view item, with <see cref="IMailItem.Recovery"/> set.</summary>
    /// <returns>A new view over <see cref="Message"/>.</returns>
    public MapiMailItem ToMailItem() =>
        new(Message, new MailProvenance(MailFormat.Pst, null, FolderPath.Length == 0 ? null : FolderPath, -1, -1, -1)) { RecoveryInfo = Provenance };
}
