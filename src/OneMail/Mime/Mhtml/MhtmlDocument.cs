using System.Text;
using RecursiveAI.OneMail.Mime.Reading;
using RecursiveAI.OneMail.Sources;

namespace RecursiveAI.OneMail.Mime.Mhtml;

/// <summary>One body part of an MHTML aggregate (RFC 2557): a resource a document can refer to by <c>cid:</c> or by location.</summary>
public sealed class MhtmlResource
{
    internal MhtmlResource(MimeEntity entity, string? contentId, string? contentLocation, string? absoluteLocation, bool isRoot)
    {
        Entity = entity;
        ContentId = contentId;
        ContentLocation = contentLocation;
        AbsoluteLocation = absoluteLocation;
        IsRoot = isRoot;
    }

    /// <summary>The part in the entity tree.</summary>
    public MimeEntity Entity { get; }

    /// <summary>The <c>Content-ID</c> without angle brackets, or null.</summary>
    public string? ContentId { get; }

    /// <summary>The <c>Content-Location</c> as written, unfolded (a fold's line break and indent removed) and trimmed, or null.</summary>
    public string? ContentLocation { get; }

    /// <summary>
    /// <see cref="ContentLocation"/> resolved against the base of the surrounding structure (RFC 2557 §5, §8.2), so it is absolute:
    /// a location with no real base becomes <c>thismessage:/…</c>. Null when the part has no location.
    /// </summary>
    public string? AbsoluteLocation { get; }

    /// <summary>Whether this is the root (start) part of the aggregate.</summary>
    public bool IsRoot { get; }

    /// <summary>The media type of the part, for example <c>image/png</c>.</summary>
    public string MediaType => Entity.MediaType;

    /// <summary>Opens the decoded content, or null when the part is a multipart or an embedded message.</summary>
    /// <returns>A read-only stream. Dispose it.</returns>
    public Stream? OpenContent() => (Entity as MimePart)?.OpenDecodedContent();

    /// <summary>The decoded content as an array, or null when the part is a multipart or an embedded message.</summary>
    /// <returns>The bytes.</returns>
    public byte[]? GetContent() => (Entity as MimePart)?.GetDecodedContent();
}

/// <summary>
/// A view of an MHTML aggregate: a <c>multipart/related</c> (or a lone HTML part) whose parts refer to each other by
/// <c>cid:</c> URLs and by <c>Content-Location</c> (RFC 2557, RFC 2392, RFC 2387). It resolves a reference found in the root document
/// to the part it names, which is what display code needs to show an embedded image or a saved web page. It changes nothing in
/// the tree: the document is a lens over the entity it was made from.
/// </summary>
/// <remarks>
/// Resolution (RFC 2557 §8.2): the reference is resolved against a base (the document's own base, or the base the caller passes, such
/// as the HTML <c>&lt;base href&gt;</c>), and the result is compared octet by octet with every part's resolved <c>Content-Location</c>.
/// When nothing matches exactly, a tolerant comparison is made (<see cref="MhtmlUri.Normalize(string)"/>): scheme and host case, hex case of
/// escapes, <c>%20</c> against a space, default ports and dot segments. <c>cid:</c> is matched against <c>Content-ID</c> only
/// (RFC 2557 §8.3), first exactly and then ignoring ASCII case. <c>mid:</c> with a content-id names a part like <c>cid:</c> does. A reference no part in this aggregate
/// satisfies is tried in each enclosing <c>multipart/related</c> (RFC 2557 §7).
/// </remarks>
public sealed class MhtmlDocument : IDisposable
{
    private readonly List<MhtmlResource> _resources = [];
    private readonly Dictionary<string, MhtmlResource> _byContentId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MhtmlResource> _byLocation = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MhtmlResource> _byNormalizedLocation = new(StringComparer.Ordinal);
    private readonly List<MimeDefect> _defects = [];
    private readonly IDisposable? _owner;

