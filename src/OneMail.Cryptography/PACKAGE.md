# OneMail.Cryptography

DKIM and ARC for .NET: sign and verify mail authentication signatures with RSA (the .NET base class library) and Ed25519 ([OneCrypto](https://github.com/recursive-ai-oss/onecrypto-csharp)), plus a small DNS TXT client for key records. It is part of [OneMail](https://github.com/recursive-ai-oss/onemail-csharp).

**Early preview.** Native platforms only (not browser WebAssembly). The API is not stable yet, and the package has not had an independent security review.

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
