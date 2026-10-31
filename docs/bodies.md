# Bodies: HTML to text, text to HTML, `format=flowed`, best body

Namespace `RecursiveAI.OneMail.Bodies`. Everything here works on strings and on the shared view (`IMailItem`); nothing reads a store. None of it
throws on malformed content: it returns the best result and a list of `BodyIssue` (`BodyIssueKind`). Kinds that start with `Ambiguous` are the
single documented resolution of a known ambiguity (rows BD1 to BD5 in [`ambiguities.md`](ambiguities.md)); the others are damage or a limit.

| Piece | Entry point | Does |
|---|---|---|
| HTML to text | `HtmlToText.Convert(html, HtmlToTextOptions?)` | Readable text from OneHtml's DOM. |
| Text to HTML | `TextToHtml.Convert(text, TextToHtmlOptions?)` | Escaped HTML with paragraphs, quote levels and links. |
| `format=flowed` | `FormatFlowed.Parse / Decode / Encode` | RFC 3676 both ways, DelSp included. |
| Best body | `BodySelector.Select / GetText / GetHtml`, `IMailItem.BestBody` | One selection order for every item type. |

## HTML to text

The document is parsed by OneHtml (`Document.Parse`); the converter walks the DOM once, without recursion, so nesting depth costs no stack. The
same input and options always give the same text.

- **Blocks.** `p`, headings, `blockquote`, `pre`, `hr`, `figure`, `fieldset`, `address` and outermost lists and tables are separated by a blank line;
  `div`, `section`, `article`, `li`, `tr`, `br` and the other block elements by a line break. Runs of `br` give at most two blank lines.
- **Whitespace** collapses as CSS `white-space: normal` says: runs of space, tab, newline and form feed become one space; spaces at the ends of a
  line go. `pre`, `listing`, `xmp`, `plaintext` and `white-space: pre / pre-wrap / break-spaces` keep every character and line; `pre-line`
  keeps lines and collapses spaces. A non-breaking space is a space that does not collapse (and is ignored at the start of a line, so `<p>&nbsp;</p>`
  is nothing). Soft hyphens, zero-width characters, the byte order mark and similar invisible characters are removed.
- **Lists.** `ul`, `menu`, `dir`: `* `; `ol`: `1. ` from `start` (and `value`); items continue under their marker; nested lists indent by the marker width.
  `dd` is indented four spaces.
- **Tables.** Each row is a line; the cells of a row are joined with `TableCellSeparator` (default `" | "`). A cell that holds blocks puts them on their
  own lines, and the separator then does not appear. Only the outermost table is set off by a blank line. There is no column alignment.
- **Quotes.** `blockquote`, and any element with `type="cite"` (Thunderbird), give `> ` prefixes, nested `> > `. A blank line inside a quote is `>`.
- **Links.** `LinkStyle.WhenDiffers` (default): `text <address>`, unless the text already is the address (compared without scheme, `www.`, `mailto:`,
  case and a final slash). A link with no text of its own (a picture without alt text) is written as `<address>`. `#fragment`, `javascript:`,
  `data:`, `cid:` and `vbscript:` addresses are not written. `TextOnly` writes only the text; `Footnotes` writes `text [1]` and the numbered
  addresses after the text.
- **Images.** `[alt]` (default), the alt text alone, or nothing. Images without alt text and images no bigger than 2 x 2 pixels are dropped.
- **Skipped.** `head`, `script`, `style`, `template`, `title`, form controls (`input`, `select`, `option`, `textarea`), `object`, `iframe`, `embed`,
  media elements, `map`/`area`, SVG and MathML, and anything hidden: the `hidden` attribute, `display: none`, `visibility: hidden/collapse` and
  `mso-hide: all` in a `style` attribute, and, with `ApplyStyleSheets` (default), the same from the document's own `<style>` rules, evaluated by
  OneHtml's cascade for a screen (so `@media (max-width: ...)` rules that do not match are ignored). The cascade runs without OneHtml's built-in
  user-agent sheet (the converter knows the element defaults itself). Stylesheets that are linked, not embedded, are not fetched.
- **Width.** `LineWidth` 0 (default) does not wrap. Otherwise lines break at spaces so that the prefix plus text fits; a word longer than the line is
  not split; `pre` lines never wrap. Continuation lines keep the quote prefix and list indent.

### Limits

All in `HtmlToTextOptions`, all on by default, each reported as an issue:

| Limit | Default | Issue |
|---|---|---|
| `MaxInputChars` | 16 Mi | `InputTruncated` (input cut) |
| `MaxOutputChars` | 16 Mi | `OutputTruncated` (the rest is not converted) |
| `MaxNesting` (quote and list levels that add a prefix) | 32 | `NestingTooDeep` (deeper levels add no prefix) |
| `MaxTableCells` | 100,000 | `TableTooLarge` (later cells skipped) |
| `MaxWhitespaceRun` | 256 | `WhitespaceRunBounded` |
| OneHtml's parse limits (4,096 open elements, 4 Mi nodes, ...) | OneHtml's | `HtmlLimitExceeded`; the converter then retries on shorter and shorter prefixes of the input, so the text before the cut is kept |

