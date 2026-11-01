# SASL (`RecursiveAI.OneMail.Client.Sasl`)

One mechanism object per authentication attempt; the same objects serve SMTP (RFC 4954), IMAP (RFC 3501 `AUTHENTICATE`, with SASL-IR, RFC 4959) and POP3 (RFC 5034). The protocol layer carries the bytes (base64 on the wire, `=` for an empty initial response, `*` to cancel); a mechanism only turns challenges into responses (`GetInitialResponse`, `Respond`, `Reset`). `RequiresEncryption` and `IsLegacy` tell the selection what to avoid.

| Mechanism | Class | Notes |
|---|---|---|
| PLAIN (RFC 4616) | `SaslPlain` | needs an encrypted connection |
| LOGIN | `SaslLogin` | not an RFC; two prompts answered in order; legacy; needs an encrypted connection |
| CRAM-MD5 (RFC 2195) | `SaslCramMd5` | legacy: HMAC-MD5 lets a recorded exchange be guessed offline; works without TLS |
| SCRAM-SHA-1, SCRAM-SHA-256 (RFC 5802, 7677) and `-PLUS` | `SaslScram` | the client checks the server's proof; iteration count limited to 1,048,576; password through SASLprep (NFKC, mapped spaces and "mapped to nothing" characters, control characters refused; the full prohibited-code-point tables are not applied) |
| XOAUTH2 | `SaslXOAuth2` | bearer token supplied by the caller |
| OAUTHBEARER (RFC 7628) | `SaslOAuthBearer` | bearer token supplied by the caller; `host`/`port` sent when given |
| NTLM (MS-NLMP) | `SaslNtlm` | NTLMv2 only (never LM or NTLMv1); our own `Md4` for the password hash; no signing or sealing negotiated; legacy; needs an encrypted connection |
| GSSAPI (RFC 4752), GSS-SPNEGO | `SaslNegotiate` | through `System.Net.Security.NegotiateAuthentication`; no security layer (TLS protects the session); needs the platform's Kerberos stack |

## Weak primitives

CRAM-MD5 and NTLMv2 are defined on HMAC-MD5, and SCRAM-SHA-1 on HMAC-SHA-1. The analyzer rules against MD5 and SHA-1 (CA5350, CA5351) are suppressed in one place, the internal `ProtocolHashes` class, and nowhere else; no other code in the library uses MD5 or SHA-1. Mechanism selection never prefers these over a stronger mechanism the server offers.

## Channel binding

`-PLUS` mechanisms bind the login to the TLS session. The binding is `tls-server-end-point` (RFC 5929 §4): the hash of the server certificate, using the hash of the certificate's signature algorithm with MD5 and SHA-1 replaced by SHA-256 (`SaslChannelBinding.From(connection)`). `tls-exporter` (RFC 9266), which servers should prefer for TLS 1.3, needs the exporter secret, which `SslStream` does not expose, so it is not offered; `tls-unique` is not used (TLS 1.2 only). When the connection has a binding but the server did not offer the `-PLUS` form, the plain SCRAM mechanism announces `y` (RFC 5802 §6) so a server that supports binding can detect that the offer was stripped.

## Selection

`SaslMechanisms.Select(offered, credentials, connection, service, host, port)` returns the mechanisms to try, strongest first, from what the server offered and what `SaslCredentials` holds:

1. SCRAM-SHA-256-PLUS, SCRAM-SHA-1-PLUS (password and a TLS connection)
2. OAUTHBEARER, XOAUTH2 (a user name and an `AccessToken`)
3. SCRAM-SHA-256, SCRAM-SHA-1
4. GSSAPI, GSS-SPNEGO (`UsePlatformCredentials`)
5. NTLM, CRAM-MD5 (legacy; `AllowLegacy`, default true)
6. PLAIN, LOGIN

A mechanism that needs encryption is left out on an unencrypted connection unless `AllowInsecure` is set. If the list is empty the caller gets an error naming what the server offered. Token acquisition and refresh are the caller's job.

## Test vectors

`tests/OneMail.Tests/Client/SaslTests.cs`: RFC 1320 (MD4, the seven test suite strings), RFC 2195 (CRAM-MD5), RFC 5802 §5 (SCRAM-SHA-1 client messages and server signature), RFC 7677 §3 (SCRAM-SHA-256), MS-NLMP §4.2.4 (NTLMv2: NTOWFv2 input, LMv2 response, NTProofStr and the `temp` blob from the example challenge), plus hostile challenges for SCRAM and NTLM. The fuzz target `sasl-challenge` feeds server messages to every mechanism; only `SaslException` may come out.

## Provider notes

The settings below are what the providers publish; they were not exercised against live accounts. Basic (password) sign-in is being withdrawn by several providers, so prefer tokens where they offer them.

- **Gmail** (`smtp.gmail.com`, 465 implicit TLS or 587 STARTTLS; IMAP `imap.gmail.com:993`): OAuth 2.0 with the scope `https://mail.google.com/` and XOAUTH2 (or OAUTHBEARER). App passwords (an account with 2-step verification) work with PLAIN/LOGIN.
- **Outlook.com and Microsoft 365** (`smtp.office365.com:587` STARTTLS; IMAP `outlook.office365.com:993`): OAuth 2.0 through Microsoft Entra ID with XOAUTH2, scopes `https://outlook.office.com/SMTP.Send` and `https://outlook.office.com/IMAP.AccessAsUser.All` (plus `offline_access` for refresh tokens). Basic authentication is disabled for most tenants and for outlook.com personal accounts.
- **Yahoo Mail** (`smtp.mail.yahoo.com`, 465/587; IMAP `imap.mail.yahoo.com:993`): app passwords (account security page) with PLAIN or LOGIN; OAuth is limited to approved partners.
- **iCloud Mail** (`smtp.mail.me.com:587` STARTTLS; IMAP `imap.mail.me.com:993`): app-specific passwords with PLAIN or LOGIN; the user name is the full iCloud address.

Pass the token as `SaslCredentials.AccessToken` with `UserName` set to the mailbox address, and the client offers OAUTHBEARER or XOAUTH2, whichever the server lists.
