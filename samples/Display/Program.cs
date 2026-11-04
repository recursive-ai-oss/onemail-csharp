using System;
using System.IO;
using System.Text;
using RecursiveAI.OneMail;
using RecursiveAI.OneMail.Display;

// Task 6, display HTML and PDF: `Display [file.eml]` writes display.html and display.pdf into the current folder.
// Without an argument it uses a small HTML message made here (with a script and a tracking pixel, to show the sanitiser).
byte[] bytes = args.Length > 0
    ? File.ReadAllBytes(args[0])
    : Encoding.UTF8.GetBytes("From: Ann <ann@example.com>\r\nSubject: Offer\r\nContent-Type: text/html\r\n\r\n<p>Hello <b>there</b></p><script>alert(1)</script><img src=\"https://t.example.com/p.gif\">\r\n");
IMailItem message = Mail.Parse(bytes);

// 1. Display HTML: sanitised, remote content blocked by default, attachments resolved. The result says what it removed.
DisplayResult html = message.ToDisplayHtml();
File.WriteAllText("display.html", html.Html);
Console.WriteLine($"{html.RemoteReferences.Count} remote references blocked, {html.TrackingIndications.Count} tracking indications");

// 2. PDF through OneRender (the RecursiveAI.OneMail.Render package); ToPngAsync writes PNG pages the same way.
await using FileStream pdf = File.Create("display.pdf");
await message.ToPdfAsync(pdf);
