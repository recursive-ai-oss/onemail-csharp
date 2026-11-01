using System.Collections.Generic;

namespace RecursiveAI.OneMail.Bodies;

/// <summary>
/// What the body conversions repaired, bounded or guessed. Nothing in this namespace throws on malformed input: each
/// conversion produces the best result it can and reports the rest as a <see cref="BodyIssue"/>. Kinds whose name starts
/// with <c>Ambiguous</c> are the one documented resolution of a known ambiguity (see <c>docs/bodies.md</c>); the others
/// are damage or a limit that was hit.
/// </summary>
public enum BodyIssueKind
{
    /// <summary>The input is longer than <c>MaxInputChars</c>; only the first part was converted.</summary>
    InputTruncated,

    /// <summary>The output reached <c>MaxOutputChars</c>; the rest of the document was not converted.</summary>
    OutputTruncated,

    /// <summary>OneHtml refused the document because it exceeds one of its parse limits (nodes, open elements, attributes); the text of what was parsed is not available, so the result is empty.</summary>
    HtmlLimitExceeded,

    /// <summary>Lists, quotes or tables nest deeper than <c>MaxNesting</c>; deeper levels were folded into the deepest one (no further prefix).</summary>
    NestingTooDeep,

    /// <summary>A table has more cells than <c>MaxTableCells</c>; the cells beyond the limit were skipped.</summary>
    TableTooLarge,

    /// <summary>A run of spaces (pre-formatted text or non-breaking spaces) is longer than <c>MaxWhitespaceRun</c>; the excess was dropped.</summary>
    WhitespaceRunBounded,

    /// <summary>Quotes in a plain-text body nest deeper than <c>MaxQuoteDepth</c>; deeper levels were folded into the deepest one.</summary>
    QuoteDepthCapped,

    /// <summary>Control characters or noncharacters were removed from text that is being turned into HTML.</summary>
    ControlCharactersRemoved,

    /// <summary>
    /// A multipart/alternative lists its HTML rendering before its plain-text one, which RFC 2046 §5.1.4 reads as "plain text is preferred". The
    /// HTML was chosen anyway, as mail clients do.
    /// </summary>
    AmbiguousAlternativeOrder,

    /// <summary>The item has an RTF body that is out of sync with its plain-text body (PidTagRtfInSync false), so the plain text was treated as stale.</summary>
    AmbiguousStaleText,

    /// <summary>The item's HTML body has no visible text and no images; the plain-text body was chosen.</summary>
    AmbiguousEmptyHtml,

    /// <summary>The item has bodies but none can be shown as text or HTML (only RTF written as RTF, with no converter result).</summary>
    OnlyRtfAvailable,
}

/// <summary>One thing a body conversion reported.</summary>
/// <param name="Kind">Which.</param>
/// <param name="Position">A zero-based UTF-16 offset in the input where it was found, or -1 when it has no position.</param>
public readonly record struct BodyIssue(BodyIssueKind Kind, int Position = -1);

/// <summary>A converted body and what the conversion reported.</summary>
/// <param name="Text">The converted text or HTML. Never null.</param>
/// <param name="Issues">The issues, in the order found; empty when the conversion was clean.</param>
public readonly record struct BodyConversion(string Text, IReadOnlyList<BodyIssue> Issues);