    private MhtmlDocument(MimeEntity entity, IDisposable? owner)
    {
        Entity = entity;
        _owner = owner;
        int depth = entity.Depth;
        MimeEntity[] parts = entity is MimeMultipart m ? [.. m.Children] : [entity];
        MimeEntity? root = null;
        if (entity is MimeMultipart)
        {
            Start = entity.GetContentTypeParameter("start");
            string? startId = Start is null ? null : StripAngles(Start);
            if (startId is not null)
            {
                root = parts.FirstOrDefault(p => string.Equals(p.GetContentId(), startId, StringComparison.Ordinal))
                    ?? parts.FirstOrDefault(p => string.Equals(p.GetContentId(), startId, StringComparison.OrdinalIgnoreCase));
                if (root is null)
                {
                    _defects.Add(new MimeDefect(MimeDefectKind.MhtmlStartNotFound, Math.Max(0, entity.SourceStart), 0, depth));
                }
            }

            root ??= parts.Length > 0 ? parts[0] : null;
        }
        else
        {
            root = entity;
        }

        Root = root;
        BaseUri = root is null ? MhtmlUri.ThisMessage : BaseInside(root);
        string locationBase = entity is MimeMultipart ? BaseInside(entity) : (entity.Parent is null ? MhtmlUri.ThisMessage : BaseInside(entity.Parent));
        foreach (MimeEntity p in parts)
        {
            string? cid = p.GetContentId();
            string? loc = LocationOf(p, depth, _defects);
            string? abs = loc is null ? null : MhtmlUri.Resolve(locationBase, loc);
            var r = new MhtmlResource(p, cid, loc, abs, ReferenceEquals(p, root));
            _resources.Add(r);
            if (cid is not null)
            {
                if (!_byContentId.TryAdd(cid, r))
                {
                    _defects.Add(new MimeDefect(MimeDefectKind.AmbiguousMhtmlDuplicateContentId, Math.Max(0, p.SourceStart), 0, depth));
                }
            }

            // RFC 2557 §8.3: a Content-Location holding a cid: URL is not a label for cid: references, and it is not a location either.
            if (abs is not null && !string.Equals(MhtmlUri.SchemeOf(abs), "cid", StringComparison.Ordinal))
            {
                string key = StripFragment(abs);
                if (!_byLocation.TryAdd(key, r))
                {
                    _defects.Add(new MimeDefect(MimeDefectKind.AmbiguousMhtmlDuplicateLocation, Math.Max(0, p.SourceStart), 0, depth));
                }

                _byNormalizedLocation.TryAdd(MhtmlUri.Normalize(key), r);
            }
        }
    }

    /// <summary>The entity the document was made from: the <c>multipart/related</c>, or the lone part of a single-part archive.</summary>
    public MimeEntity Entity { get; }

    /// <summary>The root part that holds the document to show: the part named by the <c>start</c> parameter, else the first part. Null when the multipart has no part.</summary>
    public MimeEntity? Root { get; }

    /// <summary>The <c>start</c> parameter of the <c>Content-Type</c> as written, or null.</summary>
    public string? Start { get; }

    /// <summary>
    /// The base URI for relative references inside the root document, before any <c>&lt;base&gt;</c> element of the document itself
    /// (RFC 2557 §5): an absolute <c>Content-Location</c> of the root part or of an enclosing entity, else <c>thismessage:/</c>. A legacy
    /// <c>Content-Base</c> header (RFC 2110, removed by RFC 2557) is honoured the same way, ahead of <c>Content-Location</c>.
    /// </summary>
    public string BaseUri { get; }

    /// <summary>The parts of the aggregate in order, the root included.</summary>
    public IReadOnlyList<MhtmlResource> Resources => _resources;

    /// <summary>Ambiguities and repairs found while indexing the parts. Offsets are in the parsed input.</summary>
    public IReadOnlyList<MimeDefect> Defects => _defects;

    /// <summary>The root part's resource, or null when there is none.</summary>
    public MhtmlResource? RootResource => _resources.FirstOrDefault(r => r.IsRoot);

    /// <summary>Makes a view of an entity that is a <c>multipart/related</c> or a single HTML part with a <c>Content-Location</c>.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns>The document, or null when the entity is neither.</returns>
    public static MhtmlDocument? TryCreate(MimeEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (entity is MimeMultipart && entity.MediaType == "multipart/related")
        {
            return new MhtmlDocument(entity, null);
        }

        if (entity is MimePart && entity.MediaType == "text/html" && entity.Headers.GetFirst("Content-Location") is not null)
        {
            return new MhtmlDocument(entity, null);
        }

        return null;
    }

    /// <summary>Finds the first <c>multipart/related</c> in a message, looking through the tree in document order.</summary>
    /// <param name="tree">The message or any entity.</param>
    /// <returns>The document, or null when there is none.</returns>
    public static MhtmlDocument? Find(MimeEntity tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        if (TryCreate(tree) is { } top)
        {
            return top;
        }

        return FindRelated(tree);
    }

