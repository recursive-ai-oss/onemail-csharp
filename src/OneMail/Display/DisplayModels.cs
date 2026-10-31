namespace RecursiveAI.OneMail.Display;

/// <summary>Why HTML or CSS refers to a remote resource. Passed to the remote-content policy with each URL.</summary>
public enum RemoteContentKind
{
    /// <summary><c>img src</c> and <c>srcset</c>, <c>picture/source</c>, <c>video poster</c>, <c>input type=image</c>, CSS <c>list-style-image</c> and similar.</summary>
    Image = 0,

    /// <summary>A style sheet: <c>&lt;link rel=stylesheet&gt;</c> and CSS <c>@import</c>. The sanitiser never fetches or inlines one, so these are always reported as dropped.</summary>
    Stylesheet,

    /// <summary>A CSS <c>@font-face</c> source.</summary>
    Font,

    /// <summary>The <c>background</c> attribute of <c>body</c>, <c>table</c>, <c>td</c> and <c>th</c>, and CSS <c>background*</c> images.</summary>
    Background,

    /// <summary>An <c>a href</c> or <c>area href</c> navigation target. Nothing is fetched by the markup itself; the policy may block or rewrite it (for example to open through a warning page).</summary>
    Link,

    /// <summary><c>audio</c>, <c>video</c>, <c>source</c>, <c>track</c>, <c>iframe</c>, <c>object</c>, <c>embed</c>: elements the sanitiser removes. Reported, never emitted.</summary>
    Media,
}

/// <summary>What the policy decided for one URL.</summary>
public enum RemoteContentAction
{
    /// <summary>Keep the URL as it is.</summary>
    Allow = 0,

    /// <summary>Remove the reference (the attribute or the CSS declaration).</summary>
    Block,

    /// <summary>Replace the URL with <see cref="RemoteContentDecision.Url"/>.</summary>
    Rewrite,
}

/// <summary>A policy answer: allow, block, or rewrite to another URL.</summary>
public readonly record struct RemoteContentDecision(RemoteContentAction Action, string? Url = null)
{
    /// <summary>Keep the URL.</summary>
    public static RemoteContentDecision Allow => new(RemoteContentAction.Allow);

    /// <summary>Remove the reference.</summary>
    public static RemoteContentDecision Block => new(RemoteContentAction.Block);

    /// <summary>Use <paramref name="url"/> instead. A rewrite to an empty string, or to a URL with a scheme other than http, https, mailto, tel or a data image, or a relative reference, is treated as a block unless the rewrite target is a relative reference or an application scheme the caller chose; see <see cref="SanitizerOptions.AllowRewriteToAnyUrl"/>.</summary>
    public static RemoteContentDecision Rewrite(string url) => new(RemoteContentAction.Rewrite, url);
}

/// <summary>One URL the policy is asked about.</summary>
/// <param name="Url">The absolute http or https URL, with tabs and line breaks removed, as it appeared in the message.</param>
/// <param name="Kind">Why the markup refers to it.</param>
public readonly record struct RemoteContentRequest(string Url, RemoteContentKind Kind);

/// <summary>What happened to a remote reference.</summary>
public enum RemoteContentOutcome
{
    /// <summary>The URL is in the output unchanged.</summary>
    Allowed = 0,

    /// <summary>The reference was removed by the policy.</summary>
    Blocked,

    /// <summary>The URL in the output is the policy's replacement.</summary>
    Rewritten,

    /// <summary>The element that carried the reference is not allowed at all and was removed, so nothing can be fetched through it whatever the policy says.</summary>
    ElementDropped,
}

/// <summary>A remote URL found in the message, and what was done about it.</summary>
/// <param name="Url">The URL as written in the message (never the rewrite).</param>
/// <param name="Kind">Why it was referenced.</param>
/// <param name="Outcome">What the output does with it.</param>
/// <param name="Source">Where it was found, for example <c>img[src]</c>, <c>style:background-image</c> or <c>link[href]</c>.</param>
public sealed record RemoteReference(string Url, RemoteContentKind Kind, RemoteContentOutcome Outcome, string Source);

/// <summary>Reasons an image is flagged as a tracking pixel.</summary>
[Flags]
public enum TrackingReasons
{
    /// <summary>No indication.</summary>
    None = 0,

    /// <summary>Its width or height is 0 or 1 pixel (attribute or style).</summary>
    TinySize = 1,

    /// <summary>Its style hides it: <c>display:none</c>, <c>visibility:hidden</c>, <c>opacity:0</c>, or a zero-size box.</summary>
    Hidden = 2,

    /// <summary>The URL matches a well-known open-tracking pattern.</summary>
    KnownPattern = 4,
}

/// <summary>An image that looks like an open-tracking pixel. Information only: the policy still decides whether it loads.</summary>
/// <param name="Url">The image URL.</param>
/// <param name="Reasons">Why it was flagged.</param>
public sealed record TrackingIndication(string Url, TrackingReasons Reasons);

