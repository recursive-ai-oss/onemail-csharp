# The IMAP client (`RecursiveAI.OneMail.Client.Imap.ImapClient`)

An IMAP4rev1/rev2 client (RFC 3501, 9051) on the raw layer (`client-protocol.md`). It adds what the wire format does not have: a session with a state machine (not authenticated, authenticated, selected), a model of the selected mailbox, and typed commands. `ImapClient.Raw` stays reachable for anything it does not cover. Native platforms only.

```csharp
await using var client = await ImapClient.ConnectAsync("imap.example.com", 993,
    new ImapClientOptions { Connection = new ProtocolConnectionOptions { ImplicitTls = true } });
await client.LoginAsync(user, password);
await client.SelectAsync("INBOX");

await foreach (ImapMessage m in client.FetchAsync(ImapSet.From(client.Mailbox!.Exists - 9),
    new ImapFetchQuery { Uid = true, Envelope = true, Flags = true }))
    Console.WriteLine($"{m.Uid} {m.Envelope!.Subject}");

await foreach (ImapEvent e in client.IdleAsync(stop: token))   // appends, expunges and flag changes from anyone
    Console.WriteLine(e.Kind);
```

## Rules

- **One command at a time.** The client is not thread-safe. Starting a command while a `FetchAsync` or `IdleAsync` enumeration is open throws `InvalidOperationException`. Pipelining is available on the raw layer.
- **Failures.** A NO or BAD completion throws `ImapCommandException` (`Status`, `ResponseCode` such as `TOOBIG`, `OVERQUOTA`, `AUTHENTICATIONFAILED`, `NONEXISTENT`, the full `Completion`). A missing capability throws `NotSupportedException` naming it, before anything is sent. Malformed server output never throws: it is a defect on the response (`client-protocol.md`).
- **Cancellation.** Every I/O method takes a token. Cancelling while a command is in flight leaves the stream mid-response; dispose the client. `IdleAsync` is stopped by its `stop` token without that cost (below).
- **Sync twins.** The common operations have synchronous forms (`Connect`, `Login`, `List`, `Status`, `Select`, `Fetch`, `Search`, `Store`, `Copy`, `Move`, `Expunge`, `Append`, `Download`, `GetMessage`, `Noop`, `Logout`, ...). They block on the asynchronous implementation; do not call them from a synchronisation context that is waiting for you. `IdleAsync` has no twin.
- **Credentials** are never sent unencrypted unless the connection options allow it. `ImapStartTlsMode.Auto` (default) upgrades with STARTTLS when the server offers it; `Required` fails when it does not.
- **Numbers.** Every command that takes a set has a `uid` flag (default false, as in IMAP and ImapFlow). `ImapSet` holds the sequence-set syntax (`ImapSet.All`, `Range`, `From`, `Of(IEnumerable<long>)` folding runs into ranges).
- **Names.** Mailbox names are strings: modified UTF-7 (RFC 3501 §5.1.3) is converted both ways by the raw layer, UTF-8 under `UTF8=ACCEPT`.

## Session

| Call | Command | Notes |
|---|---|---|
| `ConnectAsync`, `FromConnectionAsync` | greeting, `CAPABILITY`, `STARTTLS`, `ID` | `PREAUTH` greetings start authenticated. BYE refuses the connection. |
| `LoginAsync(user, password)` | `AUTHENTICATE PLAIN`, else `LOGIN` | not when `LOGINDISABLED` without `AUTH=PLAIN` |
| `AuthenticateAsync(mechanism)`, `AuthenticateAsync(preferences)` | `AUTHENTICATE` | any `SaslMechanism`; the list form picks the first the server announces |
| `EnableAsync`, `IdAsync`, `NamespaceAsync`, `CompressAsync`, `NoopAsync`, `LogoutAsync` | `ENABLE`, `ID`, `NAMESPACE`, `COMPRESS DEFLATE`, `NOOP`, `LOGOUT` | `Enabled` holds what the server confirmed |
| `ImapClientOptions.Utf8`, `.Compress`, `.ClientId` | after authentication | default off |

