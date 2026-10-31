# Changelog

All notable changes are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Fixed

- Encoding to ISO-8859-1 and US-ASCII code pages (the runtime's own, not in the code page provider) no longer throws in `MailEncoding.GetBytes`.

### Added

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
