# DKIM and ARC interoperability tools

Black-box checks of `RecursiveAI.OneMail.Cryptography` against other implementations, run as released binaries only (their source is not read).

- `setup.sh` installs dkimpy 1.1.8 (with PyNaCl and PyYAML, in a `uv` venv), Node 22.22.1 and mailauth 8.0.4 under `~/onemail/oracles/dkim`.
- `run.sh <work dir>` builds `DkimInterop`, makes 320 cases, signs them with this library, has dkimpy and mailauth verify them, has each of them sign the unsigned messages, and verifies those here. Counts per direction are printed.
- `DkimInterop arcgen|arcverify|arcover` and `mailauth_arc.js`, `dkimpy_arc.py` do the same for ARC chains.
- `fetch-arc-suite.sh` fetches the ARC test suite's scenarios (MIT licence, data files only, pinned commit) and converts them to JSON; `DkimInterop arcsuite <suite.json>` runs the validation scenarios here, and `arc_suite_oracles.py` / `arc_suite_mailauth.js` run them through the other two for comparison.
- `fuzz-batch.sh` runs the `dkim-tags`, `dkim-key` and `dns-reply` fuzz targets with seed corpora.

Nothing fetched is committed. See `docs/dkim-arc.md` for results.
