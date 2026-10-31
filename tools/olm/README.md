# tools/olm

Black-box checks of the Outlook for Mac archive (`.olm`) reader.

- `run.sh` runs OneMail and two released programs over the sample archives and generated ones and compares them.
- `make_olm.py` writes an archive from a seed (every item kind, nested folders, attachments, non-ASCII text); `testdata/olm/generated.olm` is `--seed 1 --messages 40 --folders 5`.
- `OlmDump.java` drives the olmreader 1.9.0 jar through its documented callbacks and prints what it delivers.
- `OlmTool/` dumps an archive through OneMail's public API (`dump`), lists counts, unmapped elements and defects (`stats`), and measures memory and allocation (`memory`).
- `compare.py` and `compare_npm.py` do the comparisons; each difference is either zero or one of the documented oracle limits in `docs/olm.md`.
