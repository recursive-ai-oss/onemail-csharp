# Text encodings: charsets, encoded words, parameter values

Normative behaviour of the text layer (`RecursiveAI.OneMail.Text`). It sits under the MIME code and knows nothing about
messages: it turns bytes into text and text into bytes, and says what it had to repair.

## Contract

- Decoders never throw on malformed input. They return the best text and report each repair or guess as a `TextIssue`
  (kind, byte offset, byte length). Offsets are into the span given to the decoder; for parameters the offset is the one the
  caller passed with the parameter.
- Kinds named `Ambiguous*` are the documented resolutions in the table below. Others are plain damage.
- Decoding is lossless where the input allows it: CR and LF survive, a byte order mark is kept as U+FEFF, U+FFFD is written only for bytes the charset cannot map.
- Passing no issue list costs nothing: the paths allocate the result string, and for encoded words one pooled byte buffer.

## Charsets

`MailEncoding.ForLabel` ignores ASCII case and surrounding white space and looks, in order, at

1. the mail-only labels: `utf-7` and its aliases, `utf-32*`, `iso-2022-kr`, `hz-gb-2312`, `iso-2022-jp-1/-2/-3`, `cp50220/50221/50222`
   (also `windows-` and `x-windows-` forms), the DOS code pages (`cp437`, `ibm850` ...), `johab`;
2. a code page number in a Windows-style spelling (`cp1252`, `windows-1251`, `codepage-1250`) that the table knows;
3. the WHATWG Encoding Standard label table, decoded by OneHtml's tables.

`replacement` and the labels the standard maps to it (ISO-2022-CN, ISO-2022-CN-EXT) are unknown (`null`); `unknown-8bit` and anything else not in
the lists are unknown. Callers apply the *unknown charset* rule below.

Because the Encoding Standard is the table, `us-ascii`, `iso-8859-1` and `iso-8859-9` decode as windows-1252 / windows-1254 and the bytes 0x80..0x9F
map to the printable characters, as every mail client in practice does. A strict Latin-1 reading would turn those bytes into C1 controls.

`MailEncoding.ForCodePage` is the reverse map for MAPI's `PidTagInternetCodepage`/`PidTagMessageCodepage`: 874, 932, 936, 949, 950, 1200, 1201, 1250-1258, 12000,
12001, 20127, 20866, 21866, 28591-28606, 38598, 437/737/775/850/852/855/857/858/860-865/869, 10000, 10007, 866, 50220-50222, 50225, 51932, 51949, 52936, 54936, 65000, 65001, 1361.
`Name` is the spelling to write in a header (`windows-1252`, `shift_jis`, `gb2312` for 936, `ks_c_5601-1987` for 949, `iso-8859-1` for 28591).
The name, not the decoder, differs for aliases: 20127 and 28591 decode like windows-1252.

Encoding (`GetBytes`) uses the runtime's code-page tables (UTF-8 and UTF-7 are ours). A character the charset lacks becomes `?` and is reported (`Unencodable`).
The stateful ISO-2022 charsets are encoded by the runtime's 50220/50221/50222/50225/52936 encoders, one word at a time.

### Decoding details

- Table-driven charsets are decoded by OneHtml. Two properties of its HTML-oriented API are worked around here so mail text is not changed: a leading
  byte order mark would override the charset (each piece is decoded with a leading space that is removed afterwards) and CR/CRLF would become LF (the input is cut at every CR byte,
  which is a character on its own in all these charsets, and the CRs are put back). Pure ASCII input skips OneHtml.
- UTF-8: the Encoding Standard's maximal-subpart replacement. UTF-16/32: the runtime's, with replacement.
- Unknown charset (`AmbiguousUnknownCharset`): the bytes are decoded as UTF-8 when they are valid UTF-8, otherwise as windows-1252.

### UTF-7 (RFC 2152) and IMAP modified UTF-7 (RFC 3501 §5.1.3)

