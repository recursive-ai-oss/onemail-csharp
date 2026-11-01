# The raw protocol layer (`RecursiveAI.OneMail.Client`)

The layer under the SMTP, IMAP and POP3 clients. It speaks the protocols' wire formats and nothing else: no mailbox model, no state machine beyond what the wire needs (cached capabilities, tags, `UTF8=ACCEPT`). Everything an extension adds can be driven through it by hand. Native platforms only; the browser build does not include it.

Every I/O method has an asynchronous form taking a `CancellationToken` and a synchronous twin. Cancelling or timing out leaves the stream in the middle of a response: dispose the connection.

## Layers

| Namespace | Types |
|---|---|
| `Client.Protocol` | `ProtocolConnection` (socket, TLS, compression), `ProtocolReader`, `ProtocolWriter`, `ProtocolLimits`, `ProtocolDefect`, `IProtocolTrace`, the exceptions |
| `Client.Sasl` | `SaslMechanism`, `SaslPlain` |
| `Client.Imap.Protocol` | `ImapRawConnection`, `ImapCommand`, `ImapResponseReader`, `ImapResponse`, `ImapValue`, the typed views |
| `Client.Smtp.Protocol` | `SmtpRawConnection`, `SmtpReplyReader`, `SmtpReply`, `SmtpCapabilities`, `SmtpDataStream` |
| `Client.Pop3.Protocol` | `Pop3RawConnection`, `Pop3Response`, `Pop3Capabilities`, `Pop3DotStream` |

Each layer uses only the one below. The readers take a `ProtocolReader`, so they run over any stream (the tests and fuzz targets feed them bytes from memory).

## Transport

- `ProtocolConnection.ConnectAsync(host, port, options)` opens a socket (`NoDelay`), then runs the TLS handshake at once when `ImplicitTls` is set. `FromStream` wraps any stream.
- **STARTTLS and STLS** are layered after the protocol's own acknowledgement: send the command, read the OK, then `StartTlsAsync`. The switch is refused (`MailProtocolViolationException`) when the reader already holds bytes the server sent after the acknowledgement, because an attacker in the path can append plaintext to the same TCP segment as the OK and have it read as part of the encrypted session. After the upgrade the cached capabilities are forgotten.
- **TLS options**: SNI and the certificate name come from `TlsTargetHost` (default the host connected to). `ServerCertificateValidationCallback` replaces validation; by default the platform's chain validation applies and a self-signed certificate is refused. `ClientCertificates`, `ApplicationProtocols` (ALPN), `EnabledSslProtocols` and a `ConfigureTls` hook reach `SslClientAuthenticationOptions`.
- **Timeouts**: `ConnectTimeout` (30 s), `ReadTimeout` and `WriteTimeout` (2 min each; settable on the open connection, IDLE needs a long read timeout). A timeout surfaces as `TimeoutException`; cancellation as `OperationCanceledException`.
- **COMPRESS=DEFLATE** (RFC 4978): `EnableCompressionAsync` wraps the stream in raw DEFLATE both ways with a sync flush per flush, again only when the reader is empty. Compressed input from the server is bounded by the same response limits as plain input.
- **Buffers** are rented from `ArrayPool` and returned on dispose. Lines returned by the reader point into the buffer and are valid until the next call.

## Limits and defects

`ProtocolLimits` is on by default: line length 1 MiB, response size 64 MiB, buffered literal 16 MiB, accepted literal 4 GiB, list nesting 64, reply lines 1,000, items per response 1,000,000. A limit that is hit throws `MailProtocolLimitException`, because the stream cannot be resynchronised after it. A closed connection throws `MailConnectionClosedException`.

Malformed content never throws. A response carries `Defects` (`ProtocolDefectKind`, an offset and a message) and as much of the tree as could be read:

| Defect | Meaning |
|---|---|
| `Malformed` | the line does not follow the grammar |
| `UnterminatedList`, `UnterminatedQuoted` | a list or quoted string ran to the end of the line |
| `BadLiteralSize` | reserved for a literal announcement with a bad size |
| `LiteralSkipped` | a literal above the buffering limit that nobody streamed was discarded (the value is marked skipped) |
| `NestingTooDeep` | lists nest beyond the limit; the rest of the line is kept as one raw atom |
| `ReplyCodeChanged`, `ReplyLineWithoutCode` | SMTP reply lines that disagree or lack a code |
| `UnknownStatus` | a POP3 line that is neither `+OK`, `-ERR` nor `+` |
| `BareLineFeed` | a line ended in LF without CR (accepted) |
| `UnexpectedShape` | a FETCH item that does not have the shape the grammar requires; the raw tree is intact |

## Credentials

