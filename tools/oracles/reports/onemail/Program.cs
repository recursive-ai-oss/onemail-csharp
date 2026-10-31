using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Mime.Authentication;
using RecursiveAI.OneMail.Mime.Mhtml;
using RecursiveAI.OneMail.Mime.Reading;
using RecursiveAI.OneMail.Mime.Reports;

namespace OneMail.Oracles.OneMailReports;

// Reads --root DIR and relative file names on standard input; writes one JSON line per file in the dump form of
// tools/oracles/reports/README.md. With --urljoin, reads "base<TAB>reference" lines and writes the resolved URI.
internal static class Program
{
    private static int Main(string[] args)
    {
        string root = ".";
        bool urljoin = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--version")
            {
                Console.Out.WriteLine("onemail working-tree");
                return 0;
            }

            if (args[i] == "--root" && i + 1 < args.Length)
            {
                root = args[++i];
            }

            if (args[i] == "--write" && i + 1 < args.Length)
            {
                WriteSamples(args[++i]);
                return 0;
            }

            if (args[i] == "--urljoin")
            {
                urljoin = true;
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
            if (urljoin)
            {
                string[] f = line.Split('\t');
                json = new JsonObject { ["base"] = f[0], ["ref"] = f.Length > 1 ? f[1] : string.Empty, ["result"] = MhtmlUri.Resolve(f[0], f.Length > 1 ? f[1] : string.Empty) }.ToJsonString();
            }
            else
            {
                try
                {
                    json = Dump(line, Path.Combine(root, line)).ToJsonString();
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    json = new JsonObject { ["schema"] = "onemail-reports/1", ["id"] = line, ["error"] = e.GetType().Name + ": " + e.Message }.ToJsonString();
                }
            }

            stdout.Write(Encoding.UTF8.GetBytes(json + "\n"));
            stdout.Flush();
        }

        return 0;
    }

    // Writes messages built with the report and MHTML writers, so every reader can be pointed at what OneMail produces.
    private static void WriteSamples(string dir)
    {
        Directory.CreateDirectory(dir);
        var perMessage = new ReportFieldBlock();
        perMessage.Add("Reporting-MTA", "dns; mx.example.org");
        perMessage.Add("Arrival-Date", "Sat, 02 Jan 2027 03:04:05 +0000");
        var dsn = new DeliveryStatusReport(perMessage, [
            RecipientStatus.Create("a+b@example.org", DsnAction.Failed, new EnhancedStatusCode(5, 1, 1), "smtp; 550 5.1.1 no such user", "dns; mail.example.org", "list@example.org"),
            RecipientStatus.Create("c@example.org", DsnAction.Delayed, new EnhancedStatusCode(4, 4, 1)),
        ]);
        byte[] original = Encoding.ASCII.GetBytes("From: <s@example.com>\r\nTo: <a@example.org>\r\nSubject: hi\r\nMessage-ID: <o1@example.com>\r\n\r\nbody\r\n");
        File.WriteAllBytes(Path.Combine(dir, "written-dsn.eml"), MimeReport.CreateDeliveryStatus("Delivery failed.", dsn, original, false, "dsn-boundary").ToArray());
        File.WriteAllBytes(Path.Combine(dir, "written-dsn-headers.eml"), MimeReport.CreateDeliveryStatus("Delivery failed.", dsn, original, true, "dsn-boundary").ToArray());

        var globalMessage = new ReportFieldBlock();
        globalMessage.Add("Reporting-MTA", "dns; mx.example.org");
        var global = new DeliveryStatusReport(globalMessage, [RecipientStatus.Create("\u7528\u6237@example.org", DsnAction.Failed, new EnhancedStatusCode(5, 1, 1), "smtp; 550 \u7528\u6237 unknown")]);
        File.WriteAllBytes(Path.Combine(dir, "written-dsn-global.eml"), MimeReport.CreateDeliveryStatus("Delivery failed.", global, original, false, "dsn-boundary").ToArray());

        if (!MdnDisposition.TryParse("manual-action/MDN-sent-manually; displayed", out MdnDisposition? disposition))
        {
            throw new InvalidOperationException("sample disposition");
        }

        var mdn = DispositionNotification.Create("pc; OneMail", "j@example.com", "<o1@example.com>", disposition!);
        File.WriteAllBytes(Path.Combine(dir, "written-mdn.eml"), MimeReport.CreateDispositionNotification("Your message was displayed.", mdn, original, true, "mdn-boundary").ToArray());

        FeedbackReport arf = FeedbackReport.Create("abuse", "OneMail/1", IPAddress.Parse("192.0.2.1"), new DateTimeOffset(2027, 1, 2, 3, 4, 5, TimeSpan.Zero));
        File.WriteAllBytes(Path.Combine(dir, "written-arf.eml"), MimeReport.CreateFeedbackReport("This is an abuse report.", arf, original, false, "arf-boundary").ToArray());

        var b = new MhtmlBuilder { Subject = "Saved page", From = "Saved <saved@example.org>", Date = new DateTimeOffset(2027, 1, 2, 3, 4, 5, TimeSpan.Zero), Boundary = "mht-boundary" };
        b.SetRoot("<html><body><img src=\"img/a.png\"><img src=\"cid:b@example.org\"></body></html>", "http://www.example.org/dir/page.html");
        b.AddResource(new byte[] { 1, 2, 3, 4 }, "image/png", "http://www.example.org/dir/img/a.png");
        b.AddResource(new byte[] { 5, 6, 7 }, "image/png", null, "b@example.org");
        using (FileStream fs = File.Create(Path.Combine(dir, "written-mht.eml")))
        {
            b.WriteTo(fs);
        }

        File.WriteAllText(Path.Combine(dir, "written-mht.refs"), "img/a.png\ncid:b@example.org\n./img/a.png\nmissing.png\n");
    }

