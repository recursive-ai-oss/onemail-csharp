using System.Text;

namespace RecursiveAI.OneMail.Display;

/// <summary>The shape of a URL after the browser-style clean-up of <see cref="SanitizerContext.NormalizeUrl"/>.</summary>
internal enum UrlForm
{
    /// <summary>Not usable: empty, control characters, or a scheme that is not well formed.</summary>
    Invalid = 0,
    Http,
    Mailto,
    Tel,
    Cid,
    DataImage,
    Fragment,
    Relative,
    ProtocolRelative,
    OtherScheme,
}

/// <summary>
/// The state of one sanitising run: options, what was changed, remote references found, and the URL rules shared by the HTML and CSS
/// halves. A run is single-threaded.
/// </summary>
internal sealed class SanitizerContext(SanitizerOptions options, bool verify)
{
    public readonly SanitizerOptions Options = options;

    /// <summary>
    /// Verify mode re-reads the sanitiser's own output: no class/id prefix, no wrapper, and the layout properties of the wrapper rule are accepted.
    /// The output of a run that is verified equals its input, which is the stability the re-parse check relies on.
    /// </summary>
    public readonly bool Verify = verify;

    public readonly List<SanitizerChange> Changes = [];
    public int ChangeCount;
    public readonly List<RemoteReference> Remote = [];
    public readonly List<TrackingIndication> Tracking = [];
    public bool Truncated;
    public int CssBudget = options.MaxCssLength;

    public void Change(SanitizerChangeKind kind, string detail)
    {
        ChangeCount++;
        if (Changes.Count < Options.MaxReported)
        {
            Changes.Add(new SanitizerChange(kind, Short(detail)));
        }
    }

    /// <summary>Shortens a name taken from the input so a report never repeats attacker-sized text.</summary>
    public static string Short(string s)
    {
        StringBuilder sb = new(Math.Min(s.Length, 64));
        foreach (char c in s)
        {
            if (sb.Length >= 64)
            {
                break;
            }

            sb.Append(c < 0x20 || c == 0x7F ? '?' : c);
        }

        return sb.ToString();
    }

    public void AddRemote(string url, RemoteContentKind kind, RemoteContentOutcome outcome, string source)
    {
        if (Remote.Count < Options.MaxReported)
        {
            Remote.Add(new RemoteReference(url, kind, outcome, source));
        }
    }

    // WHATWG URL: leading and trailing C0 controls and spaces are stripped, and tab, LF and CR are removed everywhere. Any other control
    // character left over makes the reference unusable; that is stricter than a browser and removes a whole class of scheme-smuggling tricks.
    public static string? NormalizeUrl(string raw)
    {
        int start = 0;
        int end = raw.Length;
        while (start < end && raw[start] <= ' ')
        {
            start++;
        }

        while (end > start && raw[end - 1] <= ' ')
        {
            end--;
        }

        if (start == end)
        {
            return null;
        }

        StringBuilder? sb = null;
        for (int i = start; i < end; i++)
        {
            char c = raw[i];
            if (c is '\t' or '\n' or '\r')
            {
                sb ??= new StringBuilder(raw, start, i - start, end - start);
                continue;
            }

            if (c < 0x20 || c == 0x7F || c is '\u2028' or '\u2029' or '\u0085')
            {
                return null;
            }

            sb?.Append(c);
        }

        return sb is null ? raw.Substring(start, end - start) : sb.ToString();
    }

    private static bool IsSlash(char c) => c is '/' or '\\';

    public static UrlForm Classify(string url, out string rest)
    {
        rest = url;
        int i = 0;
        while (i < url.Length && url[i] is not (':' or '/' or '\\' or '?' or '#'))
        {
            i++;
        }

        if (i < url.Length && url[i] == ':')
        {
            if (i == 0)
            {
                return UrlForm.Invalid;
            }

            for (int k = 0; k < i; k++)
            {
                char c = url[k];
                bool ok = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') || (k > 0 && (c is (>= '0' and <= '9') or '+' or '-' or '.'));
                if (!ok)
                {
                    return UrlForm.Invalid;
                }
            }

            string scheme = url[..i].ToLowerInvariant();
            rest = url[(i + 1)..];
            switch (scheme)
            {
                case "http":
                case "https":
                    // "http:foo" and "http:/foo" are relative-ish in a browser; only a full authority is a remote reference.
                    return HasAuthority(rest) ? UrlForm.Http : UrlForm.Invalid;
                case "mailto":
                    return UrlForm.Mailto;
                case "tel":
                    return UrlForm.Tel;
                case "cid":
                    return UrlForm.Cid;
                case "data":
                    return IsDataImage(rest) ? UrlForm.DataImage : UrlForm.OtherScheme;
                default:
                    return UrlForm.OtherScheme;
            }
        }

        if (url.Length >= 2 && IsSlash(url[0]) && IsSlash(url[1]))
        {
            rest = url[2..];
            return rest.Length == 0 ? UrlForm.Invalid : UrlForm.ProtocolRelative;
        }

        return url[0] == '#' ? UrlForm.Fragment : UrlForm.Relative;
    }