## Mailboxes

`ListAsync(reference, pattern, ImapListOptions)` uses `LIST`, `LSUB`, or `LIST-EXTENDED` (RFC 5258) with `RETURN (SPECIAL-USE CHILDREN STATUS (...))` when the options ask for it and the server has it. `ImapMailboxInfo.SpecialUse` reads RFC 6154 attributes. `StatusAsync` takes `ImapStatusItems` (including `HIGHESTMODSEQ`, `SIZE`, `APPENDLIMIT`, `DELETED`). `CreateAsync`, `DeleteAsync`, `RenameAsync`, `SubscribeAsync`, `UnsubscribeAsync`.

## The selected mailbox

`SelectAsync(name, ImapSelectOptions)` (SELECT or EXAMINE; `CondStore`; `QResync`) fills and returns `client.Mailbox`: `Exists`, `Recent`, `UidValidity`, `UidNext`, `Flags`, `PermanentFlags`, `HighestModSeq`/`NoModSeq`, `MailboxId`, `ReadOnly`, `FirstUnseen`. A failed select leaves no mailbox selected (RFC 9051 §6.3.2). `CloseMailboxAsync(expunge)` is `UNSELECT`, or `CLOSE` with `expunge: true`.

The model is counters only, never a per-message table, so a mailbox of any size costs the same. Every untagged response of every command, and of IDLE, passes through one place that updates it: `EXISTS`, `RECENT`, `EXPUNGE` (count down), `VANISHED` (count down by the UIDs), `FLAGS`, and the response codes `UIDVALIDITY`, `UIDNEXT`, `HIGHESTMODSEQ`, `NOMODSEQ`, `PERMANENTFLAGS`, `MAILBOXID`, `UNSEEN`, `READ-ONLY`/`READ-WRITE`. Each change also raises `ImapClient.EventReceived` with an `ImapEvent` (`Exists`, `Recent`, `Expunge`, `Vanished`, `Fetch`, `Bye`) on the reading thread. Code that keeps its own per-message cache applies the events to it (an `Expunge` event renumbers everything above it).

**QRESYNC** (RFC 7162 §3.2.5): `new ImapSelectOptions { QResync = new ImapQResync(uidValidity, modSeq, knownUids) }` enables QRESYNC if needed, sends `SELECT ... (QRESYNC (...))` and returns the server's report in `Mailbox.ResyncEvents`: `Vanished` events with `Earlier = true` for messages expunged since, and `Fetch` events for messages whose flags changed. A changed `UidValidity` means the cache is void; the server then sends no resync data.

## Fetching

`FetchAsync(set, ImapFetchQuery, uid)` is an `IAsyncEnumerable<ImapMessage>`: one result per FETCH response as it is read, so memory is one message plus the response being parsed. The query is object-initialiser style: `Uid`, `Flags`, `InternalDate`, `Size`, `Envelope`, `BodyStructure`, `ModSeq`, `ObjectIds` (EMAILID/THREADID), `Source` (`BODY[]`), `Header`, `HeaderFields`, `Sections` (`ImapSectionRequest(section, offset, count, binary)`), `ChangedSince` (+ `Vanished`), `MarkSeen`. Sections use `BODY.PEEK` unless `MarkSeen`, so reading does not set `\Seen`.

