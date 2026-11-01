# MSG and OFT reading

`RecursiveAI.OneMail.Msg` reads Outlook message files (`.msg`) and templates (`.oft`) into the MAPI model (`docs/mapi.md`).
The container is MS-CFB, read through OneArchive's compound file reader; the content is MS-OXMSG. An OFT is an MSG whose
message class is a template; the same reader opens it.

## Using it

`MsgFile.TryOpen(path | memory | stream | source, ...)` returns false with typed defects when nothing can be read, otherwise a
`MsgFile` with `Message`. `ReadEverything()` loads every recipient, attachment and embedded message and returns the defects
found, so a caller can validate a file in one call. `MsgFile.IsMsg(source)` is the content sniff used by `MailFormatSniffer`:
a compound file whose root has a `__properties_version1.0` stream. The file extension is never consulted.

Opening reads the directory and the top-level property stream only. Value streams, attachment data and children are read on
demand: opening a 2 MB file with 1,321 recipients allocates 4.7 MB; a 3.5 KB file about 21 KB.

## Layout read (MS-OXMSG 2.1-2.4)

- Root: `__properties_version1.0`, `__substg1.0_<tag>` value streams, `__recip_version1.0_#XXXXXXXX` and
  `__attach_version1.0_#XXXXXXXX` storages, `__nameid_version1.0`.
- Property stream headers: 32 bytes for the top-level message, **24 for an embedded message** (2.4.1.2), 8 for recipients and
  attachments. Entries are 16 bytes: tag, flags, then 8 bytes of value (fixed types) or size and reserved (variable types).
- Embedded messages and object attachments are in `__substg1.0_3701000D`; as a storage it is read as a message (or raw
  storage for other objects), as a stream its bytes are the value.
- Named properties: the GUID, entry and string streams of `__nameid_version1.0` map IDs from 0x8000 to names (2.2.3). The
  entry's own index is ignored in favour of its position (`NamedPropertyIndexMismatch`). Embedded messages use the top-level
  map; their own is ignored (`NestedNamedPropertyMap`).
- Multi-valued properties: fixed-length elements in one stream; variable-length ones as a length stream plus one value stream
  per element (2.1.3). The length stream is read at open.

## Code page for 8-bit strings

Resolution order, first hit wins: the object's own `PidTagMessageCodepage`, the ANSI page of `PidTagMessageLocaleId`,
`PidTagInternetCodepage` (unless the strings are Unicode), the enclosing message's answer, then 1252. Reported as
`AmbiguousCodePage` when there is no `PidTagMessageCodepage` and 8-bit strings exist. The order was fixed by the Apache POI
and Tika files that carry only a locale or an Internet code page (a Big5 file, a Cyrillic file, a German file in UTF-8 form).
`MapiObject.String8EncodingSource` says which rule applied. An unknown page falls back to 1252 (`UnsupportedCodePage`).

## Size field of strings

MS-OXMSG says a string's size is the stream length plus the terminator for Unicode (2.4.2.2). ANSI files written by some
producers store the terminating NUL inside the stream. Either is accepted; any other mismatch is `ValueSizeMismatch`, and the
stream's bytes are the value.

## Damage policy

| Situation | Result |
|---|---|
| Not a compound file / damaged container | Open fails: `NotACompoundFile`, `CompoundFileInvalid` (OneArchive's reader is strict, see below) |
| No `__properties_version1.0` | `NotAMessage` |
| Entry without its value stream | property kept, empty: `ValueStreamMissing` |
| Value stream without an entry | read as a property: `AmbiguousOrphanValueStream` |
| Property stream truncated | whole entries kept: `PropertyStreamTruncated` |
| Same tag twice | both kept, last wins: `AmbiguousDuplicateProperty` |
| Recipient/attachment counts differ from header | storages found win: `RecipientCountMismatch`, `AttachmentCountMismatch` |
| Strings of the other kind than the store mask says | read by their own type: `AmbiguousStringType` |
| Unknown streams | ignored: `UnreferencedStream` |

The full list is `MapiDefectKind`. By default a compound file that breaks the format is refused (`CompoundFileInvalid`); two
minimised fuzzer files in the Apache POI set are in that class. `MsgReaderOptions.Recovery` opens them (next section).

## Recovery (`MsgReaderOptions.Recovery`, `MaxRecoveryBytes`)

Off by default. When on, a file that passes the strict checks is read exactly as before (`MsgFile.IsRecovered` is false, no extra
defects). Otherwise OneArchive's compound file recovery opens what it can (a file cut short, FAT, DIFAT or mini FAT entries that point
outside the file or at another chain's sectors, chains that loop or end early, a lost directory chain, invalid directory entries or
links) and the message is read from what survives:

