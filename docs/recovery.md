# Recovery and forensics

Readers that return what a normal reader leaves out: what a damaged PST or OST still holds, messages deleted from one, messages freed or
unlinked inside an Outlook Express database, and the contents of an Outlook for Mac archive whose zip is damaged. Every recovered item says where it came from, how far to trust it and what is
missing. Recovery reads its input and nothing else: it never writes to it, and a repaired copy is always a new file.

PST and OST recovery is built on the NDB layer's scan mode (`NdbFile.Scan`) and uses the same model; see PST and OST below.

## Provenance

`RecursiveAI.OneMail.Recovery.RecoveryProvenance`, reached from the shared view as `IMailItem.Recovery` (null for items read in the ordinary
way; only items from a recovery reader carry one).

| Member | Meaning |
|---|---|
| `Status` | the tag, below |
| `Format` | the container: `Dbx`, `Olm`, later `Pst` |
| `Ranges` | byte ranges in the source file the item's bytes came from, sorted by offset, merged where they touch; absolute offsets |
| `Lost` | ranges that held part of the item and could not be read back (the four bytes a DBX free-list link overwrote; the bytes a zip reader skipped or found damaged) |
| `Defects` | `MailDefect`s that qualify the tag, with absolute source offsets; the family is `dbx-recovery` or `olm-recovery` |
| `Nid`, `Bid` | PST locators; null for the other formats |
| `Reason` | one line for reports |

| Tag | Means | Never means |
|---|---|---|
| `Live` | reachable from the store's live structure and whole (OLM: the zip entry reads clean) | a recovered DBX item is never `Live`: a DBX recovery lists only what the live reader does not |
| `SoftDeleted` | still linked into the structure but marked deleted (PST items in the hidden deleted set, flagged records) | DBX and OLM have no such state, so neither produces it |
| `Orphaned` | whole, live-shaped content that nothing in the live structure reaches | a copy of a live item |
| `FreeSpace` | read out of space the store has freed; its structure was overwritten in part, so the defects say what is missing | complete |
| `Partial` | only part of the item could be recovered: a chain cut at an object that is not part of it, a damaged or truncated archive entry | |

`Live` false positives are the one thing recovery must not make. Every DBX recovery path removes the offsets of everything the live reader
reaches before it looks (below), and drops chains that are byte-identical to a live message.

## PST and OST: damaged stores, deleted items, repair

`Pst.Recovery.PstRecovery` (ANSI, Unicode and 4 KiB OST files; encoded or not) recovers what a store still holds without trusting its header,
B-trees, allocation maps or density list, hands every message out with a provenance, and writes the result into a new Unicode PST. It reads the
source only. Facts are from MS-PST (2.2.2.7 pages, 2.2.2.8 blocks, 2.2.2.7.7.4 NBTENTRY: a message node's `nidParent` is its folder, a folder's is
its parent folder, 2.3.1 heap-on-node headers) and from files in the corpus.

### How a store is read

1. **Header.** If it reads (magic, a known `wVer`), it is used. If not, the generation is inferred: each of the three layouts is tried over the first
   32 MiB and the one whose trailers check out most often wins (a wrong layout finds almost nothing: a trailer needs a matching signature and
   checksum). The encoding is inferred from the data blocks the scan finds: the three `bCryptMethod` values are tried on up to 600 sampled blocks and
   the one that makes the most of them start with a heap header (`ibHnpm`, `bSig` 0xEC, a known client signature) wins; none found means not encoded.
   `PstRecoveryDefectKind.HeaderUnusable`. A header whose CRCs fail but whose fields read is used as it is, as the normal reader does.
2. **The live walk.** The node tree and block tree are walked from the header's roots. The walk is *clean* when it ends with no defect and at least one
   entry. A clean walk on a non-forensic recovery needs nothing else: the store is read as it stands and every item is `Live` or `SoftDeleted`.
