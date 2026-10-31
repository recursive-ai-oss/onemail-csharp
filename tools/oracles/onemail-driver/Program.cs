using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Mime.Reading;
using RecursiveAI.OneMail.Mime.Transfer;
using RecursiveAI.OneMail.Text;

namespace OneMail.Oracles.OneMailDump;

// OneMail's driver for the differential runner. It reads --root and relative file names on standard input and writes one
// JSON line per input in the dump form of tools/oracles/README.md, using only OneMail's public API.
internal static class Program
{
    private static readonly HashSet<string> Unstructured = new(StringComparer.OrdinalIgnoreCase)
    {
        "subject", "comments", "keywords", "content-description", "thread-topic",
    };

    private static readonly string[] AddressHeaderNames = ["from", "sender", "reply-to", "to", "cc", "bcc"];

    private static int Main(string[] args)
    {
        string root = ".";
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--version")
            {
                Console.Out.WriteLine("onemail " + typeof(MimeEntity).Assembly.GetName().Version);
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
                json = Dump(line, Path.Combine(root, line), error: null);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                json = Dump(line, Path.Combine(root, line), e.GetType().Name + ": " + e.Message);
            }

            stdout.Write(json);
            stdout.WriteByte((byte)'\n');
            stdout.Flush();
        }

        return 0;
    }

    private static byte[] Dump(string id, string path, string? error)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter w = new(buffer))
        {
            w.WriteStartObject();
            w.WriteString("schema", "onemail-dump/1");
            w.WriteString("id", id);
            w.WriteStartObject("oracle");
            w.WriteString("name", "onemail");
            w.WriteString("version", "working-tree");
            w.WriteString("notes", "MimeEntity.Parse, default limits");
            w.WriteBoolean("headersOrdered", true);
            w.WriteEndObject();
            if (error is null)
            {
                byte[] data = File.ReadAllBytes(path);
                MimeEntity msg = MimeEntity.Parse(data);
                var defects = new List<string>();
                int index = 0;
                CollectDefects(msg, defects, ref index);
                var structured = new List<MimeDefect>();
                w.WriteNull("error");
                w.WriteStartObject("addresses");
                foreach (string name in AddressHeaderNames)
                {
                    if (msg.Headers.GetFirst(name) is null)
                    {
                        continue;
                    }

                    w.WriteStartArray(name);
                    foreach (MimeAddress a in msg.GetAddresses(name, structured))
                    {
                        WriteAddress(w, a);
                    }

                    w.WriteEndArray();
                }

                w.WriteEndObject();
                if (msg.Headers.GetFirst("Date") is null)
                {
                    w.WriteNull("date");
                }
                else
                {
                    MimeDate? d = msg.GetDate(structured);
                    if (d is null)
                    {
                        w.WriteNull("date");
                    }
                    else
                    {
                        w.WriteStartObject("date");
                        w.WriteString("utc", d.Value.Utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));
                        if (d.Value.OffsetMinutes is { } off)
                        {
                            w.WriteNumber("offsetMinutes", off);
                        }
                        else
                        {
                            w.WriteNull("offsetMinutes");
                        }

                        w.WriteEndObject();
                    }
                }

                string? messageId = msg.GetMessageId(structured);
                IReadOnlyList<string> references = msg.GetReferences(structured);
                IReadOnlyList<string> inReplyTo = msg.GetInReplyTo(structured);
                w.WriteStartObject("ids");
                w.WriteString("messageId", messageId is null ? null : Clean(messageId));
                w.WriteStartArray("inReplyTo");
                foreach (string rid in inReplyTo)
                {
                    w.WriteStringValue(Clean(rid));
                }

                w.WriteEndArray();
                w.WriteStartArray("references");
                foreach (string rid in references)
                {
                    w.WriteStringValue(Clean(rid));
                }

                w.WriteEndArray();
                w.WriteEndObject();
                foreach (MimeDefect d in structured)
                {
                    defects.Add("part0:" + d.Kind);
                }

                w.WriteStartArray("defects");
                foreach (string d in defects)
                {
                    w.WriteStringValue(d);
                }

                w.WriteEndArray();
                w.WritePropertyName("root");
                WriteEntity(w, msg);
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

    private static void CollectDefects(MimeEntity e, List<string> into, ref int index)
    {
        int mine = index++;
        foreach (MimeDefect d in e.Defects)
        {
            into.Add("part" + mine + ":" + d.Kind);
        }

        switch (e)
        {
            case MimeMultipart m:
                foreach (MimeEntity c in m.Children)
                {
                    CollectDefects(c, into, ref index);
                }

                break;
            case MimeMessagePart mp when mp.Message is { } inner:
                CollectDefects(inner, into, ref index);
                break;
        }
    }

    private static void WriteAddress(Utf8JsonWriter w, MimeAddress a)
    {
        switch (a)
        {
            case MimeMailbox m:
                w.WriteStartObject();
                w.WriteString("name", Clean(m.Name ?? string.Empty));
                w.WriteString("addr", Clean(m.Address));
                w.WriteEndObject();
                break;
            case MimeGroup g:
                foreach (MimeMailbox m in g.Members)
                {
                    WriteAddress(w, m);
                }

                break;
        }
    }

    private static void WriteEntity(Utf8JsonWriter w, MimeEntity e)
    {
        w.WriteStartObject();
        w.WriteStartArray("headers");
        foreach (MimeHeader h in e.Headers)
        {
            w.WriteStartObject();
            w.WriteString("name", h.Name);
            w.WriteString("raw", Encoding.Latin1.GetString(h.RawValue.Span));
            if (Unstructured.Contains(h.Name))
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
        w.WriteString("contentType", e.MediaType);
        w.WriteStartObject("params");
        foreach (DecodedParameter p in e.GetContentTypeParameters())
        {
            w.WriteString(p.Name.ToLowerInvariant(), Clean(p.Value));
        }

        w.WriteEndObject();
        w.WriteString("disposition", e.Disposition);
        string? fn = e.FileName;
        w.WriteString("filename", fn is null ? null : Clean(fn));
        bool leaf = e is MimePart;
        w.WriteString("cteApplied", leaf ? e.TransferEncoding switch
        {
            ContentTransferEncoding.Base64 => "base64",
            ContentTransferEncoding.QuotedPrintable => "quoted-printable",
            ContentTransferEncoding.UUEncode => "uuencode",
            _ => "none",
        } : "none");
        if (e is MimeMultipart mp)
        {
            w.WriteBoolean("preamble", HasText(mp.GetPreamble()));
            w.WriteBoolean("epilogue", HasText(mp.GetEpilogue()));
        }
        else
        {
            w.WriteNull("preamble");
            w.WriteNull("epilogue");
        }

        if (e is MimePart part)
        {
            WriteBodyHashes(w, part.GetDecodedContent());
        }
        else
        {
            w.WriteNull("body");
        }

        w.WriteStartArray("children");
        if (e is MimeMultipart m)
        {
            foreach (MimeEntity c in m.Children)
            {
                WriteEntity(w, c);
            }
        }

        w.WriteEndArray();
        w.WritePropertyName("message");
        if (e is MimeMessagePart mep && mep.Message is { } inner)
        {
            WriteEntity(w, inner);
        }
        else
        {
            w.WriteNullValue();
        }

        w.WriteEndObject();
    }

    private static bool HasText(byte[] bytes)
    {
        foreach (byte b in bytes)
        {
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or 0x0B or 0x0C))
            {
                return true;
            }
        }

        return false;
    }

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
        var eol = new List<byte>(body.Length);
        for (int i = 0; i < body.Length; i++)
        {
            if (body[i] == (byte)'\r')
            {
                eol.Add((byte)'\n');
                if (i + 1 < body.Length && body[i + 1] == (byte)'\n')
                {
                    i++;
                }
            }
            else
            {
                eol.Add(body[i]);
            }
        }

        byte[] e = [.. eol];
        w.WriteString("eolSha256", Convert.ToHexStringLower(SHA256.HashData(e)));
        int n = e.Length;
        while (n > 0 && e[n - 1] == (byte)'\n')
        {
            n--;
        }

        w.WriteString("trimSha256", Convert.ToHexStringLower(SHA256.HashData(e.AsSpan(0, n))));
        w.WriteEndObject();
    }
}
