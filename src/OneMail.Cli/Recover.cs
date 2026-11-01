using System.Globalization;
using System.Text;
using System.Text.Json;
using RecursiveAI.OneMail.Conversion.Stores;
using RecursiveAI.OneMail.Pst.Recovery;
using RecursiveAI.OneMail.Recovery;
using RecursiveAI.OneMail.Sources;
using RecursiveAI.OneMail.Stores.Dbx;
using RecursiveAI.OneMail.Stores.Olm;

namespace RecursiveAI.OneMail.Cli;

/// <summary>
/// <c>onemail recover</c>: opt-in recovery from Outlook Express databases (deleted and orphaned messages) and damaged Outlook for Mac archives, writing
/// every recovered item as an EML (or MSG) file and a <c>recovery-report.json</c> beside them that says, for each, where in the source it came from.
/// Reads the input only; never writes to it.
/// </summary>
internal static class RecoverCommand
{
    public const string Usage = "usage: onemail recover [--format eml|msg] [--include-duplicates] [--forensic] [--pst] <input.dbx | store directory | input.olm | input.pst | input.ost> <output directory>";

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        bool msg = false, dupes = false, forensic = false, pst = false;
        List<string> paths = [];
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--format" when i + 1 < args.Length:
                    string f = args[++i].ToLowerInvariant();
                    if (f is not ("eml" or "msg"))
                    {
                        error.WriteLine("recover: unknown format " + f);
                        return 2;
                    }

                    msg = f == "msg";
                    break;
                case "--include-duplicates":
                    dupes = true;
                    break;
                case "--forensic":
                    forensic = true;
                    break;
                case "--pst":
                    pst = true;
                    break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                    {
                        error.WriteLine("recover: unknown option " + args[i]);
                        error.WriteLine(Usage);
                        return 2;
                    }

