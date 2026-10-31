using System.Text;
using RecursiveAI.OneHtml;
using RecursiveAI.OneHtml.Dom;

namespace RecursiveAI.OneMail.Display;

/// <summary>
/// Rewrites untrusted HTML into display HTML. The input is parsed by OneHtml's WHATWG parser; the output is written from the resulting
/// tree, element by element, from allowlists of elements, attributes, URL schemes and CSS, so nothing in the input is copied through as text.
/// The output is then parsed and written again until it no longer changes, so what a browser makes of it is what was written.
/// What this does and does not do is set out in docs/display-html.md.
/// </summary>
public static class HtmlSanitizer
{
    /// <summary>The class of the box that holds the sanitised body (<c>omx-box</c> clips it; the host page's own styles stay outside).</summary>
    internal const string BoxClass = "omx-box";

    internal const string BoxRuleText = ".omx-box{display:block;position:relative;overflow:hidden;contain:paint;isolation:isolate}";

    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "a", "abbr", "acronym", "address", "article", "aside", "b", "bdi", "bdo", "big", "blockquote", "br", "caption", "center", "cite", "code",
        "col", "colgroup", "dd", "del", "details", "dfn", "div", "dl", "dt", "em", "figcaption", "figure", "font", "footer", "h1", "h2", "h3",
        "h4", "h5", "h6", "header", "hr", "i", "img", "ins", "kbd", "li", "main", "mark", "nav", "ol", "p", "picture", "pre", "q", "rp", "rt",
        "ruby", "s", "samp", "section", "small", "source", "span", "strike", "strong", "sub", "summary", "sup", "table", "tbody", "td", "tfoot",
        "th", "thead", "time", "tr", "tt", "u", "ul", "var", "wbr",
    };

    private static readonly HashSet<string> Void = new(StringComparer.Ordinal) { "br", "col", "hr", "img", "source", "wbr" };

    // Removed together with their content: script-capable, plugin, frame, form-control and metadata elements, and anything whose content is
    // not markup in some browser mode (noscript, noembed, noframes, xmp, plaintext, template, textarea, title).
    private static readonly HashSet<string> DropWithContent = new(StringComparer.Ordinal)
    {
        "script", "noscript", "template", "iframe", "object", "embed", "applet", "frame", "frameset", "noframes", "noembed", "title", "xmp",
        "plaintext", "textarea", "select", "datalist", "audio", "video", "track", "canvas", "dialog", "meta", "link", "base", "basefont", "param",
        "bgsound", "keygen", "map", "area", "input", "svg", "math", "listing", "isindex", "portal", "fencedframe", "slot",
    };

    private static readonly HashSet<string> BackgroundElements = new(StringComparer.Ordinal) { "table", "tr", "td", "th", "thead", "tbody", "tfoot" };

    private static readonly HashSet<string> NumericAttributes = new(StringComparer.Ordinal)
    {
        "width", "height", "border", "cellpadding", "cellspacing", "colspan", "rowspan", "hspace", "vspace", "size", "span", "start", "value",
    };

    private static readonly HashSet<string> ColorAttributes = new(StringComparer.Ordinal) { "bgcolor", "color", "bordercolor" };

    /// <summary>Sanitises an HTML document or fragment.</summary>
    /// <param name="html">The untrusted HTML. Null is treated as empty.</param>
    /// <param name="options">Policy and limits; <see cref="SanitizerOptions.Default"/> when null.</param>
    /// <returns>The sanitised markup and a report. Never throws for any input: when a limit is hit, the output is what was written up to it and <see cref="SanitizedHtml.Truncated"/> is set.</returns>
    public static SanitizedHtml Sanitize(string? html, SanitizerOptions? options = null)
    {
        options ??= SanitizerOptions.Default;
        try
        {
            return SanitizeCore(html ?? string.Empty, options);
        }
#pragma warning disable CA1031 // Hostile input must never surface as an exception; the failure is reported and the output is empty.
        catch (Exception e) when (e is not OutOfMemoryException)
#pragma warning restore CA1031
        {
            string empty = "<div class=\"" + BoxClass + "\"><div class=\"" + CssSanitizer.ScopeClass + "\"></div></div>";
            return new SanitizedHtml(empty, [new SanitizerChange(SanitizerChangeKind.LimitReached, "internal error " + e.GetType().Name)], 1, [], [], true);
        }
    }

    private static SanitizedHtml SanitizeCore(string html, SanitizerOptions options)
    {
        SanitizerContext cx = new(options, verify: false);
        if (html.Length > options.MaxInputLength)
        {
            html = html[..options.MaxInputLength];
            cx.Truncated = true;
            cx.Change(SanitizerChangeKind.LimitReached, "input length");
        }

        string output = Write(html, cx);

        // Parse the output and write it again until it no longer changes (at most three rounds). A tree that does not survive being
        // serialised and parsed is the root of the mutation attacks on sanitisers, so a result that never settles is replaced by plain text.
        bool stable = false;
        for (int round = 0; round < 3 && !stable; round++)

        {
            SanitizerContext vx = new(options, verify: true);
            string again = Write(output, vx);
            stable = string.Equals(again, output, StringComparison.Ordinal);
            output = again;
        }

        if (!stable)
        {
            cx.Change(SanitizerChangeKind.LimitReached, "unstable output replaced by text");
            output = TextOnly(html, cx);
        }

        return new SanitizedHtml(output, cx.Changes, cx.ChangeCount, cx.Remote, cx.Tracking, cx.Truncated);
    }

    private static string TextOnly(string html, SanitizerContext cx)
    {
        StringBuilder sb = new();
        sb.Append("<style>").Append(BoxRuleText).Append("</style><div class=\"").Append(BoxClass).Append("\"><div class=\"").Append(CssSanitizer.ScopeClass).Append("\">");
        try
        {
            Document doc = Document.Parse(html, ParseOptions(cx));
            int budget = cx.Options.MaxNodes;
            foreach (Node n in doc.Root.Descendants)
            {
                if (n.Kind == NodeKind.Text && budget-- > 0 && !IsRawTextParent(n))
                {
                    EscapeText(n.Data, sb);
                }
            }
        }
        catch (HtmlLimitExceededException)
        {
        }

        sb.Append("</div></div>");
        return sb.ToString();
    }

    private static bool IsRawTextParent(Node n)
    {
        Node p = n.Parent;
        return p.IsElement && p.LocalName is "script" or "style" or "noscript" or "template" or "title" or "textarea" or "xmp" or "iframe";
    }

    private static ParseOptions ParseOptions(SanitizerContext cx) => new()
    {
        // A browser with scripting on reads noscript content as text; parsing the same way keeps the two views identical.
        ScriptingEnabled = true,
        Limits = new ParseLimits
        {
            MaxInputLength = Math.Max(cx.Options.MaxInputLength, 1) * 3 + 1024,
            MaxNodes = Math.Max(cx.Options.MaxNodes, 1000) * 2,
            MaxOpenElements = 1024,
        },
    };

    private static string Write(string html, SanitizerContext cx)
    {
        Document doc;
        try
        {
            doc = Document.Parse(html, ParseOptions(cx));
        }
        catch (HtmlLimitExceededException)
        {
            cx.Truncated = true;
            cx.Change(SanitizerChangeKind.LimitReached, "parse");
            doc = Document.Parse(string.Empty);
        }

        Walker w = new(cx);
        Node root = doc.DocumentElement;
        if (!root.IsNull)
        {
            for (Node child = root.FirstChild; !child.IsNull; child = child.NextSibling)
            {
                if (!child.IsElement)
                {
                    continue;
                }

                string name = child.LocalName;
                if (name == "body")
                {
                    w.BodyAttributes(child);
                    w.Children(child, 1);
                }
                else if (name == "head")
                {
                    w.Children(child, 1);
                }
                else
                {
                    w.Element(child, 1);
                }
            }
        }

        StringBuilder sb = new();
        string css = (cx.Verify ? string.Empty : BoxRuleText) + w.Css;
        if (css.Length > 0)
        {
            // '<' cannot occur in what the CSS writer emits except inside strings and identifiers, where the escape is equivalent.
            sb.Append("<style>").Append(css.Replace("<", "\\3C ", StringComparison.Ordinal)).Append("</style>");
        }

        if (cx.Verify)
        {
            sb.Append(w.Body);
        }
        else
        {
            sb.Append("<div class=\"").Append(BoxClass).Append("\"><div class=\"").Append(CssSanitizer.ScopeClass).Append('"');
            if (w.WrapperStyle.Length > 0)
            {
                sb.Append(" style=\"");
                EscapeAttribute(w.WrapperStyle, sb);
                sb.Append('"');
            }

            sb.Append('>').Append(w.Body).Append("</div></div>");
        }

        return sb.ToString();
    }

    internal static void EscapeText(ReadOnlySpan<char> s, StringBuilder sb)
    {
        foreach (char c in s)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '\0': break;
                default: sb.Append(c); break;
            }
        }
    }

    internal static void EscapeAttribute(ReadOnlySpan<char> s, StringBuilder sb)
    {
        foreach (char c in s)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\0': break;
                default: sb.Append(c); break;
            }
        }
    }

    private sealed class Walker(SanitizerContext cx)
    {
        private readonly CssSanitizer _css = new(cx);
        private readonly StringBuilder _cssText = new();
        private int _nodes;
        private bool _depthReported;
        private int _anchorDepth;

        public readonly StringBuilder Body = new();
        public string WrapperStyle = string.Empty;

        public string Css => _cssText.ToString();

        public void BodyAttributes(Node body)
        {
            if (cx.Verify)
            {
                return;
            }

            StringBuilder style = new();
            string? bg = null, text = null, background = null, own = null;
            foreach (Attr a in body.Attributes)
            {
                string value = a.Value.ToString();
                switch (a.LocalName)
                {
                    case "bgcolor": bg = value; break;
                    case "text": text = value; break;
                    case "background": background = value; break;
                    case "style": own = value; break;
                    default:
                        cx.Change(SanitizerChangeKind.AttributeRemoved, "body@" + a.LocalName);
                        break;
                }
            }

            if (IsColor(bg))
            {
                style.Append("background-color:").Append(bg!.Trim());
            }

            if (IsColor(text))
            {
                style.Append(style.Length > 0 ? ";" : "").Append("color:").Append(text!.Trim());
            }

            if (background is not null && cx.ResolveFetchUrl(background, RemoteContentKind.Background, "body[background]") is { } url)
            {
                style.Append(style.Length > 0 ? ";" : "").Append("background-image:url(")
                    .Append(OneHtml.Css.CssSerializer.SerializeString(url)).Append(')');
            }

            if (own is not null && _css.SanitizeDeclarations(own, out _) is { Length: > 0 } decls)
            {
                style.Append(style.Length > 0 ? ";" : "").Append(decls);
            }

            WrapperStyle = style.ToString();
        }

        public void Children(Node parent, int depth)
        {
            for (Node c = parent.FirstChild; !c.IsNull; c = c.NextSibling)
            {
                if (Stopped)
                {
                    return;
                }

                if (c.Kind == NodeKind.Text)
                {
                    if (!Count())
                    {
                        return;
                    }

                    EscapeText(c.Data, Body);
                }
                else if (c.IsElement)
                {
                    Element(c, depth);
                }
            }
        }

        private bool Stopped => cx.Truncated && _nodes > cx.Options.MaxNodes;

        private bool Count()
        {
            if (++_nodes > cx.Options.MaxNodes)
            {
                if (!cx.Truncated)
                {
                    cx.Truncated = true;
                    cx.Change(SanitizerChangeKind.LimitReached, "node count");
                }

                return false;
            }

            return true;
        }

        public void Element(Node n, int depth)
        {
            string name = n.LocalName;
            if (n.Namespace != ElementNamespace.Html)
            {
                // SVG and MathML bring script vectors, namespace switches and their own URL-bearing attributes.
                cx.Change(SanitizerChangeKind.ElementRemoved, name);
                ReportMediaVectors(n, 0);
                return;
            }

            if (depth > cx.Options.MaxDepth)
            {
                if (!_depthReported)
                {
                    _depthReported = true;
                    cx.Change(SanitizerChangeKind.LimitReached, "nesting depth");
                }

                cx.Truncated = true;
                return;
            }

            if (name == "style")
            {
                CollectStyle(n);
                return;
            }

            if (DropWithContent.Contains(name))
            {
                cx.Change(SanitizerChangeKind.ElementRemoved, name);
                ReportMediaVectors(n, 0);
                return;
            }

            if (!Allowed.Contains(name) || (name == "source" && !(n.Parent.IsElement && n.Parent.LocalName == "picture")))
            {
                // Unknown, form and presentational elements keep their text; head and html are structure only.
                if (name is not ("html" or "head" or "body"))
                {
                    cx.Change(SanitizerChangeKind.ElementUnwrapped, name);
                }

                Children(n, depth + 1);
                return;
            }

            // The parser cannot produce an anchor inside an anchor from markup, but its adoption-agency repair of misnested formatting
            // can leave one in the tree. Written out, it would be parsed back into a different shape, so the inner one is unwrapped.
            if (name == "a" && _anchorDepth > 0)
            {
                cx.Change(SanitizerChangeKind.ElementUnwrapped, name);
                Children(n, depth + 1);
                return;
            }

            if (!Count())
            {
                return;
            }

            int elementStart = Body.Length;
            Body.Append('<').Append(name);
            Attributes(n, name);
            Body.Append('>');
            if (Void.Contains(name))
            {
                return;
            }

            // The parser drops one newline right after <pre>, so a newline that begins the written content is doubled to survive a re-parse.
            // The check is on what was written, not on the first child: an element removed in front of the text can leave a newline first.
            int contentStart = Body.Length;
            if (name == "a")
            {
                _anchorDepth++;
            }

            Children(n, depth + 1);
            if (name == "a")
            {
                _anchorDepth--;
            }

            if (name == "pre" && Body.Length > contentStart && Body[contentStart] == '\n')
            {
                Body.Insert(contentStart, '\n');
            }

            // An empty inline element says nothing. The parser's rules for formatting elements (the adoption agency and reconstruction) re-open
            // and close empty ones in patterns that shift a little on every parse, so a deeply nested input could never settle.
            if (Body.Length == contentStart && name is "a" or "b" or "i" or "u" or "s" or "em" or "strong" or "font" or "big" or "small" or "strike" or "tt" or "code"
                && !Body.ToString(elementStart, contentStart - elementStart).Contains(" id=", StringComparison.Ordinal))
            {
                Body.Length = elementStart;
                return;
            }

            Body.Append("</").Append(name).Append('>');
        }

        private void CollectStyle(Node n)
        {
            string? type = n.GetAttribute("type");
            string? media = n.GetAttribute("media");
            if ((type is not null && !type.Trim().Equals("text/css", StringComparison.OrdinalIgnoreCase) && type.Trim().Length > 0)
                || (media is not null && media.Trim().Length > 0 && !media.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)
                    && !media.Trim().Equals("screen", StringComparison.OrdinalIgnoreCase)))
            {
                cx.Change(SanitizerChangeKind.ElementRemoved, "style");
                return;
            }

            StringBuilder text = new();
            for (Node c = n.FirstChild; !c.IsNull; c = c.NextSibling)
            {
                if (c.Kind == NodeKind.Text)
                {
                    text.Append(c.Data);
                }
            }

            if (text.Length > 0)
            {
                _cssText.Append(_css.SanitizeStylesheet(text.ToString()));
            }
        }

        // Elements the sanitiser removes may still name something to fetch; the report says so, whatever the policy.
        private void ReportMediaVectors(Node n, int depth)
        {
            if (!n.IsElement || depth > 4)
            {
                return;
            }

            string name = n.LocalName;
            string? rel = n.GetAttribute("rel");
            RemoteContentKind linkKind = rel is not null && rel.Contains("stylesheet", StringComparison.OrdinalIgnoreCase)
                ? RemoteContentKind.Stylesheet : RemoteContentKind.Media;
            Report(n, "href", name == "link" ? linkKind : RemoteContentKind.Media, name);
            Report(n, "src", RemoteContentKind.Media, name);
            Report(n, "data", RemoteContentKind.Media, name);
            Report(n, "poster", RemoteContentKind.Image, name);
            Report(n, "background", RemoteContentKind.Background, name);
            Report(n, "xlink:href", RemoteContentKind.Media, name);
            Report(n, "srcset", RemoteContentKind.Media, name);
            if (n.GetAttribute("srcdoc") is not null)
            {
                cx.Change(SanitizerChangeKind.AttributeRemoved, name + "@srcdoc");
            }

            for (Node c = n.FirstChild; !c.IsNull; c = c.NextSibling)
            {
                if (c.IsElement)
                {
                    ReportMediaVectors(c, depth + 1);
                }
            }
        }

        private void Report(Node n, string attr, RemoteContentKind kind, string element)
        {
            string? v = n.GetAttribute(attr);
            if (v is null)
            {
                return;
            }

            if (attr == "srcset")
            {
                foreach ((string url, _) in ParseSrcset(v))
                {
                    ReportUrl(url, kind, element + "[" + attr + "]");
                }

                return;
            }

            ReportUrl(v, kind, element + "[" + attr + "]");
        }

        private void ReportUrl(string raw, RemoteContentKind kind, string source)
        {
            string? norm = SanitizerContext.NormalizeUrl(raw);
            if (norm is null)
            {
                return;
            }

            UrlForm f = SanitizerContext.Classify(norm, out _);
            if (f is UrlForm.Http or UrlForm.ProtocolRelative && !cx.Verify)
            {
                cx.AddRemote(SanitizerContext.AbsoluteRemote(norm, f), kind, RemoteContentOutcome.ElementDropped, source);
            }
        }

        // ---- attributes ----

        private void Attributes(Node n, string element)
        {
            string? width = null, height = null, srcUrl = null;
            StyleFacts facts = default;
            bool isImg = element == "img";
            foreach (Attr a in n.Attributes)
            {
                string name = a.LocalName;
                string value = a.Value.ToString();
                if (a.Namespace != AttributeNamespace.None || value.Length > 100_000_000)
                {
                    cx.Change(SanitizerChangeKind.AttributeRemoved, element + "@" + a.QualifiedName);
                    continue;
                }

                string? emit = null;
                switch (name)
                {
                    case "class":
                        emit = PrefixList(value);
                        break;
                    case "id":
                        emit = value.Length is > 0 and <= 256 && value.All(c => c > ' ') ? Prefixed(value) : null;
                        break;
                    case "style":
                        string decls = _css.SanitizeDeclarations(value, out facts);
                        emit = decls.Length > 0 ? decls : null;
                        break;
                    case "href" when element == "a":
                        emit = LinkUrl(value);
                        break;
                    case "src" when isImg:
                        srcUrl = value;
                        emit = ImageUrl(value, RemoteContentKind.Image, "img[src]");
                        break;
                    case "srcset" when isImg || element == "source":
                        emit = Srcset(value, element + "[srcset]");
                        break;
                    case "sizes" when isImg || element == "source":
                        emit = value.Length <= 512 && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or ' ' or ',' or '(' or ')' or ':' or '-' or '.' or '%') ? value : null;
                        break;
                    case "background" when BackgroundElements.Contains(element):
                        emit = ImageUrl(value, RemoteContentKind.Background, element + "[background]");
                        break;
                    case "type" when element is "ol" or "ul" or "li":
                        emit = value.Length is > 0 and <= 8 && value.All(char.IsAsciiLetterOrDigit) ? value : null;
                        break;
                    case "value" or "start" when element is "li" or "ol":
                        emit = IsNumber(value) ? value.Trim() : null;
                        break;
                    case "datetime" when element is "time" or "del" or "ins":
                        emit = value.Length <= 64 ? value : null;
                        break;
                    default:
                        if (NumericAttributes.Contains(name) && name is not ("value" or "start"))
                        {
                            emit = IsNumber(value) ? value.Trim() : null;
                            if (name == "width")
                            {
                                width = emit;
                            }
                            else if (name == "height")
                            {
                                height = emit;
                            }
                        }
                        else if (ColorAttributes.Contains(name))
                        {
                            emit = IsColor(value) ? value.Trim() : null;
                        }
                        else if (name is "align" or "valign" or "clear" or "rules" or "frame" or "scope" or "dir")
                        {
                            emit = value.Length is > 0 and <= 16 && value.All(char.IsAsciiLetter) ? value.ToLowerInvariant() : null;
                        }
                        else if (name is "nowrap" or "reversed" or "open" or "noshade")
                        {
                            emit = string.Empty;
                        }
                        else if (name is "title" or "alt" or "lang" or "abbr" or "summary" or "headers" or "face" or "char")
                        {
                            emit = value.Length <= 4096 ? value : null;
                        }
                        else if (name is "rel" or "target" or "referrerpolicy")
                        {
                            // Written afresh below, so the output never depends on what the input said.
                            continue;
                        }

                        break;
                }

                if (emit is null)
                {
                    cx.Change(SanitizerChangeKind.AttributeRemoved, element + "@" + name);
                    continue;
                }

                Body.Append(' ').Append(name);
                if (emit.Length > 0 || name is "alt" or "title" or "class")
                {
                    Body.Append("=\"");
                    EscapeAttribute(emit, Body);
                    Body.Append('"');
                }
            }

            if (element == "a")
            {
                Body.Append(" rel=\"noopener noreferrer\"");
                if (cx.Options.OpenLinksInNewWindow)
                {
                    Body.Append(" target=\"_blank\"");
                }
            }
            else if (isImg)
            {
                Body.Append(" referrerpolicy=\"no-referrer\"");
                if (srcUrl is not null)
                {
                    NoteTracking(srcUrl, width, height, facts);
                }
            }
        }

        private void NoteTracking(string rawSrc, string? width, string? height, StyleFacts facts)
        {
            if (cx.Verify)
            {
                return;
            }

            string? norm = SanitizerContext.NormalizeUrl(rawSrc);
            if (norm is null)
            {
                return;
            }

            UrlForm f = SanitizerContext.Classify(norm, out _);
            if (f is not (UrlForm.Http or UrlForm.ProtocolRelative))
            {
                return;
            }

            string url = SanitizerContext.AbsoluteRemote(norm, f);
            TrackingReasons r = TrackingReasons.None;
            if (IsTinyAttr(width) || IsTinyAttr(height) || facts.TinyWidth || facts.TinyHeight)
            {
                r |= TrackingReasons.TinySize;
            }

            if (facts.Hidden)
            {
                r |= TrackingReasons.Hidden;
            }

            if (SanitizerContext.LooksLikeTrackingUrl(url))
            {
                r |= TrackingReasons.KnownPattern;
            }

            if (r != TrackingReasons.None && cx.Tracking.Count < cx.Options.MaxReported)
            {
                cx.Tracking.Add(new TrackingIndication(url, r));
            }
        }

        private static bool IsTinyAttr(string? v) => v is not null && !v.EndsWith('%')
            && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) && d >= 0 && d <= 1;

        private string Prefixed(string v) => cx.Verify ? v : CssSanitizer.NamePrefix + v;

        private string? PrefixList(string value)
        {
            StringBuilder sb = new();
            foreach (string tok in value.Split([' ', '\t', '\n', '\r', '\f'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (tok.Length > 256)
                {
                    continue;
                }

                if (sb.Length > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(Prefixed(tok));
            }

            return sb.Length > 0 ? sb.ToString() : null;
        }

        private static bool IsNumber(string v)
        {
            v = v.Trim();
            if (v.Length is 0 or > 12)
            {
                return false;
            }

            int i = 0;
            if (v[0] is '-' or '+')
            {
                i = 1;
            }

            bool digit = false, dot = false;
            for (; i < v.Length; i++)
            {
                char c = v[i];
                if (char.IsAsciiDigit(c))
                {
                    digit = true;
                }
                else if (c == '.' && !dot)
                {
                    dot = true;
                }
                else if (c == '%' && i == v.Length - 1 && digit)
                {
                    return true;
                }
                else
                {
                    return false;
                }
            }

            return digit;
        }

        private static bool IsColor(string? v)
        {
            if (v is null)
            {
                return false;
            }

            v = v.Trim();
            return v.Length is > 0 and <= 40 && v.All(c => char.IsAsciiLetterOrDigit(c) || c is '#' or '(' or ')' or ',' or '.' or '%' or ' ' or '-');
        }

        private string? LinkUrl(string raw)
        {
            string? n = SanitizerContext.NormalizeUrl(raw);
            if (n is null || n.Length > 8_000_000)
            {
                return null;
            }

            UrlForm f = SanitizerContext.Classify(n, out _);
            switch (f)
            {
                case UrlForm.Http:
                case UrlForm.ProtocolRelative:
                    if (n.Length > 8192)
                    {
                        return null;
                    }

                    return cx.Verify ? SanitizerContext.AbsoluteRemote(n, f) : cx.ApplyPolicy(SanitizerContext.AbsoluteRemote(n, f), RemoteContentKind.Link, "a[href]");
                case UrlForm.Mailto:
                case UrlForm.Tel:
                    return n;
                case UrlForm.Fragment:
                    return n.Length > 1 && n.Length < 512 ? "#" + Prefixed(n[1..]) : null;
                default:
                    // Verification re-reads what the policy produced (a caller's relative or application URL).
                    return cx.Verify ? cx.AcceptCallerUrl(n) : null;
            }
        }

        private string? ImageUrl(string raw, RemoteContentKind kind, string source)
        {
            if (cx.Verify)
            {
                string? n = SanitizerContext.NormalizeUrl(raw);
                return n is null ? null : cx.AcceptCallerUrl(n);
            }

            return cx.ResolveFetchUrl(raw, kind, source);
        }

        // HTML 4.8.4.3.1: candidates are separated by commas; a URL is a run of non-space characters (trailing commas belong to the separator).
        internal static List<(string Url, string Descriptor)> ParseSrcset(string s)
        {
            List<(string, string)> list = [];
            int i = 0;
            while (i < s.Length && list.Count < 64)
            {
                while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == ','))
                {
                    i++;
                }

                int start = i;
                while (i < s.Length && !char.IsWhiteSpace(s[i]))
                {
                    i++;
                }

                if (i == start)
                {
                    break;
                }

                string url = s[start..i];
                string desc = string.Empty;
                if (url.EndsWith(',') )
                {
                    url = url.TrimEnd(',');
                }
                else
                {
                    int ds = i;
                    int paren = 0;
                    while (i < s.Length && !(s[i] == ',' && paren == 0))
                    {
                        if (s[i] == '(')
                        {
                            paren++;
                        }
                        else if (s[i] == ')' && paren > 0)
                        {
                            paren--;
                        }

                        i++;
                    }

                    desc = s[ds..i].Trim();
                }

                if (url.Length > 0)
                {
                    list.Add((url, desc));
                }
            }

            return list;
        }

        private string? Srcset(string value, string source)
        {
            StringBuilder sb = new();
            foreach ((string url, string desc) in ParseSrcset(value))
            {
                string? resolved = ImageUrl(url, RemoteContentKind.Image, source);
                if (resolved is null)
                {
                    continue;
                }

                if (desc.Length > 0 && !desc.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or ' ' or '-'))
                {
                    continue;
                }

                // A URL that contains whitespace or a comma at its end would change the candidate list; it is not written.
                if (resolved.Any(char.IsWhiteSpace) || resolved.EndsWith(',') || resolved.Length == 0)
                {
                    continue;
                }

                if (sb.Length > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(resolved);
                if (desc.Length > 0)
                {
                    sb.Append(' ').Append(desc);
                }
            }

            return sb.Length > 0 ? sb.ToString() : null;
        }
    }
}
