# Changelog

All notable changes are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- Forensic recovery, opt-in, never writing to its input (`docs/recovery.md`). `RecursiveAI.OneMail.Recovery`: `RecoveryProvenance` (tag `Live`, `SoftDeleted`, `Orphaned`, `FreeSpace`, `Partial`; source byte ranges, lost ranges, qualifying defects, PST `Nid`/`Bid` locators) reached through the new default member `IMailItem.Recovery`.
- `DbxReader.RecoverDeleted`: messages freed by Outlook Express (rebuilt from the deleted-message list and the old next pointers that survive in freed segments) and chains no index entry reaches; nothing a live read lists is returned. `OlmReaderOptions.Recovery` and `OlmArchive.Damage`: damaged OLM archives open through OneArchive's zip recovery mode, damage records become defects and items from damaged entries are tagged `Partial`.
- Span-level `TryParse` for tools that find pieces elsewhere than a file's own structure puts them: `MimeHeaderBlock.TryParse`, `MimeEntity.TryParse`, `NdbSpan.TryParsePage` and `TryParseBlock`, `DbxSpan.TryParseSegment` and `TryParseRecord`, `CfbSpan.TryParseHeader` and `TryParseDirectoryEntry`.
- `onemail recover`: DBX and OLM recovery with a JSON provenance report; `tools/recovery` (damaged OLM variants, DBX recovery checks against `undbx -r`), `DbxBuild.Deleted`/`Unlinked` in the test builder, fuzz targets `span-ndb`, `span-mime`, `span-cfb`, `dbx-recovery`, `olm-recovery`.

