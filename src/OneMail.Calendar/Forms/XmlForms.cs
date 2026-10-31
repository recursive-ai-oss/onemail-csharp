using System.Text;
using System.Xml;
using RecursiveAI.OneMail.Calendar.ContentLines;

namespace RecursiveAI.OneMail.Calendar.Forms;

/// <summary>Options for writing xCal and xCard.</summary>
public sealed record XmlFormsOptions
{
    /// <summary>The defaults: indented output.</summary>
    public static XmlFormsOptions Default { get; } = new();

    /// <summary>Indent the XML. Default true. Indentation is only added between elements, never inside values.</summary>
    public bool Indented { get; init; } = true;
}

/// <summary>
/// xCal (RFC 6321) and xCard (RFC 6351): iCalendar and vCard as XML. Reading uses <see cref="XmlReader"/> with DTDs, external
/// entities and entity expansion off; writing uses <see cref="XmlWriter"/>. Nothing is read by reflection.
/// </summary>
public static class XmlForms
{
    /// <summary>The xCal namespace, <c>urn:ietf:params:xml:ns:icalendar-2.0</c> (RFC 6321 §7.1).</summary>
    public const string XCalNamespace = "urn:ietf:params:xml:ns:icalendar-2.0";

    /// <summary>The xCard namespace, <c>urn:ietf:params:xml:ns:vcard-4.0</c> (RFC 6351 §8.1).</summary>
    public const string XCardNamespace = "urn:ietf:params:xml:ns:vcard-4.0";

    /// <summary>
    /// Writes the VCALENDAR components of a document as xCal. Other top-level components are left out and reported.
    /// Property values take the type elements of RFC 6321 §3.6 (<c>VALUE</c> becomes the element), structured values
    /// GEO and REQUEST-STATUS their child elements, parameters their typed elements.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="options">Output options, or null for the defaults.</param>
    /// <returns>The XML and the losses. Never throws on content.</returns>
    public static FormWriteResult ToXCal(ContentDocument document, XmlFormsOptions? options = null) => Write(document, options, false);

    /// <summary>
    /// Writes the VCARD components of a document as xCard (always version 4.0, RFC 6351). Cards of version 2.1 or 3.0 are
    /// written in their 4.0 forms and reported; groups become <c>group</c> elements; the VERSION property is dropped because the
    /// namespace carries it.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="options">Output options, or null for the defaults.</param>
    /// <returns>The XML and the losses. Never throws on content.</returns>
    public static FormWriteResult ToXCard(ContentDocument document, XmlFormsOptions? options = null) => Write(document, options, true);

    private static FormWriteResult Write(ContentDocument document, XmlFormsOptions? options, bool card)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= XmlFormsOptions.Default;
        LossSink losses = new();
        string ns = card ? XCardNamespace : XCalNamespace;
        using MemoryStream ms = new();
        XmlWriterSettings settings = new()
        {
            Encoding = new UTF8Encoding(false),
            Indent = options.Indented,
            OmitXmlDeclaration = false,
            CloseOutput = false,
            CheckCharacters = true,
        };
        using (XmlWriter w = XmlWriter.Create(ms, settings))
        {
            w.WriteStartDocument();
            w.WriteStartElement(null, card ? "vcards" : "icalendar", ns);
            foreach (ComponentItem item in document.Root.Items)
            {
                if (item is Component c)
                {
                    bool match = c.IsNamed(card ? "VCARD" : "VCALENDAR");
                    if (!match)
                    {
                        losses.Add(FormLossKind.ComponentNotCarried, FormText.Lower(c.Name));
                        continue;
                    }

                    Dialect d = FormCodec.DialectOf(c);
                    FComponent fc = FormCodec.ToForm(c, d, FormText.Lower(c.Name), losses);
                    if (card)
                    {
                        WriteCard(w, fc, d, ns, losses);
                    }
                    else
                    {
                        WriteCalComponent(w, fc, d, ns, losses, true);
                    }
                }
                else
                {
                    losses.Add(FormLossKind.LineNotCarried, string.Empty);
                }
            }

            w.WriteEndElement();
            w.WriteEndDocument();
        }

