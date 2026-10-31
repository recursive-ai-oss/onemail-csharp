using System.Text;
using OneRender;
using OneRender.Image;
using OneRender.Layout;
using OneRender.Painting;
using OneRender.Pdf;
using RecursiveAI.OneMail.Display;

namespace RecursiveAI.OneMail.Render;

/// <summary>
/// Renders a message to PDF or PNG pages: the display HTML of the item (<see cref="MailItemDisplayExtensions.ToDisplayHtml"/>) is wrapped in a
/// document and handed to OneRender. Nothing is fetched from the network: the engine is given a resource loader that decodes
/// <c>data:</c> URIs and otherwise asks only <see cref="RenderOptions.Resources"/>.
/// </summary>
public static class MessageRenderer
{
    private const long MaxDataUriBytes = 64_000_000;

    /// <summary>The HTML document that is laid out for an item: the display HTML inside a page with the item's subject as title.</summary>
    /// <param name="item">Any item from the shared view.</param>
    /// <param name="options">Null for the defaults.</param>
    /// <param name="display">The display layer's result, which carries the sanitiser's report.</param>
    /// <returns>A complete HTML document.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> is null.</exception>
    public static string BuildDocument(IMailItem item, RenderOptions? options, out DisplayResult display)
    {
        ArgumentNullException.ThrowIfNull(item);
        options ??= RenderOptions.Default;
        DisplayOptions d = options.Display;
        var effective = new DisplayOptions
        {
            Sanitizer = d.Sanitizer,
            IncludeHeader = options.IncludeHeader,
            IncludeAttachmentList = options.IncludeAttachmentList,
            PreferText = d.PreferText,
            ReferenceForm = d.ReferenceForm,
            AttachmentUrl = d.AttachmentUrl,
            MaxInlineImageBytes = d.MaxInlineImageBytes,
            MaxInlineTotalBytes = d.MaxInlineTotalBytes,
            CalendarCard = d.CalendarCard,
        };
        display = item.ToDisplayHtml(effective);
        string title = item.Subject ?? string.Empty;
        var sb = new StringBuilder(display.Html.Length + 512);
        sb.Append("<!DOCTYPE html><html lang=\"").Append(Escape(options.Language)).Append("\"><head><meta charset=\"utf-8\"><title>")
            .Append(Escape(title)).Append("</title><style>body{margin:0;font-family:sans-serif;font-size:14px;line-height:1.4;color:#000}"
            // The display layer clips the message to its box so it cannot cover a host page. On a page of its own that clip is not needed, and
            // a clipped block is cut at the first page break (its later pages come out blank), so it is lifted here. A positioned or
            // isolated box is painted after the in-flow content around it, which put the body after the attachment list in the PDF's content order.
            + ".omx-box{position:static!important;overflow:visible!important;contain:none!important;isolation:auto!important}</style></head><body>")
            .Append(display.Html).Append("</body></html>");
        return sb.ToString();
    }

