using System.Globalization;
using System.Text;

namespace RecursiveAI.OneMail.Rtf;

// Writes an RtfDocument as an HTML fragment. Deterministic: the same document gives the same characters.
internal sealed class RtfHtmlWriter
{
    private readonly RtfDocument _doc;
    private readonly RtfHtmlOptions _options;
    private readonly RtfDefectList _defects;
    private readonly StringBuilder _sb = new();
    private int _domFont = -1;
    private int _domHalfPoints = 24;
    private bool _prevSpace = true;
    private bool _limited;

    private RtfHtmlWriter(RtfDocument doc, RtfHtmlOptions options, RtfDefectList defects)
    {
        _doc = doc;
        _options = options;
        _defects = defects;
    }

    internal static string Write(RtfDocument doc, RtfHtmlOptions options, RtfDefectList defects)
    {
        if (doc.Blocks.Count == 0)
        {
            return "";
        }

        var w = new RtfHtmlWriter(doc, options, defects);
        w.FindDominantStyle();
        w.WriteBody();
        return w.ToString();
    }

    public override string ToString() => _sb.ToString();

    private void FindDominantStyle()
    {
        var weights = new Dictionary<(int, int), long>();
        void Visit(List<Block> blocks)
        {
            foreach (Block b in blocks)
            {
                switch (b)
                {
                    case Paragraph p:
                        foreach (Seg s in p.Segs)
                        {
                            if (s.Kind == SegKind.Text)
                            {
                                RunStyle st = _doc.Styles[s.Style];
                                weights[(st.Font, st.HalfPoints)] = weights.GetValueOrDefault((st.Font, st.HalfPoints)) + s.Length;
                            }
                        }

                        break;
                    case Table t:
                        foreach (TableRow r in t.Rows)
                        {
                            foreach (TableCell c in r.Cells)
                            {
                                Visit(c.Blocks);
                            }
                        }

                        break;
                }
            }
        }

        Visit(_doc.Blocks);
        long best = -1;
        foreach (KeyValuePair<(int, int), long> kv in weights)
        {
            // Ties go to the smaller font index, then the smaller size, so the result does not depend on dictionary order.
            if (kv.Value > best || (kv.Value == best && (kv.Key.Item1 < _domFont || (kv.Key.Item1 == _domFont && kv.Key.Item2 < _domHalfPoints))))
            {
                best = kv.Value;
                _domFont = kv.Key.Item1;
                _domHalfPoints = kv.Key.Item2;
            }
        }

        if (best < 0)
        {
            _domFont = _doc.DefaultFont;
        }
    }

    private void WriteBody()
    {
        _sb.Append("<div class=\"rtf-body\" style=\"");
        string family = FontFamily(_domFont);
        if (family.Length > 0)
        {
            _sb.Append("font-family:").Append(family).Append(';');
        }

        _sb.Append("font-size:").Append(Points(_domHalfPoints / 2.0)).Append("pt\">\n");
        WriteBlocks(_doc.Blocks);
        _sb.Append("</div>\n");
    }

    private bool Full()
    {
        if (_sb.Length > _options.MaxOutputChars)
        {
            if (!_limited)
            {
                _limited = true;
                _defects.Add(RtfDefectKind.ContentLimitExceeded, 0, "the HTML is longer than the limit; the rest is dropped");
            }
        }

        return _limited;
    }

    // ---- blocks and lists ----

    private readonly struct OpenList(int key, bool ordered)
    {
        public readonly int Key = key;
        public readonly bool Ordered = ordered;
    }

    private void WriteBlocks(List<Block> blocks)
    {
        var open = new List<OpenList>();
        var liOpen = new List<bool>();
        foreach (Block b in blocks)
        {
            if (Full())
            {
                break;
            }

            if (b is Paragraph { Kind: not ListKind.None, ListKey: not -1 } item)
            {
                WriteListItem(item, open, liOpen);
                continue;
            }

            CloseLists(open, liOpen, 0);
            switch (b)
            {
                case Paragraph p:
                    WriteParagraph(p);
                    break;
                case Table t:
                    WriteTable(t);
                    break;
            }
        }

        CloseLists(open, liOpen, 0);
    }

    private void CloseLists(List<OpenList> open, List<bool> liOpen, int keep)
    {
        while (open.Count > keep)
        {
            int d = open.Count - 1;
            if (liOpen[d])
            {
                _sb.Append("</li>\n");
            }

            _sb.Append(open[d].Ordered ? "</ol>\n" : "</ul>\n");
            open.RemoveAt(d);
            liOpen.RemoveAt(d);
        }
    }

