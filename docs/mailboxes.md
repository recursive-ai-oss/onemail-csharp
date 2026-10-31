# Mailbox stores

How OneMail enumerates and writes mbox files, Maildir folders and Apple Mail (EMLX) stores. The code is in
`RecursiveAI.OneMail.Stores` (shared types, file-system abstraction, content sniffing) and its `.Mbox`, `.Maildir` and `.Emlx`
namespaces. Text here is written to be implemented the same way in another language. The cases where programs disagree are
listed with their resolutions in [`ambiguities.md`](ambiguities.md) (S1–S13).

## The store contract

A store reader does not parse messages. It yields **entries**: where a message is (a byte range of a source, or a file of a
directory store), what the store knows about it (flags, keywords, dates, UID) and what was irregular (**defects**). The MIME
layer parses the bytes on demand.

- An entry is plain data and holds no reference to its source. Opening the message is a call on the reader, taking the entry.
- File stores (mbox, EMLX) read from an `IRandomAccessSource`. Directory stores (Maildir, Apple Mail bundles) read from an
  `IStoreFileSystem`: list, open for reading, create new, rename, delete. A directory on disk, memory (tests, browser WebAssembly)
  and any storage mapped onto those calls all work. Paths use `/` and are relative to the file system's root.
- Enumeration: `Read()`/`ReadAsync(ct)` on the mbox reader, `Enumerate`/`EnumerateAsync(ct)` everywhere else. The synchronous and
  asynchronous paths are the same code.
- Nothing throws on malformed content. Defects are `StoreDefects` bits on the entry and, with the reader's
  `DefectHandler`, `(kind, byte offset)` pairs. `Ambiguous*` marks a choice between valid readings.
- Sniffing is by content: `StoreSniffer.Sniff(source)` (mbox, EMLX), `StoreSniffer.SniffDirectory(fs, path)` (Maildir folder,
  Maildir++ root, Apple Mail bundle), `MboxSniffer.Detect(source)` (variant). Extensions are never consulted.

## mbox

### Records tile the file

A file is `[preamble][record]…`. A record is `[envelope line][message][separator]`.

| Part | Bytes |
|---|---|
| Preamble | everything before the first envelope line (`PreambleLength`; empty for a normal file) |
| Envelope line | from `From ` through its line ending (LF or CRLF); the last line of a file may lack one |
| Message | the stored bytes, headers first, **still quoted** |
| Separator | one empty line (LF or CRLF), only if the message ends with one: the file's last record included |

`Offset + RecordLength` is the next record's `Offset` (or the file's end), so copying every record (and the preamble) reproduces the
file. `MessageLength` excludes the separator; a record whose message ends in an empty line and has no separator after it loses that
line to the separator rule (an unavoidable ambiguity of the format), and a message that does not end in a line ending has no
separator.

### Where a message ends

1. **Envelope lines.** A line starting `From ` at a line start begins a message when it reads as an envelope line (RFC 4155 §3):
   after the sender there is a month name next to a one- or two-digit day, and later a time containing a colon. That accepts
   `From a@b Mon Jan  1 00:00:00 2024`, `From 123@xxx Mon Jan 01 00:00:00 +0000 2024` (Gmail Takeout) and `From - Wed Jan 1 …`.
   Any other `From ` line is message text and sets `AmbiguousFromLine` (S1, S5). `MboxFromLineMode.Any` takes every `From ` line.
   Envelope lines longer than `MaxEnvelopeLineBytes` (4096) are text in strict mode, an envelope with `OverlongEnvelopeLine` in `Any` mode.
2. **`Content-Length`.** Unless the variant is mboxo or mboxrd, the reader looks at the header block (at most `MaxHeaderBytes`,
   64 KiB) for the first `Content-Length`. The message body is the length that many bytes after the empty line ending the headers.
   If what follows is the end of the file, or an envelope line, or one empty line and then either, the length is **valid**: the message
   ends there and its body is not scanned (so an unquoted envelope-looking line inside is kept). Otherwise the length is ignored and the
   message ends at the next envelope line, with `ContentLengthMismatch` (S2). Two differing values: the first wins, `AmbiguousContentLength`.
3. An envelope line with no empty line before it still begins a message: `MissingSeparatorLine` on the message before it (S8).

### Variants

