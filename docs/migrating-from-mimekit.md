# Migrating from MimeKit and MailKit

This page maps the calls most programs use to their OneMail equivalents. It is a map, not a shim: OneMail's types are not MimeKit's, there is no compatibility layer, and a mapping row names the nearest idea, not an identical signature. It is written from MimeKit's and MailKit's published API documentation and from what each library does when it is run. Each snippet is one or two lines; the runnable versions are in [`samples/`](../samples).

Packages: `RecursiveAI.OneMail` (formats, models, `Mail.*`), `RecursiveAI.OneMail.Client` (SMTP, IMAP, POP3, SASL), `RecursiveAI.OneMail.Cryptography` (DKIM, ARC, S/MIME, OpenPGP), `RecursiveAI.OneMail.Render` (PDF/PNG).

## What is different, before the tables

1. **Two views of a message.** MimeKit has one object (`MimeMessage`) that is both the tree and the convenience view. OneMail separates them. `IMailItem` is the read view every source implements (EML, MSG, a PST item, an OLM entry ...): `Subject`, `From`, `Sent`, `BestBody`, `Attachments`. `MimeEntity` (`RecursiveAI.OneMail.Mime`) is the raw-preserving tree under it, for when you need headers, parts, or to write back. `Mail.Parse(bytes)` gives the view; `MimeEntity.Parse(bytes)` gives the tree.
2. **Lossless by construction.** A tree you did not modify writes back byte for byte, malformed input included. A part keeps its original headers (folding, case, odd spacing) and body bytes until you change them. MimeKit normalises more on write.
3. **Defects, not exceptions.** Parsers do not throw on bad mail. Every repair or guess is a typed defect with a byte offset (`item.Defects`, `entity.Defects`) and every known ambiguity has one documented resolution ([`ambiguities.md`](ambiguities.md)). There is no `ParseException` to catch; check `Defects` if you care. Resource limits (depth, part count, header size) are on by default and also surface as defects. Only API misuse (null arguments, a disposed object) and I/O errors throw.
4. **Addresses are values.** `MailAddress(Name, Address)` is a readonly record struct. A group or a list that needs the full RFC 5322 shape is `MimeAddress` (`MimeMailbox`, `MimeGroup`) from `AddressParser`. Encoded names are decoded for you; the raw header is still on the tree.
5. **Dates carry their source.** `item.Sent` is a `MailDate` (`Value`, `Source`, `ZoneKnown`): the `Date:` header, an Outlook client-submit time, a delivery time. A message with no usable zone says so instead of silently becoming UTC.
6. **Bodies are chosen, not guessed.** `item.TextBody`, `HtmlBody`, `RtfBody` are candidates; `item.BestBody` applies the documented order (HTML, plain text with `format=flowed` joined, RTF). `BodySelector` explains the choice.
7. **Attachments are items.** `item.Attachments` lists everything that is not the body, inline images included (`IsInline`, `ContentId`); an attached message has `Embedded` (itself an `IMailItem`). `Open()` streams the bytes; large parts are lazy, not loaded.
8. **Async first, sync twins.** The protocol clients return `ValueTask`/`IAsyncEnumerable` and take a `CancellationToken`. Common operations have synchronous twins (`Connect`, `Login`, `Select`, `Fetch`, ...). Stores enumerate with `await foreach`.
9. **Crypto returns structured results.** `SmimeProcessor.Open`, `PgpMimeProcessor.Open` and `DkimVerifier.VerifyAsync` return objects (signers, chain status, covered byte ranges, issues), never a bare `bool` and never an exception for a bad signature. New signatures and encryption refuse weak algorithms; reading accepts them and flags them. CMS and OpenPGP come from OneCrypto, not a third-party crypto library.
10. **No `CryptographyContext` registry.** Keys and trust are passed in options objects; nothing is discovered through global state or reflection (the library is NativeAOT- and trim-clean).
11. **Stores are part of the library.** MimeKit has no MSG, PST, OST, OLM, DBX, ICS or vCard; `Mail.Open` reads all of them, and `Mail.ConvertAsync` converts between them with a loss report.
12. **Not implemented as server pieces.** OneMail has no SMTP/IMAP server and no SPF/DMARC evaluation. IMAP, SMTP and POP3 are native-only (not in the browser WASM build).

## Reading a message

