# Writing PST files

What `RecursiveAI.OneMail.Pst.Ndb.PstFileWriter` and `RecursiveAI.OneMail.Pst.Messaging.PstBuilder` write when they make a new Unicode PST
(MS-PST, 512-byte pages, wVer 23), how they stay in constant memory, what a stopped build leaves behind, and what has and has not been checked.
Reading is in `pst-ndb.md`, `pst-ltp.md` and `pst-messaging.md`; the block, heap, property-context and table-context builders this sits on are described in
the writing sections of the first two.

New files only. A file is written front to back and never rewritten, so a new store has no freed blocks and there is nothing to zero. ANSI and 4 KiB
files are never written. Changing an existing PST in place is separate work.

## The file (`PstFileWriter`)

| Part | What is written |
|---|---|
| Header | 564 bytes at offset 0, **written last**: `!BDN`, `wMagicClient` `SM`, `wVer` 23, `wVerClient` 19, `bidNextP`, `bidNextB`, `rgnid` (the next index for each NID type, 0x400 at least), the ROOT record (`ibFileEof`, `ibAMapLast`, `cbAMapFree`, the NBT and BBT BREFs, `fAMapValid` = 2), `rgbFM` and `rgbFP` filled with 0xFF, `bSentinel` 0x80, `bCryptMethod`, and both CRCs. |
| 0x0000 to 0x4400 | Zero until the end, except the density list at 0x4200. |
| Ranges | From 0x4400 the file is a series of 253,952-byte ranges (496 × 8 × 64, MS-PST 2.2.2.7.2). A range starts with its AMap page, then a PMap page when its index is a multiple of 8, an FMap page at index 128 and every 496th after, an FPMap page at index 8,192 and every 3,968th after. Blocks (64-byte aligned) and B-tree pages (512-byte aligned) follow in the order they are made; none crosses into the next range, so a block that does not fit starts the next one and the gap stays free in the AMap. The last range is written whole, so a file is always 0x4400 plus a whole number of ranges (an empty store is 271,360 bytes, as Outlook's is). |
| AMap | 496 bytes of bits, one per 64 bytes, set for the map pages themselves and every block and page; trailer type 0x84, signature zero, BID equal to the page's offset. `cbAMapFree` is the sum of the clear bits. |
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
builder does not claim crash consistency, which belongs to in-place writing.)

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
