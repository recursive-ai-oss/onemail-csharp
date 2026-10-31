using System.Text;

namespace RecursiveAI.OneMail.Rtf;

/// <summary>How <see cref="RtfHtmlConverter"/> refers to the pictures it finds.</summary>
public enum RtfImageMode
{
    /// <summary>The picture bytes are written into the HTML as a <c>data:</c> URI, so the HTML stands alone. This is the default.</summary>
    DataUri = 0,

    /// <summary>The HTML refers to the picture as <c>cid:</c> plus <see cref="RtfImage.ContentId"/>; the caller attaches <see cref="RtfHtmlResult.Images"/>.</summary>
    ContentId,
}

/// <summary>Settings and limits for <see cref="RtfHtmlConverter"/>.</summary>
public sealed class RtfHtmlOptions
{
    /// <summary>
    /// Supplies the decoder for a Windows code page number (1252, 932, 10000…); return <see langword="null"/> when it is unknown, and the document's
    /// default code page is used with <see cref="RtfDefectKind.AmbiguousUnknownCodePage"/>. <see langword="null"/> (the default) uses the platform's
    /// built-in encodings plus the .NET code-page provider, without registering anything globally.
    /// </summary>
    public Func<int, Encoding?>? CodePageDecoder { get; init; }

    /// <summary>The deepest group nesting accepted; see <see cref="RtfReaderOptions.MaxGroupDepth"/>. Default 1000.</summary>
    public int MaxGroupDepth { get; init; } = 1000;

    /// <summary>How pictures are referred to. Default <see cref="RtfImageMode.DataUri"/>.</summary>
    public RtfImageMode ImageMode { get; init; } = RtfImageMode.DataUri;

    /// <summary>The largest picture accepted, in decoded bytes. A larger one is replaced by a placeholder with <see cref="RtfDefectKind.PictureLimitExceeded"/>. Default 16 MiB.</summary>
    public int MaxPictureBytes { get; init; } = 16 << 20;

    /// <summary>The most picture bytes accepted for the whole document. Default 64 MiB.</summary>
    public int MaxTotalPictureBytes { get; init; } = 64 << 20;

    /// <summary>
    /// The most structural items (paragraphs, text runs, table cells, pictures) built from one document. Beyond it the rest of the document is
    /// dropped with <see cref="RtfDefectKind.ContentLimitExceeded"/>. Default 4,000,000.
    /// </summary>
    public int MaxNodes { get; init; } = 4_000_000;

    /// <summary>The most characters of text or HTML produced (each separately). Beyond it the rest is dropped with <see cref="RtfDefectKind.ContentLimitExceeded"/>. Default 64 Mi characters.</summary>
    public int MaxOutputChars { get; init; } = 64 << 20;
}

/// <summary>A picture found in an RTF document.</summary>
public sealed class RtfImage
{
    internal RtfImage(int index, string contentType, byte[] data, bool isDisplayable, int widthPx, int heightPx, string? description)
    {
        Index = index;
        ContentType = contentType;
        Data = data;
        IsDisplayable = isDisplayable;
        WidthPx = widthPx;
        HeightPx = heightPx;
        Description = description;
        ContentId = "rtfimage" + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The picture's position among the document's pictures, from 0.</summary>
    public int Index { get; }

    /// <summary>The media type: <c>image/png</c>, <c>image/jpeg</c>, <c>image/x-emf</c>, <c>image/x-wmf</c>, <c>image/bmp</c> or <c>image/x-pict</c>.</summary>
    public string ContentType { get; }

    /// <summary>The picture bytes (for a device-independent bitmap, a complete BMP file).</summary>
    public byte[] Data { get; }

    /// <summary>True for PNG, JPEG and BMP, which the HTML shows. False for metafiles and Mac pictures, which are not rendered: the HTML has a placeholder, and the bytes are here for the caller.</summary>
    public bool IsDisplayable { get; }

    /// <summary>The display width in pixels from <c>\picwgoal</c> and <c>\picscalex</c>, or 0 when the document gives none.</summary>
    public int WidthPx { get; }

    /// <summary>The display height in pixels, or 0.</summary>
    public int HeightPx { get; }

    /// <summary>The picture's description (<c>wzDescription</c>), or null.</summary>
    public string? Description { get; }

    /// <summary>The content ID used in <c>cid:</c> references in <see cref="RtfImageMode.ContentId"/> mode: <c>rtfimage1</c>, <c>rtfimage2</c>…</summary>
    public string ContentId { get; }
}

/// <summary>The outcome of <see cref="RtfHtmlConverter.Convert"/>.</summary>
public sealed class RtfHtmlResult
{
    private readonly RtfDocument _document;
    private string? _text;

