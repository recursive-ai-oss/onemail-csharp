using System;
using System.Collections.Generic;

namespace RecursiveAI.OneMail.Pst.Ndb;

/// <summary>What the NDB reader found wrong. Every repair or refusal is one of these, with the byte offset it was found at.</summary>
public enum NdbDefectKind
{
    // Header
    /// <summary>The input is shorter than a header.</summary>
    FileTooShort,
    /// <summary>The first four bytes are not "!BDN".</summary>
    BadMagic,
    /// <summary>wMagicClient is not "SM" (PST), "SO" (OST) or "AB" (PAB).</summary>
    UnknownClientMagic,
    /// <summary>wVer is not a version this reader knows; the file is not read.</summary>
    UnsupportedVersion,
    /// <summary>dwCRCPartial does not match the 471 bytes it covers.</summary>
    HeaderPartialCrcMismatch,
    /// <summary>dwCRCFull does not match the 516 bytes it covers (Unicode headers).</summary>
    HeaderFullCrcMismatch,
    /// <summary>bSentinel is not 0x80.</summary>
    BadSentinel,
    /// <summary>bCryptMethod is not a defined value.</summary>
    UnknownEncryption,
    /// <summary>The data blocks are encrypted with Windows Information Protection; their contents cannot be decoded.</summary>
    ProtectedContent,
    /// <summary>fAMapValid says the allocation maps cannot be trusted (a writer was interrupted, or the flag is not a defined value).</summary>
    AMapFlagInvalid,
    /// <summary>ibFileEof in the header differs from the real file length.</summary>
    FileSizeMismatch,
    /// <summary>A root BREF points outside the file or is misaligned.</summary>
    RootOutOfRange,
    /// <summary>The header's version and the page trailers disagree about whether pages are 512 or 4096 bytes.</summary>
    AmbiguousPageSize,

    // Pages
    /// <summary>A page lies beyond the end of the file.</summary>
    PageOutOfRange,
    /// <summary>A page is cut short by the end of the file.</summary>
    PageTruncated,
    /// <summary>The page's type is not the type its parent expects.</summary>
    PageTypeMismatch,
    /// <summary>ptypeRepeat differs from ptype.</summary>
    PageTypeRepeatMismatch,
    /// <summary>wSig does not match the signature computed from the offset and BID (MS-PST 5.5).</summary>
    PageSignatureMismatch,
    /// <summary>dwCRC does not match the page contents.</summary>
    PageCrcMismatch,
    /// <summary>The BID in the page trailer is not the BID the parent referenced.</summary>
    PageBidMismatch,

    // B-trees
    /// <summary>cEnt, cbEnt or cEntMax describe entries that do not fit in the page.</summary>
    BTreeBadGeometry,
    /// <summary>A child page's level is not exactly one below its parent's.</summary>
    BTreeLevelMismatch,
    /// <summary>The root claims more levels than the format allows.</summary>
    BTreeTooDeep,
    /// <summary>Leaf keys are not in increasing order (a page referenced twice, or entries swapped).</summary>
    BTreeKeyOrder,
    /// <summary>Walking the tree visited more pages than the file can hold; it was cut off.</summary>
    BTreeTooManyPages,
    /// <summary>An intermediate entry's key is greater than the first key of the child it points to.</summary>
    BTreeSeparatorMismatch,

    // Blocks
    /// <summary>A block lies beyond the end of the file.</summary>
    BlockOutOfRange,
    /// <summary>A block is cut short by the end of the file.</summary>
    BlockTruncated,
    /// <summary>The BBT entry's size is larger than a block can be.</summary>
    BlockTooLarge,
    /// <summary>The block's own size field differs from the BBT entry's.</summary>
    BlockSizeMismatch,
    /// <summary>wSig does not match the signature computed from the offset and BID.</summary>
    BlockSignatureMismatch,
    /// <summary>dwCRC does not match the block data.</summary>
    BlockCrcMismatch,
    /// <summary>The BID in the block trailer is not the one the BBT entry has.</summary>
    BlockBidMismatch,
    /// <summary>A BID is not in the block B-tree.</summary>
    BlockNotFound,
    /// <summary>A compressed block could not be inflated, or inflated to the wrong size.</summary>
    BlockDecompressionFailed,
    /// <summary>A block is both compressed and in an encoded file; the decoding order is not documented, data was decoded after inflating.</summary>
    AmbiguousCompressedEncodedBlock,

    // Data trees and subnodes
    /// <summary>An XBLOCK or XXBLOCK header is wrong (type, level, count or size).</summary>
    DataTreeBadHeader,
    /// <summary>A data tree is nested deeper than XXBLOCK, XBLOCK, data block.</summary>
    DataTreeTooDeep,
    /// <summary>A data tree has more leaves than the configured limit.</summary>
    DataTreeTooLarge,
    /// <summary>The leaf sizes add up to something other than lcbTotal.</summary>
    DataTreeSizeMismatch,
    /// <summary>A data tree entry references a BID that is not in the block B-tree.</summary>
    DataTreeChildMissing,
    /// <summary>An SLBLOCK or SIBLOCK header is wrong (type, level, count or size).</summary>
    SubnodeBadHeader,
    /// <summary>A subnode tree has more entries than the configured limit.</summary>
    SubnodeTooLarge,
    /// <summary>A subnode tree references a BID that is not in the block B-tree.</summary>
    SubnodeChildMissing,

    // Allocation
    /// <summary>An AMap, PMap, FMap, FPMap or density list page failed validation.</summary>
    AllocationPageInvalid,
    /// <summary>A block or page the B-trees reference is not marked allocated in the AMap.</summary>
    AMapMismatch,
    /// <summary>The free space the AMaps add up to differs from cbAMapFree in the header.</summary>
    AMapFreeSpaceMismatch,
    /// <summary>The density list is malformed.</summary>
    DensityListInvalid,

    // Limits
    /// <summary>More defects were found than the configured limit; the rest are only counted.</summary>
    DefectLimitReached,
}

/// <summary>One thing the NDB reader found wrong, and where.</summary>
/// <param name="Kind">What was wrong.</param>
/// <param name="Offset">The file offset of the structure it was found in (a page or block start, or 0 for the header).</param>
/// <param name="Detail">A short plain-language description with the values involved.</param>
public readonly record struct NdbDefect(NdbDefectKind Kind, long Offset, string Detail)
{
    /// <inheritdoc/>
    public override string ToString() => $"{Kind} at 0x{Offset:x}: {Detail}";
}

/// <summary>Limits and switches for reading a file. Defaults are safe for hostile input.</summary>
public sealed class NdbOptions
{
    /// <summary>Most defects kept in <see cref="NdbFile.Defects"/>; later ones are counted in <see cref="NdbFile.DroppedDefects"/>. Default 100,000.</summary>
    public int MaxDefects { get; init; } = 100_000;

    /// <summary>Most leaf blocks a single data tree may reference. Default 4,194,304 (about 32 GiB of 8 KiB blocks).</summary>
    public int MaxDataTreeLeaves { get; init; } = 1 << 22;

    /// <summary>Most entries a single subnode tree may hold. Default 1,048,576.</summary>
    public int MaxSubnodeEntries { get; init; } = 1 << 20;
}
