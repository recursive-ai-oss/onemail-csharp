# Changelog

All notable changes are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [1.0.1] - 2026-11-05

Documentation only; the code is the same as 1.0.0.

### Changed

- Docs: rulings the specifications leave open are called provisional defaults.
- The README and the package readme now say that writing to an existing PST in place is preview quality and to keep a copy of the file first; a new PST from the builder is not covered by that caveat (`docs/pst-writing.md`, *In place*). The same caveat is on the `AddToExistingPst` row of `docs/fidelity.md`.
- `docs/compatibility.md`: MSG and PST are conversion destinations (they were listed as not yet); Outlook 16.0.20527 opened 58 of 58 written MSG and OFT files (the MSG row said not yet verified); the DBX row says deleted and orphaned messages are read on request; the contacts, conversions and composition notes no longer say that no Outlook has been run; the speed section shows the 1.0 rows; the `.Cryptography` sections say the package is not part of 1.0.
- `docs/fidelity.md`: the store-to-store destination table lists `Msg` and `Pst`, with their flags and dates behaviour and what is skipped.
- `docs/olm.md`: damaged-zip recovery (`OlmReaderOptions.Recovery`) is no longer listed under *Not done*.
- `docs/migrating-from-mimekit.md`: says `RecursiveAI.OneMail.Cryptography` is not part of 1.0.
- Reviews are described as done by an engineer who did not write the code, not as independent (README, package readme, `docs/`).
- The README links its display HTML review relatively instead of to `main`; package readmes already point at the tag of their own version.

## [1.0.0] - 2026-11-05

The first stable release: `RecursiveAI.OneMail`, `.Calendar`, `.Client`, `.Render` and the `onemail` tool (`RecursiveAI.OneMail.Cli`). Everything below accumulated through the previews and is part of it.

`RecursiveAI.OneMail.Cryptography` (S/MIME, OpenPGP, DKIM, ARC) is not in 1.0.0: it depends on OneCrypto, which is still a preview, and a stable package cannot depend on a prerelease. It publishes at the first 1.x release after OneCrypto 1.0. A prerelease version still packs it, and the project stays in the solution, built and tested.

Known gaps, in full in `docs/compatibility.md`: Outlook.com provider rows not yet run; Outlook's signature-validity verdict can't be read unattended; Outlook's repair tool miscounts tables over 65,535 rows; protocol rows are x64 only; `LastVerbExecuted` after a real send is unchecked.

Package validation against 0.3.0-preview.2 reports no breaking change, so no suppressions are needed; the next release validates against 1.0.0.

### Added

