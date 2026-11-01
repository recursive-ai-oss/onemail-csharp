# PST/OST node database (NDB) layer

What `RecursiveAI.OneMail.Pst.Ndb` reads, how it treats damage, and what was settled by looking at real files. The structures
are MS-PST 2.2.2 (the NDB layer); where MS-PST is silent (4 KiB pages) the facts below come from a real Outlook 2013 OST and
from the libyal PFF format notes (GFDL 1.3: cited, nothing copied).

## Geometry

| | ANSI (wVer 14, 15) | Unicode (wVer 21, 23) | Unicode 4 KiB (wVer 36; 37 decided by the root page) |
|---|---|---|---|
| page | 512 | 512 | 4096 |
| page trailer | 12 | 16 | 24 |
| block trailer | 12 | 16 | 24 |
| block alignment | 64 | 64 | **512** |
| largest block data | 8192 − 12 | 8192 − 16 | 65536 − 24 |
| id size (NID/BID/IB) | 4 | 8 | 8 |
| B-tree footer at | 496 | 488 | 4056 (cEnt and cEntMax are 16 bits) |
| density list | 0x4200 | 0x4200 | 0x21000 |
| first AMap | 0x4400 | 0x4400 | 0x22000 |
| AMap bits | 496 bytes, one bit per 64 | 496 bytes, one bit per 64 | 4072 bytes, **one bit per 512** |
| first block | 0x4800 | 0x4800 | 0x23000 |

Observed on a real 4 KiB OST (a 16 MB Outlook 2013 offline store, wVer 36, `SO`, no encoding):

- Blocks are padded to 512 bytes and the 24-byte trailer sits at the end of the padded extent. A 56-byte block occupies 512.
- The trailer after `bid` carries two more 16-bit fields: a constant `2` (meaning not known) and the **uncompressed size**; 4 bytes follow.
- A block is compressed when its uncompressed size differs from the BBT's size. All 123 compressed blocks in the file are zlib streams (header `78 xx`), CRC computed over the stored (compressed) bytes. Internal blocks (XBLOCK, SLBLOCK) are never compressed. The reader accepts raw deflate too.
- The file has one AMap, the density list page (empty) and no PMap, FMap or FPMap pages. The AMap's free-byte total equals `cbAMapFree` in the header exactly when a bit stands for 512 bytes.
- The 4 KiB header root is laid out as the Unicode one (offsets 180..252).

Seen on real files since: Apache Tika's `testPST.pst` and `testPST_variousBodyTypes.pst` are Unicode files with 512-byte pages (wVer 23) and permute encoding (`bCryptMethod` 1). The first has nine AMap pages and two PMap pages; both open with no defect, scan mode finds every block the block B-tree lists, and the LTP layer reads every property and table context in them (`docs/pst-ltp.md`). Not yet seen on a real file: ANSI files (no public one was found), 4 KiB files with more than one AMap, and encoded 4 KiB files. Those paths are tested against files written from the specification by the test-side builder only.

## Sources

`NdbFile.TryOpen(IRandomAccessSource source, bool leaveOpen, ...)` opens a file over any source of the sources layer (a file, a stream, memory, or caller-supplied ranged reads); the path, stream and memory overloads are shortcuts for it. The node database reads with synchronous positioned reads, so a source that does not support them (`SupportsSyncRead` false) is refused with `ArgumentException`; short reads from a source are retried until the buffer is full or the source ends. Paths open with `FileShare.ReadWrite | FileShare.Delete`, so a file another program has open can still be read.

## Reading never throws

`NdbFile.TryOpen` returns false only when no usable header exists (too short, wrong magic, unknown version); everything else is a typed `NdbDefect` with the file offset. I/O errors (the file cannot be opened) throw `IOException` as usual.

Defects (`NdbDefectKind`), grouped:

- **Header:** FileTooShort, BadMagic, UnknownClientMagic, UnsupportedVersion, HeaderPartialCrcMismatch, HeaderFullCrcMismatch, BadSentinel, UnknownEncryption, ProtectedContent (bCryptMethod 0x10, contents not decodable), AMapFlagInvalid, FileSizeMismatch, RootOutOfRange, AmbiguousPageSize.
- **Pages:** PageOutOfRange, PageTruncated, PageTypeMismatch, PageTypeRepeatMismatch, PageSignatureMismatch, PageCrcMismatch, PageBidMismatch.
- **B-trees:** BTreeBadGeometry, BTreeLevelMismatch, BTreeTooDeep, BTreeKeyOrder (entry skipped), BTreeTooManyPages.
- **Blocks:** BlockOutOfRange, BlockTruncated, BlockTooLarge, BlockSizeMismatch, BlockSignatureMismatch, BlockCrcMismatch, BlockBidMismatch, BlockNotFound, BlockDecompressionFailed, AmbiguousCompressedEncodedBlock.
- **Data and subnode trees:** DataTreeBadHeader, DataTreeTooDeep, DataTreeTooLarge, DataTreeSizeMismatch, DataTreeChildMissing, SubnodeBadHeader, SubnodeTooLarge, SubnodeChildMissing.
- **Allocation:** AllocationPageInvalid, AMapFreeSpaceMismatch, DensityListInvalid.
- **DefectLimitReached:** the last slot of the list when `NdbOptions.MaxDefects` (100,000) is hit; later defects are only counted.

A block whose trailer checks fail is still returned (the defect says why), so a reader can show what it can; a block that cannot be read at all (outside the file, larger than a block can be, bad deflate data) yields nothing and a defect.

### Ambiguities (provisional resolutions)

