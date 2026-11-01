# Recovery and forensics

Opt-in readers that return what a normal reader leaves out: messages freed or unlinked inside an Outlook Express database, and the
contents of an Outlook for Mac archive whose zip is damaged. Every recovered item says where it came from, how far to trust it and what is
missing. Recovery reads its input and nothing else: it never writes to it, and a repaired copy is always a new file.

PST and OST scan-mode recovery will use the same model (see Provenance, PST locators); the PST NDB layer already has scan mode
(`NdbFile.Scan`) and the span-level page and block parsers below.

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

`onemail recover [--format eml|msg] [--include-duplicates] <input.dbx | store directory | input.olm> <output directory>` writes each recovered item
(DBX: only what the live reader does not list; OLM: every item that can be read, tagged) and `recovery-report.json` beside them: input, format, one
entry per item with file, source, status, bytes, ranges, lost ranges and defects, the OLM damage records and defects, and a count per tag.
The output directory must be outside the input.
