using System;
using System.IO;
using System.Text;
using RecursiveAI.OneMail;
using RecursiveAI.OneMail.Client.Sasl;
using RecursiveAI.OneMail.Client.Smtp;
using RecursiveAI.OneMail.Composition;

// Task 7, reply (or forward) and send: `ReplyAndSend <host> <user> <password> [file.eml]`. Replies to the message in the file (or to a small one made
// here) as <user>. Needs an SMTP server; it only builds without one.
if (args.Length < 3) { Console.WriteLine("usage: ReplyAndSend <host> <user> <password> [file.eml]"); return; }
IMailItem original = Mail.Parse(args.Length > 3
    ? File.ReadAllBytes(args[3])
    : Encoding.UTF8.GetBytes("From: Ann <ann@example.com>\r\nTo: " + args[1] + "\r\nSubject: Lunch?\r\nMessage-ID: <1@example.com>\r\n\r\nAre you free at noon?\r\n"));
var server = new SmtpClientOptions { Host = args[0], Credentials = new SaslCredentials { UserName = args[1], Password = args[2] } };

// 1. The reply: "Re:" subject, recipients, In-Reply-To/References and the quoted text are made for you. CreateForward works the same way.
ComposedMessage reply = original.CreateReply(new CompositionOptions { From = new MailAddress(null, args[1]), Text = "Yes, see you then." });

// 2. Send it.
SmtpSendResult result = await Mail.SendAsync(reply.Message, server);
Console.WriteLine(result.Delivered ? $"sent '{reply.Subject}' to {string.Join(", ", reply.To)}" : "not accepted");
