using RecursiveAI.OneMail.Pst.Ndb;
using RecursiveAI.OneMail.Tests.Pst;

namespace RecursiveAI.OneMail.Benchmarks;

/// <summary>
/// Writes the large stores with the test-side PST writer: deterministic mail (a fixed seed per message) spread over 20 folders, with
/// plain and HTML bodies, one to four recipients and, for most messages, an attachment. Nothing here is timed.
/// </summary>
internal static class StoreGenerator
{
    private static readonly string[] Words =
    [
        "quarterly", "report", "forecast", "meeting", "invoice", "schedule", "contract", "review", "budget", "update", "customer",
        "delivery", "approval", "draft", "attached", "regarding", "follow", "action", "items", "project", "timeline", "revision",
        "payment", "terms", "agreement", "proposal", "summary", "minutes", "agenda", "deadline", "request", "confirm",
    ];

    /// <summary>The stores <c>gen</c> writes: name, layout, message count.</summary>
    public static readonly (string Name, NdbFormat Format, int Messages)[] Specs =
    [
        ("gen-u1k", NdbFormat.Unicode, 1_000),
        ("gen-u10k", NdbFormat.Unicode, 10_000),
        ("gen-u50k", NdbFormat.Unicode, 50_000),
        ("gen-k4-1k", NdbFormat.Unicode4K, 1_000),
        ("gen-k4-10k", NdbFormat.Unicode4K, 10_000),
    ];

    /// <summary>Writes every store missing from <paramref name="dir"/> (or the ones named in <paramref name="only"/>).</summary>
    public static void Generate(string dir, IReadOnlyCollection<string> only)
    {
        Directory.CreateDirectory(dir);
        foreach ((string name, NdbFormat format, int count) in Specs)
        {
            string path = Path.Combine(dir, name + ".pst");
            if ((only.Count > 0 && !only.Contains(name)) || (only.Count == 0 && File.Exists(path)))
            {
                continue;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            NdbTestBuilder.Result r = new PstFixtureBuilder(format).Build(Tree(count), compress: false);
            File.WriteAllBytes(path, r.Bytes);
            Console.WriteLine($"{name}\t{count} messages\t{r.Bytes.Length >> 10} KiB\t{sw.Elapsed.TotalSeconds:F1} s");
        }
    }

    private static FxFolder Tree(int messages)
    {
        const int Folders = 20;
        var folders = new List<FxFolder>();
        for (int i = 0; i < Folders; i++)
        {
            folders.Add(new FxFolder { Name = i == 0 ? "Inbox" : i == 1 ? "Sent Items" : "Archive " + (i - 1).ToString("D2") });
        }

        for (int i = 0; i < messages; i++)
        {
            // Skewed: the Inbox gets a third of the mail, the rest is spread evenly.
            int f = i % 3 == 0 ? 0 : 1 + (i % (Folders - 1));
            folders[f].Messages.Add(Message(i));
        }

        return new FxFolder { Name = "Top of Test Store", Children = folders };
    }

    private static FxMessage Message(int i)
    {
        var rng = new Xorshift((uint)i + 1);
        string subject = "Re: " + Phrase(ref rng, 4 + (int)(rng.Next() % 5)) + " #" + i;
        string body = Phrase(ref rng, 150 + (int)(rng.Next() % 150));
        var recipients = new List<FxRecipient>();
        int rc = 1 + (int)(rng.Next() % 4);
        for (int r = 0; r < rc; r++)
        {
            recipients.Add(new FxRecipient { Name = "Person " + ((i + r) % 997), Smtp = "person" + ((i + r) % 997) + "@example.com", Type = 1 + (r == 0 ? 0 : r % 3) });
        }

        var attachments = new List<FxAttachment>();
        uint roll = rng.Next() % 100;
        if (roll < 70)
        {
            // Mostly a few KiB to tens of KiB; one message in 500 carries a megabyte.
            int size = i % 500 == 7 ? 1_000_000 : 2_000 + (int)(rng.Next() % 20_000);
            attachments.Add(new FxAttachment { Name = "file" + i + ".bin", Data = PstFixtures.Data(size, i) });
        }

        return new FxMessage
        {
            Subject = subject,
            Body = body,
            Html = i % 2 == 0 ? "<html><body><p>" + body + "</p></body></html>" : null,
            SenderName = "Sender " + (i % 211),
            SenderSmtp = "sender" + (i % 211) + "@example.com",
            Recipients = recipients,
            Attachments = attachments,
            // A store with no named properties has no name-to-ID map entries, which libpff refuses to open; every real store has some.
            NamedNumbers = [(PstFixtures.Set1, 0x8101, i)],
            SubmitFileTime = 133_500_000_000_000_000L + (i * 10_000_000L),
        };
    }

    private static string Phrase(ref Xorshift rng, int words)
    {
        var sb = new System.Text.StringBuilder();
        for (int w = 0; w < words; w++)
        {
            if (w > 0)
            {
                sb.Append(' ');
            }

            sb.Append(Words[rng.Next() % (uint)Words.Length]);
        }

        return sb.ToString();
    }

    private struct Xorshift(uint seed)
    {
        private uint _x = 2463534242u ^ (seed * 2654435761u);

        public uint Next()
        {
            _x ^= _x << 13;
            _x ^= _x >> 17;
            _x ^= _x << 5;
            return _x;
        }
    }
}
