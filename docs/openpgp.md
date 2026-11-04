# OpenPGP mail (`RecursiveAI.OneMail.Cryptography.OpenPgp`)

Normative notes for signing, verifying, encrypting and decrypting mail with OpenPGP: PGP/MIME (RFC 3156), inline OpenPGP in text bodies (RFC 9580 §6 armor and §7 cleartext signatures, RFC 4880 for the older form), Autocrypt headers (Autocrypt Level 1) and protected headers. **All OpenPGP work is OneCrypto's** (`RecursiveAI.OneCrypto.OpenPgp` 0.4.0-preview.1): packets, keys and certificate validity, signatures, session keys, symmetric and AEAD encryption, compression, the crypto policy. This package does the mail side: which bytes are signed, how the MIME is framed, what the structure of a message means, and what lies outside the protection. The mail path has had an adversarial review (see *Review* below).

## What it does

| Area | Types |
|---|---|
| Sign | `PgpMimeSigner.SignDetached` → `multipart/signed` with an armored `application/pgp-signature` part; options in `PgpMimeSignOptions` |
| Encrypt | `PgpMimeEncryptor.Encrypt` → `multipart/encrypted` with the `application/pgp-encrypted` control part; recipients and passwords, optional signers (the combined method); options in `PgpMimeEncryptOptions` |
| Open | `PgpMimeProcessor.Open(MimeEntity or bytes, PgpOpenOptions)` → `PgpResult` (layers outermost first, the content reached, the inner message in the shared view, what is outside the protection) |
| Keys | `IPgpKeySource` and `PgpKeySource` (lookup by recipient address over a caller-supplied source) |
| Autocrypt | `PgpAutocrypt.Read` and `Describe` (headers found in the MIME tree; the grammar and peer state are OneCrypto's `AutocryptHeader` and `AutocryptPeer`) |

Keys and certificates are passed in by the caller: `PgpSecretKey` (unlocked, from `PgpSecretKey.Unlock`) for signing, `PgpCertificate` for recipients, verification and decryption. OneMail stores no keys, reads no keyring unless the caller does through OneCrypto (`GnuPGHome`, keybox, WKD in `RecursiveAI.OneCrypto.Online`), consults no operating-system store and reads no clock: every call takes the reference time. Which certificates to hand to `PgpOpenOptions.Certificates` is the caller's trust decision; a signature is judged for the key that made it and nothing more.

## Signing (RFC 3156 §3, §5)

* **Canonical form.** The entity to sign is copied; every leaf with a body that is binary, or holds a byte above 0x7F or a NUL, is re-encoded (quoted-printable for `text/*`, base64 otherwise); a text leaf with trailing white space on a line is quoted-printable encoded so the white space survives as `=20`; a leaf with a line that starts `From ` is base64 encoded (RFC 3156 §3 note); trailing white space is removed from header lines and a header line of only white space is removed whole; line ends are CRLF. An entity that already follows these rules keeps every stored byte.
* **What is signed.** Exactly the first part of the `multipart/signed`, header block and body, as the writer stores it. The part goes into the multipart as parsed bytes so nothing later can change it. Reading hashes `signedPart.ToArray()`, the bytes as received, never a re-serialisation.
* **`micalg`.** `pgp-` plus the lower-case hash name (`pgp-sha512`, `pgp-sha256`, `pgp-sha3-256` …), one token per distinct hash, comma separated for several signers with different hashes. The signature is binary (type 0x00) and armored.
* **Algorithms.** Whatever OneCrypto signs with: v4 EdDSA (legacy), native Ed25519 and Ed448, ECDSA, RSA; v6 keys with salted v6 signatures. **New signatures refuse weak algorithms**: SHA-1, MD5 and RSA under 2048 bits raise `PgpException` (`PgpFailure.PolicyViolation`) under the default policy (`PgpMimeSignOptions.Policy` null is `CryptoPolicy.Standard`).

## Encrypting (RFC 3156 §4, §6)

* The plaintext is the MIME entity in canonical form (CRLF; with signers, the signing canonical form above, RFC 3156 §6.2). Encrypt-only content keeps 8-bit bytes and trailing white space (RFC 3156 §3).
* `multipart/encrypted; protocol="application/pgp-encrypted"`: part one `application/pgp-encrypted` with `Version: 1`, part two `application/octet-stream; name="encrypted.asc"` holding the armored message.
* The container is OneCrypto's choice from the recipients' keys: SEIPD v2 (AEAD) when every recipient is v6 or announces it, otherwise SEIPD v1 with MDC; `ForceSeipd1` forces the older one. New encryption goes through the policy.
* **Three shapes.** Encrypt only; the combined method (`PgpMimeEncryptOptions.Signers`: one-pass signatures inside the encrypted message, hidden from everyone but the recipients); sign-then-encrypt (give `PgpMimeEncryptor.Encrypt` the output of `PgpMimeSigner.SignDetached`: the signature is visible to anyone who decrypts and is a detached signature over the inner entity). All three are in the interop set.

## Opening: the result

`PgpMimeProcessor.Open` never throws on message content. It unwraps layers one inside another, up to `MaxDepth` (default 8): `multipart/signed` with an `application/pgp-signature` part, `multipart/encrypted` with `application/pgp-encrypted`, and text bodies with a cleartext-signed or armored message. If the root is a wrapper that is not OpenPGP (`multipart/mixed`, a list's `multipart/alternative`), the first OpenPGP entity below it is processed.

`PgpResult`:

| Member | Meaning |
|---|---|
| `Layers` | each layer with `Kind`, `Status`, `Succeeded`, `Issues`, `Detail`, `Signers`, `Protection`, and the entity it came from |
| `Content`, `InnerItem` | the innermost entity reached; the inner message in the shared view (`IMailItem`), with protected headers merged over the outer ones |
| `Succeeded` | at least one layer, every layer succeeded, and the protected entity was not found inside an unprotected wrapper |
| `IsAuthenticated` | `Succeeded` and at least one signature is valid for a certificate the caller supplied. **An encrypted message with no signature is never authenticated**: decryption is not verification (RFC 9580 §13). It does not say who signed |
| `IsSenderAuthenticated` | `IsAuthenticated` and a valid signature, made with a key that was usable at signing time, comes from a certificate whose certified User IDs name every address in `From`. The question a reader asks ("did the person shown as the sender sign this"); the `From` header is still outside a PGP/MIME signature unless `HeadersProtected` |
| `HeadersProtected` | authenticated, and the protected content repeats headers (`Outside.ProtectedHeaders`). Headers repeated inside content that is not authenticated (an unsigned encrypted message, a failed signature) are neither listed nor merged into `InnerItem`: the item shows the outer headers |
| `Signers` | all signatures of all layers |
| `Outside` | `UnprotectedHeaders` (outermost message headers, which RFC 3156 never covers), `UnprotectedParts` (paths of parts beside the protected one inside a wrapper), `ProtectedHeaders` |

A signature (`PgpSignerResult`) carries OneCrypto's `PgpSignatureResult`, the signer's certificate and key fingerprints, the certificate's user IDs, the user ID that names the message's sender (`UserId`, `UserIdMatchesSender`), the creation time, **whether the key was valid at that time** (`KeyValidAtSigning`: bound, unexpired, unrevoked, able to sign), the hash, the policy decision and the weak algorithms the policy flags, and the reasons. A detached layer also reports `CoveredBytes`, `CoveredSourceOffset` and `CoveredSourceLength` (where the signed part sits in the file it was read from), `Micalg`. An encrypted layer reports `Protection` (`Aead`, `Mdc`, `OcbEncryptedData`, `None` for old unauthenticated data opened only with `AllowUnauthenticatedEncryption`).

`PgpStatus`: `Valid`, `Decrypted`, `SignatureInvalid`, `SignerUnknown`, `KeyNotValid`, `PolicyRejected`, `Unsupported`, `MicalgMismatch`, `StructureInvalid`, `Malformed`, `DecryptionFailed`, `NoDecryptionKey`, `UnauthenticatedEncryption`, `LimitExceeded`, `NoSignature`. Decryption has one failure status for every reason a key cannot be used or a body does not authenticate (`DecryptionFailed`, RFC 9580 §13); `NoDecryptionKey` says no supplied key addresses the message, which is public information.

## Rules and ambiguities

Resolutions for behaviour the RFCs leave open. No mail client has been observed yet beyond GnuPG's and Sequoia's own tools, so every non-unanimous entry is an agent default.

| ID | Case | Resolution | Issue |
|---|---|---|---|
| PG1 | `micalg` names a hash other than the signature's | **Fails** (`MicalgMismatch`; RFC 3156 §5 MUST). GnuPG and Sequoia ignore the parameter; the signature itself is still reported valid in `Signers` | `Detail` names the parameter |
| PG2 | no `micalg` | Tolerated (many writers omit it) | `MicalgMissing` |
| PG3 | `multipart/signed` with other than two parts, or a second part that is not a leaf | `StructureInvalid` (RFC 3156 §5 MUST). This is how unsigned content gets placed next to a valid signature | |
| PG4 | the signed part verifies only after LF→CRLF, or after stripping trailing white space | Accepted, the repaired bytes are reported in `CoveredBytes` (RFC 3156 §3, §5: the signature is over the CRLF, no-trailing-white-space form) | `SignedPartLineEndingsNormalised`, `SignedPartTrailingWhitespaceStripped` |
| PG5 | several signatures | The layer is `Valid` when at least one is valid and none is `Invalid`, `Rejected` or `KeyNotValid`. A co-signer whose key we lack does not spoil a valid signature | per-signature status |
| PG6 | wrong or missing `protocol`, signature or control part of another content type, control part without `Version: 1` | Tolerated when the part decodes | `ProtocolParameterUnexpected`, `SignaturePartTypeUnexpected`, `ControlPartUnexpected` |
| PG7 | the OpenPGP entity sits in a `multipart/mixed` beside other parts | Found and processed, the result is not `Succeeded`, the other parts are listed | `ProtectedPartInUnprotectedWrapper` |
| PG8 | encrypted without a signature | `Succeeded` (it decrypted) but not `IsAuthenticated` | `EncryptedWithoutSignature` |
| PG9 | the signer's user IDs do not name the `From` address | Reported (`UserIdMatchesSender` false; `IsSenderAuthenticated` false), not enforced: acting on it belongs to the caller. Only User IDs the key itself certified (and had not revoked at signing time) count, and every address of every `From` header must be named | |
| PG10 | inline: armor in a quoted-printable body whose escapes the transfer encoding did not account for | Escapes decoded, then read | `InlineQuotedPrintableRepaired` |
| PG11 | inline: block surrounded by other text, or every line carrying the same quote prefix | The block is cut out; the prefix removed | `InlineTextOutsideArmor`, `InlineQuotePrefixRemoved` |
| PG12 | inline: cleartext signature that verifies only after the text is converted from the declared charset to UTF-8 | Accepted; the signature covers the text as signed, and a gateway that transcoded it changed the bytes | `InlineCharsetReencoded` |
| PG13 | protected headers (`protected-headers` on the decrypted top-level entity, or a `message/rfc822` inside) | Counted as protected only when the message is authenticated (`IsAuthenticated`); only then do they replace the outer headers of the same name in `InnerItem` | `ProtectedHeadersPresent`; otherwise `HeadersNotProtected` |
| PG14 | inline OpenPGP in a `text/html` part | Read only when an armor line stands on its own line of the markup (an armored block quoted inside ordinary HTML is not the message's protection). The signed or decrypted content is typed `text/html` (it is the part's raw markup), the text around the block is outside the protection as for text parts (PG11) | `InlineHtmlPart`, `InlineTextOutsideArmor` |
| PG15 | an authenticated, encrypted message whose protected `To`/`Cc` do not name the reader, or are absent | Reported on the result, `Succeeded` unchanged (the caller may be a Bcc recipient). Nothing is checked when there is no address to check against | `ProtectedRecipientMismatch`, `ProtectedRecipientsMissing` |
| PG16 | an attachment typed `application/octet-stream` with a `.pgp`, `.gpg` or `.asc` name | Listed in `OpenPgpAttachments` (or `AttachedKeys` for a public key block) only when the decoded content is an armored message in its first 4 KiB or starts with a packet tag a message starts with; a `.asc` file of anything else is not listed | |

## Inline OpenPGP

A text body (`text/*` except `text/html`, or `application/pgp`) that holds `-----BEGIN PGP SIGNED MESSAGE-----` (cleartext signature, RFC 9580 §7: dash-escaping, `Hash:` header, trailing white space ignored) or `-----BEGIN PGP MESSAGE-----` (encrypted, signed, or both) is read as a layer of kind `InlineCleartextSigned` or `InlineMessage`. The bytes handed to OneCrypto are the transfer-decoded body, not a charset conversion; the result's `Content` is a new `text/plain` part with the verified or decrypted text, UTF-8 when it is valid UTF-8, otherwise the declared charset. Repairs are tried in order (as received, quoted-printable escapes decoded, charset converted) and the first reading that verifies or decrypts wins; each repair is an issue. HTML bodies are not searched, and per-attachment inline encryption (`.pgp` attachments) is not read.

## Protected headers and Autocrypt

* **Protected headers** (the "memory hole", draft-autocrypt-lamps-protected-headers and draft-ietf-lamps-header-protection). *Reading:* a decrypted or signed top-level entity whose `Content-Type` has `protected-headers`, or a `message/rfc822` inside the protection, lists the headers it repeats in `Outside.ProtectedHeaders`; `InnerItem` shows them over the outer ones, but only when the message is authenticated. *Writing:* `PgpMimeSignOptions.ProtectedHeaders` and `PgpMimeEncryptOptions.ProtectedHeaders` take a `PgpProtectedHeaders` (`FromMessage(draft, obscureSubject)` copies From, Sender, To, Cc, Reply-To, Subject, Date, Message-ID, In-Reply-To, References, Keywords and Comments; or give the header fields). The signed or encrypted entity is a copy that carries exactly those fields plus its own `Content-*` headers, with `protected-headers="v1"` added to its Content-Type; the same fields are written on the returned `multipart/signed` or `multipart/encrypted` (with `MIME-Version`), and for an encrypted message the outer `Subject` can be `...` (`ObscureSubject`, text in `ObscuredSubject`). `Bcc` is refused: it would name the other recipients to every recipient. For sign-then-encrypt give the same `PgpProtectedHeaders` to the signer and to the encryptor; the encryptor then leaves the signed wrapper alone and writes only the outer fields. Signing without encrypting never obscures the subject.
* **Surreptitious forwarding.** A recipient can decrypt a signed message and re-encrypt it to someone else, and it still verifies as the author's (the signature covers the entity, not the envelope). When the message is authenticated, was encrypted and has protected headers, `Open` compares the protected `To` and `Cc` with the addresses it was opened for (`PgpOpenOptions.RecipientAddresses`, else the certified User IDs of `SecretKeys`) and puts `PgpIssue.ProtectedRecipientMismatch` in `PgpResult.Issues` when none is named, or `ProtectedRecipientsMissing` when the protected headers hold neither header. It is an issue, not a failure: `Succeeded` does not change. A Bcc recipient is, by design, not in `To` or `Cc` and gets the mismatch issue; a caller that knows a message was sent Bcc ignores it.
* **Autocrypt** (Level 1): `PgpAutocrypt.Read` returns every `Autocrypt` header of the message (valid or with OneCrypto's reason), the one Autocrypt would use (`Selected`: exactly one valid header naming the From address), and the `Autocrypt-Gossip` headers of the entity read (pass the decrypted entity; gossip counts only from an encrypted payload). `PgpAutocrypt.Describe` turns a message into OneCrypto's `AutocryptMessage` for `AutocryptPeer.Apply`. Peer storage and the recommendation are the caller's, with OneCrypto's `AutocryptPeer` and `AutocryptRecommender`.

## Attachments, HTML and display

* **Attached keys.** `PgpResult.AttachedKeys` lists the public keys attached as `application/pgp-keys` (or `.asc` parts holding a public key block), found in the outer message and in the decrypted or verified content (`FromProtectedContent`, paths in the content prefixed `protected:`). The certificates are parsed by OneCrypto's `KeyDiscovery.ReadAll`, which never accepts secret key material. Nothing is imported or stored, and a key a message carries is the sender's claim, not evidence of who they are. At most 32 key parts of 1 MiB each; more raises `AttachmentLimitReached`.
* **Encrypted attachments.** `PgpResult.OpenPgpAttachments` lists attachments that hold OpenPGP data (PG16); `PgpMimeProcessor.OpenAttachment(part, options)` decrypts one (armored or binary) and verifies any signatures inside, returning the plaintext bytes (`Data`), the name without its `.pgp`/`.gpg`/`.asc` suffix and the layer (`Status`, `Signers`, `Protection`, `Issues`). The same limits as for message layers apply (`MaxPlaintextBytes`); decryption failures are one status.
* **HTML bodies.** A decrypted or verified `text/html` part, alone or in a `multipart/alternative`, reaches the shared view like any other part: `InnerItem.HtmlBody` and `TextBody`, then `ToDisplayHtml()` and the sanitiser (docs/display-html.md). Inline OpenPGP in an HTML part is PG14.
* **Display.** `PgpDisplay.ToDisplayHtml(result, options)` renders `InnerItem` through the normal display path with a status block on top (`DisplayOptions.StatusBanner`, a new hook that writes a caller's fragment before the header block): signed by whom and with which key; encrypted but not signed ("nobody vouches for who wrote it"); signature valid but the key does not name the sender; not verified, with the reason; parts beside the signed part in an unprotected wrapper that are not shown; text around an inline signature that is not shown; the headers not covered by the signature; the signed recipient list not naming the reader; a weak algorithm. Only the protected content is rendered, so unsigned parts are never shown as if signed. Message-derived text in the block (User IDs) is escaped. `PgpDisplay.StatusHtml(result)` gives the fragment alone.

## Web Key Directory

`PgpWkdKeySource(WkdClient, WkdOptions?)` is an `IPgpKeySource` over OneCrypto's Web Key Directory client (`RecursiveAI.OneCrypto.Online`): the request limits, the network guard, redirects, the HTTP cache and the advanced-versus-direct choice are the client's and the options'; the response is cut down to the User ID that names the address by `KeyDiscovery.ForAddress`. A failed lookup is an empty answer, never an exception (only cancellation throws). `PgpKeySource.ResolveRecipientsAsync` still decides whether a certificate may be used (a User ID the key itself certified, a usable encryption key). Tested against an in-memory transport that serves the directory paths; no test touches the network.

## Keys

`IPgpKeySource.FindAsync(address)` is what a caller implements over its keyring, a GnuPG home read by OneCrypto, or WKD. `PgpKeySource.FromCertificates` is a list source that matches the address in each user ID (candidates only); `ResolveRecipientsAsync` returns, per address, a certificate that can encrypt at the reference time **and whose own key certified a User ID naming the address**, and the addresses with none; `RecipientAddresses` reads `To`, `Cc` and `Bcc`.

## Safety defaults

New signatures and encryption go through OneCrypto's standard policy: SHA-1, MD5 and short RSA keys are refused (tested). Reading accepts and flags them (`WeakAlgorithms`, `Policy`). Old Symmetrically Encrypted Data (no integrity check) is refused unless `AllowUnauthenticatedEncryption`, then flagged (`UnauthenticatedEncryption`, `Protection = None`). Decryption failures are one status. Decrypted output is bounded by `MaxPlaintextBytes` (default 256 MiB, `LimitExceeded`); OneCrypto bounds decompression (a ceiling of 1 MiB plus 1032 times the compressed octets read), key-derivation work, session-key packets and nesting. An inline body larger than `MaxInlineBytes` (64 MiB) is not searched. MIME depth is bounded by the MIME reader (64) and layers by `MaxDepth`.

## Checked

* **Unit tests** (`PgpMimeTests`, `PgpInlineTests`, `PgpHostileInputTests`): v4 EdDSA/ECDH, native v4, v6 and RSA-3072 keys, sign, encrypt, combined and nested forms; tampered byte, swapped signature, stripped signature, empty signature part, wrong and missing `micalg`, unknown signer, LF copy; QP, charset, surrounding text and quote prefix for inline; protected headers; hostile input (12 nested layers, 5 MB of armor, truncated cleartext, 3,000 parts, 3,000 nested multiparts, a decrypted size over the limit).
* **Interop**, run as released programs (`tools/openpgp/interop.sh`; the MIME parts are cut out of the messages by a splitter in the script, not by OneMail): GnuPG 2.4.4 (distribution package), GnuPG 2.5.24 (OneCrypto's pinned build), Sequoia `sqop` 0.38.0. Results are in `compatibility.md`; the messages are `testdata/openpgp/vectors/`, read back by `PgpInteropVectors` without any outside program.
* **Thunderbird 159.0**, the Mozilla release tarball run headless (`tools/thunderbird/run.sh`): it opens our messages and we open the ones it writes. Method, results and the differences are in the *Thunderbird* section below.
* **Fuzz** `pgp-mime`: see `compatibility.md`.

## Not done

* Other mail clients than Thunderbird have not opened these messages. (Thunderbird's S/MIME is in `smime.md`.)
* Thunderbird 159 does not import native-Ed25519 v4 keys or version 6 keys, and does not decrypt RFC 9580 version 6 session-key and version 2 data packets; the vectors using them are written but not opened there.
* GnuPG 2.5.24 reads our v4 EdDSA/Cv25519 messages for verification but did not decrypt to the Cv25519 subkey after importing a OneCrypto-generated secret key (the subkey is listed as not available); GnuPG 2.4.4 and Sequoia do. Reported to OneCrypto.
* GnuPG does not read RFC 9580 version 6 keys or the native Ed25519/X25519 algorithms; those cells run against Sequoia only.
* Not built: inline signing and encrypting (write with OneCrypto's `PgpMessageWriter`); signed-only or cleartext `.asc` attachments (a detached `application/pgp-signature` attachment is not matched to the file it signs); importing or storing attached keys.

## Thunderbird

`tools/thunderbird/run.sh` unpacks the pinned release (sha256-checked, no install), starts it with `MOZ_HEADLESS` (no display needed) and drives it through Marionette in the privileged context. A throwaway profile has one POP-type account whose Inbox is a maildir store, so the bytes a message has on disk are the bytes under test; mail goes out through a local SMTP sink. Keys go in through the key manager's own import and are accepted as personal keys; the status read back is the one the message header pane shows (signature and encryption state, the decrypted body, and a screenshot per message in `$OUT/shots`).

* **Ours in Thunderbird.** Each message of the vector set is dropped into the Inbox and opened. Our v4 EdDSA/Cv25519 and RSA-3072 messages all show a valid signature where signed and decrypt where encrypted, with the right body (8/8). A signature from a key the user has not accepted for that address shows as uncertain ("key not accepted"), and as valid-unverified once accepted; that is Thunderbird's trust model, not a defect (checked with the public key alone, 4/4).
* **Thunderbird in ours.** Thunderbird composes plain text (flowed off) to the key's own address in three modes. Its messages are multipart/signed with a quoted-printable part, and multipart/encrypted whose plaintext is a multipart/mixed holding a protected-header block (the Subject) and the text. All six open in `PgpMimeProcessor.Open` as authenticated where signed, with `ProtectedHeadersPresent`, and the text decodes to what was typed, including trailing spaces and a line starting `From `. They are committed in `testdata/openpgp/vectors/thunderbird` and read back by `PgpInteropVectors`.
* **Not accepted by Thunderbird:** the native-Ed25519 v4 key and the version 6 key (key import throws), so no message with them was opened; Sequoia's messages for its v4 key use RFC 9580 packets (public-key encrypted session key v6, encrypted data v2), which Thunderbird 159 shows as an empty body.
* **Bare LF (fixed in the vector script).** The first GnuPG and Sequoia vectors' detached-signature messages contained one bare LF in the signed entity: Python's quoted-printable encoder breaks long lines with `=` and a bare LF, and the script copied it into the signed text. Thunderbird makes line endings CRLF before it verifies and reported those 9 signatures invalid. A probe signed by GnuPG over eight text shapes (plain, trailing spaces, a `From ` line, both, in 7bit and quoted-printable) is valid in every one, and a bare LF makes it invalid in both encodings, so the fault was the vector bytes. The script now writes the soft breaks as CRLF and the GnuPG 2.4.4, GnuPG 2.5.24 and Sequoia vectors were regenerated (`interop.sh`: 57 of 62 cells ok; the 5 that fail are GnuPG 2.5.24 decrypting our Cv25519 key, described above). With them, Thunderbird shows every GnuPG and Sequoia signed message that it can read as valid: GnuPG 2.4.4 8/8, GnuPG 2.5.24 8/8, Sequoia 1/1 (its other three readable-key messages are the RFC 9580 ones above), including sign-then-encrypt.

## Review

An adversarial review of the OpenPGP mail path (`PgpMimeSigner`, `PgpMimeEncryptor`, `PgpMimeProcessor`, `PgpResult` and its parts, `PgpKeySource`, `PgpAutocrypt`) against the question: what can make a reader believe a message is signed by someone, or private, when it is not, and what leaks plaintext or lets an attacker steer it. OneCrypto's packet and crypto layer was exercised through the mail path but not reviewed. Tests: `PgpReviewTests`; each bug below failed before its fix and passes after.

**Verdict: passed.** No block-release finding remains open.

### Fixed

| # | Severity | Finding | Fix |
|---|---|---|---|
| 1 | block release | Headers repeated inside an **unsigned** encrypted message (anyone with the recipient's public key can make one) were counted as protected and replaced the outer headers in `InnerItem`: an attacker could show `From: boss`. `ProtectedHeaderNames` required only that *some* layer succeeded, and an unsigned decrypted layer succeeds | Protected headers count only when `IsAuthenticated`; otherwise `InnerItem` shows the outer headers and the content's own non-content headers are dropped |
| 2 | block release | `ResolveRecipientsAsync` and `UserIdMatchesSender` trusted any User ID packet on a certificate. Such a packet can be added to a public certificate in transit or on a key server without the key's owner: mail for `boss@` would be encrypted to the key of whoever added it, and a signature by that key would be "from" `boss@` | Only User IDs that the key certified itself (`PgpUserIds.HasSelfSignature`, so revoked and unsigned ones are out), judged at signing time for signatures and at the reference time for recipients |
| 3 | fix before advertising | `IsAuthenticated` meant "some supplied key signed", which a reader takes as "the sender signed" | Added `PgpResult.IsSenderAuthenticated` (additive) and documented the difference |
| 4 | fix before advertising | `UserIdMatchesSender` was true if any one `From` address matched, so `From: alice, boss` or a second `From` header passed for a signer who only names alice | Every address of every `From` header must be named |
| 5 | fix before advertising | Several signers with different hashes wrote `micalg=pgp-sha256,pgp-sha512` unquoted; a comma is a tspecial (RFC 2045 §5.1), so a reader sees only the first hash and our own reader reports `MicalgMismatch` | The value is quoted when it holds a comma |
| 6 | note | `PgpMimeEncryptor.Encrypt` did not check a caller's `Boundary` against the ciphertext (the signer did) | Throws `ArgumentException` |

### Checked and held

* **Coverage.** The signed bytes are `signedPart.ToArray()` of the entity as received, never a re-serialisation; exactly two parts; a second part that is not a leaf fails; an OpenPGP entity inside `multipart/mixed` is flagged and the result is not `Succeeded` (the wrapper's other parts are listed). The signed content of a signature that fails is still returned in `Content` for display, but `Succeeded`, `IsAuthenticated`, `HeadersProtected` and the merged headers all say no. Written messages: awkward content (header lines with trailing white space, a preamble and epilogue, 8-bit and binary leaves, a line starting `From `, bare LF) verifies after a reparse **with no repair issue**, so the canonical form we sign is the form we send.
* **Decryption.** A flipped character anywhere in the ciphertext of an SEIPD v1 (326 positions) or v2 (405) message gives `DecryptionFailed` with the same detail text; the only other statuses come from the packet headers in front of the ciphertext, which carry no secret. Cutting 1 to 40 bytes off the end (the MDC, or the AEAD tag and final chunk), adding a literal packet after, or putting one before, never yields a successful layer or the added text. Old unauthenticated encryption is refused unless `AllowUnauthenticatedEncryption` (OneCrypto's own tests build those packets). The plaintext is not shown on any failure: it is discarded with the buffer.
* **Identity and time.** Hard revocation ("compromised") after signing invalidates the earlier signature; a soft one does not; a signature dated after `Now` is not valid; `Now` is never read from a clock. A User ID revoked before the signature stops matching the sender.
* **Autocrypt.** An `Autocrypt` header whose `addr` is not the From address is not selected; two From headers, or two valid headers, select none.
* **Limits.** 12 nested layers, 5 MB of armor, 3,000 parts, 3,000 nested multiparts and an over-limit plaintext end in statuses (existing tests); the fuzz target now also builds an unsigned encrypted message with protected headers, a signed part beside an unsigned one, and text around a cleartext signature, and checks that headers are never protected without authentication and that `IsSenderAuthenticated` implies a matching valid signer.

### Open, by design or outside this package

| Severity | Item |
|---|---|
| note | **Sign-then-encrypt is not bound to the recipient** (surreptitious forwarding): a recipient can re-encrypt a signed message to someone else and it still verifies as the original author's. The signature covers the entity, not the envelope. Writing the protected `To` (`ProtectedHeaders`) lets a reader compare it, and `Open` does (`ProtectedRecipientMismatch`, PG15) |
| note | **Inline text around a cleartext signature** (PG11) leaves `Succeeded` true. Only `Content` is signed; a UI that shows the original body next to a "verified" badge shows unsigned text as well. Show `Content`, and treat `InlineTextOutsideArmor` as "partly unsigned" |
| note | **Repairs show bytes that were not signed** (PG4): after the line-ending or trailing-white-space repair, `Content` holds the received bytes and `CoveredBytes` the repaired ones. Trailing white space is meaningful in `format=flowed` text |
| note | **Not normalised when signing:** trailing white space in a quoted-printable body, on a boundary line, or in a preamble or epilogue survives into the signed bytes and a relay that strips it breaks the signature. We never produce such bodies ourselves |
| note | **Bcc recipients are visible** to every recipient through the key IDs in the public-key encrypted session key packets. `RecipientAddresses` includes `Bcc`; encrypt a separate message to each Bcc recipient. OneCrypto's writer has no hidden-recipient option (its reader accepts a zero key ID) |
| note | **Depth limit:** when `MaxDepth` layers are used up, the content below is returned unprocessed with `DepthLimitReached` in `Issues`; `Succeeded` does not change. Anything a valid signature covers stays covered |
| note | **Gossip:** `PgpAutocryptInfo.Gossip` lists the headers of the entity it is given. Gossip is trustworthy only from an authenticated encrypted payload and for addresses among that message's recipients; the caller checks both |
| note | **Weak algorithms on read** are accepted and flagged (`WeakAlgorithms`); a SHA-1 signature before the policy's reject date is `IsAuthenticated`. Rejecting them is `PgpOpenOptions.Policy` |
| note | **Memory:** decrypted plaintext, the armored input and the passwords the caller passes live in ordinary arrays and are not cleared. Session keys are OneCrypto's |
| note | Inline block search stops at the first `-----END PGP SIGNATURE-----` substring, including inside a dash-escaped line of the signed text; such a message fails verification rather than verifying differently |
| untested | Encrypting to a recipient with a weak key (for example RSA 1024) is refused by OneCrypto's policy; no such certificate can be generated here, so the mail path's refusal is not exercised |
