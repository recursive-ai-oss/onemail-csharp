# OneMail

An email library for .NET with no dependency beyond the .NET base class library and its sibling libraries: MIME/EML, MBOX, TNEF, MSG, PST/OST and more, lossless and lenient, NativeAOT and trimming clean.

**Early preview.** Working today: MIME/EML reading and writing (byte-identical round-trips), MHTML and delivery/disposition/feedback reports, mbox variants, Maildir and EMLX, ICS/vCard trees, and `Mail.Parse`/`Mail.Open` for the one-line path. Outlook items (MSG, PST), protocols and cryptography come in later previews. The API is not stable yet. What is checked, and the known gaps: <https://github.com/recursive-ai-oss/onemail-csharp/blob/main/docs/compatibility.md>.

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