- **Buffered sections** (`Source`, `Header`, `HeaderFields`, `Sections`) are available on the result as `Source`, `GetSection("1.2")`, `GetHeaderFields()`; section keys are the specifier upper-cased (`HEADER.FIELDS`, `1.MIME`, ...), BINARY ones `BINARY:1`. A literal above the connection's buffered-literal limit (16 MiB by default, `ProtocolLimits.MaxBufferedLiteral`) is skipped and carries a `LiteralSkipped` defect on `ImapMessage.Data`.
- **Streaming**: `DownloadAsync(uid, section, destination, offset, count)` copies one section from the socket to a stream with the reader's buffers only (a 100 MB message does not allocate 100 MB). `DownloadBinaryAsync` uses `BINARY[]` (RFC 3516). `GetMessageAsync(uid)` hands the literal to `MimeEntity.LoadAsync` and returns the entity tree. `ImapMessage.ParseSource()` parses a buffered `Source`.
- **Stopping early.** Leaving the `await foreach` before the end reads and discards the rest of the response, so the connection stays usable. Cancelling the token instead leaves it mid-response.
- The server may return results in any order and omit messages that vanished meanwhile.
- **Allocation.** A plain one-line response made of `UID`, `FLAGS`, `INTERNALDATE`, `RFC822.SIZE`, `MODSEQ`, `EMAILID`, `THREADID` and `ENVELOPE` is decoded straight from the line into the result, without building the generic response tree; any other shape (literals, `BODYSTRUCTURE`, odd atoms, an atom `NIL` among the flags) goes through the tree parser and gets the same values and defects, which a test and the `imap-fetch` fuzz target compare. Enumerating 10,000 results of `UID FLAGS INTERNALDATE ENVELOPE` allocates about 1 KB per message on Dovecot, which is the result itself (message, data, flags, envelope, strings and addresses), against about 7 KB through the tree. A download allocates a constant amount however many reads the transfer takes.

`ImapFetchData` (via `ImapMessage.Data`) also carries items without a typed property (`Other`, e.g. `X-GM-LABELS`).

## Search, sort, thread

`ImapSearch` builds criteria: static keys (`All`, `Unseen`, `From(text)`, `Subject`, `Body`, `Header(field, text)`, `Since(date)`, `Larger`, `Uid(set)`, `ModSeq`, ...) combined with `And`, `Or`, `Not`. String arguments go out as quoted strings or literals, never raw. A non-ASCII argument adds `CHARSET UTF-8` unless `UTF8=ACCEPT` is enabled. `SearchAsync(criteria, uid, ImapSearchReturn)` returns `ImapSearchResult` (`Numbers`, `Min`, `Max`, `Count`, `ModSeq`): with `ESEARCH` (RFC 4731) and return options it sends `RETURN (...)`; otherwise the classic form, with Min and Max computed. `SortAsync` (RFC 5256; keys and `Reverse`) and `ThreadAsync` (`References`, `OrderedSubject`; a tree of `ImapThread`, `Number == null` for the dummy parent a server invents) need the server's capability and throw `NotSupportedException` without it. Sorting and threading are always sent with charset UTF-8.

## Changing messages

- `StoreAsync(set, Set|Add|Remove, flags, uid, unchangedSince, silent)` (`.SILENT` by default). With `unchangedSince` (CONDSTORE) it returns the numbers the server refused (`[MODIFIED]`).
- `CopyAsync`, `MoveAsync` return `ImapCopyResult` (`COPYUID`: validity, source and destination UIDs; validity 0 when the server has no UIDPLUS). `MoveAsync` uses `MOVE` (RFC 6851); without it, and only for UIDs on a UIDPLUS server, it copies, marks `\Deleted` and sends `UID EXPUNGE`. There is no fallback that could expunge other messages.
- `ExpungeAsync(uids)` is `EXPUNGE`, or `UID EXPUNGE` (UIDPLUS) for given UIDs; it returns the expunge/vanished events.
- `AppendAsync(mailbox, bytes | stream+length | items)` uploads with flags and internal date; a stream is sent without being held in memory. Several items go in one command with MULTIAPPEND (RFC 3502) and one by one without it. A message above the server's `APPENDLIMIT=` (`AppendLimit`) is refused locally with `ArgumentException` before any byte is sent; a server-side refusal (`TOOBIG`, `OVERQUOTA`) is an `ImapCommandException`. `ImapAppendResult` carries `APPENDUID`.

## IDLE

