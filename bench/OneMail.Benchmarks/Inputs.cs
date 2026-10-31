namespace RecursiveAI.OneMail.Benchmarks;

/// <summary>
/// The messages the MIME rows run over, picked deterministically from the fetched corpora (see tools/corpus/fetch.sh) plus
/// one generated message with a large attachment. Corpus mail is never written anywhere by the benchmarks.
/// </summary>
internal static class Inputs
{
    public static readonly string[] Names = ["enron-2k", "enron-20k", "dovecot-multipart", "generated-attachment-6m"];

    private static string Corpus => Environment.GetEnvironmentVariable("ONEMAIL_CORPUS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "onemail", "corpus");

    /// <summary>The raw bytes of the named input.</summary>
    public static byte[] Get(string name) => name switch
    {
        "enron-2k" => File.ReadAllBytes(PickEnron(1_500, 2_500)),
        "enron-20k" => File.ReadAllBytes(PickEnron(15_000, 25_000)),
        "dovecot-multipart" => PickDovecotMultipart(),
        "generated-attachment-6m" => Generated(),
        _ => throw new ArgumentException("unknown input " + name, nameof(name)),
    };

    /// <summary>The Dovecot test mbox as one file.</summary>
    public static byte[] DovecotMbox() => File.ReadAllBytes(Path.Combine(Corpus, "dovecot-crlf"));

    // First file by sorted path whose size is in range: the same file on every machine that has the same sample.
    private static string PickEnron(int min, int max)
    {
        string root = Path.Combine(Corpus, "enron-sample", "maildir");
        foreach (string f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            long len = new FileInfo(f).Length;
            if (len >= min && len <= max)
            {
                return f;
            }
        }

        throw new InvalidOperationException("no Enron sample message of " + min + ".." + max + " bytes under " + root);
    }

    // The first message of 40-300 KB that declares a multipart in its headers.
    private static byte[] PickDovecotMultipart()
    {
        string dir = Path.Combine(Corpus, "dovecot-crlf.msgs");
        foreach (string f in Directory.EnumerateFiles(dir).Order(StringComparer.Ordinal))
        {
            long len = new FileInfo(f).Length;
            if (len < 40_000 || len > 300_000)
            {
                continue;
            }

            byte[] b = File.ReadAllBytes(f);
            int head = Math.Min(b.Length, 8192);
            if (System.Text.Encoding.Latin1.GetString(b, 0, head).Contains("multipart/", StringComparison.OrdinalIgnoreCase))
            {
                return b;
            }
        }

        throw new InvalidOperationException("no multipart Dovecot message of 40..300 KB under " + dir);
    }

    // A text body and a 6 MB base64 attachment, CRLF, 76-column lines; fixed seed.
    private static byte[] Generated()
    {
        var rng = new Random(20261031);
        byte[] raw = new byte[4_500_000];
        rng.NextBytes(raw);
        string b64 = Convert.ToBase64String(raw, Base64FormattingOptions.InsertLineBreaks).Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
        string msg = "From: Alice Example <alice@example.org>\r\nTo: Bob <bob@example.net>, carol@example.com\r\nSubject: Quarterly figures\r\n"
            + "Date: Fri, 30 Oct 2026 10:00:00 +0000\r\nMessage-ID: <gen-1@example.org>\r\nMIME-Version: 1.0\r\n"
            + "Content-Type: multipart/mixed; boundary=\"gen-boundary\"\r\n\r\n--gen-boundary\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nSee the attachment.\r\n"
            + "--gen-boundary\r\nContent-Type: application/octet-stream; name=\"data.bin\"\r\nContent-Transfer-Encoding: base64\r\n"
            + "Content-Disposition: attachment; filename=\"data.bin\"\r\n\r\n" + b64 + "\r\n--gen-boundary--\r\n";
        return System.Text.Encoding.Latin1.GetBytes(msg);
    }
}