- `PgpMimeEncryptOptions.HiddenRecipients` writes the encrypted session key packets without naming their recipients (all-zero key ID), for blind copies.
- `tools/outlook/Make-EcdhWindows.ps1` (Windows' own CMS encoder writing ECDH messages), `Probe-SigUi.ps1` (the signature line of Outlook's reading window through UI Automation), `tools/smime/SmimeInterop` `encrypt-to` and `decrypt-ec`, sender properties on the S/MIME items `outlook-items` writes, and `testdata/smime/outlook-ecdh` with `WindowsEcdhTests` (skipped until the CMS reader accepts NULL parameters on an EC originator key).
- `docs/smime.md`, `docs/compatibility.md`: why Outlook rejected our ECDH messages: Windows decrypts an EC key-agreement message only when the key-wrap algorithm identifier carries NULL parameters, and it writes NULL parameters on the originator key, which the CMS reader did not accept. Both fixes were OneCrypto's and are in 0.4.0-preview.2.
- Borrowing body APIs on `MimePart`: `TryGetRawContentMemory`, `TryGetDecodedContentMemory` (a view of the caller's buffer when no decoding is needed), `GetMaxDecodedContentLength` with `CopyDecodedContentTo(Span<byte>)` and `CopyDecodedContentTo(IBufferWriter<byte>)` (decode into caller memory), and `SetDecodedContentBorrowed` (keeps the caller's memory until the part is written). The existing copying APIs are unchanged. Benchmark rows `BodiesBorrowedRow` and `BuildBorrowedRow` measure them separately.
- `tools/outlook`: `Probe-Items.ps1` (reply/forward threading, flags and vCard import as Outlook does them), `Probe-Smime.ps1` (S/MIME items opened in Outlook with throw-away keys imported and removed), `Copy-Folder.ps1` (a large folder copied inside Outlook, the
  control for the repair tool's 65,535-row limit), `Save-Templates.ps1`; `tools/msg/MsgTool` variant generators (`props`, `prune`, `embedded`, `required`, `mixed`, `modes`, `noindex`, `vcards`); `SmimeInterop outlook-items`.
- `docs/smime.md`, `docs/composition.md`, `docs/pst-writing.md`, `docs/compatibility.md`: what Outlook 16.0.20527 does (S/MIME decryption of RSA recipients; ECDH recipients fail; the repair tool's row limit holds for an Outlook-written 70,000-item folder; conversation indexes).

### Changed

- `.Cryptography` is built on OneCrypto 0.4.0-preview.2. ECDH S/MIME now interoperates with Windows and Outlook in both directions: messages we write carry the NULL key-wrap parameters Windows requires (Outlook 16.0.20527 opens P-256, P-384 and signature + key agreement items), and Windows-written messages, whose originator key has NULL parameters, decrypt (4 of 4, `WindowsEcdhTests` is no longer skipped).
- `docs/openpgp.md`, `docs/compatibility.md`: GnuPG 2.5.24 failing to use an imported Cv25519 secret key is a known external GnuPG issue (keygrip mismatch on import; 2.4.4 20/20, Sequoia 20/20, 2.5.24 0/20), not a defect here; 2.5.24 stays informational.

- Parsed headers of an entity share one array instead of one array each, and the general address parser reuses its token lists across the elements of a list; parse results, defects and written bytes are unchanged.
- MSG writing (`MsgWriterOptions.AddRequiredProperties`, on by default) repairs what Outlook 16 refuses or cannot save a copy of, each reported as `PropertyAdjusted`: a missing message class (`IPM.Note`), a missing store
  support mask on a top-level or embedded message, error-typed properties (left out), a recipient with no type (To), and a model that mixes Unicode and 8-bit strings written as given (the 8-bit strings become Unicode). Details and the evidence in `docs/msg.md`.
- `MsgWriterOptions.Template` gives the root storage the class ID Outlook gives templates (`0006F046-...`).
- PST builder: 8-bit values of columns the template tables declare as Unicode (subject, recipient names) are written as Unicode, `PidTagDisplayTo/Cc/Bcc` are derived from the recipients when absent, and an OLE or raw attachment storage is kept
  as a compound file in a file attachment instead of being dropped (`ObjectStorageDropped` is no longer raised). The store converted from message files scans clean in Outlook's repair tool. The conversion report lists the adjustments as `PstPropertyAdjusted` (they were reported as dropped children before).
- Replies and forwards: a forward of an Outlook item (or of a message with a conversation index) carries `In-Reply-To` and `References` like a reply, and a thread with no index starts with the 22-byte header alone, stamped with the current time (what Outlook 16 does).
- vCard import to a contact follows Outlook 16: an `ADR` fills one slot (a named, free slot first, then Business, Home, Other in order), the mailing address defaults to Business, then Home, then Other, and a `TEL` with no known type is dropped (`NoContactMapping`).


## [0.8.0-preview.1]

### Added

- `MsgWriterOptions.AddRequiredProperties` (above).
- `tools/outlook`: scripts that run Outlook and its repair tool over written stores and message files on a Windows machine with a signed-in desktop session (object-model open and item comparison, repair-tool scans, Outlook-made fixtures including damaged and deleted-from stores), with the comparison against our reader's view; `testdata/pst/outlook-ansi.pst`, a small store Outlook wrote in the ANSI format.
- `tools/providers`: OAuth token minting (Google loopback with PKCE, Microsoft device code) and a provider smoke runner for Gmail and Outlook.com over SMTP, IMAP and POP3; setup and endpoints in `docs/providers.md`.

### Changed

- MIME hot paths, tuned against the native reference (details and numbers in `docs/benchmarks.md`). Base64 bodies decode in bulk: lines of one width are cut out at their stride and handed to the BCL's vectorised decoder, `GetDecodedContent()` sizes its result from the body's layout, and decoding streams borrow their buffers from the pool (the 6 MB message's bodies: 8.1 ms and 16.8 MB allocated before, about 0.8 ms and 4.5 MB after; byte-identical to the byte-at-a-time decoder on randomised and fuzzed inputs, damaged ones included). `SetDecodedContent` with base64 keeps the attachment once and encodes it as the part is written or read (a message with a 1 MB attachment: 397 us and 2.7 MB before, 141 us and 1.0 MB after). Plain address lists and dates in the usual form are read straight from the bytes, header look-ups no longer build a string for every header, and parsing allocates less (parse plus five decoded headers of a typical message: 2.9 to 3.5 us before, 1.4 to 1.6 us after). Everything else goes through the same general parsers; parse results, defects and written bytes are unchanged (517,401 of 517,401 Enron dumps byte-identical).
- PST writing is now checked against Outlook and its repair tool, and the writer follows what they require beyond MS-PST (the list is in `docs/pst-writing.md`): the header's NID counters reach the highest index in use and embedded messages take message NIDs, the receive-folder, outgoing-queue, template and search tables and nodes Outlook looks for exist, table columns sort as unsigned tags, hierarchy rows carry cells only for properties the folder has, `PidTagMessageSize` and `PidTagAttachSize` are the sizes Outlook computes (and are recomputed on every write), `PidTagMessageClass` is always a Unicode string, `MSGFLAG_HASATTACH` follows the attachments in both directions, heap blocks are full except the last and a row matrix's last block is never padded, and a recipient with no `PidTagRecipientType` is written as To. The builder set, the in-place and compacted stores and a 100,000-row folder (before and after row-by-row edits) open in Outlook with the folders and items our reader shows, and the repair tool reports only the minor findings it also reports on stores Outlook wrote.
- The in-place editor refuses to move or delete the top-level folders of a store (the top of the store, the search root, the spam search folder) and to move anything into the root (`PstEditFailure.InvalidFolderOperation`): Outlook's repair tool rejects a store where they have another parent.
- PST and OST reading: the row index of an ANSI table has 2-byte values (MS-PST 2.3.4.3), and a `PtypString8` value ends at its first NUL (Outlook's ANSI stores keep stale bytes after it). Both were found on a store Outlook wrote in the ANSI format, which is now in `testdata/pst`.
- MSG writing adds `PidTagRowid` to recipients and `PidTagAttachNumber` to attachments that lack them (Outlook cannot save the first or open the second) and splits a PST item's U+0001 subject prefix into `PidTagSubject`, `PidTagSubjectPrefix` and `PidTagNormalizedSubject`; each is reported as `PropertyAdjusted`. `MsgWriterOptions.AddRequiredProperties` (default true) turns it off.
- `TransferEncoder.Base64` encodes once and spreads the 76-character lines in place instead of calling the encoder per line and copying the result: building a message with a 1 MB attachment, 750 us to 445 us and 4.1 MB to 2.7 MB allocated (x64, indicative).
- The test servers script turns off podman's per-container session keyring and stops podman's pause helper after each run, so repeated runs can no longer use up the user's kernel key quota.
- `VCardView.Emails`, `Phones`, `Urls`, `Addresses` and the other typed lists count their properties when asked and build an item the first time it is read, so counting costs a scan of the card: vCard parse with a typed pass over 10,000 cards, 79.7 ms to 55.8 ms (comparator 67.2 ms), 5,500 to 4,000 bytes per card (Apple M2, indicative).
- In-place PST writing changes a big folder's contents, associated-contents and hierarchy tables row by row at commit instead of writing them whole: with 100,000 rows a one-item add commits in about 0.15 s instead of 2.5 s and writes 0.2 MiB instead of 33 MiB; with 600,000 rows 0.45 s instead of 10.7 s (`docs/benchmarks.md`, `docs/pst-writing.md`). `DeleteMessage` and `MoveMessage` no longer read the folder's table again for every call. Small tables, tables of other layouts and large change sets are still written whole, with the same results.
- `RecursiveAI.OneCrypto` 0.4.0-preview.1 (was 0.1.0-preview.1), plus `RecursiveAI.OneCrypto.OpenPgp`, for `.Cryptography`.
- Protocol clients allocate far less: IMAP `FetchAsync` decodes plain FETCH lines in place (about 1 KB per message for UID, FLAGS, INTERNALDATE and ENVELOPE against about 7 KB), downloads allocate a constant amount whatever the number of reads, SMTP sending and POP3 listings drop their per-message temporaries (about 10 KB per 4 KB message sent, from about 30 KB). Read and write time limits are checked by one coarse timer per direction, so a stall fails after 1 to 1.25 times the limit.

### Added

- `tests/Wasm`: the format-core tests (core and Calendar: everything under `tests/OneMail.Tests` except protocols, cryptography, rendering and the command-line tool) run in headless Chromium on `browser-wasm` through a small runner (`tests/Wasm/run.sh`). A test that needs something the browser lacks carries `[Trait("Platform", "native")]` with a `Reason` and is counted as an exclusion.
- Samples for every happy-path task (`samples/ParseMessage`, `OpenStore`, `Convert`, `Sending`, `Imap`, `Display`, `ReplyAndSend`, plus `Reading`), `docs/migrating-from-mimekit.md` (a map from MimeKit and MailKit calls to OneMail's, and the behavioural differences) and a README quick start.
- `DisplayOptions.WithStatusBanner(...)`: a copy of the options with the status banner replaced; `PgpDisplay` and `SmimeDisplay` use it instead of copying every field.
- S/MIME in Thunderbird: `tools/thunderbird/smime.py` imports the test CA and the user's PKCS #12 file into the headless Thunderbird 159.0's certificate store and opens our signed, encrypted and signed+encrypted messages (RSA-2048, RSA-3072, ECDSA P-256; 15/15, and an untrusted-CA control 3/3), and has Thunderbird write signed, encrypted and combined messages that OneMail verifies and decrypts (9/9). `SmimeInterop thunderbird` writes the vectors; both sets are committed in `testdata/smime/vectors` and read back by `SmimeInteropVectors`.
- `SmimeDisplay.ToDisplayHtml(SmimeResult)` and `SmimeDisplay.StatusHtml`: a display with a status block (signer, chain, sender match, encryption, parts outside the signature, weak algorithms) through `DisplayOptions.StatusBanner`, never showing unsigned wrapper parts as signed.
- Thunderbird interoperability for OpenPGP mail: `tools/thunderbird` runs the pinned Thunderbird 159.0 release headless, opens our PGP/MIME messages (signed, encrypted, combined, sign-then-encrypt) and has it write signed, encrypted and combined messages that `PgpInteropVectors` reads back from `testdata/openpgp/vectors/thunderbird`.
- OpenPGP follow-ups (`docs/openpgp.md`): protected headers on write (`PgpProtectedHeaders`, `PgpMimeSignOptions.ProtectedHeaders`, `PgpMimeEncryptOptions.ProtectedHeaders`, optional obscured outer `Subject`); `PgpIssue.ProtectedRecipientMismatch` and `ProtectedRecipientsMissing` for surreptitious forwarding (`PgpOpenOptions.RecipientAddresses`); attached public keys (`PgpResult.AttachedKeys`) and encrypted attachments (`OpenPgpAttachments`, `PgpMimeProcessor.OpenAttachment`); inline OpenPGP in HTML parts; `PgpDisplay.ToDisplayHtml` with `DisplayOptions.StatusBanner` for the security status; `PgpWkdKeySource` over OneCrypto's Web Key Directory client (`RecursiveAI.OneCrypto.Online` 0.4.0-preview.1 is now a dependency of `.Cryptography`). Public enums gained members at the end.
- The calendar card now covers Outlook for Mac tasks (`CalendarCard.FromItem`).
- `DkimSignatureResult.CheckAlignment` (signing domain against the From domain: `DkimAlignment.Exact`, `ParentOrChild`, `None`; no policy), `CachingTxtResolver` with `TxtLookupResult.Ttl` (filled by `DnsTxtClient` from the answer's TTLs) for DKIM key lookups, and `DbxRecoveryDefect.NotOnDeletedList` (freed DBX segments the deleted-message list does not reach are now found by the scan and tagged `FreeSpace`). `ArcSealer.SealAsync` refuses a passed chain status that cannot fit the message (`none` over a chain, or a verdict with no chain); a passed status is otherwise the caller's assertion and is documented as trusted.
- `tools/fuzz/fetch.sh` fetches libfuzzer-dotnet and SharpFuzz.CommandLine by pinned version and hash; `tools/oracles/light.sh` builds the oracle drivers on a fresh workspace; the libpff comparison pairs items with equal times, size and flags by subject too.
- `TableContextEditor` and `TableEditUnsupportedException` (`RecursiveAI.OneMail.Pst.Ltp`): add, replace and remove rows of an existing table context node inside an `NdbEditor` transaction without writing the rest of the table (`docs/pst-ltp.md`).
- OpenPGP mail in `RecursiveAI.OneMail.Cryptography.OpenPgp` (`docs/openpgp.md`): PGP/MIME signing, encrypting (encrypt-only, combined, sign-then-encrypt) and opening (`PgpMimeSigner`, `PgpMimeEncryptor`, `PgpMimeProcessor`, structured `PgpResult`), inline cleartext-signed and armored bodies with typed repair issues, protected headers, `PgpAutocrypt`, `IPgpKeySource`; built on OneCrypto 0.4.0-preview.1. Interop vectors with GnuPG and Sequoia in `testdata/openpgp` (`tools/openpgp`), fuzz target `pgp-mime`, AOT smoke check.
- `bench/ProtocolRows` and `bench/run-protocol-rows.sh`: the IMAP fetch and SMTP send rows against MailKit 4.18.2 on local Dovecot and Postfix (`docs/benchmarks.md`, Protocol rows); fuzz target `imap-fetch`.
- Android and iOS smoke apps (`smoke/Android`, `smoke/iOS`; `smoke/Mobile.props` is what they share) with `run-emulator.sh` and `run-simulator.sh`: build trimmed in Release with warnings as errors, run the NativeAOT smoke app's checks on an emulator or simulator and exit 0 only on `mobile smoke: ok`. CI builds the Android app. `smoke/Aot` gained `Program.Run()` so the three apps share one set of checks.
- `OutgoingMessage.Utf8Headers` and `CompositionOptions.Utf8Headers`: write display names, the subject and file names as raw UTF-8 (RFC 6532) for an SMTPUTF8 transport; the default stays ASCII. `PstEditException` with `PstEditFailure` (`FolderNotFound`, `ItemNotFound`, `InvalidFolderOperation`, `NidSpaceExhausted`): the typed refusal of an edit to an open PST, thrown before anything is written.
- PST and OST recovery (`RecursiveAI.OneMail.Pst.Recovery.PstRecovery`, `docs/recovery.md`): recovers what a damaged store still holds without its header, B-trees, allocation maps or density list, from a scan of its pages and blocks (generation and encoding inferred when the header is destroyed; the newest leaf page of the node tree wins; entries of damaged pages kept only when the blocks they name are whole), returns each message with a `RecoveryProvenance` (`Live` only when the store's own node tree reaches it; `Orphaned` for entries rebuilt from the scan), and with `PstRecoveryOptions.Forensic` also `SoftDeleted` messages, stale and superseded nodes and `FreeSpace` blocks. `RepairTo` writes a new Unicode PST through `PstBuilder` (items in their folders, deleted ones under `Recovered/<tag>`, provenance as named properties, a JSON report; values that cannot be read in full are dropped and listed). `onemail recover` takes `.pst` and `.ost` (`--forensic`, `--pst` for a destroyed header). `MapiMailItem.Recovery` carries the provenance. `NdbFile.Scan` now also finds a live block written over a stale page (a page trailer with a failing checksum no longer hides the blocks in its unit). Ambiguities PR1 to PR8, fuzz target `pst-recovery`, `tools/recovery/check-pst.sh` and `memory-pst.sh`, `PstOracle pstdamage|pstrecover|pstcampaign`.
- Forensic recovery, opt-in, never writing to its input (`docs/recovery.md`). `RecursiveAI.OneMail.Recovery`: `RecoveryProvenance` (tag `Live`, `SoftDeleted`, `Orphaned`, `FreeSpace`, `Partial`; source byte ranges, lost ranges, qualifying defects, PST `Nid`/`Bid` locators) reached through the new default member `IMailItem.Recovery`.
- `DbxReader.RecoverDeleted`: messages freed by Outlook Express (rebuilt from the deleted-message list and the old next pointers that survive in freed segments) and chains no index entry reaches; nothing a live read lists is returned. `OlmReaderOptions.Recovery` and `OlmArchive.Damage`: damaged OLM archives open through OneArchive's zip recovery mode, damage records become defects and items from damaged entries are tagged `Partial`.
- Span-level `TryParse` for tools that find pieces elsewhere than a file's own structure puts them: `MimeHeaderBlock.TryParse`, `MimeEntity.TryParse`, `NdbSpan.TryParsePage` and `TryParseBlock`, `DbxSpan.TryParseSegment` and `TryParseRecord`, `CfbSpan.TryParseHeader` and `TryParseDirectoryEntry`.
- `onemail recover`: DBX and OLM recovery with a JSON provenance report; `tools/recovery` (damaged OLM variants, DBX recovery checks against `undbx -r`), `DbxBuild.Deleted`/`Unlinked` in the test builder, fuzz targets `span-ndb`, `span-mime`, `span-cfb`, `dbx-recovery`, `olm-recovery`.

- In-place writing of existing Unicode PSTs (preview; the commit path has had its independent review, see `docs/pst-writing.md`, *Review*): `PstStore.OpenForWriting` returns a `PstWritableStore` with `AddMessage`, `ReplaceMessage`, `MoveMessage`, `DeleteMessage`, `CreateFolder`, `RenameFolder`, `MoveFolder`, `DeleteFolder`, `Commit` and `Rollback`; `NdbEditor` (`.Pst.Ndb`) is the node-database layer under it: exclusive open, header CRC, allocation-map and density-list checks with typed refusals (`NdbEditRefusedException`, `NdbOpenRisks`), allocation from the maps with growth by range, copy-on-write B-trees with splits and merges, block reference counts, zeroing of freed space (opt out with `ZeroFreedSpace`), and a commit that switches the header once so a stop leaves the old or the new state. `docs/pst-writing.md` has the sequence.
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

- `OutgoingMessage` wrote a display name that mixed non-ASCII and ASCII words with only the non-ASCII words encoded, so a comma, quote or `@` in the plain part could corrupt the address list. It now encodes a non-ASCII name as a whole, as the reply and forward builders already did (both now share one formatter). 200 composed messages read back identically through OneMail, MimeKit and Go and, apart from Python's known reading of raw UTF-8, Python (`docs/composition.md`).
- `PstBuilder` wrote the header's per-type NID counters as the next index to hand out; MS-PST 2.2.2.6 defines them as the last one allocated, which is what Outlook-written files hold.
- `PstWritableStore` refused edits (missing folder or item, moving the root or a folder into itself, no NID left) with `ArgumentException` or `InvalidOperationException`; they are `PstEditException` now. `ReplaceMessage` writes the new item before it touches the folder's row.
- The calendar card and `MimeMailItem.CalendarParts` missed an .ics attachment typed `application/ics`.
- OLM recovery decompressed each record entry twice (once to judge damage, once to read the records); it decompresses once.
- `docs/ambiguities.md`: the PST/LTP rows are LT1 to LT4; they shared L1 to L4 with the MIME line-end rows. `docs/pst-writing.md` says why the exclusive open is advisory on Linux and macOS. `docs/compatibility.md` S/MIME results refreshed.
- In-place writing, found by the independent review of the commit path (`docs/pst-writing.md`, *Review*):
  - A header with valid checksums and a lowered `ibFileEof` made `NdbEditor.Open` cut the file at that size without any opt-in, destroying everything after it. Both B-trees are now walked when the file is opened, and a file whose pages or blocks lie outside the recorded size, or whose size is not a size, is refused (`NdbRefusalReason.Damage`, never allowable); the file is cut only after that proof.
  - Allocation maps that add up but call space free that a tree reaches are refused (`AllocationMaps`; with the flag the maps are rebuilt) instead of being allocated from over live data.
  - A damaged `ibAMapLast` or `ibFileEof` could make a rebuild loop over a made-up number of ranges, or the next commit grow the file to it; sizes are now clamped to the file's length.
  - Index pages naming themselves or an ancestor, or at the wrong level, looped forever; levels are enforced on every step and a page reached twice stops the walk. A damaged page is found when the file is opened, not half-way through an edit (typed refusal, not `InvalidDataException` or `ArgumentOutOfRangeException`).
  - A header whose `bidNextB`/`bidNextP` lagged the trees (for example after a torn header write) made new blocks reuse BIDs in use; both are raised above what the trees use.
  - A block-tree entry whose size disagreed with the block's own trailer made freeing zero the neighbouring blocks; such a block is now left allocated.
  - `DeleteFolder` followed a hierarchy row naming any folder, so a damaged table could delete an unrelated folder or recurse forever; only folders whose node says they are children are followed, each once.
  - New NIDs started at the header's counter instead of after it. MS-PST 2.2.2.6 makes `rgnid` the last index allocated, and a file Outlook wrote uses the counter's own value for the embedded message of an attachment (a subnode), so the first item added to such a file took that embedded message's NID and the libpff tools exported the new item in its place (9 of 40 sampled leftovers of the Outlook-written test file differed; 0 of 87 after the fix).
  - A rolled-back transaction that had grown the file left its writes past the recorded end; they are cut off.
- `tests/OneMail.Tests` `NdbChecker`: an independent recomputation of references, space in use, free counts, density list and BID counters from the two trees, used by the durability runner and the soak.

- Moving a folder twice in one in-place commit (or away and back) left a stale row in the intermediate parent's hierarchy table and a wrong subfolder flag; found by the 1,000-operation soak.
- PST allocation map bits count from the **high** bit of each byte, as in files Outlook wrote; the builder wrote them from the low bit, so a program that trusted the maps of a builder-written file saw the wrong slots as used. `NdbFile.IsAllocated` has the same fix.
- iCalendar to Outlook item: a series that ends by `UNTIL` no longer counts a last day whose start is after `UNTIL`; a yearly rule whose `BYMONTH` is not the month of DTSTART keeps its month; a yearly rule with `BYMONTHDAY` and no `BYMONTH` is every month, as RFC 5545 says, instead of the month of DTSTART; `BYHOUR` and `BYMINUTE` set the series' time; a first instance moved to the first allowed day keeps its length; the sender of a REPLY is the attendee and the recipient the organizer.
- A series across a change of its zone's daylight saving rules (New York in 2007, Sydney in 2008) was an hour out in the affected weeks after conversion: the item now carries one time zone rule per change and the export writes them back.
- An Outlook item whose PidLidTimeZoneStruct holds a rule no VTIMEZONE can state (found by the fuzzer) made the export throw `InvalidOperationException`; the item now reads as having no zone, with a `ValueOutOfRange` defect.
- Outlook item to iCalendar: PidLidRecurrenceType, the end-display time zone, PidLidResponseStatus and PidLidIsException are reported as derived, not as properties iCalendar cannot hold.

### Security

- DKIM: a signature no longer passes while the message holds a `From` field it does not cover (`DkimReason.UnsignedFromHeader`; `DkimVerifierOptions.AllowUnsignedFromInstances` restores the old result), or while `l=` leaves content after the signed part of the body (`DkimReason.BodyLengthLimited`; `AllowBodyLengthLimit` restores it). `DkimSignatureResult.UnsignedHeaderInstances` and the `UnsignedHeaderInstances` warning name any header in `h=` that the message holds more often than the signature covers.
- DKIM and ARC: a message with more than `DkimVerifierOptions.MaxHeaderFields` (10,000) header fields is not verified (`DkimReason.TooManyHeaders`, a failed ARC chain); header selection no longer scans every field once per name in `h=`.
- `DkimSigner` refuses header names that could add tags to the signature.
- S/MIME: a signed layer succeeds only when every signer's chain was built to a caller-supplied anchor; with none it ends as `UntrustedChain` and `SmimeResult.Succeeded` is false, because anyone can sign with a certificate they made themselves. `SmimeOpenOptions.AllowUntrustedSigner` opts out. `SmimeSignerResult.IsTrusted`, `SmimeSignedLayer.IsTrusted` and `SmimeResult.IsTrusted` say whether a chain to a caller-supplied anchor was built (false under the opt-out). `SmimeResult.Succeeded` is false for a signed part inside an unsigned wrapper. The sender check compares the signed entity's own `From` when it has one, and a `From` naming a mailbox the certificate does not cover is a mismatch. Platform decryption errors after a recipient matched report as `DecryptionFailed` like every other failure. The default CMS content and decompression limits are 256 MiB.
- OpenPGP mail: headers repeated inside an encrypted message with no signature are no longer treated as protected or shown as the message's headers (`InnerItem` keeps the outer ones); they count only when `PgpResult.IsAuthenticated`. `PgpSignerResult.UserIdMatchesSender` and `PgpKeySource.ResolveRecipientsAsync` count only User IDs that the certificate's own key certified and had not revoked, and the sender match needs every `From` address named. New `PgpResult.IsSenderAuthenticated`. `micalg` is quoted when several hashes are listed. `PgpMimeEncryptor.Encrypt` rejects a `Boundary` that occurs in the ciphertext.

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

[Unreleased]: https://github.com/recursive-ai-oss/onemail-csharp/compare/v1.0.1...HEAD
[1.0.1]: https://github.com/recursive-ai-oss/onemail-csharp/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/recursive-ai-oss/onemail-csharp/compare/v0.3.0-preview.2...v1.0.0
[0.3.0-preview.2]: https://github.com/recursive-ai-oss/onemail-csharp/compare/v0.2.0-preview.2...v0.3.0-preview.2
[0.2.0-preview.2]: https://github.com/recursive-ai-oss/onemail-csharp/releases/tag/v0.2.0-preview.2
