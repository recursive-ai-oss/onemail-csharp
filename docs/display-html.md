# Display HTML, the sanitiser and the remote-content policy

`RecursiveAI.OneMail.Display` turns a message body into HTML for a browser or web view. `HtmlSanitizer.Sanitize(html, options)` is the layer
that rewrites HTML; `item.ToDisplayHtml(options)` (in the root namespace, over any `IMailItem`) builds a whole message view on top of it.
Both return a structured result and never throw for the content they are given.

This document says what the code does and what it was checked against. It is a description with a stated threat model (below), not a blanket
promise: anything that matters should still be shown in a frame with its own restrictions (see *What the output assumes*). The independent review
of the sanitiser is recorded at the end (*Review*).

## How it works

1. The input is parsed with OneHtml's WHATWG parser, scripting enabled (so `noscript` is text, as in a browser).
2. The tree is walked and the output is **written** from it, element by element, from the allowlists below. Nothing is copied through as text:
   names are checked, values are escaped, CSS is parsed with OneHtml's CSS parser and every token is serialised again.
3. The output is parsed and written again with the same rules, up to three times, until it is identical to its input. A tree that does not
   survive being serialised and parsed again is what mutation attacks on sanitisers use; if the output never settles, it is replaced by the
   page's text alone and a `LimitReached` change says so.

## Elements

Kept: `a abbr acronym address article aside b bdi bdo big blockquote br caption center cite code col colgroup dd del details dfn div dl dt em
figcaption figure font footer h1-h6 header hr i img ins kbd li main mark nav ol p picture pre q rp rt ruby s samp section small source
(inside picture only) span strike strong sub summary sup table tbody td tfoot th thead time tr tt u ul var wbr`.

Removed with their content: `script noscript template iframe object embed applet frame frameset noframes noembed title xmp plaintext textarea
select datalist audio video track canvas dialog meta link base basefont param bgsound keygen map area input svg math listing isindex portal
fencedframe slot`, and every element outside the HTML namespace (SVG and MathML bring script vectors, their own URL attributes and namespace
switches). A `style` element is read, filtered and re-emitted (below); it is removed if its `type` is not `text/css` or its `media` is not
`all`/`screen`.

Everything else (`form`, `button`, `label`, unknown elements, `html`, `head`, `body`) is **unwrapped**: its text and children stay, the tag goes.
`body` becomes the wrapper (below). Comments, processing instructions and conditional comments are dropped.

## Attributes

Allowed by name: `class id style title alt lang dir align valign width height border cellpadding cellspacing colspan rowspan hspace vspace
size span start value bgcolor color bordercolor face nowrap noshade reversed open scope headers abbr summary rules frame clear char datetime
type` (on `ol ul li`), `href` (on `a`), `src srcset sizes` (on `img`, and `srcset sizes` on `source`), `background` (on `table tr td th thead
tbody tfoot`). Numeric, colour and word attributes are checked against their shape. All other attributes, including every `on*` handler,
`srcdoc`, `formaction`, `ping`, `data-*`, `usemap`, `contenteditable`, `autofocus`, `name`, and any attribute with a namespace, are removed.
`rel`, `target` and `referrerpolicy` are never copied: links get `rel="noopener noreferrer"` (and `target="_blank"` unless
`OpenLinksInNewWindow` is false), images get `referrerpolicy="no-referrer"`.

`class` and `id` values (and `#fragment` links and the CSS selectors that name them) are prefixed with `om-`, so the host page's styles and scripts
do not reach the message by accident, and the message's names cannot shadow the page's.

## URLs

A reference is cleaned as a browser's URL parser does (leading and trailing control characters and blanks removed, tab and line breaks removed
everywhere); any other control character makes it unusable. Then:

| Form | `a href` | `img src`, `srcset`, `background`, CSS `url()` |
|---|---|---|
| `http:`/`https:` with a host, or `//host` (taken as https) | the policy (kind `Link`), default allow | the resolver for a matching `Content-Location`, else the policy, default block |
| `mailto:`, `tel:` | kept | removed |
| `cid:` | removed | the resolver (`ResolveReference`), else removed |
| relative reference | removed | the resolver (`Content-Location`, RFC 2557), else removed |
| `#fragment` | kept, prefixed | removed |
| `data:` | removed | kept only for `image/png gif jpeg jpg webp bmp avif x-icon`, base64, nothing else in the header |
| `javascript:`, `vbscript:`, `file:`, `blob:`, any other scheme, malformed schemes (`java script:`, `http:foo`) | removed | removed |

