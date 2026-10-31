using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MimeKit;
using MimeKit.Cryptography;

namespace OneMail.Oracles.MimeKitReports;

// Reads --root DIR and relative file names on standard input; writes one JSON line per file in the dump form of
// tools/oracles/reports/README.md, using only MimeKit's public API. Report bodies come from MessageDeliveryStatus.StatusGroups
// and MessageDispositionNotification.Fields; other report media types (feedback reports and the "global" forms) are read with
// HeaderList.Load, the public header parser. Authentication-Results comes from AuthenticationResults.TryParse. Related parts are
// resolved with MultipartRelated.IndexOf(Uri).
internal static class Program
{
    private static readonly HashSet<string> ReportTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "message/delivery-status", "message/global-delivery-status", "message/disposition-notification",
        "message/global-disposition-notification", "message/feedback-report",
    };

    private static int Main(string[] args)
    {
        string version = "unknown";
        string deps = Path.Combine(AppContext.BaseDirectory, "MimeKitReports.deps.json");
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

            string json;
            try
            {
                json = Dump(line, Path.Combine(root, line), version).ToJsonString();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                json = new JsonObject { ["schema"] = "onemail-reports/1", ["id"] = line, ["oracle"] = new JsonObject { ["name"] = "mimekit", ["version"] = version }, ["error"] = e.GetType().Name + ": " + e.Message }.ToJsonString();
            }

            stdout.Write(Encoding.UTF8.GetBytes(json + "\n"));
            stdout.Flush();
        }

        return 0;
    }

    private static JsonArray Block(HeaderList list)
    {
        var a = new JsonArray();
        foreach (Header h in list)
        {
            a.Add(new JsonArray(h.Field, Encoding.Latin1.GetString(h.RawValue)));
        }

        return a;
    }

    private static JsonObject Dump(string id, string path, string version)
    {
        using FileStream fs = File.OpenRead(path);
        MimeMessage message = new MimeParser(fs, MimeFormat.Entity).ParseMessage();
        var o = new JsonObject
        {
            ["schema"] = "onemail-reports/1",
            ["id"] = id,
            ["oracle"] = new JsonObject { ["name"] = "mimekit", ["version"] = version },
            ["error"] = null,
        };

        var reports = new JsonArray();
        if (message.Body is not null)
        {
            Walk(message.Body, reports);
        }

        o["reports"] = reports;

        var auth = new JsonArray();
        foreach (Header h in message.Headers)
        {
            if (h.Id != HeaderId.AuthenticationResults)
            {
                continue;
            }

            if (!AuthenticationResults.TryParse(h.RawValue, out AuthenticationResults? ar) || ar is null)
            {
                auth.Add(new JsonObject { ["failed"] = true });
                continue;
            }

            var results = new JsonArray();
            foreach (AuthenticationMethodResult r in ar.Results)
            {
                var props = new JsonArray();
                foreach (AuthenticationMethodProperty p in r.Properties)
                {
                    props.Add(new JsonArray(p.PropertyType, p.Property, p.Value));
                }

                results.Add(new JsonObject { ["method"] = r.Method, ["mv"] = r.Version, ["result"] = r.Result, ["comment"] = r.ResultComment, ["reason"] = r.Reason, ["props"] = props });
            }

            auth.Add(new JsonObject { ["authserv"] = ar.AuthenticationServiceIdentifier, ["version"] = ar.Version, ["results"] = results });
        }

        o["auth"] = auth;

        string refsPath = Path.ChangeExtension(path, ".refs");
        MultipartRelated? related = message.Body is null ? null : FindRelated(message.Body);
        if (File.Exists(refsPath) && related is not null)
        {
            var refs = new JsonArray();
            foreach (string l in File.ReadAllLines(refsPath))
            {
                string[] f = l.Split('\t');
                if (f.Length > 1 && f[1].Length > 0)
                {
                    // MimeKit's public lookup takes no base; a reference that needs one cannot be compared.
                    refs.Add(new JsonObject { ["ref"] = f[0], ["base"] = f[1], ["hit"] = null });
                    continue;
                }

                refs.Add(new JsonObject { ["ref"] = f[0], ["base"] = null, ["hit"] = Lookup(related, f[0]) });
            }

            o["mhtml"] = new JsonObject { ["parts"] = related.Count, ["root"] = related.Root is { } rootPart ? related.IndexOf(rootPart) : -1, ["refs"] = refs };
        }
        else
        {
            o["mhtml"] = null;
        }

        return o;
    }

    private static int? Lookup(MultipartRelated related, string url)
    {
        UriKind kind = Uri.IsWellFormedUriString(url, UriKind.Absolute) ? UriKind.Absolute : Uri.IsWellFormedUriString(url, UriKind.Relative) ? UriKind.Relative : UriKind.RelativeOrAbsolute;
        try
        {
            return related.IndexOf(new Uri(url, kind));
        }
        catch (Exception e) when (e is UriFormatException or ArgumentException)
        {
            return null;
        }
    }

    private static MultipartRelated? FindRelated(MimeEntity e)
    {
        if (e is MultipartRelated r)
        {
            return r;
        }

        if (e is Multipart m)
        {
            foreach (MimeEntity c in m)
            {
                if (FindRelated(c) is { } f)
                {
                    return f;
                }
            }
        }
        else if (e is MessagePart mp && mp.Message?.Body is { } body)
        {
            return FindRelated(body);
        }

        return null;
    }

    private static void Walk(MimeEntity e, JsonArray into)
    {
        switch (e)
        {
            case Multipart m:
                foreach (MimeEntity c in m)
                {
                    Walk(c, into);
                }

                break;
            case MessagePart mp:
                if (mp.Message?.Body is { } inner)
                {
                    Walk(inner, into);
                }

                break;
            case MessageDeliveryStatus ds:
            {
                var blocks = new JsonArray();
                foreach (HeaderList g in ds.StatusGroups)
                {
                    blocks.Add(Block(g));
                }

                into.Add(new JsonObject { ["type"] = e.ContentType.MimeType, ["blocks"] = blocks });
                break;
            }

            case MessageDispositionNotification mdn:
                into.Add(new JsonObject { ["type"] = e.ContentType.MimeType, ["blocks"] = new JsonArray(Block(mdn.Fields)) });
                break;
            case MimePart p when ReportTypes.Contains(e.ContentType.MimeType) && p.Content is not null:
            {
                using var ms = new MemoryStream();
                p.Content.DecodeTo(ms);
                ms.Position = 0;
                // MimeKit has no public reader for the groups of a "global" delivery status that did not become a
                // MessageDeliveryStatus; those are not compared.
                JsonNode? blocks = e.ContentType.MimeType.EndsWith("delivery-status", StringComparison.OrdinalIgnoreCase) ? null : new JsonArray(Block(HeaderList.Load(ms)));
                into.Add(new JsonObject { ["type"] = e.ContentType.MimeType, ["blocks"] = blocks });
                break;
            }
        }
    }
}
