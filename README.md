# OneMail

An email library for .NET with no dependency beyond the .NET base class library and its sibling libraries: the published packages use OneArchive and OneHtml (the unpublished `.Render` package adds OneRender). It reads and writes the formats mail lives in, keeps what it reads lossless, and says what it repaired instead of throwing.

> **Preview.** Reading works for MIME, the mailbox stores, ICS/vCard, MSG/OFT, PST/OST, TNEF, OLM and DBX; writing for MIME, the mailbox stores, ICS/vCard, MSG/OFT and Unicode PST (a new file, or in place in preview quality); recovery of damaged PST/OST, DBX and OLM; SMTP, IMAP and POP3 clients; S/MIME, OpenPGP, DKIM and ARC (`.Cryptography`, on OneCrypto). The PST commit path and the crypto paths have had independent reviews. Nothing here is a stable API yet. What is checked, and the known gaps: [docs/compatibility.md](docs/compatibility.md).
>
> **Display HTML has had its independent review.** Script execution and network fetch under the Block policy were checked in Chrome and Firefox; the threat model and what the output assumes (one message per frame or document) are in [docs/display-html.md](https://github.com/recursive-ai-oss/onemail-csharp/blob/main/docs/display-html.md#review).

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
- **Protocols and cryptography** in separate packages: SMTP, IMAP, POP3, SASL; DKIM, ARC, S/MIME and OpenPGP.
- **NativeAOT, trimming and browser WebAssembly** (format core) are supported targets from the start.

## Packages

All packages target `net10.0` and share one version.

| Package | Contents | Depends on |
|---|---|---|
| `RecursiveAI.OneMail.Calendar` | ICS, vCard, JSON/XML forms, recurrence, time zones | .NET only |
| `RecursiveAI.OneMail` | the formats, the models and the one-line entry points | `.Calendar`, `RecursiveAI.OneArchive`, `RecursiveAI.OneHtml` |
| `RecursiveAI.OneMail.Cli` | the `onemail` command-line tool | `RecursiveAI.OneMail` |

These three are on nuget.org. `RecursiveAI.OneMail.Client` (SMTP, IMAP, POP3, SASL), `.Cryptography` (DKIM and ARC now in preview; S/MIME and OpenPGP later) and `.Render` (a message to PDF or an image) come in later previews.

## Clean room

OneMail is written from public specifications (RFCs, Microsoft's Open Specifications, WHATWG) and from observing released programs as black boxes. The source of other mail libraries is not read.

## Not affiliated

This project is unaffiliated with other products named OneMail.

## Building

See [CONTRIBUTING.md](CONTRIBUTING.md).

## Licence

MIT OR Apache-2.0, at your option: [LICENSE-MIT](LICENSE-MIT), [LICENSE-APACHE](LICENSE-APACHE). Copyright (c) Recursive AI, LLC.

