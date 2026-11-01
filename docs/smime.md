# S/MIME (`RecursiveAI.OneMail.Cryptography.Smime`)

Normative notes for signing, verifying, encrypting and decrypting mail with S/MIME 4.0 (RFC 8551, with RFC 8550 for certificates, RFC 1847 for `multipart/signed`, RFC 5652 / RFC 5083 for the CMS), and for Outlook's S/MIME items (MS-OXOSMIME). **All CMS work is OneCrypto's** (`RecursiveAI.OneCrypto` 0.1.0-preview.1): the CMS structures, signatures, key transport and agreement, content encryption, certificate validation and the crypto policy. This package does the mail side: which bytes are signed, how the MIME is framed, what the structure of a message means and what lies outside the signature. The package has not had an independent security review.

## What it does

| Area | Types |
|---|---|
| Sign | `SmimeSigner.SignDetached` → `multipart/signed`; `SmimeSigner.SignOpaque` → `application/pkcs7-mime; smime-type=signed-data`; options in `SmimeSignOptions` |
| Encrypt | `SmimeEncryptor.Encrypt` → `enveloped-data` or `authEnveloped-data`; options in `SmimeEncryptOptions` |
| Open | `SmimeProcessor.Open(MimeEntity or bytes, SmimeOpenOptions)` → `SmimeResult` (layers outermost first, content reached, inner message in the shared view, what is outside the signature) |
| Outlook | `SmimeOutlook.Open(MapiMessage)` for items of class `IPM.Note.SMIME` / `IPM.Note.SMIME.MultipartSigned` (MSG, PST, OST) |

Keys and certificates are passed in by the caller: `CmsSignerSpec` (certificate plus a OneCrypto key or `X509Signer`), `Certificate` lists for recipients, `CmsDecryptionKey` for decryption. The operating-system store is never consulted, no trust is implied and no clock is read.

## Signing

* **Canonical form (RFC 8551 §3.1.1).** The entity to sign is copied; every leaf with a body that is binary, or that holds a byte above 0x7F or a NUL, is re-encoded (quoted-printable for `text/*`, base64 otherwise); the copy is serialised with CRLF line ends. An entity that is already 7-bit with CRLF line ends keeps every stored byte. The signature covers exactly the bytes of the first part of the `multipart/signed`, header block and body, and that part is written as parsed bytes so no later step can change them.
* **Detached.** `protocol="application/pkcs7-signature"`, `micalg` from the signers' digests (`sha-256`, `sha-384`, `sha-512`, `sha-224`, `sha-1`, `md5`; several distinct digests are comma separated; `unknown` for an algorithm without a registered token), the signature as `smime.p7s` in base64. The boundary is random and checked against the signed bytes, or caller-supplied (an error if it occurs in the content).
* **Opaque.** `application/pkcs7-mime; smime-type=signed-data; name="smime.p7m"`, content inside the CMS.
* **Attributes.** OneCrypto's defaults (content type, message digest, ESS signing-certificate-v2) plus, unless switched off, SMIMECapabilities (AES-256-GCM, AES-128-GCM, AES-256-CBC, AES-128-CBC; RFC 8551 §2.5.2) and SMIMEEncryptionKeyPreference (the signer's certificate, issuer and serial number) when that certificate can receive encrypted mail. The signing time is written only when the caller gives one.
* **Algorithms.** RSA PKCS #1 v1.5, RSASSA-PSS, ECDSA and Ed25519, as OneCrypto offers them (`CmsSignerSpec.SignatureSpec`). **New signatures refuse weak algorithms**: SHA-1 and MD5 digests and RSA keys under 2048 bits raise `PolicyViolationException` under the default policy (`SmimeSignOptions.Policy` null is `CryptoPolicy.Standard`).

## Encrypting

