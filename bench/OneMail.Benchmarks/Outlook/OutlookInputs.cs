namespace RecursiveAI.OneMail.Benchmarks;

/// <summary>
/// The Outlook-format files the rows run over. Real files come from the corpora that <c>tools/corpus/fetch-*.sh</c> fetches (never
/// committed); the larger stores are written by <c>OneMail.Benchmarks gen DIR</c> with the test-side PST writers. Names are stable
/// across machines so rows line up.
/// </summary>
internal static class OutlookInputs
{
    /// <summary>The real PST/OST files that are present on this machine (the CFReDS OST needs a 7-Zip and about 20 GB to fetch).</summary>
    public static string[] PstReal => [.. s_real.Where(Exists)];

    public static string[] PstSmallGenerated => [.. s_small.Where(Exists)];

    public static string[] PstLargeGenerated => [.. s_large.Where(Exists)];

    /// <summary>The 4 KiB-page stores: OneMail only, because XstReader.Api 1.0.6 throws on them (see the method notes in docs/benchmarks.md).</summary>
    public static string[] PstFourKiB => [.. s_k4.Where(Exists)];

    private static string Corpus => Environment.GetEnvironmentVariable("ONEMAIL_CORPUS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "onemail", "corpus");

    /// <summary>Where <c>gen</c> writes and the rows read the generated stores.</summary>
    public static string Stores => Environment.GetEnvironmentVariable("ONEMAIL_STORES")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "onemail", "u17", "stores");

    /// <summary>The path of the named PST/OST input.</summary>
    public static string PstPath(string name) => name switch
    {
        "cfreds-ost" => FirstFile(Path.Combine(Corpus, "pst", "cfreds-leakage"), "*.ost"),
        "tika-pst" => Path.Combine(Corpus, "pst", "tika", "testPST.pst"),
        "tika-bodytypes" => Path.Combine(Corpus, "pst", "tika", "testPST_variousBodyTypes.pst"),
        _ when name.StartsWith("gen-", StringComparison.Ordinal) => Path.Combine(Stores, name + ".pst"),
        _ => throw new ArgumentException("unknown PST input " + name, nameof(name)),
    };

    /// <summary>Every MSG in the corpus, in path order.</summary>
    public static string[] MsgFiles() => Directory.Exists(Path.Combine(Corpus, "msg"))
        ? [.. Directory.EnumerateFiles(Path.Combine(Corpus, "msg"), "*.msg", SearchOption.AllDirectories).Order(StringComparer.Ordinal)]
        : [];

    /// <summary>Every TNEF file in the corpus, in path order.</summary>
    public static string[] TnefFiles() => Directory.Exists(Path.Combine(Corpus, "tnef"))
        ? [.. Directory.EnumerateFiles(Path.Combine(Corpus, "tnef"), "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)]
        : [];

    /// <summary>The TNEF row names: file names, without the one 53-byte truncated stream that has no content to read.</summary>
    public static IEnumerable<string> TnefNames => TnefFiles().Where(f => new FileInfo(f).Length > 100).Select(Path.GetFileName)!;

    /// <summary>The path of the named TNEF file.</summary>
    public static string TnefPath(string name) => TnefFiles().First(f => Path.GetFileName(f) == name);

    private static readonly string[] s_real = ["cfreds-ost", "tika-pst", "tika-bodytypes"];
    private static readonly string[] s_small = ["gen-u1k"];
    private static readonly string[] s_large = ["gen-u10k", "gen-u50k"];
    private static readonly string[] s_k4 = ["gen-k4-1k", "gen-k4-10k"];

    private static bool Exists(string name)
    {
        try
        {
            return File.Exists(PstPath(name));
        }
        catch (Exception e) when (e is FileNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    private static string FirstFile(string dir, string pattern) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories).Order(StringComparer.Ordinal).First()
            : throw new FileNotFoundException("corpus directory missing: " + dir);
}
