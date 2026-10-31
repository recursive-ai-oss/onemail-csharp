using System.Globalization;
using System.Text;
using RecursiveAI.OneMail.Calendar.ContentLines;

namespace RecursiveAI.OneMail.Calendar.Forms;

// Content lines <-> the in-between model. Every rule here cites the RFC section that sets it.
internal static class FormCodec
{
    // ---- text -> model ----

    internal static Dialect DialectOf(Component c)
    {
        if (!c.IsNamed("VCARD"))
        {
            return Dialect.ICal;
        }

        return c.FindProperty("VERSION")?.Value.Trim() switch
        {
            "2.1" => Dialect.VCard21,
            "3.0" => Dialect.VCard30,
            _ => Dialect.VCard40,
        };
    }

    internal static FComponent ToForm(Component c, Dialect d, string path, LossSink losses)
    {
        FComponent fc = new() { Name = FormText.Lower(c.Name), Offset = c.Offset };
        if (!FormText.IsUpper(c.Name))
        {
            losses.Add(FormLossKind.NameCase, path);
        }

        bool seenComponent = false;
        foreach (ComponentItem item in c.Items)
        {
            if (item is ContentProperty p)
            {
                if (p.Kind != ContentLineKind.Property)
                {
                    // Blank and malformed lines have no place in the forms.
                    losses.Add(FormLossKind.LineNotCarried, path);
                    continue;
                }

                if (seenComponent)
                {
                    losses.Add(FormLossKind.ItemOrder, path);
                }

                fc.Props.Add(ToForm(p, d, path, losses));
            }
            else if (item is Component child)
            {
                seenComponent = true;
                fc.Comps.Add(ToForm(child, d, path + "/" + FormText.Lower(child.Name), losses));
            }
        }

        if (d.IsVCard())
        {
            // RFC 7095 §3.3.1.1: VERSION is the first property.
            int v = fc.Props.FindIndex(static p => p.Name == "version");
            if (v > 0)
            {
                FProperty vp = fc.Props[v];
                fc.Props.RemoveAt(v);
                fc.Props.Insert(0, vp);
                losses.Add(FormLossKind.ItemOrder, path);
            }
        }

        return fc;
    }

