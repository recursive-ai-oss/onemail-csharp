# SMTP submission (`RecursiveAI.OneMail.Client.Smtp`)

`SmtpClient` sends mail through a submission server (RFC 5321, RFC 6409). It is built on `SmtpRawConnection` (see `client-protocol.md`): everything it does can be done by hand through `SmtpClient.Raw`. Native platforms only. Every operation is asynchronous with a synchronous twin and takes a `CancellationToken`.

```csharp
// Connect, send, QUIT.
SmtpSendResult r = await Mail.SendAsync(new OutgoingMessage { From = "me@example.com", To = ["you@example.org"], Subject = "Hi", Text = "Hello" },
    new SmtpClientOptions { Host = "smtp.example.com", Credentials = new SaslCredentials { UserName = "me", Password = "…" } });

// Several messages over one session.
await using SmtpClient smtp = await SmtpClient.ConnectAsync(options);
foreach (MimeEntity m in messages) (await smtp.SendAsync(SmtpMessage.FromEntity(m))).ThrowIfNotDelivered();
```

## Connecting

`SmtpClientOptions.Security` decides how the connection is protected:

| Value | Behaviour | Default port |
|---|---|---|
| `StartTls` (default) | STARTTLS (RFC 3207) is required. If the server does not announce it, `SmtpFeatureRequiredException("STARTTLS")` is thrown and no credentials are sent. | 587 |
| `ImplicitTls` | TLS from the first byte (RFC 8314). | 465 |
| `StartTlsIfOffered` | STARTTLS when announced, plain text otherwise. An attacker who removes the announcement wins; use on trusted networks only. | 587 |
| `None` | No encryption. PLAIN and LOGIN are not used unless `SaslCredentials.AllowInsecure` is set. | 25 |