| MimeKit / MailKit | OneMail | Notes |
|---|---|---|
| `MimeMessage.Load(path)` / `Load(stream)` | `Mail.Parse(File.ReadAllBytes(path))`, `Mail.Parse(stream)` | the read view. `MimeEntity.Load(stream)` or `MimeEntity.Parse(bytes)` for the tree; `MimeEntity.LoadAsync` for async |
| `new ParserOptions { ... }` | `MimeReaderOptions` | limits and the From-line mode for mbox; pass as the second argument |
| `message.Subject` | `item.Subject` | decoded, with RFC 2047 repairs reported as defects |
| `message.From`, `To`, `Cc`, `Bcc`, `ReplyTo`, `Sender` | `item.From`, `ToRecipients`, `Cc`, `Bcc`, `ReplyTo`, `Sender` | `IReadOnlyList<MailAddress>` (`Sender` is one address or null) |
| `InternetAddress`, `InternetAddressList` | `MailAddress`; `AddressParser` and `MimeAddress` for groups | `MailAddress.ToString()` formats `Name <address>` |
| `MailboxAddress.Name` / `.Address` | `MailAddress.Name` / `.Address` | |
| `message.Date` (`DateTimeOffset`) | `item.Sent?.Value` | `item.TryGetDate(MailDateSource.X, out var d)` for other dates |
| `message.MessageId`, `InReplyTo`, `References` | `item.MessageId`, `InReplyTo`, `References` | without angle brackets, as parsed ids |
| `message.TextBody`, `message.HtmlBody` | `item.TextBody?.ReadText()`, `item.HtmlBody?.ReadText()` | `MailBody` also has `Open()`, `ContentType`, `Charset` |
| (pick yourself) | `item.BestBody?.ReadText()` | the documented selection |
| `message.Attachments`, `BodyParts` | `item.Attachments` | `FileName`, `ContentType`, `Size`, `ContentId`, `IsInline`, `Open()`, `Embedded` |
| `MimePart.Content.DecodeTo(stream)` | `attachment.Open()` (decoded stream) ; on the tree `MimePart.OpenDecodedContent()` / `GetDecodedContent()` | |
| `TextPart.Text` | `MimePart` on the tree, decoded with its charset: `MailBody.ReadText()` from the view | there is no `TextPart` type; a text part is a `MimePart` with a `text/*` media type |
| `Multipart`, `Multipart.Add` | `MimeMultipart`, `Add`, `Insert`, `Children` | preamble and epilogue bytes are kept (`GetPreamble`) |
| `MimeEntity.ContentType.MimeType` | `entity.MediaType` (`MediaTopLevelType`, `MediaSubType`) | parameters (RFC 2231 continuations and charsets joined) through `entity.Headers` and `Rfc2231` |
| `HeaderList`, `Header` | `MimeHeaderList`, `MimeHeader` | raw bytes, folding and order kept; decoded value on demand |
| `MessagePart` (`message/rfc822`) | `MimeMessagePart.Message`; `IMailAttachment.Embedded` | |
| `MimeMessage.WriteTo(stream)` | `entity.WriteTo(stream)`, `WriteToAsync`, `ToArray()` | unmodified trees write identical bytes |
| `MimeMessage.Headers[HeaderId.X]` | `entity.Headers.GetFirst("Name")`, `GetAll`, `Add`, `RemoveAll` | header ids are names; there is no enum |
| `MultipartReport`, `DeliveryStatusNotification` | `MimeReport`, `DeliveryStatusReport`, `DispositionNotification`, `FeedbackReport` | RFC 3464, 8098, 5965 |
| `AuthenticationResults.TryParse` | `AuthenticationResults` | `ReceivedSpf` too |
| `MimeKit.Text.HtmlToHtml`, `TextToHtml`, `HtmlToText`, `FlowedToText` | `HtmlToText`, `TextToHtml`, `FormatFlowed`, `HtmlSanitizer` (namespaces `RecursiveAI.OneMail.Bodies` and `.Display`) | the sanitiser is an allowlist on OneHtml's DOM; `item.ToDisplayHtml()` is the one-call form |

## Building and sending