    internal static FProperty ToForm(ContentProperty p, Dialect d, string path, LossSink losses)
    {
        string lname = FormText.Lower(p.Name);
        string ppath = path + "/" + lname;
        FProperty f = new() { Name = lname, Group = p.Group, Offset = p.Offset };
        if (!FormText.IsUpper(p.Name))
        {
            losses.Add(FormLossKind.NameCase, ppath);
        }

        if (IsFolded(p))
        {
            losses.Add(FormLossKind.Folding, string.Empty);
        }

        string? vparam = null;
        string? enc = null;
        string? encOriginal = null;
        foreach (ContentParameter cp in p.Parameters)
        {
            string pn = FormText.Lower(cp.Name);
            if (!FormText.IsUpper(cp.Name))
            {
                losses.Add(FormLossKind.NameCase, ppath);
            }

            if (cp.IsBare)
            {
                string tok = FormText.Upper(cp.Name);
                if (tok is "QUOTED-PRINTABLE" or "BASE64" or "B" or "8BIT" or "7BIT")
                {
                    enc = tok;
                    encOriginal = cp.Name;
                }
                else if (d.IsVCard())
                {
                    // vCard 2.1 allows a bare parameter name as a TYPE value.
                    losses.Add(FormLossKind.BareParameterAsType, ppath);
                    AddParam(f, "type", [cp.Name], d, ppath, losses);
                }
                else
                {
                    AddParam(f, pn, [], d, ppath, losses);
                }

                continue;
            }

            switch (pn)
            {
                case "value":
                    vparam = FormText.Lower(cp.Value ?? string.Empty);
                    continue;
                case "encoding":
                    enc = FormText.Upper(cp.Value ?? string.Empty);
                    encOriginal = cp.Value;
                    continue;
                case "charset":
                    losses.Add(FormLossKind.CharsetRemoved, ppath);
                    continue;
                default:
                    AddParam(f, pn, cp.Values, d, ppath, losses);
                    break;
            }
        }

        PropInfo info = FormSchema.Lookup(d, lname);
        string raw = p.GetDecodedValue();
        if (p.Encoding == "QUOTED-PRINTABLE")
        {
            losses.Add(FormLossKind.TransferEncodingRemoved, ppath);
        }

        string type = vparam ?? info.Type;
        Shape shape = info.Shape;
        if (vparam is not null)
        {
            if (vparam == info.Type)
            {
                losses.Add(FormLossKind.RedundantValueParameter, ppath);
            }
            else if (shape != Shape.List)
            {
                shape = Shape.Single;
            }
        }
        else if (d.IsVCard() && lname == "tz" && DateForms.IsVCardOffset(raw.Trim()))
        {
            // RFC 7095 Appendix B.1 shows TZ:-0500 as a utc-offset.
            type = "utc-offset";
        }

        bool b64 = enc is "BASE64" or "B";
        if (b64)
        {
            if (d == Dialect.ICal && type == "binary")
            {
                f.Params.Add(MakeParam("encoding", [encOriginal ?? "BASE64"]));
            }
            else if (d == Dialect.ICal && vparam is null && type == "uri")
            {
                // Producers write ENCODING=BASE64 without VALUE=BINARY on ATTACH; RFC 7265 §3.1 would decode it as text.
                // The data is binary, so it is carried as binary.
                type = "binary";
                shape = Shape.Single;
                losses.Add(FormLossKind.AmbiguousValueType, ppath);
                f.Params.Add(MakeParam("encoding", [encOriginal ?? "BASE64"]));
            }
            else if (d == Dialect.ICal && type == "unknown")
            {
                // An extension property with base64 data: binary, the type RFC 5545 §3.3.1 has for it.
                type = "binary";
                shape = Shape.Single;
                losses.Add(FormLossKind.AmbiguousValueType, ppath);
                f.Params.Add(MakeParam("encoding", [encOriginal ?? "BASE64"]));
            }
            else if (d.IsVCard() && (FormSchema.IsInlineBinaryProperty(lname) || type == "unknown"))
            {
                // jCard and xCard have no binary type (RFC 7095 §3.5): inline data becomes a data: URI (RFC 6350 §6.2.4).
                string media = MediaTypeFor(lname, f.Find("type"));
                raw = "data:" + media + ";base64," + StripWhitespace(raw);
                type = "uri";
                shape = Shape.Single;
                losses.Add(FormLossKind.InlineBinaryAsDataUri, ppath);
            }
            else if (ContentValues.TryDecodeBase64(raw, out byte[] bytes))
            {
                raw = Encoding.UTF8.GetString(bytes);
                losses.Add(FormLossKind.TransferEncodingRemoved, ppath);
            }
        }

        FormValue[]? vals = ConvertValue(raw, type, shape, lname, d, losses, ppath);
        if (vals is null && vparam is null && d == Dialect.ICal && type == "date-time")
        {
            // Many producers write DTSTART:20081006 without VALUE=DATE, and RFC 7265 Appendix B.1 shows it as a date.
            vals = ConvertValue(raw, "date", shape, lname, d, losses, ppath);
            if (vals is not null)
            {
                type = "date";
                losses.Add(FormLossKind.AmbiguousValueType, ppath);
            }
        }

        if (vals is null)
        {
            if (type != "unknown")
            {
                losses.Add(FormLossKind.ValueNotRepresentable, ppath);
            }

            type = "unknown";
            vals = [FormValue.Str(raw)];
        }

        f.Type = type;
        f.Values.AddRange(vals);
        return f;
    }

