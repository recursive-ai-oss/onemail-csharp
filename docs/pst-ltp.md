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

All are provisional agent defaults; none was contradicted by a real file.

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