| Variant | Body line quoting when writing | Reading | Notes |
|---|---|---|---|
| mboxo | `From ` → `>From ` | unquote exactly one `>` | not reversible: a stored `>From ` could have been either |
| mboxrd | `>*From ` gets one more `>` | unquote one `>` from `>+From ` | reversible; the writer's default |
| mboxcl | as mboxo, plus `Content-Length` | as mboxo; length found by the rule above | length counts the stored body |
| mboxcl2 | none, plus `Content-Length` | stored bytes as they are | only the length protects the text |
| Unknown (reading) | | non-length entries unquoted as mboxrd; entries ended by a valid length returned as stored (S9) | |

`MboxSniffer.Detect` guesses the variant from the first messages (up to 8 MiB or 2,000): a valid `Content-Length` means mboxcl, or
mboxcl2 when a length-delimited body holds an unquoted envelope line; a `>>From ` line means mboxrd; a lone `>From ` is taken as mboxrd.
No evidence leaves it Unknown. Reading never depends on this call.

### Memory

One pooled window (default 64 KiB) holds the bytes being scanned. A scan looks for the vectorised pattern `\nFrom ` and
checks each hit; a length-delimited message is skipped without being read. Enumeration allocates no objects per message (entries are
structs); the measured numbers are in the benchmark notes.

### Writing

`MboxWriter` appends one message at a time: the envelope line (`From <sender> <asctime date>`, UTC; sender and date default to
`MAILER-DAEMON` and the clock, both overridable per call), the message with the variant's quoting, a line ending if the message lacks
one, and an empty line. The file is complete after every call. For mboxcl and mboxcl2 an existing `Content-Length` header is
removed and a correct one added as the last header; a body without a final line ending gets one (the length counts it). A header
block over 1 MiB is refused.

`AppendToExisting` looks at the last bytes of a seekable file and adds a missing line ending or separator before the first new envelope
line. `AppendRecord` and `WritePreamble` copy bytes of another file verbatim, so read→write of untouched records is byte-identical.

### Metadata

`MboxMetadata` reads hints from the header block: `Status` `R` (seen); `X-Status` `A F D T` (answered, flagged, deleted, draft);
`X-Gmail-Labels` (comma separated, quoted labels may hold commas, encoded words decoded); `X-GM-THRID`; `X-Keywords`;
`Content-Length`. `MboxEnvelope` parses sender and date from the envelope line (no zone means UTC).

## Maildir

A Maildir folder holds `cur`, `new` and `tmp`. `MaildirStore` reads `cur` then `new`, each by file name (ordinal); `tmp` is
never listed; names starting with `.` inside `cur` and `new` are ignored.