    /// <summary>Renders the item to a tagged PDF written to <paramref name="output"/>. The stream is left open.</summary>
    /// <param name="item">Any item from the shared view.</param>
    /// <param name="output">Where the file goes; written sequentially.</param>
    /// <param name="options">Null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the render; the output is then incomplete.</param>
    /// <returns>The page count, the display result and what was left out.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> or <paramref name="output"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The page or an option is out of range.</exception>
    /// <exception cref="MailRenderException">A page or time limit was reached, or OneRender refused the document.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public static Task<RenderResult> ToPdfAsync(IMailItem item, Stream output, RenderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(output);
        RenderOptions o = options ?? RenderOptions.Default;
        Validate(o);
        // The engine is synchronous and CPU-bound; running it on the pool keeps the caller's context free.
        return Task.Run(() => ToPdf(item, output, o, cancellationToken), cancellationToken);
    }

    /// <summary>Renders the item to PNG pages, asking <paramref name="openPage"/> for the stream of each page just before it is written.</summary>
    /// <param name="item">Any item from the shared view.</param>
    /// <param name="openPage">Given the 1-based page number, returns the stream that page goes to. It is left open.</param>
    /// <param name="options">Null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the render between pages and inside the engine.</param>
    /// <returns>The page count, the display result and what was left out.</returns>
    /// <remarks>
    /// A page is one viewport-high strip of the laid-out message at the page width, with the page margins around the text on the sides and
    /// at the top of the first page and the bottom of the last; unlike a PDF, a strip can cut through a line of text.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> or <paramref name="openPage"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The page or an option is out of range.</exception>
    /// <exception cref="MailRenderException">The time limit was reached or OneRender refused the document.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public static Task<RenderResult> ToPngAsync(IMailItem item, Func<int, Stream> openPage, RenderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(openPage);
        RenderOptions o = options ?? RenderOptions.Default;
        Validate(o);
        return Task.Run(() => ToPng(item, openPage, o, cancellationToken), cancellationToken);
    }

    private static void Validate(RenderOptions o)
    {
        o.Page.Validate();
        ArgumentOutOfRangeException.ThrowIfLessThan(o.Scale, 0.25);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(o.Scale, 8);
        // One PNG page is held as RGBA pixels, then encoded; 40 million pixels (160 MB) is five times an A4 page at 4x.
        if (o.Page.Width * o.Page.Height * o.Scale * o.Scale > 40_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(o), "A PNG page may have at most 40 million pixels (page size times scale squared).");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(o.FirstPage, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(o.LastPage);
        ArgumentOutOfRangeException.ThrowIfLessThan(o.MaxPdfPages, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(o.MaxPngPages, 1);
    }

    private static RenderResult ToPdf(IMailItem item, Stream output, RenderOptions o, CancellationToken ct)
    {
        string html = BuildDocument(item, o, out DisplayResult display);
        var loader = new Loader(o.Resources);
        using CancellationTokenSource? timer = Timer(o, ct, out CancellationToken token);
        var pdf = new HtmlPdfOptions
        {
            Page = new PdfPageSetup(o.Page.Width, o.Page.Height, o.Page.MarginTop, o.Page.MarginRight, o.Page.MarginBottom, o.Page.MarginLeft),
            Output = new PdfOutputOptions { Language = o.Language, Tagged = o.Tagged, Profile = o.Profile, LeaveOpen = true, MaxPages = o.MaxPdfPages },
            Resources = loader.Load,
            Fonts = o.Fonts,
        };
        try
        {
            HtmlPdfResult r = HtmlPdf.ToPdf(html, output, pdf, token);
            return Finish(r.PageCount, r.PageCount, display, r.Diagnostics, loader, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw TimeLimit(o);
        }
        catch (PdfRenderException ex)
        {
            throw new MailRenderException(
                ex.Message.Contains("pages", StringComparison.Ordinal) ? RenderFailure.PageLimit : RenderFailure.Engine, ex.Message, ex);
        }
    }

    private static RenderResult ToPng(IMailItem item, Func<int, Stream> openPage, RenderOptions o, CancellationToken ct)
    {
        string html = BuildDocument(item, o, out DisplayResult display);
        // The margins become padding on the body so one non-paged layout serves every strip.
        html = html.Replace("<style>body{margin:0;", FormattableString.Invariant(
            $"<style>body{{margin:0;padding:{o.Page.MarginTop}px {o.Page.MarginRight}px {o.Page.MarginBottom}px {o.Page.MarginLeft}px;"), StringComparison.Ordinal);
        var loader = new Loader(o.Resources);
        var store = new ImageStore(loader.Load);
        var fonts = o.Fonts ?? FontProviders.ForCurrentThread();
        int w = (int)Math.Ceiling(o.Page.Width), h = (int)Math.Ceiling(o.Page.Height);
        float dpr = (float)o.Scale;
        using CancellationTokenSource? timer = Timer(o, ct, out CancellationToken token);
        var defects = new List<RenderDefect>();
        try
        {
            var pageOptions = new HtmlPageOptions(fonts)
            {
                Images = store,
                Replaced = store,
                DevicePixelRatio = dpr,
                ViewportWidth = w,
                ViewportHeight = h,
                StylesheetBytes = loader.Load,
            };
            HtmlPage page = HtmlPage.Create(html, pageOptions, token);
            ScrollRange range = ScrollState.GetViewportRange(page.Fragments, w, h);
            double total = range.MaxY + h;
            int pages = Math.Max(1, (int)Math.Ceiling((total - 0.01) / h));
            int last = o.LastPage == 0 ? pages : Math.Min(o.LastPage, pages);
            if (o.LastPage > pages || o.FirstPage > pages)
            {
                defects.Add(new RenderDefect(RenderDefectKind.PageRangeClamped, o.FirstPage + "-" + o.LastPage + " of " + pages));
            }

            int first = Math.Min(o.FirstPage, pages);
            if (last - first + 1 > o.MaxPngPages)
            {
                defects.Add(new RenderDefect(RenderDefectKind.PagesLimited, (last - first + 1) + " pages, limit " + o.MaxPngPages));
                last = first + o.MaxPngPages - 1;
            }

            var list = new DisplayList();
            var painter = new Painter();
            var raster = new RasterOptions { DevicePixelRatio = dpr, Background = PaintColor.White, Images = store };
            int pxW = (int)Math.Ceiling(w * dpr), pxH = (int)Math.Ceiling(h * dpr);
            int written = 0;
            for (int p = first; p <= last; p++)
            {
                token.ThrowIfCancellationRequested();
                double want = (p - 1) * (double)h;
                double actual = Math.Min(want, range.MaxY);
                page.Scroll.SetViewportOffset(0, want);
                page.Paint(list, painter, token);
                RgbaImage img = DisplayListRasterizer.Render(list, pxW, pxH, raster, token);
                if (want > actual)
                {
                    // The last strip cannot scroll past the end of the content, so it shows the previous strip's tail; move that tail out.
                    img = DropRepeatedRows(img, (int)Math.Round((want - actual) * dpr));
                }

                Stream s = openPage(p);
                ArgumentNullException.ThrowIfNull(s);
                byte[] png = img.ToPng();
                s.Write(png, 0, png.Length);
                s.Flush();
                written++;
            }

            return Finish(pages, written, display, [], loader, defects);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw TimeLimit(o);
        }
    }

    // Moves the bottom rows of the image to the top and clears the rest to white, so the strip holds only the content not shown before.
    private static RgbaImage DropRepeatedRows(RgbaImage img, int shiftRows)
    {
        shiftRows = Math.Clamp(shiftRows, 0, img.Height);
        byte[] px = new byte[img.Pixels.Length];
        int stride = img.Width * 4;
        Array.Fill(px, (byte)255);
        Buffer.BlockCopy(img.Pixels, shiftRows * stride, px, 0, (img.Height - shiftRows) * stride);
        return new RgbaImage(img.Width, img.Height, px);
    }

    private static CancellationTokenSource? Timer(RenderOptions o, CancellationToken ct, out CancellationToken token)
    {
        if (o.MaxDuration <= TimeSpan.Zero)
        {
            token = ct;
            return null;
        }

        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(o.MaxDuration);
        token = cts.Token;
        return cts;
    }

    private static MailRenderException TimeLimit(RenderOptions o) =>
        new(RenderFailure.TimeLimit, "The render took longer than " + o.MaxDuration + ".");

    private static RenderResult Finish(int pages, int written, DisplayResult display, IReadOnlyList<Diagnostic> diagnostics, Loader loader, List<RenderDefect>? defects)
    {
        defects ??= [];
        if (display.Body?.Truncated == true)
        {
            defects.Add(new RenderDefect(RenderDefectKind.DisplayTruncated, "sanitiser limit"));
        }

        foreach (string r in display.UnresolvedReferences)
        {
            defects.Add(new RenderDefect(RenderDefectKind.UnresolvedReference, r));
        }

        foreach (string u in loader.Unserved)
        {
            defects.Add(new RenderDefect(RenderDefectKind.ResourceNotServed, u));
        }

        return new RenderResult(pages, written, display, diagnostics, loader.Unserved, defects);
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            sb.Append(c switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", _ => c.ToString() });
        }

        return sb.ToString();
    }

    // The engine's only way to get a resource. data: URIs are decoded here; anything else goes to the caller's callback, and without one
    // is recorded and answered with nothing, so no render can reach the network.
    private sealed class Loader(Func<string, byte[]?>? caller)
    {
        private readonly List<string> _unserved = [];

        public IReadOnlyList<string> Unserved => _unserved;

        public byte[]? Load(string address)
        {
            if (address.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return DecodeDataUri(address);
            }

            byte[]? found = caller?.Invoke(address);
            if (found is null && !_unserved.Contains(address, StringComparer.Ordinal) && _unserved.Count < 1_000)
            {
                _unserved.Add(address);
            }

            return found;
        }
    }

    // RFC 2397: data:[<mediatype>][;base64],<data>
    internal static byte[]? DecodeDataUri(string uri)
    {
        int comma = uri.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0)
        {
            return null;
        }

        ReadOnlySpan<char> header = uri.AsSpan(5, comma - 5);
        ReadOnlySpan<char> data = uri.AsSpan(comma + 1);
        if (header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
        {
            if ((long)data.Length / 4 * 3 > MaxDataUriBytes)
            {
                return null;
            }

            byte[] buffer = new byte[(data.Length / 4 * 3) + 3];
            return Convert.TryFromBase64Chars(data, buffer, out int n) ? buffer.AsSpan(0, n).ToArray() : null;
        }

        string text = Uri.UnescapeDataString(data.ToString());
        return Encoding.Latin1.GetBytes(text);
    }
}