`IdleAsync(renewEvery, stop)` yields `ImapEvent`s as they arrive and updates the mailbox model first. It ends when `stop` is cancelled or when the consumer stops iterating: in both cases the client sends `DONE` and reads the completion, so the connection is reusable and no read is ever cancelled mid-response (the `DONE` is written from the token callback while the loop is blocked reading). IDLE is restarted every `renewEvery` (default 29 minutes, RFC 2177 §3), and the read timeout is raised for the duration. A `BYE` ends the enumeration after its event.

## MailKit calls and ours

This maps calls for people moving over; there is no compatibility layer. MailKit names are those of its public documentation.

| MailKit | OneMail |
|---|---|
| `new ImapClient()`; `ConnectAsync(host, port, SecureSocketOptions)` | `ImapClient.ConnectAsync(host, port, new ImapClientOptions { Connection = new ProtocolConnectionOptions { ImplicitTls = true } })`; `StartTls` is `Auto`/`Required`/`Never` |
| `AuthenticateAsync(user, password)` | `LoginAsync(user, password)` |
| `AuthenticateAsync(SaslMechanism)` | `AuthenticateAsync(SaslMechanism)` (OneMail's own mechanism types) |
| `client.Capabilities` (flags) | `client.Capabilities` (strings), `HasCapability("CONDSTORE")` |
| `client.Inbox`, `GetFolderAsync(path)`, `GetFoldersAsync(namespace)` | `ListAsync("", "*")` returns `ImapMailboxInfo`s; there are no folder objects, mailboxes are names |
| `folder.OpenAsync(FolderAccess)` | `SelectAsync(name, new ImapSelectOptions { ReadOnly = true })` |
| `folder.Count`, `UidValidity`, `HighestModSeq` | `client.Mailbox.Exists`, `.UidValidity`, `.HighestModSeq` |
| `folder.FetchAsync(uids, MessageSummaryItems...)` | `FetchAsync(set, new ImapFetchQuery { ... }, uid: true)` |
| `folder.GetMessageAsync(uid)` | `GetMessageAsync(uid)` (returns OneMail's `MimeEntity`) |
| `folder.GetStreamAsync(uid, section)` | `DownloadAsync(uid, section, stream)` |
| `folder.SearchAsync(SearchQuery)` | `SearchAsync(ImapSearch, uid: true)` |
| `folder.SortAsync`, `ThreadAsync` | `SortAsync`, `ThreadAsync` |
| `folder.AddFlagsAsync(uids, flags, silent)` | `StoreAsync(set, ImapStoreMode.Add, flags, uid: true, silent: true)` |
| `folder.CopyToAsync`, `MoveToAsync` | `CopyAsync`, `MoveAsync` (return the UID mapping) |
| `folder.ExpungeAsync()` | `ExpungeAsync()` |
| `folder.AppendAsync(message, flags, date)` | `AppendAsync(mailbox, bytes, flags, date)` (the caller writes the message) |
| `client.IdleAsync(CancellationToken done)` | `await foreach (var e in client.IdleAsync(stop: token))` |
| `folder.CountChanged`, `MessageExpunged`, `MessageFlagsChanged` events | `client.EventReceived` (one event type) or the `IdleAsync` stream |
| `client.CompressAsync()` | `CompressAsync()` |
| `client.EnableQuickResyncAsync()`; `folder.OpenAsync(uidValidity, highestModSeq, uids)` | `SelectAsync(name, new ImapSelectOptions { QResync = new ImapQResync(...) })`, result in `Mailbox.ResyncEvents` |
| `ImapClient.Capabilities` / protocol logger | `ImapClient.Raw`, `ProtocolConnectionOptions.Trace` |

## What is not here

`NOTIFY` (RFC 5465), `METADATA`, `QUOTA`, `ACL`, `CATENATE`, `URLAUTH`, `LIST-METADATA`, `PARTIAL` (RFC 9394), `SEARCHRES` (`$`), `X-GM-EXT-1` are not typed in this version. Each can be driven through `ImapClient.Raw` with the same connection; the raw response readers parse their data into `ImapResponse.Data`.
