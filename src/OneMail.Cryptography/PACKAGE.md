# OneMail.Cryptography

S/MIME, DKIM and ARC for .NET. S/MIME (RFC 8551) signs, verifies, encrypts and decrypts messages on the Cryptographic Message Syntax of [OneCrypto](https://github.com/recursive-ai-oss/onecrypto-csharp); DKIM and ARC sign and verify mail authentication signatures with RSA (the .NET base class library) and Ed25519 (OneCrypto), with a small DNS TXT client for key records. It is part of [OneMail](https://github.com/recursive-ai-oss/onemail-csharp).

**Early preview.** Native platforms only (not browser WebAssembly). The API is not stable yet, and the package has not had an independent security review.

## S/MIME

```csharp
using RecursiveAI.OneCrypto;
using RecursiveAI.OneMail.Cryptography.Smime;
using RecursiveAI.OneMail.Mime;

// Sign: multipart/signed with a detached signature (or SignOpaque for application/pkcs7-mime).
MimeMultipart signed = SmimeSigner.SignDetached(body, new SmimeSignOptions
{
    Signers = [new CmsSignerSpec { Certificate = cert, Key = key }],
});

// Encrypt for the recipients' certificates (AES-GCM with SmimeEncryptionKind.AuthEnveloped).
MimePart secret = SmimeEncryptor.Encrypt(signed, new SmimeEncryptOptions { Recipients = [bobCert] });

// Open: unwraps signed, encrypted and nested layers and reports on each.
SmimeResult result = SmimeProcessor.Open(File.ReadAllBytes("message.eml"), new SmimeOpenOptions
{
    DecryptionKeys = [CmsDecryptionKey.FromRsa(rsa, bobCert)],
    Chain = new ValidationOptions { TrustAnchors = [TrustAnchor.FromCertificate(root)], ReferenceTime = now },
});
foreach (SmimeSignerResult s in result.Signers)
{
    Console.WriteLine($"{s.Status} chain={s.Chain} sender={s.SenderMatch} weak=[{string.Join(",", s.WeakAlgorithms)}]");
}
```

- Results are structured: every layer and signer has a status, OneCrypto's per-rule checks, the exact covered bytes, what of the message is outside the signature, whether the certificate matches the sender, and weak-algorithm flags. Nothing is a bare yes/no.
- No trust is implied: without `Chain` options no chain is built and the operating-system store is never used, and a signed message then ends as `UntrustedChain` (`Succeeded` is false) because anyone can sign with a certificate they made themselves. Set `AllowUntrustedSigner` to judge the certificate yourself; `IsTrusted` says a chain to your anchor was built. The library never reads a clock.
- New signatures and encryption refuse SHA-1, MD5 and RSA under 2048 bits; reading accepts and flags them. Triple DES is read only with `AllowLegacyCiphers`.
- Outlook S/MIME items (`IPM.Note.SMIME`, `IPM.Note.SMIME.MultipartSigned`) open through `SmimeOutlook.Open`.

Details and the interop results: <https://github.com/recursive-ai-oss/onemail-csharp/blob/main/docs/smime.md>.

## DKIM and ARC

```csharp
using RecursiveAI.OneMail.Cryptography.Dkim;
using RecursiveAI.OneMail.Cryptography.Dns;

var verifier = new DkimVerifier(new DkimVerifierOptions { Resolver = new DnsTxtClient() });
foreach (DkimSignatureResult r in await verifier.VerifyAsync(File.ReadAllBytes("message.eml")))
{
    Console.WriteLine(r);   // dkim=pass header.d=example.com header.s=sel header.b=AbCdEfGh
}

using var key = DkimSigningKey.FromPem(File.ReadAllText("private.pem"));
byte[] signed = new DkimSigner(key, new DkimSignerOptions { Domain = "example.com", Selector = "sel" })
    .Sign(File.ReadAllBytes("message.eml"));
```

- Every result says why: status, reason, domain, selector, algorithm, key size, covered headers and body ranges, whether `l=` leaves content unsigned, time validity and weak-algorithm flags. Nothing is a bare yes/no.
- New signatures refuse RSA under 2048 bits and `rsa-sha1`. Verification accepts RSA from 1024 bits and `rsa-sha1` (RFC 8301) and flags them; set `AllowRsaSha1 = false` to refuse.
- ARC (RFC 8617): `ArcVerifier` validates a chain and reports every set and the oldest-pass value; `ArcSealer` adds a set.
- Resolvers are pluggable (`ITxtResolver`); `DnsTxtClient` speaks UDP with TCP fallback, treats replies as hostile input, and does no DNSSEC.
- Canonicalisation works on the message's own bytes. Bare LF line ends are treated as the CRLF they would be on the wire.

Details and the interop results: <https://github.com/recursive-ai-oss/onemail-csharp/blob/main/docs/dkim-arc.md>. Licence: MIT OR Apache-2.0.

This project is unaffiliated with other products named OneMail.
