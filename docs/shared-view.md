# The shared read view and `Mail.*`

Every item OneMail reads (a MIME message now; MSG, PST items, calendar items and others as their readers arrive) implements one
read-only interface, `IMailItem`, in the root namespace `RecursiveAI.OneMail`. It is a **projection**: it copies nothing from
the model underneath, parses only what the caller asks for, and never throws on malformed content. Code that only needs to read
mail depends on the view; code that needs every byte and every defect goes to the model (for MIME, `MimeMailItem.Entity`).

## The view

| Member | Meaning |
|---|---|
| `Kind` | `Message`, `Appointment`, `Contact`, `Task`, `Note`, `Journal`, `DistributionList`, `Other`. MIME is always `Message`. |
| `Subject` | Decoded subject, or null. |
| `Sender`, `From`, `ToRecipients`, `Cc`, `Bcc`, `ReplyTo` | `MailAddress(Name, Address)`; groups flattened (V7). Only the first field of each name is read (H2). |
| `Sent`, `TryGetDate(source, out value)` | A date and where it comes from: `DateHeader`, `ClientSubmit`, `Delivery`, `Creation`. MIME has `DateHeader`; a store adds `Delivery` (mbox envelope date, Maildir modification time, Apple Mail `date-received`). `ZoneKnown` is false when the source states no zone. MAPI items add the other two. |
| `MessageId`, `InReplyTo`, `References` | Without angle brackets. |
| `ConversationIndex`, `ConversationTopic` | MAPI properties; for MIME the `Thread-Index` (base64) and `Thread-Topic` headers when present, else empty/null. |
| `TextBody`, `HtmlBody`, `RtfBody` | `MailBody`: `ContentType`, `Charset`, `Open()` (decoded bytes), `ReadText()`. **Raw candidates, not the best body**: choosing between them and converting one to another is later work. MIME has no RTF. |
| `Attachments` | `IMailAttachment`: `FileName`, `ContentType`, `ContentId`, `IsInline`, `Size` (decoded; computed on first use by decoding once), `Open()`, `Embedded` (an `IMailItem` for `message/rfc822`). Built on first use. Resolving `cid:` references plugs in here, against `ContentId`/`IsInline`. |
| `Defects` | One shape (`MailDefect`: family, code, offset, ambiguity flag) over each family's type: `mime` (the tree's defects, then those of the structured headers read), `mailbox` (the store reader's bits for this message). Evaluated on each enumeration. |
| `Provenance` | Format of the container, location, folder, byte offset and length of the item in its container file (-1 when not a range), index. |

Offsets in a `mime` defect are relative to the item's own first byte (what the MIME reader sees), not to the container file;
add `Provenance.Offset` for a file position (for an mboxrd store whose body lines were unquoted, positions after the first
unquoted line shift by the removed `>` bytes).

### Bodies and attachments for MIME

- **Text/HTML**: `multipart/alternative`: the last matching child (RFC 2046 5.1.4); `multipart/related` and `multipart/signed`: the
  first child only; any other multipart: the first matching child in order; a `message/*` part is not searched. A text part with
  `Content-Disposition: attachment` is never a body. Text decoding: the stated charset; if absent or unknown, UTF-8 when valid, else
  Windows-1252.
- **Attachments**: every leaf part and `message/*` part that is not the chosen text/HTML body, in document order, except a text part
  directly in a `multipart/alternative` that is not marked `attachment` (that is another rendering of the body). A message that is
  one leaf part is its own attachment. `IsInline` is true for a part that is not marked `attachment` and has a `Content-ID`.
  The resolutions are V1–V8 in [`ambiguities.md`](ambiguities.md).
- An embedded `message/rfc822` is an `IMailItem` with the same view, parsed from the same bytes.

## Opening things: `Mail.*`

| Call | Result |
|---|---|
| `Mail.Parse(bytes \| stream, MimeReaderOptions?)` | One `IMailItem`. |
| `Mail.Open(path \| stream \| source, MimeReaderOptions?, MboxReaderOptions?)` | A `MailContainer`: the sniffed `Format`, `IsStore`, `Enumerate()` and `await foreach`. A single message is a container of one. |

Each `Mail.*` method is one call into the layer that does the work; the options are those layers' own option types.

`MailContainer` lifetime: the container owns the file it opened from a path (a stream or source passed in stays the caller's). Items
read their bytes when first used, so they are valid until the container is disposed, after which reading an unread item throws
`ObjectDisposedException`. Enumeration holds one message's state at a time. A source without synchronous reads is handled by the
async enumeration (mbox messages are loaded before the item is yielded).

### Sniffing

`MailFormatSniffer.Sniff(source)` and `.SniffDirectory(path)` decide by content; the file name is never read.

| Evidence | Format |
|---|---|
| `D0 CF 11 E0 A1 B1 1A E1`, and the UTF-16 name `__properties_version1.0` in the first 16 MiB | `Msg` (MS-OXMSG); otherwise `CompoundFile` |
| `!BDN`, client magic `SO` at offset 8 / other | `Ost` / `Pst` |
| `78 9F 3E 22` | `Tnef` |
| first line is an mbox envelope line (`From ` + sender + date) | `Mbox`; `EMLX`: byte count, message, then end of file or a property list |
| `BEGIN:VCALENDAR` / `BEGIN:VCARD` (any case, after an optional BOM and blanks) | `Ics` / `VCard` |
| first line is a header field: printable ASCII name, optional blanks, `:` | `Eml` |
| directory: `cur` and `new`; dot-prefixed Maildir subfolders; `Messages` directories of `.emlx` | `MaildirFolder`, `MaildirRoot`, `AppleMailBundle` |

A message with no header block (starts with a blank line or with text that is not a header field) cannot be told from any text
file and is `Unknown`; `Mail.Parse` still reads it. A recognised format whose reader is not in the library yet (`Pst`, `Ost`,
`Msg`, `Tnef`, `Ics`, `VCard`, `CompoundFile`) makes `Open` throw `UnsupportedMailFormatException` with its `Format`; `Unknown`
throws the same exception with `Format == Unknown`. DBX and OLM are not yet recognised.

## The CLI: `onemail inspect`

`onemail inspect [--json] [--sizes] [--defects] <file-or-directory>...` prints the format and every item. A directory that is not a
store is walked and each file inspected on its own (a tree of loose messages); a file that is not readable is reported, not fatal.
Text: one line per item (date, first sender, subject, attachment names, defect count). `--json`: one object per line, a `container`
record then an `item` record per message with the whole view and provenance. `--sizes` decodes attachments to measure them.
It streams: one item is held at a time.

## How it is checked

- `tools/oracles/viewcheck.sh`: runs `onemail inspect --json`, cuts each item's bytes out of its container, runs the OneMail oracle
  driver (built on the tree API, not the view) and compares subject, addresses, date, Message-ID/In-Reply-To/References and
  attachment lists per item; then compares attachment names and counts with Python `email`, grouping differences by signature, each
  tied to a V entry or an N entry.
- Allocation: enumerating an mbox of small messages creates one ~150-byte item per message and reads nothing; reading subject,
  from and date of each costs about 5.4 KB per message, almost all of it the MIME tree (~2.7 KB) and the address and date parsers,
  not the view.