    private void WriteListItem(Paragraph p, List<OpenList> open, List<bool> liOpen)
    {
        bool ordered = p.Kind != ListKind.Bullet;
        int level = p.ListLevel;
        if (level > open.Count)
        {
            level = open.Count; // the item sits deeper than the list above it: one level below
            _defects.Add(RtfDefectKind.AmbiguousListLevel, 0, "a list item is deeper than the open list; it is placed one level below it");
        }

        CloseLists(open, liOpen, level + 1);
        if (open.Count == level + 1 && (open[level].Key != p.ListKey || open[level].Ordered != ordered))
        {
            CloseLists(open, liOpen, level);
        }

        if (open.Count == level + 1)
        {
            if (liOpen[level])
            {
                _sb.Append("</li>\n");
                liOpen[level] = false;
            }
        }
        else
        {
            // A nested list goes inside the item above it, which stays open.
            if (ordered)
            {
                _sb.Append("<ol");
                string? type = p.Kind switch
                {
                    ListKind.UpperAlpha => "A",
                    ListKind.LowerAlpha => "a",
                    ListKind.UpperRoman => "I",
                    ListKind.LowerRoman => "i",
                    _ => null,
                };
                if (type is not null)
                {
                    _sb.Append(" type=\"").Append(type).Append('"');
                }

                if (p.Number != 1)
                {
                    _sb.Append(" start=\"").Append(p.Number.ToString(CultureInfo.InvariantCulture)).Append('"');
                }

                _sb.Append(">\n");
            }
            else
            {
                _sb.Append("<ul>\n");
            }

            open.Add(new OpenList(p.ListKey, ordered));
            liOpen.Add(false);
        }

        _sb.Append("<li");
        ParagraphStyle(p, includeMargins: false);
        _sb.Append('>');
        liOpen[level] = true;
        WriteSegs(p);
    }

    // ---- paragraphs ----

    // ---- style attributes: declarations are written as they come; the attribute opens with the first one ----

    private bool _styleOpen;

    private void Decl(string name, string value)
    {
        _sb.Append(_styleOpen ? ';' : " style=\"").Append(name).Append(':').Append(value);
        _styleOpen = true;
    }

    private void EndStyle()
    {
        if (_styleOpen)
        {
            _sb.Append('"');
            _styleOpen = false;
        }
    }

    private void ParagraphStyle(Paragraph p, bool includeMargins)
    {
        if (p.Align != Align.Left)
        {
            Decl("text-align", p.Align switch { Align.Center => "center", Align.Right => "right", _ => "justify" });
        }

        if (includeMargins)
        {
            Decl("margin-top", Length(p.SpaceBefore));
            Decl("margin-bottom", Length(p.SpaceAfter));
            if (p.Left != 0)
            {
                Decl("margin-left", Length(p.Left));
            }

            if (p.Right != 0)
            {
                Decl("margin-right", Length(p.Right));
            }

            if (p.FirstLine != 0)
            {
                Decl("text-indent", Length(p.FirstLine));
            }
        }

        if (p.LineSpacing > 0 && p.LineSpacingMultiple)
        {
            Decl("line-height", (p.LineSpacing / 240.0).ToString("0.##", CultureInfo.InvariantCulture)); // \slmult1: 240 = single spacing
        }
        else if (p.LineSpacing != 0 && !p.LineSpacingMultiple)
        {
            Decl("line-height", Length(Math.Abs(p.LineSpacing)));
        }

        EndStyle();
    }

    private void WriteParagraph(Paragraph p)
    {
        bool hasRule = false;
        foreach (Seg s in p.Segs)
        {
            if (s.Kind == SegKind.HorizontalRule)
            {
                hasRule = true;
                _sb.Append("<hr>\n");
            }
        }

        if (hasRule && p.Segs.TrueForAll(s => s.Kind == SegKind.HorizontalRule))
        {
            return;
        }

        string tag = p.Heading > 0 ? "h" + p.Heading.ToString(CultureInfo.InvariantCulture) : "p";
        _sb.Append('<').Append(tag);
        ParagraphStyle(p, includeMargins: true);
        _sb.Append('>');
        if (p.Segs.Count == 0)
        {
            _sb.Append("<br>");
        }
        else
        {
            WriteSegs(p);
        }

        _sb.Append("</").Append(tag).Append(">\n");
    }

    // ---- runs ----

