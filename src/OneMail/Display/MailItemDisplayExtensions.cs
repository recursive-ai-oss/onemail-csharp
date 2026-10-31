using RecursiveAI.OneMail.Display;

namespace RecursiveAI.OneMail;

/// <summary>Display HTML for any <see cref="IMailItem"/>.</summary>
public static class MailItemDisplayExtensions
{
    /// <summary>
    /// Renders a message as an HTML fragment for a browser or web view: an optional header block, the body (the HTML body sanitised by
    /// <see cref="HtmlSanitizer"/>, else the plain-text body as escaped text), and an optional attachment list. <c>cid:</c> and
    /// <c>Content-Location</c> references are resolved to the message's own attachments.
    /// </summary>
    /// <param name="item">A message from any reader (MIME, MSG, PST, ...).</param>
    /// <param name="options">Defaults when null: header and attachment list on, remote content blocked, images inline as data URIs.</param>
    /// <returns>
    /// The fragment and the sanitiser's report. Never throws for the content of the item: a body that cannot be read is shown as empty.
    /// The fragment is written to be inserted in a page of its own or a sandboxed frame; docs/display-html.md says what it assumes.
    /// </returns>
    public static DisplayResult ToDisplayHtml(this IMailItem item, DisplayOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        return DisplayHtmlBuilder.Build(item, options ?? DisplayOptions.Default);
    }
}
