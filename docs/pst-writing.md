# Writing PST files

What `RecursiveAI.OneMail.Pst.Ndb.PstFileWriter` and `RecursiveAI.OneMail.Pst.Messaging.PstBuilder` write when they make a new Unicode PST
(MS-PST, 512-byte pages, wVer 23), how they stay in constant memory, what a stopped build leaves behind, and what has and has not been checked.
Reading is in `pst-ndb.md`, `pst-ltp.md` and `pst-messaging.md`; the block, heap, property-context and table-context builders this sits on are described in
the writing sections of the first two.

New files only. A file is written front to back and never rewritten, so a new store has no freed blocks and there is nothing to zero. ANSI and 4 KiB
files are never written. Changing an existing PST in place is the last section ("In place").

## The file (`PstFileWriter`)

| Part | What is written |
|---|---|
| Header | 564 bytes at offset 0, **written last**: `!BDN`, `wMagicClient` `SM`, `wVer` 23, `wVerClient` 19, `bidNextP`, `bidNextB`, `rgnid` (the next index for each NID type, 0x400 at least), the ROOT record (`ibFileEof`, `ibAMapLast`, `cbAMapFree`, the NBT and BBT BREFs, `fAMapValid` = 2), `rgbFM` and `rgbFP` filled with 0xFF, `bSentinel` 0x80, `bCryptMethod`, and both CRCs. |
| 0x0000 to 0x4400 | Zero until the end, except the density list at 0x4200. |
| Ranges | From 0x4400 the file is a series of 253,952-byte ranges (496 × 8 × 64, MS-PST 2.2.2.7.2). A range starts with its AMap page, then a PMap page when its index is a multiple of 8, an FMap page at index 128 and every 496th after, an FPMap page at index 8,192 and every 3,968th after. Blocks (64-byte aligned) and B-tree pages (512-byte aligned) follow in the order they are made; none crosses into the next range, so a block that does not fit starts the next one and the gap stays free in the AMap. The last range is written whole, so a file is always 0x4400 plus a whole number of ranges (an empty store is 271,360 bytes, as Outlook's is). |
| AMap | 496 bytes of bits, one per 64 bytes (the first slot is the **high** bit of the first byte: seen in files Outlook wrote, MS-PST does not say), set for the map pages themselves and every block and page; trailer type 0x84, signature zero, BID equal to the page's offset. `cbAMapFree` is the sum of the clear bits. |
| PMap, FMap, FPMap | Valid pages (CRC, trailer) of types 0x83, 0x82 and 0x85, every byte 0xFF. The PMap content is what Outlook wrote in the files we have (all ones). FMap and FPMap content and position follow the specification's statement that they must exist at the right intervals and the header's note that its own FMap covers the first 128 ranges; this is **not verified against Outlook** (it matters only for files over about 32 MB). |
| Density list | At 0x4200: `DFL_BACKFILL_COMPLETE`, up to 119 entries of (range index, free 64-byte slots) with the emptiest range first, and the last range as the current page. |
| Blocks | Placed as the `NdbBlockBuilder` makes them; each gets a block-B-tree entry with `cRef` 2. A BID reserved early for a block written later (the first block of a heap) holds back only the entries after it until it is placed. |
| Block B-tree | Pages are written as entries arrive, bottom-up, with one open page per level: 20 entries per leaf page (24 bytes each) and 20 per index page, so the tree costs no memory per block. |
| Node B-tree | Nodes arrive in any order. They are sorted 65,536 at a time; runs after the first go to a spool (a temporary file that is deleted on close, or memory where files are not available), and `Complete` merges the runs into pages the same way. 15 entries per leaf page (32 bytes each). A repeated NID is refused. |

`Complete` also refuses a file with a BID that was taken and never placed. Page BIDs count up from 0x40 in steps of one (as seen in files Outlook wrote);
block BIDs are the block builder's.

### After a stop

Everything before the header is already on disk when a build is stopped; the header is not. A file the writer was stopped on has a zero header (no
`!BDN`), and `NdbFile.TryOpen` and `PstStore.TryOpen` refuse it with `BadMagic`. The header is one 564-byte write at the very end, after the last
range and the density list have been written and the stream flushed. A kill between those writes leaves the zero header; a kill after leaves a whole
store. There is no state in between that a reader accepts. (An operating system crash can still lose unflushed pages of a file that has a header; the
builder does not claim crash consistency, which belongs to in-place writing, below.)

## The store (`PstBuilder`)

`PstBuilder.Create(path)` or `Create(stream)` opens the file and writes the mandatory nodes at once or at `Complete` (MS-PST 2.7.1):

| NID | Node |
|---|---|
| 0x21 | message store: record key, display name, entry IDs of the top folder, Deleted Items and the search root, no password |
| 0x61 | name-to-ID map |
| 0x122 | root folder (its own parent), with hierarchy 0x12D, contents 0x12E and associated 0x12F tables |
| 0x8022 | "Top of Personal Folders" (`IpmSubtree`); user folders start at index 0x404 |
| 0x8062 | Deleted Items, under the top folder |
| 0x8042 | Search Root |
| 0x2223 | the spam search folder (a search folder: a property context and empty search-contents, hierarchy and associated tables) |
| 0x1E1, 0x201 | the search management queue and the search activity list, nodes without data |
| 0x60D, 0x60E, 0x60F, 0x610, 0x692, 0x671 | the template tables, columns only |

Messages are NID type 4 from index 0x10000 and folder-associated messages type 8 from 0x8000, the values Outlook's files start at.

**Folders.** `CreateFolder`, `GetOrCreateFolder` and `GetOrCreatePath` make subfolders; the folder's property context and its three tables are written at
`Complete`, children before parents, from counts and spooled rows. A folder of any size therefore costs a buffer of at most 32 KiB until it is closed, then
a table whose rows stream into row-matrix blocks and whose heap blocks are written as they fill (see *Memory*).

**Messages.** `AddMessage` writes the message at once: a property context holding every property (values over 3,580 bytes in subnodes, streamed from
lazy values), the recipient table (always present, as MS-PST 2.4.5.3 requires; its columns are the template's plus every property the recipients have),
the attachment table, each attachment as an attachment object subnode, an embedded message as a subnode of its attachment with its own tables
(MS-PST 2.4.6.2.2), and the folder's row. Only that row is kept.

**Property-exact, and what is not.** A property is written with its tag type, raw bytes and, for a named property, its name; multi-valued values use the PST
layout (counts and offsets for strings and binaries). These differences are listed in `PstBuilder.Defects` (and, for a conversion, in the loss report as
`Pst…` codes), never silent:

- *Added* (`PropertyAdded`, with `PstBuilderOptions.AddRequiredProperties`, default true): the properties MS-PST says every message and attachment must have: `PidTagMessageClass` (`IPM.Note`
  if missing), `PidTagMessageFlags` (`MSGFLAG_HASATTACH` when it has attachments), `PidTagMessageSize`, `PidTagMessageStatus`, `PidTagCreationTime`,
  `PidTagLastModificationTime` (the item's last-modified or submit time, else `PstBuilderOptions.Timestamp`, else the time the builder was created),
  `PidTagSearchKey` (16 bytes of a hash of the NID, class and subject); `PidTagAttachSize`, `PidTagAttachMethod`, `PidTagRenderingPosition` (−1) for attachments.
- *Renumbered*: a named property gets the next ID from 0x8000 in this file; its name travels in the name-to-ID map. Property-exact is by name.
- *Left out*: a named property whose name is unknown (`NamedPropertyUnmapped`); more than 32,767 names (`NamedPropertyLimit`); a repeated property ID, first kept
  (`DuplicateProperty`); types a property context cannot hold, such as null, unspecified, server ID and restriction (`UnsupportedType`), or a value whose size does not fit its type;
  an OLE or raw attachment storage (`ObjectStorageDropped`); embedded messages nested deeper than `MaxEmbeddingDepth` (32); more than 65,535 recipients or attachments in one message.
- *Not stored*: the table bookkeeping columns `PidTagLtpRowId` and `PidTagLtpRowVer` (they belong to the table).

**Name-to-ID map** (MS-PST 2.4.7): GUID stream (property sets other than the two well-known ones), entry stream (one NAMEID per name: numeric ID or string-stream offset,
the GUID index and N bit, the property index), string stream (length-prefixed UTF-16, padded to four bytes) and 251 hash buckets of NAMEID records, each
holding the CRC of the name for string names. The bucket number is `(dword 0 XOR low word of dword 1) mod 251`, with the CRC as dword 0 for string names; the specification
leaves open whether the CRC or the offset selects the bucket for a string name, and this is **not verified against Outlook**.

**Search update queue.** There is nothing to queue in a store with no search folder content; the queue and the activity list are empty nodes, and the
spam search folder has no criteria. The store is consistent.

### Defaults

| Setting | Default | Why |
|---|---|---|
| `Encryption` | `Permute` | what Outlook writes; obfuscation that every reader undoes, not protection (`None` and `Cyclic` are the others) |
| `StoreId` | a new GUID | the provider UID in entry IDs and `PidTagRecordKey`; set it to write the same bytes twice |
| `Timestamp` | when the builder was created | only for items without a modification time; set it to write the same bytes twice |
| `AddRequiredProperties` | true | a store Outlook accepts needs them (see above) |
| `MaxEmbeddingDepth` | 32 | the reader's default |

## Memory

Working set does not grow with the number of messages: a block is handed to the file as it is made; the block B-tree keeps one open page per level; the node
B-tree keeps 65,536 entries and spools the rest; each folder's rows are buffered up to 32 KiB and spooled. The heap of a folder's contents table writes its
blocks as it fills them (all but block 0, which carries the user root and the summaries of the first eight blocks, and the block-bitmap block of the 128
blocks in progress, each waiting until the blocks it describes are finished). What still grows is the table's row index (the BTH of row ID to row index), about 12
bytes per row of the folder being closed. Measured runs are in `benchmarks.md`.

## How it is checked

- `tests/OneMail.Tests/Pst/PstBuilderTests.cs` and `PstWriterTests.cs`: stores written by the builder open with no defect, read back property by property, scan
  mode finds every block and page, the AMaps add up to the header's free space, files over several ranges (including the FMap and PMap pages) open clean, a store
  with no messages is exactly one range, stopped builds are refused.
- `tools/pst/write-check.sh`: converts a source (the Tika and CFReDS PSTs and OST, the MSG, TNEF, OLM, EML and mbox corpora) with `onemail convert --to pst`, then compares every
  source message with every message in the new store (`PstOracle pstcheck`), checks the property bags against the raw tables (`propexact`), the node database against a
  scan (`dump`), and runs the libpff tools over the result (`pffinfo`, and `pffexport` against our reader through `tools/oracles/libpff-messaging.sh`).
- `tools/pst/memory.sh`: peak resident set for generated stores of 10,000 to 1,000,000 messages and one folder of 600,000.

Results of the last full run (Checked build, x64): 218 PST tests and 64 conversion tests pass; the write-check above over ten sources writes 8,922 messages with 0 unmatched
(OLM not compared by `pstcheck`) and 0 property differences, `pffinfo` opens every file, and `pffexport` finds every item. What libpff reports differently is libpff's own behaviour, not a defect in the file:
it decodes narrow strings with the `-c` code page of the tool and ignores `PidTagMessageCodepage`, it gives an unnamed attachment the name `Attachment.txt`, it leaves
`IPM.Post` items out, and it drops the first letter of two OLM subjects (both letters end in the byte 0xD1). Peak memory: see `benchmarks.md`.

Two things the corpora found: libpff refuses a name-to-ID map whose entry, GUID or string stream is empty (so a store with no named properties, or none of one kind, gets
a placeholder: the numbered name 0x8503 in the Common set and the public string "Keywords"), and it decodes the `PtypString8` values of an item by its own code page, so
the builder adds `PidTagMessageCodepage` (taken from the item's code page rule) to an item that has narrow strings but did not carry one.

## Not verified

Needs Outlook and `scanpst.exe`, which are not available yet: that Outlook opens the files, FMap and FPMap pages and the name-to-ID bucket choice (above), that the
hierarchy and contents rows Outlook expects beyond MS-PST's minimum template (replication columns, view descriptors) are enough, that a store without
`PidTagValidFolderMask`, `PidTagIpmSubTreeEntryId` siblings such as the outbox and sent-items entry IDs, and without a message-store `PidTagRecordKey`-derived search
key opens without a repair prompt, and the `cRef` of blocks shared by several nodes (none are shared here).

## In place

`PstStore.OpenForWriting` (`RecursiveAI.OneMail.Pst.Messaging.PstWritableStore`) and, underneath it, `NdbEditor` (`RecursiveAI.OneMail.Pst.Ndb`) change an existing
Unicode PST: add, replace, move and delete folders and items, in place and crash-consistently. **This code has not been through an independent review of its
commit path yet, and has not been opened in Outlook or checked by `scanpst.exe`.** Treat it as preview quality and keep a copy of files you cannot lose.

### Opening

The file is opened exclusively (`FileShare.None`): a second editor, or any other reader, gets an `IOException`. Only Unicode files with 512-byte pages (wVer 21 and
23) and the personal-store magic `SM` are writable. ANSI files, offline stores (OST), 4 KiB-page files, other versions and files with protected or unknown block
encoding are refused for good (`NdbEditRefusedException`, with every reason in `Refusals`; `CanAllow` false). Anything else that looks wrong is refused unless the caller names it
in `Allow` (`NdbOpenRisks`), one class at a time:

| Finding | `NdbRefusalReason` | Allowed by |
|---|---|---|
| a header CRC or the sentinel does not match | `HeaderChecksum` | `HeaderChecksum` |
| `fAMapValid` is not 1 or 2 (a writer stopped), or an AMap page fails its checks, or the AMaps' free space is not the header's `cbAMapFree` | `AllocationMaps` | `AllocationMaps`: every map is rebuilt from the two B-trees (MS-PST 2.6.1.3.7) before the first commit |
| the density list page is damaged | `DensityList` | `DensityList`: rebuilt from the maps |
| more than `DefectThreshold` (default 0) other defects from the node database reader | `Damage` | `Damage` |

Bytes after the header's `ibFileEof` (a stopped commit that was growing the file) are not a finding; they are cut off at open.

### Allocation (MS-PST 2.2.2.7.2 to 2.2.2.7.4, 2.6.1.1)

Space comes from the AMaps: blocks take 64-byte slots, B-tree pages 512-byte aligned runs of eight. A request is tried in the range used last, then in the ranges the
density list says have room (most free first), then in the last range, and otherwise a new 253,952-byte range is added at the end of the file with its AMap page (and its PMap,
FMap and FPMap pages where the layout calls for them, all-ones content as in files Outlook wrote). The density list is kept up to date (the 119 emptiest ranges) and written at each
commit; `ibAMapLast`, `ibFileEof` and `cbAMapFree` follow. PMap, FMap and FPMap pages of existing ranges are not changed (the PMap is deprecated, and the FMap is not used to find space).
Only allocation maps are consulted to find free space; nothing is taken from a slot a map marks used.

Freed space stays allocated until the commit that freed it has switched the header, because the committed tree still reaches it. Space taken and freed within one transaction is free at once.

### Changing the trees

A page or block of the committed file is never written over. Changing a B-tree page (MS-PST 2.6.1.1.8) copies it to new space, changes the copy, and puts the copy's reference
in a copy of the parent, up to a new root; the old page is freed at commit. Pages the transaction made are changed in place. A full page splits in two; a page left under half full
merges with a neighbour if the two fit in one page; an empty page is dropped; a root with one child is replaced by it. Block B-tree keys have the reserved low bit cleared.

Blocks carry reference counts (MS-PST 2.2.2.7.7.3.1): a new block starts at 2 (the tree entry and the one reference from the node, subnode entry or XBLOCK that names it). Dropping
a reference (2.6.1.1.7) lowers the count; at 2 or less the block is freed and its entry removed, and for an XBLOCK, XXBLOCK, SLBLOCK or SIBLOCK the blocks it names lose a
reference in turn. Blocks shared by several nodes (counts above 2, as in files Outlook wrote) keep their count until the last user lets go.

### The commit (`NdbEditor.Commit`)

Every write goes through one layer with a *barrier* that makes everything written before it durable (`FileStream.Flush(true)` for files). The sequence for a transaction:

| # | Step | What a stop here leaves |
|---|---|---|
| 1 | Data blocks were written to free space as they were made. Write the new B-tree pages to free space. **Barrier.** | The old state. The new pages and blocks are in space the on-disk maps still call free, and no header refers to them. |
| 2 | Write the header again with the **old** roots and `fAMapValid` = 0 (INVALID_AMAP). **Barrier.** | The old state, with the maps marked untrusted. |
| 3 | Grow the file if a new range was added. Write the changed AMap pages (new allocations marked, freed space cleared, new ranges' map pages) and the density list. **Barrier.** | The old state with maps that disagree with its trees. The flag says so; the next open rebuilds them. |
| 4 | Write the header with the **new** roots (`NBT`, `BBT`), `ibFileEof`, `ibAMapLast`, `cbAMapFree`, `bidNextP`, `bidNextB`, the NID counters, `dwUnique` + 1 and `fAMapValid` = 2, both CRCs, in one 564-byte write. **Barrier.** | The old state (header not yet written) or the new state (written). |
| 5 | Overwrite everything the commit freed with zeros (unless `ZeroFreedSpace` is false). **Barrier.** | The new state, with some freed content not yet wiped. |

So the single header write in step 4 is the commit point. The assumptions: a 564-byte header write at offset 0 is not torn (it fits one 4 KiB physical sector; the checks in
`tests/Durability` tear other writes at 512-byte sector boundaries but not this one), pages and blocks are written whole or not at all at the sector level, and barriers are honoured.
A stop in step 5 leaves freed blocks un-zeroed: they are free space no tree refers to, and the next transaction's frees do not revisit them; a caller who must be sure runs
`PstCompaction.CompactAsync`, which writes a new file. Uncommitted data written in step 1 or before stays in free space un-zeroed after a stop or a rollback, for the same reason.

When the header says the maps are invalid (a stop in step 2 or 3, or a file from another program), opening for writing needs `NdbOpenRisks.AllocationMaps`; the editor then clears every map,
marks the map pages, every B-tree page and every block the two trees reach, recomputes `cbAMapFree` from the result and rebuilds the density list, and writes all of it with the next commit.
Reading ignores the flag, as MS-PST allows.

`NdbEditor.Rollback` (and disposing without committing) drops everything since the last commit; nothing of the transaction was ever referenced by the file.

### Items and folders (`PstWritableStore`)

Operations: `AddMessage`, `ReplaceMessage`, `MoveMessage`, `DeleteMessage`, `CreateFolder`, `RenameFolder`, `MoveFolder`, `DeleteFolder`, `Commit`, `Rollback`. Item bodies, recipients, attachments, embedded
messages and named properties are written by the same code the builder uses (`PstItemWriter`), against the editor instead of a new file. Changes are collected per folder and written by
`Commit`, which rewrites for each touched folder: its property context (name, `PidTagContentCount`, `PidTagContentUnreadCount`, `PidTagSubfolders`, `PidTagAssociatedContentCount` when present;
every other property is copied unchanged), the contents and associated-contents tables (the committed rows with removed and replaced ones left out and added ones merged by row ID; the table's
own columns are kept, and added rows fill the columns the table has), the hierarchy table (rows added, removed and, for children whose counts or name changed, rebuilt from the old row), and
the hierarchy row each child has in its parent. Rows are streamed: a commit holds one row at a time, not the table. The name-to-ID map (NID 0x61) is rewritten when an item brings a new
named property; existing names keep their IDs (ID = 0x8000 + position), which needs the existing map to number from 0 without gaps (otherwise new named properties are dropped with a defect).

* A folder delete unlinks the folder (and its tables and subfolders) and commits that first; the items are then freed in batches, each a commit. A stop in between leaves a consistent file whose
  not-yet-freed items are unreachable.
* A move changes the node's parent and the two folders' tables; the item's blocks are untouched.
* Search folders' criteria and search update queues (MS-PST 2.4.8) are not maintained. A client rebuilds a search folder when it opens it; whether Outlook accepts that without a repair
  prompt is listed under "Not verified".
* Memory: B-tree pages changed in a transaction (4 KiB per touched leaf path) and one row at a time per table; `PstWriteOptions.AutoCommitRows` (default 20,000) commits at the end of an
  operation when that many rows are waiting. Rewriting a folder's table is linear in its rows, so a commit that touches a folder of 100,000 items reads and writes all of them.

### Compaction

`PstCompaction.CompactAsync(source, destination)` (`RecursiveAI.OneMail.Conversion.Stores`) streams the live store through `PstBuilder` into a new file: no free space, no leftovers. The source is not touched.
Folder properties other than name and container class are not carried (the loss report says so).

### How it is checked

* `tests/OneMail.Tests/Pst/NdbEditorTests.cs`, `PstWritableStoreTests.cs`, `PstWritableCorpusTests.cs`: allocation, growth past a range, splits and merges, reference counts, zeroing, exclusive open, rollback, refusals,
  item and folder operations, named properties, the Outlook-written Tika files edited and compared item by item, and the allocation maps of builder- and Outlook-written files agreeing with their block trees.
* `tests/Durability` (run by `tools/pst/inplace-check.sh`): power-cut replays, process kills and a soak, then the libpff tools over the edited files. Results are in `benchmarks.md` (*PST in-place writing*).

### Not verified

Needs Outlook and `scanpst.exe`: that Outlook opens an edited file without a repair prompt; the FMap and FPMap pages and the PMap left as they were in existing ranges; rows added to Outlook's contents tables
carrying only the columns our template fills (other columns absent); a folder created with only the properties the builder writes; search folders left to the client; the reader-side behaviour on
`fAMapValid` = 0 files. Not verified at all: 4 KiB or ANSI files (never written), and files over 2 GB.
