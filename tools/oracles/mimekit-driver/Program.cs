using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MimeKit;

namespace OneMail.Oracles.MimeKitDump;

// Reads a root directory (--root) and relative file names on standard input, one per line. Writes one JSON line per input
// in the dump form of tools/oracles/README.md. Only MimeKit's public API is used; nothing here depends on how MimeKit works inside.
internal static class Program
{
    private static readonly HashSet<string> UnstructuredHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "subject", "comments", "keywords", "content-description", "thread-topic",
    };

    private static readonly string[] AddressHeaderNames = ["from", "sender", "reply-to", "to", "cc", "bcc"];

    private static readonly UTF8Encoding Utf8 = new(false);

    private static int Main(string[] args)
    {
        // The package version is what the oracle pin names; the assembly's own version string can differ from it.
        // The restored package is listed in the app's dependency file as "<name>/<version>".
        string version = "unknown";
        string deps = Path.Combine(AppContext.BaseDirectory, "MimeKitDump.deps.json");
        if (File.Exists(deps))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(deps));
            foreach (JsonProperty lib in doc.RootElement.GetProperty("libraries").EnumerateObject())
            {
                string[] parts = lib.Name.Split('/');
                if (parts.Length == 2 && parts[0] == "MimeKit")
                {
                    version = parts[1];
                }
            }
        }

        string root = ".";
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--version")
            {
                Console.Out.WriteLine("mimekit " + version);
                return 0;
            }

            if (args[i] == "--root" && i + 1 < args.Length)
            {
                root = args[++i];
            }
        }

        using Stream stdout = Console.OpenStandardOutput();
        string? line;
        while ((line = Console.In.ReadLine()) != null)
        {
            if (line.Length == 0)
            {
                continue;
            }

            byte[] json;
            try
            {
                json = Dump(line, Path.Combine(root, line), version, error: null);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                json = Dump(line, Path.Combine(root, line), version, e.GetType().Name + ": " + e.Message);
            }

            stdout.Write(json);
            stdout.WriteByte((byte)'\n');
            stdout.Flush();
        }

        return 0;
    }

    // With error == null the message is parsed and dumped; otherwise an error record is written without parsing.
    private static byte[] Dump(string id, string path, string version, string? error)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter w = new(buffer))
        {
            w.WriteStartObject();
            w.WriteString("schema", "onemail-dump/1");
            w.WriteString("id", id);
            w.WriteStartObject("oracle");
            w.WriteString("name", "mimekit");
            w.WriteString("version", version);
            w.WriteString("notes", "MimeParser(MimeFormat.Entity), ParserOptions.Default");
            w.WriteBoolean("headersOrdered", true);
            w.WriteEndObject();
            if (error is null)
            {
                // Everything is read inside the call: content streams are lazy and may fail later.
                using FileStream fs = File.OpenRead(path);
                MimeMessage message = new MimeParser(fs, MimeFormat.Entity).ParseMessage();
                WriteBody(w, message);
            }
            else
            {
                w.WriteString("error", Clean(error));
                w.WriteNull("defects");
            }

            w.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static void WriteBody(Utf8JsonWriter w, MimeMessage message)
    {
        w.WriteNull("error");
        w.WriteNull("defects");
        w.WriteStartObject("addresses");
        foreach (string name in AddressHeaderNames)
        {
            if (!message.Headers.Contains(name))
            {
                continue;
            }

            InternetAddressList list = name switch
            {
                "from" => message.From,
                "sender" => message.Sender is { } s ? [s] : [],
                "reply-to" => message.ReplyTo,
                "to" => message.To,
                "cc" => message.Cc,
                _ => message.Bcc,
            };
            w.WriteStartArray(name);
            WriteAddresses(w, list);
            w.WriteEndArray();
        }

        w.WriteEndObject();
        if (message.Headers.Contains(HeaderId.Date))
        {
            if (message.Date == DateTimeOffset.MinValue)
            {
                w.WriteNull("date");
            }
            else
            {
                w.WriteStartObject("date");
                w.WriteString("utc", message.Date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));
                w.WriteNumber("offsetMinutes", (int)message.Date.Offset.TotalMinutes);
                w.WriteEndObject();
            }
        }
        else
        {
            w.WriteNull("date");
        }

        w.WritePropertyName("root");
        WriteRoot(w, message);
    }

    private static void WriteAddresses(Utf8JsonWriter w, InternetAddressList list)
    {
        foreach (InternetAddress a in list)
        {
            switch (a)
            {
                case MailboxAddress m:
                    w.WriteStartObject();
                    w.WriteString("name", Clean(m.Name ?? string.Empty));
                    w.WriteString("addr", Clean(m.Address ?? string.Empty));
                    w.WriteEndObject();
                    break;
                case GroupAddress g:
                    // Group members are flattened; the group name is not compared.
                    WriteAddresses(w, g.Members);
                    break;
                default:
                    break;
            }
        }
    }

    // The root is the message's own header block plus its body entity: the dump has one entity per MIME entity, and the
    // message headers are the headers of the root entity.
    private static void WriteRoot(Utf8JsonWriter w, MimeMessage message)
    {
        if (message.Body is null)
        {
            // No body entity at all (header-only message): a text/plain entity with no body.
            w.WriteStartObject();
            WriteHeaders(w, message.Headers);
            w.WriteString("contentType", "text/plain");
            w.WriteNull("params");
            w.WriteNull("disposition");
            w.WriteNull("filename");
            w.WriteString("cteApplied", "none");
            w.WriteNull("preamble");
            w.WriteNull("epilogue");
            WriteBodyHashes(w, []);
            w.WriteStartArray("children");
            w.WriteEndArray();
            w.WriteNull("message");
            w.WriteEndObject();
            return;
        }

        WriteEntity(w, message.Body, message.Headers.Concat(message.Body.Headers));
    }

    private static void WriteEntity(Utf8JsonWriter w, MimeEntity e, IEnumerable<Header>? headersOverride = null)
    {
        w.WriteStartObject();
        // MimeKit splits a message's header block: Content-* fields live on the body entity, the rest on the message.
        // The dump's root entity lists both, message fields first, so the header order is only comparable without Content-*.
        WriteHeaders(w, headersOverride ?? e.Headers);
        w.WriteString("contentType", e.ContentType.MimeType.ToLowerInvariant());
        w.WriteStartObject("params");
        foreach (Parameter p in e.ContentType.Parameters)
        {
            w.WriteString(p.Name.ToLowerInvariant(), Clean(p.Value));
        }

        w.WriteEndObject();
        w.WriteString("disposition", e.ContentDisposition?.Disposition.ToLowerInvariant());
        string? filename = e is MimePart mp ? mp.FileName : e.ContentDisposition?.FileName ?? e.ContentType.Name;
        w.WriteString("filename", filename is null ? null : Clean(filename));
        ContentEncoding enc = (e as MimePart)?.ContentTransferEncoding ?? ContentEncoding.Default;
        w.WriteString("cteApplied", enc switch
        {
            ContentEncoding.Base64 => "base64",
            ContentEncoding.QuotedPrintable => "quoted-printable",
            ContentEncoding.UUEncode => "uuencode",
            _ => "none",
        });
        if (e is Multipart mpart)
        {
            w.WriteBoolean("preamble", HasText(mpart.Preamble));
            w.WriteBoolean("epilogue", HasText(mpart.Epilogue));
        }
        else
        {
            w.WriteNull("preamble");
            w.WriteNull("epilogue");
        }

        if (e is MimePart part)
        {
            using MemoryStream ms = new();
            part.Content?.DecodeTo(ms);
            WriteBodyHashes(w, ms.ToArray());
        }
        else if (e is MessagePart)
        {
            w.WriteNull("body");
        }
        else if (e is Multipart)
        {
            w.WriteNull("body");
        }
        else
        {
            // Other entity classes (delivery-status groups) have no byte content in the public model.
            w.WriteNull("body");
        }

        w.WriteStartArray("children");
        if (e is Multipart m)
        {
            foreach (MimeEntity c in m)
            {
                WriteEntity(w, c);
            }
        }

        w.WriteEndArray();
        w.WritePropertyName("message");
        if (e is MessagePart mep && mep.Message is { } inner)
        {
            if (inner.Body is null)
            {
                WriteRoot(w, inner);
            }
            else
            {
                WriteEntity(w, inner.Body, inner.Headers.Concat(inner.Body.Headers));
            }
        }
        else
        {
            w.WriteNullValue();
        }

        w.WriteEndObject();
    }

    private static bool HasText(string? s) => s is not null && s.Any(c => !char.IsWhiteSpace(c));

    private static void WriteHeaders(Utf8JsonWriter w, IEnumerable<Header> headers)
    {
        w.WriteStartArray("headers");
        foreach (Header h in headers)
        {
            w.WriteStartObject();
            w.WriteString("name", Latin1(h.Field));
            w.WriteString("raw", Latin1(h.RawValue));
            if (UnstructuredHeaders.Contains(h.Field))
            {
                w.WriteString("value", Clean(h.Value));
            }
            else
            {
                w.WriteNull("value");
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static string Latin1(string s) => s;

    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    // JSON text cannot hold lone surrogates; all three drivers replace them the same way.
    private static string Clean(string s)
    {
        StringBuilder? sb = null;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            bool bad = char.IsLowSurrogate(c) || (char.IsHighSurrogate(c) && (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])));
            if (char.IsHighSurrogate(c) && !bad)
            {
                sb?.Append(c).Append(s[i + 1]);
                i++;
                continue;
            }

            if (bad)
            {
                sb ??= new StringBuilder(s, 0, i, s.Length);
                sb.Append('\uFFFD');
            }
            else
            {
                sb?.Append(c);
            }
        }

        return sb?.ToString() ?? s;
    }

    private static void WriteBodyHashes(Utf8JsonWriter w, byte[] body)
    {
        w.WriteStartObject("body");
        w.WriteNumber("len", body.Length);
        w.WriteString("sha256", Convert.ToHexStringLower(SHA256.HashData(body)));
        byte[] eol = NormaliseEol(body);
        w.WriteString("eolSha256", Convert.ToHexStringLower(SHA256.HashData(eol)));
        int n = eol.Length;
        while (n > 0 && eol[n - 1] == (byte)'\n')
        {
            n--;
        }

        w.WriteString("trimSha256", Convert.ToHexStringLower(SHA256.HashData(eol.AsSpan(0, n))));
        w.WriteEndObject();
    }

    // CRLF and lone CR become LF.
    private static byte[] NormaliseEol(byte[] body)
    {
        List<byte> o = new(body.Length);
        for (int i = 0; i < body.Length; i++)
        {
            if (body[i] == (byte)'\r')
            {
                o.Add((byte)'\n');
                if (i + 1 < body.Length && body[i + 1] == (byte)'\n')
                {
                    i++;
                }
            }
            else
            {
                o.Add(body[i]);
            }
        }

        return [.. o];
    }
}