/// <summary>The kind of change the sanitiser made to the input.</summary>
public enum SanitizerChangeKind
{
    /// <summary>An element and everything inside it was removed.</summary>
    ElementRemoved = 0,

    /// <summary>An element was removed and its children kept.</summary>
    ElementUnwrapped,

    /// <summary>An attribute was removed.</summary>
    AttributeRemoved,

    /// <summary>A URL was removed because its scheme or form is not allowed.</summary>
    UrlRemoved,

    /// <summary>A CSS rule or at-rule was removed.</summary>
    CssRuleRemoved,

    /// <summary>A CSS declaration was removed.</summary>
    CssDeclarationRemoved,

    /// <summary>A remote reference was blocked by the policy.</summary>
    RemoteBlocked,

    /// <summary>A resource limit was reached; the output is partial.</summary>
    LimitReached,
}

/// <summary>One thing the sanitiser removed or neutralised. <see cref="Detail"/> is a short name (an element, attribute or property name, never the removed content).</summary>
public readonly record struct SanitizerChange(SanitizerChangeKind Kind, string Detail);

/// <summary>How <c>cid:</c> and <c>Content-Location</c> references are turned into URLs.</summary>
public enum ContentReferenceForm
{
    /// <summary>The attachment bytes become a <c>data:</c> URI (images only; limited by <see cref="DisplayOptions.MaxInlineImageBytes"/>).</summary>
    DataUri = 0,

    /// <summary>The URL comes from <see cref="DisplayOptions.AttachmentUrl"/>.</summary>
    Callback,
}

/// <summary>Options of <see cref="HtmlSanitizer"/>.</summary>
public sealed class SanitizerOptions
{
    /// <summary>The default options: remote content blocked, links allowed, links open in a new window with <c>rel="noopener noreferrer"</c>.</summary>
    public static SanitizerOptions Default { get; } = new();

    /// <summary>What to do with remote images, backgrounds and fonts when <see cref="Policy"/> is null. Default <see cref="RemoteContentAction.Block"/>. Links are not affected (see <see cref="RemoteContentKind.Link"/>).</summary>
    public RemoteContentAction RemoteContent { get; init; } = RemoteContentAction.Block;

    /// <summary>
    /// Called for every remote URL of every kind, in document order, once per occurrence. Overrides <see cref="RemoteContent"/>.
    /// A throwing policy blocks that URL.
    /// </summary>
    public Func<RemoteContentRequest, RemoteContentDecision>? Policy { get; init; }

    /// <summary>
    /// Resolves a reference that names a part of the message: <c>cid:</c> URLs (the argument is the text after <c>cid:</c>, percent-decoded) and relative references
    /// (<c>Content-Location</c>, RFC 2557). Returns the URL to use or null to drop the reference. The sanitiser accepts the result if it is a data image URI,
    /// an http(s) URL, or (when <see cref="AllowRewriteToAnyUrl"/>) a relative or application URL.
    /// </summary>
    public Func<ContentReference, string?>? ResolveReference { get; init; }

    /// <summary>
    /// When true, a URL returned by <see cref="ResolveReference"/> or a policy rewrite may be relative or use an application-defined scheme (never
    /// <c>javascript:</c>, <c>vbscript:</c>, <c>data:</c> other than images, <c>file:</c> or <c>blob:</c>). Default true: the caller chose it.
    /// </summary>
    public bool AllowRewriteToAnyUrl { get; init; } = true;

    /// <summary>Add <c>target="_blank"</c> to links. Default true; <c>rel="noopener noreferrer"</c> is always added.</summary>
    public bool OpenLinksInNewWindow { get; init; } = true;

    /// <summary>Longest input in UTF-16 code units; the rest is ignored and a <see cref="SanitizerChangeKind.LimitReached"/> change is reported. Default 8,000,000.</summary>
    public int MaxInputLength { get; init; } = 8_000_000;

    /// <summary>Deepest element nesting kept; deeper subtrees are removed. Default 256.</summary>
    public int MaxDepth { get; init; } = 256;

    /// <summary>Most nodes written; the output stops there. Default 500,000.</summary>
    public int MaxNodes { get; init; } = 500_000;

    /// <summary>Most CSS characters read from style elements and attributes together. Default 1,000,000.</summary>
    public int MaxCssLength { get; init; } = 1_000_000;

    /// <summary>Most entries kept in <see cref="SanitizedHtml.Changes"/> and <see cref="SanitizedHtml.RemoteReferences"/> each. Default 1,000.</summary>
    public int MaxReported { get; init; } = 1_000;
}

/// <summary>A reference from the HTML to a part of the message.</summary>
/// <param name="Value">For a <c>cid:</c> reference the content ID without angle brackets; for a relative reference the text as written.</param>
/// <param name="IsContentId">True for <c>cid:</c>, false for a relative (Content-Location) reference.</param>
public readonly record struct ContentReference(string Value, bool IsContentId);