    private static bool IsFolded(ContentProperty p)
    {
        ReadOnlySpan<byte> raw = p.Raw.Span;
        int end = raw.Length;
        while (end > 0 && raw[end - 1] is (byte)'\r' or (byte)'\n')
        {
            end--;
        }

        return raw[..end].IndexOfAny((byte)'\r', (byte)'\n') >= 0;
    }

    private static string StripWhitespace(string s)
    {
        if (s.AsSpan().IndexOfAny(" \t\r\n") < 0)
        {
            return s;
        }

        StringBuilder sb = new(s.Length);
        foreach (char c in s)
        {
            if (c is not (' ' or '\t' or '\r' or '\n'))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static string MediaTypeFor(string lname, FParam? type)
    {
        string? t = null;
        if (type is not null)
        {
            foreach (string v in type.Values)
            {
                if (!v.Equals("pref", StringComparison.OrdinalIgnoreCase))
                {
                    t = v;
                    break;
                }
            }
        }

        if (t is null)
        {
            return string.Empty;
        }

        if (t.Contains('/', StringComparison.Ordinal))
        {
            return t.ToLowerInvariant();
        }

        return (lname == "sound" ? "audio/" : lname == "key" ? "application/" : "image/") + t.ToLowerInvariant();
    }

    private static FParam MakeParam(string name, IEnumerable<string> values)
    {
        FParam p = new(name);
        p.Values.AddRange(values);
        return p;
    }

    // Adds a parameter, merging a repeat of the same name (TYPE=work;TYPE=voice) into one list, and splitting the comma
    // lists of vCard TYPE, PID and SORT-AS (RFC 7095 §3.4.2).
    private static void AddParam(FProperty f, string name, IReadOnlyList<string> values, Dialect d, string ppath, LossSink losses)
    {
        FParam? existing = f.Find(name);
        if (existing is null)
        {
            existing = new FParam(name);
            f.Params.Add(existing);
        }
        else if (values.Count > 0)
        {
            losses.Add(FormLossKind.DuplicateParameterMerged, ppath);
        }

        bool list = FormSchema.IsListParam(d, name);
        foreach (string v in values)
        {
            if (list && v.Contains(',', StringComparison.Ordinal))
            {
                foreach (string part in v.Split(','))
                {
                    if (part.Length > 0)
                    {
                        existing.Values.Add(part);
                    }
                }
            }
            else
            {
                existing.Values.Add(d.IsVCard() && name == "label" ? LabelUnescape(v) : v);
            }
        }
    }

    // RFC 7095 §3.3.1.3 shows ADR;LABEL="…\n…" with a backslash-n as a line break in the JSON value.
    internal static string LabelUnescape(string v)
    {
        if (!v.Contains('\\', StringComparison.Ordinal))
        {
            return v;
        }

        StringBuilder sb = new(v.Length);
        for (int i = 0; i < v.Length; i++)
        {
            if (v[i] == '\\' && i + 1 < v.Length)
            {
                char n = v[i + 1];
                if (n is 'n' or 'N')
                {
                    sb.Append('\n');
                    i++;
                    continue;
                }

                if (n is '\\' or ',' or ';')
                {
                    sb.Append(n);
                    i++;
                    continue;
                }
            }

            sb.Append(v[i]);
        }

        return sb.ToString();
    }

    internal static string LabelEscape(string v) => v.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\r\n", "\\n", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\n", StringComparison.Ordinal);

    private static FormValue[]? ConvertValue(string raw, string type, Shape shape, string lname, Dialect d, LossSink losses, string ppath)
    {
        if (type == "unknown" || !FormSchema.IsKnownType(type))
        {
            return [FormValue.Str(raw)];
        }

        TextEscapeStyle style = d.Style();
        switch (shape)
        {
            case Shape.List:
            {
                List<string> items = type == "text" ? ContentValues.SplitUnescaped(raw, ',') : [.. raw.Split(',')];
                FormValue[] vals = new FormValue[items.Count];
                for (int i = 0; i < vals.Length; i++)
                {
                    FormValue? v = Scalar(items[i], type, d);
                    if (v is null)
                    {
                        return null;
                    }

                    vals[i] = v;
                }

                return vals;
            }

            case Shape.Struct when type == "text":
            {
                List<string> comps = ContentValues.SplitUnescaped(raw, ';');
                List<FormValue> items = new(comps.Count);
                foreach (string comp in comps)
                {
                    if (!FormSchema.CommaListsInStruct(d))
                    {
                        items.Add(FormValue.Str(ContentValues.Unescape(comp, style)));
                        continue;
                    }

                    List<string> sub = ContentValues.SplitUnescaped(comp, ',');
                    if (sub.Count == 1)
                    {
                        items.Add(FormValue.Str(ContentValues.Unescape(sub[0], style)));
                    }
                    else
                    {
                        List<FormValue> subItems = new(sub.Count);
                        foreach (string s in sub)
                        {
                            subItems.Add(FormValue.Str(ContentValues.Unescape(s, style)));
                        }

                        items.Add(FormValue.Arr(subItems));
                    }
                }

                // RFC 7095 §3.3.1.3: every component is present, empty ones as "".
                int want = FormSchema.StructCount(lname);
                if (items.Count < want)
                {
                    losses.Add(FormLossKind.StructuredPadded, ppath);
                    while (items.Count < want)
                    {
                        items.Add(FormValue.Str(string.Empty));
                    }
                }

                return [FormValue.Arr(items)];
            }

            case Shape.SemiList when type == "text":
            {
                List<string> comps = ContentValues.SplitUnescaped(raw, ';');
                if (comps.Count == 1)
                {
                    return [FormValue.Str(ContentValues.Unescape(comps[0], style))];
                }

                List<FormValue> items = new(comps.Count);
                foreach (string comp in comps)
                {
                    items.Add(FormValue.Str(ContentValues.Unescape(comp, style)));
                }

                return [FormValue.Arr(items)];
            }

            case Shape.Pair when type == "float":
            {
                string[] parts = raw.Split(';');
                if (parts.Length != 2)
                {
                    return null;
                }

                string? a = FormText.NormalizeNumber(parts[0], false);
                string? b = FormText.NormalizeNumber(parts[1], false);
                return a is null || b is null ? null : [FormValue.Arr([FormValue.Num(a), FormValue.Num(b)])];
            }

            case Shape.RequestStatus when type == "text":
            {
                List<string> comps = ContentValues.SplitUnescaped(raw, ';');
                if (comps.Count is < 2 or > 3)
                {
                    return null;
                }

                List<FormValue> items = new(comps.Count);
                foreach (string comp in comps)
                {
                    items.Add(FormValue.Str(ContentValues.Unescape(comp, style)));
                }

                return [FormValue.Arr(items)];
            }

            default:
            {
                FormValue? v = Scalar(raw, type, d);
                return v is null ? null : [v];
            }
        }
    }

    // One value of a known type, in the form jCal/jCard give it, or null when the text is not a value of that type.
    private static FormValue? Scalar(string raw, string type, Dialect d)
    {
        switch (type)
        {
            case "text":
                return FormValue.Str(ContentValues.Unescape(raw, d.Style()));
            case "uri" or "cal-address" or "language-tag" or "unknown":
                return FormValue.Str(raw);
            case "binary":
                return FormValue.Str(StripWhitespace(raw));
            case "boolean":
                return raw.Trim().ToUpperInvariant() switch
                {
                    "TRUE" => FormValue.Boolean(true),
                    "FALSE" => FormValue.Boolean(false),
                    _ => null,
                };
            case "integer" or "float":
            {
                string? n = FormText.NormalizeNumber(raw, type == "integer");
                return n is null ? null : FormValue.Num(n);
            }

            case "duration":
                return DateForms.IsDuration(raw) ? FormValue.Str(raw) : null;
            case "utc-offset":
            {
                string? o = d == Dialect.ICal ? DateForms.IcsOffsetToExtended(raw) : DateForms.VCardOffsetToExtended(raw);
                return o is null ? null : FormValue.Str(o);
            }

            case "period":
            {
                string[] parts = raw.Split('/');
                if (parts.Length != 2)
                {
                    return null;
                }

                string? a = DateForms.IcsDateTimeToExtended(parts[0]);
                string? b = DateForms.IsDuration(parts[1]) ? parts[1] : DateForms.IcsDateTimeToExtended(parts[1]);
                return a is null || b is null ? null : FormValue.Arr([FormValue.Str(a), FormValue.Str(b)]);
            }

            case "recur":
                return RecurToForm(raw);
            case "date" or "time" or "date-time" or "date-and-or-time" or "timestamp":
                return DateScalar(raw, type, d);
            default:
                return FormValue.Str(raw);
        }
    }

    private static FormValue? DateScalar(string raw, string type, Dialect d)
    {
        if (d == Dialect.ICal)
        {
            string? s = type switch
            {
                "date" => DateForms.IcsDateToExtended(raw),
                "time" => DateForms.IcsTimeToExtended(raw),
                "date-time" => DateForms.IcsDateTimeToExtended(raw),
                _ => null,
            };
            return s is null ? null : FormValue.Str(s);
        }

        DateForms.VCardDt? dt = type == "time" && !raw.StartsWith('T') ? DateForms.VCardDt.ParseTimeOnly(raw) : DateForms.VCardDt.Parse(raw);
        if (dt is null)
        {
            return null;
        }

        bool ok = type switch
        {
            "date" => dt.HasDate && !dt.HasTime && !dt.HasT && dt.Zone is null,
            "time" => !dt.HasDate && dt.HasTime,
            "date-time" or "timestamp" => dt.HasDate && dt.HasTime,
            _ => true,
        };
        return ok ? FormValue.Str(dt.ToExtended()) : null;
    }

    // ---- recurrence rules: RFC 7265 §3.6.10 ----

    private static readonly string[] NumericParts = ["bysecond", "byminute", "byhour", "bymonthday", "byyearday", "byweekno", "bymonth", "bysetpos"];

    private static FormValue? RecurToForm(string raw)
    {
        FormValue obj = FormValue.Obj();
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string part in raw.Split(';'))
        {
            if (part.Length == 0)
            {
                continue;
            }

            int eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0 || eq == part.Length - 1)
            {
                return null;
            }

            string key = part[..eq].ToLowerInvariant();
            string val = part[(eq + 1)..];
            if (!seen.Add(key))
            {
                return null;
            }

            FormValue v;
            if (key == "until")
            {
                string? u = DateForms.IcsDateTimeToExtended(val) ?? DateForms.IcsDateToExtended(val);
                if (u is null)
                {
                    return null;
                }

                v = FormValue.Str(u);
            }
            else if (key is "count" or "interval")
            {
                string? n = FormText.NormalizeNumber(val, true);
                if (n is null)
                {
                    return null;
                }

                v = FormValue.Num(n);
            }
            else if (Array.IndexOf(NumericParts, key) >= 0)
            {
                string[] items = val.Split(',');
                List<FormValue> list = new(items.Length);
                foreach (string it in items)
                {
                    string? n = FormText.NormalizeNumber(it, true);
                    list.Add(n is null ? FormValue.Str(it) : FormValue.Num(n));
                }

                v = list.Count == 1 ? list[0] : FormValue.Arr(list);
            }
            else if (key == "byday")
            {
                string[] items = val.Split(',');
                v = items.Length == 1 ? FormValue.Str(items[0]) : FormValue.Arr([.. items.Select(static i => FormValue.Str(i))]);
            }
            else
            {
                v = FormValue.Str(val);
            }

            obj.Members.Add(new KeyValuePair<string, FormValue>(key, v));
        }