After the greeting (`220`), the client says `EHLO` with `ClientDomain` (default `localhost`; set the machine's public name for servers that check it). A server that answers `EHLO` with a 5xx is spoken to with `HELO` and is taken to have no extensions. After STARTTLS and after authentication the capabilities are asked for again, because what a server announces before TLS or login does not count (RFC 3207 §4.2, RFC 4954).

Certificate validation, SNI, timeouts, limits and the trace come from `SmtpClientOptions.Connection` (`ProtocolConnectionOptions`); its `ImplicitTls` is ignored.

## Authentication

With `Credentials` set, the client picks mechanisms with `SaslMechanisms.Select` (see `sasl.md`) and tries them strongest first. A `504` (mechanism not implemented) or `534` (too weak) moves on to the next; any other refusal stops with `SmtpAuthenticationException` carrying the server's reply. If nothing usable is offered the exception says what the server offered and why each was skipped. `MechanismSelector` replaces the choice.

## Sending

`SmtpMessage` is built from a MIME tree (`FromEntity`), bytes (`FromBytes`) or a stream (`FromStream`, never held whole; the sender and recipients must be given).

**Envelope.** The reverse-path is `SmtpMessage.Sender`, else the `Sender` header, else the first `From` address. An empty `Sender` is the null reverse-path `<>`. The forward-paths are `Recipients`, else every address in `To`, `Cc` and `Bcc` (groups flattened, duplicates removed, ignoring case). A `Bcc` header is used for the envelope and is **not transmitted**: the tree it sits in has it taken out for the duration of the send and put back at the same place afterwards. For bytes, remove `Bcc` yourself; the bytes are sent as they are.

**Result.** `SmtpSendResult` carries the reply to `MAIL FROM` (`SenderReply`), one `SmtpRecipientResult` per recipient with the server's reply and its enhanced status code (RFC 3463), and the reply to the end of the data (`DataReply`, which is where servers put the queue id). A rejected sender or recipient is data in the result, never an exception. `Delivered` means the server took the message (a 2xx final reply) for at least one recipient; `Complete` means it also accepted all of them. With no accepted recipient nothing is sent and `DataReply` is null. `RequireAllRecipients` turns a single rejection into "send nothing" (RSET). `ThrowIfNotDelivered()` is for callers that prefer exceptions.

**Data.** If the server announces CHUNKING (RFC 3030) and `UseChunking` is true, the message goes as BDAT chunks of `ChunkSize` (default 256 KiB), each acknowledged, the last marked LAST. Otherwise it goes as DATA with dot-stuffing (RFC 5321 §4.5.2). Either way the message is streamed from its source. Bare LF and bare CR are turned into CRLF on the way (`NormalizeLineEndings`, default true; a message that already uses CRLF is untouched, byte for byte). A refused BDAT chunk stops the rest, and the next send starts with RSET.

**Pipelining.** With PIPELINING (RFC 2920) and `UsePipelining`, `MAIL FROM` and all `RCPT TO` go in one write, and `DATA` is added to the group when `RequireAllRecipients` is false and BDAT is not used. Replies are matched to commands in order.

**Parameters** (each only when the server announced the extension; the ones the caller asked for and the server lacks are refused before anything is sent):

| Need | Parameter | If the server lacks it |
|---|---|---|
| SIZE (RFC 1870) | `SIZE=n` with the message length in bytes, measured by one counting pass over a tree | `SmtpMessageTooLargeException` when the announced limit is smaller than the message; no limit announced means no check |
| 8BITMIME (RFC 6152) | `BODY=8BITMIME` for 8-bit bodies and for streams | downgrade, below |
| BINARYMIME (RFC 3030) | `BODY=BINARYMIME` for `Content-Transfer-Encoding: binary` parts, sent with BDAT | the binary parts are re-encoded as base64 |
| SMTPUTF8 (RFC 6531) | `SMTPUTF8` when an envelope address or a header field holds non-ASCII text | non-ASCII *address*: `SmtpFeatureRequiredException("SMTPUTF8")`. Non-ASCII *headers* with 8BITMIME: sent as 8-bit; without 8BITMIME: `SmtpDowngradeRefusedException` |
| DSN (RFC 3461) | `RET`, `ENVID` on `MAIL`, `NOTIFY` on `RCPT` (xtext-encoded) | `SmtpFeatureRequiredException("DSN")` |
| REQUIRETLS (RFC 8689) | `REQUIRETLS` | `SmtpFeatureRequiredException("REQUIRETLS")`, also when this hop is not encrypted |

**Downgrade to 7 bit.** A message with 8-bit content for a server without 8BITMIME is re-encoded on a private copy: text parts become quoted-printable, other parts base64 (RFC 6152 §2; RFC 2046 §5.2.1 forbids encoding `message/rfc822` itself, so a `message/rfc822` part declared 8bit or binary cannot be downgraded). The caller's tree is not changed. `SmtpSendResult.Downgraded` says it happened, and `SmtpMessage.AllowDowngrade = false` refuses instead. **A signed message is never re-encoded**: S/MIME and PGP structure (`multipart/signed`, `application/pkcs7-*`, `application/pgp-*`) anywhere in the message, or a `DKIM-Signature`, `ARC-*` or `DomainKey-Signature` header, makes the send fail with `SmtpDowngradeRefusedException` before a byte goes out. For bytes the check for those structures is deliberately broad (the text appearing anywhere counts).

**Sessions.** After a successful transaction the next `SendAsync` begins directly. After a failed one (`BDAT` refused, `DATA` refused) the client sends `RSET` first. A `421` reply, a timeout, a cancellation or an I/O error makes `IsUsable` false: dispose and reconnect. Exceptions that refuse a message before the transaction starts (the `Smtp*Exception` types above) leave the session usable.

## What the client does not do

- It does not add `Date` or `Message-ID`: `OutgoingMessage` does (and servers usually add missing ones). It does not sign (DKIM and S/MIME belong to `.Cryptography`).
- It does not queue or retry: a `4xx` is reported in the result and the caller decides.
- It does not look up MX records: it submits to the server it is told to.

## Tests

`tests/OneMail.Tests/Client/SmtpClientTests.cs` drives the client against a scripted loopback server (parameters, pipelining, BDAT chunk sizes, rejections, SIZE, downgrade, Bcc, reuse). `SmtpClientServerTests.cs` runs against Dovecot's submission service, Stalwart and Postfix (in a guest) started by `tools/servers/servers.sh`; what each server received is read back (`smtp-sink.py` keeps what Dovecot and Postfix relay; Stalwart is read through IMAP) and compared with what was sent: the delivered message must end with the exact bytes sent, with only the server's own trace headers in front.
