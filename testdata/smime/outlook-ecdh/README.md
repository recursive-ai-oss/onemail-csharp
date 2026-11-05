# Windows-made ECDH S/MIME messages

`windows-*.eml` are `EnvelopedData` messages (`application/pkcs7-mime`, `smime-type=enveloped-data`) that Windows' own CMS encoder (the `CryptMsgOpenToEncode` call that Outlook's S/MIME goes through) wrote for the throw-away P-256 key in `cert.pem` / `key.pem` (a test key). The content is a `text/plain` entity whose text is `windows made body` (`expected.txt`).

| File | Key-agreement scheme | Key wrap | Recipient identifier | UKM |
|---|---|---|---|---|
| `windows-sha1-a256` | dhSinglePass-stdDH-sha1kdf | AES-256 | issuer and serial | no |
| `windows-sha256-a256` | dhSinglePass-stdDH-sha256kdf | AES-256 | issuer and serial | no |
| `windows-sha1-a256-ukm` | dhSinglePass-stdDH-sha1kdf | AES-256 | issuer and serial | 32 bytes |
| `windows-sha1-a256-ski` | dhSinglePass-stdDH-sha1kdf | AES-256 | subject key identifier | no |

Windows writes the originator key's algorithm identifier and the key-wrap algorithm identifier with `NULL` parameters. The generator is `tools/outlook/Make-EcdhWindows.ps1`, which has to run on Windows.