## Text to HTML

`TextToHtml.Convert` escapes `& < >`, turns a blank line into a paragraph break and a single line break into `<br>`, and writes one
`<blockquote type="cite">` per quote level. The quote level of a line is its leading `>` markers, optionally separated by single spaces (`>>`, `> >` and
`> > ` are all level 2). Leading and repeated spaces and tabs are kept with `&nbsp;`; a lone CR is a line break; control characters and
noncharacters are removed (`ControlCharactersRemoved`) so the output parses with no parse errors. Links are detected conservatively: `http://`,
`https://`, `ftp://`, `mailto:` (with an `@`) and `www.` (with a dot) at the start of a word; trailing punctuation and unbalanced closing brackets stay out of the
link; nothing else is linked. Options: `Flowed` / `DelSp` unwrap a `format=flowed` body first; `FullDocument` (default) writes `DOCTYPE`, `head` with the
charset and `body`. Limits: `MaxInputChars`, `MaxOutputChars`, `MaxQuoteDepth` (32, `QuoteDepthCapped`).

## `format=flowed` (RFC 3676)

`Parse` reads a flowed body into `FlowedParagraph(QuoteDepth, Text)`: quote depth is the count of leading `>`; one leading space (space-stuffing) is
removed; a line is flowed when it ends in a space after that, except the signature separator `-- `; a flowed line joins the next only when it has the same
quote depth (a depth change ends the paragraph); `DelSp` drops the one trailing space of each soft break. `Decode` writes the paragraphs as text with
`> ` prefixes (an unquoted paragraph that starts with `>` gets a leading space so it is not read as a quote). `Encode` wraps at `width`
(default 78, counting the prefix, stuffing and the soft-break space), space-stuffs lines that start with a space, `>` or `From `, writes `>` markers followed by
a space, and ends every wrapped line but the last of a paragraph with a space. With `delSp` it also splits words longer than a line and adds the
soft-break space that the reader removes. Trailing spaces of a paragraph are dropped (a fixed line cannot end in one) except for `-- `. Python's `email`
package has no flowed handling, so there is no oracle; the behaviour above is the reading of the RFC, tested by round trip.

## Best body

`BodySelector.Select(item)` returns `SelectedBody(Kind, Body, Reason, Issues)`. Order, first match wins:

1. **Outlook, RTF newer than the text** (`PidTagRtfInSync` false, MS-OXCMSG): the body de-encapsulated from the RTF (HTML or text) replaces the stored
   one (`StaleText`, issue `AmbiguousStaleText`).
2. **Outlook, written as plain text** (`PidTagNativeBody` 1 or 4) and both bodies are usable: the plain text (`NativeText`).
3. **HTML**, when it has visible text or an image (`HtmlPreferred` with a text body, `HtmlOnly` without).
4. **Plain text** (`TextOnly`; `EmptyHtml` when HTML exists but shows nothing, issue `AmbiguousEmptyHtml`).
5. **RTF written as RTF** (`RtfOnly`, issue `OnlyRtfAvailable`).
6. Nothing (`NoBody`).

What the candidates are comes from the shared view: for MIME, RFC 2046 §5.1.4 inside the tree (last alternative, first child of `related`/`signed`, a TNEF
attachment's bodies, see [`shared-view.md`](shared-view.md)); for Outlook items, the stored bodies, then what the RTF body encapsulates, then (when the native
RTF converter is present) RTF converted to HTML; OLM and DBX items arrive as the view of their own format. A `text/plain; format=flowed` body is returned
joined (`DerivedMailBody` with `BodyDerivation.FlowedDecoded`). When HTML comes before plain text inside a `multipart/alternative`, HTML is still chosen
and the issue `AmbiguousAlternativeOrder` says so.

`BodySelector.GetText(item)` always gives plain text (the stored text, else the best HTML through `HtmlToText`, `BodyDerivation.HtmlToText`);
`GetHtml(item)` always gives HTML (the stored HTML, else the text through `TextToHtml`, `BodyDerivation.TextToHtml`); both give null when the item has
neither. `IMailItem.BestBody` is `Select(item).Body`. Derived bodies are `DerivedMailBody` (UTF-8; `Derivation`, `Issues`). `MailBody.IsFormatFlowed` and
`FlowedDelSp` tell whether a stored body is flowed.

## What was checked

Oracles, all released programs run as separate processes: `w3m` 0.5.3+git20230121-2ubuntu5, `lynx` 2.9.0rel.0-2build2 (Ubuntu noble packages unpacked
user-locally) and Python `html2text` 2025.4.15 (`tools/bodies/oracles.sh`). Inputs: the 118 HTML bodies of the corpora (Dovecot mbox 50, MSG 38, OLM 11,
PST 14, Mime4j 5; the Enron maildir has no HTML part that parses as one, 0 of 517,401), 43 hand-written constructs and 300 seeded random documents
(`tools/bodies/gen_cases.py`), 461 in all. `tools/bodies/compare.py` reduces each output to its sequence of words (links, image text, list markers
and table marks removed), aligns the sequences and classifies every difference; also checks link addresses.

