using System;
using System.IO;
using RecursiveAI.OneMail;

// The reading tasks of the happy path, each one or two lines. The rest of this file only creates something to read.
string dir = Path.Combine(Path.GetTempPath(), "onemail-sample-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
try
{
    string mbox = Path.Combine(dir, "inbox");
    File.WriteAllText(mbox,
        "From a@example.com Mon Jan  1 00:00:00 2024\nSubject: First\nFrom: Ann <a@example.com>\nDate: Mon, 1 Jan 2024 00:00:00 +0000\n\nhello\n\n" +
        "From b@example.com Tue Jan  2 00:00:00 2024\nSubject: Second\nFrom: b@example.com\nContent-Type: multipart/mixed; boundary=x\n\n" +
        "--x\nContent-Type: text/plain\n\nsee attached\n--x\nContent-Type: application/pdf; name=report.pdf\nContent-Transfer-Encoding: base64\n\nAAEC\n--x--\n");
    string eml = Path.Combine(dir, "one.eml");
    File.WriteAllText(eml, "Subject: Third\nContent-Type: multipart/mixed; boundary=y\n\n--y\n\nbody\n--y\nContent-Type: image/png; name=logo.png\n\nx\n--y--\n");

    // 1. Open a file (an mbox, a Maildir, a single message: the content decides) and print the subjects.
    await using MailContainer inbox = Mail.Open(mbox);
    await foreach (IMailItem message in inbox) Console.WriteLine(message.Subject);

    // 2. Parse a message and list its attachments.
    foreach (IMailAttachment a in Mail.Parse(File.ReadAllBytes(eml)).Attachments) Console.WriteLine($"{a.FileName} ({a.ContentType}, {a.Size} bytes)");

    // 3. Iterate an mbox: who each message is from, when it was written, how many files it carries.
    foreach (IMailItem m in inbox.Enumerate()) Console.WriteLine($"{(m.From.Count > 0 ? m.From[0].ToString() : "-")} {m.Sent?.Value:u} {m.Attachments.Count}");

    // 5. Render a message to a PDF (the RecursiveAI.OneMail.Render package); ToPngAsync writes PNG pages the same way.
    await using (FileStream pdf = File.Create(Path.Combine(dir, "one.pdf"))) await Mail.Parse(File.ReadAllBytes(eml)).ToPdfAsync(pdf);

    // 4. Reply to a message: the subject, the threading headers, the recipients and the quoted text are made for you.
    Console.WriteLine(Mail.Parse(File.ReadAllBytes(eml)).CreateReply().Subject);
}
finally
{
    Directory.Delete(dir, recursive: true);
}