3. **Scan.** Otherwise `NdbFile.Scan` (damaged structures included) finds every page and block: a block is accepted only when its signature and
   checksum match, so the **block index** is rebuilt from clean blocks alone (a BID is never reused, so a second clean copy is the same block). A
   block that fails either check is left out and counted (`BlockDamaged`); the range is kept for `Lost`.
   A page trailer that still stands on a unit a later, smaller block was written over is not taken for the unit's only content: blocks are looked for
   in the unit too (a real Outlook file has such units).
4. **Node index.** Every leaf page of the node tree found by the scan is parsed (NBTENTRY: NID, data BID, subnode BID, parent NID). Entries of a page
   whose checksum fails are kept only when the blocks they name are in the block index (`DamagedPageEntriesKept`). The **newest page wins**: pages are
   ordered by their page BID (allocated in increasing order), and the first copy of a NID in that order is the current one. Entries that differ from
   the current one are earlier versions.
5. **The messaging layer** reads the rebuilt index as it reads any store (`PstStore` over an `NdbFile` whose lookups use the index), so folders,
   messages, recipients, attachments, embedded messages and the name map come out of the usual code. Messages are found from the node index by NID
   type, not through folder tables, so a destroyed hierarchy or contents table loses no message; the folder of a message is its `nidParent`.
6. **Messages with no node.** When the node tree is damaged (or in forensic mode), the data blocks that no node (live or stale) accounts for are
   searched for property contexts (`ibHnpm`, 0xEC, client signature 0xBC) that carry a message class. Each is a message whose NID, recipients,
   attachments and subnode-held values are lost (`SubnodesNotFound`): the link from a message to its recipient and attachment tables lives only in
   the node entry, and guessing it from file order would put one message's recipients on another. A block that equals a live message's data block
   is a copy of a live item and is left out.

### Tags

| Tag | When |
|---|---|
| `Live` | the node tree's own walk reaches the node, its folder's contents table lists it (or that table cannot be read: `ListingUnknown`), and every block it needs reads back whole |
| `SoftDeleted` | (forensic) the node tree reaches the node, but its folder's contents table (read cleanly) does not list it (`NotListedByFolder`), or its folder is not in the folder hierarchy (`FolderNotInHierarchy`) |
| `Orphaned` | the node tree does not reach it. With a damaged tree: the entry was rebuilt from the newest leaf page (`IndexEntryRebuilt`), and a repaired copy files the item in its folder; a deleted node whose page survived looks the same, which cannot be told once the tree's root is gone. With an intact tree (forensic): the node is only named by an older page (stale), or an earlier version of a live node's data (`SupersededVersion`, only with `IncludeSuperseded`), or a message block no node names that the block tree still lists. Stale and superseded items are filed under `Recovered` |
| `FreeSpace` | (forensic) a message block no node names and the block tree no longer lists, so the space is free (`SubnodesNotFound`) |
| `Partial` | the item needs a block that is missing, fails its checks or lies past the end of a truncated file (`BlockMissing`, `BlockDamaged`), or the messaging layer repaired or skipped part of it (`MessageDamaged`). Replaces the tags above except `FreeSpace` |

`Live` is claimed only for nodes the store's own structure reaches, so a damaged store with no walkable tree has no `Live` items at all: everything the
scan rebuilds is `Orphaned` and says so. This is deliberate: the one thing recovery must not do is call a deleted item live.
`Ranges` are the whole blocks (data tree, subnode tree, and their internal blocks) the item was read from; `Lost` the blocks that failed their checks (clipped
to the file); `Nid` is null for items with no node; `Bid` is the data block.

### Repair into a new store