SVG is never accepted as a data image.

## CSS

`style` elements and `style` attributes are parsed and rewritten.

- **Properties** are allowlisted (colour, background, border, margin, padding, size, font, text, list, table, display, visibility, float, overflow
  and the like). `position`, `z-index`, `transform`, `filter`, `mask`, `content`, `cursor`, `animation`, `transition`, `behavior`, `-moz-binding`,
  `clip-path`, custom properties (`--x`) and `var()` are not allowed. `!important` is kept.
- **Values** are rebuilt from tokens: identifiers, numbers, dimensions, percentages, colours, strings, commas, `/ + - *`, parentheses, and the functions
  `rgb rgba hsl hsla hwb lab lch oklab oklch color calc min max clamp` and the gradient functions. `url()` goes through the URL rules above.
  Any other function (`expression`, `image-set`, `attr`, `env`, ...) removes the declaration. Escapes are decoded before they are compared, so
  `u\72l(` and `expr\65ssion(` are seen for what they are.
- **At-rules**: `@media` (with a plain media query) is kept, `@font-face` is kept when it has `font-family` and a `src` whose URLs pass the policy
  (kind `Font`); `@import` is removed and reported as a stylesheet reference; everything else (`@supports @namespace @keyframes @page @charset
  @layer @container`, nested rules) is removed.
- **Selectors** are rebuilt and confined to the wrapper: each one is prefixed `.omx `; `html`, `body` and `:root` name the wrapper itself.
  Allowed: type, class, id, `*`, attribute selectors (not on `class`, `id`, `style`), the combinators, the pseudo-classes `hover focus active visited
  link first-child last-child only-child first-of-type last-of-type only-of-type empty checked disabled enabled focus-within focus-visible`,
  `nth-*()` with a plain argument, `:not()` of these, and the pseudo-elements `first-line first-letter before after selection marker`. A
  selector that uses anything else is removed.
- Every `<` in the output is written as the escape `\3C `, so no style content can close the element.
- The wrapper rule `.omx-box{display:block;position:relative;overflow:hidden;contain:paint;isolation:isolate}` clips the message to its box, so
  negative margins and large boxes cannot cover the host page.

## Output shape

```html
<style>…box rule and the message's filtered rules…</style><div class="omx-box"><div class="omx" style="…body attributes…">…body…</div></div>
```

`bgcolor`, `text`, `background` and `style` on `body` become the style of the inner `div`.

## The remote-content policy

Every way the markup names something to fetch is reported in `SanitizedHtml.RemoteReferences` (URL, kind, outcome, where it was found) and, for
kinds the output can carry, asked of the policy:

| Vector | Kind | In the output |
|---|---|---|
| `img src`, `img srcset`, `picture/source srcset`, CSS `list-style-image` | `Image` | kept / blocked / rewritten |
| `background` attribute (`body table tr td th ...`), CSS `background*` | `Background` | kept / blocked / rewritten |
| `@font-face src` | `Font` | kept / blocked / rewritten |
| `a href` and links in plain text | `Link` | kept / blocked / rewritten (default allow) |
| `link rel=stylesheet`, `@import` | `Stylesheet` | removed (`ElementDropped`) |
| `video poster` | `Image` | element removed (`ElementDropped`) |
| `video/audio/source/track src`, `iframe`, `embed`, `object`, `input type=image`, `bgsound`, SVG `image`/`use` | `Media` | element removed (`ElementDropped`) |

`SanitizerOptions.RemoteContent` (`Allow` or `Block`, default **Block**) sets the default for everything except links. `SanitizerOptions.Policy` is
called for each occurrence with `(Url, Kind)` and returns `Allow`, `Block` or `Rewrite(url)`; it overrides the default, a throwing policy blocks.
A rewrite target must be http(s), mailto, tel or a data image; relative and application-scheme URLs are accepted when `AllowRewriteToAnyUrl` is true
(the default), never `javascript:`, `vbscript:`, `file:`, `blob:` or a non-image `data:`.

**Tracking indications** (`TrackingIndications`) are information only. An `img` with a remote URL is flagged when its width or height (attribute or style)
is at most one pixel (`TinySize`), its style hides it (`display:none`, `visibility:hidden`, `opacity:0`: `Hidden`), or its URL contains a known
open-tracking pattern (`KnownPattern`; the list is in `SanitizerContext`). Flagged images are still blocked or allowed by the policy like any other.

## `cid:` and `Content-Location`

