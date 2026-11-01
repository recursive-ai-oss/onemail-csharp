using System;
using System.Collections.Generic;
using System.Text;
using RecursiveAI.OneHtml;
using RecursiveAI.OneHtml.Css;

namespace RecursiveAI.OneMail.Bodies;

/// <summary>How links are written by <see cref="HtmlToText"/>.</summary>
public enum LinkStyle
{
    /// <summary>Only the link text.</summary>
    TextOnly,

    /// <summary>The link text, then the address in angle brackets when the address is not what the text already says (default). A link with no visible text is dropped.</summary>
    WhenDiffers,

    /// <summary>The link text with a number, <c>text [1]</c>, and the numbered addresses after the text.</summary>
    Footnotes,
}

/// <summary>How images are written by <see cref="HtmlToText"/>.</summary>
public enum ImageStyle
{
    /// <summary>Nothing.</summary>
    None,

    /// <summary>The alternative text in square brackets, <c>[logo]</c> (default). Images without alt text, and one-pixel images, are dropped.</summary>
    Bracketed,

    /// <summary>The alternative text as it is.</summary>
    AltText,
}

/// <summary>Options for <see cref="HtmlToText"/>.</summary>
public sealed record HtmlToTextOptions
{
    /// <summary>The options used when none are given.</summary>
    public static HtmlToTextOptions Default { get; } = new();

    /// <summary>The longest line to write, in characters, including quote and list prefixes. 0 (default) does not wrap. Pre-formatted lines are never wrapped, and a word longer than the line is not split.</summary>
    public int LineWidth { get; init; }

    /// <summary>How links are written. Default <see cref="LinkStyle.WhenDiffers"/>.</summary>
    public LinkStyle Links { get; init; } = LinkStyle.WhenDiffers;

    /// <summary>How images are written. Default <see cref="ImageStyle.Bracketed"/>.</summary>
    public ImageStyle Images { get; init; } = ImageStyle.Bracketed;

    /// <summary>
    /// True (default) to apply the document's own style sheets (<c>style</c> elements, with media queries as for a screen) when deciding what is hidden
    /// and which text is pre-formatted. The <c>style</c> attribute and the <c>hidden</c> attribute always apply.
    /// </summary>
    public bool ApplyStyleSheets { get; init; } = true;

    /// <summary>What goes between the cells of a table row. Default <c>" | "</c>.</summary>
    public string TableCellSeparator { get; init; } = " | ";

    /// <summary>The input is cut after this many characters (<see cref="BodyIssueKind.InputTruncated"/>). Default 16,777,216.</summary>
    public int MaxInputChars { get; init; } = 16 * 1024 * 1024;

    /// <summary>The output stops growing at this many characters (<see cref="BodyIssueKind.OutputTruncated"/>). Default 16,777,216.</summary>
    public int MaxOutputChars { get; init; } = 16 * 1024 * 1024;

    /// <summary>Quote and list levels beyond this add no more prefix (<see cref="BodyIssueKind.NestingTooDeep"/>). Default 32.</summary>
    public int MaxNesting { get; init; } = 32;

    /// <summary>Table cells beyond this are skipped (<see cref="BodyIssueKind.TableTooLarge"/>). Default 100,000.</summary>
    public int MaxTableCells { get; init; } = 100_000;

    /// <summary>The longest run of spaces kept (<see cref="BodyIssueKind.WhitespaceRunBounded"/>). Default 256.</summary>
    public int MaxWhitespaceRun { get; init; } = 256;
}