    private static string L1(ReadOnlyMemory<byte> b) => Encoding.Latin1.GetString(b.Span);

    private static JsonArray Block(ReportFieldBlock b)
    {
        var a = new JsonArray();
        foreach (ReportField f in b)
        {
            a.Add(new JsonArray(f.Name, L1(f.RawValue)));
        }

        return a;
    }

    private static JsonObject Dump(string id, string path)
    {
        byte[] data = File.ReadAllBytes(path);
        MimeEntity root = MimeEntity.Parse(data);
        var o = new JsonObject
        {
            ["schema"] = "onemail-reports/1",
            ["id"] = id,
            ["oracle"] = new JsonObject { ["name"] = "onemail", ["version"] = "working-tree" },
            ["error"] = null,
        };

        var rt = new MemoryStream();
        root.WriteTo(rt);
        o["roundtrip"] = rt.GetBuffer().AsSpan(0, (int)rt.Length).SequenceEqual(data);

        var reports = new JsonArray();
        Walk(root, reports);
        o["reports"] = reports;

        var auth = new JsonArray();
        foreach (AuthenticationResults ar in root.GetAuthenticationResults())
        {
            var results = new JsonArray();
            foreach (AuthenticationMethodResult r in ar.Results)
            {
                var props = new JsonArray();
                foreach (AuthenticationProperty p in r.Properties)
                {
                    props.Add(new JsonArray(p.PropertyType, p.Name, p.Value));
                }

                results.Add(new JsonObject { ["method"] = r.Method, ["mv"] = r.MethodVersion, ["result"] = r.Result, ["comment"] = r.ResultComment, ["reason"] = r.Reason, ["props"] = props });
            }

            var kinds = new JsonArray();
            foreach (MimeDefect d in ar.Defects)
            {
                kinds.Add(d.Kind.ToString());
            }

            auth.Add(new JsonObject { ["authserv"] = ar.AuthServId, ["version"] = ar.Version, ["results"] = results, ["defects"] = kinds });
        }

        o["auth"] = auth;

        string refsPath = Path.ChangeExtension(path, ".refs");
        if (File.Exists(refsPath) && MhtmlDocument.Find(root) is { } doc)
        {
            var refs = new JsonArray();
            foreach (string l in File.ReadAllLines(refsPath))
            {
                string[] f = l.Split('\t');
                MhtmlResource? hit = doc.Resolve(f[0], f.Length > 1 && f[1].Length > 0 ? f[1] : null);
                int idx = -1;
                for (int i = 0; i < doc.Resources.Count; i++)
                {
                    if (ReferenceEquals(doc.Resources[i], hit))
                    {
                        idx = i;
                    }
                }

                refs.Add(new JsonObject { ["ref"] = f[0], ["base"] = f.Length > 1 && f[1].Length > 0 ? f[1] : null, ["hit"] = idx });
            }

            int rootIdx = -1;
            for (int i = 0; i < doc.Resources.Count; i++)
            {
                if (doc.Resources[i].IsRoot)
                {
                    rootIdx = i;
                }
            }

            o["mhtml"] = new JsonObject { ["parts"] = doc.Resources.Count, ["root"] = rootIdx, ["refs"] = refs };
        }
        else
        {
            o["mhtml"] = null;
        }

        return o;
    }

    private static void Walk(MimeEntity e, JsonArray into)
    {
        if (e is MimePart p)
        {
            JsonArray? blocks = null;
            string media = p.MediaType;
            if (DeliveryStatusReport.From(p) is { } ds)
            {
                blocks = [Block(ds.PerMessage)];
                foreach (RecipientStatus r in ds.Recipients)
                {
                    blocks.Add(Block(r.Fields));
                }
            }
            else if (DispositionNotification.From(p) is { } mdn)
            {
                blocks = [Block(mdn.Fields)];
            }
            else if (FeedbackReport.From(p) is { } arf)
            {
                blocks = [Block(arf.Fields)];
            }

            if (blocks is not null)
            {
                into.Add(new JsonObject { ["type"] = media, ["blocks"] = blocks });
            }
        }

        if (e is MimeMultipart m)
        {
            foreach (MimeEntity c in m.Children)
            {
                Walk(c, into);
            }
        }
        else if (e is MimeMessagePart { Message: { } inner })
        {
            Walk(inner, into);
        }
    }
}