`WriteSecret` / `WriteSecretAscii` mark bytes the trace must not show: the trace sees asterisks of the same length. `LOGIN`, `AUTHENTICATE` responses, `AUTH`, `PASS` and every SASL response use it. The raw connections refuse to send a password or a PLAIN response over an unencrypted connection (`InvalidOperationException` before anything is written) unless `AllowInsecureAuthentication` is set. `SaslMechanism.RequiresEncryption` says which mechanisms need that.

## SASL framework

`SaslMechanism` is the client side of a challenge/response exchange (RFC 4422): `ClientFirst`, `GetInitialResponse`, `Respond`. The raw connections carry it over IMAP `AUTHENTICATE` (with the initial response on the command when the server offers `SASL-IR`, RFC 4959), SMTP `AUTH` (RFC 4954) and POP3 `AUTH` (RFC 5034): base64 on the wire, `=` for an empty initial response, `*` to cancel when a challenge is not valid base64. `SaslPlain` (RFC 4616) is the first mechanism; the others belong to the SASL package of the family.

## IMAP

**Commands.** `ImapCommand` is a list of typed parts: `Atom`, `Number`, `SequenceSet`, `Astring`, `Quoted`, `Mailbox`, `Flags`, `Open`/`Close`, `Literal` (bytes or a stream with its length) and `Raw`. Spaces are placed automatically. String-like parts are validated and quoted or sent as literals as their content requires, so a value cannot break out of its position; `Raw` is the escape hatch for syntax the parts do not cover and is the caller's responsibility. `Mailbox` converts to modified UTF-7 (RFC 3501 §5.1.3, `ImapUtf7` in the core) unless `UTF8=ACCEPT` was enabled on the connection, then sends UTF-8.

**Literals in commands.** `{n}` waits for the server's continuation before the bytes follow; if the server answers the tag with NO or BAD instead (`[TOOBIG]`), `Send` returns false and that completion is the next response read. `{n+}` (LITERAL+) is used when the server advertises `LITERAL+`, or `LITERAL-` and the literal is at most 4096 bytes (RFC 7888); `ImapLiteralMode` and `ImapRawConnection.LiteralMode` override this.

**Responses.** `ImapResponseReader` reads one logical response at a time: the first line, plus any literals announced at the end of a line and the lines that continue after them. `ImapResponse` has `Kind` (untagged, tagged, continuation), `Tag`, `Status`, `Number` (`* 3 EXISTS`), `Name` (the keyword, upper case), `Code` (a bracketed response code with parsed arguments), `Text`, `Data` (the generic tree: `ImapValue` of kind nil, atom, quoted, literal or list), `Defects` and `FirstLine`. Atoms keep their section specifier whole (`BODY[HEADER.FIELDS (FROM TO)]<0>`). Quoted strings are unescaped; bytes are kept raw (`Bytes`), decoded as UTF-8 on demand (`Text`).

Typed views over the tree, each returning false when the response is not of that kind: `TryGetCapabilities` (untagged or in a code), `TryGetFetch` (UID, FLAGS, INTERNALDATE, RFC822.SIZE, MODSEQ, EMAILID, THREADID, ENVELOPE, BODYSTRUCTURE with section numbers, section items; every other item stays in `Other` with its value), `TryGetList` (LIST and LSUB with decoded mailbox name and extended data), `TryGetStatus`, `TryGetSearch`, `TryGetEsearch`, `TryGetNamespace`, `TryGetVanished`. `ImapSequenceSet.Expand` expands sequence sets with a cap. Anything else (`X-GM-LABELS`, `METADATA`, `QUOTA`, ...) is read from `Data`.

**Literals as streams.** A literal in a `FETCH` response can be handed to a handler as a `Stream` over the connection, before the rest of the response is read: `ExecuteAsync(command, (context, literal, ct) => ...)`. The handler returns true when it is done (what it left unread is discarded so the next line starts in the right place) or false, without reading, to have the literal buffered instead. A 100 MB `BODY[]` goes through `MimeReader` with a working set of the reader's buffers. Literals elsewhere (mailbox names, annotations) are buffered up to the limit.

**Rules the reader follows**
- Only untagged data can carry literals: `{n}` at the end of a status response's text (`* OK ... {5}`) is text, not a literal, so a server cannot make the client swallow the next lines by ending a message that way.
- A line ends at LF; a CR before it is part of the terminator.
- `~{n}` (literal8, RFC 3516) is read as a literal; the command side does not send it yet.

**Session helpers** on `ImapRawConnection`: `ReadGreeting`, `Execute` (collects untagged responses, or hands each to a callback for constant memory), `Send`/`ReadResponse` for pipelining, `StartTls`, `Compress`, `Login`, `Authenticate`, `IdleStart`/`IdleDone`. Capabilities are updated from every CAPABILITY response and `[CAPABILITY ...]` code, and cleared after STARTTLS and authentication unless the OK carried a fresh list (RFC 9051 §6.2).