- **Page size of wVer 37** (and any version above 36): decided by which trailer position repeats `ptype` on the NBT root page; if neither or both do, 512 with `AmbiguousPageSize`.
- **Compressed and encoded block together:** MS-PST does not say which comes first. The reader inflates, then decodes, and reports `AmbiguousCompressedEncodedBlock`. No real file has both.
- **ANSI allocation page CRC:** computed over the 496 map bytes (after 4 bytes of padding) by some writers and over the whole page body by others; both are accepted. Not yet observed on a real ANSI file.

## Resource limits (defaults, `NdbOptions`)

| Limit | Default | Why |
|---|---|---|
| defects kept | 100,000 | a damaged 50 GB file must not exhaust memory with its own complaints |
| leaf blocks per data tree | 4,194,304 | a data tree is at most 2 levels (XXBLOCK); this is 32 GiB of 8 KiB blocks |
| entries per subnode tree | 1,048,576 | |
| pages visited per B-tree walk | file length / page size + 8 | two parents pointing at one child cannot multiply the work |
| B-tree levels | 8 | MS-PST 2.2.2.7.7 |

Decompression is capped by the uncompressed size in the trailer (at most one block), so a compressed block cannot expand beyond 64 KiB.

## Scan mode

`NdbFile.Scan` finds pages and blocks without using either B-tree: a page is a page-sized unit whose last bytes are a valid page trailer (ptype 0x80..0x86 repeated, signature, CRC); a block is an extent ending, on a block-alignment boundary, in a trailer with a plausible size, a signature that matches its own offset and BID, and a matching CRC. One forward pass over a 1 MiB window; memory does not grow with the file. `IncludeDamaged` also reports structures that fail only one of signature or CRC. Results carry the offset, size, BID and which checks passed; this is the substrate for recovery.

On the real OST: all 381 BBT blocks and both root pages are found, plus 101 further clean blocks that no tree references (stale versions Outlook left behind).

## Comparison with the libpff tools (20180714, black box)

`tools/oracles/libpff-ndb.sh <file>` prints what `pffinfo` and `pffexport` say next to `PstOracle dump` for the same file. On the 4 KiB OST: both report `64-bit with 4k page`, `Offline Storage Tables (OST)`, no encryption, file size 16,818,176. pffexport builds the folder tree and exports messages, but logs "unable to read table" for 12 items in this file (its 2018 build and 4 KiB stores); our reader finds no defect anywhere in the file. Item-level agreement belongs to the LTP/Messaging layers.

## Writing blocks

`NdbBlockBuilder` makes the blocks of a Unicode file (512-byte pages; ANSI and 4 KiB files are read-only) and nothing else. It does not write the header, B-tree pages, allocation maps or density list, and it does not decide where a block goes: every block is handed to an `INdbBlockSink` as an `NdbBlock`, with a BID from an `IBidAllocator`, and the caller puts it in the file and in the block B-tree.

| Piece | What it writes |
|---|---|
| Data block | The data, zero padding, and the 16-byte trailer so the block ends on a 64-byte boundary (MS-PST 2.2.2.8.1): `cb`, `wSig`, `dwCRC` (over the `cb` data bytes only), `bid`. 1 to 8,176 data bytes. |
| Encoding | `NDB_CRYPT_NONE`, `NDB_CRYPT_PERMUTE` and `NDB_CRYPT_CYCLIC` (key: the low 32 bits of the block's BID), set once on the builder and to be matched by the header's `bCryptMethod`. Internal blocks are never encoded. The CRC covers the stored (encoded) bytes. Windows Information Protection is refused. |
| Data tree | One data block when the stream fits; else an XBLOCK (btype 1, cLevel 1, `cEnt`, `lcbTotal`, BIDs: up to 1,021 leaves, 8,347,696 bytes); else an XXBLOCK over XBLOCKs (cLevel 2; its `lcbTotal` is the whole stream). Up to 1,021 × 1,021 leaves and 4,294,967,295 bytes (`lcbTotal` is 32 bits); beyond that is refused. A stream is read and written one block at a time; empty data is no block (null BID). `BeginDataTree` takes leaves of any size the caller chooses (heap blocks, row-matrix blocks). |
| Subnode tree | One SLBLOCK (btype 2, cLevel 0, up to 340 24-byte SLENTRYs), or an SIBLOCK (cLevel 1, up to 510 16-byte SIENTRYs holding each child's smallest NID) over SLBLOCKs: 173,400 subnodes at most. Entries are written in NID order; a repeated NID is refused. |

`wSig` is the one field that depends on placement (MS-PST 5.5: offset XOR BID), so an `NdbBlock` carries everything else and `WriteTo(destination, offset)` fills the signature in for the offset the caller chose. `NdbBlock.DataLength` is the `cb` for the block's BBT entry and `RefCount` suggests the `cRef`: 2, the BBT entry plus the one reference from the node, subnode entry or XBLOCK that names the block (MS-PST 2.2.2.7.7.3.1; the 3, 4 and 5 seen in Outlook-written files are blocks shared by several nodes, which the placing code counts). `SequentialBidAllocator` numbers blocks by index, shifted left two bits with bit 1 set for internal blocks, and exposes the next index for the header's `bidNextB`.

Working set: one block image (at most 8,192 bytes) at a time, plus eight bytes per data block of the stream being written (the BIDs the XBLOCK lists; 12 MB for a 4 GB stream). A stream is never held whole.

What is checked: `tests/OneMail.Tests/Pst/NdbBlockBuilderTests.cs` writes data trees of 1, 100, 8,175, 8,176 and 8,177 bytes, a full XBLOCK, one byte more (the first XXBLOCK) and two full XBLOCKs and a bit, in a file made around the builder's blocks, and reads each back byte for byte through `NdbFile` with no defect (CRC, signature and BID of every trailer); all three encodings; subnode trees of 1, 340, 341, 1,000 and the maximum 173,400 entries; the trailer fields by value.
