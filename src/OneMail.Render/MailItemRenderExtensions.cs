using RecursiveAI.OneMail.Render;

namespace RecursiveAI.OneMail;

/// <summary>PDF and PNG output for any <see cref="IMailItem"/>, in the RecursiveAI.OneMail.Render package. Each call is one call over <see cref="MessageRenderer"/>.</summary>
public static class MailItemRenderExtensions
{
    /// <summary>Renders the item to a tagged PDF written to <paramref name="output"/>, which stays open.</summary>
    /// <param name="item">A message from any reader.</param>
    /// <param name="output">Where the file goes.</param>
    /// <param name="options">Null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the render.</param>
    /// <returns>The page count and what was left out.</returns>
    /// <exception cref="MailRenderException">A page or time limit was reached.</exception>
    public static Task<RenderResult> ToPdfAsync(this IMailItem item, Stream output, RenderOptions? options = null, CancellationToken cancellationToken = default) =>
        MessageRenderer.ToPdfAsync(item, output, options, cancellationToken);

    /// <summary>Renders the item to PNG pages, asking <paramref name="openPage"/> for each page's stream (1-based number); the streams stay open.</summary>
    /// <param name="item">A message from any reader.</param>
    /// <param name="openPage">Returns the stream for a page.</param>
    /// <param name="options">Null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the render.</param>
    /// <returns>The page count and what was left out.</returns>
    /// <exception cref="MailRenderException">The time limit was reached.</exception>
    public static Task<RenderResult> ToPngAsync(this IMailItem item, Func<int, Stream> openPage, RenderOptions? options = null, CancellationToken cancellationToken = default) =>
        MessageRenderer.ToPngAsync(item, openPage, options, cancellationToken);
}