    private void WriteSegs(Paragraph p)
    {
        _prevSpace = true;
        string? href = null;
        foreach (Seg s in p.Segs)
        {
            if (s.Kind == SegKind.HorizontalRule)
            {
                continue;
            }

            RunStyle st = _doc.Styles[s.Style];
            if (st.Href != href)
            {
                if (href is not null)
                {
                    _sb.Append("</a>");
                }

                href = st.Href;
                if (href is not null)
                {
                    _sb.Append("<a href=\"");
                    AppendAttribute(href);
                    _sb.Append("\">");
                }
            }

            WriteSeg(s, st);
        }

        if (href is not null)
        {
            _sb.Append("</a>");
        }
    }

    private void WriteSeg(Seg s, RunStyle st)
    {
        switch (s.Kind)
        {
            case SegKind.Text:
                bool span = OpenRun(st);
                AppendText(s.Start, s.Length);
                CloseRun(st, span);
                break;
            case SegKind.Tab:
                _sb.Append("<span style=\"white-space:pre\">\t</span>");
                _prevSpace = true;
                break;
            case SegKind.LineBreak:
                _sb.Append("<br>");
                _prevSpace = true;
                break;
            case SegKind.Attachment:
                _sb.Append("<span data-rtf-attachment=\"").Append(s.Start.ToString(CultureInfo.InvariantCulture)).Append("\"></span>");
                break;
            case SegKind.Image:
                WriteImage(_doc.Images[s.Start]);
                break;
        }
    }

    // Fixed nesting order: span, b, i, u, s, sup/sub.
    private bool OpenRun(RunStyle st)
    {
        bool span = false;
        void Open(string name, string value)
        {
            if (!span)
            {
                _sb.Append("<span");
                span = true;
            }

            Decl(name, value);
        }

        if (st.Color >= 0) { Open("color", Hex(st.Color)); }
        if (st.Background >= 0) { Open("background-color", Hex(st.Background)); }
        if (st.Font != _domFont)
        {
            string family = FontFamily(st.Font);
            if (family.Length > 0)
            {
                Open("font-family", family);
            }
        }

        if (st.HalfPoints != _domHalfPoints) { Open("font-size", Points(st.HalfPoints / 2.0) + "pt"); }
        if (st.Caps) { Open("text-transform", "uppercase"); }
        if (st.SmallCaps) { Open("font-variant", "small-caps"); }
        if (span)
        {
            EndStyle();
            _sb.Append('>');
        }

        if (st.Bold) { _sb.Append("<b>"); }
        if (st.Italic) { _sb.Append("<i>"); }
        if (st.Underline != 0) { _sb.Append("<u>"); }
        if (st.Strike) { _sb.Append("<s>"); }
        if (st.Script == 1) { _sb.Append("<sup>"); }
        if (st.Script == 2) { _sb.Append("<sub>"); }
        return span;
    }

    private void CloseRun(RunStyle st, bool span)
    {
        if (st.Script == 2) { _sb.Append("</sub>"); }
        if (st.Script == 1) { _sb.Append("</sup>"); }
        if (st.Strike) { _sb.Append("</s>"); }
        if (st.Underline != 0) { _sb.Append("</u>"); }
        if (st.Italic) { _sb.Append("</i>"); }
        if (st.Bold) { _sb.Append("</b>"); }
        if (span)
        {
            _sb.Append("</span>");
        }
    }

    private void WriteImage(RtfImage image)
    {
        if (!image.IsDisplayable)
        {
            string format = image.ContentType.StartsWith("image/x-", StringComparison.Ordinal) ? image.ContentType[8..] : "unknown";
            _sb.Append("<span class=\"rtf-picture\" data-format=\"").Append(format).Append("\">[").Append(format).Append(" picture]</span>");
            return;
        }

        _sb.Append("<img src=\"");
        if (_options.ImageMode == RtfImageMode.ContentId)
        {
            _sb.Append("cid:").Append(image.ContentId);
        }
        else
        {
            _sb.Append("data:").Append(image.ContentType).Append(";base64,").Append(Convert.ToBase64String(image.Data));
        }

        _sb.Append("\" alt=\"");
        AppendAttribute(image.Description ?? "");
        _sb.Append('"');
        if (image.WidthPx > 0)
        {
            _sb.Append(" width=\"").Append(image.WidthPx.ToString(CultureInfo.InvariantCulture)).Append('"');
        }

        if (image.HeightPx > 0)
        {
            _sb.Append(" height=\"").Append(image.HeightPx.ToString(CultureInfo.InvariantCulture)).Append('"');
        }

        _sb.Append('>');
        _prevSpace = false;
    }

