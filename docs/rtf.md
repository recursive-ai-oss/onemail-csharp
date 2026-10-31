# RTF bodies: compressed RTF and de-encapsulation

`RecursiveAI.OneMail.Rtf` reads and writes what Outlook stores in `PidTagRtfCompressed` and recovers the HTML or plain text
that Outlook wraps inside an RTF body. The namespace uses only the .NET base class library: it does not reference any other
part of OneMail, so it can be lifted out as the seed of a general RTF reader. Nothing in it throws on malformed input: damage
is a typed `RtfDefect` with a byte offset, and the ambiguity entries (RT1–RT18) are in [ambiguities.md](ambiguities.md).

| Piece | Type | Spec |
|---|---|---|
| Tokenizer | `RtfReader` (`ref struct`) | RTF 1.9.1, lexical rules |
| Decompress | `Compression.RtfDecompressor` | MS-OXRTFCP 2.2 |
| Compress | `Compression.RtfCompressor` | MS-OXRTFCP 2.3 |
| HTML/text out of RTF | `RtfDeencapsulator` | MS-OXRTFEX 2.2 |
| HTML/text into RTF | `RtfEncapsulator` | MS-OXRTFEX 2.1 |

## Compressed RTF (MS-OXRTFCP)

The stream is a 16-byte header (COMPSIZE, RAWSIZE, COMPTYPE, CRC, little-endian) and contents. `LZFu` contents are
LZ77 over a 4096-byte circular dictionary that starts with 207 bytes of common RTF text; a control byte tells which of the next
eight tokens are literals (1 byte) or references (2 bytes, big-endian: 12-bit dictionary offset, 4-bit length minus two). A
reference to the dictionary's write position ends the data. `MELA` contents are the RTF itself. The CRC is the reflected
CRC-32 polynomial (0xEDB88320) with a zero start value and no final inversion, over the contents (padding included).

**Decompression** (`RtfDecompressor.Decompress`) checks, in this order, and reports each failure without stopping unless it
cannot continue:

| Check | Defect | Output |
|---|---|---|
| fewer than 16 bytes | `HeaderTruncated` | empty |
| type neither `LZFu` nor `MELA` | `UnknownCompressionType` | empty |
| COMPSIZE + 4 differs from the bytes present | `CompressedSizeMismatch` | the declared range, or what is there |
| RAWSIZE above `MaxOutputBytes` (256 MiB) | `OutputLimitExceeded` | empty |
| RAWSIZE above 20 × the input length | `ExpansionRatioExceeded` | empty |
| data ends before the end marker | `MissingEndMarker` | everything decoded |
| reference to a dictionary position nothing was written to | `ReferenceBeyondDictionary` | zero bytes used |
| CRC differs | `CrcMismatch` | everything decoded |
| bytes produced differ from RAWSIZE | `RawSizeMismatch` | everything decoded (the buffer grows up to the limit) |

The size and ratio checks run before the output buffer is allocated. Honest data cannot expand more than 17 bytes per 2-byte
reference (8.5 : 1), so the ratio limit of 20 never refuses real data. `MELA` copies the contents and does not validate the CRC
(MS-OXRTFCP 2.2.3.1). `RtfDecompressionResult.CrcVerified` is true only for `LZFu` data whose CRC matched.

**Compression** (`RtfCompressor.Compress`) searches the 4095 bytes before the cursor through a hash chain of two-byte pairs
(at most 128 candidates per position), takes the longest match of 2 to 17 bytes (the older one on a tie, as the specification's
oldest-first scan does), and writes the end marker. Matches may overlap the bytes being written, which is what the
decoder's byte-at-a-time copy expects. Output decompresses back to the input exactly with `RtfDecompressor` and with the
oracle; it reproduces both worked examples of the specification byte for byte. It is not required to equal Outlook's own bytes
(see Measurements). An empty input becomes `01 0C F0` (an end marker only); Outlook's writer adds a NUL byte there, which
the reader treats as one byte of output (RT18).

## The tokenizer (`RtfReader`)

A forward-only reader over a `ReadOnlySpan<byte>`: `GroupStart`, `GroupEnd`, `ControlWord` (name, optional signed number,
one optional delimiting space that belongs to the word), `ControlSymbol` (`\*`, `\~`, `\{` …), `HexEscape` (`\'hh`), `Text` (a
run of bytes), and `Binary` (the `N` bytes after `\binN`, not parsed). CR and LF between tokens are skipped, as RTF requires.
A backslash before a line break is a control symbol that readers treat as `\par`. It never interprets code pages,
destinations or Unicode: those are the caller's.

Defects: `UnbalancedGroupEnd`, `UnclosedGroups`, `ParameterOverflow` (clamped to `int.MaxValue`), `InvalidHexEscape`,
`BinaryDataTruncated`, `TruncatedToken`, `NestingLimitExceeded` (default depth 1000, then reading stops).

## De-encapsulation (MS-OXRTFEX 2.2)

`Detect` follows 2.2.3.1: the document starts `{\rtf`, and among its first ten tokens (all group starts or control words)
`\fromhtml1` or `\fromtext` appears. `\fromhtml`, `\fromhtml0` and any other form do not count (RT12).
`Deencapsulate` returns the text as a .NET string, with the document's default code page. All bytes are decoded, so there is
no byte-oriented result to mis-label.

HTML (`\fromhtml1`):