| MimeKit / MailKit | OneMail | Notes |
|---|---|---|
| `BodyBuilder { TextBody, HtmlBody }`, `Attachments.Add(path)`, `ToMessageBody()` | `OutgoingMessage { From, To, Subject, Text, Html, Attachments = [new OutgoingAttachment(name, bytes)] }`, `ToEntity()` | the object initialiser is the builder. Embedded images: build a `MimeMultipart("related")` by hand, or `MhtmlBuilder` |
| `new MimeMessage()`, `message.From.Add(new MailboxAddress(...))` | `new OutgoingMessage { From = "Ann <ann@example.com>" }`; or the tree: `MimeEntity.Headers.Add("From", ...)` | addresses are strings in `OutgoingMessage`; non-ASCII names are encoded for you |
| `message.WriteTo(...)` | `outgoing.ToEntity().WriteTo(stream)` | |
| `new SmtpClient(); Connect; Authenticate; Send; Disconnect` | `await Mail.SendAsync(message, new SmtpClientOptions { Host = ..., Credentials = ... })` | one call: connect, STARTTLS, authenticate with the best mechanism the server offers, send, QUIT. `SmtpClient` is the long-lived form |
| `client.Send(message)` result strings, `MessageSent` | `SmtpSendResult` (`Delivered`, `Rejected`, `DataReply`); `ThrowIfNotDelivered()` | rejected recipients are data, not exceptions, unless you ask |
| `SecureSocketOptions.StartTls` / `SslOnConnect` | `SmtpClientOptions.Security` (`SmtpSecurity`) | |
| `SaslMechanismOAuth2`, `NetworkCredential` | `SaslCredentials`, `SaslXOAuth2`, `SaslOAuthBearer`; `SaslMechanism` classes | token acquisition stays yours |
| `message.Prepare(EncodingConstraint.SevenBit)` | `MimeWriterOptions`; transfer encodings are chosen on write; `TransferEncoder` | |
| `MimeMessage.CreateReply` (sample code in the MimeKit docs) | `item.CreateReply(options)`, `CreateReplyAll`, `CreateForward` | threading headers, conversation index, quoting; `ComposedMessage.Message` is the tree to send |

## IMAP, POP3

| MailKit | OneMail (`RecursiveAI.OneMail.Client.Imap`, `.Pop3`) | Notes |
|---|---|---|
| `new ImapClient(); ConnectAsync(host, port, SecureSocketOptions.SslOnConnect)` | `await ImapClient.ConnectAsync(host, 993, new ImapClientOptions { Connection = new ProtocolConnectionOptions { ImplicitTls = true } })` | STARTTLS policy is `ImapClientOptions.StartTls` |
| `AuthenticateAsync(user, password)` | `LoginAsync(user, password)`; `AuthenticateAsync(mechanism)` | `LoginAsync` uses AUTHENTICATE PLAIN, else LOGIN |
| `client.Inbox.OpenAsync(FolderAccess.ReadWrite)` | `await client.SelectAsync("INBOX")` returns `ImapMailbox` (`Exists`, `UidNext`, `UidValidity`, flags, `HighestModSeq`) | one selected mailbox per client; there is no folder object |
| `GetFoldersAsync`, `GetSubfolders` | `ListAsync(reference, pattern, options)` | SPECIAL-USE attributes on `ImapMailboxInfo` |
| `UniqueId`, `UniqueIdRange`, `IList<UniqueId>` | `long` uid plus `ImapSet` (`Of`, `Range`, `From`, `All`, `Parse`) with `uid: true` | pair a UID with `ImapMailbox.UidValidity` yourself; sets use IMAP syntax |
| `folder.FetchAsync(range, MessageSummaryItems.Envelope \| UniqueId \| Flags)` | `client.FetchAsync(set, new ImapFetchQuery { Uid = true, Envelope = true, Flags = true }, uid: true)` | `IAsyncEnumerable<ImapMessage>` |
| `folder.GetMessageAsync(uid)` | `client.GetMessageAsync(uid)` returns a `MimeEntity`; or fetch with `Source = true` and `Mail.Parse(m.Source)` | |
| `GetStreamAsync`, `GetBodyPartAsync` | `DownloadAsync(uid, section, stream, offset, count)` | partial `BODY[]` streams |
| `folder.SearchAsync(SearchQuery.NotSeen.And(SearchQuery.FromContains("x")))` | `client.SearchAsync(ImapSearch.Unseen.And(ImapSearch.From("x")))` | `ImapSearchResult`; ESEARCH, SORT and THREAD are separate calls (`SortAsync`, `ThreadAsync`) |
| `AddFlagsAsync`, `RemoveFlagsAsync`, `SetFlagsAsync` | `StoreAsync(set, ImapStoreMode, flags, uid, unchangedSince)` | CONDSTORE `unchangedSince` supported |
| `CopyToAsync`, `MoveToAsync` | `CopyAsync(set, destination)`, `MoveAsync(set, destination)` | return `ImapCopyResult` (UIDPLUS) |
| `ExpungeAsync` | `ExpungeAsync(uids)` | |
| `AppendAsync(message)` | `AppendAsync(mailbox, bytes, flags, internalDate)` | |
| `client.IdleAsync(token)` + `CountChanged` events | `await foreach (ImapEvent e in client.IdleAsync(stop: token))` | events are yielded in order, `client.Mailbox` is already updated; stopping is a token and leaves the connection usable |
| `ImapClient.Capabilities`, `Enable` | `HasCapability(name)`, `EnableAsync` | |
| raw commands | `client.Raw` (`ImapRawConnection`) | tagged responses, literals as streams; drive any extension by hand |
| `Pop3Client` | `Pop3Client` | `ConnectAsync`, `LoginAsync`, `StatAsync`, `ListAsync`, `UidlAsync`, `GetMessageAsync` (a `MimeEntity`), `RetrieveAsync` (to a stream), `DeleteAsync`; `Pop3RawConnection` underneath |