## SMTP

`SmtpReplyReader` reads replies (`ddd-text` lines up to `ddd text`, or a bare `ddd`), with the enhanced status code of the first line (RFC 3463 syntax, class 2/4/5) and the reply-line limit. `SmtpCapabilities` parses the EHLO reply into keywords and parameters (`AUTH=` and `AUTH ` are merged, `SIZE` as a number).

`SmtpRawConnection`: `Command`/`Send`/`ReadReply` for hand-driven exchanges; `Pipeline` writes several command lines in one flush and reads one reply each (RFC 2920; the caller keeps to the groups the RFC allows); `Ehlo`, `StartTls`, `Authenticate`; `MailFromArguments` and `RcptToArguments` build the parameters (`SIZE`, `BODY`, `SMTPUTF8`, `REQUIRETLS`, DSN `RET`/`ENVID`/`NOTIFY`/`ORCPT` with xtext, `AUTH`, extras) and refuse addresses that would break the command; `BeginData` returns a stream that dot-stuffs (RFC 5321 §4.5.2), adds the missing CRLF and sends `.` CRLF on `Complete`; `SendBdat` writes a `BDAT n [LAST]` chunk without waiting, so chunks can be pipelined (RFC 3030) and the replies read afterwards. The data stream writes the bytes as given; `normalizeLineEndings` turns bare LF or CR into CRLF for streams that need it, at the price of changing the bytes.

## POP3

`Pop3Response` parses `+OK` / `-ERR` (case-insensitive) with an optional bracketed response code (RFC 3206) and `+` continuations. `Pop3RawConnection` sends commands, returns multi-line bodies as `Pop3DotStream` (byte-unstuffed, line ends kept as sent, ends at the lone-period line; read it or `DrainAsync` it before the next command, which throws otherwise), and has `Capa`, `Stat`, `List` (LIST/UIDL), `StartTls`, `Login` (USER/PASS) and `Authenticate`. A body goes straight into `MimeReader`.

## Container test harness

`tools/servers/` runs the published server images without root or a container daemon:

- `setup-podman.sh` unpacks the static podman bundle under the user's home; `pull-images.sh` pulls the images; `images.env` pins them by digest; `setup-stalwart-cli.sh` fetches the vendor's CLI used to provision Stalwart.
- The host has subordinate id ranges but no `newuidmap`, which rootless podman needs for a multi-user mapping. `userns.sh` runs a command in a user namespace with empty subid tables so podman uses its documented one-id mapping; each container maps just the one user its service runs as (`--uidmap`). With `-n` the command also gets a private network namespace (loopback only, low ports allowed), so servers bind their standard ports and nothing clashes or leaves the machine.
- `servers.sh --servers dovecot,stalwart,cyrus -- <command>` starts the servers inside that namespace, provisions accounts, runs the command with `ONEMAIL_SERVERS` and `ONEMAIL_<SERVER>_<PROTOCOL>_PORT` set, and stops everything. `run-container-tests.sh` builds the tests and does this for `ServerTests`.
- Servers and accounts: Dovecot (user `test`, a shared password; IMAP 31143/31993, POP3 31110/31995, submission 31587/31465, `smtp-sink.py` as its relay), Stalwart (`test@example.test`; IMAP 143/993, POP3 110/995, submission 587/465), Cyrus (`user1`, any password; IMAP 143/993, POP3 110/995). TLS certificates are the images' own or self-signed; the tests accept any certificate.
- Postfix needs the superuser and several system users, which one mapped id cannot give it, so it runs in a small Linux guest instead: `guest-build.sh` builds a Debian 12 cloud image (pinned by URL and SHA-512) with Postfix and Dovecot's authentication service through cloud-init, once, with network access; `guest-run.sh` boots it on a throw-away overlay under user-mode qemu with KVM and forwards SMTP, submission and smtps to 127.0.0.1:32025, 32587 and 32465 (user `test`, the shared password, recipient `test@example.test`). No root is needed on the host.

The container tests (`ServerTests`) skip when `ONEMAIL_SERVERS` is unset. `ONEMAIL_CAPTURE_DIR` keeps the server's side of each session, which is where `testdata/client/captures` comes from.

## Fuzzing

Targets `imap-response`, `smtp-reply`, `pop3-response` and `pop3-listings` (`fuzz/`) feed hostile server output to the readers and the typed views with reduced limits; only `MailProtocolException` may come out. Seeds are in `testdata/client/fuzz-seeds`.
