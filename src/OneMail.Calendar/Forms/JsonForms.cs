using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using RecursiveAI.OneMail.Calendar.ContentLines;

namespace RecursiveAI.OneMail.Calendar.Forms;

/// <summary>Options for writing jCal and jCard.</summary>
public sealed record JsonFormsOptions
{
    /// <summary>The defaults: compact output, a single object when there is one top-level component.</summary>
    public static JsonFormsOptions Default { get; } = new();

    /// <summary>Write indented JSON. Default false.</summary>
    public bool Indented { get; init; }

    /// <summary>Write an array even when there is only one top-level component. Default false: one component is written as its own array, as in the RFC examples.</summary>
    public bool AlwaysArray { get; init; }
}

/// <summary>
/// jCal (RFC 7265) and jCard (RFC 7095): iCalendar and vCard as JSON. Writing maps a <see cref="ContentDocument"/> to JSON and
/// says what the JSON could not carry; reading maps JSON back to a document. Both use <see cref="Utf8JsonReader"/> and
/// <see cref="Utf8JsonWriter"/> only, with no reflection-based serialisation.
/// </summary>
public static class JsonForms
{
    private static readonly JavaScriptEncoder Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    /// <summary>
    /// Writes the top-level VCALENDAR and VCARD components of a document as jCal and jCard. Other top-level components are
    /// written as jCal-shaped components too. Names are lower case, <c>VALUE</c> becomes the type, vCard groups become the
    /// <c>group</c> parameter, and values of known properties take the JSON shapes of the RFCs; values of unknown properties
    /// are carried as <c>unknown</c> text.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="options">Output options, or null for the defaults.</param>
    /// <returns>The JSON and the losses: everything the text had that JSON does not keep (folding, name case, blank lines, transfer encodings…). Never throws on content.</returns>
    public static FormWriteResult ToJson(ContentDocument document, JsonFormsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= JsonFormsOptions.Default;
        LossSink losses = new();
        List<(FComponent Comp, Dialect Dialect)> comps = [];
        foreach (ComponentItem item in document.Root.Items)
        {
            if (item is Component c)
            {
                Dialect d = FormCodec.DialectOf(c);
                comps.Add((FormCodec.ToForm(c, d, FormText.Lower(c.Name), losses), d));
            }
            else if (item is ContentProperty)
            {
                losses.Add(FormLossKind.LineNotCarried, string.Empty);
            }
        }

        using MemoryStream ms = new();
        using (Utf8JsonWriter w = new(ms, new JsonWriterOptions { Indented = options.Indented, Encoder = Encoder, SkipValidation = false }))
        {
            bool array = options.AlwaysArray || comps.Count != 1;
            if (array)
            {
                w.WriteStartArray();
            }

            foreach ((FComponent comp, Dialect d) in comps)
            {
                WriteComponent(w, comp, d);
            }

            if (array)
            {
                w.WriteEndArray();
            }
        }

        return new FormWriteResult(ms.ToArray(), losses.ToList());
    }

    private static void WriteComponent(Utf8JsonWriter w, FComponent c, Dialect d)
    {
        w.WriteStartArray();
        w.WriteStringValue(S(c.Name));
        w.WriteStartArray();
        foreach (FProperty p in c.Props)
        {
            WriteProperty(w, p);
        }

        w.WriteEndArray();

        // RFC 7095 §3.2: a jCard object has two elements; RFC 7265 §3.3: a jCal component has three.
        if (d == Dialect.ICal || c.Comps.Count > 0)
        {
            w.WriteStartArray();
            foreach (FComponent child in c.Comps)
            {
                WriteComponent(w, child, d);
            }

            w.WriteEndArray();
        }

        w.WriteEndArray();
    }

    private static void WriteProperty(Utf8JsonWriter w, FProperty p)
    {
        w.WriteStartArray();
        w.WriteStringValue(S(p.Name));
        w.WriteStartObject();
        if (p.Group is not null)
        {
            // RFC 7095 §3.3.1.2: the group becomes the "group" parameter.
            w.WriteString("group", S(p.Group));
        }

        foreach (FParam pa in p.Params)
        {
            if (pa.Name == "group" && p.Group is not null)
            {
                continue;
            }

            w.WritePropertyName(S(pa.Name));
            if (pa.Values.Count == 1)
            {
                w.WriteStringValue(S(pa.Values[0]));
            }
            else
            {
                w.WriteStartArray();
                foreach (string v in pa.Values)
                {
                    w.WriteStringValue(S(v));
                }

                w.WriteEndArray();
            }
        }

        w.WriteEndObject();
        w.WriteStringValue(S(p.Type));
        foreach (FormValue v in p.Values)
        {
            WriteValue(w, v);
        }

        w.WriteEndArray();
    }

