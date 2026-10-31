using System.Globalization;
using System.Text;
using System.Xml;

namespace RecursiveAI.OneMail.Stores.Emlx;

/// <summary>
/// Reads and writes XML property lists (the trailer of an <c>.emlx</c> file and Apple Mail's <c>Info.plist</c>).
/// Values map to .NET types without reflection: <c>dict</c> to <see cref="Dictionary{TKey,TValue}"/> of string to
/// <see cref="object"/>, <c>array</c> to <see cref="List{T}"/> of object, <c>string</c> to <see cref="string"/>,
/// <c>integer</c> to <see cref="long"/>, <c>real</c> to <see cref="double"/>, <c>true</c>/<c>false</c> to <see cref="bool"/>,
/// <c>date</c> to <see cref="DateTimeOffset"/> and <c>data</c> to <c>byte[]</c>.
/// </summary>
/// <remarks>
/// Parsing is lenient and bounded: DTDs are ignored and never fetched, nesting is limited to 32 levels, a document to
/// 1 MiB and 100,000 values, and malformed input returns false instead of throwing. Binary property lists
/// (<c>bplist00</c>) are not read.
/// </remarks>
public static class Plist
{
    private const int MaxDepth = 32;
    private const int MaxValues = 100_000;
    private const int MaxBytes = 1 << 20;

    /// <summary>Parses an XML property list.</summary>
    /// <param name="xml">The document bytes (UTF-8 or as its declaration says).</param>
    /// <param name="root">The top-level value (usually a dictionary), or null on failure.</param>
    /// <returns>True if the document was a property list.</returns>
    public static bool TryParse(ReadOnlySpan<byte> xml, out object? root)
    {
        root = null;
        if (xml.Length == 0 || xml.Length > MaxBytes)
        {
            return false;
        }

        try
        {
            // The text is decoded here, as UTF-8, and read through a TextReader. That way the reader never switches encodings on
            // the strength of the XML declaration, a path where the framework's reader can throw on hostile bytes.
            string text = Encoding.UTF8.GetString(xml);
            if (text.Length > 0 && text[0] == '\uFEFF')
            {
                text = text[1..];
            }

            using StringReader sr = new(text);
            XmlReaderSettings settings = new()
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                IgnoreWhitespace = true,
                MaxCharactersInDocument = MaxBytes,
            };
            using XmlReader r = XmlReader.Create(sr, settings);
            if (!r.ReadToFollowing("plist"))
            {
                return false;
            }

            if (r.IsEmptyElement)
            {
                return false;
            }

            r.Read();
            int budget = MaxValues;
            root = ReadValue(r, 0, ref budget);
            return root is not null;
        }
        catch (XmlException)
        {
            root = null;
            return false;
        }
        catch (FormatException)
        {
            root = null;
            return false;
        }
        catch (ArgumentException)
        {
            // Malformed input must give false, never an exception; the framework's reader raises this for some hostile documents.
            root = null;
            return false;
        }
    }

    private static object? ReadValue(XmlReader r, int depth, ref int budget)
    {
        if (depth > MaxDepth || --budget < 0)
        {
            throw new FormatException("The property list is too deep or too large.");
        }

        if (r.NodeType != XmlNodeType.Element)
        {
            return null;
        }

        string name = r.LocalName;
        bool empty = r.IsEmptyElement;
        switch (name)
        {
            case "dict":
            {
                Dictionary<string, object?> d = new(StringComparer.Ordinal);
                if (empty)
                {
                    r.Read();
                    return d;
                }

                r.Read();
                while (r.NodeType == XmlNodeType.Element)
                {
                    if (r.LocalName != "key")
                    {
                        throw new FormatException("A dictionary entry has no key.");
                    }

                    string key = r.ReadElementContentAsString();
                    object? v = ReadValue(r, depth + 1, ref budget) ?? throw new FormatException("A dictionary key has no value.");
                    d[key] = v;
                }

                r.ReadEndElement();
                return d;
            }

            case "array":
            {
                List<object?> a = [];
                if (empty)
                {
                    r.Read();
                    return a;
                }

                r.Read();
                while (r.NodeType == XmlNodeType.Element)
                {
                    a.Add(ReadValue(r, depth + 1, ref budget));
                }

                r.ReadEndElement();
                return a;
            }

            case "string":
                return r.ReadElementContentAsString();
            case "integer":
                return long.Parse(r.ReadElementContentAsString().Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            case "real":
                return double.Parse(r.ReadElementContentAsString().Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
            case "true":
                r.Read();
                return true;
            case "false":
                r.Read();
                return false;
            case "date":
                return DateTimeOffset.Parse(r.ReadElementContentAsString().Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            case "data":
                return Convert.FromBase64String(r.ReadElementContentAsString());
            default:
                throw new FormatException("Unknown property list element " + name);
        }
    }

    /// <summary>Writes a property list in the layout Apple's tools use (tab indented, XML declaration and DOCTYPE).</summary>
    /// <param name="root">A value of the types listed on <see cref="Plist"/>; dictionary keys are written in the order given.</param>
    /// <returns>The document as UTF-8 bytes.</returns>
    /// <exception cref="ArgumentException">The value contains a type that has no property list form.</exception>
    public static byte[] Write(object root)
    {
        StringBuilder sb = new();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        sb.Append("<plist version=\"1.0\">\n");
        WriteValue(sb, root, 0);
        sb.Append("</plist>\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static void WriteValue(StringBuilder sb, object? v, int depth)
    {
        string pad = new('\t', depth);
        switch (v)
        {
            case IReadOnlyDictionary<string, object?> d:
                sb.Append(pad).Append("<dict>\n");
                foreach (KeyValuePair<string, object?> kv in d)
                {
                    sb.Append(pad).Append("\t<key>").Append(Escape(kv.Key)).Append("</key>\n");
                    WriteValue(sb, kv.Value, depth + 1);
                }

                sb.Append(pad).Append("</dict>\n");
                break;
            case IReadOnlyList<object?> a:
                sb.Append(pad).Append("<array>\n");
                foreach (object? item in a)
                {
                    WriteValue(sb, item, depth + 1);
                }

                sb.Append(pad).Append("</array>\n");
                break;
            case string s:
                sb.Append(pad).Append("<string>").Append(Escape(s)).Append("</string>\n");
                break;
            case long l:
                sb.Append(pad).Append("<integer>").Append(l.ToString(CultureInfo.InvariantCulture)).Append("</integer>\n");
                break;
            case int i:
                sb.Append(pad).Append("<integer>").Append(i.ToString(CultureInfo.InvariantCulture)).Append("</integer>\n");
                break;
            case double dbl:
                sb.Append(pad).Append("<real>").Append(dbl.ToString("R", CultureInfo.InvariantCulture)).Append("</real>\n");
                break;
            case bool b:
                sb.Append(pad).Append(b ? "<true/>\n" : "<false/>\n");
                break;
            case DateTimeOffset dt:
                sb.Append(pad).Append("<date>").Append(dt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)).Append("</date>\n");
                break;
            case byte[] bytes:
                sb.Append(pad).Append("<data>").Append(Convert.ToBase64String(bytes)).Append("</data>\n");
                break;
            default:
                throw new ArgumentException("No property list form for " + (v?.GetType().Name ?? "null"), nameof(v));
        }
    }

    private static string Escape(string s)
    {
        StringBuilder sb = new(s.Length);
        foreach (char c in s)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                default:
                    // XML 1.0 cannot carry most control characters; drop them rather than write an invalid file.
                    if (c >= ' ' || c is '\t' or '\n' or '\r')
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.ToString();
    }
}
