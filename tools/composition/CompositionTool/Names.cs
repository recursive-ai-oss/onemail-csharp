using System.Text;
using System.Text.Json;
using RecursiveAI.OneMail;
using RecursiveAI.OneMail.Composition;

namespace CompositionTool;

// CompositionTool names OUT [--count N] [--seed S]
// Composes N messages whose display names, subjects and file names are not ASCII, half as ASCII headers (encoded-words and RFC 2231
// parameters) and half as raw UTF-8 (RFC 6532). They go to OUT/eml/NNNNNN.eml with what each should read back as in OUT/expected/NNNNNN.json
// (check_names.py compares that with the black-box parsers), and every message is also read back by OneMail itself.
internal static class Names
{
    private static readonly string[] Words =
    [
        "Müller", "Jörg", "Zoë", "Ünal", "Åsa", "Łukasz", "Çelik", "naïve", "façade", "Straße",
        "Иван", "Петров", "Ελένη", "Παπαδόπουλος", "محمد", "אבי", "日本語", "名前", "田中", "太郎", "김민준", "😀", "👩‍💻", "ñandú", "Őrs",
        "Smith", "Jones", "Dr.", "Jr.", "O'Brien", "Anne-Marie", "van der Berg", "(work)", "\"Z\"", "@home", "<x>", "a,b", "semi;colon", "back\\slash",
    ];

    private static readonly string[] Extensions = [".pdf", ".txt", ".docx", ".png", ".tar.gz", ""];

    public static int Run(string[] args)
    {
        string? outDir = null;
        int count = 200;
        int seed = 2026;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--count")
            {
                count = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (args[i] == "--seed")
            {
                seed = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                outDir = args[i];
            }
        }

        if (outDir is null)
        {
            Console.Error.WriteLine("usage: CompositionTool names OUT [--count N] [--seed S]");
            return 2;
        }

        Directory.CreateDirectory(Path.Combine(outDir, "eml"));
        Directory.CreateDirectory(Path.Combine(outDir, "expected"));
        var rng = new Random(seed);
        int bad = 0;
        for (int n = 0; n < count; n++)
        {
            bool utf8 = n >= count / 2;
            string Phrase(int min, int max)
            {
                int k = rng.Next(min, max + 1);
                var parts = new List<string>();
                for (int j = 0; j < k; j++)
                {
                    parts.Add(Words[rng.Next(Words.Length)]);
                }

                return string.Join(' ', parts);
            }

            // A name must keep at least one non-ASCII word, and is never blank or edge-spaced.
            string Name() => Phrase(1, 6) + " " + Words[rng.Next(0, 25)];
            (string Name, string Addr) From = (Name(), $"from{n}@example.org");
            var to = Enumerable.Range(0, rng.Next(1, 4)).Select(j => (Name: Name(), Addr: $"to{n}x{j}@example.org")).ToList();
            var cc = Enumerable.Range(0, rng.Next(0, 3)).Select(j => (Name: Name(), Addr: $"cc{n}x{j}@example.org")).ToList();
            string subject = Phrase(2, 14) + " " + Words[rng.Next(0, 25)];
            var files = Enumerable.Range(0, rng.Next(1, 3)).Select(j => Phrase(1, 8).Replace('\\', '_').Replace("\"", "'", StringComparison.Ordinal) + " " + Words[rng.Next(0, 25)] + Extensions[rng.Next(Extensions.Length)]).ToList();

            static string Quote(string name, string addr) => "\"" + name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\" <" + addr + ">";
            var msg = new OutgoingMessage
            {
                From = Quote(From.Name, From.Addr),
                To = [.. to.Select(t => Quote(t.Name, t.Addr))],
                Cc = [.. cc.Select(t => Quote(t.Name, t.Addr))],
                Subject = subject,
                Text = "Hello",
                Attachments = [.. files.Select((f, j) => new OutgoingAttachment(f, new byte[] { (byte)n, (byte)j }, "application/octet-stream"))],
                Date = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
                MessageId = $"names{n}@example.org",
                Utf8Headers = utf8,
            };

            byte[] bytes = msg.ToEntity().ToArray();
            string id = n.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            File.WriteAllBytes(Path.Combine(outDir, "eml", id + ".eml"), bytes);
            var expected = new
            {
                utf8,
                from = new { name = Norm(From.Name), addr = From.Addr },
                to = to.Select(t => new { name = Norm(t.Name), addr = t.Addr }).ToArray(),
                cc = cc.Select(t => new { name = Norm(t.Name), addr = t.Addr }).ToArray(),
                subject = Norm(subject),
                filenames = files.Select(Norm).ToArray(),
            };
            File.WriteAllText(Path.Combine(outDir, "expected", id + ".json"), JsonSerializer.Serialize(expected), new UTF8Encoding(false));

            // OneMail's own reader: the exact strings, not just the whitespace-normalised ones.
            IMailItem item = Mail.Parse(bytes);
            bool ok = item.Subject == subject && item.From.Single() == new MailAddress(From.Name, From.Addr)
                && item.ToRecipients.Select(a => (a.Name, a.Address)).SequenceEqual(to.Select(t => ((string?)t.Name, t.Addr)))
                && item.Cc.Select(a => (a.Name, a.Address)).SequenceEqual(cc.Select(t => ((string?)t.Name, t.Addr)))
                && item.Attachments.Select(a => a.FileName).SequenceEqual(files);
            if (!ok)
            {
                bad++;
                Console.WriteLine($"MISMATCH {id} ({(utf8 ? "utf8" : "ascii")}): subject {item.Subject}");
            }
        }

        Console.WriteLine($"composed {count} messages ({count - count / 2} ascii-header, {count / 2} utf-8-header); read back differently by OneMail: {bad}");
        return bad == 0 ? 0 : 1;
    }

    private static string Norm(string s) => string.Join(' ', s.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
}