- In-place writing of existing Unicode PSTs (preview; the commit path is not yet independently reviewed): `PstStore.OpenForWriting` returns a `PstWritableStore` with `AddMessage`, `ReplaceMessage`, `MoveMessage`, `DeleteMessage`, `CreateFolder`, `RenameFolder`, `MoveFolder`, `DeleteFolder`, `Commit` and `Rollback`; `NdbEditor` (`.Pst.Ndb`) is the node-database layer under it: exclusive open, header CRC, allocation-map and density-list checks with typed refusals (`NdbEditRefusedException`, `NdbOpenRisks`), allocation from the maps with growth by range, copy-on-write B-trees with splits and merges, block reference counts, zeroing of freed space (opt out with `ZeroFreedSpace`), and a commit that switches the header once so a stop leaves the old or the new state. `docs/pst-writing.md` has the sequence.
- `PstCompaction.CompactAsync` streams a PST into a new, smaller file; `StoreConversionOptions.AddToExistingPst` makes `Mail.ConvertAsync(..., ConvertFormat.Pst, existingPst)` add the converted items to that PST in place and commit when the conversion completes.
- `tests/Durability` and `tools/pst/inplace-check.sh`: power-cut replays of every write and barrier of a scenario, process kills, and a random-operation soak.
- `INdbNodeSink`: where the PST object writers put nodes (a new file's writer or an editor).

- `OutlookNames.RecurrenceType` (`PidLidRecurrenceType`) and `OutlookNames.CounterProposal` (`PidLidAppointmentCounterProposal`).
- iCalendar to Outlook item: `METHOD:COUNTER` becomes a tentative response with the counter-proposal flag (and back); an RDATE paired with an EXDATE becomes a moved instance; a daily rule on weekdays, a yearly `BYMONTHDAY` with no month and one day, and a task's weekly, monthly and yearly rules are now carried; new loss entries say what an Outlook item cannot hold of attendees (`AttendeeRoleNotCarried`, `AttendeeTypeApproximated`, `PartstatNotCarried`, `RsvpIsPerMessage`), of STATUS (`StatusNotExported`), of a series (`SubMinuteTimeDropped`, `RangeThisAndFutureNotCarried`, `RdatePeriodNotCarried`) and of duplicate UIDs (`DuplicateUidNotConverted`); a rule Outlook cannot express reports which part (`RecurrenceNotMapped`), listed in `docs/conversion.md`.
- `tools/oxocal`: `OxocalTool icsdiff` and `itemdiff` compare an iCalendar or an Outlook item with its round trip field by field and classify every difference as reported or unreported; `ics-roundtrip-checks.sh` adds the expansion oracles on the exported iCalendar.
- `PstBuilder` (`RecursiveAI.OneMail.Pst.Messaging`) and `PstFileWriter` (`.Pst.Ndb`): write a new Unicode PST from folders and MAPI items handed over one at a time, in constant memory. The file writer places blocks as they are made, builds the block and node B-trees bottom-up (the node tree from sorted runs), and writes the allocation, page, free and free-page maps, the density list and the header last, so a stopped build leaves a file every reader refuses. The builder writes the mandatory nodes, folders with their hierarchy, contents and associated tables, messages with recipient and attachment tables, embedded messages, large values in subnodes, and the name-to-ID map with its hash buckets; every property is written as it is, and what it adds, renumbers or leaves out is listed in `PstBuilder.Defects`. `ConvertFormat.Pst`, `onemail convert --to pst` and `StoreEntry.Message` carry the same through `Mail.ConvertAsync`. `HeapNodeBuilder` and `TableContextBuilder` can write a heap's blocks as they fill, and `NdbBlockBuilder.DataTreeWriter` reserves a leaf's BID before its bytes. Outlook opening the files is not yet verified. See `docs/pst-writing.md`.
- S/MIME in `RecursiveAI.OneMail.Cryptography` (`Smime` namespace, preview): `SmimeSigner` (multipart/signed with a detached signature and opaque signed-data; RSA PKCS #1, RSA-PSS, ECDSA, Ed25519; SMIMECapabilities and encryption-key-preference attributes; weak algorithms refused), `SmimeEncryptor` (enveloped-data and authEnveloped-data; RSA-OAEP key transport, ECDH/X25519 key agreement), and `SmimeProcessor.Open`, which verifies and decrypts nested layers (signed, encrypted, compressed, triple-wrapped) into a structured `SmimeResult`: per-signer status with OneCrypto's rule checks, the exact covered bytes and their place in the source, what lies outside the signature, certificate-versus-sender match, optional chain validation (nothing trusted implicitly), weak-algorithm flags and the inner message in the shared view. `SmimeOutlook` opens `IPM.Note.SMIME` and `IPM.Note.SMIME.MultipartSigned` items (MS-OXOSMIME). CMS work is OneCrypto's. Ambiguities SM1 to SM8, `docs/smime.md`, the `smime-structure` fuzz target and `tools/smime` (OpenSSL and MimeKit interop, RFC 9216 example keys, corpus scan).
- `RecursiveAI.OneMail.Client` (new package, preview): the raw protocol layer under IMAP, SMTP and POP3: connections with implicit TLS, STARTTLS/STLS (refused when the server sent data after the acknowledgement), certificate hooks, ALPN/SNI, timeouts and `COMPRESS=DEFLATE`; IMAP command builder and a response reader that parses every line into a typed tree with FETCH literals streamed (`MimeReader` over a `BODY[]` literal); SMTP reply reader, pipelining, dot-stuffing and BDAT; POP3 responses and dot-unstuffed bodies; SASL framework with PLAIN; a trace with credentials redacted; limits on by default. See `docs/client-protocol.md`.
- `SmtpClient` (`RecursiveAI.OneMail.Client.Smtp`, preview): SMTP submission with STARTTLS (required by default) or implicit TLS, EHLO/HELO fallback, authentication, PIPELINING, CHUNKING/BDAT, SIZE checked before sending, 8BITMIME with a downgrade to quoted-printable/base64 that refuses signed messages, SMTPUTF8, DSN, REQUIRETLS, a structured `SmtpSendResult` with a reply and enhanced status code for every recipient, a streamed message source (MIME tree, bytes or stream), `Bcc` kept out of the transmitted headers, and session reuse with RSET. `docs/smtp.md`.
- SASL mechanisms (`Client.Sasl`): LOGIN, CRAM-MD5, SCRAM-SHA-1/256 and `-PLUS` (tls-server-end-point), XOAUTH2, OAUTHBEARER, NTLMv2 on our own `Md4`, GSSAPI and GSS-SPNEGO through `NegotiateAuthentication`, and `SaslMechanisms.Select` (strongest offered that the credentials allow; PLAIN/LOGIN never without TLS unless allowed). `SaslMechanism.IsLegacy`. `docs/sasl.md` with per-provider token notes.
- `Mail.SendAsync` (extension on `Mail` in `RecursiveAI.OneMail.Client`): connect, send, QUIT in one call, and `OutgoingMessage` (`RecursiveAI.OneMail.Composition`): a new message from addresses, subject, text, HTML and attachments, with RFC 2047/2231 encoding of non-ASCII names.
- The test server scripts: the SMTP sink keeps what it receives, the Postfix guest relays to it and has a legacy listener without 8BITMIME, PIPELINING and CHUNKING, and Dovecot and the guest offer CRAM-MD5 and SCRAM.
- `ImapClient` and `Pop3Client` (`RecursiveAI.OneMail.Client`, preview), the typed clients on the raw protocol layer. IMAP: login by SASL PLAIN or LOGIN, `ENABLE`, `ID`, `NAMESPACE`, `LIST`/`LSUB`/`LIST-EXTENDED` with `SPECIAL-USE` and `LIST-STATUS`, `STATUS`, `SELECT`/`EXAMINE` with `CONDSTORE` and `QRESYNC` into a selected-mailbox model kept current by every unsolicited response (`EXISTS`, `EXPUNGE`, `VANISHED`, flag `FETCH`) and an `EventReceived` event, `FETCH` as an `IAsyncEnumerable` (envelope, flags, `BODYSTRUCTURE`, buffered sections, `BINARY`, `CHANGEDSINCE`), `DownloadAsync` and `GetMessageAsync` that stream a literal from the socket to a stream or into the MIME model, `SEARCH` with `ESEARCH`, `SORT`, `THREAD`, `STORE` (with `UNCHANGEDSINCE`), `COPY`/`MOVE` with the `COPYUID` mapping, `EXPUNGE`/`UID EXPUNGE`, `APPEND` with `APPENDLIMIT`, `MULTIAPPEND` and stream bodies, `IDLE` as an async stream of events that ends with `DONE` instead of a cancelled read, `COMPRESS=DEFLATE`, `UTF8=ACCEPT`. POP3: `STLS`, `USER`/`PASS`, SASL, `APOP`, `STAT`, `LIST`, `UIDL`, `RETR` and `TOP` into the MIME model, pipelined `RETR` and `DELE`, `RSET`, `QUIT`. Async-first with synchronous twins; `docs/imap.md`, `docs/pop3.md` (with the MailKit call tables).
- `Pop3RawConnection.SendAsync`, `FlushAsync` and `ReadMultilineAsync`: write several commands, then read their replies (PIPELINING).
- `tools/oracles/imap-compare`: times this library's IMAP fetches against MailKit on one server (indicative).
- Container test servers: Dovecot polls for other sessions' changes every second and Cyrus keeps 8-bit header bytes, so IDLE and byte-identical round trips can be tested against them.

### Fixed

- Moving a folder twice in one in-place commit (or away and back) left a stale row in the intermediate parent's hierarchy table and a wrong subfolder flag; found by the 1,000-operation soak.
- PST allocation map bits count from the **high** bit of each byte, as in files Outlook wrote; the builder wrote them from the low bit, so a program that trusted the maps of a builder-written file saw the wrong slots as used. `NdbFile.IsAllocated` has the same fix.
- iCalendar to Outlook item: a series that ends by `UNTIL` no longer counts a last day whose start is after `UNTIL`; a yearly rule whose `BYMONTH` is not the month of DTSTART keeps its month; a yearly rule with `BYMONTHDAY` and no `BYMONTH` is every month, as RFC 5545 says, instead of the month of DTSTART; `BYHOUR` and `BYMINUTE` set the series' time; a first instance moved to the first allowed day keeps its length; the sender of a REPLY is the attendee and the recipient the organizer.
- A series across a change of its zone's daylight saving rules (New York in 2007, Sydney in 2008) was an hour out in the affected weeks after conversion: the item now carries one time zone rule per change and the export writes them back.
- An Outlook item whose PidLidTimeZoneStruct holds a rule no VTIMEZONE can state (found by the fuzzer) made the export throw `InvalidOperationException`; the item now reads as having no zone, with a `ValueOutOfRange` defect.
- Outlook item to iCalendar: PidLidRecurrenceType, the end-display time zone, PidLidResponseStatus and PidLidIsException are reported as derived, not as properties iCalendar cannot hold.

## [0.4.0-preview.1]

On the local feed. Bodies, display and conversion close with this preview: RTF to HTML, HTML and text, best body, display HTML with its independent review passed, the calendar card, the Render package, conversions with loss reports, replies and forwards. It also carries the first Outlook writing (MSG and OFT, PST write primitives), opt-in recovery of damaged MSG files, and DKIM and ARC in the Cryptography package. What is checked, and the known gaps, are in [docs/compatibility.md](docs/compatibility.md).

### Added

- `MsgWriter` (`RecursiveAI.OneMail.Msg`): writes MSG and OFT files from the MAPI model (named properties, recipients, attachments, embedded messages, OLE storages), Unicode or code page strings, limits checked before the first byte, large attachment data copied from the caller's stream (`MapiStreamValue`). Outlook opening the files is not yet verified.
- `ConvertFormat.Msg`, `StoreConverter` and `onemail convert --to msg`: any readable store to MSG files with loss reports.
- `MapiDistributionList.Create` and `Notes.MapiNote`: builders for distribution list and sticky note items.

- PST write primitives, the layer under the Unicode PST builder (Unicode, 512-byte pages; nothing is placed in a file yet). `RecursiveAI.OneMail.Pst.Ndb`: `NdbBlockBuilder` makes data blocks with their trailers and CRCs (none, permute and cyclic encodings), data trees (XBLOCK and XXBLOCK, from a span, a stream or blocks the caller sizes) and subnode trees (SLBLOCK and SIBLOCK), handing each `NdbBlock` to an `INdbBlockSink` with a BID from an `IBidAllocator` (`SequentialBidAllocator`); the signature is filled in for the offset the caller chooses. `RecursiveAI.OneMail.Pst.Ltp`: `HeapNodeBuilder` (HIDs, page maps, fill levels, multi-block heaps), `BthTreeBuilder` (any key and entry size, index levels), `PropertyContextBuilder` (inline, heap or subnode values by size, streaming for large ones) and `TableContextBuilder` (columns, row matrix in the heap or a subnode, row index, rows streamed block by block), each result an `LtpNodeResult`. Every property and table context of the Tika PSTs and the CFReDS OST rebuilds and reads back identical. See `docs/pst-ndb.md` and `docs/pst-ltp.md`.

- `RecursiveAI.OneMail.Cryptography` (new package, preview): DKIM signing and verification (RFC 6376, RFC 8301, RSA-SHA256 and Ed25519-SHA256 per RFC 8463) with structured per-signature results, ARC chain validation and sealing (RFC 8617), a pluggable `ITxtResolver`, and `DnsTxtClient` (DNS TXT over UDP with TCP fallback, no DNSSEC). RSA runs on the base class library, Ed25519 on `RecursiveAI.OneCrypto` 0.1.0-preview.1. New signatures refuse RSA under 2048 bits and rsa-sha1. See `docs/dkim-arc.md`.

- `MsgReaderOptions.Recovery` (and `MaxRecoveryBytes`) opens a damaged MSG or OFT through OneArchive's compound file recovery; `MsgFile.IsRecovered`; new defect kinds `CompoundFileRecovered`, `CompoundFileDamage` (file offset, damage kind, entry, length) and `StreamDamaged` (kept against declared bytes). Strict stays the default and reads exactly as before
- `RecursiveAI.OneMail.Render`: `ToPdfAsync` and `ToPngAsync` on any item render the display HTML to a tagged PDF or PNG pages through OneRender, with page size and margins, header and attachment-list switches, fonts, PNG scale and page range, limits on pages and time, and a loader that decodes `data:` URIs and fetches nothing else. See `docs/render.md`.
- `onemail inspect` reads ICS, vCard, jCal, jCard, xCal and xCard files: the number of calendars or cards, the kinds of components in them and the reader's defects (`--json` and `--defects` supported).
- Bodies (`RecursiveAI.OneMail.Bodies`): HTML to text on OneHtml's DOM (blocks, lists, tables, quotes, links, images, `pre`, hidden content, optional wrapping, limits), text to HTML with quote levels and conservative links, `format=flowed` decode and encode (RFC 3676, DelSp), and best-body selection across MIME, Outlook, TNEF, OLM and DBX items (`BodySelector`, `IMailItem.BestBody`). Every body carries one `MailBody.Origin` (stored, RTF-encapsulated, RTF-converted, HTML-to-text, text-to-HTML, flowed-decoded); a reply to an HTML-only message now carries a text alternative made from its HTML.

### Security

- Display HTML had its independent review (see `docs/display-html.md`, *Review*): no script execution, no fetch under the Block policy and no escape from the scope box in Chrome or Firefox. Fixed on the way: quadratic link-bracket trimming in plain-text display and per-reference part scans in `cid:`/location resolution (both denial of service on crafted input); control characters are no longer written; `format=flowed` quote depth is capped at 32; verify passes keep only URLs the first pass wrote; a parenthesis glued to a CSS value token is refused.

### Changed

- OLM archives open OneArchive's zip reader with `LazyDirectory`, so the zip reader no longer holds the central directory: peak working set 185 MiB instead of 234 MiB over 133,345 entries; items are unchanged

## [0.3.0-preview.2]

A fix release for 0.2.0-preview.2, on top of 0.3.0-preview.1.

### Added

- `RecursiveAI.OneMail.Display.CalendarCard`: the compact card mail clients show for a calendar object, as an HTML fragment and as plain text. It covers iMIP/iTIP methods (invitation, update, cancellation, reply, counter-proposal, ...), the time in a caller-chosen zone with the organizer's time kept, a plain-English recurrence sentence ("Every 2 weeks on Monday and Wednesday, until 30 Nov 2026", or "Repeats (custom rule)" when it cannot be said exactly) with the next occurrences, location and conference URLs, organizer, attendees and a truncated description, from `.ics` files, `text/calendar` parts and Outlook appointment and meeting items (`FromIcs`, `FromMime`, `FromMapi`, `FromItem`). The HTML is built from escaped text and passes the display sanitiser unchanged. See `docs/calendar-card.md`.
- `OutlookNames.NetShowUrl` (`PidLidNetShowUrl`).
- `IMailAttachment.ContentTypeInferred` (a default interface member): true when `ContentType` was guessed from the file name's extension because the item stored no specific type.

### Fixed

- `RecursiveAI.OneMail` now depends on `RecursiveAI.OneArchive` 1.1.0 (it was 0.7.0-preview.1), so an app that also uses OneArchive 1.x no longer hits a version conflict. The compound-file and zip readers are no longer preview APIs, so the build opt-outs are gone.
- Attachments with no stored content type, or the generic `application/octet-stream` (Outlook, TNEF, OLM, and MIME parts), report a type from a built-in table when the file name's extension is known (`.pdf` is `application/pdf`); a stored specific type always wins. See `docs/shared-view.md`.
- The package page (`PACKAGE.md`) links to the files of the release tag instead of `main`; the link is stamped from the package version when packing.
- The README lists only the packages that are on nuget.org.

## [0.3.0-preview.1]

On the local feed. Calendar and contacts close with this preview (speed rows in docs/benchmarks.md); it also carries the bodies, display and conversion work done so far. What is checked, and the known gaps, are in [docs/compatibility.md](docs/compatibility.md).

### Added

- `RecursiveAI.OneMail.Composition`: `CreateReply`, `CreateReplyAll` and `CreateForward` (inline or as a `message/rfc822` attachment) on any item. They make the threading headers, recipients, subject prefix (never stacked), quoted text or HTML, inline images, and for Outlook items the MS-OXOMSG conversation index level, as MIME and, for Outlook originals, as a MAPI message; hostile originals are bounded and reported as typed defects. `ConversationIndex` and `SubjectPrefixes` are public. See `docs/composition.md`.
- `StoreConverter` and `Mail.ConvertAsync`/`Mail.Convert`: any readable store or directory to EML, mbox, Maildir++, Apple Mail bundles, ICS, vCard, the j/x forms or TNEF, keeping folders, flags and keywords where the target has them, with progress, cancellation, an aggregate loss report and an `IStoreSink` extension point; the fidelity matrix is in `docs/fidelity.md`.
- `onemail convert` and `onemail export`, with `--json`.
- `MaildirStore.DeliverPath`: delivers and returns the path (`Deliver` re-listed the directory per message).
- Outlook calendar and task items (MS-OXOCAL, MS-OXOTASK): appointments, recurrence blobs with exceptions, time zones, meeting requests, responses and cancellations, and tasks in the shared view, with conversion to and from iCalendar per MS-OXCICAL and loss reports.
- Outlook for Mac OLM reading (`RecursiveAI.OneMail.Stores.Olm`): archives over the zip reader, every item kind with a lossless element tree, calendar and contact mapping with loss reports, and `Mail.Open` on `.olm`.
- Display HTML (`RecursiveAI.OneMail.Display`): an allowlist sanitiser on the HTML DOM, a remote-content policy (block, allow or a rewrite callback), `cid:` and attachment resolution, and optional header and attachment blocks. It has not had its independent review yet.
- Calendar and contact benchmark rows against Ical.Net and FolkerKinzel.VCards, with a libical reference, in `docs/benchmarks.md`.
- `RtfHtmlConverter`: RTF written as RTF (no encapsulated HTML or text) becomes HTML and plain text with typed defects and limits; the Outlook item view uses it when no other body exists (`MailBody.Origin`).

### Changed

- Content lines: a run of carriage returns before a line feed is one line end (most readers join a fold written that way), reported as `AmbiguousDoubleCarriageReturn`; the bytes are kept. Parsing allocates less.
- `MimeWriter` keeps the white space inside a header value when it writes one (runs of spaces and tabs were collapsed); untouched messages still write from their raw bytes.

## [0.2.0-preview.2]

The first preview on nuget.org. It has everything in 0.2.0-preview.1 plus the changes below. What is checked, and the known gaps, are in [docs/compatibility.md](docs/compatibility.md).

### Added

- jCal, jCard, xCal and xCard (`RecursiveAI.OneMail.Calendar.Forms`): read and write both directions from ICS and vCard trees with a loss report, typed defects and resource limits; `MailFormatSniffer` names the four forms.
- Outlook Express 5/6 DBX reading (`RecursiveAI.OneMail.Stores.Dbx`): message databases, the folder tree from `Folders.dbx`, lazy message streams in constant memory, content sniffing, and `Mail.Open` on a `.dbx` file or a store folder.
- `MaildirStoreOptions.InfoSeparator`: Maildir file names use `;` before the info section on Windows (where `:` is not allowed in file names) and `:` elsewhere; both are read on every platform.

### Fixed

- IANA time-zone names now resolve on Windows when the runtime knows only Windows zone ids (for example with invariant globalization), through the CLDR Windows-zone map; Windows names resolve through the same map on every platform.
- Maildir delivery no longer fails on Windows.

## [0.2.0-preview.1]

Outlook formats on read: MSG and OFT, PST and OST, TNEF and compressed RTF, the MAPI property model, and conversion between MAPI and MIME. Calendar recurrence and time zones. What is checked, and the known gaps, are in [docs/compatibility.md](docs/compatibility.md).

### Fixed

- Encoding to ISO-8859-1 and US-ASCII code pages (the runtime's own, not in the code page provider) no longer throws in `MailEncoding.GetBytes`.

### Added

- The MAPI property model (`RecursiveAI.OneMail.Mapi`): typed property bags with named properties, code-page handling and the MS-OXPROPS tag tables.
- MSG and OFT reading (MS-OXMSG) over the compound file reader of OneArchive, with embedded messages and attachments.
- The PST/OST lists, tables and properties layer (`RecursiveAI.OneMail.Pst.Ltp`: heap-on-node, BTH, property and table contexts) over the node database, reading through the random-access sources.
- TNEF reading and writing (MS-OXTNEF): the attribute layer, MAPI messages and embedded messages.
- Compressed RTF (MS-OXRTFCP) both ways and HTML and text de-encapsulation from RTF (MS-OXRTFEX), in a namespace with no dependencies on the rest of the library.
- Conversion between MAPI messages and MIME per MS-OXCMAIL in both directions, with a loss report listing every value that did not carry over (`RecursiveAI.OneMail.Conversion`).
- Calendar recurrence expansion (RRULE, RDATE, EXDATE, overrides) and time-zone resolution through VTIMEZONE, the IANA database and Windows zone names.
- Benchmark rows for PST and OST enumeration and extraction, MSG read and TNEF read ([docs/benchmarks.md](docs/benchmarks.md)).
- Contacts: `VCardView` covers the rest of RFC 6350 (`Gender`, `Geos`, `Impps`, `Related`, `Keys`, `Sounds`, repeated properties, lazy `VCardMedia`); `MapiContact` and `MapiDistributionList` read Outlook contacts and lists (also as `MapiMailItem.Contact` / `.DistributionList`); `ContactToVCardConverter` and `VCardToContactConverter` convert both ways by MS-OXVCARD with loss reports ([docs/contacts.md](docs/contacts.md)).

- The PST/OST messaging layer (`RecursiveAI.OneMail.Pst.Messaging`): store, named properties, folders, messages, recipients, attachments and embedded messages as MAPI property bags, in ANSI, Unicode and 4 KiB files.
- MAPI items (MSG, PST, OST) in the shared view; `Mail.Open` reads them and `onemail inspect` lists them.
- TNEF parts inside MIME messages: `MimeToMapiConverter` merges them into the MAPI message by the rules of MS-OXCMAIL 2.2.3.8 (headers win, richer bodies kept, loss-report entries for every property set aside), the shared view shows their attachments and bodies in place of winmail.dat (`MimeMailItem.TnefParts`), and `OxcMailExportOptions.Tnef` writes a TNEF message.
- The browser smoke app covers MSG, PST, TNEF, compressed RTF and de-encapsulation with legacy code pages, recurrence through a VTIMEZONE and the IANA database, and the MS-OXCMAIL conversions.

## [0.1.0-preview.1]

First preview: the MIME core, mailbox stores and the one-line reading path. What is checked, and the known gaps, are in [docs/compatibility.md](docs/compatibility.md).

### Added

- Random-access sources (memory, stream, file, ranged reads) under every file format.
- `MimeReader` and `MimeWriter` (forward-only events with byte offsets, streaming writes) and the lossless entity tree: untouched messages write back byte for byte, every repair is a typed defect, and resource limits are on by default.
- Charsets: the WHATWG encodings plus UTF-7, IMAP modified UTF-7 and the ISO-2022-JP variants; RFC 2047 encoded words and RFC 2231 parameters.
- Structured headers: addresses and groups, dates, message identifiers, content type and disposition parameters.
- multipart/report views (delivery status, disposition notification, feedback reports) with builders; MHTML resolution; `Authentication-Results` and `Received-SPF` parsing.
- Mailbox stores: mbox (mboxo, mboxrd, mboxcl, mboxcl2, Gmail Takeout labels), Maildir with Courier and Dovecot keywords, EMLX and Apple Mail bundles, read and written; content-based store sniffing.
- The shared read view (`IMailItem`), `Mail.Open` and `Mail.Parse`, and content-based format sniffing.
- `RecursiveAI.OneMail.Calendar`: ICS and vCard content lines and lossless component trees with typed views.
- The PST/OST node database layer (header, pages, B-trees, data and subnode trees, allocation maps, scan mode).
- The `onemail inspect` command.
- The ambiguity table (`docs/ambiguities.md`), the differential and round-trip tools, fuzz targets and the MIME benchmark rows.
- Package skeleton: the `RecursiveAI.OneMail`, `.Calendar`, `.Client`, `.Cryptography` and `.Render` packages and the `onemail` tool as buildable placeholders, with tests, a benchmark shell, a fuzz shell and NativeAOT and browser WebAssembly smoke apps.