/// <summary>
/// HTML to readable plain text, on OneHtml's DOM: block structure becomes line breaks and blank lines, lists get markers and indentation, table
/// rows become lines with separated cells, <c>blockquote</c> and <c>type="cite"</c> become <c>"&gt; "</c> prefixes, links and images are written by
/// the options, <c>pre</c> keeps its lines, and whitespace collapses as CSS <c>white-space</c> says. The same input and options always give the same text.
/// </summary>
/// <remarks>
/// Skipped: <c>head</c>, <c>script</c>, <c>style</c>, <c>template</c>, forms controls, embedded objects, SVG and MathML, and anything hidden by
/// <c>display:none</c>, <c>visibility:hidden</c>, <c>mso-hide:all</c> or the <c>hidden</c> attribute. Invisible characters (soft hyphen, zero-width
/// characters, byte order mark) are removed and a non-breaking space is a space. The rules and their reasons are in <c>docs/bodies.md</c>.
/// </remarks>
public static class HtmlToText
{
    /// <summary>Converts an HTML document or fragment to text.</summary>
    /// <param name="html">The HTML, already decoded to text.</param>
    /// <param name="options">Options; null for <see cref="HtmlToTextOptions.Default"/>.</param>
    /// <returns>The text, without a final line ending, and what the conversion reported. Never throws on content.</returns>
    public static BodyConversion Convert(string html, HtmlToTextOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(html);
        options ??= HtmlToTextOptions.Default;
        List<BodyIssue> issues = [];
        if (html.Length > options.MaxInputChars)
        {
            html = html[..options.MaxInputChars];
            issues.Add(new BodyIssue(BodyIssueKind.InputTruncated, options.MaxInputChars));
        }

        // OneHtml refuses documents past its parse limits (for example 4,096 open elements). The text is still worth having, so
        // retry on a shorter prefix until the parser accepts it; the issue says the document was cut.
        Document? doc = null;
        for (int attempt = 0; attempt < 24 && doc is null; attempt++)
        {
            try
            {
                doc = Document.Parse(html);
            }
            catch (HtmlLimitExceededException)
            {
                if (attempt == 0)
                {
                    issues.Add(new BodyIssue(BodyIssueKind.HtmlLimitExceeded));
                }

                html = html[..(html.Length / 2)];
            }
        }

        if (doc is null)
        {
            return new BodyConversion(string.Empty, issues);
        }

        return new BodyConversion(new TextWriterState(options, issues).Run(doc), issues);
    }
}

internal enum WsMode
{
    Normal,
    Pre,
    PreLine,
}

// The walk and the line builder. A line is built in _cur, flushed (wrapped, prefixed) into _out when something ends it.
internal sealed class TextWriterState
{
    private readonly HtmlToTextOptions _o;
    private readonly List<BodyIssue> _issues;
    private readonly StringBuilder _out = new();
    private readonly StringBuilder _cur = new();
    private readonly List<Seg> _segs = [];
    private readonly List<(bool Ordered, int Next)> _lists = [];
    private readonly Stack<Frame> _frames = new();
    private readonly List<string> _footnotes = [];
    private DocumentStyles? _styles;
    private WsMode _ws = WsMode.Normal;
    private bool _hasLines;
    private int _blankReq;
    private int _blankDepth;
    private bool _pendingSpace;
    private bool _cellSep;
    private int _spaceRun;
    private int _blankRun; // blank lines at the end of the output so far
    private string _lineFirst = string.Empty;
    private string _lineRest = string.Empty;
    private bool _lineNoWrap;
    private int _visible;
    private int _tableDepth;
    private int _cells;
    private bool _stop;
    private bool _nestingReported;
    private bool _tableReported;
    private bool _spaceReported;

    private struct Seg
    {
        public string First;
        public string Rest;
        public bool Used;
        public bool IsItem;
    }

    private struct Frame
    {
        public int Segs;
        public int Lists;
        public WsMode PrevWs;
        public int AfterBreak;
        public bool IsTable;
        public string? Href;
        public string? AnchorText;
        public int VisibleAtStart;
    }

    public TextWriterState(HtmlToTextOptions o, List<BodyIssue> issues)
    {
        _o = o;
        _issues = issues;
    }

