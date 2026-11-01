using System.Globalization;
using System.Text;
using System.Text.Json;
using RecursiveAI.OneMail.Conversion;
using RecursiveAI.OneMail.Conversion.Stores;

namespace RecursiveAI.OneMail.Cli;

/// <summary>
/// <c>onemail convert</c> and <c>onemail export</c>: thin over <see cref="Mail.ConvertAsync"/>. Both print what happened to every item
/// they could not carry over completely, as text or (with <c>--json</c>) one JSON object per line.
/// </summary>
internal static class ConvertCommand
{
    public const string ConvertUsage = "usage: onemail convert [--to eml|mbox|maildir|emlx|ics|vcf|jcal|jcard|xcal|xcard|tnef|msg|pst] [--folder <path>] [--no-other-items] [--json] <input> <output>";

    public const string ExportUsage = "usage: onemail export [--format eml|mbox|maildir|ics|vcf] [--folder <path>] [--no-other-items] [--json] <store> <directory>";

    public static int Convert(string[] args, TextWriter output, TextWriter error) => Run("convert", args, output, error, ConvertUsage, defaultFormat: null);

    public static int Export(string[] args, TextWriter output, TextWriter error) => Run("export", args, output, error, ExportUsage, defaultFormat: ConvertFormat.Eml);

