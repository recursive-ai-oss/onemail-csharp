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
| PMap, FMap, FPMap | Valid pages (CRC, trailer) of types 0x83, 0x82 and 0x85, every byte 0xFF. The PMap content is what Outlook wrote in the files we have (all ones). FMap and FPMap content and position follow the specification's statement that they must exist at the right intervals and the header's note that its own FMap covers the first 128 ranges; Outlook opens a 54 MB store written this way (one FMap page, at range index 128) and `scanpst.exe` validates its allocation maps with no finding; neither names the FMap or PMap pages, and no store reaches the first FPMap page (over 2 GB). |
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
  an OLE or raw attachment storage kept as a compound file in a file attachment (`PropertyAdjusted`; `ObjectStorageDropped` is no longer raised); embedded messages nested deeper than `MaxEmbeddingDepth` (32); more than 65,535 recipients or attachments in one message.
- *Not stored*: the table bookkeeping columns `PidTagLtpRowId` and `PidTagLtpRowVer` (they belong to the table).

**Name-to-ID map** (MS-PST 2.4.7): GUID stream (property sets other than the two well-known ones), entry stream (one NAMEID per name: numeric ID or string-stream offset,
the GUID index and N bit, the property index), string stream (length-prefixed UTF-16, padded to four bytes) and 251 hash buckets of NAMEID records, each
holding the CRC of the name for string names. The bucket number is `(dword 0 XOR low word of dword 1) mod 251`, with the CRC as dword 0 for string names; the specification
leaves open whether the CRC or the offset selects the bucket for a string name, and Outlook does not show which choice it makes (below, *Not verified*).

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

## What Outlook requires beyond MS-PST

These rules are not in MS-PST (or not where a writer would look). Each one was found by opening a written store in Outlook 16.0.20527 and running its repair tool, `scanpst.exe` 16.0.20527.20088, over it: the tool says what it is looking for, and the store changes until it stops saying it. `tools/outlook` drives both; the fixtures that show each rule are the builder and edited stores listed under *Checked against Outlook*.