    private static void WriteValue(Utf8JsonWriter w, FormValue v)
    {
        switch (v.Kind)
        {
            case FKind.String:
                w.WriteStringValue(S(v.Text));
                break;
            case FKind.Number:
                w.WriteRawValue(v.Text);
                break;
            case FKind.Bool:
                w.WriteBooleanValue(v.Bool);
                break;
            case FKind.Array:
                w.WriteStartArray();
                foreach (FormValue i in v.Items)
                {
                    WriteValue(w, i);
                }

                w.WriteEndArray();
                break;
            default:
                w.WriteStartObject();
                foreach (KeyValuePair<string, FormValue> m in v.Members)
                {
                    w.WritePropertyName(S(m.Key));
                    WriteValue(w, m.Value);
                }

                w.WriteEndObject();
                break;
        }
    }

    // Lone surrogates cannot be written as UTF-8.
    private static string S(string s) => FormText.Sanitize(s, out _);

    /// <summary>
    /// Reads jCal or jCard: one component array, or an array of them. Properties, parameters and values map back to content
    /// lines (names upper case, a <c>VALUE</c> parameter where the type is not the property's default, <c>group</c> back to a
    /// group prefix). Comments and trailing commas are accepted.
    /// </summary>
    /// <param name="json">UTF-8 JSON.</param>
    /// <param name="limits">Resource limits, or null for <see cref="FormLimits.Default"/>.</param>
    /// <returns>The document and the defects. Never throws on content; input that is not JSON, or exceeds a limit, gives an empty document and a defect.</returns>
    public static FormReadResult FromJson(ReadOnlyMemory<byte> json, FormLimits? limits = null)
    {
        limits ??= FormLimits.Default;
        DefectSink defects = new();
        ContentDocument doc = new();
        if (json.Length > limits.MaxInputBytes)
        {
            defects.Add(FormDefectKind.InputTooLarge, 0, string.Empty);
            return new FormReadResult(doc, defects.Items);
        }

        FormValue? root = Parse(json.Span, limits, defects);
        if (root is not null)
        {
            List<FComponent> comps = MapRoot(root, defects);
            foreach (FComponent fc in comps)
            {
                doc.Root.Add(FormCodec.FromForm(fc, FormCodec.DialectOf(fc), fc.Name, defects));
            }
        }

        return new FormReadResult(doc, defects.Items);
    }

    /// <summary>Reads jCal or jCard from a string.</summary>
    /// <param name="json">The JSON text.</param>
    /// <param name="limits">Resource limits, or null for the defaults.</param>
    /// <returns>See <see cref="FromJson(ReadOnlyMemory{byte}, FormLimits?)"/>.</returns>
    public static FormReadResult FromJson(string json, FormLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        return FromJson(Encoding.UTF8.GetBytes(json), limits);
    }

    private sealed class LimitException(FormDefectKind kind) : Exception
    {
        internal FormDefectKind Kind { get; } = kind;
    }

    private sealed class ParseState
    {
        internal int Nodes;
        internal FormLimits Limits = FormLimits.Default;
        internal DefectSink Defects = new();
    }

