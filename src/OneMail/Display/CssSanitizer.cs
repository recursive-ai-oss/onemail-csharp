using System.Text;
using RecursiveAI.OneHtml.Css;

namespace RecursiveAI.OneMail.Display;

/// <summary>What a sanitised <c>style</c> attribute says about the size and visibility of its element, for tracking-pixel detection.</summary>
internal struct StyleFacts
{
    public bool TinyWidth;
    public bool TinyHeight;
    public bool Hidden;
}

/// <summary>
/// The CSS half of the sanitiser: declarations and style sheets are parsed with OneHtml's CSS parser and written again from an allowlist of
/// properties, value tokens, functions and selectors. Nothing is copied through as text; every token is re-serialised.
/// </summary>
internal sealed class CssSanitizer(SanitizerContext cx)
{
    /// <summary>The class that marks the element holding the message body; every selector is confined below it.</summary>
    public const string ScopeClass = "omx";

    /// <summary>The prefix given to authors' class names and ids so neither the host page's styles nor its scripts reach them by accident.</summary>
    public const string NamePrefix = "om-";

    private const int MaxValueDepth = 8;

    // Properties that style the content of the box and nothing else. Anything that positions content outside it (position, z-index, transform,
    // float is kept because the wrapper clips), animates, filters, or reads the page (content, cursor, counter-*) is absent.
    private static readonly HashSet<string> Properties = new(StringComparer.Ordinal)
    {
        "color", "opacity", "visibility", "display", "direction", "unicode-bidi", "float", "clear", "box-sizing", "overflow", "overflow-x", "overflow-y",
        "width", "height", "min-width", "min-height", "max-width", "max-height", "vertical-align", "table-layout", "border-collapse", "border-spacing",
        "caption-side", "empty-cells", "line-height", "letter-spacing", "word-spacing", "white-space", "word-break", "word-wrap", "overflow-wrap",
        "hyphens", "tab-size", "text-align", "text-align-last", "text-decoration", "text-decoration-line", "text-decoration-style", "text-decoration-color",
        "text-indent", "text-transform", "text-overflow", "text-shadow", "text-rendering", "box-shadow", "quotes", "list-style", "list-style-type",
        "list-style-position", "list-style-image", "orphans", "widows", "-webkit-text-size-adjust", "-ms-text-size-adjust", "text-size-adjust",
        "font", "font-family", "font-size", "font-style", "font-variant", "font-weight", "font-stretch", "font-kerning", "font-size-adjust",
        "background", "background-color", "background-image", "background-repeat", "background-position", "background-size", "background-origin",
        "background-clip", "background-attachment",
        "border", "border-top", "border-right", "border-bottom", "border-left", "border-color", "border-style", "border-width", "border-radius",
        "border-top-color", "border-right-color", "border-bottom-color", "border-left-color", "border-top-style", "border-right-style",
        "border-bottom-style", "border-left-style", "border-top-width", "border-right-width", "border-bottom-width", "border-left-width",
        "border-top-left-radius", "border-top-right-radius", "border-bottom-left-radius", "border-bottom-right-radius",
        "margin", "margin-top", "margin-right", "margin-bottom", "margin-left",
        "padding", "padding-top", "padding-right", "padding-bottom", "padding-left",
        "outline", "outline-color", "outline-style", "outline-width", "outline-offset",
    };

    // Accepted only when re-reading our own output (the wrapper's clipping rule).
    private static readonly HashSet<string> VerifyOnlyProperties = new(StringComparer.Ordinal) { "position", "contain", "isolation" };

    private static readonly HashSet<string> Functions = new(StringComparer.Ordinal)
    {
        "rgb", "rgba", "hsl", "hsla", "hwb", "lab", "lch", "oklab", "oklch", "color", "calc", "min", "max", "clamp",
        "linear-gradient", "radial-gradient", "conic-gradient", "repeating-linear-gradient", "repeating-radial-gradient", "repeating-conic-gradient",
    };

