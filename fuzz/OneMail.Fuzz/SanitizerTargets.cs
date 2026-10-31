using System.Text;
using RecursiveAI.OneHtml;
using RecursiveAI.OneHtml.Dom;
using RecursiveAI.OneMail.Display;

namespace RecursiveAI.OneMail.Fuzz;

// The display-HTML sanitiser on hostile markup. Byte 0 picks the options (policy allow or block, a rewrite policy, tight limits); the rest is HTML.
//   sanitizer:           nothing may throw or hang, and the output may hold only what the allowlists permit: no script-bearing element, no
//                        event-handler attribute, no URL scheme beyond http(s), mailto, tel, data images and fragments.
//   sanitizer-reparse:   the output is parsed and written back with OneHtml's own spec serialiser, twice; the two strings must be equal, which is
//                        the property a browser relies on when it copies the markup through innerHTML (mutation XSS needs it to fail).
internal static class SanitizerTargets
{
    private static readonly HashSet<string> Banned = new(StringComparer.Ordinal)
    {
        "script", "iframe", "object", "embed", "form", "input", "svg", "math", "link", "meta", "base", "noscript", "template", "frame",
        "frameset", "video", "audio", "textarea", "select", "button", "applet", "plaintext", "xmp",
    };

    private static SanitizerOptions Options(byte b) => (b & 7) switch
    {
        0 => new SanitizerOptions { RemoteContent = RemoteContentAction.Allow },
        1 => new SanitizerOptions { Policy = r => RemoteContentDecision.Rewrite("/p?u=" + Uri.EscapeDataString(r.Url)) },
        2 => new SanitizerOptions { MaxDepth = 8, MaxNodes = 64, MaxCssLength = 256 },
        3 => new SanitizerOptions { OpenLinksInNewWindow = false, AllowRewriteToAnyUrl = false, Policy = r => RemoteContentDecision.Rewrite("/p") },
        _ => SanitizerOptions.Default,
    };

    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        string html = Encoding.UTF8.GetString(data[1..]);
        SanitizedHtml r = HtmlSanitizer.Sanitize(html, Options(data[0]));
        // The public entry turns an unexpected exception into an empty result; the fuzzer wants to see it.
        if (r.Changes.Any(c => c.Detail.StartsWith("internal error", StringComparison.Ordinal) || c.Detail.StartsWith("unstable", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(r.Changes.First(c => c.Detail.StartsWith("internal", StringComparison.Ordinal) || c.Detail.StartsWith("unstable", StringComparison.Ordinal)).Detail);
        }

        Check(r.Html);
    }

    public static void Reparse(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        SanitizedHtml r = HtmlSanitizer.Sanitize(Encoding.UTF8.GetString(data[1..]), Options(data[0]));
        Document d1 = Document.Parse(r.Html);
        string s1 = d1.SerializeChildren(d1.Root);
        Document d2 = Document.Parse(s1);
        string s2 = d2.SerializeChildren(d2.Root);
        if (!string.Equals(s1, s2, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("sanitised output changed when written and parsed again");
        }
    }

    private static void Check(string output)
    {
        Document doc = Document.Parse(output);
        foreach (Node n in doc.Root.Descendants)
        {
            if (n.Kind == NodeKind.Comment)
            {
                throw new InvalidOperationException("comment in output");
            }

            if (!n.IsElement)
            {
                continue;
            }

            if (n.Namespace != ElementNamespace.Html || Banned.Contains(n.LocalName))
            {
                throw new InvalidOperationException("element " + n.LocalName);
            }

            foreach (Attr a in n.Attributes)
            {
                string name = a.LocalName;
                string v = new string(a.Value.ToString().Where(c => c > ' ').ToArray()).ToLowerInvariant();
                if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase) || name is "srcdoc" or "formaction" or "action" or "ping")
                {
                    throw new InvalidOperationException("attribute " + name);
                }

                if (v.Contains("javascript:", StringComparison.Ordinal) || v.Contains("vbscript:", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("script URL in " + name);
                }

                if (name is "href" or "src" && !(v.StartsWith("http", StringComparison.Ordinal) || v.StartsWith("mailto:", StringComparison.Ordinal)
                    || v.StartsWith("tel:", StringComparison.Ordinal) || v.StartsWith('#') || v.StartsWith("data:image/", StringComparison.Ordinal)
                    || v.StartsWith('/') || v.Length == 0))
                {
                    throw new InvalidOperationException("URL scheme in " + name);
                }
            }

            if (n.LocalName == "style")
            {
                string css = (n.TextContent ?? string.Empty).ToLowerInvariant();
                if (css.Contains("@import", StringComparison.Ordinal) || css.Contains("expression", StringComparison.Ordinal)
                    || css.Contains("javascript", StringComparison.Ordinal) || css.Contains('<'))
                {
                    throw new InvalidOperationException("style content");
                }
            }
        }
    }
}
