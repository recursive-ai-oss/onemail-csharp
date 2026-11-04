# Thunderbird interoperability (OpenPGP and S/MIME)

Checks that Thunderbird's built-in OpenPGP opens the PGP/MIME messages OneMail writes, and that OneMail opens the ones
Thunderbird writes. Thunderbird runs as the released Mozilla tarball (version and SHA-256 pinned in `setup.sh`), headless,
with no display; it is controlled through Marionette (`marionette.py`) and nothing of its source is used.

```
bash tools/thunderbird/run.sh          # setup + all four checks; GPG=/path/to/gpg adds the whitespace probe
```

| Command | What it does |
|---|---|
| `interop.py ours VECTORS OUT` | opens every message under `VECTORS/<origin>/<key>/` in a local Inbox and reads the signature and encryption state from the message pane |
| `interop.py theirs VECTORS OUT` | has Thunderbird sign, encrypt and sign+encrypt to the key's own address; the mail goes to a local SMTP sink and into `OUT/thunderbird/<key>/` |
| `interop.py correspondent VECTORS OUT` | opens our signed messages with only the public key imported, not accepted and accepted |
| `interop.py whitespace VECTORS OUT` | GnuPG-signed variants (trailing spaces, a `From ` line, a bare LF) to see what Thunderbird verifies |

Output lines are `RESULT <check> ...` (`ok`, `FAIL`, `EXPLAINED`, `UNSUPPORTED`) and a `TOTAL`; screenshots of each message
are in `OUT/shots`. Copy `OUT/thunderbird/*` to `testdata/openpgp/vectors/thunderbird` to refresh the committed messages
(`PgpInteropVectors` reads them back without Thunderbird). Everything stays in a throwaway profile under `WORK`.

## S/MIME

`smime.py` does the same for S/MIME with the vectors `tools/smime` writes (`SmimeInterop thunderbird <dir>`; committed in `testdata/smime/vectors`). The CA and the user's PKCS #12 file are imported through Thunderbird's certificate database interface, the CA trusted for email.

| Command | What it does |
|---|---|
| `smime.py ours VECTORS OUT` | opens every message under `VECTORS/ours/<key>/` and reads the signature and encryption icons, the signer address and the body |
| `smime.py untrusted VECTORS OUT` | the signed message with the CA imported but not trusted for email: the status must not be good |
| `smime.py theirs VECTORS OUT` | has Thunderbird sign, encrypt and sign+encrypt to the key's own address; the messages land in `OUT/thunderbird/<key>/` (copy to `testdata/smime/vectors/thunderbird`) |
