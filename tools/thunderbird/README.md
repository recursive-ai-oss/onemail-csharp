# Thunderbird interoperability (OpenPGP)

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
