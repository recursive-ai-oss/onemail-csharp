# Calendar and contact tools

Checks for `RecursiveAI.OneMail.Calendar`. Nothing here ships in a package.

## Corpus

`corpus.py fetch` downloads the public ICS and vCard files listed in `corpus.tsv` (pinned URLs and SHA-256) into `$ONEMAIL_CORPUS/calendar`. They are never committed. `corpus.py rfc` cuts the example blocks out of the RFC texts into `testdata/calendar/rfc` (committed; IETF documents, see `THIRD-PARTY-NOTICES.md`). `make_fixtures.py` generates `testdata/calendar/generated` (hand-written client-style and malformed files, plus seeded mutations of the RFC examples).

| Source | Terms | Committed |
|---|---|---|
| RFC 2426/2445/2446/5545/5546/6047/6350 example blocks | IETF Trust legal provisions: code components under the Revised BSD licence | yes, with notice |
| libical `test-data`, `src/test`, `zoneinfo` (MPL-2.0 OR LGPL-2.1) | licence file in the repository | no |
| ical.js `test/parser`, `samples` (MPL-2.0) | licence file in the repository | no |
| ez-vcard test vCards (BSD-2-Clause) | licence file in the repository | no |
| biweekly test calendars (BSD-2-Clause) | licence file in the repository | no |
| sabre/vobject test calendars (BSD-3-Clause) | licence file in the repository | no |
| Thunderbird holiday calendars | Mozilla website content licence | no |

## Round trip

    dotnet run --project tools/calendar/CalendarDump -c Release -- roundtrip testdata/calendar $ONEMAIL_CORPUS/calendar

prints, per source, how many files parsed and wrote back byte-identical, and lists every file that did not.

## Oracle comparison

`CalendarDump facts <root> <out.jsonl>` writes the normalised facts for every file: `[path, name, params, value, raw]` per component and property, with canonical values (`t:` text, `d:`/`dt:` dates, `dur:` seconds, `off:` seconds, `rr:` rule, `per:` period, `s:`/`l:` structured and list text, `b:` base64 length). `oracles/py_facts.py icalendar|vobject` writes the same from the released Python packages (`oracles/setup_python.sh` builds the pinned venv); `oracles/dotnet` does it for Ical.Net (components, text and date-time properties) and FolkerKinzel.VCards (FN, EMAIL, TEL, NOTE) through their public API. `oracles/compare.py ours.jsonl oracle.jsonl` prints identical/different counts and the differences by property. A typed property an oracle returns as plain text is counted separately, not as a difference.

## Fuzz

`fuzz/run.sh contentlines <seconds>` and `fuzz/run.sh calendar <seconds>`; see `fuzz/README.md`.

## JSON and XML forms

`FormsDump` (`tools/calendar/FormsDump`): `to json|xml <file>`, `from json|xml <file>`, `check <root>…` (text to form to text round trips and form stability; `FORMS_PROBLEMS=n` lists more than 25 problems per source), `emit <root> <outdir>`, `reread <indir> <outdir> json|xml` and `alloc <root>…` (bytes allocated per property). `make_forms_fixtures.py` cuts the RFC examples into `testdata/calendar/forms/rfc`.

`oracles/setup_forms.sh <dir>` fetches the pinned oracles (ical.js, ez-vcard, biweekly, icalendar, with Node and a JDK, hash-checked); `oracles/forms/run_forms_diff.sh <workdir>` runs them over the corpus and the committed fixtures and writes `report/*.txt`; `oracles/forms/explained.tsv` lists every accepted difference with its reason, and the run ends with the count of unexplained classes.
