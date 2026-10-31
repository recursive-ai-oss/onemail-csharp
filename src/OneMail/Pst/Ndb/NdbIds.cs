using System;

namespace RecursiveAI.OneMail.Pst.Ndb;

/// <summary>The kind of a node, taken from the low five bits of its <see cref="Nid"/> (MS-PST 2.2.2.1 nidType).</summary>
/// <remarks>The NDB layer gives the type no meaning; the layers above do. Values not listed here are carried as-is.</remarks>
public enum NidType : byte
{
    /// <summary>Heap node (NID_TYPE_HID).</summary>
    Hid = 0x00,
    /// <summary>Internal node (NID_TYPE_INTERNAL).</summary>
    Internal = 0x01,
    /// <summary>Normal folder (NID_TYPE_NORMAL_FOLDER).</summary>
    NormalFolder = 0x02,
    /// <summary>Search folder (NID_TYPE_SEARCH_FOLDER).</summary>
    SearchFolder = 0x03,
    /// <summary>Normal message (NID_TYPE_NORMAL_MESSAGE).</summary>
    NormalMessage = 0x04,
    /// <summary>Attachment (NID_TYPE_ATTACHMENT).</summary>
    Attachment = 0x05,
    /// <summary>Search update queue (NID_TYPE_SEARCH_UPDATE_QUEUE).</summary>
    SearchUpdateQueue = 0x06,
    /// <summary>Search criteria object (NID_TYPE_SEARCH_CRITERIA_OBJECT).</summary>
    SearchCriteriaObject = 0x07,
    /// <summary>Folder-associated message (NID_TYPE_ASSOC_MESSAGE).</summary>
    AssocMessage = 0x08,
    /// <summary>Contents table index (NID_TYPE_CONTENTS_TABLE_INDEX).</summary>
    ContentsTableIndex = 0x0A,
    /// <summary>Receive folder table (NID_TYPE_RECEIVE_FOLDER_TABLE).</summary>
    ReceiveFolderTable = 0x0B,
    /// <summary>Outgoing queue table (NID_TYPE_OUTGOING_QUEUE_TABLE).</summary>
    OutgoingQueueTable = 0x0C,
    /// <summary>Hierarchy table (NID_TYPE_HIERARCHY_TABLE).</summary>
    HierarchyTable = 0x0D,
    /// <summary>Contents table (NID_TYPE_CONTENTS_TABLE).</summary>
    ContentsTable = 0x0E,
    /// <summary>Folder-associated contents table (NID_TYPE_ASSOC_CONTENTS_TABLE).</summary>
    AssocContentsTable = 0x0F,
    /// <summary>Search contents table (NID_TYPE_SEARCH_CONTENTS_TABLE).</summary>
    SearchContentsTable = 0x10,
    /// <summary>Attachment table (NID_TYPE_ATTACHMENT_TABLE).</summary>
    AttachmentTable = 0x11,
    /// <summary>Recipient table (NID_TYPE_RECIPIENT_TABLE).</summary>
    RecipientTable = 0x12,
    /// <summary>Search table index (NID_TYPE_SEARCH_TABLE_INDEX).</summary>
    SearchTableIndex = 0x13,
    /// <summary>LTP (NID_TYPE_LTP).</summary>
    Ltp = 0x1F,
}