        return new FormWriteResult(ms.ToArray(), losses.ToList());
    }

    private static void WriteCalComponent(XmlWriter w, FComponent c, Dialect d, string ns, LossSink losses, bool top)
    {
        string path = c.Name;
        if (!IsName(c.Name))
        {
            losses.Add(FormLossKind.ComponentNotCarried, path);
            return;
        }

        w.WriteStartElement(null, c.Name, ns);
        if (c.Props.Count > 0 || top)
        {
            w.WriteStartElement(null, "properties", ns);
            foreach (FProperty p in c.Props)
            {
                WriteProperty(w, p, d, ns, path, losses);
            }

            w.WriteEndElement();
        }

        if (c.Comps.Count > 0)
        {
            w.WriteStartElement(null, "components", ns);
            foreach (FComponent child in c.Comps)
            {
                WriteCalComponent(w, child, d, ns, losses, false);
            }

            w.WriteEndElement();
        }

        w.WriteEndElement();
    }

    private static void WriteCard(XmlWriter w, FComponent c, Dialect d, string ns, LossSink losses)
    {
        w.WriteStartElement(null, "vcard", ns);
        string? group = null;
        foreach (FProperty p in c.Props)
        {
            if (p.Name == "version")
            {
                // RFC 6351 §5.1: the namespace plays the role of VERSION.
                if (p.Values.Count == 0 || p.Values[0].ScalarText.Trim() != "4.0")
                {
                    losses.Add(FormLossKind.VersionNotCarried, "vcard/version");
                }

                continue;
            }

            FormCodec.UpgradeToV4(p, d, "vcard", losses);
            if (!string.Equals(p.Group, group, StringComparison.Ordinal))
            {
                if (group is not null)
                {
                    w.WriteEndElement();
                }

                group = p.Group;
                if (group is not null)
                {
                    w.WriteStartElement(null, "group", ns);
                    w.WriteAttributeString("name", FormText.XmlSafe(group, out _));
                }
            }

            WriteProperty(w, p, Dialect.VCard40, ns, "vcard", losses);
        }

        if (group is not null)
        {
            w.WriteEndElement();
        }

        w.WriteEndElement();
    }

    // An XML name without a colon: the names of properties, parameters and components that come from content lines. The
    // framework's own character tests are used so that every name accepted here is one the XML writer accepts.
    private static bool IsName(string n)
    {
        if (n.Length == 0 || !XmlConvert.IsStartNCNameChar(n[0]))
        {
            return false;
        }

        foreach (char c in n)
        {
            if (!XmlConvert.IsNCNameChar(c))
            {
                return false;
            }
        }

        return true;
    }

    private static string X(string s, LossSink losses, string path)
    {
        string r = FormText.XmlSafe(s, out bool changed);
        if (changed)
        {
            losses.Add(FormLossKind.InvalidXmlCharacter, path);
        }

        return r;
    }

    private static void WriteProperty(XmlWriter w, FProperty p, Dialect d, string ns, string cpath, LossSink losses)
    {
        string path = cpath + "/" + p.Name;
        if (p.Name == "xml" && p.Type == "text" && p.Values.Count == 1 && p.Values[0].Kind == FKind.String && TryWriteForeignElement(w, p.Values[0].Text, ns))
        {
            return;
        }

        if (!IsName(p.Name))
        {
            losses.Add(FormLossKind.PropertyNotCarried, path);
            return;
        }

        if (p.Group is not null && d == Dialect.ICal)
        {
            losses.Add(FormLossKind.GroupNotCarried, path);
        }

        w.WriteStartElement(null, p.Name, ns);
        List<FParam> ps = p.Params.FindAll(static x => x.Name != "group");
        if (ps.Count > 0)
        {
            w.WriteStartElement(null, "parameters", ns);
            foreach (FParam fp in ps)
            {
                if (!IsName(fp.Name))
                {
                    losses.Add(FormLossKind.ParameterNotCarried, path);
                    continue;
                }

                w.WriteStartElement(null, fp.Name, ns);
                string pt = FormSchema.ParamXmlType(d, fp.Name);
                // A parameter written without a value is one empty value, as in jCal and jCard.
                foreach (string v in fp.Values.Count == 0 ? [string.Empty] : fp.Values)
                {
                    WriteParamValue(w, pt, v, ns, d, losses, path);
                }

                w.WriteEndElement();
            }

            w.WriteEndElement();
        }

        WriteValues(w, p, d, ns, losses, path);
        w.WriteEndElement();
    }

    private static void WriteParamValue(XmlWriter w, string pt, string v, string ns, Dialect d, LossSink losses, string path)
    {
        string t = pt;
        string text = v;
        if (pt == "integer" && FormText.NormalizeNumber(v, true) is { } n)
        {
            text = n;
        }
        else if (pt == "integer")
        {
            t = "text";
        }
        else if (pt == "boolean")
        {
            if (v.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || v.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
            {
                text = v.ToLowerInvariant();
            }
            else
            {
                t = "text";
            }
        }

        _ = d;
        w.WriteStartElement(null, t, ns);
        w.WriteString(X(text, losses, path));
        w.WriteEndElement();
    }

    private static string XmlText(string type, string text, Dialect d)
    {
        // xCard keeps vCard's own date, time and offset spelling (RFC 6351 Appendix A patterns); xCal uses the extended form.
        if (!d.IsVCard())
        {
            return text;
        }

        switch (type)
        {
            case "date" or "time" or "date-time" or "date-and-or-time" or "timestamp":
                return (DateForms.VCardDt.Parse(text) ?? DateForms.VCardDt.ParseTimeOnly(text))?.ToBasic() ?? text;
            case "utc-offset":
                return DateForms.OffsetToBasic(text);
            default:
                return text;
        }
    }

    private static void WriteValues(XmlWriter w, FProperty p, Dialect d, string ns, LossSink losses, string path)
    {
        string[]? parts = FormSchema.XmlPartNames(d, p.Name);
        PropInfo info = FormSchema.Lookup(d, p.Name);
        FormValue? first = p.Values.Count > 0 ? p.Values[0] : null;

        if (parts is not null && p.Type == info.Type && first is not null)
        {
            List<FormValue> comps = first.Kind == FKind.Array ? first.Items : [first];
            bool optional = p.Name is "request-status" or "gender" or "clientpidmap";
            int count = optional ? Math.Min(comps.Count, parts.Length) : parts.Length;
            if (comps.Count > parts.Length)
            {
                losses.Add(FormLossKind.ExtraComponentsDropped, path);
            }

            for (int i = 0; i < count; i++)
            {
                FormValue c = i < comps.Count ? comps[i] : FormValue.Str(string.Empty);
                IEnumerable<FormValue> items = c.Kind == FKind.Array ? c.Items : [c];
                bool any = false;
                foreach (FormValue it in items)
                {
                    any = true;
                    w.WriteElementString(parts[i], ns, X(it.ScalarText, losses, path));
                }

                if (!any)
                {
                    w.WriteElementString(parts[i], ns, string.Empty);
                }
            }

            return;
        }

        if (p.Name == "org" && d.IsVCard() && p.Type == "text" && first is not null)
        {
            // RFC 6351 Appendix A: ORG is a list of text elements, the organisation then its units.
            IEnumerable<FormValue> comps = first.Kind == FKind.Array ? first.Items : [first];
            foreach (FormValue c in comps)
            {
                w.WriteElementString("text", ns, X(c.ScalarText, losses, path));
            }

            return;
        }

        string type = IsName(p.Type) ? p.Type : "unknown";
        if (!IsName(p.Type))
        {
            losses.Add(FormLossKind.TypeNameNotCarried, path);
        }
        foreach (FormValue v in p.Values)
        {
            switch (v.Kind)
            {
                case FKind.Array when p.Type == "period" && v.Items.Count == 2:
                    w.WriteStartElement(null, "period", ns);
                    w.WriteElementString("start", ns, v.Items[0].ScalarText);
                    w.WriteElementString(DateForms.IsDuration(v.Items[1].ScalarText) ? "duration" : "end", ns, v.Items[1].ScalarText);
                    w.WriteEndElement();
                    break;
                case FKind.Object when p.Type == "recur":
                    WriteRecur(w, v, ns, losses, path);
                    break;
                default:
                    w.WriteElementString(ElementFor(type, v), ns, X(XmlText(p.Type, FlattenScalar(v), d), losses, path));
                    break;
            }
        }
    }

    // xCard has no date-and-or-time element: RFC 6351 Appendix A makes it one of date, date-time or time, by the value.
    private static string ElementFor(string type, FormValue v)
    {
        if (type != "date-and-or-time")
        {
            return type;
        }

        DateForms.VCardDt? dt = DateForms.VCardDt.Parse(v.ScalarText) ?? DateForms.VCardDt.ParseTimeOnly(v.ScalarText);
        return dt is null ? "text" : dt.HasDate ? (dt.HasTime || dt.HasT ? "date-time" : "date") : "time";
    }

    private static string FlattenScalar(FormValue v) => v.Kind switch
    {
        FKind.Array => string.Join(',', v.Items.Select(FlattenScalar)),
        FKind.Object => string.Empty,
        _ => v.ScalarText,
    };

    // RFC 6321 Appendix A fixes the order of the recurrence rule parts.
    private static readonly string[] RecurOrder =
        ["freq", "until", "count", "interval", "bysecond", "byminute", "byhour", "byday", "bymonthday", "byyearday", "byweekno", "bymonth", "bysetpos", "wkst"];

    private static void WriteRecur(XmlWriter w, FormValue obj, string ns, LossSink losses, string path)
    {
        w.WriteStartElement(null, "recur", ns);
        List<KeyValuePair<string, FormValue>> ordered = [];
        foreach (string k in RecurOrder)
        {
            foreach (KeyValuePair<string, FormValue> m in obj.Members)
            {
                if (m.Key == k)
                {
                    ordered.Add(m);
                }
            }
        }

        foreach (KeyValuePair<string, FormValue> m in obj.Members)
        {
            if (Array.IndexOf(RecurOrder, m.Key) < 0)
            {
                ordered.Add(m);
            }
        }

        foreach (KeyValuePair<string, FormValue> m in ordered)
        {
            if (!IsName(m.Key))
            {
                continue;
            }

            if (m.Value.Kind == FKind.Array)
            {
                foreach (FormValue i in m.Value.Items)
                {
                    w.WriteElementString(m.Key, ns, X(i.ScalarText, losses, path));
                }
            }
            else
            {
                w.WriteElementString(m.Key, ns, X(m.Value.ScalarText, losses, path));
            }
        }

        w.WriteEndElement();
    }

    // RFC 6351 §6, RFC 6321 §4.1: the value of an XML property that is a single element from another namespace goes back
    // into the document as that element.
    private static bool TryWriteForeignElement(XmlWriter w, string xml, string ns)
    {
        if (xml.Length == 0 || xml.Length > 1_000_000)
        {
            return false;
        }

        try
        {
            using XmlReader r = XmlReader.Create(new StringReader(xml), ReaderSettings(FormLimits.Default, ConformanceLevel.Document));
            r.MoveToContent();
            if (r.NodeType != XmlNodeType.Element || r.NamespaceURI.Length == 0 || r.NamespaceURI == ns || r.NamespaceURI is XCalNamespace or XCardNamespace)
            {
                return false;
            }

            // Read the whole element first so that a malformed value falls back to the plain XML property without a half-written element.
            string outer = r.ReadOuterXml();
            using XmlReader again = XmlReader.Create(new StringReader(outer), ReaderSettings(FormLimits.Default, ConformanceLevel.Document));
            again.MoveToContent();
            w.WriteNode(again, true);
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static XmlReaderSettings ReaderSettings(FormLimits limits, ConformanceLevel level) => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 0,
        MaxCharactersInDocument = limits.MaxInputBytes,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = false,
        ConformanceLevel = level,
    };

    // ---- reading ----

    private sealed class XEl
    {
        internal string Name = string.Empty;
        internal string Ns = string.Empty;
        internal string? GroupName;
        internal string Text = string.Empty;
        internal bool Foreign;
        internal string Outer = string.Empty;
        internal int Line;
        internal int Col;
        internal List<XEl> Kids = [];
    }

    private sealed class LimitException(FormDefectKind kind) : Exception
    {
        internal FormDefectKind Kind { get; } = kind;
    }

    /// <summary>
    /// Reads xCal or xCard. The root decides: <c>icalendar</c> gives VCALENDAR components, <c>vcards</c> gives VCARD components
    /// (VERSION 4.0 is added first). Elements of another namespace under properties become an <c>XML</c> property holding
    /// their text (RFC 6351 §6).
    /// </summary>
    /// <param name="xml">UTF-8 (or BOM-marked UTF-16) XML.</param>
    /// <param name="limits">Resource limits, or null for <see cref="FormLimits.Default"/>.</param>
    /// <returns>The document and the defects. Never throws on content; a DOCTYPE, malformed XML or a limit gives an empty document and a defect.</returns>
    public static FormReadResult FromXml(ReadOnlyMemory<byte> xml, FormLimits? limits = null)
    {
        limits ??= FormLimits.Default;
        DefectSink defects = new();
        ContentDocument doc = new();
        if (xml.Length > limits.MaxInputBytes)
        {
            defects.Add(FormDefectKind.InputTooLarge, 0, string.Empty);
            return new FormReadResult(doc, defects.Items);
        }

        XEl? root = Parse(xml, limits, defects);
        if (root is null)
        {
            return new FormReadResult(doc, defects.Items);
        }

        bool cal = root.Name == "icalendar" && (root.Ns == XCalNamespace || root.Ns.Length == 0);
        bool card = root.Name == "vcards" && (root.Ns == XCardNamespace || root.Ns.Length == 0);
        if (!cal && !card)
        {
            defects.Add(FormDefectKind.UnrecognizedRoot, -1, root.Name, root.Line, root.Col);
            return new FormReadResult(doc, defects.Items);
        }

        foreach (XEl e in root.Kids)
        {
            if (e.Foreign)
            {
                defects.Add(FormDefectKind.ForeignContent, -1, root.Name, e.Line, e.Col);
                continue;
            }

            if (cal && e.Name == "vcalendar")
            {
                FComponent fc = MapCalComponent(e, "vcalendar", defects);
                doc.Root.Add(FormCodec.FromForm(fc, Dialect.ICal, fc.Name, defects));
            }
            else if (card && e.Name == "vcard")
            {
                FComponent fc = MapCard(e, defects);
                doc.Root.Add(FormCodec.FromForm(fc, Dialect.VCard40, fc.Name, defects));
            }
            else
            {
                defects.Add(FormDefectKind.MalformedStructure, -1, root.Name + "/" + e.Name, e.Line, e.Col);
            }
        }

        return new FormReadResult(doc, defects.Items);
    }

    /// <summary>Reads xCal or xCard from a string.</summary>
    /// <param name="xml">The XML text.</param>
    /// <param name="limits">Resource limits, or null for the defaults.</param>
    /// <returns>See <see cref="FromXml(ReadOnlyMemory{byte}, FormLimits?)"/>.</returns>
    public static FormReadResult FromXml(string xml, FormLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(xml);
        return FromXml(Encoding.UTF8.GetBytes(xml), limits);
    }

    private static XEl? Parse(ReadOnlyMemory<byte> data, FormLimits limits, DefectSink defects)
    {
        int nodes = 0;
        try
        {
            using MemoryStream ms = new(data.ToArray(), false);
            using XmlReader r = XmlReader.Create(ms, ReaderSettings(limits, ConformanceLevel.Document));
            IXmlLineInfo li = (IXmlLineInfo)r;
            Stack<XEl> stack = new();
            XEl? root = null;
            bool read = r.Read();
            while (read)
            {
                if (r.Depth > limits.MaxDepth)
                {
                    throw new LimitException(FormDefectKind.DepthLimitExceeded);
                }

                switch (r.NodeType)
                {
                    case XmlNodeType.Element:
                    {
                        if (++nodes > limits.MaxNodes)
                        {
                            throw new LimitException(FormDefectKind.NodeLimitExceeded);
                        }

                        bool known = r.NamespaceURI.Length == 0 || r.NamespaceURI is XCalNamespace or XCardNamespace;
                        XEl el = new() { Name = r.LocalName, Ns = r.NamespaceURI, Line = li.LineNumber, Col = li.LinePosition };
                        if (stack.Count > 0 && !known)
                        {
                            // Foreign content under a property container is kept as its own XML text.
                            el.Foreign = true;
                            el.Outer = r.ReadOuterXml();
                            CheckString(el.Outer, limits);
                            stack.Peek().Kids.Add(el);
                            read = r.NodeType != XmlNodeType.None && !r.EOF;
                            continue;
                        }

                        if (r.NamespaceURI.Length == 0 || known)
                        {
                            el.GroupName = r.GetAttribute("name");
                        }

                        if (stack.Count > 0)
                        {
                            stack.Peek().Kids.Add(el);
                        }
                        else
                        {
                            root = el;
                        }

                        if (r.IsEmptyElement)
                        {
                            break;
                        }

                        stack.Push(el);
                        break;
                    }

                    case XmlNodeType.EndElement:
                        if (stack.Count > 0)
                        {
                            stack.Pop();
                        }

                        break;
                    case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                        if (stack.Count > 0)
                        {
                            XEl top = stack.Peek();
                            if (top.Text.Length + r.Value.Length > limits.MaxStringChars)
                            {
                                throw new LimitException(FormDefectKind.StringTooLong);
                            }

                            top.Text += r.Value;
                        }

                        break;
                    default:
                        break;
                }

                read = r.Read();
            }

            return root;
        }
        catch (LimitException e)
        {
            defects.Add(e.Kind, -1, string.Empty);
            return null;
        }
        catch (XmlException e)
        {
            bool dtd = System.Text.Encoding.UTF8.GetString(data.Span).Contains("<!DOCTYPE", StringComparison.Ordinal);
            defects.Add(dtd ? FormDefectKind.DtdProhibited : FormDefectKind.NotWellFormed, -1, string.Empty, e.LineNumber, e.LinePosition);
            return null;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IndexOutOfRangeException or IOException or DecoderFallbackException)
        {
            // The framework's reader throws these, rather than XmlException, for some broken encoding declarations (an
            // encoding switch halfway through a misdeclared stream); to a caller that is a document that is not well formed.
            defects.Add(FormDefectKind.NotWellFormed, -1, string.Empty);
            return null;
        }
    }

    private static void CheckString(string s, FormLimits limits)
    {
        if (s.Length > limits.MaxStringChars)
        {
            throw new LimitException(FormDefectKind.StringTooLong);
        }
    }

    private static FComponent MapCalComponent(XEl e, string path, DefectSink defects)
    {
        FComponent c = new() { Name = FormText.Lower(e.Name) };
        foreach (XEl k in e.Kids)
        {
            if (k.Foreign)
            {
                defects.Add(FormDefectKind.ForeignContent, -1, path, k.Line, k.Col);
            }
            else if (k.Name == "properties")
            {
                foreach (XEl pe in k.Kids)
                {
                    if (pe.Foreign)
                    {
                        c.Props.Add(ForeignProperty(pe));
                    }
                    else
                    {
                        c.Props.Add(MapProperty(pe, Dialect.ICal, null, path, defects));
                    }
                }
            }
            else if (k.Name == "components")
            {
                foreach (XEl ce in k.Kids)
                {
                    if (!ce.Foreign)
                    {
                        c.Comps.Add(MapCalComponent(ce, path + "/" + FormText.Lower(ce.Name), defects));
                    }
                }
            }
            else
            {
                defects.Add(FormDefectKind.MalformedStructure, -1, path + "/" + k.Name, k.Line, k.Col);
            }
        }

        return c;
    }

    private static FComponent MapCard(XEl e, DefectSink defects)
    {
        FComponent c = new() { Name = "vcard" };
        void Add(XEl pe, string? group)
        {
            if (pe.Foreign)
            {
                c.Props.Add(ForeignProperty(pe));
            }
            else
            {
                c.Props.Add(MapProperty(pe, Dialect.VCard40, group, "vcard", defects));
            }
        }

        foreach (XEl k in e.Kids)
        {
            if (!k.Foreign && k.Name == "group")
            {
                foreach (XEl pe in k.Kids)
                {
                    Add(pe, k.GroupName ?? string.Empty);
                }
            }
            else
            {
                Add(k, null);
            }
        }

        // RFC 6351 §5.1: the namespace plays the role of VERSION, so the text form gets one.
        if (!c.Props.Exists(static p => p.Name == "version"))
        {
            FProperty v = new() { Name = "version", Type = "text" };
            v.Values.Add(FormValue.Str("4.0"));
            c.Props.Insert(0, v);
        }

        return c;
    }

    private static FProperty ForeignProperty(XEl e)
    {
        FProperty p = new() { Name = "xml", Type = "text", Line = e.Line, Column = e.Col };
        p.Values.Add(FormValue.Str(e.Outer));
        return p;
    }

    private static FProperty MapProperty(XEl e, Dialect d, string? group, string cpath, DefectSink defects)
    {
        FProperty f = new() { Name = FormText.Lower(e.Name), Line = e.Line, Column = e.Col, Group = string.IsNullOrEmpty(group) ? null : group };
        string path = cpath + "/" + f.Name;
        PropInfo info = FormSchema.Lookup(d, f.Name);
        List<XEl> valueEls = [];
        foreach (XEl k in e.Kids)
        {
            if (k.Foreign)
            {
                defects.Add(FormDefectKind.ForeignContent, -1, path, k.Line, k.Col);
            }
            else if (k.Name == "parameters")
            {
                foreach (XEl pe in k.Kids)
                {
                    if (pe.Foreign)
                    {
                        continue;
                    }

                    FParam fp = new(FormText.Lower(pe.Name));
                    if (pe.Kids.Count == 0)
                    {
                        fp.Values.Add(pe.Text);
                    }

                    foreach (XEl ve in pe.Kids)
                    {
                        fp.Values.Add(ve.Text);
                    }

                    f.Params.Add(fp);
                }
            }
            else
            {
                valueEls.Add(k);
            }
        }

        string[]? parts = FormSchema.XmlPartNames(d, f.Name);
        if (parts is not null && valueEls.Count > 0 && valueEls.TrueForAll(v => Array.IndexOf(parts, v.Name) >= 0))
        {
            bool optional = f.Name is "request-status" or "gender" or "clientpidmap";
            int last = -1;
            List<FormValue> comps = new(parts.Length);
            bool numeric = d == Dialect.ICal && f.Name == "geo";
            for (int i = 0; i < parts.Length; i++)
            {
                List<XEl> els = valueEls.FindAll(v => v.Name == parts[i]);
                if (els.Count > 0)
                {
                    last = i;
                }

                FormValue Make(string text)
                {
                    string? n = numeric ? FormText.NormalizeNumber(text, false) : null;
                    return n is null ? FormValue.Str(text) : FormValue.Num(n);
                }

                comps.Add(els.Count switch
                {
                    0 => FormValue.Str(string.Empty),
                    1 => Make(els[0].Text),
                    _ => FormValue.Arr(els.ConvertAll(v => Make(v.Text))),
                });
            }

            if (optional)
            {
                comps.RemoveRange(last + 1, comps.Count - last - 1);
            }

            f.Type = numeric ? "float" : "text";
            f.Values.Add(comps.Count == 1 && d.IsVCard() ? comps[0] : FormValue.Arr(comps));
            return f;
        }

        if (valueEls.Count == 0)
        {
            defects.Add(FormDefectKind.MissingValueElement, -1, path, e.Line, e.Col);
            f.Type = info.Type;
            f.Values.Add(FormValue.Str(e.Text));
            return f;
        }

        f.Type = FormText.Lower(valueEls[0].Name);
        if (d.IsVCard() && info.Type == "date-and-or-time" && f.Type is "date" or "date-time" or "time")
        {
            f.Type = info.Type;
        }

        foreach (XEl ve in valueEls)
        {
            string t = FormText.Lower(ve.Name);
            if (t != f.Type && !(f.Type == "date-and-or-time" && t is "date" or "date-time" or "time"))
            {
                defects.Add(FormDefectKind.MixedValueTypes, -1, path, ve.Line, ve.Col);
            }

            f.Values.Add(MapValue(ve, f.Type == "date-and-or-time" ? "date-and-or-time" : t, d));
        }

        if (d.IsVCard() && info.Shape == Shape.SemiList && f.Type == "text" && f.Values.Count > 1)
        {
            FormValue arr = FormValue.Arr([.. f.Values]);
            f.Values.Clear();
            f.Values.Add(arr);
        }

        return f;
    }

    private static FormValue MapValue(XEl ve, string type, Dialect d)
    {
        string text = ve.Text;
        switch (type)
        {
            case "period" when ve.Kids.Count >= 2:
            {
                XEl? start = ve.Kids.Find(static k => k.Name == "start");
                XEl? other = ve.Kids.Find(static k => k.Name is "end" or "duration");
                if (start is not null && other is not null)
                {
                    return FormValue.Arr([FormValue.Str(start.Text), FormValue.Str(other.Text)]);
                }

                break;
            }

            case "recur" when ve.Kids.Count > 0:
                return MapRecur(ve);
            case "boolean":
                return text.Trim() switch
                {
                    "true" or "1" => FormValue.Boolean(true),
                    "false" or "0" => FormValue.Boolean(false),
                    _ => FormValue.Str(text),
                };
            case "integer" or "float":
            {
                string? n = FormText.NormalizeNumber(text, type == "integer");
                return n is null ? FormValue.Str(text) : FormValue.Num(n);
            }

            case "date" or "time" or "date-time" or "date-and-or-time" or "timestamp" when d.IsVCard():
                return FormValue.Str((DateForms.VCardDt.Parse(text) ?? DateForms.VCardDt.ParseTimeOnly(text))?.ToExtended() ?? text);
            case "utc-offset" when d.IsVCard():
                return FormValue.Str(DateForms.VCardOffsetToExtended(text) ?? text);
            default:
                break;
        }

        return FormValue.Str(text);
    }

    private static FormValue MapRecur(XEl ve)
    {
        FormValue obj = FormValue.Obj();
        foreach (XEl k in ve.Kids)
        {
            string key = FormText.Lower(k.Name);
            bool numeric = key is "count" or "interval" or "bysecond" or "byminute" or "byhour" or "bymonthday" or "byyearday" or "byweekno" or "bymonth" or "bysetpos";
            string? n = numeric ? FormText.NormalizeNumber(k.Text, true) : null;
            FormValue v = n is null ? FormValue.Str(k.Text) : FormValue.Num(n);
            int i = obj.Members.FindIndex(m => m.Key == key);
            if (i < 0)
            {
                obj.Members.Add(new KeyValuePair<string, FormValue>(key, v));
            }
            else
            {
                // A part repeated as elements is a list (RFC 6321 §3.6.10).
                FormValue existing = obj.Members[i].Value;
                if (existing.Kind == FKind.Array)
                {
                    existing.Items.Add(v);
                }
                else
                {
                    obj.Members[i] = new KeyValuePair<string, FormValue>(key, FormValue.Arr([existing, v]));
                }
            }
        }

        return obj;
    }
}