    private static FormValue? Parse(ReadOnlySpan<byte> json, FormLimits limits, DefectSink defects)
    {
        ParseState st = new() { Limits = limits, Defects = defects };
        Utf8JsonReader r = new(json, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            MaxDepth = Math.Clamp(limits.MaxDepth, 1, 1000),
        });
        try
        {
            if (!r.Read())
            {
                defects.Add(FormDefectKind.NotWellFormed, 0, string.Empty);
                return null;
            }

            return ReadValue(ref r, st);
        }
        catch (LimitException e)
        {
            defects.Add(e.Kind, r.TokenStartIndex, string.Empty);
            return null;
        }
        catch (JsonException) when (r.CurrentDepth >= Math.Clamp(limits.MaxDepth, 1, 1000))
        {
            defects.Add(FormDefectKind.DepthLimitExceeded, r.TokenStartIndex, string.Empty);
            return null;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException)
        {
            defects.Add(FormDefectKind.NotWellFormed, r.BytesConsumed, string.Empty);
            return null;
        }
    }

    private static FormValue ReadValue(ref Utf8JsonReader r, ParseState st)
    {
        if (++st.Nodes > st.Limits.MaxNodes)
        {
            throw new LimitException(FormDefectKind.NodeLimitExceeded);
        }

        long offset = r.TokenStartIndex;
        FormValue v;
        switch (r.TokenType)
        {
            case JsonTokenType.StartArray:
                v = FormValue.Arr();
                while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                {
                    v.Items.Add(ReadValue(ref r, st));
                }

                break;
            case JsonTokenType.StartObject:
                v = FormValue.Obj();
                while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                {
                    string key = ReadString(ref r, st);
                    r.Read();
                    FormValue member = ReadValue(ref r, st);
                    foreach (KeyValuePair<string, FormValue> m in v.Members)
                    {
                        if (m.Key == key)
                        {
                            st.Defects.Add(FormDefectKind.DuplicateKey, offset, key);
                            break;
                        }
                    }

                    v.Members.Add(new KeyValuePair<string, FormValue>(key, member));
                }

                break;
            case JsonTokenType.String:
                v = FormValue.Str(ReadString(ref r, st));
                break;
            case JsonTokenType.Number:
                v = FormValue.Num(Encoding.UTF8.GetString(r.ValueSpan));
                break;
            case JsonTokenType.True:
                v = FormValue.Boolean(true);
                break;
            case JsonTokenType.False:
                v = FormValue.Boolean(false);
                break;
            default:
                // null has no place in these forms; it is read as an empty string.
                st.Defects.Add(FormDefectKind.ValueShape, offset, string.Empty);
                v = FormValue.Str(string.Empty);
                break;
        }

        v.Offset = offset;
        return v;
    }

    private static string ReadString(ref Utf8JsonReader r, ParseState st)
    {
        // The unescaped string cannot be longer than the escaped one, so the byte length bounds the allocation.
        if (r.ValueSpan.Length > (long)st.Limits.MaxStringChars * 3)
        {
            st.Defects.Add(FormDefectKind.StringTooLong, r.TokenStartIndex, string.Empty);
            return string.Empty;
        }

        string s = r.GetString() ?? string.Empty;
        if (s.Length > st.Limits.MaxStringChars)
        {
            st.Defects.Add(FormDefectKind.StringTooLong, r.TokenStartIndex, string.Empty);
            return string.Empty;
        }

        return s;
    }

    private static List<FComponent> MapRoot(FormValue root, DefectSink defects)
    {
        List<FComponent> list = [];
        if (root.Kind != FKind.Array)
        {
            defects.Add(FormDefectKind.UnrecognizedRoot, root.Offset, string.Empty);
            return list;
        }

        if (root.Items.Count > 0 && root.Items[0].Kind == FKind.String)
        {
            FComponent? c = MapComponent(root, string.Empty, defects, 0);
            if (c is not null)
            {
                list.Add(c);
            }

            return list;
        }

        foreach (FormValue item in root.Items)
        {
            FComponent? c = item.Kind == FKind.Array ? MapComponent(item, string.Empty, defects, 0) : null;
            if (c is null)
            {
                defects.Add(FormDefectKind.UnrecognizedRoot, item.Offset, string.Empty);
            }
            else
            {
                list.Add(c);
            }
        }

        return list;
    }

    private static FComponent? MapComponent(FormValue arr, string parentPath, DefectSink defects, int depth)
    {
        if (arr.Kind != FKind.Array || arr.Items.Count < 2 || arr.Items[0].Kind != FKind.String || arr.Items[1].Kind != FKind.Array)
        {
            defects.Add(FormDefectKind.MalformedStructure, arr.Offset, parentPath);
            return null;
        }

        FComponent c = new() { Name = FormText.Lower(arr.Items[0].Text), Offset = arr.Offset };
        string path = parentPath.Length == 0 ? c.Name : parentPath + "/" + c.Name;
        foreach (FormValue p in arr.Items[1].Items)
        {
            FProperty? fp = MapProperty(p, path, defects);
            if (fp is not null)
            {
                c.Props.Add(fp);
            }
        }

        if (arr.Items.Count > 2)
        {
            if (arr.Items[2].Kind != FKind.Array)
            {
                defects.Add(FormDefectKind.MalformedStructure, arr.Items[2].Offset, path);
            }
            else
            {
                foreach (FormValue child in arr.Items[2].Items)
                {
                    FComponent? cc = MapComponent(child, path, defects, depth + 1);
                    if (cc is not null)
                    {
                        c.Comps.Add(cc);
                    }
                }
            }
        }

        return c;
    }

    private static FProperty? MapProperty(FormValue arr, string path, DefectSink defects)
    {
        if (arr.Kind != FKind.Array || arr.Items.Count < 3 || arr.Items[0].Kind != FKind.String || arr.Items[2].Kind != FKind.String)
        {
            defects.Add(FormDefectKind.MalformedStructure, arr.Offset, path);
            return null;
        }

        FProperty p = new() { Name = FormText.Lower(arr.Items[0].Text), Type = FormText.Lower(arr.Items[2].Text), Offset = arr.Offset };
        string ppath = path + "/" + p.Name;
        FormValue ps = arr.Items[1];
        if (ps.Kind != FKind.Object)
        {
            defects.Add(FormDefectKind.MalformedStructure, ps.Offset, ppath);
        }
        else
        {
            foreach (KeyValuePair<string, FormValue> m in ps.Members)
            {
                FParam fp = new(FormText.Lower(m.Key));
                if (m.Value.Kind == FKind.Array)
                {
                    foreach (FormValue v in m.Value.Items)
                    {
                        fp.Values.Add(ParamText(v, ppath, defects));
                    }
                }
                else
                {
                    fp.Values.Add(ParamText(m.Value, ppath, defects));
                }

                p.Params.Add(fp);
            }
        }

        for (int i = 3; i < arr.Items.Count; i++)
        {
            p.Values.Add(arr.Items[i]);
        }

        if (p.Values.Count == 0)
        {
            defects.Add(FormDefectKind.MalformedStructure, arr.Offset, ppath);
            p.Values.Add(FormValue.Str(string.Empty));
        }

        return p;
    }

    private static string ParamText(FormValue v, string ppath, DefectSink defects)
    {
        if (v.Kind == FKind.String)
        {
            return v.Text;
        }

        defects.Add(FormDefectKind.ParameterValueNotString, v.Offset, ppath);
        return v.IsScalar ? v.ScalarText : string.Empty;
    }
}