    public string Run(Document doc)
    {
        if (_o.ApplyStyleSheets && doc.Root.GetElementsByTagName("style").Count > 0)
        {
            // Without the built-in user-agent sheet: this code knows the elements' default display and white-space itself, and the sheet costs
            // most of the allocation for a small message. Author rules (style elements, media queries) and style attributes still apply.
            _styles = DocumentStyles.Create(doc, options: new StyleSourceOptions { IncludeUserAgentSheet = false });
        }

        Node root = doc.Root;
        Node n = root.FirstChild;
        while (!n.IsNull && !_stop)
        {
            bool descended = false;
            if (n.Kind == NodeKind.Text)
            {
                Text(n.Data);
            }
            else if (n.IsElement && Enter(n))
            {
                if (n.HasChildNodes)
                {
                    n = n.FirstChild;
                    descended = true;
                }
                else
                {
                    Exit();
                }
            }

            if (descended)
            {
                continue;
            }

            while (true)
            {
                if (!n.NextSibling.IsNull)
                {
                    n = n.NextSibling;
                    break;
                }

                n = n.Parent;
                if (n.IsNull || n.Kind == NodeKind.Document || n.Kind == NodeKind.DocumentFragment)
                {
                    n = default;
                    break;
                }

                Exit();
            }
        }

        while (_frames.Count > 0)
        {
            Exit();
        }

        FlushLine();
        if (_footnotes.Count > 0 && !_stop)
        {
            _out.Append('\n');
            for (int i = 0; i < _footnotes.Count; i++)
            {
                _out.Append('[').Append(i + 1).Append("] ").Append(_footnotes[i]).Append('\n');
            }
        }

        return Finish();
    }

    private string Finish()
    {
        int start = 0;
        while (start < _out.Length && _out[start] == '\n')
        {
            start++;
        }

        int end = _out.Length;
        while (end > start && (_out[end - 1] is '\n' or ' ' or '\t'))
        {
            end--;
        }

        return _out.ToString(start, end - start);
    }

    // ---- elements ----

    private static bool IsSkipped(string name) => name switch
    {
        "head" or "script" or "style" or "template" or "title" or "noembed" or "noframes" or "iframe" or "object" or "embed" or "applet"
            or "canvas" or "audio" or "video" or "select" or "datalist" or "input" or "meta" or "link" or "base" or "frameset" or "frame"
            or "param" or "source" or "track" or "area" or "map" or "rp" or "textarea" => true,
        _ => false,
    };

    // 2 = blank line around the element, 1 = line break around it, 0 = inline.
    private static int BlockKind(string name) => name switch
    {
        "p" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "blockquote" or "pre" or "hr" or "figure" or "fieldset" or "address"
            or "listing" or "xmp" or "plaintext" => 2,
        "div" or "section" or "article" or "aside" or "header" or "footer" or "nav" or "main" or "form" or "li" or "dt" or "dd" or "tr"
            or "caption" or "figcaption" or "center" or "hgroup" or "summary" or "details" or "dialog" or "body" or "html" or "legend"
            or "thead" or "tbody" or "tfoot" or "table" or "ul" or "ol" or "dl" or "menu" or "dir" or "noscript" or "br" => 1,
        _ => 0,
    };