| Rule | What the repair tool did without it |
|---|---|
| The header's NID counters are at least the highest index in use: one counter for the folder-table types (0x0D to 0x0F share a folder's own index) and one for attachments (type 5), and an embedded message takes the next message NID of the top-level message (not the counter's own value) | reported the high-water marks and the embedded NIDs as inconsistent |
| The store carries the receive-folder table (NID 0x62B, with its one row), the outgoing queue (0x64C), the template tables 0x6B6, 0x6D7 and 0x6F8, and the nodes 0xC01 and 0xEC1 | reported each as missing |
| Search folders have their update queue and criteria nodes, empty when there is nothing to search | reported the search folder as lacking them |
| The template tables have the columns `PidTagLtpRowId`/`PidTagLtpRowVer` plus 0x0E30, 0x3013, 0x682F and 0x0058 where Outlook's files have them | reported the column sets |
| A hierarchy row has a cell only where the folder's own property context has the property | compared the row with the folder and reported a stale row |
| Table columns sort as unsigned tags, so named-property columns (IDs from 0x8000) stay in order | "tags not all increasing", then every other column missing |
| `PidTagMessageSize` is the bytes in the data trees of the message node and all its subnodes (embedded messages included) and is recomputed on every write, whatever the item carried; `PidTagAttachSize` is the sum of the attachment's value sizes with an embedded message counted whole | reported a size that disagrees with the node as invalid, and a folder row that disagrees with the message as stale |
| An embedded message's own recipient and attachment tables count toward its size; a recipient without `PidTagRecipientType` is written as To (reported as `PropertyAdded`), because the tool drops a typeless recipient table when it measures the message | an embedded message with a typeless recipient was measured too small |
| `PidTagMessageClass` is a Unicode string (a PtypString8 class is rewritten, `PropertyAdjusted`) | reported the message as having no class |
| `MSGFLAG_HASATTACH` is set exactly when the message has attachments, in both directions (`PropertyAdjusted`) | Outlook did not show the attachments; a stale flag was reported |
| Heap blocks are full except the last; a row matrix's last block ends at its last row even when it holds a full block's rows (never padded) | "bad LCB" on a table of exactly 64 rows per block |
| The editor refuses to move or delete the top-level folders of the store, or to move anything into the root (`InvalidFolderOperation`) | rejected a store where those folders had another parent |

Outlook can still make ANSI stores when asked (`wVer` 14); the reader notes for them are in `pst-ltp.md` and `ambiguities.md` (LT5, LT6). The writer makes Unicode stores only.

### Checked against Outlook

Outlook 16.0.20527 opened each store through its object model (`tools/outlook/Check-Stores.ps1`: no dialog, the folders, item counts, subjects, classes, recipient and attachment counts, read state and sender compared with our reader's view) and `scanpst.exe` scanned it without repairing. Outlook-written stores run the same way as controls: they open, and `scanpst.exe` reports only minor inconsistencies on them, which is the baseline a written store is judged against. The table gives the last run (the code in this repository, Outlook and `scanpst.exe` as above):

| Set | Files | Opened in Outlook, views agree with ours | `scanpst.exe` |
|---|---|---|---|
| Builder: two Tika PSTs, the generated MSGs, three folders of ICS and vCard items, 3,000 generated messages, 5,032 Enron messages (54 MB, so one FMap page) | 8 | 8 of 8 open, no dialog; views agree 7 of 8 (below) | 7 minor only; 1 with two findings (below) |
| Outlook-written stores and the durability bases made from them and from the builder | 5 | 5 of 5 | 5 of 5 minor only |
| In-place edits, tables changed row by row (the 21 durability scenarios: add, delete, folders, grow, split, named, mixed on three bases) | 21 | 21 of 21 | 21 of 21 minor only |
| The same edits with tables written whole | 21 | 21 of 21 | 21 of 21 minor only |
| Compaction of edited stores through the builder | 3 | 3 of 3 | 3 of 3 minor only |
| One folder of 60,000 items (builder) | 1 | not opened | minor only, 60,010 items found |
| One folder of 100,000 items (`Durability tables-make`), before and after 200 items were added, deleted or moved in place (rows changed one by one) and 200 added with the table written whole | 5 | 5 of 5, folder counts 100,010, 100,210, 99,810, 100,010 and 100,210, all equal to ours | the tool did not finish in 15 minutes on either of two files and reports errors (below) |

## Not verified

Still open after the Outlook runs:

- FPMap pages (a file over 2 GB), 4 KiB-page stores and ANSI files are never written, so none has been through Outlook.
- The bucket a string name takes in the name-to-ID map (CRC or offset, above): Outlook opens stores with named properties and the repair tool says nothing about the map, which does not show which choice it makes.
- The `cRef` of blocks shared by several nodes (none are shared here).
- Item kinds with Outlook-only settings: whether the search folders' contents are rebuilt silently when Outlook opens a store that had items added to a folder a search covers; Outlook's behaviour on `fAMapValid` = 0 files.
- The one finding just above (folders over 65,535 rows): a repair-tool limit, checked against an Outlook-written folder.

**One finding that is not a clean pass: folders over 65,535 rows.** Outlook opens such a folder and shows every item, but `scanpst.exe` does not accept the table. A folder of
60,000 builder-written items scans with only minor findings (60,010 items found, about a minute); the same builder with 66,000 items reports `Row matrix ... bad row count
(cRow=66000, cRowBTH=464)`, a computed content count of 0 against 66,000, and each message "added back to the database". 464 is 66,000 modulo 65,536, so the tool counts the row
index in 16 bits. **The same happens to a folder Outlook wrote:** `tools/outlook/Copy-Folder.ps1` opens a store of ours with 70,000 items in Outlook, adds a new PST and copies the folder
inside Outlook (`Folder.CopyTo`, 69 s for 70,000 items, where creating the items one by one made 30,000 in 22 minutes). `scanpst.exe` over that Outlook-written file: "Errors were found",
`Row matrix (...) bad row count (cRow=70000, cRowBTH=4464)` (4,464 is 70,000 modulo 65,536), `Invalid PR_CONTENT_COUNT (computed=0, actual=70000)` and 70,000 "Adding message back to the
database". So the repair tool's limit is 65,535 rows per folder, whoever wrote the file; folders over it are checked by Outlook opening them and by our reader.

**Builder rules found by the 8-bit-string stores** (`from-msg.pst`, now clean): a `PtypString8` value of a column the template tables declare as `PtypString` (subject, class,
display names, recipient names) is written as `PtypString` (`PropertyAdjusted`), because the 8-bit value goes to an extra column and the standard one stays empty, which the tool
reports as "row doesn't match sub-object"; `PidTagDisplayTo/Cc/Bcc` are derived from the recipients when the item has none (the tool derives them too); an attachment that is an OLE object or
a raw storage keeps its storage as the bytes of a compound file in an ordinary file attachment (`PropertyAdjusted`), because Outlook hides an OLE attachment that has no object (it showed 2 of 3
attachments), and the way Outlook stores the object itself in a PST is not documented well enough to write it.

## In place

`PstStore.OpenForWriting` (`RecursiveAI.OneMail.Pst.Messaging.PstWritableStore`) and, underneath it, `NdbEditor` (`RecursiveAI.OneMail.Pst.Ndb`) change an existing
Unicode PST: add, replace, move and delete folders and items, in place and crash-consistently. Its commit path was reviewed by an engineer who did not write it (*Review*, below). Outlook opens the files it edits and `scanpst.exe` reports only minor findings on them (*Checked against Outlook*, above), but treat it as preview quality and keep a copy of files you cannot lose.

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
| the maps' totals add up, but a tree reaches a page or block they call free | `AllocationMaps` | `AllocationMaps`: rebuilt as above |
| more than `DefectThreshold` (default 0) other defects from the node database reader | `Damage` | `Damage` |
| a B-tree page fails its checks, repeats, or sits at the wrong level; a page or block lies outside the allocated part of the file the header describes (below its first allocation map, or past `ibFileEof` or the end of the file); `ibFileEof` is smaller than the first allocation map; a BID is implausibly large | `Damage` | never (`CanAllow` false): such a file is repaired into a fresh PST (recovery), not edited |

Opening walks both B-trees once (one pass over their pages and the block entries; no block is read), so a damaged page is found when the file is opened and not half-way through an edit.
The sizes in the header are not trusted: the file is never taken to be longer than it is, so a made-up `ibFileEof` or `ibAMapLast` cannot make the editor loop over, or a commit grow the file to,
a made-up number of ranges. `bidNextB` and `bidNextP` are raised above the BIDs the trees use when the header's are lower (a torn header write, another program's file), so new blocks never reuse a BID.

Bytes after the header's `ibFileEof` (a stopped commit that was growing the file) are not a finding, **provided nothing in either tree reaches them**; they are then cut off at open, and a rolled-back
transaction cuts the bytes it wrote past the end in the same way. If a tree does reach them, the file is refused for good and left as it was.

The exclusive open is `FileShare.None`: on Windows the operating system enforces it, on Linux and macOS the BCL takes an advisory `flock`, which stops other .NET programs and other editors but not a program that
does not look (such as the libpff tools or a backup that reads the file mid-commit). The BCL offers nothing stronger on these systems (mandatory locks are not available through it, and `FileStream.Lock` is a
byte-range advisory lock there too), so callers who share a file with other programs must coordinate with them: close the store before copying or reading the file, and do not edit a file another process has open.

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
every other property is copied unchanged), the contents and associated-contents tables (big ones row by row, see *Changing a table row by row*; the others from the committed rows, with removed and replaced ones left out and added ones merged by row ID; the table's
own columns are kept, and added rows fill the columns the table has), the hierarchy table (rows added, removed and, for children whose counts or name changed, rebuilt from the old row), and
the hierarchy row each child has in its parent. Rows are streamed: a commit holds one row at a time, not the table. The name-to-ID map (NID 0x61) is rewritten when an item brings a new
named property; existing names keep their IDs (ID = 0x8000 + position), which needs the existing map to number from 0 without gaps (otherwise new named properties are dropped with a defect).

