using System.Globalization;
using System.Text;

namespace RecursiveAI.OneMail.Mime.Mhtml;

/// <summary>
/// URI reference resolution and comparison for MHTML (RFC 2557 §5 and §8.2), by the algorithm of RFC 3986 §5.2. Nothing here
/// throws on odd input: any string is a valid reference, as in RFC 3986 appendix B.
/// </summary>
public static class MhtmlUri
{
    /// <summary>The base URI RFC 2557 §5(e) prescribes when no document supplies one.</summary>
    public const string ThisMessage = "thismessage:/";

    /// <summary>
    /// Resolves <paramref name="reference"/> against <paramref name="baseUri"/> (RFC 3986 §5.2.2, strict). The fragment of the
    /// reference is kept.
    /// </summary>
    /// <param name="baseUri">An absolute URI. When it has no scheme, <see cref="ThisMessage"/> is used in its place.</param>
    /// <param name="reference">A URI reference, absolute or relative.</param>
    /// <returns>The target URI. A reference with a scheme is returned with its dot segments removed.</returns>
    public static string Resolve(string? baseUri, string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        Parts r = Split(reference);
        Parts b = Split(string.IsNullOrEmpty(baseUri) ? ThisMessage : baseUri);
        if (b.Scheme is null)
        {
            b = Split(ThisMessage);
        }

        var t = new Parts();
        if (r.Scheme is not null)
        {
            t.Scheme = r.Scheme;
            t.Authority = r.Authority;
            t.Path = RemoveDotSegments(r.Path);
            t.Query = r.Query;
        }
        else
        {
            if (r.Authority is not null)
            {
                t.Authority = r.Authority;
                t.Path = RemoveDotSegments(r.Path);
                t.Query = r.Query;
            }
            else
            {
                if (r.Path.Length == 0)
                {
                    t.Path = b.Path;
                    t.Query = r.Query ?? b.Query;
                }
                else
                {
                    t.Path = r.Path[0] == '/' ? RemoveDotSegments(r.Path) : RemoveDotSegments(Merge(b, r.Path));
                    t.Query = r.Query;
                }

                t.Authority = b.Authority;
            }

            t.Scheme = b.Scheme;
        }

        t.Fragment = r.Fragment;
        return t.ToString();
    }

    /// <summary>
    /// A form of the URI for tolerant comparison: no fragment, scheme and host in lower case, the default port of http and https
    /// dropped, an empty path with an authority written <c>/</c>, spaces written <c>%20</c>, percent escapes in upper case, and
    /// escapes of unreserved characters decoded (RFC 3986 §6.2.2). Dot segments are removed. The path of a <c>file:</c> URI is put in lower case.
    /// </summary>
    /// <param name="uri">A URI or URI reference.</param>
    /// <returns>The normalised text. Two URIs with the same result name the same resource for MHTML purposes.</returns>
    public static string Normalize(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        Parts p = Split(uri);
        p.Fragment = null;
        p.Scheme = p.Scheme?.ToLowerInvariant();
        if (p.Authority is not null)
        {
            int at = p.Authority.LastIndexOf('@');
            string user = at >= 0 ? p.Authority[..(at + 1)] : string.Empty;
            string host = (at >= 0 ? p.Authority[(at + 1)..] : p.Authority).ToLowerInvariant();
            if ((p.Scheme == "http" && host.EndsWith(":80", StringComparison.Ordinal)) || (p.Scheme == "https" && host.EndsWith(":443", StringComparison.Ordinal)))
            {
                host = host[..host.LastIndexOf(':')];
            }

            p.Authority = user + host;
            if (p.Path.Length == 0)
            {
                p.Path = "/";
            }
        }

        if (p.Scheme is not null)
        {
            p.Path = RemoveDotSegments(p.Path);
        }

        if (p.Scheme == "file")
        {
            // Saved pages name Windows files (file:///C:/...), whose paths are not case-sensitive, and web servers do not matter here.
            p.Path = p.Path.ToLowerInvariant();
        }

        p.Path = NormalizeEscapes(p.Path);
        if (p.Query is not null)
        {
            p.Query = NormalizeEscapes(p.Query);
        }

        return p.ToString();
    }

    /// <summary>The scheme of a URI reference in lower case, or null when it is relative.</summary>
    /// <param name="reference">The reference.</param>
    /// <returns>The scheme without the colon, or null.</returns>
    public static string? SchemeOf(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return Split(reference).Scheme?.ToLowerInvariant();
    }