## Mailbox files and Outlook formats

| MimeKit | OneMail | Notes |
|---|---|---|
| `MimeParser(stream, MimeFormat.Mbox)` and `ParseMessage()` in a loop | `await foreach (IMailItem m in Mail.Open("archive.mbox"))` | content-sniffed: mboxo/rd/cl/cl2 and Gmail Takeout labels. `MboxReader.Enumerate(source)` yields byte ranges and metadata without parsing |
| `MimeParser` position bookkeeping | `IMailItem.Provenance` (`Format`, `Folder`, `Offset`, `Length`, `Index`) | |
| writing mbox | `MboxWriter.Append(bytes)` | `Stores.Mbox` |
| Maildir / EMLX | `Mail.Open(dir)`; `MaildirStore`, `EmlxReader`, `AppleMailBundle` | flags and keywords on the metadata |
| `TnefReader`, `TnefPart.ConvertToMessage()` | `TnefMessage.Read(bytes)`; for a `winmail.dat` part, the `part.IsTnef()` / `part.ReadTnef()` (`MimeTnefExtensions`) on the tree; `MimeToMapiConverter` / `MapiToMimeConverter` | the TNEF attribute stream is `TnefDocument`; unknown attributes are kept |
| (no equivalent) | `Mail.Open("file.msg" / ".pst" / ".ost" / ".olm" / ".dbx")` | same `IMailItem` view; `MapiMessage` and `MapiPropertyBag` for the property truth |
| (no equivalent) | `Mail.ConvertAsync(source, ConvertFormat.Mbox, dest)` | PST to mbox, MSG to EML, any store to PST, with a loss report |

## Signing and encryption

| MimeKit | OneMail (`RecursiveAI.OneMail.Cryptography`) | Notes |
|---|---|---|
| `DkimSigner(key, domain, selector).Sign(message, headers)` | `new DkimSigner(DkimSigningKey.FromPem(pem), new DkimSignerOptions { ... }).Sign(entity)` | RSA and Ed25519; returns the signed bytes. Over-signing and header choice live in the options |
| `DkimVerifier.Verify(message, header)` returns `bool` | `await new DkimVerifier(options).VerifyAsync(bytes)` returns one `DkimSignatureResult` per signature (status, reason, covered ranges, key facts) | resolver is pluggable (`ITxtResolver`, `DnsTxtClient`, `CachingTxtResolver`) |
| ARC (`ArcSigner`, `ArcVerifier`) | `ArcSealer`, `ArcVerifier` | structured chain status |
| `SecureMimeContext`, `MultipartSigned.Create`, `ApplicationPkcs7Mime.Encrypt` | `SmimeSigner.SignDetached` / `SignOpaque`, `SmimeEncryptor.Encrypt` | options carry the keys; weak algorithms refused |
| `MultipartSigned.Verify()` -> `DigitalSignatureCollection` | `SmimeProcessor.Open(entity)` -> `SmimeResult` (layers, signers, chain status, issues, `InnerItem`) | decrypt and verify in one call; `IsTrusted`, `Succeeded` |
| `OpenPgpContext`, `MultipartEncrypted` | `PgpMimeSigner`, `PgpMimeEncryptor`, `PgpMimeProcessor.Open` -> `PgpResult`; `PgpKeySource`, `PgpWkdKeySource` | PGP/MIME, inline reading, Autocrypt |
| display of verification state | `SmimeDisplay.ToDisplayHtml(result)`, `PgpDisplay.ToDisplayHtml(result)` | status block above the message |

## Things MimeKit does that OneMail does differently on purpose

- **`MimeMessage` mutation as the editing model.** Edit the tree (`Headers`, `Children`, `SetDecodedContent`); entities know whether they were modified (`IsModified`), and a tree nobody touched writes back as the bytes it was read from. To compose, use `OutgoingMessage` or `CreateReply`.
- **`ParseException`/`FormatException` on malformed mail.** Read `Defects`.
- **`IEnumerable` of messages from a stream.** `Mail.Open` returns `MailContainer` (`IAsyncEnumerable<IMailItem>` and `Enumerate()`); dispose it.
- **Charset registration** (`CharsetUtils`, `Encoding.RegisterProvider`). The WHATWG encodings, UTF-7, IMAP modified UTF-7 and MAPI code pages are built in (`MailEncoding`); nothing to register.
- **Synchronous-by-default protocol clients.** Use the async methods; the sync twins block.
