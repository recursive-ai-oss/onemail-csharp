# OneMail

An email library for .NET with no dependency beyond the .NET base class library and its sibling libraries: MIME/EML, MBOX, TNEF, MSG, PST/OST and more, lossless and lenient, NativeAOT and trimming clean.

**1.0.** Reading: MIME/EML, MHTML and reports, mbox variants, Maildir, EMLX, ICS/vCard, MSG/OFT, PST/OST, TNEF, compressed RTF, OLM and DBX. Writing: MIME, the mailbox stores, ICS/vCard, MSG/OFT and Unicode PST: new files with the builder, or existing files in place (in-place writing is preview quality; keep a copy of the file before you write to it). Recovery of damaged PST/OST, DBX and OLM, MAPI↔MIME and store-to-store conversion with loss reports, display HTML, and `Mail.Parse`/`Mail.Open` for the one-line path. SMTP, IMAP and POP3 are in `RecursiveAI.OneMail.Client`, PDF and PNG in `RecursiveAI.OneMail.Render`. S/MIME, OpenPGP, DKIM and ARC (`RecursiveAI.OneMail.Cryptography`) follow at the first 1.x after OneCrypto 1.0, with previews on the way.

Known gaps (all in <https://github.com/recursive-ai-oss/onemail-csharp/blob/main/docs/compatibility.md>): Outlook.com provider rows not yet run; Outlook's signature-validity verdict can't be read unattended; Outlook's repair tool miscounts tables over 65,535 rows; protocol rows are x64 only; `LastVerbExecuted` after a real send is unchecked. What is checked: the same page.

**Display HTML was reviewed by an engineer who did not write it.** Script execution and network fetch under the Block policy were checked in Chrome and Firefox; the threat model and what the output assumes (one message per frame or document) are in [docs/display-html.md](https://github.com/recursive-ai-oss/onemail-csharp/blob/main/docs/display-html.md#review).

```csharp
using RecursiveAI.OneMail;

using MailContainer mbox = Mail.Open("archive.mbox");
foreach (IMailItem item in mbox.Enumerate())
{
    Console.WriteLine($"{item.Sent?.Value:u} {item.Subject}");
}
```

Project home and source: <https://github.com/recursive-ai-oss/onemail-csharp>. Changes: <https://github.com/recursive-ai-oss/onemail-csharp/blob/main/CHANGELOG.md>. Licence: MIT OR Apache-2.0.

This project is unaffiliated with other products named OneMail.