- UTF-7 decoding accepts any ASCII byte in direct mode; `+-` is `+`; a base64 run ends at `-` (absorbed) or at a non-base64 byte (kept). A byte of 0x80 or above, a run with
  unpaired surrogates, non-zero leftover bits, a leftover of six or more bits, or an empty run (`+` followed by a non-base64, non-`-` byte) is `InvalidUtf7`; the damaged part becomes U+FFFD where it carries text.
- UTF-7 encoding writes letters, digits, `'(),-./:?`, space, tab, CR and LF directly and everything else in base64 (also the "optional direct" set); `-` closes a run only
  when the next character could continue it.
- Modified UTF-7 decoding: `&-` is `&`; a run is base64 with `,` for `/` and ends at `-`. A run without its `-` still decodes (`InvalidImapUtf7`). Bytes outside 0x20..0x7E in direct mode become U+FFFD.
  Encoding writes printable ASCII directly (`&` as `&-`) and other runs as `&base64-`, always closed.

### ISO-2022 family (ISO-2022-JP, -1, -2, -3, CP50221/50222, ISO-2022-KR, HZ)

One tolerant decoder serves the whole ISO-2022-JP family. A text that uses a character set outside the label's repertoire is still decoded and reported as `AmbiguousIso2022Repertoire`.

| Label | Repertoire |
|---|---|
| `iso-2022-jp` (cp50220) | ASCII, JIS-Roman (`ESC ( J`), JIS C 6226-1978 (`ESC $ @`), JIS X 0208-1983 (`ESC $ B`) |
| `iso-2022-jp-1` | + JIS X 0212 (`ESC $ ( D`) |
| `iso-2022-jp-2` | + GB 2312 (`ESC $ A`), KS C 5601 (`ESC $ ( C`), ISO-8859-1 and ISO-8859-7 high parts as G2 (`ESC . A`, `ESC . F`, `ESC N`) |
| `iso-2022-jp-3` | base set + JIS X 0213 plane 1 (`ESC $ ( O` / `Q`, read as JIS X 0208) and plane 2 (`ESC $ ( P`: each pair becomes U+FFFD with `UnsupportedCharacterSet`) |
| `cp50221`, `cp50222` | base set + half-width katakana (50221: `ESC ( I`; 50222: SO/SI) |
| `iso-2022-kr` | ASCII + KS C 5601 through SO/SI; the `ESC $ ) C` header is consumed |
| `hz-gb-2312` | `~{ ... ~}`, `~~`, `~` LF |

The two-byte sets are rewritten to their EUC form and decoded by OneHtml's EUC-JP / GBK / EUC-KR tables. A line end (CR or LF) returns to ASCII in every variant, so a missing reset cannot
swallow the following line. Unknown escapes, SO/SI where the variant has none, bytes ≥ 0x80 and unpaired bytes become U+FFFD (`InvalidIso2022`).
JIS X 0213 beyond plane 1 and ISO-2022-CN are not decoded.

## Encoded words (RFC 2047)

`Rfc2047.DecodeText` decodes header text, `Rfc2047.Encode` produces it.

Decode rules:

- A fold (CRLF, or a bare CR/LF, followed by space or tab) is removed first; the white space after it stays (RFC 5322 §2.2.3).
- White space between two adjacent encoded words is dropped; between a word and plain text it stays (RFC 2047 §6.2).
- `charset*language` is accepted; the language is ignored (RFC 2231 §5).
- B: characters outside the alphabet and data after padding are skipped; wrong or missing padding and a one-character tail are `InvalidBase64`; the bytes that can be decoded are kept.
- Q: `_` is a space; `=XX` is a byte; `=` without two hex digits stays a literal `=` (`InvalidQuotedPrintable`).
- A word is `=?charset?B|Q?text?=` with the text ending at the first `?=`. White space (space, tab) inside the text is accepted and reported (`MalformedEncodedWord`); CR/LF inside, an encoding other than B/Q (case-insensitive)
  or a missing `?=` make it not a word: the characters stay literal (`MalformedEncodedWord`) and scanning resumes two bytes later. An empty or space-containing charset, or a missing `?` structure, is not
  recognised as a word at all: literal text, no issue.
