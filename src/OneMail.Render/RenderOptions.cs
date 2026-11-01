using OneRender.Pdf;
using OneRender.Layout;
using RecursiveAI.OneMail.Display;

namespace RecursiveAI.OneMail.Render;

/// <summary>A page: its size and margins in CSS pixels (96 per inch).</summary>
/// <param name="Width">Page width, 1 to 14,400.</param>
/// <param name="Height">Page height, 1 to 14,400.</param>
/// <param name="MarginTop">Top margin, 0 or more.</param>
/// <param name="MarginRight">Right margin.</param>
/// <param name="MarginBottom">Bottom margin.</param>
/// <param name="MarginLeft">Left margin.</param>
public readonly record struct RenderPage(double Width, double Height, double MarginTop, double MarginRight, double MarginBottom, double MarginLeft)
{
    /// <summary>ISO A4 (210 x 297 mm) with half-inch margins.</summary>
    public static RenderPage A4 { get; } = new(793.7007874, 1122.5196850, 48, 48, 48, 48);

    /// <summary>US Letter (8.5 x 11 in) with half-inch margins.</summary>
    public static RenderPage Letter { get; } = new(816, 1056, 48, 48, 48, 48);

    /// <summary>Width inside the margins.</summary>
    public double ContentWidth => Width - MarginLeft - MarginRight;

    /// <summary>Height inside the margins.</summary>
    public double ContentHeight => Height - MarginTop - MarginBottom;

    internal void Validate()
    {
        const double max = 14400;
        if (!(Width >= 1 && Width <= max) || !(Height >= 1 && Height <= max))
        {
            throw new ArgumentOutOfRangeException(nameof(RenderOptions.Page), "The page size must be 1 to 14,400 CSS px.");
        }

        if (!(MarginTop >= 0 && MarginRight >= 0 && MarginBottom >= 0 && MarginLeft >= 0) || !(ContentWidth >= 1) || !(ContentHeight >= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(RenderOptions.Page), "The margins must be 0 or more and leave at least 1 CSS px of content.");
        }
    }
}

/// <summary>Options for rendering a message to PDF or PNG pages. Every default is the safe one: remote content blocked, nothing fetched.</summary>
public sealed class RenderOptions
{
    /// <summary>The options with every default.</summary>
    public static RenderOptions Default { get; } = new();

    /// <summary>
    /// How the message is turned into HTML before it is laid out: the body, <c>cid:</c> and attachment images, the remote-content policy
    /// and the calendar card hook. Default <see cref="DisplayOptions.Default"/>. <see cref="IncludeHeader"/> and
    /// <see cref="IncludeAttachmentList"/> replace the same two settings on it. The renderer itself never fetches anything, so a
    /// remote image the policy allows is still only drawn when <see cref="Resources"/> supplies its bytes.
    /// </summary>
    public DisplayOptions Display { get; init; } = DisplayOptions.Default;

    /// <summary>Show the subject / From / To / Cc / date block above the body. Default true.</summary>
    public bool IncludeHeader { get; init; } = true;

    /// <summary>Show the attachment list below the body. Default true.</summary>
    public bool IncludeAttachmentList { get; init; } = true;

    /// <summary>Page size and margins. Default <see cref="RenderPage.A4"/>.</summary>
    public RenderPage Page { get; init; } = RenderPage.A4;

    /// <summary>
    /// The fonts to draw with. Null (the default) uses the fonts packaged with OneRender (Latin, Greek, Cyrillic). Text in other scripts,
    /// CJK and emoji needs the caller to pass a provider built from the matching OneRender font packages.
    /// </summary>
    public IFontProvider? Fonts { get; init; }

    /// <summary>The document language written to the PDF and the HTML <c>lang</c>. Default <c>en</c>.</summary>
    public string Language { get; init; } = "en";

    /// <summary>
    /// Supplies the bytes of a resource (image, font, style sheet) by absolute address. Called only for addresses that are not
    /// <c>data:</c> URIs, which the renderer decodes itself. Null (the default) supplies nothing: every remote reference stays unloaded and is listed in
    /// <see cref="RenderResult.UnservedRequests"/>.
    /// </summary>
    public Func<string, byte[]?>? Resources { get; init; }

    /// <summary>Whether the PDF is tagged for accessibility. Default true. PDF only.</summary>
    public bool Tagged { get; init; } = true;

    /// <summary>The PDF conformance profile. Default the plain profile. PDF only.</summary>
    public PdfProfile Profile { get; init; }

    /// <summary>Device pixels per CSS pixel for PNG pages, 0.25 to 8, with at most 40 million pixels per page. Default 1 (96 dpi); 2 gives 192 dpi. PNG only.</summary>
    public double Scale { get; init; } = 1;

    /// <summary>The first PNG page to write, 1-based. Default 1. PNG only.</summary>
    public int FirstPage { get; init; } = 1;

    /// <summary>The last PNG page to write, 1-based; 0 means the last page of the message. Default 0. PNG only.</summary>
    public int LastPage { get; init; }

    /// <summary>Most pages a PDF may have; more throws <see cref="MailRenderException"/> with <see cref="RenderFailure.PageLimit"/>. Default 2,000.</summary>
    public int MaxPdfPages { get; init; } = 2_000;

    /// <summary>Most PNG pages one call writes; the rest are left out and reported as <see cref="RenderDefectKind.PagesLimited"/>. Default 100.</summary>
    public int MaxPngPages { get; init; } = 100;

    /// <summary>
    /// Longest time one render may take; a longer one is cancelled and throws <see cref="MailRenderException"/> with
    /// <see cref="RenderFailure.TimeLimit"/>. Default 2 minutes. <see cref="TimeSpan.Zero"/> or less means no limit.
    /// </summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(2);
}
