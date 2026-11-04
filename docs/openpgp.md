# OpenPGP mail (`RecursiveAI.OneMail.Cryptography.OpenPgp`)

Normative notes for signing, verifying, encrypting and decrypting mail with OpenPGP: PGP/MIME (RFC 3156), inline OpenPGP in text bodies (RFC 9580 §6 armor and §7 cleartext signatures, RFC 4880 for the older form), Autocrypt headers (Autocrypt Level 1) and protected headers. **All OpenPGP work is OneCrypto's** (`RecursiveAI.OneCrypto.OpenPgp` 0.4.0-preview.1): packets, keys and certificate validity, signatures, session keys, symmetric and AEAD encryption, compression, the crypto policy. This package does the mail side: which bytes are signed, how the MIME is framed, what the structure of a message means, and what lies outside the protection. The package has not had an independent security review; the OpenPGP path is due one.

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
| `IsAuthenticated` | `Succeeded` and at least one signature is valid. **An encrypted message with no signature is never authenticated**: decryption is not verification (RFC 9580 §13) |
| `HeadersProtected` | authenticated, and the protected content repeats headers (`Outside.ProtectedHeaders`) |
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
| PG9 | the signer's user IDs do not name the `From` address | Reported (`UserIdMatchesSender` false), not enforced: the sender check belongs to the caller | |
| PG10 | inline: armor in a quoted-printable body whose escapes the transfer encoding did not account for | Escapes decoded, then read | `InlineQuotedPrintableRepaired` |
| PG11 | inline: block surrounded by other text, or every line carrying the same quote prefix | The block is cut out; the prefix removed | `InlineTextOutsideArmor`, `InlineQuotePrefixRemoved` |
| PG12 | inline: cleartext signature that verifies only after the text is converted from the declared charset to UTF-8 | Accepted; the signature covers the text as signed, and a gateway that transcoded it changed the bytes | `InlineCharsetReencoded` |
| PG13 | protected headers (`protected-headers` on the decrypted top-level entity, or a `message/rfc822` inside) | Counted as protected only when some layer succeeded; they replace the outer headers of the same name in `InnerItem` | `ProtectedHeadersPresent`; otherwise `HeadersNotProtected` |

## Inline OpenPGP

A text body (`text/*` except `text/html`, or `application/pgp`) that holds `-----BEGIN PGP SIGNED MESSAGE-----` (cleartext signature, RFC 9580 §7: dash-escaping, `Hash:` header, trailing white space ignored) or `-----BEGIN PGP MESSAGE-----` (encrypted, signed, or both) is read as a layer of kind `InlineCleartextSigned` or `InlineMessage`. The bytes handed to OneCrypto are the transfer-decoded body, not a charset conversion; the result's `Content` is a new `text/plain` part with the verified or decrypted text, UTF-8 when it is valid UTF-8, otherwise the declared charset. Repairs are tried in order (as received, quoted-printable escapes decoded, charset converted) and the first reading that verifies or decrypts wins; each repair is an issue. HTML bodies are not searched, and per-attachment inline encryption (`.pgp` attachments) is not read.

## Protected headers and Autocrypt

* **Protected headers** (the "memory hole", draft-autocrypt-lamps-protected-headers): read. A decrypted or signed top-level entity whose `Content-Type` has `protected-headers`, or a `message/rfc822` inside the protection, lists the headers it repeats in `Outside.ProtectedHeaders`; `InnerItem` shows them over the outer ones. Writing protected headers is not built: put the header lines in the entity you encrypt and add the parameter.
* **Autocrypt** (Level 1): `PgpAutocrypt.Read` returns every `Autocrypt` header of the message (valid or with OneCrypto's reason), the one Autocrypt would use (`Selected`: exactly one valid header naming the From address), and the `Autocrypt-Gossip` headers of the entity read (pass the decrypted entity; gossip counts only from an encrypted payload). `PgpAutocrypt.Describe` turns a message into OneCrypto's `AutocryptMessage` for `AutocryptPeer.Apply`. Peer storage and the recommendation are the caller's, with OneCrypto's `AutocryptPeer` and `AutocryptRecommender`.

## Keys

`IPgpKeySource.FindAsync(address)` is what a caller implements over its keyring, a GnuPG home read by OneCrypto, or WKD. `PgpKeySource.FromCertificates` is a list source that matches the address in each user ID; `ResolveRecipientsAsync` returns, per address, a certificate that can encrypt at the reference time, and the addresses with none; `RecipientAddresses` reads `To`, `Cc` and `Bcc`.

## Safety defaults

New signatures and encryption go through OneCrypto's standard policy: SHA-1, MD5 and short RSA keys are refused (tested). Reading accepts and flags them (`WeakAlgorithms`, `Policy`). Old Symmetrically Encrypted Data (no integrity check) is refused unless `AllowUnauthenticatedEncryption`, then flagged (`UnauthenticatedEncryption`, `Protection = None`). Decryption failures are one status. Decrypted output is bounded by `MaxPlaintextBytes` (default 256 MiB, `LimitExceeded`); OneCrypto bounds decompression (a ceiling of 1 MiB plus 1032 times the compressed octets read), key-derivation work, session-key packets and nesting. An inline body larger than `MaxInlineBytes` (64 MiB) is not searched. MIME depth is bounded by the MIME reader (64) and layers by `MaxDepth`.

## Checked

* **Unit tests** (`PgpMimeTests`, `PgpInlineTests`, `PgpHostileInputTests`): v4 EdDSA/ECDH, native v4, v6 and RSA-3072 keys, sign, encrypt, combined and nested forms; tampered byte, swapped signature, stripped signature, empty signature part, wrong and missing `micalg`, unknown signer, LF copy; QP, charset, surrounding text and quote prefix for inline; protected headers; hostile input (12 nested layers, 5 MB of armor, truncated cleartext, 3,000 parts, 3,000 nested multiparts, a decrypted size over the limit).
* **Interop**, run as released programs (`tools/openpgp/interop.sh`; the MIME parts are cut out of the messages by a splitter in the script, not by OneMail): GnuPG 2.4.4 (distribution package), GnuPG 2.5.24 (OneCrypto's pinned build), Sequoia `sqop` 0.38.0. Results are in `compatibility.md`; the messages are `testdata/openpgp/vectors/`, read back by `PgpInteropVectors` without any outside program.
* **Fuzz** `pgp-mime`: see `compatibility.md`.

## Not done

* Thunderbird (and other mail clients) have not opened these messages: no released Thunderbird could be run headless here to open a PGP/MIME file. It needs a Thunderbird release build, a display or `xvfb`, a profile with the test key imported through its OpenPGP settings, and a driver (Marionette or a mail-folder fixture plus screenshots).
* GnuPG 2.5.24 reads our v4 EdDSA/Cv25519 messages for verification but did not decrypt to the Cv25519 subkey after importing a OneCrypto-generated secret key (the subkey is listed as not available); GnuPG 2.4.4 and Sequoia do. Reported to OneCrypto.
* GnuPG does not read RFC 9580 version 6 keys or the native Ed25519/X25519 algorithms; those cells run against Sequoia only.
* Not built: HTML bodies, `.pgp` attachments, writing protected headers, WKD lookups (OneCrypto's online package does them; wire one to `IPgpKeySource`), inline signing and encrypting (write with OneCrypto's `PgpMessageWriter`).
