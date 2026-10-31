# Reading MIME

This is the behaviour of `MimeReader` (`RecursiveAI.OneMail.Mime.Reading`) and the entity tree built on it
(`RecursiveAI.OneMail.Mime`). It is written to be implemented the same way in another language.

## Events cover the input exactly

The reader turns a message into a flat sequence of events. Every input byte belongs to exactly one event, in order, so
concatenating the bytes of all events reproduces the input — including malformed input. The tree stores the same ranges,
so an unchanged tree writes back byte for byte.

| Event | Bytes it covers |
|---|---|
| `EntityStart`, `EntityEnd`, `Defect` | none (a position) |
| `Header` | one header field: name, colon, value, folded lines, and the field's own line end |
| `HeadersEnd` | the empty separator line (`CRLF` or `LF`), or nothing if the input has none |
| `Preamble` / `Body` / `Epilogue` | a chunk of text; chunk sizes depend on buffering and carry no meaning |
| `Delimiter` | the line end that precedes the delimiter line, the delimiter line `--boundary[--]`, its transport padding, and its own line end |

A multipart body is: preamble, then for each part a `Delimiter`, the part, and finally the closing `Delimiter` and the epilogue.
The line end before a delimiter belongs to the delimiter (RFC 2046 §5.1.1), so a part's body does not include it.

## Structure

- A header block ends at the first empty line. A line that is not a header field (no colon, or a name with characters other than
  printable ASCII) also ends it: the line starts the body and `MissingHeaderBodySeparator` is reported. A folded line (starting with
  space or tab) continues the field before it (RFC 5322 §2.2.3); one with no field before it ends the header block.
- An entity is a **multipart** when `Content-Type` is `multipart/*` with a usable `boundary`, and the transfer encoding is
  7bit, 8bit or binary. It is a **message** when the type is `message/rfc822` or `message/global` (or it has no `Content-Type`
  inside `multipart/digest`, RFC 2046 §5.1.5), under the same encoding rule. Everything else is a **leaf**.
- A delimiter line is `--` + boundary, optionally followed by `--`, then optional spaces and tabs, then the line end or the end of
  the input. Active boundaries are tried innermost first. A delimiter of an enclosing multipart ends everything nested inside it
  (RFC 2046 §5.1.2); each multipart cut off this way reports `MissingFinalBoundary`.
- The first `Content-Type` and the first `Content-Transfer-Encoding` of an entity decide; later ones are reported and ignored.

## Defects

Defects are events, reported after the event that raised them, with the byte offset. They never stop reading.

| Kind | Meaning | Reading used |
|---|---|---|
| `MissingHeaderBodySeparator` | header block ended by end of input or a non-header line | the offending line starts the body |
| `HeaderContinuationWithoutHeader` | folded line with no field to continue | ends the header block; the line starts the body |
| `BareCarriageReturnInHeader` | CR not followed by LF inside a field | left in place |
| `NulInHeader` | NUL inside a field | left in place |
| `Utf8Header` | bytes above 0x7F that are valid UTF-8 (RFC 6532) | informational |
| `AmbiguousEightBitHeader` | bytes above 0x7F that are not UTF-8 | kept raw; the text layer picks a charset |
| `WhitespaceBeforeHeaderColon` | `Name : value` (obsolete syntax, RFC 5322 §4.2) | name is trimmed |
| `AmbiguousDuplicateContentType` | more than one `Content-Type` | first wins |
| `AmbiguousDuplicateTransferEncoding` | more than one `Content-Transfer-Encoding` | first wins |
| `InvalidContentType` | not `type/subtype` | `text/plain` |
| `UnknownTransferEncoding` | not 7bit/8bit/binary/base64/quoted-printable | identity |
| `MultipartWithoutBoundary`, `EmptyBoundary`, `BoundaryTooLong` | unusable boundary | the entity is a leaf |
| `AmbiguousDuplicateBoundary` | a multipart reuses an enclosing multipart's boundary | innermost wins |
| `AmbiguousBoundaryLikeLine` | a line starts with `--boundary` but continues with other text | body text, not a delimiter |
| `AmbiguousEncodedComposite` | multipart or message with base64 or quoted-printable encoding | a leaf: the content is not parsed |
| `MissingFinalBoundary` | multipart without closing delimiter | the multipart ends at the enclosing delimiter or the end of input |

`Ambiguous*` marks places where mail clients disagree. The reading above is the library's choice; it has not yet been confirmed
against how particular clients behave, and may change once it is.

## Resource limits (on by default)

Reaching a limit reports a defect and keeps the rest of the input as body text, so nothing is lost and nothing is allocated for it.

| Limit | Default | Defect | Effect |
|---|---|---|---|
| `MaxDepth` | 64 | `DepthLimitExceeded` | a deeper multipart/message is a leaf |
| `MaxEntityCount` | 10,000 | `EntityLimitExceeded` | no more delimiters are recognised; the rest of the input is body of the current entity |
| `MaxHeaderBlockBytes` | 1 MiB | `HeaderLimitExceeded` | the header block ends; the rest is body |
| `MaxHeaderFieldBytes` | 256 KiB | `HeaderFieldLimitExceeded` | the header block ends; the rest is body |
| `MaxHeadersPerEntity` | 10,000 | `HeaderLimitExceeded` | the header block ends; the rest is body |
| `MaxBoundaryLength` | 255 | `BoundaryTooLong` | the entity is a leaf |
| `MaxTotalBytes` | 2 GiB − 1 | `TotalSizeLimitExceeded` | reading stops; `IsTruncated` is set |

The read buffer is pooled and grows only to hold one header field, never past twice `MaxHeaderBlockBytes`.

## Transfer encodings

`Base64Decoder` and `QuotedPrintableDecoder` are lenient and report what they repaired as `TransferIssues`.

- base64: whitespace is ignored; characters outside the alphabet are skipped; padding ends a group and data after it starts a
  new one (concatenated base64 blocks decode); a final partial group yields its complete bytes.
- quoted-printable: `=XX` in either hex case; soft breaks (`=` + line end, also with blanks in between) vanish; blanks before a
  line end are dropped (RFC 2045 §6.7); a malformed `=` stays literal. Hard line ends are copied as they are.

## Writing

`MimeWriter` copies reader events byte for byte, and generates headers (folded at word boundaries at 78 columns, UTF-8 when not
ASCII) and delimiters. A header name or value that could start a new header (a colon in the name, a line break in the value) is
refused. A modified tree re-serialises only the entities on the path to a change; untouched entities, bodies, preambles and
epilogues are copied from the source as ranges.