* RSA recipients get key transport, RSA-OAEP with SHA-256 by default (`KeyTransportScheme`; PKCS #1 v1.5 only with `AllowLegacy`); EC, X25519 and X448 recipients get key agreement (ECDH with the X9.63 KDF, RFC 5753; X25519/X448 with HKDF, RFC 8418). A certificate with another kind of key is an `ArgumentException`.
* `Enveloped` (default) is AES-256-CBC, which every client reads and which has no integrity protection of its own (reading reports `UnauthenticatedEncryption`); `AuthEnveloped` is AES-256-GCM (RFC 5083, RFC 8551 §2.7). Pick another cipher by OID in `ContentEncryption`.
* The content is the canonical form above. To sign and encrypt, encrypt the result of a signing call.
* New encryption refuses RSA recipient keys under 2048 bits; `Standard` flags but writes CBC, `Strict` refuses it.

## Opening: the result

`SmimeProcessor.Open` never throws on a message (only on a policy given without a reference time, or the reverse). It unwraps layers one inside another, up to `MaxDepth` (default 8): `multipart/signed`, `application/pkcs7-mime` and `application/x-pkcs7-mime` with `smime-type` signed-data, enveloped-data, authEnveloped-data, compressed-data, certs-only, and `application/octet-stream` with a `.p7m` name (MS-OXOSMIME §2.1.3.2.1.1). If the root is a multipart wrapper that is not S/MIME, the first S/MIME entity below it is processed.

`SmimeResult`:

| Member | Meaning |
|---|---|
| `Layers` | each layer with `Kind`, `Status`, `Succeeded`, `Issues`, `Detail` and the entity it came from |
| `Content`, `InnerItem` | the innermost entity reached; the inner message in the shared view (`IMailItem`) |
| `Succeeded` | every layer succeeded; separate from trust |
| `Signers` | all signers of all signed layers |
| `Outside` | `UnsignedHeaders` (outermost headers, which a `multipart/signed` never covers), `UnsignedParts` (paths of parts beside a signed part inside a wrapper), `ProtectedHeaders` (headers a signed `message/rfc822` carries) |

A signed layer (`SmimeSignedLayer`) adds `Signers`, OneCrypto's whole `SignedDataReport`, `CoveredBytes` (the exact bytes checked), `CoveredSourceOffset`/`CoveredSourceLength` (where the signed part sits in the file it was read from), `Micalg`, `SignedEntity` and `Certificates`. Each `SmimeSignerResult` has:

* `Status` (see below) and `Report`, OneCrypto's per-rule `SignerReport` (`signer-certificate`, `digest-algorithm`, `attributes`, `content-type`, `message-digest`, `signing-time`, `signature`, `algorithm-protection`, `key-usage`, `key-strength`, `signing-certificate`, `policy`, `chain`, `countersignatures`), with every check's status, reason and reference.
* `Chain`: `NotChecked` unless the caller passes `SmimeOpenOptions.Chain` (OneCrypto's `ValidationOptions`: anchors, reference time, revocation data). Nothing is trusted implicitly and the operating-system store is never used. A `Valid` signer without a chain check proves only that the holder of the certificate's key signed.
* `SenderMatch`: the certificate's e-mail addresses (rfc822Name entries of the subject alternative name, then the subject's emailAddress) against `From`, or `Sender` when there is no `From`, compared ignoring case (RFC 8550 §3 and §4.4.1); `NoSenderAddress`, `NoCertificateAddress`, `Match`, `Mismatch` or `NoCertificate`. The address comes from the outermost entity that has one (for an Outlook item, from the item's sender).
* `WeakAlgorithms`: tags such as `digest:sha-1`, `key:weak`, `eku:no-emailProtection` (an extended key usage that does not allow e-mail protection, RFC 8550 §4.4.4), and the rule ids of flagged crypto-policy decisions. Reading accepts these; it never rewrites them as failures unless the caller's policy rejects them.
* `Capabilities` (OIDs of SMIMECapabilities) and `HasEncryptionKeyPreference`.

`SmimeStatus`: signer values are `Valid`, `ContentModified` (the message-digest attribute does not match: the covered bytes changed), `SignatureInvalid`, `CertificateNotFound`, `PolicyRejected`, `Unsupported`, `RuleFailed` (another rule, such as key usage) and `UntrustedChain` (sound signature, certificate does not validate or could not be decided). Layer values add `Malformed` (damaged or truncated CMS, not CMS), `StructureInvalid`, `Decrypted`, `Decompressed`, `NoMatchingRecipient`, `DecryptionFailed`, `NoDecryptionKey`, `LimitExceeded` and `CertificatesOnly`.

### Verification rules this package adds to OneCrypto's

* **Exactly two parts** in a `multipart/signed` (RFC 1847 §2.1). A third part is `StructureInvalid`: that is how unsigned content is smuggled next to a valid signature.
* A detached signature must not carry content of its own; if its CMS has encapsulated content the layer is `StructureInvalid` (a verifier that preferred it would show text other than the text it checked).
* **Line ends.** The stored bytes of the signed part are checked first. Only if the message-digest check fails and those bytes hold a bare LF or CR is the CRLF form checked; if that verifies the layer is `Valid` with `SignedPartLineEndingsNormalised`. See ambiguity SM4.
* `micalg` is advisory: a missing or wrong value is an issue (`MicalgMissing`, `MicalgMismatch`), never a failure.
* A decryption failure after a recipient matched is one status, `DecryptionFailed`, whatever the cause (OneCrypto's single-error rule, RFC 3218 §2.3). `NoMatchingRecipient` is told apart because it is a property of the public message. AuthEnveloped content is released only after the tag verifies; CBC cannot be checked beyond the padding.
* Legacy ciphers (Triple DES) are read only with `AllowLegacyCiphers`; otherwise the layer is `Unsupported`. RSA PKCS #1 v1.5 key transport is read by default (`AllowPkcs1V15KeyTransport`), with the RFC 3218 protections.

### Limits

`MaxDepth` (default 8) bounds nesting. `Limits` is OneCrypto's `CmsLimits` (signers 64, certificates 1024, recipients 1024, attribute sizes, decompression ratio 1000 and output 1 GiB, content 1 GiB for one-shot decrypts). Messages are processed in memory: an S/MIME message is as large as the bytes passed in plus the unwrapped content.

## Outlook items (MS-OXOSMIME)

`IPM.Note.SMIME.MultipartSigned` holds one attachment whose data is the `multipart/signed` entity with only its `Content-Type` header (§2.1.3.1): the signed bytes inside it are the original ones, so the signature verifies after a round trip through MSG or PST. `IPM.Note.SMIME` holds one attachment whose data is the raw CMS (transfer encoding removed, §2.1.3.2), and the `Content-Type` of the original entity in the named property `Content-Type` of the property set `PS_INTERNET_HEADERS`; without it the type comes from `PidTagAttachMimeTag` and the file name (§2.1.3.2.2.3). `SmimeOutlook.ToEntity` rebuilds the Internet entity (adding a `From` from the item's sender) and `SmimeOutlook.Open` runs it through `SmimeProcessor`, so wire messages, MSG and PST items share one path. The result's `InnerItem` is the decrypted or verified message in the shared view. A class ending in `.SMIME` or `.SMIME.MultipartSigned` counts (§2.1.3.1.2.2, §2.1.3.2.2.2). The first attachment that holds data is used when there is more than one.

## Ambiguities

Eight ambiguities (SM1 to SM8) are in the S/MIME section of [`ambiguities.md`](ambiguities.md), each with its resolution and the defect or issue it raises. They are agent defaults: no mail client has been observed for them yet (OpenSSL and MimeKit were observed for the interop and do not settle these cases).

## Interop and tests

`tools/smime/` holds the harness; `docs/compatibility.md` has the results. OpenSSL (`openssl cms`, run as a binary) and MimeKit 4.18.2 (the released package, public API only) open what this package writes, and this package opens what they write; RFC 9216's published example keys and certificate authorities sign, encrypt and validate. Tamper classes (a changed body byte, a changed signed header, a third part, a swapped signature, a damaged signature value, a replaced certificate, truncated and garbage CMS, wrong recipient, flipped ciphertext) end as statuses, never exceptions.