    private static readonly HashSet<string> PseudoClasses = new(StringComparer.Ordinal)
    {
        "hover", "focus", "active", "visited", "link", "any-link", "first-child", "last-child", "only-child", "first-of-type", "last-of-type",
        "only-of-type", "empty", "checked", "disabled", "enabled", "focus-within", "focus-visible", "lang",
    };

    private static readonly HashSet<string> PseudoElements = new(StringComparer.Ordinal)
    {
        "first-line", "first-letter", "before", "after", "selection", "marker",
    };

    // ---- declarations ----

    /// <summary>
    /// Rewrites a <c>style</c> attribute or a rule's block. Returns the declarations as <c>a:b;c:d</c> (no trailing semicolon), or empty when
    /// nothing survived.
    /// </summary>
    /// <param name="text">The declaration list.</param>
    /// <param name="facts">Size and visibility the kept declarations state.</param>
    /// <param name="isFontFace">The block of an <c>@font-face</c>: font descriptors instead of properties.</param>
    public string SanitizeDeclarations(string text, out StyleFacts facts, bool isFontFace = false)
    {
        facts = default;
        if (!Charge(text.Length))
        {
            return string.Empty;
        }

        CssTree tree;
        try
        {
            tree = CssTree.ParseDeclarationList(text);
        }
        catch (CssLimitExceededException)
        {
            cx.Change(SanitizerChangeKind.LimitReached, "css");
            return string.Empty;
        }

        return WriteDeclarations(tree.Root, out facts, isFontFace);
    }

    private string WriteDeclarations(CssNode block, out StyleFacts facts, bool isFontFace)
    {
        facts = default;
        StringBuilder sb = new();
        foreach (CssNode d in block.Declarations)
        {
            if (d.Kind != CssNodeKind.Declaration)
            {
                continue;
            }

            string name = d.Name.ToLowerInvariant();
            bool known = isFontFace
                ? name is "font-family" or "font-style" or "font-weight" or "font-stretch" or "font-display" or "src" or "unicode-range"
                : Properties.Contains(name) || (cx.Verify && VerifyOnlyProperties.Contains(name));
            if (d.IsCustomProperty || !known)
            {
                cx.Change(SanitizerChangeKind.CssDeclarationRemoved, name);
                continue;
            }

            StringBuilder value = new();
            RemoteContentKind kind = isFontFace ? RemoteContentKind.Font
                : name.StartsWith("background", StringComparison.Ordinal) ? RemoteContentKind.Background
                : RemoteContentKind.Image;
            if (!WriteValue(d.Value, value, 0, name, kind) || value.ToString().Trim() is not { Length: > 0 } v)
            {
                cx.Change(SanitizerChangeKind.CssDeclarationRemoved, name);
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append(';');
            }

            sb.Append(name).Append(':').Append(v);
            if (d.IsImportant)
            {
                sb.Append(" !important");
            }

            Note(name, v, ref facts);
        }

        return sb.ToString();
    }

