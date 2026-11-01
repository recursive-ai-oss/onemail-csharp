# Changelog

All notable changes are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- `MsgWriter` (`RecursiveAI.OneMail.Msg`): writes MSG and OFT files from the MAPI model (named properties, recipients, attachments, embedded messages, OLE storages), Unicode or code page strings, limits checked before the first byte, large attachment data copied from the caller's stream (`MapiStreamValue`). Outlook opening the files is not yet verified.
- `ConvertFormat.Msg`, `StoreConverter` and `onemail convert --to msg`: any readable store to MSG files with loss reports.
- `MapiDistributionList.Create` and `Notes.MapiNote`: builders for distribution list and sticky note items.

- PST write primitives, the layer under the Unicode PST builder (Unicode, 512-byte pages; nothing is placed in a file yet). `RecursiveAI.OneMail.Pst.Ndb`: `NdbBlockBuilder` makes data blocks with their trailers and CRCs (none, permute and cyclic encodings), data trees (XBLOCK and XXBLOCK, from a span, a stream or blocks the caller sizes) and subnode trees (SLBLOCK and SIBLOCK), handing each `NdbBlock` to an `INdbBlockSink` with a BID from an `IBidAllocator` (`SequentialBidAllocator`); the signature is filled in for the offset the caller chooses. `RecursiveAI.OneMail.Pst.Ltp`: `HeapNodeBuilder` (HIDs, page maps, fill levels, multi-block heaps), `BthTreeBuilder` (any key and entry size, index levels), `PropertyContextBuilder` (inline, heap or subnode values by size, streaming for large ones) and `TableContextBuilder` (columns, row matrix in the heap or a subnode, row index, rows streamed block by block), each result an `LtpNodeResult`. Every property and table context of the Tika PSTs and the CFReDS OST rebuilds and reads back identical. See `docs/pst-ndb.md` and `docs/pst-ltp.md`.

- `RecursiveAI.OneMail.Cryptography` (new package, preview): DKIM signing and verification (RFC 6376, RFC 8301, RSA-SHA256 and Ed25519-SHA256 per RFC 8463) with structured per-signature results, ARC chain validation and sealing (RFC 8617), a pluggable `ITxtResolver`, and `DnsTxtClient` (DNS TXT over UDP with TCP fallback, no DNSSEC). RSA runs on the base class library, Ed25519 on `RecursiveAI.OneCrypto` 0.1.0-preview.1. New signatures refuse RSA under 2048 bits and rsa-sha1. See `docs/dkim-arc.md`.
- `RecursiveAI.OneMail.Render`: `ToPdfAsync` and `ToPngAsync` on any item render the display HTML to a tagged PDF or PNG pages through OneRender, with page size and margins, header and attachment-list switches, fonts, PNG scale and page range, limits on pages and time, and a loader that decodes `data:` URIs and fetches nothing else. See `docs/render.md`.
- `onemail inspect` reads ICS, vCard, jCal, jCard, xCal and xCard files: the number of calendars or cards, the kinds of components in them and the reader's defects (`--json` and `--defects` supported).
- Bodies (`RecursiveAI.OneMail.Bodies`): HTML to text on OneHtml's DOM (blocks, lists, tables, quotes, links, images, `pre`, hidden content, optional wrapping, limits), text to HTML with quote levels and conservative links, `format=flowed` decode and encode (RFC 3676, DelSp), and best-body selection across MIME, Outlook, TNEF, OLM and DBX items (`BodySelector`, `IMailItem.BestBody`). Every body carries one `MailBody.Origin` (stored, RTF-encapsulated, RTF-converted, HTML-to-text, text-to-HTML, flowed-decoded); a reply to an HTML-only message now carries a text alternative made from its HTML.

### Security

- Display HTML had its independent review (see `docs/display-html.md`, *Review*): no script execution, no fetch under the Block policy and no escape from the scope box in Chrome or Firefox. Fixed on the way: quadratic link-bracket trimming in plain-text display and per-reference part scans in `cid:`/location resolution (both denial of service on crafted input); control characters are no longer written; `format=flowed` quote depth is capped at 32; verify passes keep only URLs the first pass wrote; a parenthesis glued to a CSS value token is refused.

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
