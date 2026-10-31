# OneMail

An email library for .NET with no dependency beyond the .NET base class library and its sibling libraries (OneArchive, OneHtml, OneRender). It reads and writes the formats mail lives in, keeps what it reads lossless, and says what it repaired instead of throwing.

> **Early preview.** This repository holds the build skeleton and one placeholder type per package. There is no mail functionality yet; nothing here is a stable API.

## Goals

- **Reads:** MIME/EML, MHTML, TNEF, MSG/OFT, PST and OST, MBOX variants, Maildir, EMLX, OLM, DBX, ICS and vCard (and their JSON/XML forms).
- **Writes:** the exchange formats, MSG/OFT and Unicode PST.
- **Lossless and lenient:** untouched MIME, ICS and vCard write back byte for byte; Outlook data round-trips property for property; every repair of malformed input is a typed defect with its byte offset.
- **Every layer public:** random-access sources, the MIME reader and writer, the PST layers and the raw protocol layer, with a one-line happy path on top.
- **Protocols and cryptography** in separate packages: SMTP, IMAP, POP3, SASL; DKIM, ARC, S/MIME and OpenPGP.
- **NativeAOT, trimming and browser WebAssembly** (format core) are supported targets from the start.

## Packages

All packages target `net10.0` and share one version.

| Package | Contents | Depends on |
|---|---|---|
| `RecursiveAI.OneMail.Calendar` | ICS, vCard, JSON/XML forms, recurrence, time zones | .NET only |
| `RecursiveAI.OneMail` | the formats, the models and the one-line entry points | `.Calendar`, `RecursiveAI.OneArchive`, `RecursiveAI.OneHtml` |
| `RecursiveAI.OneMail.Client` | SMTP, IMAP, POP3, SASL and the raw protocol layer | `RecursiveAI.OneMail` |
| `RecursiveAI.OneMail.Cryptography` | DKIM, ARC, S/MIME, OpenPGP | `RecursiveAI.OneMail` |
| `RecursiveAI.OneMail.Render` | a message to PDF or an image | `RecursiveAI.OneMail`, `RecursiveAI.OneRender` |
| `RecursiveAI.OneMail.Cli` | the `onemail` command-line tool | `RecursiveAI.OneMail` |

## Clean room

OneMail is written from public specifications (RFCs, Microsoft's Open Specifications, WHATWG) and from observing released programs as black boxes. The source of other mail libraries is not read.

## Not affiliated

This project is unaffiliated with other products named OneMail.

## Building

See [CONTRIBUTING.md](CONTRIBUTING.md).

## Licence

MIT OR Apache-2.0, at your option: [LICENSE-MIT](LICENSE-MIT), [LICENSE-APACHE](LICENSE-APACHE). Copyright (c) Recursive AI, LLC.