- **Names.** `unique[:2,flags[,extra]]`. The separator before `2,` is `:` in the Maildir specification, which Windows file names cannot
  hold; Windows tools use `;` (isync's `InfoDelimiter` defaults to `;` on Windows and `:` elsewhere). **Reading accepts both on every
  platform** (a `;` counts only when `2,` follows it, so other semicolons stay in the unique part; a colon wins when both occur). **Writing**
  uses `MaildirStoreOptions.InfoSeparator`, default `;` on Windows and `:` elsewhere; only those two are allowed (S14). `dovecot-uidlist`
  file names are matched with either separator. Standard flags `D F P R S T` map to draft, flagged, forwarded ("passed"), answered, seen,
  trashed (`Deleted`). Lowercase `a`–`z` are Dovecot keyword slots. A file in `new` also carries `Recent`. `,S=<size>` and `,W=<vsize>` in the
  unique part are read as sizes. Any other info version (`:1,…`) is kept in `RawInfo` and not interpreted. Names are built with
  flags in ASCII order (S10).
- **Keywords.** Dovecot: `dovecot-keywords` lines `<index> <name>`, letter `a` is index 0. Courier: `courierimapkeywords/:list` (S13).
  A letter without a name appears as itself with `UnknownKeywordLetter`. A folder holds at most 26 Dovecot keywords.
- **UIDs.** `dovecot-uidlist`: a header line, then `<uid> [fields] :<file name>`; the entry's `Uid` is matched by the unique part.
- **Folders.** Maildir++: the root is the inbox (folder name `""`); a subfolder is a directory `.Name` holding `cur`/`new`, and
  the dot is the hierarchy separator (`.Archive.2024` is folder `Archive.2024`). Nested layouts (a folder inside a folder
  directory, as Python's `add_folder` makes on a subfolder) are not Maildir++ and are not read.
- **Delivery** writes the message under `tmp` with a unique name, then renames it into `new`, or into `cur` with `<separator>2,flags` when it has
  flags or keywords. Default unique names are `seconds.M<microseconds>P<pid>Q<counter>.<host>` with `/` and `:` in the host escaped; supply a
  factory for reproducible names. The message bytes are written unchanged.
- **`SetFlags`** renames into `cur` with the new info section; the unique part and the content do not change.
- **`CopyToAsync`** copies a store through the model: every message file under its exact name, and the folder control files
  byte for byte.

## EMLX and Apple Mail bundles

An `.emlx` file is a decimal byte count and a line feed, that many bytes of message, and (usually) an XML property list.
`EmlxReader.Read` returns the three ranges; a bad count line gives no message and the whole file as trailer (`BadByteCount`), a count
past the end of the file is cut there (`TruncatedMessage`). The property list gives the flag word, dates, remote id and conversation id
(S12); `Plist.TryParse` reads XML property lists only, ignores DTDs (nothing is fetched or expanded), and is bounded (1 MiB,
32 levels, 100,000 values). `.partial.emlx` files hold messages without their attachments, which Mail keeps in
`Attachments/<number>/` beside the `Messages` directory; the bundle reader names that directory and does not read it.

`AppleMailBundle` finds `Messages` directories at any depth up to eight below the bundle (older layouts hold `Messages` directly, newer
ones nest it under `<GUID>/Data/…`) and lists their `.emlx` files by number. `EmlxWriter.Write` writes a message with a new property
list; `WriteFrom` rewrites a file from its parts (canonical count line, message, original trailer bytes), so a file with a canonical
count line is reproduced exactly.

## Defects

| Defect | Where | Meaning |
|---|---|---|
| `PreambleBeforeFirstMessage` | mbox | bytes before the first envelope line, kept as the preamble |
| `MissingSeparatorLine` | mbox | no empty line before the next envelope line |
| `AmbiguousFromLine` | mbox | a `From ` line that is not an envelope line, kept as text (strict) |
| `ContentLengthMismatch` | mbox | `Content-Length` does not end at an envelope line; ignored |
| `AmbiguousContentLength` | mbox | two different `Content-Length` values; the first is used |
| `OverlongEnvelopeLine` | mbox | envelope line over the limit |
| `UnrecognisedFileName` | Maildir | info section is not version 2 |
| `UnsortedFlags` | Maildir | flag letters out of order or repeated |
| `UnknownKeywordLetter` | Maildir | a keyword letter with no name in the folder's keyword file |
| `MalformedControlFile` | Maildir | a keyword, UID or Courier list line that could not be read |
| `BadByteCount`, `TruncatedMessage` | EMLX | as described above |

## Checks

`tools/mailbox/run.sh` is the whole check (see its header). The method:

- **Boundaries.** `compare_mbox.py` runs the file through OneMail in an oracle-compatible mode (`Any`, Content-Length ignored), Python's `mailbox`
  and MimeKit's mbox parser (`tools/oracles/mailbox-driver`), and compares the message count, each envelope offset and message end. Each
  difference is tied to a table entry (S3, S4, S5); OneMail's default mode is compared with its own compatible mode, and every
  difference must carry the defect that explains it (S1, S2).
- **Ground truth.** `gen_mboxes.py` writes every variant, LF and CRLF, and edge shapes (no separators, preamble, wrong lengths, Takeout
  headers), with independent writers (hand-written quoting, Python's `mailbox.mbox`) and a sidecar holding the SHA-256 of each message as
  given. `OneMailRanges verify` reads them back.
- **Round trips.** Record copy of every mbox, a message-level rewrite (unquote, append again with the same variant, sender and date),
  Maildir copy through the model, EMLX rewrite from parts. Files that a message-level rewrite changes are listed with the reason
  (no separators or no final separator in the source, a preamble, a wrong length corrected, an unquoted writer, a Takeout envelope with a zone).
- **Writers.** `OneMailRanges write` makes every variant and a Maildir with flags and keywords; `py_check_written.py` reads them with
  Python's `mailbox` (mboxcl2 excepted, S6).
- **Fuzz.** Targets `mbox` and `emlx` (`fuzz/`): the window size must not change the entries, entries must tile the file, copying records
  must reproduce it, mboxrd quoting must be reversible, and nothing may throw.
