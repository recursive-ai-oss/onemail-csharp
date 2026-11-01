using System;
using RecursiveAI.OneMail;
using RecursiveAI.OneMail.Client.Sasl;
using RecursiveAI.OneMail.Client.Smtp;
using RecursiveAI.OneMail.Composition;

// The sending task of the happy path: usage is `Sending <host> <user> <password>`; the message goes to the sender itself.
string host = args.Length > 0 ? args[0] : "smtp.example.com";
string user = args.Length > 1 ? args[1] : "me@example.com";
string password = args.Length > 2 ? args[2] : "secret";

// 1. Send a message: connects with STARTTLS, authenticates with the strongest mechanism the server offers, sends, says QUIT.
SmtpSendResult result = await Mail.SendAsync(
    new OutgoingMessage { From = user, To = [user], Subject = "Hello", Text = "Sent with OneMail." },
    new SmtpClientOptions { Host = host, Credentials = new SaslCredentials { UserName = user, Password = password } });

// 2. Rejections are in the result: which recipients the server took, and what it said about the others.
foreach (SmtpRecipientResult r in result.Rejected) Console.WriteLine($"{r.Address}: {r.Reply}");
Console.WriteLine(result.Delivered ? "queued: " + result.DataReply : "not accepted");
