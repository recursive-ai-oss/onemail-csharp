# OneMail

An email library for .NET with no dependency beyond the .NET base class library and its sibling libraries: the packages use OneArchive and OneHtml (the `.Render` package adds OneRender). It reads and writes the formats mail lives in, keeps what it reads lossless, and says what it repaired instead of throwing.

> **1.0.** Reading works for MIME, the mailbox stores, ICS/vCard, MSG/OFT, PST/OST, TNEF, OLM and DBX; writing for MIME, the mailbox stores, ICS/vCard, MSG/OFT and Unicode PST (a new file with the builder, or an existing file in place); recovery of damaged PST/OST, DBX and OLM; SMTP, IMAP and POP3 clients; display HTML and PDF/PNG rendering. In-place PST writing commits by MS-PST's two-root protocol; its commit path was reviewed by an engineer who did not write it, and Outlook and its repair tool checked the output. **Writing to an existing PST in place is preview quality: keep a copy of the file before you write to it** ([docs/pst-writing.md](docs/pst-writing.md#in-place)). A new PST from the builder is not covered by that caveat. What is checked, and the known gaps: [docs/compatibility.md](docs/compatibility.md).
>
> **`.Cryptography` is not part of 1.0.** S/MIME, OpenPGP, DKIM and ARC (`RecursiveAI.OneMail.Cryptography`) are built and tested in this repository on OneCrypto, which is still a preview, and a stable package cannot depend on a prerelease. The package publishes at the first 1.x release after OneCrypto 1.0, with prerelease versions on the way.
>
> **Display HTML was reviewed by an engineer who did not write it.** Script execution and network fetch under the Block policy were checked in Chrome and Firefox; the threat model and what the output assumes (one message per frame or document) are in [docs/display-html.md](docs/display-html.md#review).

## Known gaps

Listed in full in [docs/compatibility.md](docs/compatibility.md); none is hidden.

- **Outlook.com provider rows have not been run.** Gmail passed over SMTP, IMAP and POP3 (27 pass, 0 fail); Outlook.com needs an OAuth client that is not set up yet. Yahoo and iCloud have not been run either. The Dovecot, Postfix, Stalwart and Cyrus containers pass.
- **Outlook's verdict on a signature cannot be read unattended**, so S/MIME signature validity in Outlook's reading window is not checked (the window says "problems with the signature" for valid and tampered messages alike).
- **The repair tool's 65,535-row limit.** `scanpst.exe` miscounts a table of more than 65,535 rows, on stores Outlook wrote as well as ours, so a folder of 66,000 or more items could not be checked with it. Outlook itself opens those stores.
- **The protocol rows are x64 only.** The container protocol suites have not been run on arm64.
- **`LastVerbExecuted` after a real send** (the reply and forward marks Outlook keeps on the original) needs a message that was actually sent, and has not been checked against Outlook.

## Quick start

Each snippet is the whole task; the runnable versions are in [`samples/`](samples).

**Reading** ([ParseMessage](samples/ParseMessage/Program.cs)):

```csharp
IMailItem message = Mail.Parse(File.ReadAllBytes("message.eml"));
Console.WriteLine($"{message.Subject} from {message.From[0]}: {message.BestBody?.ReadText()}");
```

**Opening stores**: PST, OST, mbox, Maildir, OLM, DBX, MSG and EML, found by content ([OpenStore](samples/OpenStore/Program.cs)):

```csharp
await using MailContainer store = Mail.Open("archive.pst");
await foreach (IMailItem item in store) Console.WriteLine(item.Subject);
```

**Converting**, with the loss report ([Convert](samples/Convert/Program.cs)):

```csharp
StoreConversionReport report = await Mail.ConvertAsync("archive.pst", ConvertFormat.Mbox, "out");
```

**Sending** (`RecursiveAI.OneMail.Client`; [Sending](samples/Sending/Program.cs)), and **replying** ([ReplyAndSend](samples/ReplyAndSend/Program.cs)):

```csharp
await Mail.SendAsync(new OutgoingMessage { From = me, To = [you], Subject = "Hello", Text = "Hi." }, new SmtpClientOptions { Host = "smtp.example.com", Credentials = creds });
await Mail.SendAsync(original.CreateReply(new CompositionOptions { From = me }).Message, server);
```

**IMAP** ([Imap](samples/Imap/Program.cs)), **display HTML and PDF** ([Display](samples/Display/Program.cs)):

```csharp
await foreach (ImapEvent e in imap.IdleAsync(stop: token)) Console.WriteLine(e.Kind);
DisplayResult html = message.ToDisplayHtml();   // sanitised; await message.ToPdfAsync(stream) with .Render
```

Coming from MimeKit or MailKit? [docs/migrating-from-mimekit.md](docs/migrating-from-mimekit.md) maps the calls.

## Goals

- **Reads:** MIME/EML, MHTML, TNEF, MSG/OFT, PST and OST, MBOX variants, Maildir, EMLX, OLM, DBX, ICS and vCard (and their JSON/XML forms).
- **Writes:** the exchange formats, MSG/OFT and Unicode PST.
- **Lossless and lenient:** untouched MIME, ICS and vCard write back byte for byte; Outlook data round-trips property for property; every repair of malformed input is a typed defect with its byte offset.
- **Every layer public:** random-access sources, the MIME reader and writer, the PST layers and the raw protocol layer, with a one-line happy path on top.
- **Protocols** in a separate package: SMTP, IMAP, POP3 and SASL. **Cryptography** (DKIM, ARC, S/MIME, OpenPGP) follows the first 1.x after OneCrypto 1.0.
- **NativeAOT, trimming and browser WebAssembly** (format core) are supported targets from the start.

## Packages

All packages target `net10.0` and share one version.

| Package | Contents | Depends on |
|---|---|---|
| `RecursiveAI.OneMail.Calendar` | ICS, vCard, JSON/XML forms, recurrence, time zones | .NET only |
| `RecursiveAI.OneMail` | the formats, the models and the one-line entry points | `.Calendar`, `RecursiveAI.OneArchive`, `RecursiveAI.OneHtml` |
| `RecursiveAI.OneMail.Client` | SMTP, IMAP, POP3, SASL and the raw protocol layer | `RecursiveAI.OneMail` |
| `RecursiveAI.OneMail.Render` | a message to PDF or PNG pages | `RecursiveAI.OneMail`, `RecursiveAI.OneRender` |
| `RecursiveAI.OneMail.Cli` | the `onemail` command-line tool | `RecursiveAI.OneMail` |

`RecursiveAI.OneMail.Cryptography` (S/MIME, OpenPGP, DKIM, ARC) is not in 1.0.0: it publishes at the first 1.x after OneCrypto 1.0, with previews on the way.

## Clean room

OneMail is written from public specifications (RFCs, Microsoft's Open Specifications, WHATWG) and from observing released programs as black boxes. The source of other mail libraries is not read.

## Not affiliated

This project is unaffiliated with other products named OneMail.

## Building

See [CONTRIBUTING.md](CONTRIBUTING.md).

## Licence

MIT OR Apache-2.0, at your option: [LICENSE-MIT](LICENSE-MIT), [LICENSE-APACHE](LICENSE-APACHE). Copyright (c) Recursive AI, LLC.