| Oracle | Files | Identical | Differing, all explained | Unexplained |
|---|---|---|---|---|
| w3m | 461 | 229 | 232 | 0 |
| lynx | 461 | 240 | 221 | 0 |
| html2text | 461 | 288 | 173 | 0 |

By corpus (identical / explained / unexplained): w3m Dovecot 48/2/0, MSG 28/10/0, OLM 10/1/0, PST 13/1/0, Mime4j 4/1/0, constructs 36/7/0, random 90/210/0;
lynx 49/1/0, 37/1/0, 10/1/0, 13/1/0, 4/1/0, 35/8/0, 92/208/0; html2text 50/0/0, 38/0/0, 11/0/0, 14/0/0, 5/0/0, 38/5/0, 132/168/0.

Explanation classes (count of differing stretches, w3m / lynx / html2text): text a screen hides (display none, `hidden`, `mso-hide`, media rules) 109 / 293 /
111, which the oracles print because they apply no CSS; image alt text 78 / 217 / 0 (OneMail writes it in brackets, `html2text` is run without images);
table cells set side by side or as a markdown table 114 / 0 / 87; text of elements skipped on purpose (head, title, script, form controls) 7+ / 31 / 2;
word boundaries only (an oracle glues words OneMail separates) 3 / 1 / 1; an alt word glued to its neighbour 1 / 0 / 0. Other normalisations, applied to
both sides: Windows-1252 numeric references (`&#153;`, which the oracles drop), small numbers in documents with ordered lists, text of links the oracle cannot
follow (`news:`).

Link addresses: lynx lists 1,139 and html2text 1,135; OneMail writes every one that is not text it already shows (1,134 found, 0 missing), skips `#`, `javascript:` and
`file:` forms (3 / 1), image-map `area` links (2, not written), and writes none that an oracle lacks (0). List item counts (informational): OneMail 794, w3m 842, lynx 1,040,
html2text 903 (the oracles count their own bullets for definition lists, menus and nested layout lists).

Text to HTML: the output of 1,715 fuzz-style inputs and all unit inputs parses with no OneHtml parse errors; quote levels are kept (unit tests). Flowed: encode then
decode returns the paragraphs on 600 generated cases (delSp both ways, widths 20 to 79) and on the fuzz target. Hostile input: depth, huge tables, long whitespace runs and
output/input limits are unit-tested and fuzzed (`htmltext` target).

Allocation, `HtmlToText.Convert` with default options: 21.6 KB per KiChar of HTML on the 118 real bodies (136 KB per message), 56 KB per KiChar on the small generated cases (42 KB
per document); most of it is OneHtml's parse (a document and its style sheets). Run `BodiesTool alloc <dir>`.

### Best body over the corpora

Counts by chosen kind and reason; derivations of the text and HTML bodies are in the second table. `Message` items unless the kind is named.

| Corpus (format) | Items | Html: preferred / only | Text only | Rtf only | No body |
|---|---|---|---|---|---|
| Enron maildir (EML) | 517,401 | 0 / 0 | 517,401 | 0 | 0 |
| Dovecot mbox | 3,237 | 47 / 3 | 3,187 | 0 | 0 |
| Mime4j test messages (EML, mbox) | 78 | 4 / 1 | 51 | 0 | 22 |
| MSG (Tika, POI) | 53 | 35 (33 messages, 2 appointments) / 2 | 14 (8 messages, 2 tasks, 2 notes, 2 contacts) | 1 | 1 |
| PST (Tika) | 11 | 4 / 0 | 7 | 0 | 0 |
| OLM (2 archives) | 530 | 3 (2 tasks, 1 contact) / 7 (6 messages, 1 note) | 483 | 0 | 37 |
| TNEF files | 7 | 0 / 2 | 0 | 3 | 2 |
| DBX (Hacking case) | 2,053 | 0 / 1 | 32 | 0 | 2,020 |

Where no body exists: the 22 Mime4j messages are test cases whose parts are all attachments or embedded messages (a `message/*` part is not searched for a body, see the
shared-view rules), a few with a text part marked `Content-Disposition: attachment`; the one MSG has no body property (`AmbiguousCodePage` only); the 37 OLM items are 18 folder-like `Other` items, 8 appointments, 5 contacts, 3 tasks,
2 distribution lists and 1 message with no description or notes; the 2 TNEF files have no body attribute (lower-layer defects); the 2,020 DBX items have no
message text in the store (`AmbiguousDbxNoMessageText`). The 3 TNEF and 1 MSG "RTF only" items have RTF written as RTF with no encapsulated HTML or text; the native
RTF converter turns them into HTML when it is present. The choice never creates a body; `GetText` converts the 16 HTML-only items (Dovecot 3, Mime4j 1, MSG 2,
OLM 7, TNEF 2, DBX 1) and `GetHtml` converts every text-only item. 820 Dovecot messages are `format=flowed` and are joined; `AmbiguousAlternativeOrder` is reported on 4.
