# PST/OST lists, tables and properties (LTP) layer

What `RecursiveAI.OneMail.Pst.Ltp` reads, how it treats damage, and what was settled by looking at real files. The structures
are MS-PST 2.3 (heap-on-node, BTree-on-Heap, property context, table context). The layer sits directly on the node database
layer (`docs/pst-ndb.md`) and gives values no meaning: it returns property IDs, property types and **references to bytes**.
Strings, names, folders, messages and attachments belong to the layers above. LTP is the same in ANSI, Unicode and 4 KiB
files; the differences (block sizes, compression, encoding) are the node database's.

## Types

| Type | Spec name | What it is |
|---|---|---|
| `HeapNode` | HN (2.3.1) | A node's data read as heap blocks of numbered allocations. Resolves HIDs, owns the subnode tree, reads values. |
| `Hid`, `Hnid` | HID, HNID (2.3.1.1, 2.3.3.2) | 32-bit heap address; heap-or-subnode address. |
| `Bth`, `BthCursor` | BTH (2.3.2) | The tree: `TryFind` by key, a cursor over leaf records in key order. |
| `PropertyContext`, `PcProperty` | PC (2.3.3) | Properties by ID: `TryGet`, `foreach`, `ReadValue`. |
| `TableContext`, `TcColumn`, `TableRow` | TC (2.3.4) | Columns, a row index, rows copied into the caller's buffer, cells as references. |
| `LtpValueRef` | | Where a value's bytes are: `Empty`, `Inline` (up to eight bytes in the reference), `Heap` (an allocation) or `Subnode` (a subnode's data). Nothing is read until you ask. |
| `LtpValues` | | Fixed-size scalars (`TryGetInt16`, `Int32`, `Int64`, `Boolean`, `Single`, `Double`, `FileTime`, `Guid`) and multi-valued layouts (`TryGetMultiValueCount`, `TryGetMultiValueElement`). |
| `LtpPropertyTypes` | MS-OXCDATA 2.11.1 | The type codes, which sizes are fixed, the multi-value flag. |

Open a context from a node (`PropertyContext.TryOpen(file, nid, ...)`), a subnode entry, or a heap you already hold
(`HeapNode.TryOpenSubnode` gives the heap of a message's recipient table, for instance). Contexts opened from a file or
subnode own their heap and dispose it; a context made over your `HeapNode` leaves it to you. Opening a heap whose
`ClientSignature` is the wrong one for the context asks for is refused with `HeapClientSignatureMismatch`.

Value access is the same everywhere: `GetValueLength(ref)`, `ReadValue(ref, offset, buffer)` (a range, so large subnode values
stream), and `ReadValue(ref)` for a whole value as an array (null beyond `LtpOptions.MaxValueBytes`, default 256 MiB).

## How values are found

- **PC** (2.3.3.3): a BTH with 2-byte keys (property ID) and 6-byte entries (type, `dwValueHnid`). A fixed-size type of
  at most four bytes (Integer16, Integer32, Floating32, ErrorCode, Boolean) is in the entry itself and comes out as
  `Inline`. Everything else is an HNID: a zero HNID is `Empty`, an HID is `Heap`, a subnode NID is `Subnode`.
- **TC** (2.3.4.4): a cell of 1, 2, 4 or 8 bytes whose size equals the column type's fixed size is inline; a four-byte cell for
  any other type (strings, binary, objects, GUIDs, multi-valued) is an HNID, resolved as above. A cell that does not exist
  (its bit in the cell-existence bitmap is clear) gives no reference; the slot's bytes are not interpreted (2.3.4.4.3).
- **Rows** (2.3.4.4): the row matrix is one heap allocation (small tables) or the data of a subnode. In a subnode, rows never
  straddle a block, so row *N* is in block *N / rowsPerBlock* at *(N % rowsPerBlock) × rowSize*.
- **Multi-valued** (2.3.3.4): fixed-size elements are stored back to back; String8, String and Binary elements are a 4-byte
  count, one 4-byte offset per element from the start of the value, then the data.

## Reading never throws

`TryOpen` returns false and a typed list only when the structure is unusable. Everything else is an `LtpDefect` (kind, file
offset of the heap block, plain-language detail) in the heap's `Defects`, capped by `LtpOptions.MaxDefects` (1,000 per open heap;
the rest are counted in `DroppedDefects`). Problems in the blocks themselves (checksums, missing blocks, bad compression) are
`NdbDefect`s on the `NdbFile`; a block with a bad checksum is still returned by the node database layer, so LTP reads
through it and the two lists together say what was wrong.