    private bool Enter(Node n)
    {
        if (n.Namespace != RecursiveAI.OneHtml.ElementNamespace.Html)
        {
            return false;
        }

        string name = n.LocalName;
        if (IsSkipped(name) || IsHidden(n, out WsMode? styleWs))
        {
            return false;
        }

        switch (name)
        {
            case "br":
                HardBreak();
                return false;
            case "hr":
                Boundary(2);
                StartLineIfEmpty();
                _lineNoWrap = true;
                _cur.Append('-', _o.LineWidth > 0 ? Math.Max(Math.Min(_o.LineWidth - PrefixWidth(), 40), 3) : 40);
                FlushLine();
                Boundary(2);
                return false;
            case "img":
                Image(n);
                return false;
        }

        Frame f = new() { PrevWs = _ws, AfterBreak = 0 };
        int kind = BlockKind(name);
        bool outerTable = false;
        if (name == "table")
        {
            outerTable = _tableDepth == 0;
            kind = outerTable ? 2 : 1;
            _tableDepth++;
            f.IsTable = true;
        }

        if (name is "ul" or "ol" or "menu" or "dir")
        {
            kind = _lists.Count == 0 ? 2 : 1;
            // A list directly inside an item that has said nothing yet: the item's marker would otherwise sit on the nested item's line.
            for (int i = _segs.Count - 1; i >= 0 && _segs[i].IsItem && !_segs[i].Used; i--)
            {
                Seg s = _segs[i];
                s.Used = true;
                _segs[i] = s;
            }

            int start = 1;
            bool ordered = name == "ol";
            if (ordered && n.GetAttribute("start") is { } sv && int.TryParse(sv, out int parsed))
            {
                start = parsed;
            }

            _lists.Add((ordered, start));
            f.Lists = 1;
        }

        if (name == "dl" && _segs.Count == 0)
        {
            kind = 2;
        }

        if (name is "td" or "th")
        {
            if (++_cells > _o.MaxTableCells)
            {
                if (!_tableReported)
                {
                    _issues.Add(new BodyIssue(BodyIssueKind.TableTooLarge));
                    _tableReported = true;
                }

                if (f.IsTable)
                {
                    _tableDepth--;
                }

                return false;
            }

            if (_cur.Length > 0)
            {
                _cellSep = true;
            }
        }

        if (kind > 0)
        {
            Boundary(kind);
            f.AfterBreak = kind;
        }

        if (name == "li")
        {
            string marker;
            if (_lists.Count > 0 && _lists[^1].Ordered)
            {
                int num = _lists[^1].Next;
                if (n.GetAttribute("value") is { } vv && int.TryParse(vv, out int v))
                {
                    num = v;
                }

                _lists[^1] = (true, num + 1);
                marker = num.ToString(System.Globalization.CultureInfo.InvariantCulture) + ". ";
            }
            else
            {
                marker = "* ";
            }

            f.Segs += PushSeg(marker, new string(' ', marker.Length), item: true) ? 1 : 0;
        }
        else if (name == "dd")
        {
            f.Segs += PushSeg("    ", "    ", item: false) ? 1 : 0;
        }
        else if (name == "blockquote" || (name != "li" && string.Equals(n.GetAttribute("type"), "cite", StringComparison.OrdinalIgnoreCase)))
        {
            f.Segs += PushSeg("> ", "> ", item: false) ? 1 : 0;
        }

        if (name is "pre" or "listing" or "xmp" or "plaintext")
        {
            _ws = WsMode.Pre;
        }

        if (styleWs is { } sw)
        {
            _ws = sw;
        }

        if (name == "a" && n.GetAttribute("href") is { } href && UsableHref(href))
        {
            f.Href = href.Trim();
            f.AnchorText = _o.Links == LinkStyle.TextOnly ? null : Collapse(n.TextContent ?? string.Empty);
            f.VisibleAtStart = _visible;
        }

        _frames.Push(f);
        return true;
    }

    private void Exit()
    {
        Frame f = _frames.Pop();
        if (f.Href is not null)
        {
            Link(f);
        }

        for (int i = 0; i < f.Segs && _segs.Count > 0; i++)
        {
            _segs.RemoveAt(_segs.Count - 1);
        }

        if (f.Lists > 0 && _lists.Count > 0)
        {
            _lists.RemoveAt(_lists.Count - 1);
        }

        if (f.IsTable)
        {
            _tableDepth--;
        }

        _ws = f.PrevWs;
        if (f.AfterBreak > 0)
        {
            Boundary(f.AfterBreak);
        }
    }

    private bool PushSeg(string first, string rest, bool item)
    {
        if (_segs.Count >= _o.MaxNesting)
        {
            if (!_nestingReported)
            {
                _issues.Add(new BodyIssue(BodyIssueKind.NestingTooDeep));
                _nestingReported = true;
            }

            return false;
        }

        _segs.Add(new Seg { First = first, Rest = rest, IsItem = item });
        return true;
    }