    private static int Run(string verb, string[] args, TextWriter output, TextWriter error, string usage, ConvertFormat? defaultFormat)
    {
        ConvertFormat? format = defaultFormat;
        string? folder = null;
        bool json = false, skipOther = false;
        List<string> paths = [];
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--json":
                    json = true;
                    break;
                case "--no-other-items":
                    skipOther = true;
                    break;
                case "--folder" when i + 1 < args.Length:
                    folder = args[++i];
                    break;
                case "--to" or "--format" when i + 1 < args.Length && (a == "--to") == (verb == "convert"):
                    if (!TryFormat(args[++i], out ConvertFormat f))
                    {
                        error.WriteLine(verb + ": unknown format " + args[i]);
                        return 2;
                    }

                    format = f;
                    break;
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal))
                    {
                        error.WriteLine(verb + ": unknown option " + a);
                        error.WriteLine(usage);
                        return 2;
                    }

                    paths.Add(a);
                    break;
            }
        }

        if (paths.Count != 2)
        {
            error.WriteLine(usage);
            return 2;
        }

        string input = paths[0], dest = paths[1];
        if (format is null && !FromExtension(dest, out format))
        {
            error.WriteLine("convert: cannot tell the output format from '" + dest + "'; give --to");
            return 2;
        }

        ConvertFormat target = format!.Value;

        if (!File.Exists(input) && !Directory.Exists(input))
        {
            error.WriteLine(verb + ": no such file or directory: " + input);
            return 1;
        }

        var options = new StoreConversionOptions { FolderFilter = folder, OtherItems = skipOther ? OtherItemsMode.Skip : OtherItemsMode.Route };
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        var converter = new StoreConverter(options);
        var progress = new SyncProgress(r => WriteItem(r, json, output));
        try
        {
            StoreConversionReport report = converter.ConvertAsync(input, target, dest, json ? progress : null, cts.Token).GetAwaiter().GetResult();
            WriteSummary(report, target, dest, json, output);
            return report.Errors.Count > 0 ? 1 : 0;
        }
        catch (OperationCanceledException)
        {
            error.WriteLine(verb + ": cancelled after " + converter.Report.ItemsRead.ToString(CultureInfo.InvariantCulture) + " items; what was written is complete");
            return 130;
        }
        catch (IOException e)
        {
            error.WriteLine(verb + ": " + e.Message);
            return 1;
        }
    }

    private sealed class SyncProgress(Action<StoreItemResult> a) : IProgress<StoreItemResult>
    {
        public void Report(StoreItemResult value) => a(value);
    }

    private static bool TryFormat(string name, out ConvertFormat format)
    {
        switch (name.ToLowerInvariant())
        {
            case "eml":
                format = ConvertFormat.Eml;
                return true;
            case "mbox":
                format = ConvertFormat.Mbox;
                return true;
            case "maildir":
                format = ConvertFormat.Maildir;
                return true;
            case "emlx" or "applemail":
                format = ConvertFormat.AppleMail;
                return true;
            case "ics":
                format = ConvertFormat.Ics;
                return true;
            case "vcf" or "vcard":
                format = ConvertFormat.VCard;
                return true;
            case "jcal":
                format = ConvertFormat.JCal;
                return true;
            case "jcard":
                format = ConvertFormat.JCard;
                return true;
            case "xcal":
                format = ConvertFormat.XCal;
                return true;
            case "xcard":
                format = ConvertFormat.XCard;
                return true;
            case "tnef":
                format = ConvertFormat.Tnef;
                return true;
            case "msg":
                format = ConvertFormat.Msg;
                return true;
            case "pst":
                format = ConvertFormat.Pst;
                return true;
            default:
                format = default;
                return false;
        }
    }

    // Only the formats whose destination is named by an extension; eml, maildir and emlx are directories and need --to.
    private static bool FromExtension(string dest, out ConvertFormat? format)
    {
        string ext = Path.GetExtension(dest.TrimEnd('/', '\\')).TrimStart('.');
        if (ext is "mbox" or "mbx" or "ics" or "vcf" or "jcal" or "jcard" or "xcal" or "xcard" or "tnef" or "pst")
        {
            TryFormat(ext == "mbx" ? "mbox" : ext, out ConvertFormat f);
            format = f;
            return true;
        }

        format = null;
        return false;
    }

    private static void WriteItem(StoreItemResult r, bool json, TextWriter output)
    {
        if (!json)
        {
            return;
        }

        using MemoryStream ms = new();
        using (Utf8JsonWriter w = new(ms))
        {
            w.WriteStartObject();
            w.WriteString("type", "item");
            w.WriteNumber("index", r.Index);
            w.WriteString("folder", r.Folder);
            w.WriteString("subject", r.Subject);
            w.WriteString("outcome", r.Outcome.ToString());
            if (r.Reason.Length > 0)
            {
                w.WriteString("reason", r.Reason);
            }

            w.WriteString("location", r.Location);
            w.WriteStartArray("losses");
            foreach (LossEntry e in r.Losses)
            {
                w.WriteStartObject();
                w.WriteString("family", e.Family);
                w.WriteString("name", e.Name);
                w.WriteString("kind", e.Kind.ToString());
                w.WriteString("code", e.Code);
                w.WriteString("path", e.Path);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        output.WriteLine(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
        output.Flush();
    }

    private static void WriteSummary(StoreConversionReport r, ConvertFormat format, string dest, bool json, TextWriter output)
    {
        if (json)
        {
            using MemoryStream ms = new();
            using (Utf8JsonWriter w = new(ms))
            {
                w.WriteStartObject();
                w.WriteString("type", "summary");
                w.WriteString("format", format.ToString());
                w.WriteString("destination", dest);
                w.WriteNumber("read", r.ItemsRead);
                w.WriteNumber("written", r.ItemsWritten);
                w.WriteNumber("skipped", r.ItemsSkipped);
                w.WriteNumber("withContentLoss", r.ItemsWithContentLoss);
                w.WriteStartObject("skippedByReason");
                foreach ((string k, long v) in r.SkippedByReason.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    w.WriteNumber(k, v);
                }

                w.WriteEndObject();
                w.WriteStartObject("folders");
                foreach ((string k, FolderCount v) in r.Folders)
                {
                    w.WriteStartObject(k);
                    w.WriteNumber("read", v.Read);
                    w.WriteNumber("written", v.Written);
                    w.WriteEndObject();
                }

                w.WriteEndObject();
                w.WriteStartArray("losses");
                foreach (LossTotal t in r.Losses)
                {
                    w.WriteStartObject();
                    w.WriteString("family", t.Family);
                    w.WriteString("code", t.Code);
                    w.WriteString("kind", t.Kind.ToString());
                    w.WriteNumber("items", t.Items);
                    w.WriteString("example", t.Example);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteStartArray("errors");
                foreach (string e in r.Errors)
                {
                    w.WriteStringValue(e);
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            output.WriteLine(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
            return;
        }

        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"read {r.ItemsRead} items, wrote {r.ItemsWritten} as {format} to {dest}, skipped {r.ItemsSkipped}, {r.ItemsWithContentLoss} with something that did not carry over"));
        foreach ((string k, long v) in r.SkippedByReason.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  skipped {v} {k}"));
        }

        if (r.Folders.Count > 1 || r.Folders.Keys.Any(k => k.Length > 0))
        {
            foreach ((string k, FolderCount v) in r.Folders)
            {
                output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  folder {(k.Length == 0 ? "(top)" : k)}: {v.Read} read, {v.Written} written"));
            }
        }

        foreach (LossTotal t in r.Losses.Where(t => t.Kind != LossKind.Internal).Take(30))
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {t.Kind} {t.Code} ({t.Family}) in {t.Items} items, for example {t.Example}"));
        }

        foreach (string e in r.Errors.Take(10))
        {
            output.WriteLine("  error: " + e);
        }
    }
}