| Group | Kinds |
|---|---|
| Nodes | `NodeNotFound`, `HeapUnreadable` (no data blocks) |
| Heap | `HeapBadHeader`, `HeapBadSignature` (bSig is not 0xEC: the node is not a heap), `HeapClientSignatureMismatch`, `HeapPageMapInvalid`, `HeapBlockMissing`, `HidInvalid` |
| Subnodes and values | `SubnodeMissing`, `SubnodesUnreadable`, `ValueUnreadable` |
| BTH | `BthBadHeader`, `BthTooDeep`, `BthNodeSize`, `BthKeyOrder`, `BthTooManyNodes` |
| PC | `PcBadShape` |
| TC | `TcBadInfo`, `TcBadLayout`, `TcRowIndexShape`, `TcRowOutOfRange`, `TcCellShape` |

`LtpValues` has no defect list: multi-valued data whose count or offsets do not fit returns false.

Lookups that fail return false, `-1` (a length) or null (a whole value) rather than a partial answer.

### Limits and hostile-input rules

| Rule | Value |
|---|---|
| BTH index levels | 8 (`LtpOptions.MaxBthLevels`); real trees have 1 to 3 |
| BTH nodes visited by one cursor | at most (heap length / 2) + 16: a node cannot be smaller than two bytes, so a well-formed tree cannot exceed this, and several parents pointing at one child cannot multiply the work |
| BTH key order | strictly increasing; a record that does not follow its predecessor is skipped with `BthKeyOrder` |
| `cbKey` / `cbEnt` | 2, 4, 8 or 16 / 1 to 32 (2.3.2.1); anything else is `BthBadHeader` |
| HID | type bits zero, index 1 to `cAlloc`, block inside the heap, allocation inside its block's data area (after the block header, before the page map) |
| Page map | `ibHnpm` leaves room for the map and the map fits the block; checked once per loaded block |
| Row access | row index and row size are checked against the matrix before any copy |
| Whole-value reads | `LtpOptions.MaxValueBytes` |

No allocation size is trusted before it is checked against the block it lives in; every read is a bounds-checked slice of the
decoded block. The layer has no `unsafe` code.

## Provisional resolutions

All are provisional defaults; none was contradicted by a real file.

1. **Rows per block of a subnode row matrix** (2.3.4.4 gives `floor((blockSize − trailer) / rowSize)` for 8 KiB blocks): taken
   from the first block's own size (`size / rowSize`), which equals the formula for 8 KiB blocks and adapts if 4 KiB files lay
   rows out in larger blocks. A single block holds all rows.
2. **BTH index keys** are not checked against the first key of the child they point to; lookups trust the key order and the
   level count.
3. **A BTH key that does not increase** is skipped, not returned, so a node referenced twice cannot produce duplicates.
4. **Allocation lower bound:** an allocation may not start before its block's header (12 bytes in block 0, 66 in blocks 8, 136,
   264... which carry a fill-level bitmap, 2 elsewhere). The bitmap-header blocks (2.3.1.4) were only exercised on files we
   wrote: no real file we have holds a heap of more than two blocks.