                    paths.Add(args[i]);
                    break;
            }
        }

        if (paths.Count != 2)
        {
            error.WriteLine(Usage);
            return 2;
        }

        string input = paths[0], dest = paths[1];
        if (!File.Exists(input) && !Directory.Exists(input))
        {
            error.WriteLine("recover: no such file or directory: " + input);
            return 1;
        }

        // The output must not be inside the input: recovery never writes to what it reads.
        string full = Path.GetFullPath(dest).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullIn = Path.GetFullPath(input).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (full.StartsWith(fullIn, StringComparison.Ordinal) || Path.GetFullPath(input) == Path.GetFullPath(dest))
        {
            error.WriteLine("recover: the output directory must be outside the input");
            return 2;
        }

        Directory.CreateDirectory(dest);
        if (File.Exists(input) && (pst || IsPst(input)))
        {
            return RecoverPst(input, dest, forensic, output, error);
        }

        using MemoryStream ms = new();
        int written = 0;
        string format;
        using (Utf8JsonWriter w = new(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("input", input);
            try
            {
                if (File.Exists(input) && IsOlm(input))
                {
                    format = "olm";
                    w.WriteString("format", format);
                    written = RecoverOlm(input, dest, msg, w, error);
                }
                else
                {
                    format = "dbx";
                    w.WriteString("format", format);
                    written = RecoverDbx(input, dest, msg, dupes, w, error);
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                error.WriteLine("recover: " + e.Message);
                return 1;
            }

            w.WriteEndObject();
        }

        File.WriteAllBytes(Path.Combine(dest, "recovery-report.json"), ms.ToArray());
        output.WriteLine("recovered " + written.ToString(CultureInfo.InvariantCulture) + " item(s) from " + input + "; report: " + Path.Combine(dest, "recovery-report.json"));
        return 0;
    }

    // A PST or OST starts with "!BDN". A file whose header is destroyed does not, so --pst names it by hand.
    private static bool IsPst(string path)
    {
        using FileSource s = FileSource.Open(path);
        Span<byte> head = stackalloc byte[4];
        return s.ReadFully(0, head) == 4 && head.SequenceEqual("!BDN"u8);
    }

    // Recovery writes a new PST of what it found and the report beside it; the source is only read.
    private static int RecoverPst(string input, string dest, bool forensic, TextWriter output, TextWriter error)
    {
        try
        {
            if (!PstRecovery.TryOpen(input, out PstRecovery? r, new PstRecoveryOptions { Forensic = forensic }) || r is null)
            {
                error.WriteLine("recover: no page or block of a PST or OST was found in " + input);
                return 1;
            }

            using (r)
            {
                string pst = Path.Combine(dest, "recovered.pst");
                string report = Path.Combine(dest, "recovery-report.json");
                PstRepairSummary s = r.RepairTo(pst, report);
                output.WriteLine("recovered " + s.Written.ToString(CultureInfo.InvariantCulture) + " item(s) from " + input + " into " + pst + (s.Failed > 0 ? " (" + s.Failed.ToString(CultureInfo.InvariantCulture) + " could not be copied)" : string.Empty) + "; report: " + report);
                return 0;
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            error.WriteLine("recover: " + e.Message);
            return 1;
        }
    }

    private static bool IsOlm(string path)
    {
        using FileSource s = FileSource.Open(path);
        Span<byte> head = stackalloc byte[4];
        return s.ReadFully(0, head) == 4 && head.SequenceEqual("PK\x03\x04"u8);
    }

    // ---- DBX ----

    private static int RecoverDbx(string input, string dest, bool msg, bool dupes, Utf8JsonWriter w, TextWriter error)
    {
        IEnumerable<string> files = Directory.Exists(input)
            ? Directory.EnumerateFiles(input, "*.dbx", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }).Order(StringComparer.Ordinal)
            : [input];
        int total = 0;
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        w.WriteStartArray("items");
        foreach (string file in files)
        {
            using FileSource src = FileSource.Open(file);
            var reader = new DbxReader(src);
            if (reader.Kind != DbxFileKind.MessageDatabase)
            {
                continue;
            }

            string baseName = Path.GetFileNameWithoutExtension(file);
            string dir = Path.Combine(dest, Sanitize(baseName));
            int n = 0;
            foreach (DbxRecoveredMessage m in reader.RecoverDeleted(new DbxRecoveryOptions { IncludeDuplicatesOfLive = dupes }))
            {
                Directory.CreateDirectory(dir);
                string name = (++n).ToString("D4", CultureInfo.InvariantCulture) + "-" + m.Provenance.Status + (msg ? ".msg" : ".eml");
                byte[]? bytes = msg ? ConvertToMsg(m.ToMailItem()) : m.Content.ToArray();
                if (bytes is null)
                {
                    error.WriteLine("recover: " + name + ": no MSG form for this item; written as EML");
                    name = Path.ChangeExtension(name, ".eml");
                    bytes = m.Content.ToArray();
                }

                File.WriteAllBytes(Path.Combine(dir, name), bytes);
                string status = m.Provenance.Status.ToString();
                counts[status] = counts.GetValueOrDefault(status) + 1;
                WriteItem(w, Path.Combine(Sanitize(baseName), name), Path.GetFileName(file), m.Provenance, m.Content.Length, null);
                total++;
            }
        }

        w.WriteEndArray();
        WriteSummary(w, counts);
        return total;
    }

    // ---- OLM ----

    private static int RecoverOlm(string input, string dest, bool msg, Utf8JsonWriter w, TextWriter error)
    {
        using OlmArchive olm = OlmArchive.Open(input, new OlmReaderOptions { Recovery = true });
        int total = 0, n = 0;
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        w.WriteStartArray("items");
        foreach (OlmItem item in olm.Enumerate())
        {
            n++;
            RecoveryProvenance p = item.Recovery!;
            ConvertFormat target = item.OlmKind switch
            {
                OlmItemKind.Email => msg ? ConvertFormat.Msg : ConvertFormat.Eml,
                OlmItemKind.Appointment or OlmItemKind.Task or OlmItemKind.Note => ConvertFormat.Ics,
                OlmItemKind.Contact or OlmItemKind.Group => ConvertFormat.VCard,
                _ => ConvertFormat.Eml,
            };
            if (item.OlmKind == OlmItemKind.Category)
            {
                continue;
            }

            byte[]? bytes;
            try
            {
                bytes = StoreConverter.ConvertItem(item, target).Value;
            }
            catch (Exception e) when (e is IOException or InvalidDataException)
            {
                error.WriteLine("recover: item " + n.ToString(CultureInfo.InvariantCulture) + " in " + item.Entry + ": " + e.Message);
                bytes = null;
            }

            string? file = null;
            if (bytes is not null)
            {
                string ext = target switch { ConvertFormat.Msg => ".msg", ConvertFormat.Ics => ".ics", ConvertFormat.VCard => ".vcf", _ => ".eml" };
                string dir = Path.Combine(dest, Sanitize(item.Folder.Length == 0 ? "(root)" : item.Folder));
                Directory.CreateDirectory(dir);
                file = n.ToString("D5", CultureInfo.InvariantCulture) + "-" + p.Status + ext;
                File.WriteAllBytes(Path.Combine(dir, file), bytes);
                file = Path.Combine(Sanitize(item.Folder.Length == 0 ? "(root)" : item.Folder), file);
                total++;
            }

            string status = p.Status.ToString();
            counts[status] = counts.GetValueOrDefault(status) + 1;
            WriteItem(w, file, item.Entry, p, bytes?.Length ?? 0, item.Subject);
        }

        w.WriteEndArray();
        w.WriteStartArray("damage");
        foreach (OlmDamage d in olm.Damage)
        {
            w.WriteStartObject();
            w.WriteString("kind", d.Kind.ToString());
            w.WriteNumber("offset", d.Offset);
            w.WriteNumber("length", d.Length);
            if (d.Entry is not null)
            {
                w.WriteString("entry", d.Entry);
            }

            w.WriteString("reason", d.Reason);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteStartArray("defects");
        foreach (OlmDefect d in olm.Defects)
        {
            w.WriteStringValue(d.ToString());
        }

        w.WriteEndArray();
        WriteSummary(w, counts);
        return total;
    }

    // ---- shared ----

    private static byte[]? ConvertToMsg(IMailItem item) => StoreConverter.ConvertItem(item, ConvertFormat.Msg).Value;

    private static void WriteItem(Utf8JsonWriter w, string? file, string source, RecoveryProvenance p, long bytes, string? subject)
    {
        w.WriteStartObject();
        if (file is null)
        {
            w.WriteNull("file");
        }
        else
        {
            w.WriteString("file", file.Replace('\\', '/'));
        }

        w.WriteString("source", source);
        w.WriteString("status", p.Status.ToString());
        if (subject is not null)
        {
            w.WriteString("subject", subject);
        }

        w.WriteNumber("bytes", bytes);
        if (p.Reason is not null)
        {
            w.WriteString("reason", p.Reason);
        }

        WriteRanges(w, "ranges", p.Ranges);
        WriteRanges(w, "lost", p.Lost);
        w.WriteStartArray("defects");
        foreach (MailDefect d in p.Defects)
        {
            w.WriteStartObject();
            w.WriteString("family", d.Family);
            w.WriteString("code", d.Code);
            w.WriteNumber("offset", d.Offset);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteRanges(Utf8JsonWriter w, string name, IReadOnlyList<ByteRange> ranges)
    {
        w.WriteStartArray(name);
        foreach (ByteRange r in ranges)
        {
            w.WriteStartObject();
            w.WriteNumber("offset", r.Offset);
            w.WriteNumber("length", r.Length);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static void WriteSummary(Utf8JsonWriter w, SortedDictionary<string, int> counts)
    {
        w.WriteStartObject("summary");
        foreach (KeyValuePair<string, int> kv in counts)
        {
            w.WriteNumber(kv.Key, kv.Value);
        }

        w.WriteEndObject();
    }

    private static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ' ' or '/' ? c : '_');
        }

        string s = sb.ToString().Trim('/', ' ', '.');
        return s.Length == 0 ? "_" : s.Replace("..", "_", StringComparison.Ordinal);
    }
}