/// <summary>A node ID: a 5-bit type and a 27-bit index (MS-PST 2.2.2.1). Always 32 bits, in ANSI and Unicode files alike.</summary>
/// <param name="Value">The raw 32-bit value.</param>
public readonly record struct Nid(uint Value)
{
    /// <summary>The low five bits.</summary>
    public NidType Type => (NidType)(Value & 0x1F);

    /// <summary>The upper 27 bits.</summary>
    public uint Index => Value >> 5;

    /// <inheritdoc/>
    public override string ToString() => "0x" + Value.ToString("x", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// A block ID (MS-PST 2.2.2.2). 64 bits in Unicode files and 32 bits in ANSI files; the value is widened to 64 bits here.
/// Block BIDs reserve bit 0 and use bit 1 as the "internal" flag; page BIDs use every bit.
/// </summary>
/// <param name="Value">The raw value as stored.</param>
public readonly record struct Bid(ulong Value)
{
    /// <summary>True for the all-zero BID, which references nothing.</summary>
    public bool IsNull => Value == 0;

    /// <summary>
    /// Bit 1: the block holds NDB metadata (an XBLOCK, XXBLOCK, SLBLOCK or SIBLOCK) rather than node data. Meaningless for page BIDs.
    /// </summary>
    public bool IsInternal => (Value & 2) != 0;

    /// <summary>The value with the reserved bit 0 cleared. MS-PST 2.2.2.2: readers ignore it before looking a BID up in the BBT.</summary>
    public Bid Normalized => new(Value & ~1UL);

    /// <inheritdoc/>
    public override string ToString() => "0x" + Value.ToString("x", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>A block reference: a BID and the absolute file offset (IB) where that page or block starts (MS-PST 2.2.2.4).</summary>
/// <param name="Bid">The block or page ID.</param>
/// <param name="Ib">The absolute byte offset in the file.</param>
public readonly record struct Bref(Bid Bid, ulong Ib);

/// <summary>The ROOT record of the header (MS-PST 2.2.2.5).</summary>
/// <param name="FileEof">ibFileEof: the file size the writer recorded.</param>
/// <param name="AMapLast">ibAMapLast: offset of the last AMap page.</param>
/// <param name="AMapFree">cbAMapFree: free bytes over all AMaps.</param>
/// <param name="PMapFree">cbPMapFree: free bytes over all PMaps (deprecated; normally zero).</param>
/// <param name="NodeBTree">BREFNBT: the root page of the node B-tree.</param>
/// <param name="BlockBTree">BREFBBT: the root page of the block B-tree.</param>
/// <param name="AMapValid">fAMapValid as stored: 0 invalid, 1 or 2 valid.</param>
public readonly record struct NdbRoot(
    ulong FileEof, ulong AMapLast, ulong AMapFree, ulong PMapFree, Bref NodeBTree, Bref BlockBTree, byte AMapValid);

/// <summary>A leaf entry of the node B-tree (MS-PST 2.2.2.7.7.4 NBTENTRY).</summary>
/// <param name="Nid">The node ID, the key.</param>
/// <param name="Data">The BID of the node's data block or data tree root. Null for a node without data.</param>
/// <param name="Sub">The BID of the node's subnode tree root. Null when the node has no subnodes.</param>
/// <param name="Parent">nidParent: the parent folder's NID. Meaningless to the NDB layer.</param>
public readonly record struct NodeEntry(Nid Nid, Bid Data, Bid Sub, Nid Parent);

/// <summary>A leaf entry of the block B-tree (MS-PST 2.2.2.7.7.3 BBTENTRY).</summary>
/// <param name="Block">The block's BID and file offset.</param>
/// <param name="Size">cb: the data bytes in the block, without trailer or padding. For a compressed block, the compressed size.</param>
/// <param name="RefCount">cRef: the reference count.</param>
public readonly record struct BlockEntry(Bref Block, int Size, int RefCount)
{
    /// <summary>The block's BID.</summary>
    public Bid Bid => Block.Bid;
}

/// <summary>An entry of a subnode tree (MS-PST 2.2.2.8.3.3.1.1 SLENTRY).</summary>
/// <param name="Nid">The subnode's local NID, unique only within the parent node.</param>
/// <param name="Data">The BID of the subnode's data block or data tree root.</param>
/// <param name="Sub">The BID of the subnode's own subnode tree, or null.</param>
public readonly record struct SubnodeEntry(Nid Nid, Bid Data, Bid Sub);