    private static void Note(string name, string value, ref StyleFacts f)
    {
        switch (name)
        {
            case "width":
            case "max-width":
                f.TinyWidth |= IsTiny(value);
                break;
            case "height":
            case "max-height":
                f.TinyHeight |= IsTiny(value);
                break;
            case "display":
                f.Hidden |= value == "none";
                break;
            case "visibility":
                f.Hidden |= value is "hidden" or "collapse";
                break;
            case "opacity":
                f.Hidden |= double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double o) && o == 0;
                break;
        }
    }

    private static bool IsTiny(string v)
    {
        // "0", "1px", "0px", "1" and the like: a box of at most one pixel.
        string t = v.Trim().ToLowerInvariant();
        if (t.EndsWith("px", StringComparison.Ordinal))
        {
            t = t[..^2];
        }

        return double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double n) && n >= 0 && n <= 1;
    }

    private bool Charge(int n)
    {
        cx.CssBudget -= n;
        if (cx.CssBudget < 0)
        {
            cx.Change(SanitizerChangeKind.LimitReached, "css");
            cx.Truncated = true;
            return false;
        }

        return true;
    }

    private bool WriteValue(ComponentValueList list, StringBuilder sb, int depth, string prop, RemoteContentKind kind)
    {
        if (depth > MaxValueDepth)
        {
            return false;
        }

        bool lastSpace = true;
        CssTokenKind previous = CssTokenKind.Delim;
        foreach (ComponentValue cv in list)
        {
            if (cv.IsWhitespace)
            {
                if (!lastSpace)
                {
                    sb.Append(' ');
                }

                lastSpace = true;
                continue;
            }

            // A parenthesis right after a number, dimension or hash ("0url(x)") reads as a function name to a parser that tokenises
            // differently from the spec; it is never legitimate in a property value, so the declaration goes.
            if (cv.Kind == ComponentValueKind.SimpleBlock && !lastSpace
                && previous is CssTokenKind.Number or CssTokenKind.Percentage or CssTokenKind.Dimension or CssTokenKind.Hash or CssTokenKind.Ident or CssTokenKind.QuotedString)
            {
                return false;
            }

            lastSpace = false;
            CssToken t = cv.Token;
            previous = cv.Kind == ComponentValueKind.Token ? t.Kind : CssTokenKind.Delim;
            if (cv.Kind == ComponentValueKind.Function)
            {
                string fn = t.Value.ToLowerInvariant();
                if (fn == "url")
                {
                    if (!WriteUrlFunction(cv, sb, prop, kind))
                    {
                        return false;
                    }

                    continue;
                }

                bool fontSrcFn = prop == "src" && fn is "local" or "format";
                if (!Functions.Contains(fn) && !fontSrcFn)
                {
                    return false;
                }

                sb.Append(fn).Append('(');
                if (fontSrcFn)
                {
                    foreach (ComponentValue a in cv.Children)
                    {
                        if (!a.IsWhitespace && a.Kind == ComponentValueKind.Token && a.Token.Kind is CssTokenKind.QuotedString or CssTokenKind.Ident)
                        {
                            sb.Append(a.Token.Kind == CssTokenKind.Ident ? CssSerializer.SerializeIdentifier(a.Token.Value) : CssSerializer.SerializeString(a.Token.Value));
                        }
                        else if (!a.IsWhitespace)
                        {
                            return false;
                        }
                    }
                }
                else if (!cv.IsClosed || !WriteValue(cv.Children, sb, depth + 1, prop, kind))
                {
                    return false;
                }

                sb.Append(')');
                continue;
            }

            if (cv.Kind == ComponentValueKind.SimpleBlock)
            {
                if (t.Kind != CssTokenKind.LeftParen || !cv.IsClosed)
                {
                    return false;
                }

                sb.Append('(');
                if (!WriteValue(cv.Children, sb, depth + 1, prop, kind))
                {
                    return false;
                }

                sb.Append(')');
                continue;
            }

            switch (t.Kind)
            {
                case CssTokenKind.Ident:
                    sb.Append(CssSerializer.SerializeIdentifier(t.Value));
                    break;
                case CssTokenKind.Number:
                case CssTokenKind.Percentage:
                case CssTokenKind.Dimension:
                    ReadOnlySpan<char> num = t.NumberText;
                    if (num.Length is 0 or > 24)
                    {
                        return false;
                    }

                    sb.Append(num);
                    if (t.Kind == CssTokenKind.Percentage)
                    {
                        sb.Append('%');
                    }
                    else if (t.Kind == CssTokenKind.Dimension)
                    {
                        sb.Append(CssSerializer.SerializeIdentifier(t.Value));
                    }

                    break;
                case CssTokenKind.Hash:
                    string h = t.Value;
                    if (h.Length is 0 or > 16 || !h.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z')))
                    {
                        return false;
                    }

                    sb.Append('#').Append(h);
                    break;
                case CssTokenKind.QuotedString:
                    if (t.Value.Length > 512)
                    {
                        return false;
                    }

                    sb.Append(CssSerializer.SerializeString(t.Value));
                    break;
                case CssTokenKind.Comma:
                    sb.Append(',');
                    break;
                case CssTokenKind.Delim:
                    string dv = t.Value;
                    if (dv is not ("/" or "+" or "-" or "*"))
                    {
                        return false;
                    }

                    sb.Append(dv);
                    break;
                case CssTokenKind.Url:
                    if (!WriteUrl(t.Value, sb, prop, kind))
                    {
                        return false;
                    }

                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    private bool WriteUrlFunction(ComponentValue cv, StringBuilder sb, string prop, RemoteContentKind kind)
    {
        // url("x") parses as a function token holding one string; url(x) as a url token (handled by the caller's switch).
        string? url = null;
        foreach (ComponentValue a in cv.Children)
        {
            if (a.IsWhitespace)
            {
                continue;
            }

            if (url is not null || a.Kind != ComponentValueKind.Token || a.Token.Kind != CssTokenKind.QuotedString)
            {
                return false;
            }

            url = a.Token.Value;
        }

        return url is not null && WriteUrl(url, sb, prop, kind);
    }

    private bool WriteUrl(string url, StringBuilder sb, string prop, RemoteContentKind kind)
    {
        string? resolved = cx.ResolveFetchUrl(url, kind, "style:" + prop);
        if (resolved is null)
        {
            return false;
        }

        sb.Append("url(").Append(CssSerializer.SerializeString(resolved)).Append(')');
        return true;
    }

    // ---- style sheets ----

    /// <summary>Rewrites a style sheet. Returns canonical rules, confined to the body wrapper, or empty.</summary>
    public string SanitizeStylesheet(string css)
    {
        if (!Charge(css.Length))
        {
            return string.Empty;
        }

        CssTree tree;
        try
        {
            tree = CssTree.ParseStylesheet(css);
        }
        catch (CssLimitExceededException)
        {
            cx.Change(SanitizerChangeKind.LimitReached, "css");
            return string.Empty;
        }

        StringBuilder sb = new();
        foreach (CssNode rule in tree.Root.Rules)
        {
            WriteRule(rule, sb, 0);
        }

        return sb.ToString();
    }

    private void WriteRule(CssNode rule, StringBuilder sb, int depth)
    {
        if (rule.Kind == CssNodeKind.QualifiedRule)
        {
            WriteQualified(rule, sb);
        }
        else if (rule.Kind == CssNodeKind.AtRule)
        {
            string name = rule.Name.ToLowerInvariant();
            if (name == "media" && depth == 0 && rule.HasBlock)
            {
                StringBuilder inner = new();
                foreach (CssNode r in rule.Rules)
                {
                    WriteRule(r, inner, depth + 1);
                }

                if (inner.Length > 0 && WriteMediaQuery(rule.Prelude, out string q))
                {
                    sb.Append("@media ").Append(q).Append('{').Append(inner).Append('}');
                }
                else if (inner.Length > 0)
                {
                    cx.Change(SanitizerChangeKind.CssRuleRemoved, "@media");
                }
            }
            else if (name == "font-face" && rule.HasBlock)
            {
                string decls = WriteDeclarations(rule, out _, true);
                if (decls.Contains("src:", StringComparison.Ordinal) && decls.Contains("font-family:", StringComparison.Ordinal))
                {
                    sb.Append("@font-face{").Append(decls).Append('}');
                }
                else
                {
                    cx.Change(SanitizerChangeKind.CssRuleRemoved, "@font-face");
                }
            }
            else
            {
                if (name == "import")
                {
                    ReportImport(rule);
                }

                cx.Change(SanitizerChangeKind.CssRuleRemoved, "@" + name);
            }
        }
    }

    private void ReportImport(CssNode rule)
    {
        foreach (ComponentValue cv in rule.Prelude)
        {
            string? u = null;
            if (cv.Kind == ComponentValueKind.Function && cv.IsFunctionNamed("url"))
            {
                foreach (ComponentValue a in cv.Children)
                {
                    if (a.Kind == ComponentValueKind.Token && a.Token.Kind == CssTokenKind.QuotedString)
                    {
                        u = a.Token.Value;
                    }
                }
            }
            else if (cv.Kind == ComponentValueKind.Token && cv.Token.Kind is CssTokenKind.Url or CssTokenKind.QuotedString)
            {
                u = cv.Token.Value;
            }

            if (u is not null)
            {
                string? n = SanitizerContext.NormalizeUrl(u);
                if (n is not null && SanitizerContext.Classify(n, out _) is UrlForm.Http or UrlForm.ProtocolRelative)
                {
                    cx.AddRemote(SanitizerContext.AbsoluteRemote(n, SanitizerContext.Classify(n, out _)), RemoteContentKind.Stylesheet, RemoteContentOutcome.ElementDropped, "style:@import");
                }

                return;
            }
        }
    }

    private static bool WriteMediaQuery(ComponentValueList prelude, out string text)
    {
        StringBuilder sb = new();
        text = string.Empty;
        if (!WriteMediaTokens(prelude, sb, 0))
        {
            return false;
        }

        text = sb.ToString().Trim();
        return text.Length > 0;
    }

    private static bool WriteMediaTokens(ComponentValueList list, StringBuilder sb, int depth)
    {
        if (depth > 4)
        {
            return false;
        }

        bool lastSpace = true;
        foreach (ComponentValue cv in list)
        {
            if (cv.IsWhitespace)
            {
                if (!lastSpace)
                {
                    sb.Append(' ');
                }

                lastSpace = true;
                continue;
            }

            lastSpace = false;
            if (cv.Kind == ComponentValueKind.SimpleBlock)
            {
                if (cv.Token.Kind != CssTokenKind.LeftParen || !cv.IsClosed)
                {
                    return false;
                }

                sb.Append('(');
                if (!WriteMediaTokens(cv.Children, sb, depth + 1))
                {
                    return false;
                }

                sb.Append(')');
                continue;
            }

            if (cv.Kind == ComponentValueKind.Function)
            {
                return false;
            }

            CssToken t = cv.Token;
            switch (t.Kind)
            {
                case CssTokenKind.Ident:
                    sb.Append(CssSerializer.SerializeIdentifier(t.Value));
                    break;
                case CssTokenKind.Number:
                case CssTokenKind.Percentage:
                case CssTokenKind.Dimension:
                    if (t.NumberText.Length is 0 or > 24)
                    {
                        return false;
                    }

                    sb.Append(t.NumberText);
                    if (t.Kind == CssTokenKind.Percentage)
                    {
                        sb.Append('%');
                    }
                    else if (t.Kind == CssTokenKind.Dimension)
                    {
                        sb.Append(CssSerializer.SerializeIdentifier(t.Value));
                    }

                    break;
                case CssTokenKind.Colon:
                    sb.Append(':');
                    break;
                case CssTokenKind.Comma:
                    sb.Append(',');
                    break;
                case CssTokenKind.Delim when t.Value is "/" or "<" or ">" or "=":
                    sb.Append(t.Value);
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    private void WriteQualified(CssNode rule, StringBuilder sb)
    {
        if (!rule.HasBlock)
        {
            return;
        }

        string decls = WriteDeclarations(rule, out _, false);
        StringBuilder sel = new();
        int kept = 0;
        List<ComponentValue> current = [];
        List<List<ComponentValue>> groups = [current];
        foreach (ComponentValue cv in rule.Prelude)
        {
            if (cv.Kind == ComponentValueKind.Token && cv.Token.Kind == CssTokenKind.Comma)
            {
                current = [];
                groups.Add(current);
            }
            else
            {
                current.Add(cv);
            }
        }

        foreach (List<ComponentValue> g in groups)
        {
            StringBuilder one = new();
            if (WriteComplexSelector(g, one, top: true, 0))
            {
                if (kept++ > 0)
                {
                    sel.Append(',');
                }

                sel.Append(one);
            }
            else
            {
                cx.Change(SanitizerChangeKind.CssRuleRemoved, "selector");
            }
        }

        if (kept == 0 || decls.Length == 0)
        {
            if (kept > 0)
            {
                cx.Change(SanitizerChangeKind.CssRuleRemoved, "empty rule");
            }

            return;
        }

        sb.Append(sel).Append('{').Append(decls).Append('}');
    }

    // ---- selectors ----

    private sealed class Compound
    {
        public readonly StringBuilder Text = new();
        public bool RootLike;
        public bool Plain = true; // only the root token so far, no class/id/attribute/pseudo
    }

    private bool WriteComplexSelector(List<ComponentValue> items, StringBuilder sb, bool top, int depth)
    {
        if (depth > 3)
        {
            return false;
        }

        List<(char Comb, Compound C)> parts = [];
        Compound? cur = null;
        char pending = '\0';
        for (int i = 0; i < items.Count; i++)
        {
            ComponentValue cv = items[i];
            if (cv.IsWhitespace)
            {
                if (cur is not null && pending == '\0')
                {
                    pending = ' ';
                    parts.Add((' ', cur));
                    cur = null;
                }

                continue;
            }

            if (cv.Kind == ComponentValueKind.Token && cv.Token.Kind == CssTokenKind.Delim && cv.Token.Value is ">" or "+" or "~")
            {
                char comb = cv.Token.Value[0];
                if (cur is not null)
                {
                    parts.Add((comb, cur));
                    cur = null;
                }
                else if (parts.Count > 0 && parts[^1].Comb == ' ')
                {
                    parts[^1] = (comb, parts[^1].C);
                }
                else
                {
                    return false;
                }

                pending = comb;
                continue;
            }

            pending = '\0';
            bool start = cur is null;
            cur ??= new Compound();
            if (!WriteSimpleSelector(items, ref i, cur, start, top && parts.Count == 0 && start, depth))
            {
                return false;
            }
        }

        if (cur is null && parts.Count > 0 && parts[^1].Comb == ' ')
        {
            // Trailing blanks before the block are not a combinator.
            cur = parts[^1].C;
            parts.RemoveAt(parts.Count - 1);
        }

        if (cur is null || cur.Text.Length == 0)
        {
            return false;
        }

        parts.Add(('\0', cur));
        // A trailing descendant marker cannot occur (whitespace only sets one when a compound follows).
        if (!top || cx.Verify)
        {
            for (int k = 0; k < parts.Count; k++)
            {
                if (k > 0)
                {
                    sb.Append(parts[k - 1].Comb == ' ' ? " " : parts[k - 1].Comb.ToString());
                }

                sb.Append(parts[k].C.Text);
            }

            return true;
        }

        // Scope: below the body wrapper. html, body and :root name the wrapper itself.
        int first = 0;
        StringBuilder outSel = new();
        if (parts[0].C.RootLike)
        {
            outSel.Append(parts[0].C.Text);
            first = 1;
            while (first < parts.Count && parts[first].C.RootLike && parts[first - 1].C.Plain && parts[first - 1].Comb is ' ' or '>')
            {
                // "html body x" -> ".omx x": body is the same box, so its tail joins the previous compound.
                outSel.Append(parts[first].C.Text, ScopeClass.Length + 1, parts[first].C.Text.Length - ScopeClass.Length - 1);
                first++;
            }
        }
        else
        {
            outSel.Append('.').Append(ScopeClass).Append(' ');
        }

        for (int k = first; k < parts.Count; k++)
        {
            if (parts[k].C.RootLike)
            {
                return false;
            }

            if (k > 0)
            {
                outSel.Append(parts[k - 1].Comb == ' ' ? " " : parts[k - 1].Comb.ToString());
            }

            outSel.Append(parts[k].C.Text);
        }

        sb.Append(outSel);
        return true;
    }

    private bool WriteSimpleSelector(List<ComponentValue> items, ref int i, Compound c, bool compoundStart, bool firstOfSelector, int depth)
    {
        ComponentValue cv = items[i];
        if (cv.Kind == ComponentValueKind.Function)
        {
            return false;
        }

        if (cv.Kind == ComponentValueKind.SimpleBlock)
        {
            c.Plain = false;
            return cv.Token.Kind == CssTokenKind.LeftBracket && cv.IsClosed && WriteAttributeSelector(cv.Children, c.Text);
        }

        CssToken t = cv.Token;
        switch (t.Kind)
        {
            case CssTokenKind.Ident:
                if (!compoundStart || c.Text.Length > 0)
                {
                    return false;
                }

                string type = t.Value.ToLowerInvariant();
                if (!cx.Verify && type is "html" or "body")
                {
                    // html and body are the wrapper itself; where they may appear is decided once the whole selector is read.
                    c.RootLike = true;
                    c.Text.Append('.').Append(ScopeClass);
                }
                else
                {
                    c.Text.Append(CssSerializer.SerializeIdentifier(type));
                }

                return true;
            case CssTokenKind.Delim when t.Value == "*":
                if (!compoundStart || c.Text.Length > 0)
                {
                    return false;
                }

                c.Text.Append('*');
                return true;
            case CssTokenKind.Hash:
                if (!t.IsHashId)
                {
                    return false;
                }

                c.Plain = false;
                c.Text.Append('#').Append(CssSerializer.SerializeIdentifier(Prefixed(t.Value)));
                return true;
            case CssTokenKind.Delim when t.Value == ".":
                if (i + 1 >= items.Count || items[i + 1].Kind != ComponentValueKind.Token || items[i + 1].Token.Kind != CssTokenKind.Ident)
                {
                    return false;
                }

                i++;
                c.Plain = false;
                c.Text.Append('.').Append(CssSerializer.SerializeIdentifier(Prefixed(items[i].Token.Value)));
                return true;
            case CssTokenKind.Colon:
                return WritePseudo(items, ref i, c, firstOfSelector && compoundStart, depth);
            default:
                return false;
        }
    }

    private string Prefixed(string name) => cx.Verify ? name : NamePrefix + name;

    private bool WritePseudo(List<ComponentValue> items, ref int i, Compound c, bool selectorStart, int depth)
    {
        bool doubleColon = false;
        if (i + 1 < items.Count && items[i + 1].Kind == ComponentValueKind.Token && items[i + 1].Token.Kind == CssTokenKind.Colon)
        {
            doubleColon = true;
            i++;
        }

        if (i + 1 >= items.Count)
        {
            return false;
        }

        ComponentValue next = items[i + 1];
        i++;
        string name = next.Token.Value.ToLowerInvariant();
        c.Plain = false;
        if (next.Kind == ComponentValueKind.Token && next.Token.Kind == CssTokenKind.Ident)
        {
            if (!doubleColon && name == "root" && selectorStart && !cx.Verify)
            {
                c.RootLike = true;
                c.Plain = true;
                c.Text.Append('.').Append(ScopeClass);
                return true;
            }

            bool ok = doubleColon ? PseudoElements.Contains(name) : PseudoClasses.Contains(name) || PseudoElements.Contains(name);
            if (!ok || name == "lang")
            {
                return false;
            }

            c.Text.Append(doubleColon ? "::" : ":").Append(name);
            return true;
        }

        if (next.Kind != ComponentValueKind.Function || doubleColon || !next.IsClosed)
        {
            return false;
        }

        if (name is "nth-child" or "nth-last-child" or "nth-of-type" or "nth-last-of-type")
        {
            StringBuilder arg = new();
            bool lastSpace = true;
            foreach (ComponentValue a in next.Children)
            {
                if (a.IsWhitespace)
                {
                    if (!lastSpace)
                    {
                        arg.Append(' ');
                    }

                    lastSpace = true;
                    continue;
                }

                lastSpace = false;
                if (a.Kind != ComponentValueKind.Token)
                {
                    return false;
                }

                CssToken at = a.Token;
                switch (at.Kind)
                {
                    case CssTokenKind.Number:
                    case CssTokenKind.Dimension:
                        if (at.NumberText.Length is 0 or > 10)
                        {
                            return false;
                        }

                        arg.Append(at.NumberText);
                        if (at.Kind == CssTokenKind.Dimension)
                        {
                            string u = at.Value.ToLowerInvariant();
                            if (!(u == "n" || u.StartsWith("n-", StringComparison.Ordinal) && u.Length > 2 && u[2..].All(char.IsAsciiDigit)))
                            {
                                return false;
                            }

                            arg.Append(u);
                        }

                        break;
                    case CssTokenKind.Ident:
                        string iv = at.Value.ToLowerInvariant();
                        if (!(iv is "n" or "odd" or "even" or "-n" || iv.StartsWith("n-", StringComparison.Ordinal) && iv[2..].All(char.IsAsciiDigit) || iv.StartsWith("-n-", StringComparison.Ordinal) && iv[3..].All(char.IsAsciiDigit)))
                        {
                            return false;
                        }

                        arg.Append(iv);
                        break;
                    case CssTokenKind.Delim when at.Value is "+" or "-":
                        arg.Append(at.Value);
                        break;
                    default:
                        return false;
                }
            }

            c.Text.Append(':').Append(name).Append('(').Append(arg.ToString().Trim()).Append(')');
            return true;
        }

        if (name == "not")
        {
            // :not() takes a selector list; each member is checked like any selector, without the scope prefix.
            List<ComponentValue> member = [];
            List<List<ComponentValue>> members = [member];
            foreach (ComponentValue a in next.Children)
            {
                if (a.Kind == ComponentValueKind.Token && a.Token.Kind == CssTokenKind.Comma)
                {
                    member = [];
                    members.Add(member);
                }
                else
                {
                    member.Add(a);
                }
            }

            StringBuilder inner = new();
            for (int m = 0; m < members.Count; m++)
            {
                if (m > 0)
                {
                    inner.Append(',');
                }

                // Trim blanks at both ends before checking.
                while (members[m].Count > 0 && members[m][0].IsWhitespace)
                {
                    members[m].RemoveAt(0);
                }

                while (members[m].Count > 0 && members[m][^1].IsWhitespace)
                {
                    members[m].RemoveAt(members[m].Count - 1);
                }

                if (!WriteComplexSelector(members[m], inner, top: false, depth + 1))
                {
                    return false;
                }
            }

            c.Text.Append(":not(").Append(inner).Append(')');
            return true;
        }

        return false;
    }

    private static bool WriteAttributeSelector(ComponentValueList list, StringBuilder sb)
    {
        StringBuilder a = new();
        int n = 0;
        string? attr = null;
        foreach (ComponentValue cv in list)
        {
            if (cv.IsWhitespace)
            {
                continue;
            }

            if (cv.Kind != ComponentValueKind.Token || n++ > 8)
            {
                return false;
            }

            CssToken t = cv.Token;
            switch (t.Kind)
            {
                case CssTokenKind.Ident:
                    if (attr is null)
                    {
                        attr = t.Value.ToLowerInvariant();
                        if (attr is "class" or "id" or "style")
                        {
                            return false;
                        }

                        a.Append(CssSerializer.SerializeIdentifier(attr));
                    }
                    else if (t.Value.ToLowerInvariant() is "i" or "s")
                    {
                        a.Append(' ').Append(t.Value.ToLowerInvariant());
                    }
                    else
                    {
                        a.Append(CssSerializer.SerializeIdentifier(t.Value));
                    }

                    break;
                case CssTokenKind.QuotedString:
                    if (attr is null || t.Value.Length > 256)
                    {
                        return false;
                    }

                    a.Append(CssSerializer.SerializeString(t.Value));
                    break;
                case CssTokenKind.Delim when attr is not null && t.Value is "=" or "~" or "|" or "^" or "$" or "*":
                    a.Append(t.Value);
                    break;
                default:
                    return false;
            }
        }

        if (attr is null)
        {
            return false;
        }

        sb.Append('[').Append(a).Append(']');
        return true;
    }
}
