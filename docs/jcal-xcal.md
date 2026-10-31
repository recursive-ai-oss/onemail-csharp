# jCal, jCard, xCal and xCard

Normative notes for `RecursiveAI.OneMail.Calendar.Forms` (BCL only). The standards are RFC 7265 (jCal), RFC 7095 (jCard), RFC 6321 (xCal) and RFC 6351 (xCard). Choices the standards leave open are the `J` rows in [`ambiguities.md`](ambiguities.md), calendar section.

## What the layer does

`JsonForms.ToJson` and `XmlForms.ToXCal`/`ToXCard` write a `ContentDocument` (the ICS or vCard tree) as jCal/jCard or xCal/xCard. `JsonForms.FromJson` and `XmlForms.FromXml` read the forms into a `ContentDocument` again. Both directions go through one model (components, properties with a type and values, parameters), so the JSON and XML forms behave the same.

Reading never throws on content. Every repair is a `FormDefect` with a byte offset (JSON), line and column (XML) and a component path. Writing never fails: what a form cannot carry is a `FormLoss` with a kind, the property path and a count, returned beside the bytes. The text document is not changed.

## Mapping

- Names are lower case in JSON and XML and upper case again in text. A vCard group prefix becomes the `group` parameter (RFC 7095 §3.3.1.2); the same is done for the rare group on iCalendar data.
- The value type is `VALUE` when given, otherwise the property's default type from RFC 5545/7986/6350. Values take the JSON shapes of the RFCs: structured text (`N`, `ADR`, `REQUEST-STATUS`, `GEO`) as arrays, recurrence rules as objects with lower-case part names and a day string for `WKST`, periods as two-element arrays, `TYPE`/`PID`/`SORT-AS` as arrays, numbers as JSON numbers.
- Dates, times and offsets use the extended spelling in jCal, jCard and xCal (`2026-03-28T02:30:00Z`, `-05:00`). xCard uses the basic spelling its schema shows for vCard dates; `date-and-or-time` is written as the `date`, `date-time` or `time` element its value fits.
- xCard is vCard 4.0: `VERSION` is not carried, and a 3.0 card is written with the 4.0 forms of the properties that differ (`GEO` as a geo URI, `TYPE=pref` as `PREF=1`).
- Inline binary (vCard 2.1/3.0 `PHOTO`, `LOGO`, `SOUND`, `KEY`, and extension properties with base64) becomes a `data:` URI, because jCard and xCard have no binary type; it goes back to `ENCODING=b`/`BASE64` for 2.1/3.0. iCalendar `ATTACH` with `VALUE=BINARY` keeps the `binary` type and its `ENCODING` parameter.
- XML elements that are not in the xCal/xCard namespaces, under a property, become an `XML` property (RFC 6351 §6, RFC 6321 §4.1) and come back as elements.
- A value that does not parse as its type is carried as `unknown` with the raw text, and the declared `VALUE` stays as a parameter so the text writes back as read. `ValueNotRepresentable` reports it.
- A vCard 2.1 line break needs quoted-printable; the writer adds `ENCODING=QUOTED-PRINTABLE;CHARSET=UTF-8` for it and never ends a folded physical line in `=` (which a 2.1 reader takes for a soft break).

## Loss kinds (`FormLossKind`)

`Folding`, `NameCase`, `ItemOrder`, `LineNotCarried` (blank, malformed or stray lines, which have no place in the forms), `RedundantValueParameter`, `DuplicateParameterMerged`, `CharsetRemoved`, `TransferEncodingRemoved`, `BareParameterAsType`, `InlineBinaryAsDataUri`, `ValueNotRepresentable`, `StructuredPadded`, `ExtraComponentsDropped`, `VersionNotCarried`, `GroupNotCarried`, `ComponentNotCarried`, `PropertyNotCarried`, `ParameterNotCarried`, `TypeNameNotCarried` (a name that is not an XML name cannot be an element), `InvalidXmlCharacter` (replaced by U+FFFD), `VersionSpecificForm`, `AmbiguousValueType`.

## Limits (`FormLimits`, on by default)

| Limit | Default | Defect |
|---|---|---|
| `MaxInputBytes` | 256 MiB | `InputTooLarge`, checked before parsing |
| `MaxDepth` | 128 nesting levels (JSON arrays or XML elements; the 32-level component limit needs about 100) | `DepthLimitExceeded` |
| `MaxNodes` | 16,000,000 | `NodeLimitExceeded` |
| `MaxStringChars` | 16 Mi characters in one string | `StringTooLong` |

XML is read with the document type declaration prohibited and no resolver, so there is no entity expansion and no external access (`DtdProhibited`). JSON accepts comments and trailing commas as a repair.

## Checks

- RFC examples: RFC 7265 Appendix B, RFC 7095 Appendix B, RFC 6321 Appendix B, RFC 6351 §4 and the section examples, in both directions (`testdata/calendar/forms/rfc`; the corrections made to the RFC text are in `ERRATA.txt`).
- `FormsDump check <root>…` converts every text file of a corpus to JSON and XML, reads the forms back, compares property by property, and checks that the second form is the same as the first. Over the corpora (biweekly, ez-vcard, ical.js, libical, sabre/vobject, Thunderbird, our generated and recurrence fixtures: 2,000+ files, about 130,000 properties) every file is stable in JSON; the only comparison differences are listed below. Differences in XML that follow a reported loss (`InvalidXmlCharacter`, `PropertyNotCarried`, `ParameterNotCarried`, `ComponentNotCarried`, `TypeNameNotCarried`) are counted apart: the file lost something XML cannot hold, and a second pass starts from a different file.
- Oracle comparison, `tools/calendar/oracles/forms/run_forms_diff.sh`: the same inputs through released ical.js (jCal, jCard), Python icalendar (jCal), biweekly (jCal, xCal) and ez-vcard (jCard, xCard), in three directions: the oracle writes the form from the text, the oracle reads our form and writes it again, and we read the oracle's form and write it again. Every difference is a rule in `tools/calendar/oracles/forms/explained.tsv`; the run reports unexplained classes, and the count is zero. The rules are the `X`, `Y`, `V`, `W` and `Z` rows: the oracle rejects the input (X1), we keep what the oracle repairs or guesses (X2), the oracle departs from the RFC (ical.js numeric `WKST`, biweekly period strings and basic spellings), the oracle normalises (URI scheme case, UTC conversion, durations), or the oracle writes vCard 4.0 from a 3.0 card (ez-vcard).
- There are two independent oracles for jCal (ical.js, icalendar) and for jCard (ical.js, ez-vcard) on the corpus. xCal and xCard have one oracle each: biweekly and ez-vcard. No other released reader of the XML forms is known to us; xCal and xCard are otherwise checked against the RFC examples and our own round trips.
- Fuzz targets `jcal-jcard` and `xcal-xcard` (`fuzz/`): reading never throws, the result writes in all forms, and the JSON form of it reads back to itself.