/// <summary>The result of <see cref="HtmlSanitizer.Sanitize(string, SanitizerOptions?)"/>.</summary>
public sealed class SanitizedHtml
{
    internal SanitizedHtml(string html, List<SanitizerChange> changes, int changeCount, List<RemoteReference> remote, List<TrackingIndication> tracking, bool truncated)
    {
        Html = html;
        Changes = changes;
        ChangeCount = changeCount;
        RemoteReferences = remote;
        TrackingIndications = tracking;
        Truncated = truncated;
    }

    /// <summary>
    /// The sanitised fragment: a <c>&lt;style&gt;</c> element holding the filtered, scoped CSS (absent when there is none) followed by a wrapper <c>div</c>
    /// holding the body. Meant to be inserted as a fragment where the caller accepts the wrapper's inline layout styles; see docs/display-html.md.
    /// </summary>
    public string Html { get; }

    /// <summary>What was removed or neutralised, in document order, at most <see cref="SanitizerOptions.MaxReported"/> entries.</summary>
    public IReadOnlyList<SanitizerChange> Changes { get; }

    /// <summary>How many changes there were in all (may exceed <see cref="Changes"/>.Count).</summary>
    public int ChangeCount { get; }

    /// <summary>Every remote URL found, with its outcome (at most <see cref="SanitizerOptions.MaxReported"/>).</summary>
    public IReadOnlyList<RemoteReference> RemoteReferences { get; }

    /// <summary>Images that look like tracking pixels.</summary>
    public IReadOnlyList<TrackingIndication> TrackingIndications { get; }

    /// <summary>True when a resource limit stopped the work and the output is partial.</summary>
    public bool Truncated { get; }
}

/// <summary>Options of <c>ToDisplayHtml</c>.</summary>
public sealed class DisplayOptions
{
    /// <summary>The default options.</summary>
    public static DisplayOptions Default { get; } = new();

    /// <summary>Options of the sanitiser that cleans the HTML body. <see cref="SanitizerOptions.ResolveReference"/> is supplied by the display layer unless set.</summary>
    public SanitizerOptions Sanitizer { get; init; } = SanitizerOptions.Default;

    /// <summary>Show a block with subject, From, To, Cc and date before the body. Default true.</summary>
    public bool IncludeHeader { get; init; } = true;

    /// <summary>Show the list of attachments (names, types, sizes; inline images used by the body excluded) after the body. Default true.</summary>
    public bool IncludeAttachmentList { get; init; } = true;

    /// <summary>Use the plain-text body even when an HTML body exists. Default false.</summary>
    public bool PreferText { get; init; }

    /// <summary>How <c>cid:</c> and <c>Content-Location</c> references resolve. Default <see cref="ContentReferenceForm.DataUri"/>.</summary>
    public ContentReferenceForm ReferenceForm { get; init; } = ContentReferenceForm.DataUri;

    /// <summary>For <see cref="ContentReferenceForm.Callback"/>: the URL for an attachment (null drops the reference).</summary>
    public Func<IMailAttachment, string?>? AttachmentUrl { get; init; }

    /// <summary>Largest image turned into a data URI, in bytes. Default 5,000,000. Larger images are left unresolved.</summary>
    public long MaxInlineImageBytes { get; init; } = 5_000_000;

    /// <summary>All data URIs together, in bytes. Default 25,000,000.</summary>
    public long MaxInlineTotalBytes { get; init; } = 25_000_000;

    /// <summary>
    /// A place for the calendar card of appointment and meeting items: called with the item, it returns an HTML fragment that is written as given
    /// (the caller is responsible for it; it is not sanitised) before the body, or null for none.
    /// </summary>
    public Func<IMailItem, string?>? CalendarCard { get; init; }
}

/// <summary>The result of <c>ToDisplayHtml</c>.</summary>
public sealed class DisplayResult
{
    internal DisplayResult(string html, SanitizedHtml? body, bool usedText, IReadOnlyList<string> resolved, IReadOnlyList<string> unresolved)
    {
        Html = html;
        Body = body;
        UsedTextBody = usedText;
        ResolvedAttachments = resolved;
        UnresolvedReferences = unresolved;
    }

    /// <summary>The display HTML fragment.</summary>
    public string Html { get; }

    /// <summary>The sanitiser's result for the HTML body (changes, remote references, tracking); null when the text body or no body was shown.</summary>
    public SanitizedHtml? Body { get; }

    /// <summary>True when the plain-text body was rendered.</summary>
    public bool UsedTextBody { get; }

    /// <summary>The <c>cid:</c> and location references that were resolved to attachments.</summary>
    public IReadOnlyList<string> ResolvedAttachments { get; }

    /// <summary>References that matched no attachment (or exceeded the inline limits) and were dropped.</summary>
    public IReadOnlyList<string> UnresolvedReferences { get; }

    /// <summary>Remote URLs found in the HTML body (empty for text).</summary>
    public IReadOnlyList<RemoteReference> RemoteReferences => Body?.RemoteReferences ?? [];

    /// <summary>Tracking indications for the HTML body.</summary>
    public IReadOnlyList<TrackingIndication> TrackingIndications => Body?.TrackingIndications ?? [];
}
