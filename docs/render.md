# Rendering a message to PDF and PNG

`RecursiveAI.OneMail.Render` (package `RecursiveAI.OneMail.Render`) turns any item of the shared view into a tagged PDF or into PNG pages with
[OneRender](https://github.com/recursive-ai-oss/onerender-csharp): no browser, no network, no native code. It is native-platform only (not browser WebAssembly).

```csharp
IMailItem message = Mail.Parse(File.ReadAllBytes("message.eml"));
await message.ToPdfAsync(File.Create("message.pdf"));                       // one tagged PDF
await message.ToPngAsync(page => File.Create($"message-{page}.png"));       // one PNG per page
```

Both are extension methods over `MessageRenderer`; they add nothing of their own. Streams are written sequentially and left open.

## What is laid out

The input is `item.ToDisplayHtml(...)` (see [`display-html.md`](display-html.md)): the optional header block, the calendar card hook, the sanitised HTML body or the
plain-text body, and the optional attachment list, with `cid:` and `Content-Location` images already turned into `data:` URIs from the message's own parts.
`MessageRenderer.BuildDocument` wraps it in a document whose `<title>` is the subject and whose `lang` is `RenderOptions.Language`, and adds three rules:

- a base font (`sans-serif`, 14 px);
- `.omx-box{position:static; overflow:visible; contain:none; isolation:auto}`. The display layer clips a message to its box so it cannot cover a host page. On a page of its
  own the clip is not needed, a clipped block is cut at the first page break (every later page of a long message came out blank), and a positioned or isolated box is
  painted after the content around it (the attachment list came before the body in the PDF's reading order);
- for PNG, the page margins as padding on `body`.

Because the display layer already enforces the remote-content policy, the sanitiser's limits (input size, depth, node count) apply here too; a body cut at a limit
is reported as `RenderDefectKind.DisplayTruncated`.

## Nothing is fetched

OneRender asks its host for every resource (images, fonts, style sheets) and never opens a connection itself. This package gives it one loader:

| Address | Result |
|---|---|
| `data:` URI (RFC 2397, base64 or percent-encoded, at most 64 MB decoded) | decoded here |
| anything else | `RenderOptions.Resources` if set, else nothing; the address goes in `RenderResult.UnservedRequests` and as a `ResourceNotServed` defect |

With the default policy (remote content blocked) the display HTML names no remote resource at all, so the list is empty. If the caller's policy allows remote
images, the layout asks for them, still gets nothing unless the caller's `Resources` callback supplies bytes, and the result lists what was asked for. The
callback is the only place a caller can bring remote content in, and then it is the caller's fetch.

## Options (`RenderOptions`)

| Option | Default | |
|---|---|---|
| `Display` | `DisplayOptions.Default` | everything the display layer takes: sanitiser and policy, `PreferText`, `CalendarCard`, inline-image limits |
| `IncludeHeader`, `IncludeAttachmentList` | true | replace the same two settings of `Display` |
| `Page` | `RenderPage.A4` (793.7 x 1122.5 CSS px, 48 px margins) | also `RenderPage.Letter` or any size, 1 to 14,400 px |
| `Fonts` | null | null = the fonts packaged with OneRender |
| `Language` | `en` | `lang` of the document and the PDF's language |
| `Resources` | null | see above |
| `Tagged`, `Profile` | true, plain | PDF only; `Profile` is OneRender's `PdfProfile` |
| `Scale`, `FirstPage`, `LastPage` | 1, 1, 0 (to the end) | PNG only; `Scale` is device pixels per CSS px (2 = 192 dpi), at most 40 million pixels a page |
| `MaxPdfPages`, `MaxPngPages` | 2,000, 100 | more PDF pages throw `MailRenderException` (`PageLimit`); more PNG pages are left out and reported (`PagesLimited`) |
| `MaxDuration` | 2 minutes | a longer render is cancelled and throws `MailRenderException` (`TimeLimit`) |

## Pages

- **PDF** is paginated by OneRender as print media: lines are not cut, tables repeat their header rows, links to `#id` become page links, and the page count is
  `RenderResult.PageCount`.
- **PNG** is not paginated: the message is laid out once at the page width and cut into page-high strips, so a strip boundary can cut through a line. The first
  strip starts at the top margin and the last ends with the bottom margin. `FirstPage`/`LastPage` choose strips; `PageCount` is always the total.
- Content wider than the page (a 60-column table, a 6,000 px image) is clipped at the page's right margin edge as print layout does; nothing is scaled to fit.

## Fonts

The default fonts cover Latin, Greek, Cyrillic and common symbols. A character no font has is drawn as nothing and the text stays extractable (OneRender 1.3.2); it
is not reported. For CJK, emoji and other scripts reference the matching `RecursiveAI.OneRender.Fonts.*` package in the application: it registers itself and the
default provider then falls back to it. `RenderOptions.Fonts` takes an `IFontProvider` for anything else. The engine keeps one font provider per thread, so the first
render on each pool thread loads the faces.

## Defects and limits

| `RenderDefectKind` | Meaning |
|---|---|
| `DisplayTruncated` | the sanitiser stopped at a limit |
| `UnresolvedReference` | a `cid:` or attachment reference matched no part (or was over the inline limits) |
| `ResourceNotServed` | the layout asked for a non-`data:` address and got nothing |
| `PagesLimited`, `PageRangeClamped` | PNG pages left out because of `MaxPngPages`, or a range partly past the end |

`RenderResult.Diagnostics` carries OneRender's own list (unsupported CSS and so on) unchanged.

## How it was checked

Method and numbers are in [`compatibility.md`](compatibility.md). `tools/render/run.sh` runs the checks: every item of the MSG, PST/OST, TNEF, OLM, DBX, Mime4j, dovecot
and Enron-sample corpora and the generated cases go to PDF and PNG; each PDF's text, extracted by poppler's `pdftotext -raw`, is compared with the text of the
display HTML (white space, soft hyphens, zero-width and bidi control characters removed, compatibility forms folded); headless Chrome prints the same HTML for a
page-count and page-1 pixel comparison (reported, not gating: the engines use different fonts).
