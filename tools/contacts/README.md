# Contact tools

Checks for the vCard typed view, the Outlook contact view and the MS-OXVCARD conversions (`docs/contacts.md`). Nothing here ships in a package.

- `ContactTool` (`dump`, `export`, `rt-contact`, `rt-vcard`, `vfacts`, `gen`, `photo`): reads contact and list items of MSG/PST/OST, converts both ways, writes the facts the oracle comparison reads.
- `run.sh [stage]` (worker): `gen`, `dump`, `rt-contact`, `extract-msg`, `export`; state under `~/onemail/u20`.
- `vcard_checks.sh` (worker): the typed view against vobject and FolkerKinzel.VCards over the fetched corpus (`tools/calendar/corpus.py fetch`), the generated vCards and the vCards the conversion produces, then the byte-identical round trip.
- `make_vcards.py OUT [SEED [COUNT]]`: seeded vCards 2.1/3.0/4.0; `testdata/contacts/generated` holds the output for seed 20261031.
- `compare_extract_msg.py dump.jsonl DIR...`: contact fields against extract-msg 0.55.0 (run it from a venv that has it).
- `compare_vcard.py ours.jsonl oracle.jsonl`: the typed-view comparison with its rule classes (OL-*, ESC, K1, V21COMMA).
