using System;
using System.IO;
using System.Text;
using RecursiveAI.OneMail;

// Task 1, parse a message: `ParseMessage [file.eml]`. Without an argument it parses a small message made here.
byte[] bytes = args.Length > 0
    ? File.ReadAllBytes(args[0])
    : Encoding.UTF8.GetBytes("From: Ann <ann@example.com>\r\nSubject: Quarterly report\r\nDate: Mon, 1 Jan 2024 09:30:00 +0100\r\nContent-Type: multipart/mixed; boundary=b\r\n\r\n" +
        "--b\r\nContent-Type: text/plain\r\n\r\nNumbers attached.\r\n--b\r\nContent-Type: application/pdf; name=report.pdf\r\nContent-Transfer-Encoding: base64\r\n\r\nAAEC\r\n--b--\r\n");

// 1. Parse it. Nothing throws on bad input: repairs are listed in message.Defects.
IMailItem message = Mail.Parse(bytes);

// 2. Subject, sender, date (with where it came from) and the body to show.
Console.WriteLine($"{message.Subject} from {message.From[0]} on {message.Sent?.Value:u} ({message.Sent?.Source})\n{message.BestBody?.ReadText()}");

// 3. Attachments are items too; Open() streams their bytes.
foreach (IMailAttachment a in message.Attachments) Console.WriteLine($"{a.FileName} ({a.ContentType}, {a.Size} bytes)");