    /// <summary>Decodes percent escapes in <paramref name="text"/> as UTF-8 (RFC 2392 §2 uses this for <c>cid:</c> and <c>mid:</c> URLs). Bad escapes stay as written.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The decoded text.</returns>
    public static string PercentDecode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Contains('%', StringComparison.Ordinal))
        {
            return text;
        }

        var bytes = new List<byte>(text.Length);
        foreach (char c in text)
        {
            bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
        }

        var outBytes = new List<byte>(bytes.Count);
        for (int i = 0; i < bytes.Count; i++)
        {
            if (bytes[i] == (byte)'%' && i + 2 < bytes.Count && IsHex(bytes[i + 1]) && IsHex(bytes[i + 2]))
            {
                outBytes.Add((byte)Convert.ToInt32(Encoding.ASCII.GetString([bytes[i + 1], bytes[i + 2]]), 16));
                i += 2;
            }
            else
            {
                outBytes.Add(bytes[i]);
            }
        }

        return Encoding.UTF8.GetString([.. outBytes]);
    }

    private static bool IsHex(byte b) => b is (>= (byte)'0' and <= (byte)'9') or (>= (byte)'a' and <= (byte)'f') or (>= (byte)'A' and <= (byte)'F');

    private sealed class Parts
    {
        public string? Scheme;
        public string? Authority;
        public string Path = string.Empty;
        public string? Query;
        public string? Fragment;

        public override string ToString()
        {
            // RFC 3986 §5.3
            var sb = new StringBuilder();
            if (Scheme is not null)
            {
                sb.Append(Scheme).Append(':');
            }

            if (Authority is not null)
            {
                sb.Append("//").Append(Authority);
            }

            sb.Append(Path);
            if (Query is not null)
            {
                sb.Append('?').Append(Query);
            }

            if (Fragment is not null)
            {
                sb.Append('#').Append(Fragment);
            }

            return sb.ToString();
        }
    }

    // RFC 3986 appendix B: ^(([^:/?#]+):)?(//([^/?#]*))?([^?#]*)(\?([^#]*))?(#(.*))?
    private static Parts Split(string s)
    {
        var p = new Parts();
        int i = 0;
        int colon = -1;
        for (int k = 0; k < s.Length; k++)
        {
            char c = s[k];
            if (c == ':')
            {
                colon = k;
                break;
            }

            if (c is '/' or '?' or '#')
            {
                break;
            }
        }

        if (colon > 0)
        {
            p.Scheme = s[..colon];
            i = colon + 1;
        }

        if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '/')
        {
            int e = s.AsSpan(i + 2).IndexOfAny('/', '?', '#');
            int end = e < 0 ? s.Length : i + 2 + e;
            p.Authority = s[(i + 2)..end];
            i = end;
        }

        int pe = s.AsSpan(i).IndexOfAny('?', '#');
        int pathEnd = pe < 0 ? s.Length : i + pe;
        p.Path = s[i..pathEnd];
        i = pathEnd;
        if (i < s.Length && s[i] == '?')
        {
            int h = s.IndexOf('#', i);
            int qe = h < 0 ? s.Length : h;
            p.Query = s[(i + 1)..qe];
            i = qe;
        }

        if (i < s.Length && s[i] == '#')
        {
            p.Fragment = s[(i + 1)..];
        }

        return p;
    }

    private static string Merge(Parts b, string path)
    {
        if (b.Authority is not null && b.Path.Length == 0)
        {
            return "/" + path;
        }

        int slash = b.Path.LastIndexOf('/');
        return slash < 0 ? path : b.Path[..(slash + 1)] + path;
    }

    // RFC 3986 §5.2.4
    private static string RemoveDotSegments(string path)
    {
        if (path.Length == 0 || (!path.Contains("./", StringComparison.Ordinal) && !path.EndsWith("/.", StringComparison.Ordinal) && !path.EndsWith("/..", StringComparison.Ordinal) && path != "." && path != ".."))
        {
            return path;
        }

        string input = path;
        var sb = new StringBuilder();
        while (input.Length > 0)
        {
            if (input.StartsWith("../", StringComparison.Ordinal))
            {
                input = input[3..];
            }
            else if (input.StartsWith("./", StringComparison.Ordinal))
            {
                input = input[2..];
            }
            else if (input.StartsWith("/./", StringComparison.Ordinal))
            {
                input = input[2..];
            }
            else if (input == "/.")
            {
                input = "/";
            }
            else if (input.StartsWith("/../", StringComparison.Ordinal))
            {
                input = input[3..];
                PopSegment(sb);
            }
            else if (input == "/..")
            {
                input = "/";
                PopSegment(sb);
            }
            else if (input is "." or "..")
            {
                input = string.Empty;
            }
            else
            {
                int start = input[0] == '/' ? 1 : 0;
                int next = input.IndexOf('/', start);
                string seg = next < 0 ? input : input[..next];
                sb.Append(seg);
                input = next < 0 ? string.Empty : input[next..];
            }
        }

        return sb.ToString();
    }

    private static void PopSegment(StringBuilder sb)
    {
        int slash = -1;
        for (int i = sb.Length - 1; i >= 0; i--)
        {
            if (sb[i] == '/')
            {
                slash = i;
                break;
            }
        }

        sb.Length = slash < 0 ? 0 : slash;
    }

    // RFC 3986 §6.2.2.1 and §6.2.2.2: upper-case hex in escapes, decode escapes of unreserved characters. A space becomes %20.
    private static string NormalizeEscapes(string s)
    {
        if (!s.Contains('%', StringComparison.Ordinal) && !s.Contains(' ', StringComparison.Ordinal))
        {
            return s;
        }

        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '%' && i + 2 < s.Length && char.IsAsciiHexDigit(s[i + 1]) && char.IsAsciiHexDigit(s[i + 2]))
            {
                int v = int.Parse(s.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                char d = (char)v;
                if (char.IsAsciiLetterOrDigit(d) || d is '-' or '.' or '_' or '~')
                {
                    sb.Append(d);
                }
                else
                {
                    sb.Append('%').Append(char.ToUpperInvariant(s[i + 1])).Append(char.ToUpperInvariant(s[i + 2]));
                }

                i += 2;
            }
            else if (c == ' ')
            {
                sb.Append("%20");
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