* A folder delete unlinks the folder (and its tables and subfolders) and commits that first; the items are then freed in batches, each a commit. A stop in between leaves a consistent file whose
  not-yet-freed items are unreachable.
* A move changes the node's parent and the two folders' tables; the item's blocks are untouched.
* Search folders' criteria and search update queues (MS-PST 2.4.8) are not maintained. A client rebuilds a search folder when it opens it; Outlook opened every edited store without a prompt, but none of them had items added to a folder a search folder covers (listed under "Not verified").
* Memory: B-tree pages changed in a transaction (4 KiB per touched leaf path) and one row at a time per table; `PstWriteOptions.AutoCommitRows` (default 20,000) commits at the end of an
  operation when that many rows are waiting. A table that is written whole costs time proportional to its rows; a big table is changed row by row instead (next section), so a commit that touches a few rows of a folder of 100,000 items reads and writes a few blocks.

### Changing a table row by row

A commit changes a folder's contents, associated-contents or hierarchy table in place (`TableContextEditor`, `RecursiveAI.OneMail.Pst.Ltp`) when the table is big and few of its rows change; otherwise it writes the table whole, as before. "Big" means the row matrix is a subnode (more than about 3,580 bytes of rows). "Few" means the rows removed, replaced and added number at most one sixteenth of the table's rows (an internal option lets the tests and the durability runs pin either way). Anything the editor does not handle makes it hand the table back untouched and the commit writes it whole: a heap block that is not laid out as MS-PST 2.3.1 describes, a matrix whose last row is not the one the row index calls last, a cell value over 3,580 bytes, a table left with no rows, a differing column list. The result reads the same either way: the same rows (row ID, every cell) and the same item properties; a test runs both paths on builder-written and Outlook-written files and compares them.