- Longer than 75 characters: decoded, `EncodedWordTooLong`.
- Plain 8-bit text: UTF-8 if valid (RFC 6532), else the fallback charset (default windows-1252), `AmbiguousRaw8Bit`.

Ambiguities and their resolutions:

| Case | Resolution | Issue |
|---|---|---|
| Charset not known | UTF-8 if valid else windows-1252 | `AmbiguousUnknownCharset` |
| Adjacent words with the same charset (case-insensitive) | the bytes of the run are decoded together, so a multibyte character or an ISO-2022 shift split between words survives; words with different charsets are decoded separately | `AmbiguousSplitMultibyte` (UTF-8 only: reported when a word alone is not valid UTF-8) |
| Encoded word in a quoted string or parameter value | decoded | `AmbiguousEncodedWordInQuotedString` |
| White space inside an encoded word | decoded | `MalformedEncodedWord` |
| Invalid base64 / Q escape | repaired as above | `InvalidBase64` / `InvalidQuotedPrintable` |
| `=?` that does not start a word, tried again inside | the scan resumes at the next `=?`, so `=?=?utf-8?Q?abc?=?=` decodes the inner word | none |

Encode rules: text without characters outside printable ASCII (and without `=?`) is returned unchanged. Otherwise each maximal group of tokens that need encoding, together with the
white space between them (a decoder drops it), becomes encoded words of at most 75 characters, never cutting a character (or a surrogate pair), B or Q whichever is shorter. Q uses only
`A-Za-z0-9!*+-/` plus `_` for space, so the result is valid in phrases, comments and unstructured text. Words are separated by one space; folding there is legal.

## Parameters (RFC 2231)

`ParameterCollector` takes the parameters of one header in source order (name as written, value unquoted) and `Resolve` returns one `DecodedParameter` per name (case-insensitive, first spelling kept).

- `name*` is an extended value `charset'language'percent-encoded`; `name*N` and `name*N*` are continuation segments; only segment 0 carries the charset; `*N*` segments are percent-decoded, `*N` segments are literal.
- Segments are ordered by number (stable) and joined **as bytes**, then decoded once with the charset, so a character split between segments survives.
- No charset (value without two `'`, or an empty charset): the percent-decoded bytes are read as UTF-8 if valid else the fallback (`MalformedExtendedValue` when the quotes are missing; `AmbiguousRaw8Bit` for the fallback).
- Percent escape that is not `%XX`: kept literally (`InvalidPercentEscape`).
- Plain `name=value`: UTF-8 if valid else fallback; a value containing an encoded word is decoded as one (`AmbiguousEncodedWordInQuotedString`).

| Case | Resolution | Issue |
|---|---|---|
| Segment numbers have a gap or do not start at 0 | every segment present is joined in numeric order | `AmbiguousParameterSegments` |
| Same segment number twice | both are kept, in source order | `DuplicateParameterSegment` |
| `name=` and `name*=`/`name*0=` both present | the RFC 2231 form wins, wherever it appears | `AmbiguousParameterForms` |
| Same plain name twice | the first | `DuplicateParameter` |
| More than 1024 parameters, segment number ≥ 256 | the excess is ignored | `LimitExceeded` |

`Rfc2231.Encode` returns the parts to write: a token value as `name=value`; other printable ASCII as a quoted string; anything else as `name*=charset''%XX...`, or
`name*0*`, `name*1*` ... for values longer than the part length (default 50), each part holding whole characters; the charset appears in the first part only.

## How this is checked

`tools/text-diff` generates ≥ 10k encoded-word and parameter cases (valid, adjacent, split, malformed), plus charset cases (UTF-7, IMAP UTF-7, ISO-2022 family, HZ) and values harvested from the CMU Enron maildir,
runs them through this library and through Python `email` (`decode_header`, `Message.get_param` with `collapse_rfc2231_value`), Go `mime` (`WordDecoder`, `ParseMediaType`, with `golang.org/x/text` charsets) and MimeKit's
public decoding API, and compares decoded values. Each difference must match a rule in `explain.py` that names the resolution above; anything else is a failure. The Python and Go oracles cannot
decode IMAP modified UTF-7, so those cases check against an independent reference encoder in the generator.