    private static bool UsableHref(string href)
    {
        ReadOnlySpan<char> h = href.AsSpan().Trim();
        return h.Length > 0 && h[0] != '#' && !h.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
            && !h.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && !h.StartsWith("cid:", StringComparison.OrdinalIgnoreCase)
            && !h.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase);
    }

    private void Link(Frame f)
    {
        if (f.AnchorText is null)
        {
            return;
        }

        string url = Collapse(f.Href!);
        if (_visible == f.VisibleAtStart)
        {
            // A link with no text of its own (a picture without alt text, an empty anchor) is written as its address, so the destination is not lost.
            Token(_o.Links == LinkStyle.Footnotes ? "[" + FootnoteNumber(url) + "]" : "<" + url + ">", spaceBefore: true);
            return;
        }

        if (_o.Links == LinkStyle.Footnotes)
        {
            Token(" [" + FootnoteNumber(url) + "]", spaceBefore: false);
            return;
        }

        if (!SameAddress(f.AnchorText, url))
        {
            Token("<" + url + ">", spaceBefore: true);
        }
    }

    private string FootnoteNumber(string url)
    {
        int idx = _footnotes.IndexOf(url);
        if (idx < 0)
        {
            _footnotes.Add(url);
            idx = _footnotes.Count - 1;
        }

        return (idx + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    // The text already says the address: compare without the scheme, "www.", a mailto: prefix, case and a final slash.
    private static bool SameAddress(string text, string url)
    {
        return Normalize(text) == Normalize(url);

        static string Normalize(string s)
        {
            s = s.Trim();
            foreach (string p in (ReadOnlySpan<string>)["mailto:", "tel:", "https://", "http://"])
            {
                if (s.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                {
                    s = s[p.Length..];
                    break;
                }
            }

            if (s.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            {
                s = s[4..];
            }

            return s.TrimEnd('/').ToLowerInvariant();
        }
    }

    private void Image(Node n)
    {
        if (_o.Images == ImageStyle.None)
        {
            return;
        }

        if (IsTiny(n.GetAttribute("width")) && IsTiny(n.GetAttribute("height")))
        {
            return;
        }

        string alt = Collapse(n.GetAttribute("alt") ?? string.Empty);
        if (alt.Length == 0)
        {
            return;
        }

        Token(_o.Images == ImageStyle.Bracketed ? "[" + alt + "]" : alt, spaceBefore: true);
    }

    private static bool IsTiny(string? dim) => dim is not null && int.TryParse(dim.Trim(), out int v) && v is >= 0 and <= 2;

    // ---- hidden content and white-space ----

    private bool IsHidden(Node n, out WsMode? ws)
    {
        ws = null;
        if (n.HasAttribute("hidden"))
        {
            return true;
        }

        if (n.GetAttribute("style") is { Length: > 0 } style)
        {
            string? d = Decl(style, "display");
            if (d == "none")
            {
                return true;
            }

            string? v = Decl(style, "visibility");
            if (v is "hidden" or "collapse")
            {
                return true;
            }

            if (Decl(style, "mso-hide") == "all")
            {
                return true;
            }

            ws = MapWs(Decl(style, "white-space"));
        }

        if (_styles is not null)
        {
            ComputedStyle cs = _styles.GetStyle(n);
            if (cs.GetPropertyValue("display") == "none" || cs.GetPropertyValue("visibility") is "hidden" or "collapse")
            {
                return true;
            }

            ws ??= MapWs(cs.GetPropertyValue("white-space"));
            if (ws == WsMode.Normal && _ws != WsMode.Normal && n.LocalName is not ("pre" or "textarea"))
            {
                // The cascade reports the computed value; an explicit normal under a pre-formatted parent is real, so keep it.
                ws = WsMode.Normal;
            }
        }

        return false;
    }

    private static WsMode? MapWs(string? v) => v switch
    {
        "pre" or "pre-wrap" or "break-spaces" => WsMode.Pre,
        "pre-line" => WsMode.PreLine,
        "normal" or "nowrap" => WsMode.Normal,
        _ => null,
    };

    // The last declaration of `name` in a style attribute, lower case, without !important; null when absent. A scan, not a CSS parser.
    private static string? Decl(string style, string name)
    {
        string? found = null;
        int pos = 0;
        while (pos < style.Length)
        {
            int semi = style.IndexOf(';', pos);
            int end = semi < 0 ? style.Length : semi;
            ReadOnlySpan<char> d = style.AsSpan(pos, end - pos);
            pos = end + 1;
            int colon = d.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            if (d[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                ReadOnlySpan<char> v = d[(colon + 1)..].Trim();
                int bang = v.IndexOf('!');
                if (bang >= 0)
                {
                    v = v[..bang].Trim();
                }

                found = v.ToString().ToLowerInvariant();
            }
        }

        return found;
    }

    // ---- text ----

    private static bool IsCssSpace(char c) => c is ' ' or '\t' or '\n' or '\r' or '\f';

    private static bool IsInvisible(char c) =>
        c is '\u00AD' or '\u200B' or '\u200C' or '\u200D' or '\u200E' or '\u200F' or '\u2060' or '\uFEFF' or '\u034F' or '\u180E';

    private void Text(ReadOnlySpan<char> s)
    {
        foreach (char c in s)
        {
            if (_stop)
            {
                return;
            }

            if (_ws == WsMode.Pre)
            {
                if (c == '\n')
                {
                    PreNewline();
                }
                else if (c != '\r' && !IsInvisible(c))
                {
                    if (c is ' ' or '\u00A0')
                    {
                        EmitSpace(literal: true);
                    }
                    else
                    {
                        Emit(c);
                    }
                }

                continue;
            }

            if (c == '\n' && _ws == WsMode.PreLine)
            {
                PreNewline();
                continue;
            }

            if (IsCssSpace(c))
            {
                if (_cur.Length > 0)
                {
                    _pendingSpace = true;
                }

                continue;
            }

            if (c == '\u00A0')
            {
                if (_cur.Length > 0)
                {
                    EmitSpace(literal: true);
                }

                continue;
            }

            if (IsInvisible(c))
            {
                continue;
            }

            Emit(c);
        }
    }

    // Link and image text: a whole token, with the space before it that inline content would have.
    private void Token(string s, bool spaceBefore)
    {
        if (_stop || s.Length == 0)
        {
            return;
        }

        if (spaceBefore && _cur.Length > 0)
        {
            _pendingSpace = true;
        }

        foreach (char c in s)
        {
            Emit(IsCssSpace(c) ? ' ' : c);
        }
    }

    private void EmitSpace(bool literal)
    {
        _ = literal;
        if (_cur.Length == 0)
        {
            StartLine();
        }

        if (++_spaceRun > _o.MaxWhitespaceRun)
        {
            if (!_spaceReported)
            {
                _issues.Add(new BodyIssue(BodyIssueKind.WhitespaceRunBounded));
                _spaceReported = true;
            }

            return;
        }

        _cur.Append(' ');
    }

    private void Emit(char c)
    {
        if (_cur.Length == 0)
        {
            StartLine();
        }
        else if (_cellSep)
        {
            _cur.Append(_o.TableCellSeparator);
        }
        else if (_pendingSpace)
        {
            _cur.Append(' ');
        }

        _cellSep = false;
        _pendingSpace = false;
        _spaceRun = 0;
        _cur.Append(c);
        if (c != ' ')
        {
            _visible++;
        }

        if (_cur.Length > 1 << 24)
        {
            // A single line this long is not text anyone reads; end it so a hostile document cannot grow one buffer without bound.
            _lineNoWrap = true;
            FlushLine();
        }
    }

    // ---- lines ----

    private void Boundary(int kind)
    {
        FlushLine();
        if (kind >= 2)
        {
            // The blank line sits at the shallowest quote level the boundary touched: a quote opening or closing does not push it inwards.
            _blankDepth = _blankReq == 0 ? _segs.Count : Math.Min(_blankDepth, _segs.Count);
            _blankReq = 1;
        }
    }

    private void StartLineIfEmpty()
    {
        if (_cur.Length == 0)
        {
            StartLine();
        }
    }

    // First content of a line: pay a requested blank line, take this line's prefixes (and use up item markers).
    private void StartLine()
    {
        if (_hasLines && _blankReq > 0 && _blankRun == 0)
        {
            AppendBlankLine();
        }

        _blankReq = 0;
        StringBuilder first = new();
        StringBuilder rest = new();
        for (int i = 0; i < _segs.Count; i++)
        {
            Seg s = _segs[i];
            first.Append(s.Used ? s.Rest : s.First);
            rest.Append(s.Rest);
            s.Used = true;
            _segs[i] = s;
        }

        _lineFirst = first.ToString();
        _lineRest = rest.ToString();
        _lineNoWrap = _ws != WsMode.Normal;
    }

    private int PrefixWidth()
    {
        int w = 0;
        foreach (Seg s in _segs)
        {
            w += s.Rest.Length;
        }

        return w;
    }

    private void AppendBlankLine()
    {
        StringBuilder rest = new();
        for (int i = 0; i < _segs.Count && i < _blankDepth; i++)
        {
            rest.Append(_segs[i].Rest);
        }

        AppendOut(rest.ToString().TrimEnd(), string.Empty);
    }

    private void HardBreak()
    {
        if (_cur.Length > 0)
        {
            FlushLine();
            return;
        }

        if (_hasLines && _blankReq > 0 && _blankRun == 0)
        {
            AppendBlankLine();
        }

        _blankReq = 0;

        // Runs of <br> are a few blank lines at most; more than two in a row are layout padding.
        if (_ws == WsMode.Normal && _blankRun >= 2)
        {
            return;
        }

        AppendBlankLine();
    }

    private void PreNewline()
    {
        if (_cur.Length > 0)
        {
            FlushLine();
            return;
        }

        if (_hasLines && _blankReq > 0)
        {
            AppendBlankLine();
            _blankReq = 0;
        }

        AppendBlankLine();
    }

    private void FlushLine()
    {
        if (_cur.Length == 0)
        {
            _cellSep = false;
            _pendingSpace = false;
            return;
        }

        string content = _cur.ToString();
        _cur.Clear();
        _cellSep = false;
        _pendingSpace = false;
        _spaceRun = 0;
        if (_lineNoWrap || _o.LineWidth <= 0 || _lineFirst.Length + content.Length <= _o.LineWidth)
        {
            AppendOut(_lineFirst, content);
        }
        else
        {
            Wrap(content);
        }
    }

    private void Wrap(string content)
    {
        int width = _o.LineWidth;
        int pos = 0;
        string prefix = _lineFirst;
        while (pos < content.Length)
        {
            int avail = Math.Max(width - prefix.Length, 8);
            if (content.Length - pos <= avail)
            {
                AppendOut(prefix, content[pos..]);
                return;
            }

            int k = content.LastIndexOf(' ', pos + avail, avail + 1);
            if (k <= pos)
            {
                k = content.IndexOf(' ', pos + avail);
                if (k < 0)
                {
                    AppendOut(prefix, content[pos..]);
                    return;
                }
            }

            AppendOut(prefix, content.Substring(pos, k - pos));
            pos = k + 1;
            while (pos < content.Length && content[pos] == ' ')
            {
                pos++;
            }

            prefix = _lineRest;
        }
    }

    private void AppendOut(string prefix, string content)
    {
        if (_out.Length > _o.MaxOutputChars)
        {
            if (!_stop)
            {
                _issues.Add(new BodyIssue(BodyIssueKind.OutputTruncated));
            }

            _stop = true;
            return;
        }

        string line = (prefix + content).TrimEnd(' ');
        _out.Append(line).Append('\n');
        _hasLines = true;
        _blankRun = content.Length == 0 ? _blankRun + 1 : 0;
    }

    private static string Collapse(string s)
    {
        StringBuilder sb = new(s.Length);
        bool space = false;
        foreach (char c in s)
        {
            if (IsCssSpace(c) || c == '\u00A0')
            {
                space = sb.Length > 0;
            }
            else if (!IsInvisible(c))
            {
                if (space)
                {
                    sb.Append(' ');
                    space = false;
                }

                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
