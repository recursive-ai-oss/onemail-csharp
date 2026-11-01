# S/MIME interoperability tools

Black-box checks of `RecursiveAI.OneMail.Cryptography.Smime` against other implementations. Released builds only; their source is not read.

- `run.sh <work dir> [corpus dir]` builds `SmimeInterop` and runs three things.
  - **interop**: this library writes signed (detached and opaque; RSA PKCS #1, RSA-PSS, ECDSA P-256, Ed25519), encrypted (EnvelopedData CBC and AuthEnvelopedData GCM; RSA-OAEP, RSA PKCS #1 v1.5, ECDH P-256) and nested messages and `openssl cms` opens them; `openssl cms` writes the same set and this library opens them with the chain checked against the test CA. MimeKit 4.18.2 (RSA, RSA-PSS, ECDSA, enveloped with OAEP and PKCS #1 v1.5, Triple DES) does the same through its public API. A cell OpenSSL cannot produce is recorded as `oracle cannot:` with its message.
  - **rfc9216**: signs and encrypts with the example keys and certificate authorities of RFC 9216 (Alice and Bob RSA, Carlos and Dana Ed25519 and X25519), validates the chains to the published roots and opens the messages. The RFC text is fetched (sha256 checked), not committed.
  - **scan**: counts S/MIME-class Outlook items and S/MIME MIME messages in the MSG, PST, OST and message corpora and what the processor makes of them.
- `SmimeInterop <work dir>`, `SmimeInterop rfc9216 <rfc9216.txt>`, `SmimeInterop scan <paths...>` run the parts by hand. Set `ONEMAIL_OPENSSL` to use a particular `openssl`.

Results and versions are in `docs/compatibility.md`; behaviour is in `docs/smime.md`.
