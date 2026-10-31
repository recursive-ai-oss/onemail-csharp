# The shared read view and `Mail.*`

Every item OneMail reads (a MIME message, an MSG, a PST/OST message; calendar items and others as their readers arrive) implements one
read-only interface, `IMailItem`, in the root namespace `RecursiveAI.OneMail`. It is a **projection**: it copies nothing from
the model underneath, parses only what the caller asks for, and never throws on malformed content. Code that only needs to read
mail depends on the view; code that needs every byte and every defect goes to the model (for MIME, `MimeMailItem.Entity`).

## The view

| Member | Meaning |
|---|---|
| `Kind` | `Message`, `Appointment`, `Contact`, `Task`, `Note`, `Journal`, `DistributionList`, `Other`. MIME is always `Message`. |
| `Subject` | Decoded subject, or null. |
| `Sender`, `From`, `ToRecipients`, `Cc`, `Bcc`, `ReplyTo` | `MailAddress(Name, Address, X500Address)` (`X500Address` is only set for Outlook items whose address is an Exchange address); groups flattened (V7). Only the first field of each name is read (H2). |
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

## MAPI items (MSG, PST, OST)

`MapiMailItem` projects a `MapiMessage` (`docs/mapi.md`) onto the same view. Nothing is looked up outside the item.

| Member | From |
|---|---|
| `Kind` | `PidTagMessageClass` by whole dot-separated segment, case-insensitive: `IPM.Appointment` Appointment, `IPM.Contact` Contact, `IPM.Task` Task, `IPM.StickyNote` Note, `IPM.Activity` Journal, `IPM.DistList` DistributionList; `IPM.Note`, `IPM.Post`, `IPM.Schedule`, `IPM.TaskRequest`, `IPM.Recall`, `IPM.Outlook.Recall`, `IPM.Sharing` and `REPORT` Message; no class Message; anything else Other. So `IPM.TaskRequest` is a message and not a task. |
| `Subject` | `PidTagSubject` with the leading U+0001 plus one-character pair removed (the marker of the normalised-subject split, MS-OXPROPS). |
| `From` | `PidTagSentRepresenting*`, else `PidTagSender*` (MS-OXCMAIL 2.1.3.1). |
| `Sender` | `PidTagSender*`, only when a representing sender exists and has a different address; else null. |
| address of a person | the SMTP address (`Address`). An Exchange (`EX`) address is resolved without a directory, in the order of [`conversion.md`](conversion.md) (OX1): its own `PidTagSmtpAddress` family property; another sender or recipient of the message with the same distinguished name; the transport `From`/`Sender` field's single mailbox for the sender groups; a unique display-name match in the transport headers. The distinguished name stays in `X500Address`. When nothing resolves, `Address` is that distinguished name (as before) and `X500Address` is the same. An address of another type (X.400, fax) is the stored address. The name is the display name (`PidTagRecipientDisplayName` for a recipient row without one). |
| `ToRecipients`, `Cc`, `Bcc` | recipient table rows by `PidTagRecipientType` 1, 2, 3, in table order. `ReplyTo` is read from `PidTagReplyRecipientEntries`: one-off EntryIDs (MS-OXCDATA 2.2.5.1) give name, type and address; other entry kinds need a directory and are left out. |
| `Sent`, `TryGetDate` | `PidTagClientSubmitTime` (`ClientSubmit`), `PidTagMessageDeliveryTime` (`Delivery`), `PidTagCreationTime` (`Creation`). FILETIME in UTC, so `ZoneKnown` is false. |
| `MessageId`, `InReplyTo`, `References` | `PidTagInternetMessageId` (0x1035), `PidTagInReplyToId` (0x1042), `PidTagInternetReferences` (0x1039), angle brackets removed; when the property is absent, the same-named field of `PidTagTransportMessageHeaders`. |
| `ConversationIndex`, `ConversationTopic` | the two properties. |
| `TextBody` | `PidTagBody`; when absent and the RTF body is a plain-text encapsulation (MS-OXRTFEX), the text from the RTF (UTF-8). `HtmlBody`: `PidTagHtml` (binary) or `PidTagBodyHtml` (string); when absent and the RTF body encapsulates HTML, the HTML from the RTF (UTF-8). `RtfBody`: `PidTagRtfCompressed` **decompressed** (MS-OXRTFCP), absent when it does not decompress. A body is absent when nothing gives it. |
| body text and charset | A string property is UTF-16LE (`Charset` utf-16le). An 8-bit text body uses the message's code page rule (`docs/msg.md`); binary HTML uses `PidTagInternetCodepage` (1200 means UTF-16LE), else the same rule; RTF has no charset. |
| `Attachments` | the attachment table in order. `FileName` long name, else short name, else display name. `ContentType` `PidTagAttachMimeTag` lower-cased, `message/rfc822` for an embedded message, else `application/octet-stream`. `ContentId` without angle brackets. `IsInline`: has a content ID and `PidTagAttachContentDisposition` is not `attachment`. `Size`: length of `PidTagAttachDataBinary`, else `PidTagAttachSize`. `Embedded`: the embedded message as a `MapiMailItem`. |
| `Defects` | the model's `MapiDefect`s (message, recipients, attachments, embedded messages), family `mapi`; ambiguity flag from the kind. |
| `Provenance` | PST/OST: format, file, folder path, index in enumeration order; offset and length are -1 (a message is not a byte range). MSG: format, file, offset 0, the file's length. |

PST/OST enumeration through `Mail.Open` yields ordinary messages of non-search folders in folder order (`docs/pst-messaging.md`).

## Opening things: `Mail.*`

| Call | Result |
|---|---|
| `Mail.Parse(bytes \| stream, MimeReaderOptions?)` | One `IMailItem`. |
| `Mail.Open(path \| stream \| source, MimeReaderOptions?, MboxReaderOptions?)` | A `MailContainer`: the sniffed `Format`, `IsStore`, `Enumerate()` and `await foreach`. A single message (EML, MSG) is a container of one; a PST or OST is a store. A PST/OST or MSG that is such by content but cannot be read at all throws `InvalidDataException`. |

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
file and is `Unknown`; `Mail.Parse` still reads it. A recognised format whose reader is not in the library yet (`Ics`, `VCard`,
`CompoundFile`) makes `Open` throw `UnsupportedMailFormatException` with its `Format`; `Unknown`
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
