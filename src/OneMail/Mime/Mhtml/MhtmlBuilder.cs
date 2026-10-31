using System.Globalization;
using System.Text;
using RecursiveAI.OneMail.Mime.Transfer;

namespace RecursiveAI.OneMail.Mime.Mhtml;

/// <summary>
/// Builds an MHTML aggregate (RFC 2557): a <c>multipart/related</c> message whose first part is an HTML document and whose other
/// parts are the resources it refers to. The result opens in <see cref="MhtmlDocument"/> and in other readers.
/// </summary>
/// <remarks>
/// The HTML is written as UTF-8 in quoted-printable. Resources are written in base64. Every part gets a <c>Content-Location</c>
/// and, when given, a <c>Content-ID</c>. A relative location is resolved against the root's location, as readers do, so use
/// absolute locations (or none for the base) consistently.
/// </remarks>
public sealed class MhtmlBuilder
{
    private (byte[] Content, string ContentType, string Location, string? ContentId)? _root;
    private readonly List<(byte[] Content, string ContentType, string? Location, string? ContentId)> _resources = [];

    /// <summary>The <c>Subject</c> of the message, usually the page title. Null writes none.</summary>
    public string? Subject { get; set; }

    /// <summary>The <c>From</c> of the message, usually a note about the program that saved it. Null writes none.</summary>
    public string? From { get; set; }

    /// <summary>The <c>Date</c> of the message. Null writes none (output stays reproducible).</summary>
    public DateTimeOffset? Date { get; set; }

    /// <summary>The multipart boundary; null generates a random one. Set it for reproducible output.</summary>
    public string? Boundary { get; set; }

    /// <summary>Sets the root document.</summary>
    /// <param name="html">The HTML text. It is written as UTF-8; the markup's own <c>&lt;meta charset&gt;</c> should agree.</param>
    /// <param name="location">The URL the document was saved from, for its <c>Content-Location</c>. Absolute, so that relative references in it have a base.</param>
    /// <param name="contentId">An optional <c>Content-ID</c> without angle brackets. When set, the multipart's <c>start</c> parameter names it.</param>
    /// <returns>This builder.</returns>
    public MhtmlBuilder SetRoot(string html, string location, string? contentId = null)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(location);
        _root = (Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8", location, contentId);
        return this;
    }

    /// <summary>Adds a resource part.</summary>
    /// <param name="content">The bytes.</param>
    /// <param name="contentType">The media type, for example <c>image/png</c>.</param>
    /// <param name="location">The <c>Content-Location</c>: the URL the HTML refers to it by. Null for a part reachable only by <paramref name="contentId"/>.</param>
    /// <param name="contentId">The <c>Content-ID</c> without angle brackets, for <c>cid:</c> references; null for none.</param>
    /// <returns>This builder.</returns>
    public MhtmlBuilder AddResource(ReadOnlyMemory<byte> content, string contentType, string? location, string? contentId = null)
    {
        ArgumentNullException.ThrowIfNull(contentType);
        _resources.Add((content.ToArray(), contentType, location, contentId));
        return this;
    }

    /// <summary>Builds the message.</summary>
    /// <returns>A new <c>multipart/related</c> entity ready to write.</returns>
    /// <exception cref="InvalidOperationException">No root was set.</exception>
    public MimeMultipart Build()
    {
        if (_root is not { } root)
        {
            throw new InvalidOperationException("Call SetRoot first.");
        }

        string subtype = "related; type=\"text/html\"" + (root.ContentId is null ? string.Empty : "; start=\"<" + root.ContentId + ">\"");
        var m = new MimeMultipart(subtype, Boundary);
        m.Headers.Add("MIME-Version", "1.0");
        if (Subject is not null)
        {
            m.Headers.Add("Subject", Subject);
        }

        if (From is not null)
        {
            m.Headers.Add("From", From);
        }

        if (Date is { } d)
        {
            m.Headers.Add("Date", d.ToString("ddd, dd MMM yyyy HH:mm:ss ", CultureInfo.InvariantCulture) + (d.Offset < TimeSpan.Zero ? "-" : "+") + d.Offset.ToString("hhmm", CultureInfo.InvariantCulture));
        }

        m.Add(MakePart(root.Content, root.ContentType, root.Location, root.ContentId, ContentTransferEncoding.QuotedPrintable));
        foreach ((byte[] content, string type, string? location, string? id) in _resources)
        {
            m.Add(MakePart(content, type, location, id, ContentTransferEncoding.Base64));
        }

        return m;
    }

    /// <summary>Builds the message and writes it.</summary>
    /// <param name="output">The stream.</param>
    public void WriteTo(Stream output) => Build().WriteTo(output);

    private static MimePart MakePart(byte[] content, string contentType, string? location, string? contentId, ContentTransferEncoding encoding)
    {
        var p = new MimePart(contentType);
        if (contentId is not null)
        {
            p.Headers.Add("Content-ID", "<" + contentId + ">");
        }

        if (location is not null)
        {
            p.Headers.Add("Content-Location", location);
        }

        p.SetDecodedContent(content, encoding);
        return p;
    }
}
