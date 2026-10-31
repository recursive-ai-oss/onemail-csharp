# Body checks

Tools for the body conversions (`RecursiveAI.OneMail.Bodies`, [`docs/bodies.md`](../../docs/bodies.md)).

- `oracles.sh`: fetches the HTML-to-text oracles as released builds, user-locally (`w3m` and `lynx` Ubuntu packages unpacked with `dpkg -x`, Python `html2text` in a `uv` venv).
- `BodiesTool`: extracts the HTML bodies of stores (`html`), converts them (`text`), lists what a screen hides (`hidden`), prints best-body choices per item class (`bodies`), lists items without a body (`nobody`) and reports allocation per KiChar (`alloc`).
- `gen_cases.py`: hand-written constructs and seeded random HTML documents.
- `compare.py`, `h2t_server.py`, `run-compare.sh`: run the three oracles over a directory of HTML files and classify every difference from OneMail's text; prints the counts per oracle and per corpus and lists anything unexplained.
- `fuzz-seeds.py`: seeds for the `htmltext` fuzz target.

Corpus HTML is extracted into a work directory outside the checkout and never committed.