    private static bool HasAuthority(string rest)
    {
        // After "http:" a browser skips any run of slashes; what is left must name a host.
        int i = 0;
        while (i < rest.Length && IsSlash(rest[i]))
        {
            i++;
        }

        return i >= 2 && i < rest.Length && rest[i] is not ('?' or '#');
    }

    // A data URI is accepted only for a raster image type, base64 coded (RFC 2397). SVG is excluded: it can carry script.
    private static readonly string[] DataImageTypes = ["image/png", "image/gif", "image/jpeg", "image/jpg", "image/webp", "image/bmp", "image/avif", "image/x-icon", "image/vnd.microsoft.icon"];

    private static bool IsDataImage(string rest)
    {
        int comma = rest.IndexOf(',');
        if (comma < 0)
        {
            return false;
        }

        string head = rest[..comma].Trim().ToLowerInvariant();
        if (!head.EndsWith(";base64", StringComparison.Ordinal))
        {
            return false;
        }

        string type = head[..^7];
        if (Array.IndexOf(DataImageTypes, type) < 0)
        {
            return false;
        }

        for (int i = comma + 1; i < rest.Length; i++)
        {
            char c = rest[i];
            bool b64 = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '+' or '/' or '=' or '-' or '_' or '%';
            if (!b64)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The absolute form of a remote URL: protocol-relative references take https, backslashes before the host become slashes.</summary>
    public static string AbsoluteRemote(string url, UrlForm form)
    {
        if (form == UrlForm.ProtocolRelative)
        {
            return "https://" + url[2..].Replace('\\', '/');
        }

        // "http:\\host" and "http:///host" both name the host in a browser. Keep exactly two slashes.
        int colon = url.IndexOf(':');
        string scheme = url[..colon].ToLowerInvariant();
        int i = colon + 1;
        while (i < url.Length && IsSlash(url[i]))
        {
            i++;
        }

        string tail = url[i..];
        int stop = tail.IndexOfAny(['/', '\\', '?', '#']);
        string host = stop < 0 ? tail : tail[..stop];
        string after = stop < 0 ? string.Empty : tail[stop..].Replace('\\', '/');
        return scheme + "://" + host + after;
    }

    /// <summary>
    /// Whether a URL a caller produced may be written. http(s), mailto, tel and data images always; relative and application-scheme URLs
    /// only when <see cref="SanitizerOptions.AllowRewriteToAnyUrl"/>; never script, file or blob schemes or a data URI that is not an image.
    /// </summary>
    public string? AcceptCallerUrl(string? url)
    {
        if (url is null || url.Length > 8_000_000)
        {
            return null;
        }

        string? n = NormalizeUrl(url);
        if (n is null)
        {
            return null;
        }

        UrlForm f = Classify(n, out _);
        switch (f)
        {
            case UrlForm.Http:
            case UrlForm.ProtocolRelative:
                return AbsoluteRemote(n, f);
            case UrlForm.Mailto:
            case UrlForm.Tel:
            case UrlForm.DataImage:
                return n;
            case UrlForm.Relative:
            case UrlForm.Fragment:
                return Options.AllowRewriteToAnyUrl ? n : null;
            case UrlForm.OtherScheme:
                if (!Options.AllowRewriteToAnyUrl)
                {
                    return null;
                }

                string scheme = n[..n.IndexOf(':')].ToLowerInvariant();
                return scheme is "javascript" or "vbscript" or "data" or "file" or "blob" or "livescript" or "mocha" or "jar" or "view-source" or "filesystem"
                    ? null
                    : n;
            default:
                return null;
        }
    }

    /// <summary>Asks the policy about a remote URL and records the answer. Returns the URL to write, or null when it is removed.</summary>
    public string? ApplyPolicy(string absoluteUrl, RemoteContentKind kind, string source)
    {
        RemoteContentDecision d;
        try
        {
            if (Options.Policy is { } p)
            {
                d = p(new RemoteContentRequest(absoluteUrl, kind));
            }
            else
            {
                // Following a link fetches nothing; the default only blocks what the page would load by itself.
                d = kind == RemoteContentKind.Link || Options.RemoteContent == RemoteContentAction.Allow
                    ? RemoteContentDecision.Allow
                    : RemoteContentDecision.Block;
            }
        }
#pragma warning disable CA1031 // A caller's policy that throws must not take the sanitiser down; the URL is blocked.
        catch (Exception)
#pragma warning restore CA1031
        {
            d = RemoteContentDecision.Block;
        }

        if (d.Action == RemoteContentAction.Allow)
        {
            AddRemote(absoluteUrl, kind, RemoteContentOutcome.Allowed, source);
            return absoluteUrl;
        }

        if (d.Action == RemoteContentAction.Rewrite && AcceptCallerUrl(d.Url) is { } rewritten)
        {
            AddRemote(absoluteUrl, kind, RemoteContentOutcome.Rewritten, source);
            return rewritten;
        }

        AddRemote(absoluteUrl, kind, RemoteContentOutcome.Blocked, source);
        Change(SanitizerChangeKind.RemoteBlocked, source);
        return null;
    }

    /// <summary>
    /// Resolves a URL that loads content (image, background, font) to the URL to write. Remote URLs go through the policy; <c>cid:</c> and relative
    /// references go to the resolver; data images pass; anything else is removed. Null means remove the reference.
    /// </summary>
    public string? ResolveFetchUrl(string raw, RemoteContentKind kind, string source)
    {
        if (Verify)
        {
            // Re-reading our own output: what is there was already decided.
            return NormalizeUrl(raw) is { } v ? AcceptCallerUrl(v) : null;
        }

        string? n = NormalizeUrl(raw);
        if (n is null || n.Length > 8_000_000)
        {
            Change(SanitizerChangeKind.UrlRemoved, source);
            return null;
        }

        UrlForm f = Classify(n, out string rest);
        switch (f)
        {
            case UrlForm.Http:
            case UrlForm.ProtocolRelative:
                if (n.Length > 8192)
                {
                    return Drop(source);
                }

                string abs = AbsoluteRemote(n, f);
                // RFC 2557: an absolute URL equal to a part's Content-Location names that part, not the network.
                if (Resolve(new ContentReference(abs, false)) is { } part)
                {
                    return part;
                }

                return ApplyPolicy(abs, kind, source);
            case UrlForm.DataImage:
                return n;
            case UrlForm.Cid:
            case UrlForm.Relative:
                if (Resolve(new ContentReference(f == UrlForm.Cid ? PercentDecode(rest) : n, f == UrlForm.Cid)) is { } ok)
                {
                    return ok;
                }

                return Drop(source);
            default:
                return Drop(source);
        }
    }

    private string? Resolve(ContentReference r)
    {
        if (Options.ResolveReference is not { } resolve)
        {
            return null;
        }

        try
        {
            return AcceptCallerUrl(resolve(r));
        }
#pragma warning disable CA1031 // A throwing resolver means "not found".
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    private string? Drop(string source)
    {
        Change(SanitizerChangeKind.UrlRemoved, source);
        return null;
    }

    public static string PercentDecode(string s)
    {
        if (s.IndexOf('%') < 0)
        {
            return s;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(s);
        List<byte> o = new(bytes.Length);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == '%' && i + 2 < bytes.Length && Hex(bytes[i + 1]) >= 0 && Hex(bytes[i + 2]) >= 0)
            {
                o.Add((byte)((Hex(bytes[i + 1]) << 4) | Hex(bytes[i + 2])));
                i += 2;
            }
            else
            {
                o.Add(bytes[i]);
            }
        }

        return Encoding.UTF8.GetString(o.ToArray());
    }

    private static int Hex(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };

    // Open-tracking URL patterns seen in newsletters. Matching is on the lower-cased URL; this only reports, it never decides.
    private static readonly string[] TrackingFragments =
    [
        "/track/open", "/tracking/open", "/open.gif", "/open.png", "/o.gif", "/pixel.gif", "/pixel.png", "/pixel?", "/beacon", "/wf/open",
        "/e2t/o/", "/trk?", "open.aspx", "/openrate", "/emailopen", "/email/open", "/mail/open", "/open?", "/opentrack", "/track.gif", "/tracker.gif", "/trackopen", "/ss/o/", "list-manage.com/track", "ct.sendgrid.net", "mandrillapp.com/track", "/t.gif",
        "/spacer.gif?", "google-analytics.com/collect", "mailtrack.io", "hubspotemail", 
        "emltrk.com", "sparkpostmail", "/ptrack/", "/r/o/",
    ];

    public static bool LooksLikeTrackingUrl(string url)
    {
        string l = url.ToLowerInvariant();
        foreach (string f in TrackingFragments)
        {
            if (l.Contains(f, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