        return obj.Members.Count == 0 ? null : obj;
    }

    private static string RecurToText(FormValue obj)
    {
        StringBuilder sb = new();
        foreach (KeyValuePair<string, FormValue> m in obj.Members)
        {
            if (sb.Length > 0)
            {
                sb.Append(';');
            }

            sb.Append(m.Key.ToUpperInvariant()).Append('=');
            if (m.Value.Kind == FKind.Array)
            {
                sb.Append(string.Join(',', m.Value.Items.Select(static i => i.ScalarText)));
            }
            else if (m.Key == "until")
            {
                sb.Append(DateForms.ExtendedToBasic(m.Value.ScalarText));
            }
            else
            {
                sb.Append(m.Value.Kind == FKind.Number ? FormText.NumberToText(m.Value.Text, true) : m.Value.ScalarText);
            }
        }

        return sb.ToString();
    }

    // ---- model -> text ----

    internal static Component FromForm(FComponent fc, Dialect d, string path, DefectSink defects)
    {
        Component c = new(FormText.Upper(fc.Name));
        foreach (FProperty p in fc.Props)
        {
            c.Add(FromForm(p, d, path, defects));
        }

        foreach (FComponent child in fc.Comps)
        {
            c.Add(FromForm(child, d, path + "/" + child.Name, defects));
        }

        return c;
    }

    internal static Dialect DialectOf(FComponent fc)
    {
        if (fc.Name != "vcard")
        {
            return Dialect.ICal;
        }

        foreach (FProperty p in fc.Props)
        {
            if (p.Name == "version" && p.Values.Count > 0 && p.Values[0].IsScalar)
            {
                return p.Values[0].ScalarText.Trim() switch
                {
                    "2.1" => Dialect.VCard21,
                    "3.0" => Dialect.VCard30,
                    _ => Dialect.VCard40,
                };
            }
        }

        return Dialect.VCard40;
    }

    internal static ContentProperty FromForm(FProperty f, Dialect d, string path, DefectSink defects)
    {
        string ppath = path + "/" + f.Name;
        PropInfo info = FormSchema.Lookup(d, f.Name);
        string type = f.Type;
        Shape shape = info.Shape == Shape.List || type == info.Type ? info.Shape : Shape.Single;
        string? group = f.Group;
        List<ContentParameter> ps = [];
        string? valueParam = null;
        string text;
        bool dataUriToEncoding = false;

        // Inline binary of vCard 2.1/3.0 goes back to ENCODING=b/BASE64.
        if (d is Dialect.VCard21 or Dialect.VCard30 && (FormSchema.IsInlineBinaryProperty(f.Name) || f.Name.StartsWith("x-", StringComparison.Ordinal)) && type == "uri" && f.Values.Count == 1 && f.Values[0].Kind == FKind.String
            && TrySplitBase64DataUri(f.Values[0].Text, out string payload))
        {
            text = payload;
            dataUriToEncoding = true;
        }
        else
        {
            text = ValuesToText(f, d, type, shape, ppath, defects);
        }

        if (type == "unknown" || dataUriToEncoding)
        {
            // RFC 7265 §5.2: no VALUE parameter for unknown.
        }
        else if (type != info.Type && !(d.IsVCard() && f.Name == "tz" && type == "utc-offset") && !(info.Shape == Shape.List && type == info.Type))
        {
            valueParam = d == Dialect.ICal ? FormText.Upper(type) : type;
            ps.Add(new ContentParameter("VALUE", valueParam));
        }

        if (dataUriToEncoding)
        {
            ps.Add(new ContentParameter("ENCODING", d == Dialect.VCard21 ? "BASE64" : "b"));
        }
        else if (d == Dialect.ICal && type == "binary" && f.Find("encoding") is null)
        {
            ps.Add(new ContentParameter("ENCODING", "BASE64"));
        }

        foreach (FParam fp in f.Params)
        {
            if (fp.Name == "group" && fp.Values.Count == 1)
            {
                group = fp.Values[0];
                continue;
            }

            if (fp.Name == "value")
            {
                continue;
            }

            IEnumerable<string> vals = d.IsVCard() && fp.Name == "label" ? fp.Values.Select(LabelEscape) : fp.Values;
            ps.Add(new ContentParameter(FormText.Upper(fp.Name), vals));
        }

        if (d == Dialect.VCard21 && text.AsSpan().IndexOfAny('\r', '\n') >= 0)
        {
            // vCard 2.1 carries a line break only through quoted-printable.
            text = ContentValues.EncodeQuotedPrintable(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal)));
            ps.Add(new ContentParameter("ENCODING", "QUOTED-PRINTABLE"));
            ps.Add(new ContentParameter("CHARSET", "UTF-8"));
        }
        else if (d == Dialect.VCard21 && !Ascii.IsValid(Encoding.UTF8.GetBytes(text)))
        {
            ps.Add(new ContentParameter("CHARSET", "UTF-8"));
        }

        if (text.AsSpan().IndexOfAny('\r', '\n') >= 0)
        {
            defects.Add(FormDefectKind.LineBreakEscaped, f.Offset, ppath);
            text = text.Replace("\r\n", "\\n", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\n", StringComparison.Ordinal);
        }

        if (group is not null && (group.Length == 0 || group.AsSpan().IndexOfAny(";:\r\n") >= 0))
        {
            group = null;
        }

        ContentProperty prop = new(SafeName(f.Name), text, group);
        foreach (ContentParameter p in ps)
        {
            prop.Parameters.Add(p);
        }

        return prop;
    }

    private static string SafeName(string name)
    {
        string n = FormText.Upper(name);
        if (n.Length == 0 || n.AsSpan().IndexOfAny(";:\r\n") >= 0)
        {
            n = n.Length == 0 ? "X-EMPTY" : n.Replace(';', '-').Replace(':', '-').Replace('\r', '-').Replace('\n', '-');
        }

        return n;
    }

    private static bool TrySplitBase64DataUri(string s, out string payload)
    {
        payload = string.Empty;
        if (!s.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int comma = s.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0 || !s.AsSpan(5, comma - 5).EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        payload = s[(comma + 1)..];
        return true;
    }

    private static string ValuesToText(FProperty f, Dialect d, string type, Shape shape, string ppath, DefectSink defects)
    {
        FormValue? first = f.Values.Count > 0 ? f.Values[0] : null;
        switch (shape)
        {
            case Shape.Struct or Shape.SemiList when type == "text":
            {
                List<FormValue> comps = first is { Kind: FKind.Array } ? first.Items : first is null ? [] : [first];
                StringBuilder sb = new();
                for (int i = 0; i < comps.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(';');
                    }

                    FormValue c = comps[i];
                    if (c.Kind == FKind.Array)
                    {
                        for (int j = 0; j < c.Items.Count; j++)
                        {
                            sb.Append(j > 0 ? "," : string.Empty).Append(EscapeText(c.Items[j].ScalarText, d));
                        }
                    }
                    else
                    {
                        sb.Append(EscapeText(c.ScalarText, d));
                    }
                }

                return sb.ToString();
            }

            case Shape.Pair when type == "float":
                if (first is { Kind: FKind.Array } pair)
                {
                    return string.Join(';', pair.Items.Select(static i => FormText.NumberToText(i.ScalarText, false)));
                }

                break;
            case Shape.RequestStatus when type == "text":
                if (first is { Kind: FKind.Array } rs)
                {
                    return string.Join(';', rs.Items.Select(i => EscapeText(i.ScalarText, d)));
                }

                break;
            default:
                break;
        }

        StringBuilder o = new();
        for (int i = 0; i < f.Values.Count; i++)
        {
            if (i > 0)
            {
                o.Append(',');
            }

            o.Append(ScalarToText(f.Values[i], type, d, ppath, defects, f.Offset));
        }

        return o.ToString();
    }

    private static string ScalarToText(FormValue v, string type, Dialect d, string ppath, DefectSink defects, long offset)
    {
        if (type == "period" && v is { Kind: FKind.Array, Items.Count: 2 })
        {
            string a = DateForms.ExtendedToBasic(v.Items[0].ScalarText);
            string b = v.Items[1].ScalarText;
            return a + "/" + (DateForms.IsDuration(b) ? b : DateForms.ExtendedToBasic(b));
        }

        if (type == "recur" && v.Kind == FKind.Object)
        {
            return RecurToText(v);
        }

        if (!v.IsScalar)
        {
            // An array or object where the type calls for a single value: the items are joined as a list.
            defects.Add(FormDefectKind.ValueShape, offset, ppath);
            return v.Kind == FKind.Array ? string.Join(',', v.Items.Select(i => ScalarToText(i, type, d, ppath, defects, offset))) : string.Empty;
        }

        string s = v.ScalarText;
        switch (type)
        {
            case "text":
                return EscapeText(s, d);
            case "boolean":
                return s.Equals("true", StringComparison.OrdinalIgnoreCase) ? "TRUE" : s.Equals("false", StringComparison.OrdinalIgnoreCase) ? "FALSE" : s;
            case "integer" or "float":
                return v.Kind == FKind.Number ? FormText.NumberToText(s, type == "integer") : s;
            case "date" or "time" or "date-time" or "date-and-or-time" or "timestamp":
                if (d == Dialect.ICal)
                {
                    return DateForms.ExtendedToBasic(s);
                }

                {
                    DateForms.VCardDt? dt = DateForms.VCardDt.Parse(s) ?? DateForms.VCardDt.ParseTimeOnly(s);
                    return dt is null ? s : d == Dialect.VCard30 ? dt.ToExtended() : dt.ToBasic();
                }

            case "utc-offset":
                return d == Dialect.VCard30 ? s : DateForms.OffsetToBasic(s);
            default:
                return s;
        }
    }

    // ---- version 3.0 / 2.1 -> 4.0 forms, for xCard and for VCardView.ToVersion4 ----

    internal static void UpgradeToV4(FProperty p, Dialect from, string path, LossSink losses)
    {
        if (from == Dialect.VCard40)
        {
            return;
        }

        if (p.Name == "geo" && p.Type == "float" && p.Values.Count == 1 && p.Values[0] is { Kind: FKind.Array, Items.Count: 2 } g)
        {
            // RFC 6350 §6.5.2 and RFC 5870: GEO is a geo URI in version 4.0.
            p.Type = "uri";
            string uri = "geo:" + g.Items[0].Text + "," + g.Items[1].Text;
            p.Values.Clear();
            p.Values.Add(FormValue.Str(uri));
            losses.Add(FormLossKind.VersionSpecificForm, path + "/" + p.Name);
        }

        FParam? t = p.Find("type");
        if (t is not null)
        {
            // RFC 6350 §5.3: PREF is a parameter; "pref" as a TYPE value is version 3.0 usage.
            int i = t.Values.FindIndex(static v => v.Equals("pref", StringComparison.OrdinalIgnoreCase));
            if (i >= 0)
            {
                t.Values.RemoveAt(i);
                if (t.Values.Count == 0)
                {
                    p.Params.Remove(t);
                }

                if (p.Find("pref") is null)
                {
                    FParam pref = new("pref");
                    pref.Values.Add("1");
                    p.Params.Add(pref);
                }

                losses.Add(FormLossKind.VersionSpecificForm, path + "/" + p.Name);
            }
        }
    }

    // Text escaping for a value. vCard 2.1 has no \n escape (a line break needs quoted-printable, applied to the whole
    // value afterwards), so only backslash and semicolon are escaped there.
    private static string EscapeText(string text, Dialect d)
    {
        if (d != Dialect.VCard21)
        {
            return ContentValues.Escape(text, TextEscapeStyle.Rfc5545);
        }

        return text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace(";", "\\;", StringComparison.Ordinal);
    }

    internal static string InvariantInt(int n) => n.ToString(CultureInfo.InvariantCulture);
}