`PstRecovery.RepairTo(path, reportPath)` (or to streams) writes a Unicode PST through `PstBuilder` and a JSON report. Items go in their folders
(folder names and container classes are kept; other folder properties are not); items that are not `PlaceInFolder` go under
`Top of Personal Folders/Recovered/<tag>/<original folder>`. A value of a damaged item that cannot be read in full is dropped and listed in the
report (`droppedProperties`); the rest of the item is written. Each item carries three named properties in the property set
`6f1c2f4e-0a39-4bd1-9d5e-6a1e3d9c5b01`: `OneMailRecoveryStatus`, `OneMailRecoverySource` (`nid=0x..;bid=0x..;ranges=offset+length,..;lost=..`) and
`OneMailRecoveryReason`. Writing to the file being recovered is refused.

### Obfuscation and the password

Blocks encoded with the permute or cyclic method are decoded whatever the header says, and the password CRC (`PstStore.PasswordCrc`) is read and never
checked: neither protects anything (both are fixed, public tables or a public checksum). A store protected with Windows Information Protection
(`bCryptMethod` 0x10) cannot be decoded; its blocks are found but not read.

### Limits

The index is held in memory: about 60 bytes per block and per node, so the peak working set grows with the store (660 MiB for a 926 MiB file with
250,000 messages; about 165 MiB at 80,000 messages with a small allocation budget). Everything else streams, one item at a time. A message whose heap spans several blocks is lost with its node entry (only its first block can be found, and it reads incomplete). 4 KiB compressed blocks are found by the scan like any other; ANSI stores are
scanned and parsed from the layout but no ANSI file has been seen (generated stores only).

## DBX: deleted and orphaned messages

`DbxReader.RecoverDeleted(DbxRecoveryOptions?)` returns `DbxRecoveredMessage`s (bytes, provenance, segment offsets, and the range each segment
contributed; `ToMailItem()` parses the bytes and attaches the provenance). Facts are from the oedbx notes (DESIGN → Sources, cite only:
OE_Dbx_Deleted, OE_Dbx_Message, OE_Dbx_FileHeader, OE_Dbx_FileStructure) and confirmed on the 20 freed segments in the CFReDS Hacking Case
`Inbox.dbx`.

**How Outlook Express deletes a message.** Its tree entry is removed. The indexed-info record and each segment of the text chain get a deleted
object's header: five int4, which for a message segment (0x210 bytes) read

| Offset | Live segment | Freed segment |
|---|---|---|
| 0 | its own offset | its own offset (kept) |
| 4 | 0x200, the body length | 0x1fc, the length of the free body |
| 8 | the text length | 0x210, that plus 0x14 |
| 12 | the next segment of the chain | **the same next pointer, not cleared** |
| 16 | first four text bytes | the next deleted object (the list link) |

The freed segments hang on a single linked list whose root is at file header offset 0x48; freed records go on lists by size from 0x29c.
Everything else in the 0x1fc body bytes is left as it was. So a freed message keeps its whole chain (the old next pointers), minus the first
four bytes of each segment and minus the length of its last segment.

**Sources.**
1. *The deleted-message list* (`FreeSpace`). Each listed object must carry its own offset, 0x1fc and 0x210; a pointer outside the file, a
   wrong header, a segment a live chain also uses, or a loop ends the walk (`FreeListBroken` on the messages; a `StoreDefects` bit to the reader's
   handler). Chains are rebuilt from the old next pointers. A segment nothing points to starts a chain. Text per segment is the 0x1fc bytes at +20.
   The last segment's length is unknown (`LastSegmentLengthUnknown`): trailing zero bytes are dropped, anything else stays. The four overwritten
   bytes per segment are listed in `Lost` (`LeadingBytesOverwritten`); the content omits them, it does not pad.
2. *A scan for intact segments* (`Orphaned`): every 4-aligned offset (no unaligned segment appears in the corpus) whose first int4 is that offset,
   body length 0x200, text length 1 to 0x200, that no live chain, record or list reaches. Chains follow the next pointers and run to a pointer of 0.
   `Orphaned` chains whose bytes equal a live message are dropped (`DuplicateOfLive` when `IncludeDuplicatesOfLive` is set).