    internal RtfHtmlResult(RtfDocument document, string html, IReadOnlyList<RtfDefect> defects)
    {
        _document = document;
        Html = html;
        Defects = defects;
    }

    /// <summary>
    /// The document as an HTML fragment: one <c>&lt;div class="rtf-body"&gt;</c> holding paragraphs, lists, tables and headings, with the document's usual
    /// font and size on the div. Empty when the input is not RTF. Well-formed; the same input gives the same bytes.
    /// </summary>
    public string Html { get; }

    /// <summary>The same content as plain text: paragraphs on lines of their own (CR LF), lists with bullets or numbers, table cells separated by tabs, links as <c>text &lt;url&gt;</c>. Built on first use.</summary>
    public string Text => _text ??= RtfTextWriter.Write(_document);

    /// <summary>The pictures in document order; see <see cref="RtfImage.IsDisplayable"/>.</summary>
    public IReadOnlyList<RtfImage> Images => _document.Images;

    /// <summary>The number of <c>\objattph</c> attachment positions in the text (the HTML has an empty <c>span data-rtf-attachment</c> at each, numbered from 0).</summary>
    public int AttachmentPlaceholders => _document.AttachmentPlaceholders;

    /// <summary>The Windows code page the document declared or implied; 1252 if neither.</summary>
    public int DefaultCodePage => _document.DefaultCodePage;

    /// <summary>Everything repaired, guessed or dropped, with byte offsets into the RTF. At most 256 are kept.</summary>
    public IReadOnlyList<RtfDefect> Defects { get; }
}

/// <summary>
/// Converts an RTF document that was written as RTF (not an HTML or text encapsulation: see <see cref="RtfDeencapsulator"/>) to HTML and plain text, for
/// the mail-body subset of RTF 1.9.1 that Outlook and Word write: character formatting, paragraphs, lists, tables, hyperlinks and pictures. Contains no
/// dependency on the rest of OneMail.
/// </summary>
/// <remarks>
/// Never throws on malformed input: damage is a typed <see cref="RtfDefect"/> and the readable part is converted. Text in hidden runs, headers, footers,
/// footnotes and unknown ignorable destinations is left out. Style sheets are read only for heading names; their other properties are not inherited.
/// The behaviour is written down in docs/rtf.md.
/// </remarks>
public static class RtfHtmlConverter
{
    /// <summary>Converts an RTF document to HTML and text.</summary>
    /// <param name="rtf">The decompressed RTF bytes (see <see cref="Compression.RtfDecompressor"/>).</param>
    /// <param name="options">Settings and limits; null for the defaults.</param>
    /// <returns>The result. For input that does not start with <c>{\rtf</c> it is empty with <see cref="RtfDefectKind.NotRtf"/>.</returns>
    public static RtfHtmlResult Convert(ReadOnlySpan<byte> rtf, RtfHtmlOptions? options = null)
    {
        options ??= new RtfHtmlOptions();
        var defects = new RtfDefectList();
        RtfDocument doc = RtfDocumentParser.Parse(rtf, options, defects);
        string html = RtfHtmlWriter.Write(doc, options, defects);
        return new RtfHtmlResult(doc, html, defects.Items);
    }
}