`ToDisplayHtml` supplies the resolver: `cid:x` (percent-decoded) is matched to an attachment's `ContentId` (exact, then ignoring case); a relative
reference, or an absolute URL, is matched to `IMailAttachment.ContentLocation` (equal, or one a `/`-tail of the other, RFC 2557 §5; exact, then ignoring
case). Both MIME (`Content-ID`, `Content-Location`) and MAPI items (`PidTagAttachContentId`, `PidTagAttachContentLocation`) work. The URL is a `data:` URI
(`ReferenceForm.DataUri`: the type comes from the bytes, PNG/GIF/JPEG/WebP/BMP only, at most `MaxInlineImageBytes` per image and `MaxInlineTotalBytes`
in all) or what `AttachmentUrl` returns (`ReferenceForm.Callback`). Resolved attachments leave the attachment list. Unresolved references are listed in
`UnresolvedReferences` and removed.

## Display HTML

`ToDisplayHtml` writes `omx-display`: a small stylesheet for its own chrome, an optional header block (subject, From, To, Cc, Bcc, date, all escaped), the
`CalendarCard` hook's HTML if the caller supplied one (written as given, not sanitised), the body, and the attachment list (name, type, size).

- The **HTML body** is used unless `PreferText` is set or there is none; otherwise the **text body**.
- **Plain text** is escaped, one block per line (`white-space:pre-wrap`). With `format=flowed` (RFC 3676) lines ending in a space are joined with the next line
  of the same quote depth (the space is deleted when `delsp=yes`), the signature separator `-- ` never flows, quote depth is the count of leading `>`
  and becomes nested `blockquote`s (at most 32 deep; deeper quoting is shown at depth 32), and one leading space (space-stuffing) is removed.
- **Links in text** are only `http://` and `https://` addresses, with sentence punctuation and an unbalanced closing bracket left outside; each goes through the
  policy as a `Link`. `www.` and bare addresses are not linked. A run of address characters longer than 4,096 is not considered for a link.
- A body that cannot be decoded is shown as empty.

## Limits

`MaxInputLength` (8,000,000 chars; the rest is ignored), `MaxDepth` (256; deeper subtrees are removed), `MaxNodes` (500,000; output stops), `MaxCssLength` (1,000,000
chars), `MaxReported` (1,000 entries in each report list), URLs longer than 8,192 chars (remote) are removed. Resolving references against the message's
parts looks up content ids in a dictionary and spends at most 20 million comparisons on `Content-Location` matches per message. Reaching one sets `Truncated` and adds a
`LimitReached` change. OneHtml's own parse limits are set from these.

## Threat model

Trusted: the library, the host page that inserts the output, the caller's options and callbacks (policy, resolver, attachment URL, calendar card hook).
Untrusted: the HTML, its CSS, its URLs, attachment bytes, names and content types, and the text of plain-text bodies.

Contexts the output is written for, and what is promised in each:

| Context | Script execution | Network fetch with `Block` | Escape from the box | Form submission, top-frame navigation |
|---|---|---|---|---|
| `iframe sandbox` (no `allow-scripts`, no `allow-same-origin`), `srcdoc` or a document of its own | none; the sandbox holds even if markup were wrong | none | clipped to `omx-box` | forms are removed; links open in a new window with `noopener noreferrer`, never in the top frame |
| An element in the host page (`innerHTML`, no CSP) | none: only allowlisted elements, attributes and URL schemes are written | none | clipped to `omx-box`; host styles and ids are not reachable by name | as above |
| A preview pane or web view with script off | none | none | as above | as above |

"None" means nothing in the output is active content and nothing is fetched by the page on its own: images, backgrounds, fonts, stylesheets, frames,
media, plug-ins and SVG are removed or blocked. A link is followed only when the reader clicks it (links are allowed by default; the policy can block or
rewrite them).

Goals, in this order:

1. no script runs and no markup the browser treats as active reaches the output (script, handlers, `javascript:` and its kin, frames, plugins, forms, SVG/MathML);
2. nothing is fetched unless the policy allows it, so opening a message does not tell the sender;
3. the message cannot restyle, cover or read the host page (scoped CSS, clipped box, prefixed names, no `position`);
4. the markup survives a serialise/parse round trip, so a later `innerHTML` copy cannot change its meaning;
5. hostile size is bounded in time and output: every limit below, plus the plain-text path's bracket trimming, quote depth and attachment lookup.