What changes, and what is written:

* **Row matrix** (2.3.4.4). A removed row's place is taken by the table's last row, so the matrix stays dense and only the block of the removed row and the last block are written; the row index is told the moved row's new position. The matrix is therefore not kept in row ID order, which the format does not ask for (the row index finds rows by ID; readers enumerate through it). An added row goes after the last row, in a new block when the last is full; every block but the last is written at the full length of the first block (zeros after the last row), and the last as long as its rows, as the builder does. A replaced row keeps its place. PidTagLtpRowVer is zero in every row, as in a table written whole.
* **Row index BTH** (2.3.2, 2.6.2.2), keys and data four bytes each. A node is one heap allocation, so a node that gains or loses a record is allocated again (the old one freed first, so it usually lands in the same block) and the one record that names it, in its parent or the BTHHEADER, is changed in place. A leaf that outgrows 3,580 bytes splits in two and the parent gains a record (splitting in turn, up to a new root). A leaf that empties is removed from its parent, and a root left with one record is dropped. Nodes are not merged: the format asks for sorted keys, not full nodes. The key in an index record stays the first key of its child.
* **Heap** (2.3.1, 2.6.2.1). Only the blocks that change are written again. A freed allocation moves the later ones in its block up (2.6.2.1.3), its slot stays in the page map with no bytes and cFree counts it; a later allocation may take an empty slot only while every empty slot in the block is a counted free one, so a HID is never reused while something may still name it. New allocations go to the block of the one they replace, then the last block, then any block whose fill level promises room, then a new block (headers as 2.3.1.2 to 2.3.1.4 say). The fill level of every changed block is updated in block 0 or in the bitmap block that covers it. A cell value goes to a heap allocation; one that would not fit sends the whole table to the rewrite.
* **Blocks.** The heap and the matrix are data trees (2.2.2.8.3.2). The new tree names the unchanged leaves and, when none of an XBLOCK's leaves changed, the XBLOCK itself; each block the new tree shares with the old one gets one more reference (MS-PST 2.6.1.1.7) and the old root is released as part of replacing the node, so exactly the replaced blocks are freed (and zeroed) and a shared block loses the extra reference. New blocks are written to free space like any others and nothing names them until the commit's two-root write, so the NDB commit is unchanged and a kill at any point leaves the old or the new table. The subnode block of the table is written again with the matrix entry pointing at the new tree; its other entries (large values of rows that stay) are kept, those of removed rows dropped.
* **Cost.** Work is proportional to the rows changed plus the number of blocks in the table's trees (opening a table looks up each of its blocks; a rewritten XBLOCK names its leaves again). A commit holds the changed heap blocks and matrix blocks in memory (8 KiB each) and nothing per row of the table.

### Compaction

`PstCompaction.CompactAsync(source, destination)` (`RecursiveAI.OneMail.Conversion.Stores`) streams the live store through `PstBuilder` into a new file: no free space, no leftovers. The source is not touched.
Folder properties other than name and container class are not carried (the loss report says so).

### How it is checked

