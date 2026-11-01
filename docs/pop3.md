# The POP3 client (`RecursiveAI.OneMail.Client.Pop3.Pop3Client`)

A POP3 client (RFC 1939, 2449 capabilities, 2595 STLS, 5034 AUTH, 3206 response codes) on `Pop3RawConnection` (`client-protocol.md`). Native platforms only.

```csharp
await using var pop = await Pop3Client.ConnectAsync("pop.example.com", 995,
    new Pop3ClientOptions { Connection = new ProtocolConnectionOptions { ImplicitTls = true } });
await pop.LoginAsync(user, password);

foreach (var m in await pop.UidlAsync())
{
    MimeEntity message = await pop.GetMessageAsync(m.Number);   // RETR, streamed into the MIME model
    await pop.DeleteAsync(m.Number);                             // takes effect at QUIT
}
await pop.QuitAsync();
```

## Rules

- One command at a time; not thread-safe. A multi-line reply is always read to its end before the method returns.
- `-ERR` throws `Pop3CommandException` (`Response`, `ResponseCode` such as `AUTH`, `IN-USE`, `LOGIN-DELAY`, `SYS/TEMP`). The session stays usable after a failed `RETR` or `DELE`.
- `Pop3StartTlsMode.Auto` (default) sends `STLS` when `CAPA` lists it; `Required` fails when it does not. Passwords are not sent unencrypted unless the connection options allow it.
- Message numbers are valid for one session only. `UidlAsync` gives ids that survive it (servers that lack `UIDL` answer `-ERR`).
- Sync twins exist for connect, login, authenticate, `Stat`, `List`, `Uidl`, `Retrieve`, `GetMessage`, `Quit`; they block on the asynchronous implementation.
- Capabilities are read after connect, after STLS and after login (`Capabilities`, null when the server has no `CAPA`).

## Calls

| Call | Command |
|---|---|
| `LoginAsync(user, password)` | SASL `PLAIN` when `CAPA` lists it under `SASL`, else `USER`/`PASS` |
| `AuthenticateAsync(SaslMechanism)` | `AUTH` (initial response in the command, `=` for empty, `*` cancels) |
| `ApopAsync(user, password)` | `APOP`: MD5 of the greeting's timestamp and the password (RFC 1939 §7). MD5 is what the protocol defines; use it only where TLS is impossible |
| `StatAsync()`, `ListAsync()`, `UidlAsync()` | `STAT`, `LIST`, `UIDL` |
| `RetrieveAsync(n, stream)` | `RETR` to a stream, dot-unstuffed, line ends as sent; returns the byte count |
| `GetMessageAsync(n)`, `GetTopAsync(n, lines)` | `RETR`, `TOP` parsed into a `MimeEntity` (the `TOP` tree is of a truncated message) |
| `GetMessagesAsync(numbers)` | pipelined `RETR`s, yielding each parsed message in order |
| `DeleteAsync(n)`, `DeleteAsync(numbers)`, `ResetAsync()`, `NoopAsync()`, `QuitAsync()` | `DELE`, `RSET`, `NOOP`, `QUIT` |

**Pipelining** (RFC 2449): with `PIPELINING` in `CAPA`, `GetMessagesAsync` and `DeleteAsync(numbers)` keep up to `Pop3ClientOptions.PipelineWindow` (16) commands in flight, written in one flush, and read the replies in order. The window bounds what is outstanding in each direction, which avoids the deadlock of both sides blocked on a full socket buffer. Without `PIPELINING` the window is one. Deletions of a batch report the first `-ERR` after all replies are read.

## MailKit calls and ours

| MailKit | OneMail |
|---|---|
| `new Pop3Client()`; `ConnectAsync(host, port, SecureSocketOptions)` | `Pop3Client.ConnectAsync(host, port, new Pop3ClientOptions { Connection = new ProtocolConnectionOptions { ImplicitTls = true } })` |
| `AuthenticateAsync(user, password)` | `LoginAsync(user, password)` |
| `Count`, `GetMessageSizesAsync()` | `StatAsync()`, `ListAsync()` |
| `GetMessageUidsAsync()` | `UidlAsync()` |
| `GetMessageAsync(index)` (zero-based) | `GetMessageAsync(number)` (one-based, the protocol's number) |
| `GetMessageHeadersAsync(index)` | `GetTopAsync(number, 0)` |
| `GetStreamAsync(index)` | `RetrieveAsync(number, stream)` |
| `DeleteMessageAsync(index)`, `ResetAsync()` | `DeleteAsync(number)`, `ResetAsync()` |
| `DisconnectAsync(quit: true)` | `QuitAsync()` |