Not goals: sender authenticity, phishing detection (links keep their text; a link can say one thing and go elsewhere; an invisible link can cover the
message), spoofed display names, text direction tricks (`unicode-bidi` and `direction` are allowed), visual spoofing inside the box (a message can draw a
fake header or a fake button), large or slow images, and content a policy allows (an allowed remote image is the sender's to serve).

What the sanitiser does not cover: the calendar card hook's HTML is written as given, so the hook must escape what it writes; a policy, resolver or
attachment-URL callback is trusted (their results are still checked against the URL rules, but with `AllowRewriteToAnyUrl` true any application scheme
that is not on the script/file/blob list is accepted: set it false to confine callbacks to http(s), mailto, tel and data images).

## What the output assumes

The output is a fragment of HTML (not XHTML: `&` inside the style element is literal). The host should still:

- show it in an `iframe` with `sandbox` (no `allow-scripts`, no `allow-same-origin`) or a web view with script disabled, and a `Content-Security-Policy`
  (`default-src 'none'; img-src data: …; style-src 'unsafe-inline'`), as defence in depth;
- serve it as UTF-8 and say so (`charset=utf-8`, or `srcdoc`): the writer drops control characters, but a page decoded with another character set is not covered;
- show one message per document or frame: the scope class is the same for every message, so one message's style rules and `om-` ids would reach another message
  in the same document;
- not insert it into a context other than an element's content (for example inside a `table`, a `select` or an attribute);
- keep user interface that depends on `om-`-prefixed ids and classes out of the message's namespace.

## How it is checked

- `testdata/sanitizer/xss.txt` (233 vectors) and `testdata/sanitizer/review.txt` (134 vectors from the review): our own, written from the categories in public
  evasion lists (script and handler variants, URL schemes and their encodings, CSS, SVG/MathML, parser-differential and mutation cases, every remote-fetch
  vector). `SanitizerTests` and `SanitizerReviewTests` sanitise each (block and allow) and assert the output holds only allowlisted elements, attributes and
  URL schemes, that no remote URL is written that the report does not list as allowed, and that sanitising it again changes nothing dangerous.
- `tools/sanitizer/browser-check.sh` renders every vector in headless Chrome and Firefox, raw (the control), sanitised with the default policy, and sanitised with
  remote content allowed, against a logging server: script calls and requests are counted (block: none of either; allow: every request must be for a URL the
  policy let through). A structure page parses every output four ways in the browser (div `innerHTML`, `template`, `DOMParser`, a range fragment) and fails on
  any element, attribute or URL outside the allowlists, any disagreement between the parses, and any output that changes when read back and serialised again;
  it carries positive controls. `tools/sanitizer/run-browsers.sh` runs both vector files in every installed browser.
- `tools/sanitizer/mutate.py SEED COUNT OUT.json` writes random tag soup (nesting-sensitive and raw-text elements, foreign content, comments, CDATA, quotes,
  entities); `STRUCTURE_ONLY=1 VECTORS=OUT.json tools/sanitizer/browser-check.sh DIR` runs the structure page over it.
- `tools/sanitizer/stress.py DIR` writes 32 hostile-size inputs (100,000-deep nesting, a million misnested formatting elements, 200,000 attributes, 300,000 rules,
  8 MB of text); `SanitizeTool show` sanitises each.
- `tools/sanitizer/compare-dompurify.sh` compares with the released DOMPurify on the same vectors.
- `SanitizeTool corpus` sanitises real message bodies and compares the visible words before and after, apart from the elements removed above.
- Fuzz targets `sanitizer` (invariants on the output) and `sanitizer-reparse` (OneHtml's serialiser must be idempotent on the output).

## Review

A second engineer, who did not write the sanitiser, reviewed it on 2026-10-31 as an attacker and a reader of the code: the threat model above, 134 new vectors
(`review.txt`), 12 encoded-body messages, 30 hostile callback results through every URL-bearing place, 40,000 generated tag-soup documents (80,000 outputs, each parsed four ways in both browsers), 32 hostile-size
inputs, a read of every file in `src/OneMail/Display/`, and fuzzing of the final code (`sanitizer` 7.4 million executions, `sanitizer-reparse` 7.5 million and `sanitizer-display` 6.2 million, 600 s each, no findings). Chrome 155 and Firefox 158 were run headless on Linux x64. Verdict: **no open exploitable finding**.

| # | Severity | Finding | Status |
|---|---|---|---|
| R1 | hardening | Control characters (ESC, DEL, C0, U+FFFE/U+FFFF) were written into text and attribute values; an ESC starts a character-set switch if a page is mislabelled ISO-2022-JP. | Fixed: not written (`ControlCharactersAreNotWritten`). |
| R2 | note | The calendar card hook's HTML is written as given. | Documented above; pinned by `CalendarCardHtmlIsWrittenAsGiven`. The hook must escape. |
| R3 | note | A newline after `<pre>` is written twice (the parser drops one). A browser whose `innerHTML` does not write the extra newline reads a copy one newline shorter. Whitespace only. | Accepted; `PreWithLeadingNewlineKeepsItsTextThroughOurOwnReparse`; the browser check normalises it. |
| R4 | **exploitable** (availability) | Plain-text bodies: trimming closing brackets off a link rescanned the whole address for each one, so one line of `http://x/` plus a few hundred thousand `)` took minutes (60,000 took 1.1 s; the 8 MB input limit allows hours). | Fixed: linear trimming, 4,096-character scan cap (`LongRunsOfClosingBracketsInPlainTextCostLinearTime`). |
| R5 | hardening | Plain-text `format=flowed`: one line of `>` signs wrote a block quote for each (200,000 signs: 7.8 million characters of output). | Fixed: depth capped at 32 (`QuoteDepthInFlowedTextIsCapped`). |
| R6 | **exploitable** (availability) | `cid:` and location resolution scanned every part for every reference and read each part's properties each time: 5,000 parts and 120,000 references took 2 min 36 s of CPU from a message of about 4 MB. | Fixed: ids in a dictionary, parts read once, a 20 million comparison budget for location matches; the same message takes under 2 s with the whole suite (`ManyPartsAndManyReferencesResolveInBoundedTime`). |
| R7 | hardening | The verification passes that read the output again apply no policy, so a remote URL that appeared only on a re-parse (markup the first parse did not see) would have been kept. | Fixed: a verification pass keeps a remote or caller-supplied URL only if the first pass wrote it. No vector triggers it (it needs a parser disagreement, and none was found in 40,000 generated documents in two browsers); covered by the existing suite staying green. |
| R8 | hardening | A parenthesis glued to a number, dimension, hash, identifier or string in a CSS value (`0url(x)`) is a dimension plus a block to the spec but could read as a function name to a different tokeniser. Found by the display fuzz target (whose check also flagged the word `javascript` used as an identifier; that check now looks for `javascript:`). | Fixed: the declaration is removed (`ParenthesisGluedToAValueTokenIsRemoved`). |
| N1 | note | The scope class and the `om-` prefix are the same for every message, so two messages in one document share style rules and ids. | Documented: one message per frame or document. |
| N2 | note | With `AllowRewriteToAnyUrl` (default) a callback may return any application scheme except script, file, blob, data and a few more (a deny list). Callbacks are trusted. | Documented; deferred: setting the option false confines callbacks to http(s), mailto, tel and data images. |
| N3 | note | `#fragment` links get `target="_blank"` like other links. | Deferred: harmless, opens an empty tab in some hosts. |
| N4 | note | Visual spoofing is possible inside the box: `unicode-bidi`/`direction`, invisible links over content, drawn fake headers. | Non-goal, stated above. |
| N5 | note | The browser check cannot see `https` fetches (the logging server speaks http), so protocol-relative URLs under `Allow` are checked through the report and the structure page only. | Deferred. |
| N6 | note | Inputs beyond the size limits give an empty body with a `LimitReached` change (for example 100,000-deep nesting, a million `<br>`), never an exception or a slow path: the slowest of 32 stress inputs took 1.05 s and 278 MB. | As designed. |

What came out clean: every URL scheme and obfuscation (entities, controls, tabs, NBSP, ideographic and fullwidth forms, double encoding), `srcset`/`imagesrcset`,
`ping`, `formaction`, `xlink:href`, `<base>`, `<meta http-equiv=refresh>`, SVG/MathML/`foreignObject`/`annotation-xml` and namespace-confusion forms, `noscript`,
`template`, `xmp`, `textarea`, `title`, `iframe`, `noembed`, `noframes` and `plaintext` breakouts, comments, CDATA and bogus comments, duplicate and obfuscated attribute
names, NUL bytes, every CSS fetching form (`url()`, `image-set()`, `cross-fade()`, `element()`, shorthand properties, gradients, `@import`, `@font-face`, nested at-rules,
escapes in identifiers and strings, `expression`, `behavior`, `-moz-binding`), `position:fixed` overlays and other box-escape properties, selector forms that would reach
the host page, UTF-7, ISO-2022-JP and BOM bodies, quoted-printable splits of a scheme, hostile rewrite and resolver results, and the plain-text path.