* `tests/OneMail.Tests/Pst/NdbEditorTests.cs`, `PstWritableStoreTests.cs`, `PstWritableCorpusTests.cs`: allocation, growth past a range, splits and merges, reference counts, zeroing, exclusive open, rollback, refusals,
  item and folder operations, named properties, the Outlook-written Tika files edited and compared item by item, and the allocation maps of builder- and Outlook-written files agreeing with their block trees.
* `tests/Durability` (run by `tools/pst/inplace-check.sh`): power-cut replays, process kills and a soak, then the libpff tools over the edited files. Results are in `benchmarks.md` (*PST in-place writing*).

### Review

The commit path was reviewed by an engineer who did not write it: `NdbEditor` and its map, page-store and B-tree helpers, `PstWritableStore` and its table rewrite, `PstCompaction`, the open-time
refusals, and the durability harness itself. The review read the code against MS-PST (2.2.2.6, 2.2.2.7.x, 2.6.1.1.x, 2.6.1.3.7) and this document, then tried to break it with the runs below.

**What was run** (Checked build, Linux x64, real files on local NVMe, libpff tools 20180714):

* An independent checker (`tests/OneMail.Tests/Pst/NdbChecker.cs`) that parses the bytes itself and recomputes, from the two trees, what the bookkeeping must say: every page and block trailer and CRC; B-tree
  order, levels and index keys; extents that overlap; every page and block marked used in the allocation maps (and used slots nothing reaches, reported separately as leaks); `cbAMapFree`; the density list;
  `ibAMapLast`; each block's reference count against the users that name it (nodes, subnode entries, XBLOCK/XXBLOCK/SLBLOCK/SIBLOCK entries); `bidNextB`, `bidNextP` and the NID counters. Its sensitivity is
  pinned by a test that damages each of those and expects the finding.
* The soak (`Durability soak`) runs it after every commit: 27,000 random operations (adds, deletes, moves, replaces, folder create/rename/move/delete) over a builder-written file and the two Outlook-written test
  files, 900 commits, 903 checker runs, **0 findings** (and 0 leaked slots); freed space was wiped and added items read back property for property each time.
* Power-cut replays (`Durability campaign`): each scenario's every write, barrier and length change is recorded, and the file is rebuilt as a power cut at **every** point could leave it: all earlier writes; a random
  subset of the writes since the last barrier (twice); the last write torn at a sector boundary; every write since the last barrier torn into a random subset of its 512-byte sectors. Each leftover must open, carry
  no defect the flag does not explain, equal **by a whole-store snapshot** (every folder and item with every property, attachment and recipient) one of the states the scenario passes through, and pass the
  checker; leftovers with invalid maps must also be repairable (open with `AllocationMaps`, add an item, check the result). 3 bases x 7 scenarios (add, delete, folders, grow past a range, split, named properties,
  mixed): 36,840 leftovers, **0 failures**. By the step of the commit the stop fell in: 30,845 stopped while data blocks and pages were written (step 1), 360 at the invalid-flag header write (step 2), 800 during the map writes (step 3), 360 at the commit header write (step 4), 4,370 inside the wiping (step 5), 105 after the last commit.
* Process kills (`Durability kill`): SIGKILL at the Nth write or barrier, and after a random time, on a real file: 2,520 runs (3 bases x 7 scenarios x 80 stops at the Nth write or barrier and 40 at random times), 2,137 killed before the scenario finished, **0 failures**; each file opened as the old state, the new state or a commit boundary.
* Leftovers through libpff: 378 leftovers (3 bases x 7 scenarios x 6 commit steps x 3 kinds): `pffinfo` opened 378 of 378; `pffexport` compared with our reader (`tools/oracles/libpff-messaging.sh`) on every second leftover with valid maps: 130 files, 130 agree. Before R2 was fixed, 9 of 40 sampled leftovers of the Outlook-written test file differed.
* Hostile files (`Durability hostile`): random byte, bit and 64-bit changes in the header, the ROOT and pages, with the checksums fixed up half the time so the change is judged on its content, then open (no flag,
  and with every flag), add an item, commit, delete it, commit: 36,000 cases (3 bases x 2 modes x 6,000), 0 hangs, 0 crashes, and 5 exceptions that are not a typed refusal (below).

**Verdict per question.**

