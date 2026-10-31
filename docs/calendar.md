# ICS and vCard: content lines and component trees

Normative notes for `RecursiveAI.OneMail.Calendar` (BCL only). Behaviour on ambiguous input is in [`ambiguities.md`](ambiguities.md), calendar section.

## Layers

1. **Content lines** (`…Calendar.ContentLines`): `ContentLineReader` is a forward-only reader over a byte span. It yields `ContentLine` values (group, name, parameter text, value; offsets; the raw bytes). It unfolds (RFC 5545 §3.1: CRLF, LF or lone CR, then one space or tab), and for vCard 2.1 joins quoted-printable soft breaks and unindented base64 blocks. Parameters are read with `ContentParameterEnumerator`: quoted values, comma lists, bare parameters. `ContentLineWriter` folds at 75 octets without splitting a UTF-8 sequence, quotes parameter values that contain `, ; :` and encodes `^`, `"` and line breaks per RFC 6868. After the first folded line the reader allocates nothing per line (one reusable unfold buffer).
2. **Component trees** (`…Calendar`): `ContentDocument.Parse` builds `Component`/`ContentProperty` trees. Every line keeps its source bytes; an unchanged line is written as read, a changed or new line is written from its parts. Untouched documents therefore write back byte for byte, whatever was malformed. Blank lines, stray lines, an unmatched `END`, a byte order mark and anything cut off by a limit are kept as items. Editing a property, its parameters or a component name marks only that line modified.
3. **Typed views** (`…Calendar.Ics`, `…Calendar.VCard`): `IcsComponent` and `VCardView` read RFC 5545/7986/5546 and vCard 2.1/3.0/4.0 properties on demand. A value that does not parse yields null and stays in the tree. Values: `IcsTime` (DATE, floating, UTC, zoned with the `TZID` carried, never resolved), `IcsDuration` (units kept as written), `IcsPeriod`, `IcsUtcOffset`, `RecurrenceRule` (parsed parts, unknown parts kept), `VCardDate` (partial dates). Recurrence expansion and time-zone resolution are separate.

## Defects

`CalendarDefect(Kind, Offset, Length, Depth)`; `Ambiguous*` kinds mark a documented choice between valid readings, `IsLimit` kinds mark a resource limit. Parsing never throws on content. Offsets are absolute byte offsets in the input. `BareLineFeed`, `BareCarriageReturn` and `AmbiguousDoubleCarriageReturn` (CR CR LF, read as one line end) are reported once, at the first line. The kinds are listed with their meaning in `CalendarDefectKind`.

## Limits (`CalendarLimits`, on by default)

| Limit | Default | When exceeded |
|---|---|---|
| `MaxLineBytes` | 16 MiB | the value is cut there; the source bytes are kept |
| `MaxDepth` | 32 | deeper `BEGIN`/`END` lines become ordinary properties |
| `MaxComponents` | 1,000,000 | further `BEGIN`/`END` lines become ordinary properties |
| `MaxProperties` | 8,000,000 | the rest of the input is kept as one malformed block |
| `MaxTotalBytes` | 512 MiB | the rest of the input (from a line end) is kept as one malformed block |

Nothing is lost when a limit applies: untouched documents still write back identically.

## Charsets

UTF-8 is the format. A `CHARSET` parameter (vCard 2.1) selects the charset for that value from the encodings the .NET runtime ships (including the Windows and ISO code pages through the runtime's code-page provider); an unknown name is a defect and the bytes are read as UTF-8 or Windows-1252. Changed lines with a `CHARSET` are written in that charset.

## Recurrence and time zones

Expanding RRULE/RDATE/EXDATE/EXRULE and overrides into occurrences, and resolving `TZID`s (VTIMEZONE, IANA, Windows names): `docs/recurrence.md`.

## Checks

`tools/calendar/README.md` describes the corpus, the round-trip check, the oracle comparison and the fuzz targets (`contentlines`, `calendar`).