5. **Boolean and Integer16 in a PC** are inline (the entry's low one or two bytes); MS-PST states "up to four bytes".
6. **Unknown types** are read as HNIDs in a PC (a four-byte cell in a TC); a one-, two- or eight-byte cell of an unknown type is
   kept inline.
7. **`rgibAlloc` is zero-based while HID indexes are one-based:** HID index *i* is the allocation from `rgibAlloc[i − 1]` to
   `rgibAlloc[i]` (the page map's wording is ambiguous; every HID in the three real files resolves this way).
8. **Subnode lookup** compares the whole 32-bit NID, type bits included, as the HNID carries it.

## Real files

Checked on the NIST CFReDS Outlook 2013 OST (4 KiB pages, compressed blocks), Apache Tika's `testPST.pst` and
`testPST_variousBodyTypes.pst` (Unicode, 512-byte pages, permute-encoded), all fetched by `tools/corpus/fetch-pst.sh`.
`tools/oracles/libpff-ltp.sh <file>` reads every property and table context, then walks folders, items, recipients and
attachments the way a messaging layer would and compares with the libpff tools' export.

| File | PCs | TCs | Properties | Rows | Cells | LTP defects | NDB defects |
|---|---|---|---|---|---|---|---|
| CFReDS OST | 106 | 206 | 3,771 | 309 | 2,627 | 0 | 0 |
| `testPST.pst` | 21 | 38 | 998 | 25 | 360 | 0 | 0 |
| `testPST_variousBodyTypes.pst` | 14 | 34 | 733 | 15 | 252 | 0 | 0 |

(Nodes that are not heaps at all, such as search queues and internal lists, fail to open as `HeapBadSignature`,
`HeapBadHeader` or `HeapUnreadable`; the tool counts them separately and they are not defects of any LTP structure.)

Against `pffexport` (20180714) the folder hierarchy, items per folder, subjects, sizes, flags, three timestamps, sender,
conversation topic, recipients (name, address, type) and attachments (count and data size; an embedded message counts as one
with no size) agree for all three files: 48 / 6 / 6 folders, 14 / 7 / 4 items, 0 differences.

- The only systematic difference is a normalisation: a subject may begin with U+0001 and one more character (a prefix such as
  "RE: "); libpff drops both, the raw layer keeps them, the comparison strips them.
- `pffexport` reports no complaint in the live tree for any of the three files. All of its complaints (96, 40, 0) come from
  its final pass over *recovered* items, nodes found by scanning for old index entries. The twelve offsets it fails to read in
  the OST are the "unable to read data block" lines of that pass ("mismatch in data size"): five are not the start of any
  block in the file today, and seven hold blocks of a different size from the one it expected (three of them are live blocks
  that belong to other nodes now). The stale entries point at space that has been reused. Our reader never visits them,
  because nothing in the live node tree points there.

## Memory

On the CFReDS OST (release build, one thread): opening a property context allocates about 2.7 KB, opening a table context
2.6 KB, enumerating a context's properties 144 B; `TryGet`, `ReadValue` into a buffer, `TryReadRow`, reading every cell of a
row, and `TryFindRow` allocate nothing. Heap blocks are rented from the array pool and returned on dispose. Reading every
PC and TC in the file 200 times in a row keeps the managed heap at 339 to 355 KB after collection and the peak working set
at about 60 MB (of which the runtime is most), flat from the 75th pass on.

## Writing

The builders mirror the readers: what `HeapNode`, `Bth`, `PropertyContext` and `TableContext` read is what `HeapNodeBuilder`, `BthTreeBuilder`, `PropertyContextBuilder` and `TableContextBuilder` write. They sit on `NdbBlockBuilder` (`docs/pst-ndb.md`), so they write blocks, not a file: a property or table builder returns an `LtpNodeResult` holding the BID of the heap's data tree (for the node's `bidData`) and the subnodes it had to create, and `WriteSubnodes` turns those, plus any the caller adds (a recipient table, an attachment), into the BID for `bidSub`. Unicode files only.

### Heap (MS-PST 2.3.1)

`Allocate` places an allocation of 1 to 3,580 bytes (the limit of MS-PST 2.6.2.1.2) in the last block, opening a new block when it would not fit together with its page map, and returns its HID. Allocations are packed tightly in the order made, with no alignment, and a block holds up to 8,176 bytes (the data a block can carry) and 2,047 allocations; up to 65,536 blocks. `Finish` writes each block's header (HNHDR in block 0, HNBITMAPHDR in blocks 8, 136, 264…, HNPAGEHDR otherwise), page map (`cAlloc`, `cFree` = 0, `cAlloc + 1` offsets, starting on a 2-byte boundary after the last allocation) and the fill levels (low nibble first; bucketed by free bytes as the table in 2.3.1.2 gives them, counted as 8,176 less the end of the page map), and returns the blocks, one per heap block, for `NdbBlockBuilder.BeginDataTree`. The heap is held in memory until `Finish`.

### BTH (2.3.2)

`BthTreeBuilder` allocates the BTHHEADER first (so a tree created first in a heap is HID 0x20), takes records in any order, sorts them by key (little-endian unsigned) and refuses a repeated key. Any `cbKey` 2, 4, 8 or 16 and `cbEnt` 1 to 32. Leaves are full allocations of up to 3,580 bytes; index levels (key of the child's first record, then its 4-byte HID) are added until one root is left. An empty tree is the header alone with a null root. Records can be patched after `Build` (`SetData`), which is how a property context allocates the tree before the values.

### Property context (2.3.3)

| Value | Stored as |
|---|---|
| Integer16, Integer32, Floating32, ErrorCode, Boolean | in the 4-byte HNID field of the entry, zero-extended |
| any other fixed size (Integer64, Time, Floating64, Currency, Guid) | a heap allocation |
| variable size, empty | HNID 0 (an empty value, not an absent property) |
| variable size, 1 to 3,580 bytes | a heap allocation (HID) |
| variable size, 3,581 bytes and up (or a stream) | a subnode: a data tree written as soon as the value is set; the subnode NID is `NID_TYPE_LTP` (0x1F) with an index counting from 1 unless the caller supplies an allocator |
| a node reference (an attachment's embedded message, `PtypObject`) | the caller's subnode NID (type bits must not be zero), whose subnode the caller writes |

Fixed-size types must be given exactly their size, checked before anything is read or allocated. A repeated property ID is refused. The tree is built in ID order; its nodes are allocated before the values (header 0x20, records 0x40, values after), and the records are then patched with the values' HIDs.

### Table context (2.3.4)

Columns are sorted by tag; PidTagLtpRowId (0x67F2) and PidTagLtpRowVer (0x67F3) are added when missing (RowId filled from the row's ID, an added RowVer zero) at offsets 0 and 4 with bits 0 and 1 (2.3.4.4.1). An ID may be a column twice with two types, never twice with one. Cells of 8 bytes come first (so they are 8-aligned), then 4-byte cells (4-byte values and HNIDs; a Guid, being larger than 8 bytes, is an HNID), then 2-byte, then 1-byte cells, each group in tag order, then the cell-existence bitmap; bits after the first two number the cells in row order. At most 255 columns and rows that fit a block. A cell never set is absent (bit clear); one set to an empty value is present with HNID 0. Variable-size cell values follow the property context rules above. The heap holds the row index BTH header first (0x20) and the TCINFO second (0x40); `hidIndex` is zero.

Rows are added one at a time (`BeginRow`, `SetCell`…, `EndRow`, or `AddRow`) and packed into the row matrix. Up to 3,580 bytes the matrix is one heap allocation (HNID = HID); beyond that it is the data of a subnode (`NID_TYPE_LTP`) whose blocks each hold ⌊8,176 / row size⌋ whole rows: every block but the last is written as a full 8,176 bytes (the rows, then zero "dead space"), as 2.3.4.4 requires of a block that is "8192 bytes", and the last is as long as its rows. A full block is always more than 3,580 bytes, so the builder knows the matrix is a subnode the moment the first block fills and writes it out then; a row index (row ID to position, 4-byte keys and values) is built alongside and duplicates are refused.

### Working set

`PropertyContextBuilder` and `TableContextBuilder` hold the heap being built (small values, the BTH, the row index) and one row-matrix block; values above 3,580 bytes and matrix blocks go to the block builder as they are produced. Light indicative run (release build, one thread, blocks discarded, a table of four columns, a 300-byte binary cell and a short string per row, 29-byte rows; `PstOracle ltpmem <rows>`):

| Rows | Blocks written | Managed bytes allocated | Live before `Build` | Peak working set |
|---|---|---|---|---|
| 20,000 | 956 (7.3 MiB) | 20.8 MiB | 7.9 MiB | 55 MiB |
| 100,000 | 4,813 (36.5 MiB) | 103.1 MiB | 38.9 MiB | 118 MiB |
| 400,000 | 19,276 (146.8 MiB) | 410.0 MiB | 156.3 MiB | 283 MiB |

Memory grows with the table's heap (here the 300-byte cells, 90% of the output), which is the structure being built; the row matrix itself is not held. A table whose heap should not be held at all needs heap blocks emitted as they seal, with the first block and the bitmap blocks written last under BIDs reserved up front; the builders do not do that yet.

### Checked against real files

`tests/PstOracle ltpwrite <file>` (`tools/oracles/ltpwrite.sh`) reads every property context and table context of a file (top-level nodes and subnodes, recursively), rebuilds each with the builders, places the result in a scratch file, reads it back and compares every property (ID, type, bytes) and every row (row ID, row index, presence and bytes of each cell). A property of `PtypObject` type that names a subnode is compared by its HNID.

| File | PCs | TCs | Properties | Rows | Cells | Differences | NDB defects in the scratch file |
|---|---|---|---|---|---|---|---|
| Tika `testPST.pst` | 21 | 38 | 998 | 25 | 696 | 0 | 0 |
| Tika `testPST_variousBodyTypes.pst` | 14 | 34 | 733 | 15 | 336 | 0 | 0 |
| CFReDS OST (4 KiB, read only: contents rebuilt as 512-byte-page Unicode) | 106 | 206 | 3,771 | 309 | 5,144 | 0 | 0 |

(61, 50 and 481 heaps in the three files: the remainder are heaps that are neither a PC nor a TC. 13, 9 and 17 nodes are not heaps at all.)

### How the layout differs from Outlook's

Measured on the same three files (heap blocks of the two Unicode files; table and property facts on all three):

| | Outlook-written | OneMail builders |
|---|---|---|
| Allocation packing | tight, no alignment (starts and sizes of every parity), first allocation right after the header, page map at the next even offset, `cFree` 0 (two heaps in one file have freed items) | same |
| Fill levels | `rgbFillLevel` equals the 2.3.1.2 bucket of the free bytes (8,176 minus the end of the page map) in every heap checked; block 0 in the low nibble | same |
| Heap blocks | non-last blocks full to within 16 bytes | same |
| Property context order | header 0x20; records before values in 107 of the 134 contexts that have heap values (the rest have values first) | header, records, values |
| Table heap order | row index header 0x20, TCINFO 0x40 in all 278 | same; then values as rows are added, then row index nodes and the matrix (Outlook: nodes and matrix before the values) |
| Row layout | cells in creation order with 8-byte cells 8-aligned, bits in creation order (the cell order in only 67 of 278), RowId 0/bit 0 and RowVer 4/bit 1 | 8-byte, 4-byte, 2-byte, 1-byte groups in tag order; bits in cell order. 49 of 278 tables have the identical layout; both keep the 4- and 8-byte, 2-byte, 1-byte group order the format requires |
| Subnode NIDs | `NID_TYPE_LTP`, matrix subnode 0x3F in all 7 | `NID_TYPE_LTP`, 0x3F for the first subnode of a node |
| Block cRef | 2 for data blocks and internal blocks not shared; 3 to 5 for shared ones | 2 (suggested; the placing code counts shared references) |

MS-PST leaves the allocation order, the iBit numbering beyond RowId and RowVer, and the order of cells inside a size group open, so the differences are legal. No multi-block row matrix occurs in these files (the one Unicode subnode matrix is one block), so the "full 8,176-byte block" rule for non-last matrix blocks is the specification's wording, not something observed in a real file.