    // Below the top, only a multipart/related counts: a lone HTML part inside another multipart is not an aggregate.
    private static MhtmlDocument? FindRelated(MimeEntity e)
    {
        if (e is MimeMultipart && e.MediaType == "multipart/related")
        {
            return new MhtmlDocument(e, null);
        }

        foreach (MimeEntity c in e.ChildEntities())
        {
            if (FindRelated(c) is { } d)
            {
                return d;
            }
        }

        return null;
    }

    /// <summary>Reads an <c>.mht</c> file. The file stays open, and bodies are read from it when asked for, until the document is disposed.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The document, or null when the file is not an MHTML aggregate. Dispose it.</returns>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static MhtmlDocument? Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        FileSource source = FileSource.Open(path);
        try
        {
            MimeEntity e = MimeEntity.Load(source);
            MhtmlDocument? d = TryCreate(e);
            if (d is null)
            {
                source.Dispose();
                return null;
            }

            return new MhtmlDocument(e, source);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <summary>Reads an MHTML aggregate from a stream. A seekable stream must stay open while the document is used.</summary>
    /// <param name="stream">The stream, at the start of the message.</param>
    /// <returns>The document, or null when the data is not an MHTML aggregate.</returns>
    public static MhtmlDocument? Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return TryCreate(MimeEntity.Load(stream));
    }

    /// <summary>Writes the aggregate; parts nobody changed are copied byte for byte.</summary>
    /// <param name="output">The stream.</param>
    public void WriteTo(Stream output) => Entity.WriteTo(output);

    /// <summary>Releases the file opened by <see cref="Open(string)"/>.</summary>
    public void Dispose() => _owner?.Dispose();

    /// <summary>
    /// Finds the part a reference in the root document names.
    /// </summary>
    /// <param name="reference">A URL as found in the HTML: <c>cid:logo@example</c>, <c>mid:id/part</c>, <c>images/a.png</c>, <c>http://host/a.png</c>. Surrounding white space is ignored; a fragment is ignored.</param>
    /// <param name="baseUri">The base set by the document itself (an HTML <c>&lt;base href&gt;</c>), absolute or relative to <see cref="BaseUri"/>; null to use <see cref="BaseUri"/>.</param>
    /// <returns>The part, or null when the aggregate (and the <c>multipart/related</c> structures around it) has none.</returns>
    public MhtmlResource? Resolve(string reference, string? baseUri = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        string r = reference.Trim();
        if (r.Length == 0)
        {
            return null;
        }

        string? scheme = MhtmlUri.SchemeOf(r);
        string? target = null;
        if (scheme is "cid" or "mid")
        {
            if (!TryContentIdOf(r, scheme, out string id))
            {
                return null;
            }

            for (MhtmlDocument? d = this; d is not null; d = d.Enclosing())
            {
                MhtmlResource? hit = d.ByContentId(id);
                if (hit is not null)
                {
                    return hit;
                }
            }

            return null;
        }

        string b = baseUri is null ? BaseUri : MhtmlUri.Resolve(BaseUri, baseUri);
        target = StripFragment(MhtmlUri.Resolve(b, r));
        for (MhtmlDocument? d = this; d is not null; d = d.Enclosing())
        {
            if (d._byLocation.TryGetValue(target, out MhtmlResource? hit) || d._byNormalizedLocation.TryGetValue(MhtmlUri.Normalize(target), out hit))
            {
                return hit;
            }
        }

        return null;
    }

    /// <summary>Resolves a reference to an absolute URI against the document's base (RFC 3986 §5.2), without looking for a part.</summary>
    /// <param name="reference">The URL as found in the HTML.</param>
    /// <param name="baseUri">An HTML <c>&lt;base href&gt;</c>, or null.</param>
    /// <returns>The absolute URI; <c>thismessage:/…</c> when the document has no real base.</returns>
    public string ResolveToAbsolute(string reference, string? baseUri = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        string b = baseUri is null ? BaseUri : MhtmlUri.Resolve(BaseUri, baseUri);
        return MhtmlUri.Resolve(b, reference.Trim());
    }

    private MhtmlResource? ByContentId(string id)
    {
        if (_byContentId.TryGetValue(id, out MhtmlResource? r))
        {
            return r;
        }

        // RFC 2392 §2 compares content-ids as written; mail in the wild changes their case in transit.
        return _resources.FirstOrDefault(x => x.ContentId is not null && string.Equals(x.ContentId, id, StringComparison.OrdinalIgnoreCase));
    }