    // ---- tables ----

    private void WriteTable(Table t)
    {
        _sb.Append("<table style=\"border-collapse:collapse\">\n");
        foreach (TableRow row in t.Rows)
        {
            _sb.Append("<tr>");
            foreach (TableCell c in row.Cells)
            {
                if (c.Hidden)
                {
                    continue;
                }

                string tag = row.Header ? "th" : "td";
                _sb.Append('<').Append(tag);
                if (c.ColSpan > 1)
                {
                    _sb.Append(" colspan=\"").Append(c.ColSpan.ToString(CultureInfo.InvariantCulture)).Append('"');
                }

                if (c.RowSpan > 1)
                {
                    _sb.Append(" rowspan=\"").Append(c.RowSpan.ToString(CultureInfo.InvariantCulture)).Append('"');
                }

                if (c.Width > 0)
                {
                    Decl("width", Length(c.Width));
                }

                Decl("vertical-align", c.VAlign == 1 ? "middle" : c.VAlign == 2 ? "bottom" : "top");
                if (c.Shading >= 0)
                {
                    Decl("background-color", Hex(c.Shading));
                }

                AddBorder("top", c.Top);
                AddBorder("right", c.Right_);
                AddBorder("bottom", c.Bottom);
                AddBorder("left", c.Left);
                EndStyle();
                _sb.Append('>');
                WriteBlocks(c.Blocks);
                _sb.Append("</").Append(tag).Append('>');
            }

            _sb.Append("</tr>\n");
        }

        _sb.Append("</table>\n");
    }

    private void AddBorder(string side, Border b)
    {
        if (b.Width > 0)
        {
            Decl("border-" + side, Length(b.Width) + " solid " + (b.Color >= 0 ? Hex(b.Color) : "#000000"));
        }
    }

    // ---- text and formatting helpers ----

    private void AppendText(int start, int length)
    {
        for (int k = start; k < start + length; k++)
        {
            char c = _doc.Text[k];
            switch (c)
            {
                case ' ':
                    // Runs of spaces and a space at the start of a line are kept visible with non-breaking spaces.
                    _sb.Append(_prevSpace ? "&nbsp;" : " ");
                    _prevSpace = true;
                    continue;
                case '&': _sb.Append("&amp;"); break;
                case '<': _sb.Append("&lt;"); break;
                case '>': _sb.Append("&gt;"); break;
                case '"': _sb.Append("&quot;"); break;
                case '\u00A0': _sb.Append("&nbsp;"); break;
                case '\t':
                case '\r':
                case '\n':
                    _sb.Append(' ');
                    _prevSpace = true;
                    continue;
                default:
                    if (c < ' ' || c == '\uFFFE' || c == '\uFFFF')
                    {
                        continue; // not allowed in HTML
                    }

                    _sb.Append(c);
                    break;
            }

            _prevSpace = false;
        }
    }

    private void AppendAttribute(string value)
    {
        foreach (char c in value)
        {
            switch (c)
            {
                case '&': _sb.Append("&amp;"); break;
                case '<': _sb.Append("&lt;"); break;
                case '>': _sb.Append("&gt;"); break;
                case '"': _sb.Append("&quot;"); break;
                default:
                    if (c >= ' ' && c != '\uFFFE' && c != '\uFFFF')
                    {
                        _sb.Append(c);
                    }

                    break;
            }
        }
    }

    private string FontFamily(int index)
    {
        if (!_doc.Fonts.TryGetValue(index, out FontInfo? f) || f.Name.Length == 0)
        {
            return "";
        }

        var name = new StringBuilder();
        foreach (char c in f.Name)
        {
            if (c >= ' ' && c != '\'' && c != '"' && c != '\\' && c != ';' && c != '<' && c != '>' && c != '&')
            {
                name.Append(c);
            }
        }

        if (name.Length == 0)
        {
            return "";
        }

        string generic = f.Family switch { 1 => ",serif", 2 => ",sans-serif", 3 => ",monospace", 4 => ",cursive", 5 => ",fantasy", _ => "" };
        return "'" + name + "'" + generic;
    }

    internal static string Hex(int rgb) => "#" + rgb.ToString("x6", CultureInfo.InvariantCulture);

    internal static string Points(double pt) => pt.ToString("0.##", CultureInfo.InvariantCulture);

    internal static string Length(int twips) => twips == 0 ? "0" : Points(twips / 20.0) + "pt";
}
