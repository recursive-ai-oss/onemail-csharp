# OneMail.Client

The raw protocol layer under the IMAP, SMTP and POP3 clients of [OneMail](https://github.com/recursive-ai-oss/onemail-csharp): connections with implicit TLS, STARTTLS and `COMPRESS=DEFLATE`, command writers, response readers that turn every line into a typed tree, SASL, and a protocol trace with credentials redacted. It is for tools that need to drive a server by hand, including extensions no typed client knows.

**Early preview.** Native platforms only (not browser WebAssembly). The API is not stable yet. The typed `ImapClient`, `Pop3Client` and `SmtpClient`, `Mail.SendAsync` and the SASL mechanisms (SCRAM, OAuth, NTLM, GSSAPI and more) are on top of it.

```csharp
using RecursiveAI.OneMail.Client.Imap.Protocol;
using RecursiveAI.OneMail.Client.Protocol;
using RecursiveAI.OneMail.Client.Sasl;
using RecursiveAI.OneMail.Mime.Reading;

await using ImapRawConnection imap = await ImapRawConnection.ConnectAsync("imap.example.com", 993,
    new ProtocolConnectionOptions { ImplicitTls = true });
await imap.ReadGreetingAsync();
await imap.AuthenticateAsync(new SaslPlain("user", "password"));
await imap.ExecuteAsync(ImapCommand.Create("SELECT").Mailbox("INBOX"));

// A message body goes straight from the socket into the MIME reader, without being buffered.
await imap.ExecuteAsync(ImapCommand.Create("FETCH").SequenceSet("1").Raw("(BODY.PEEK[])"), async (context, literal, ct) =>
{
    using var mime = new MimeReader(literal);
    while (await mime.ReadAsync(ct)) { /* headers, bodies, parts */ }
    return true;
});
```

```csharp
// Connect (STARTTLS required), authenticate with the strongest mechanism offered, send, QUIT.
SmtpSendResult r = await Mail.SendAsync(
    new OutgoingMessage { From = "me@example.com", To = ["you@example.org"], Subject = "Hi", Text = "Hello" },
    new SmtpClientOptions { Host = "smtp.example.com", Credentials = new SaslCredentials { UserName = "me", Password = "…" } });
foreach (var rejected in r.Rejected) Console.WriteLine($"{rejected.Address}: {rejected.Reply}");
```

- Credentials are never sent over an unencrypted connection unless you say so (`AllowInsecureAuthentication`); the trace shows asterisks in their place.
- Limits are on by default: line length, response size, literal size, list nesting and reply lines. A parsed response carries typed defects for what it had to repair; only a closed connection or a limit throws.
- Every operation has an asynchronous form with a `CancellationToken` and a synchronous twin.

```csharp
await using var client = await ImapClient.ConnectAsync("imap.example.com", 993,
    new ImapClientOptions { Connection = new ProtocolConnectionOptions { ImplicitTls = true } });
await client.LoginAsync("user", "password");
await client.SelectAsync("INBOX");
await foreach (ImapMessage m in client.FetchAsync(ImapSet.From(1), new ImapFetchQuery { Uid = true, Envelope = true }))
    Console.WriteLine(m.Envelope!.Subject);
await foreach (ImapEvent e in client.IdleAsync(stop: token))   // appends, expunges and flag changes
    Console.WriteLine(e.Kind);
```

Project home and source: <https://github.com/recursive-ai-oss/onemail-csharp>. Details: <https://github.com/recursive-ai-oss/onemail-csharp/blob/main/docs/client-protocol.md>. Licence: MIT OR Apache-2.0.

This project is unaffiliated with other products named OneMail.