    private bool TryContentIdOf(string url, string scheme, out string id)
    {
        // RFC 2392 §2: "cid:" content-id and "mid:" message-id ["/" content-id], percent-encoded, without angle brackets.
        string rest = MhtmlUri.PercentDecode(url[(scheme.Length + 1)..]);
        id = string.Empty;
        if (scheme == "cid")
        {
            id = StripAngles(StripFragment(rest));
            return id.Length > 0;
        }

        int slash = rest.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            return false; // a bare message-id names a whole message, which an aggregate does not hold
        }

        string mid = StripAngles(rest[..slash]);
        id = StripAngles(StripFragment(rest[(slash + 1)..]));
        MimeEntity top = Entity;
        while (top.Parent is { } p && p is not MimeMessagePart)
        {
            top = p;
        }

        string? own = top.GetMessageId();
        return id.Length > 0 && (own is null || string.Equals(own, mid, StringComparison.Ordinal));
    }

    private MhtmlDocument? Enclosing()
    {
        for (MimeEntity? p = Entity.Parent; p is not null; p = p.Parent)
        {
            if (p is MimeMultipart && p.MediaType == "multipart/related")
            {
                return new MhtmlDocument(p, null);
            }
        }

        return null;
    }

    private static string StripFragment(string s)
    {
        int h = s.IndexOf('#', StringComparison.Ordinal);
        return h < 0 ? s : s[..h];
    }

    private static string StripAngles(string s)
    {
        string t = s.Trim();
        if (t.Length >= 2 && t[0] == '<' && t[^1] == '>')
        {
            t = t[1..^1];
        }

        return t;
    }

    // RFC 2557 §4.2 and §8.2(a): the value is unfolded; the line break and the indentation of a fold are not part of the URI.
    private static string? HeaderUri(MimeHeader? h)
    {
        if (h is null)
        {
            return null;
        }

        ReadOnlySpan<byte> v = h.RawValue.Span;
        var bytes = new List<byte>(v.Length);
        for (int i = 0; i < v.Length; i++)
        {
            if (v[i] is (byte)'\r' or (byte)'\n')
            {
                while (i + 1 < v.Length && v[i + 1] is (byte)'\r' or (byte)'\n' or (byte)' ' or (byte)'\t')
                {
                    i++;
                }

                continue;
            }

            bytes.Add(v[i]);
        }

        string s = ReportEncoding(bytes).Trim(' ', '\t');
        return s.Length == 0 ? null : s;
    }

    private static string ReportEncoding(List<byte> bytes)
    {
        byte[] arr = [.. bytes];
        return System.Text.Unicode.Utf8.IsValid(arr) ? Encoding.UTF8.GetString(arr) : Text.MailEncoding.Windows1252.GetString(arr);
    }

    private static string? LocationOf(MimeEntity e, int depth, List<MimeDefect> defects)
    {
        MimeHeader? h = e.Headers.GetFirst("Content-Location");
        string? s = HeaderUri(h);
        if (h is not null && s is null)
        {
            defects.Add(new MimeDefect(MimeDefectKind.InvalidContentLocation, Math.Max(0, h.SourceOffset), h.Raw.Length, depth));
        }
        else if (s is not null && s.Any(char.IsControl))
        {
            defects.Add(new MimeDefect(MimeDefectKind.InvalidContentLocation, Math.Max(0, h!.SourceOffset), h.Raw.Length, depth));
        }

        return s;
    }

    // RFC 2557 §5: the base for relative URIs inside e (and for the Content-Location of e's children) is found in this order:
    // a legacy Content-Base, an absolute Content-Location of e, then the same question one level up; at the top, "thismessage:/".
    private static string BaseInside(MimeEntity e)
    {
        string above = e.Parent is null ? MhtmlUri.ThisMessage : BaseInside(e.Parent);
        string? cb = HeaderUri(e.Headers.GetFirst("Content-Base"));
        if (cb is not null)
        {
            return MhtmlUri.Resolve(above, cb);
        }

        string? loc = HeaderUri(e.Headers.GetFirst("Content-Location"));
        if (loc is not null)
        {
            string abs = MhtmlUri.Resolve(above, loc);
            if (!abs.StartsWith(MhtmlUri.ThisMessage, StringComparison.Ordinal))
            {
                return abs;
            }
        }

        return above;
    }
}