| Defect | Meaning |
|---|---|
| `CompoundFileRecovered` | once per file: recovery ran; the offset is 0 |
| `CompoundFileDamage` | one per repair the compound file reader made. The offset is a **file offset**, the location is empty, the detail names the damage kind (`HeaderInconsistent`, `Truncated`, `AllocationTableUnusable`, `MiniStreamUnusable`, `DirectoryUnusable`, `BadEntry`, `BadLink`, `OrphanedEntry`, `DamagedStream`), the directory entry, the length and the reason |
| `StreamDamaged` | a stream whose sector chain ends before the size its directory entry declares. The location is the stream's path, the offset the length kept, the detail the declared size. Reported once per stream, on the object that owns it |

A stream cut short gives the bytes before the break, so a property stream yields the entries it holds (`PropertyStreamTruncated` for
a partial one) and a value stream gives a shorter value (`ValueSizeMismatch`). Nothing throws. Damage inside a stream's bytes is not
detected. A file with no usable header, FAT or directory is still refused, as is one whose tables sit in a lost tail; the limits
(`MaxRecoveryBytes` and the compound file entry limits) are never recovered from. `MsgFile.IsMsg` and format sniffing stay strict, so
`Mail.Open` does not open a damaged file; use `MsgFile.TryOpen(..., new MsgReaderOptions { Recovery = true })`.
`MsgTool damage` and `tools/msg/regress.sh` damage messages in memory in about 30 ways and report what recovery returns against the
intact original.

## Limits (`MsgReaderOptions`)

Properties per object 1,048,576; recipients and attachments 65,536 each (the specification says 2,048; real files exceed it);
embedding depth 32; values per multi-valued property 1,048,576; bytes per value 256 MiB; kept defects 10,000. Sizes are checked
before anything is allocated. The compound-file reader has its own total-size limit (1 GiB by default).

## Property-exact re-encoding

`MsgStreams.Encode` re-encodes an object's property stream and value streams from the model. For all 53 openable corpus files
and 7 generated fixtures, every object's property stream and every value stream re-encode byte for byte (see below), so a
writer can be built on the model without loss.

## How it is checked

- `tests/OneMail.Tests/Msg`: generated fixtures (test-side builder, `testdata/msg/generated`, regenerate with
  `tools/msg/MsgTool gen`) and the public corpus when fetched (`tools/corpus/fetch-msg.sh`).
- `tools/msg/MsgTool streams <file>` compares the re-encoded streams with the file's.
- `tools/msg/compare_extract_msg.py` compares, per object, property tags, flags, entry bytes, value stream bytes, decoded
  strings and named-property names against the Python package extract-msg, run as a released package (version in
  `tools/msg/README.md`). Differences are either listed with a reason in the script or reported as unexplained.
- Fuzz target `msg` (`fuzz/run.sh msg <seconds>`): patches a valid fixture and opens it, enumerating everything.

Results (62 files: 55 corpus + 7 generated): 60 open, 60 re-encode identically; 58 compared, 1,516 objects, 15,609
properties, 9,822 value streams, 6,131 decoded strings, 955 named properties, **0 unexplained**. Explained: 36 strings in files
with no message code page (the oracle uses a fixed default; ours follows the rule above), one orphan stream, one signed
attachment the oracle exposes no properties for. Not compared: the two refused files, a generated empty file and a generated
file with an OLE attachment (the oracle cannot open them).
