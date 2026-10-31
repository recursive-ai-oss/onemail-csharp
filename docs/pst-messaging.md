# PST/OST messaging layer

What `RecursiveAI.OneMail.Pst.Messaging` reads, how it treats damage, and what was checked. The structures are MS-PST 2.4
(store, folders, messages, attachments, recipients, named properties) on the lists-tables-properties layer
(`docs/pst-ltp.md`). The same code reads ANSI, Unicode, 4 KiB and OST files; the differences live in the node database
(`docs/pst-ndb.md`). Every object it returns is a `RecursiveAI.OneMail.Mapi` property bag, so a PST message, a folder and an
MSG message are read with the same accessors (`docs/mapi.md`).

## Types

| Type | Spec | What it is |
|---|---|---|
| `PstStore` | MS-PST 2.4.3 | `TryOpen` over a path, stream, byte array or random-access source. `Root`, the store's `Properties`, `PasswordCrc`, `TryGetNamedProperty`, `EnumerateFolders`, `EnumerateMessages(PstMessageSet)` (and `Async` twins), `TryOpenMessage(nid)`, `ReadEverything()` (walks and reads every object, returns `PstWalkCounts`). |
| `PstFolder` | 2.4.4 | Properties, `Name`, `Path`, `Depth`, `ParentFolder`, `EnumerateSubfolders`, `EnumerateMessages(PstMessageSet)` and `EnumerateMessageIds`, `IsSearchFolder`. |
| `PstMessage` | 2.4.5 | A `MapiMessage` with `Recipients`, `Attachments`, defects, `IsEmbedded`, `IsAssociated`. |
| `PstRecipient`, `PstAttachment` | 2.4.5.3, 2.4.6 | Rows of the recipient and attachment tables with their properties; an attachment may carry an embedded `PstMessage`. |
| `PstReaderOptions` | | Limits (below) and the options of the layers underneath. |

`MapiMailItem` (root namespace) adapts any `MapiMessage` to the shared view (`docs/shared-view.md`); `Mail.Open` uses it for
PST, OST and MSG files.

## How things are loaded

- **Names.** The named-property map (NID 0x61, MS-PST 2.4.7) is read once at open; property IDs of 0x8000 and above are
  resolved to GUID plus numeric ID or string name through it. A missing or unreadable map leaves those properties unnamed and
  raises `NodeMissing` or `ContextUnreadable`; nothing is dropped.
- **Folders.** From the root folder's hierarchy table, depth first. A folder reached twice, or deeper than
  `MaxFolderDepth`, is not entered again (`FolderLoop`). Contents tables list the messages; a message listed in a table
  whose node is missing is reported (`NodeMissing`) and skipped.
- **Search folders** (their contents are links to other folders' messages) and **associated (FAI) messages** (rules, views,
  settings) are not enumerated by default: `PstMessageSet.Normal` is the ordinary contents of ordinary folders; `Associated` and
  `All` add the hidden messages. Search folders are listed by `EnumerateFolders` (`IsSearchFolder`) but their contents are not
  enumerated, so no message appears twice. `Mail.Open` uses `Normal`.
- **Messages.** The property context is read when the message is opened. Values held in the heap are copied at that point;
  values held in subnodes (large bodies, attachment data, big tables) stay on disk and are read when asked, by range
  (`PstValueStream` for streams). Items therefore need no disposal and stay valid until the store is disposed.
- **Recipients and attachments.** Each table is a table context in a subnode of the message. The row's identifier is checked
  against the subnode the object is stored under (`RowIdMismatch`). A row whose properties are in a separate subnode
  (attachments) is merged with it; the table's bookkeeping columns (0x67F2, 0x67F3) are not exposed as properties.
- **Embedded messages.** An attachment of method `ATTACH_EMBEDDED_MSG` holds its message as an object property: an 8-byte
  structure (subnode NID, size) in the heap (MS-PST 2.3.3.5). A bare subnode NID is accepted too (ambiguity P2). The embedded
  message is read the same way as a top-level one, to `MaxEmbeddingDepth`.
- **Large tables.** A recipient or attachment table whose row matrix is in a data tree is read through the node database's data
  tree, row by row.
- **Password and obfuscation.** `PasswordCrc` returns the stored CRC. It is not protection: the store reads whatever the
  value. Block obfuscation is decoded by the node database for every file.

## Limits

| Limit | Option | Default |
|---|---|---|
| Folder depth | `MaxFolderDepth` | 256 |
| Properties in one object | `MaxProperties` | 1,048,576 |
| Recipients of a message | `MaxRecipients` | 65,536 |
| Attachments of a message | `MaxAttachments` | 65,536 |
| Embedded-message depth | `MaxEmbeddingDepth` | 32 |
| Named properties | `MaxNamedProperties` | 32,768 |
| Defects kept (the rest are counted in `DroppedDefects`) | `MaxDefects` | 10,000 |

Reading never throws on malformed content. Beyond the limits, rows and objects are skipped and `LimitReached` is raised (`DefectLimitReached` when the defect list itself is full).
`TryOpen` returns false with defects only when no usable header or root exists.

## Defects

`MapiDefect` carries a kind, a message, the node where known, and an offset when the lower layer gave one. Added by this layer:
`NodeMissing`, `ContextUnreadable`, `ValueUnreadable`, `LowerLayerDefect` (a node database or LTP defect met while reading an
object), `FolderLoop`, `RowIdMismatch`, and the two ambiguities `AmbiguousAttachmentTableMissing` and
`AmbiguousObjectReference` (P1, P2 in `docs/ambiguities.md`). Store-wide node database defects are on `DatabaseDefects`.

## How it is checked

Worker, libpff `pffexport` 20180714 (black box), `tests/PstOracle`:

- Real files (testPST, testPST_variousBodyTypes, the CFReDS OST): folders, items, recipients, attachments (names, sizes,
  content), embedded messages, subject, sender, body text/HTML/RTF values and times, against `pffexport -f all -m all`:
  0 differences. Items libpff does not export (31 folder-associated messages of the OST) are counted separately.
- Property-exact: every property bag against the raw LTP entries (tag, type, value bytes): 0 differences on 27 objects /
  959 properties (testPST), 16 / 796 (variousBodyTypes), 109 / 3,763 (OST).
- Generated files from the test builder in ANSI, Unicode and 4 KiB layouts (up to 128 items, a 300-recipient message,
  110-row tables in data trees, 3-deep embedded messages, large attachments in subnodes): 0 differences from `pffexport`
  (except the 4 KiB case below) and 0 from the property-exact check.
- **libpff limitation (4 KiB files).** libpff 20180714 does not read a table whose row matrix lies in a data tree in a 4 KiB
  file: on the generated 4 KiB store of 215 items it reads 18 items, and the 300-recipient message gives 0 recipients (tried with
  data block sizes of 8,168 and 65,512 bytes). Our reader reads all of them, and the property-exact check agrees with the raw
  entries. The same libpff agrees with us on every small table of the real 4 KiB OST. So the 4 KiB large-table case has no
  differential until another oracle (Outlook) exists.
- Fuzzing: the `pst-messaging` target (bounded run recorded in the unit report).
- Memory: `PstOracle pstalloc <file> <passes>` prints bytes allocated per message and the working set after each pass.