- `{\*\htmltag…}` group content is copied, regardless of `\htmlrtf` (RT6); the numeric parameter is ignored. `\*\mhtmltag` and every
  other ignorable destination are skipped.
- Outside `\htmltag`, text and control words under `\htmlrtf` (or `\htmlrtf1`) are dropped until `\htmlrtf0`. The state is
  saved on `{` and restored on `}`. The current font is tracked even in suppressed text.
- `\par` and `\line` become CRLF, `\tab` a tab. Other control words are ignored.
- Destinations that hold no body text are skipped (`\colortbl`, `\stylesheet`, `\info`, `\pict`, `\object`, headers and footers,
  `\fldinst`, list and paragraph-number groups, bookmarks…); `\fonttbl` is read for code pages only (RT13).

Plain text (`\fromtext`): the same walk with no `\htmltag` groups; `\htmlrtf` is ignored (RT10).

**Characters.** `\uN` is a signed 16-bit UTF-16 unit; the next `\ucN` characters (default 1) are its fallback and are skipped.
A skipped character is a text byte or a `\'hh` escape; control words do not count, and `{` or `}` ends the skipping (RT8). `\uc`
is scoped to its group. An unpaired surrogate becomes U+FFFD (`LoneSurrogate`). `\'hh` and text bytes are decoded as runs in the code page of the
current font: `\cpgN` of the font, else the charset of `\fcharsetN` (0 is windows-1252 whatever `\ansicpg` says; 1 means the
document's code page), else `\ansicpgN` (or what `\ansi`, `\mac`, `\pc`, `\pca` imply, else 1252) (RT9). `\htmltag` content always
uses the document's default code page. Runs are decoded together so that two-byte characters split across `\'hh\'hh` come out
whole. Code pages come from `RtfDeencapsulationOptions.CodePageDecoder`, or by default from the base library's encodings and
`CodePagesEncodingProvider` (no global registration); an unknown code page uses the default one and reports
`AmbiguousUnknownCodePage` (RT11).

Control words and symbols with a character meaning: `\lquote \rquote \ldblquote \rdblquote \bullet \endash \emdash \enspace
\emspace \qmspace \zwj \zwnj \ltrmark \rtlmark`, `\~` (U+00A0), and `\_` (U+00AD inside `\htmltag`, U+2011 elsewhere) (RT5, RT7).
`\{ \} \\` are the literal characters.

## Encapsulation (MS-OXRTFEX 2.1)

`EncapsulateHtml` writes `{\rtf1\ansi\ansicpg1252\fromhtml1 \deff0{\fonttbl…}`, the empty `{\*\htmltag64}`, then walks the
HTML: markup (tags, comments, doctype, and `<script>`/`<style>` with their content) goes into `{\*\htmltagN …}` with the
TagType flag as parameter (plus 8 for closing tags); entities go into `\htmltag0` with their rendering after `\htmlrtf`; line
breaks in text become `\htmltag0 \par` plus a suppressed space; everything else is visible RTF text. The output is 7-bit ASCII
(`\uN?` for non-ASCII, `\'hh` for control characters). `EncapsulateText` writes `\fromtext` and the text with `\par` for every
line break. This is not an HTML-to-RTF converter: no formatting is produced.

Line breaks (CR, LF or CRLF) come back as CRLF, which MS-OXRTFEX 1.3.1 allows; everything else round-trips exactly.

## Measurements and method

`tools/rtf` extracts the RTF, HTML and text body streams from the public Tika and POI `.msg` files (OneArchive's compound-file
reader; `tools/corpus/fetch-msg.sh` fetches the files), runs OneMail over them, and compares with released packages run as
black boxes. See [tools/rtf/README.md](../tools/rtf/README.md) for the commands and the oracle versions.

On 55 `.msg` files (53 read; two fuzzer-minimised files have a damaged compound file) there are 45 compressed RTF bodies, 27
distinct streams:

| Check | Result |
|---|---|
| decompression vs compressed-rtf 1.0.7 | 45 / 45 byte-identical; header CRC verified on 45 / 45; no defects |
| our compression read back by OneMail and by compressed-rtf | 45 / 45 identical to the input |
| compressed-rtf's compression read back by OneMail | 45 / 45 identical, CRC verified |
| our compressed size vs Outlook's own stream | 97,438 vs 97,422 bytes over the 27 distinct streams (1.0002 ×); 3 of 45 streams byte-identical, the rest differ only in which of several equal-length matches is used |
| de-encapsulation vs RTFDE 0.1.2.2 | 31 HTML and 4 text results: identical apart from RT1 (all) and RT2 (4 files) |
| de-encapsulation vs the message's own `PidTagHtml` / `PidTagBody` | 4 text bodies identical; 6 HTML bodies: 5 differ only by an injected content-type `<meta>` (RT3), 1 is the same text with re-serialised markup (RT4) |
| 10 RTF bodies without `\fromhtml1`/`\fromtext` | `NotEncapsulated`; RTFDE refuses them too |
| 3,000 damaged streams (truncations, bit flips, header edits, insertions) | OneMail reports a defect on 2,998; the 2 without are valid; oracle refuses 2,616 of them; bytes equal wherever both decode, except 123 where OneMail's size or ratio limit refused what the oracle decoded (RT14) |

Corpus bodies are not committed; the specification's example blocks are in `testdata/rtf` and the tests.