**Partial.** A chain whose next pointer leads into a live segment, another chain, or outside the file ends there: `ChainContinuesIntoOtherObject`,
`ChainLoopOrShared`, `ChainEndsAtBadPointer`. A chain found only as the left-over of a cycle starts at its lowest offset (`StartNotConfirmed`).
`StartNotConfirmed` is also the honest caveat for any chain whose first segment nothing points to: if its predecessor was overwritten by a later
message, the start is missing and cannot be told from a real start.

**What is not recovered.** Freed indexed-info records: the free header overwrites the length, object length, field count and one index entry, so
the record is not trusted; the message's own headers carry subject, sender and dates. Freed segments that are not on the list and are not
live-shaped (a list root zeroed and the headers rewritten). Deleted tree nodes. The `SoftDeleted` tag is never produced.

**Generating deleted stores.** `DbxBuild.Deleted` (test builder, `tests/.../DbxTestBuilder.cs`, used by `tools/dbx/DbxTool gen-deleted`) applies
the table above: tree entry out, segments freed in chain order each pushed to the front of the list, record freed on its size list.
`DbxBuild.Unlinked` removes only the tree entry (the orphan case).

**Checks.** `tools/recovery/check-dbx.sh`: recall on the generated stores against `manifest.tsv` (every deleted message's expected bytes, every
unlinked message whole), the re-read of every range from the file, no overlap with live segments, no copy of a live message; the same on the
real corpus; and a count comparison with `undbx -r` (`docs/compatibility.md`).

## OLM: damaged archives

`OlmReaderOptions.Recovery = true` opens through OneArchive's zip recovery mode (`ZipReaderOptions.Recovery`): a missing or unusable directory
is replaced by a scan of local headers; bytes between entries are skipped; damaged or truncated entries yield what decodes. OneMail has no zip code
of its own; this is OneArchive's reader with its damage records mapped.

| `OlmDamage.Kind` | From the zip reader | Effect |
|---|---|---|
| `DirectoryUnusable` | no end record, bad directory, or a local header not where the directory says | entries are found through local headers; the archive is read forward |
| `SkippedBytes` | bytes belonging to no entry | the entry behind them is found by scanning |
| `DamagedEntry` | data does not decode, or its size or CRC-32 is wrong | records before the damage are kept; the entry's items are `Partial` |
| `Truncated` | the archive ends inside an entry or before the directory | likewise |

Reading forward has no random access, so each entry is opened by a fresh forward reader starting at its local header. `OlmArchive.Damage` lists
the records with file offsets; defects `ArchiveRecovered` (once) and `EntryDamaged` (per damaged record entry) join `OlmArchive.Defects`.

**Item tags.** Before the records of an entry are parsed, the entry is read to its end and discarded, so damage is known before any item is
returned. An item is `Live` when its record entry and the attachment entries it lists read clean; `Partial` when one is damaged or cut short, or
when an attachment is missing from an archive that skipped bytes, was truncated or lost its directory (the missing entry may be what damage took).
`Ranges` is the entry's bytes (local header to the next entry's header); `Lost` is the damage records that name the entry. The verification
costs one extra decompression of each record entry, and of an item's attachments on first use of `Recovery`; recovery is opt-in for that reason.

A truncated XML record is not repaired: the record being read when the data ended is dropped (`EntryUnreadable`), earlier records are kept.

## Span-level TryParse

For tools that find pieces somewhere else than where a file's own structure puts them. None throws, none reads a file, and a span that is not
the piece gives false. They say what the bytes are, not whether the live structure reaches them; ownership is the caller's call. The library does
not carve media.

