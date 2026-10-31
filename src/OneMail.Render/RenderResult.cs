using OneRender;
using RecursiveAI.OneMail.Display;

namespace RecursiveAI.OneMail.Render;

/// <summary>What a rendered message needed that the renderer reports instead of failing.</summary>
public enum RenderDefectKind
{
    /// <summary>The display layer stopped at a resource limit (input size, depth, node count); the page shows part of the body.</summary>
    DisplayTruncated,

    /// <summary>A <c>cid:</c> or attachment reference matched no attachment (or was over the inline size limits) and was left out.</summary>
    UnresolvedReference,

    /// <summary>The layout asked for an address that is not a <c>data:</c> URI and <see cref="RenderOptions.Resources"/> had nothing; the resource is missing from the page.</summary>
    ResourceNotServed,

    /// <summary>More PNG pages than <see cref="RenderOptions.MaxPngPages"/> exist; the later ones were not written.</summary>
    PagesLimited,

    /// <summary>The requested PNG page range lies partly or wholly beyond the last page.</summary>
    PageRangeClamped,
}

/// <summary>One thing the render left out or changed.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Detail">The address, reference or number involved.</param>
public readonly record struct RenderDefect(RenderDefectKind Kind, string Detail);

/// <summary>What a render produced.</summary>
public sealed class RenderResult
{
    internal RenderResult(int pageCount, int pagesWritten, DisplayResult display, IReadOnlyList<Diagnostic> diagnostics,
        IReadOnlyList<string> unserved, IReadOnlyList<RenderDefect> defects)
    {
        PageCount = pageCount;
        PagesWritten = pagesWritten;
        Display = display;
        Diagnostics = diagnostics;
        UnservedRequests = unserved;
        Defects = defects;
    }

    /// <summary>Pages the message has: all of a PDF's, and the number a PNG render would give without a page range.</summary>
    public int PageCount { get; }

    /// <summary>Pages written: <see cref="PageCount"/> for a PDF, the pages of the range for PNG.</summary>
    public int PagesWritten { get; }

    /// <summary>The display layer's result: the HTML that was laid out, what the sanitiser changed, remote references and their outcome.</summary>
    public DisplayResult Display { get; }

    /// <summary>What OneRender approximated or left out (unsupported CSS, missing glyphs, ...).</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>Every address the layout asked for that was not a <c>data:</c> URI and that <see cref="RenderOptions.Resources"/> did not supply, in order, without repeats. Nothing was fetched for any of them.</summary>
    public IReadOnlyList<string> UnservedRequests { get; }

    /// <summary>What the render left out; empty for a message that rendered completely.</summary>
    public IReadOnlyList<RenderDefect> Defects { get; }
}

/// <summary>Why a render was stopped.</summary>
public enum RenderFailure
{
    /// <summary>The PDF would have more than <see cref="RenderOptions.MaxPdfPages"/> pages, or OneRender's own page limit.</summary>
    PageLimit,

    /// <summary>The render took longer than <see cref="RenderOptions.MaxDuration"/>.</summary>
    TimeLimit,

    /// <summary>OneRender refused the document (a limit of its own or a profile rule); see the inner exception.</summary>
    Engine,
}

/// <summary>A render stopped at a limit. The output written so far is not a complete file.</summary>
public sealed class MailRenderException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="failure">What stopped the render.</param>
    /// <param name="message">A description.</param>
    /// <param name="inner">The underlying exception, if any.</param>
    public MailRenderException(RenderFailure failure, string message, Exception? inner = null)
        : base(message, inner)
    {
        Failure = failure;
    }

    /// <summary>What stopped the render.</summary>
    public RenderFailure Failure { get; }
}