1. *A stop leaving a file that is neither the old nor the new state, or that readers reject:* no, under the assumptions below. Replays and kills above; every leftover was the old or the new state (or, for
   multi-commit scenarios, one of the commit boundaries between), our reader and `pffinfo` opened all of them, and `pffexport` agreed with our reader on every leftover with valid maps that was compared. Leftovers
   with invalid maps (a stop between steps 2 and 4) are the old state with the flag set; the next editor open needs `AllocationMaps` and rebuilds them, which the repair check exercised.
2. *Overwriting a page or block the committed header still reaches; reusing freed space before the commit is durable:* no. Nothing is written to a committed page or block before the header switch except zeroing
   of space *taken in the same transaction* and the allocation maps themselves (which are not part of either tree). The maps clear freed bits in step 3, **before** the header switch, so for the window between
   steps 3 and 4 the on-disk maps call space free that the committed (old) state still reaches: the only protection is `fAMapValid` = 0, which every writer must honour (MS-PST 2.6.1.3.7). The editor honours it;
   a program that ignores the flag could allocate over the old state. Allocation consults only maps that were checked at open (below), and a transaction's own allocations are never visible to the committed state.
3. *Reference counts, AMap bits, `cbAMapFree`, the density list, `bidNextB/P`, NID counters, `dwUnique` consistent after commit and after a rebuild:* yes, as the soak and the checker above show, with three
   defects found and fixed (the NID counter, lagging BID counters and the maps-versus-trees check; see the table). `dwUnique` only has to differ per header write (MS-PST 2.2.2.6) and is incremented per commit.
   The checker used to report, as soft findings, NIDs of the table types 0x0D, 0x0E and 0x0F above their header counters (6 in the soak on the Outlook-written Tika files, and in the files as received). It was the checker's rule, not the files or the editor: a folder's hierarchy, contents and associated contents tables take the folder's own `nidIndex` (MS-PST 2.4.4) and so draw it from the folder's counter (type 0x02, or 0x03 for a search folder's contents table); neither Outlook nor the writer advances the table types' counters. The checker now compares those types against the folder counter, and reports other types per type with a count and the highest index, so a file that had the condition from the start is told apart from one that got worse. After the change the soak (1,000 operations, seed 11) reports 0 soft findings on the Outlook-written files and on ours.
4. *Zeroing live data:* no. Space is wiped only when it is freed, which needs the block's reference count to reach 1 (counts above 2 are decremented and the block stays), and after this review only when the
   block's own trailer (size and BID) agrees with the tree entry. Space taken and freed in one transaction is wiped at once and may be reused; space freed from the committed file is wiped after the
   header switch. A stop during wiping leaves un-wiped free space, never a wiped block the state reaches (replays cut inside the wiping writes: 4,370 leftovers, 0 failures).
5. *Completeness of the refusals and exclusive open:* see the table in *Opening*. OST, ANSI, 4 KiB, unknown encryption and unsupported versions are refused for good; checksum, map, density-list and
   damage findings are refused unless allowed one class at a time. The review added the cases that were missing (a lowered or nonsensical `ibFileEof`, maps that disagree with the trees, damaged tree pages,
   out-of-range BIDs). Exclusive open is advisory on Linux and macOS (above).
6. *Hostile input:* an open-time hang and unbounded growth on a made-up size, one data-destroying truncation, two endless loops and several untyped exceptions were found and fixed (table). What remains are a few exceptions thrown **before any
   write of the commit** while a folder's table or properties are rewritten from damaged data (`ArgumentException`: no such folder; a property whose value does not fit its type) or when a counter in the
   header leaves no NID index free (`InvalidOperationException`). Nothing is written and the store can be disposed; they are not typed refusals and are listed as follow-ups.

**Findings fixed.** R1-R5, R7, R8 and R10 have a regression test (`NdbReviewTests`) that failed before the fix; R6, R9 and the footer check of R5 are covered by the hostile runs and the replays.

| # | Finding | Fix |
|---|---|---|
| R1 | A header with valid checksums and a lowered `ibFileEof` made `Open` cut the file at that size with no flag: everything after it was destroyed on open. (An `ibFileEof` above the signed range cast to a negative size and threw `ArgumentOutOfRangeException`.) | Both trees are walked at open; a page or block outside the allocated part of the file refuses the file for good, and the cut is made only after that proof. The header's size is clamped to the file's length. |
| R2 | New NIDs started at the header's counter. `rgnid` is the last index allocated (MS-PST 2.2.2.6), and a file Outlook wrote uses the counter's own value for the embedded message of an attachment (a subnode): the first item added took that NID, and `pffexport` then exported the new item in place of the embedded message (9 of 40 sampled leftovers of the Outlook-written test file differed). | `AllocateNid` starts after the counter. 0 of 87 differ afterwards. |
| R3 | Maps whose totals add up but which call space free that a tree reaches passed the open checks; allocation would write over live data. | Refused (`AllocationMaps`), or rebuilt when allowed. |
| R4 | `ibAMapLast` or `ibFileEof` made up: a rebuild looped over, or a commit grew the file to, that many ranges. | Sizes are clamped to the file's length. |
| R5 | An index page naming itself or an ancestor, or at the wrong level, looped forever in lookups and inserts. A page whose footer was impossible indexed outside its buffer. | The child's level must be the parent's minus one on every step; the walk detects a page reached twice; footers are validated when a page is read. |
| R6 | A damaged tree page was found half-way through an edit (`InvalidDataException`), or `ArgumentOutOfRangeException` on a 64-bit offset above the signed range. | Found at open and refused for good; a read at such an offset finds nothing. |
| R7 | A header whose `bidNextB`/`bidNextP` lagged the trees (a torn header write that was allowed through, or another program's file) made new blocks reuse BIDs in use. | Raised above the largest BID in either tree at open and kept across rollbacks. |
| R8 | A block-tree entry whose size disagreed with the block's trailer made freeing zero the neighbouring blocks. | The trailer must agree before space is freed; otherwise the block is left allocated (a leak, never a wipe). |
| R9 | `DeleteFolder` followed any folder a hierarchy row named: a damaged table could delete an unrelated folder or recurse forever. | Only folders whose own node names this folder as parent are followed, each once. A chain of internal blocks deeper than any real file's is not followed. |
| R10 | A rolled-back transaction that had grown the file left its writes past `ibFileEof`. | They are cut off at rollback. |

**Assumptions that remain, stated plainly.**

* *Atomic sectors.* Pages and blocks are written whole or not at all at 512-byte sector granularity (the replays tear them at that granularity and pass). The commit point is **one 564-byte write at offset 0**;
  it fits one 4 KiB physical sector, which is what makes it atomic. If a device splits it at the 512-byte boundary, bytes 0-511 (the new roots) and 512-563 (`bidNextB` and the full CRC) can come from different
  headers: the full CRC then fails and the file is refused without `HeaderChecksum`. The replays that tear the header there (`DURABILITY_TEAR_HEADER=1`) show exactly that: 7,512 leftovers, 12 failures in the main run and 16 in a re-run of three scenarios, every one `HeaderFullCrcMismatch`, all at the two header writes of a commit, all of them
  `HeaderFullCrcMismatch`. With the flag, the new roots are used with the old `bidNextB`, which R7 now corrects. MS-PST has one header and no second copy, so this cannot be removed without leaving the format.
* *Barriers.* `FileStream.Flush(true)` is taken to make earlier writes durable before it returns (fsync). Disks that lie about flushes, or a file system that reorders across fsync, are outside what any test here can show.
* *Exclusive open* is advisory on Linux and macOS (above), so it protects against other .NET programs and other editors, not against a program that does not look.
* *Free space is not wiped after a stop.* Content written by a stopped or rolled-back transaction, and blocks freed by a commit that stopped before step 5, stay in free space un-wiped until reused; `PstCompaction` writes a file without them.
* *Sizes of blocks and pages* in the trees are trusted beyond the trailer check at free time; a crafted file whose extents overlap with consistent trailers is not detected at open (the checker detects it).

**Not covered**: Outlook's own behaviour on `fAMapValid` = 0 files and on a search folder whose source folder was edited; files over 2 GB, 4 KiB and ANSI files (never written).

### Not verified

Still open: Outlook's behaviour on a file with `fAMapValid` = 0; search folders whose covered folders were edited (the editor leaves their contents to the client); whether the repair tool or Outlook notices the PMap, FMap and FPMap pages left as they were in an existing range after a long run of edits (the edited stores here are one to a few ranges); files over 2 GB, 4 KiB and ANSI files, which are never written.
