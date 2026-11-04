using System;
using System.Threading;
using RecursiveAI.OneMail;
using RecursiveAI.OneMail.Client.Imap;
using RecursiveAI.OneMail.Client.Protocol;

// Task 5, IMAP in the ImapFlow style: `Imap <host> <user> <password> [port]` (implicit TLS, port 993 by default). Reads the last five messages of
// INBOX, then waits for new mail until you press Ctrl+C. Needs a server; it only builds without one.
if (args.Length < 3) { Console.WriteLine("usage: Imap <host> <user> <password> [port]"); return; }
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };

// 1. Connect, log in, select.
await using ImapClient client = await ImapClient.ConnectAsync(args[0], args.Length > 3 ? int.Parse(args[3]) : 993, new ImapClientOptions { Connection = new ProtocolConnectionOptions { ImplicitTls = true } });
await client.LoginAsync(args[1], args[2]);
ImapMailbox inbox = await client.SelectAsync("INBOX");

// 2. Fetch the newest messages (by UID) straight into the model: the raw bytes parse like any other message.
await foreach (ImapMessage m in client.FetchAsync(ImapSet.From(Math.Max(1, inbox.UidNext - 5)), new ImapFetchQuery { Uid = true, Source = true }, uid: true))
    Console.WriteLine($"{m.Uid}: {Mail.Parse(m.Source).Subject}");

// 3. IDLE: events arrive as the server sends them; on new mail, fetch what is new.
await foreach (ImapEvent e in client.IdleAsync(stop: stop.Token)) Console.WriteLine($"{e.Kind}: now {client.Mailbox!.Exists} messages");