| Entry point | Recognises |
|---|---|
| `Mime.Reading.MimeHeaderBlock.TryParse(span, out MimeHeaderBlockInfo, maxBytes)` | a header block at the start of the span: field count, length, body offset, whether a blank line ended it |
| `Mime.MimeEntity.TryParse(span, out MimeEntity?, options)` | a message or fragment that starts with a header block; the bytes are copied, repairs are in `EnumerateDefects` |
| `Pst.Ndb.NdbSpan.TryParsePage(span, format, ib, out NdbPageInfo, defects, expectedBid)` | a page of any type (ptype 0x80 to 0x86), trailer fields and the type, signature, CRC and BID checks (MS-PST 2.2.2.7) |
| `Pst.Ndb.NdbSpan.TryParseBlock(span, format, ib, out NdbBlockInfo, defects, expectedBid)` | a block: tries each size the format allows until a trailer agrees with its size, signature and CRC (MS-PST 2.2.2.8); data is returned still encoded |
| `Pst.Ndb.NdbHeader.TryParse(span, out header, defects)` | the file header (existing) |
| `Stores.Dbx.DbxSpan.TryParseSegment(span, offset, out DbxSegmentInfo)` | a message segment, live or freed, by its own offset and fixed lengths |
| `Stores.Dbx.DbxSpan.TryParseRecord(span, offset, out DbxRecord?, out StoreDefects)` | an indexed-info record; a record cut by the end of the span keeps what is there and says so |
| `Msg.CfbSpan.TryParseHeader(span, out CfbHeaderInfo)` | a compound file header (signature, version, sector shifts); irregularities are flags, not refusals |
| `Msg.CfbSpan.TryParseDirectoryEntry(span, out CfbDirectoryEntryInfo)` | one 128-byte directory entry |

`CfbSpan` is a recogniser, not a reader: reading compound files is OneArchive's job.

Limits: the NDB block parse tries at most 129 sizes (4 KiB layouts: 128), each a bounded read; the MIME header block stops at 1 MiB by default;
DBX records at 1 MiB.

## Method

Generated deleted DBX stores (`tools/dbx/DbxTool gen-deleted`, seed 2026) and `tools/recovery/check-dbx.sh`; damaged OLM variants
(`tools/recovery/damage_olm.py`, seed 2026) and `tools/recovery/check-olm.sh`; unit tests in `tests/OneMail.Tests/Stores/DbxRecoveryTests.cs`,
`Olm/OlmRecoveryTests.cs`, `Recovery/SpanTryParseTests.cs`; fuzz targets `span-ndb`, `span-mime`, `span-cfb`, `dbx-recovery`, `olm-recovery`.

## CLI

`onemail recover [--format eml|msg] [--include-duplicates] [--forensic] [--pst] <input.dbx | store directory | input.olm | input.pst | input.ost> <output directory>` writes each recovered item
(DBX: only what the live reader does not list; OLM: every item that can be read, tagged) and `recovery-report.json` beside them: input, format, one
entry per item with file, source, status, bytes, ranges, lost ranges and defects, the OLM damage records and defects, and a count per tag.
The output directory must be outside the input.

For a PST or OST (`!BDN` at the start, or `--pst` when the header is destroyed) it writes `recovered.pst` and `recovery-report.json` (the library's
report: file format, inferred header, scan counts, defects, and per item: status, folders, locators, ranges, lost ranges, defects, dropped
properties) and ignores `--format`; `--forensic` adds the deleted, stale and free-space items.

## Method (PST and OST)

`tools/recovery/check-pst.sh <out>` on the worker: a generated store (`PstOracle pstgen`, 3,000 messages) and the corpus files, each damaged in 11 ways
(`PstOracle pstcampaign`: header zeroed, root pages zeroed, allocation maps zeroed, density list zeroed, random corruption of 0.1, 1 and 5 % of the
512-byte units (16 random bytes each, seeds 1 to 3), truncation to 25, 50 and 90 %), recovered, compared with the intact original by message signature
(every property, recipient and attachment), repaired into a new store that is reopened (no defect, same count), and exported by `pffexport -m all` next
to ours. `tools/recovery/memory-pst.sh` measures the peak resident set. Unit tests: `Pst/PstRecoveryTests.cs` (damage classes; forensic states built by
`PstDamage.RemoveFromTrees` and the builder's internals), fuzz target `pst-recovery`.
