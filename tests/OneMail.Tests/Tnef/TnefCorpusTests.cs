using RecursiveAI.OneMail.Tnef;

namespace RecursiveAI.OneMail.Tests.Tnef;

/// <summary>
/// The public TNEF data files fetched by tools/corpus/fetch-tnef.sh, when they are present: the attribute layer writes back to the bytes it read,
/// the MAPI layer is property-exact through write and read, and the file that exceeds the limits is refused with typed defects.
/// </summary>
public class TnefCorpusTests
{
    private static string? Dir()
    {
        string root = Environment.GetEnvironmentVariable("ONEMAIL_CORPUS") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "onemail", "corpus");
        string dir = Path.Combine(root, "tnef");
        return Directory.Exists(dir) ? dir : null;
    }

    [Fact]
    public void CorpusFilesRoundTripAtBothLayers()
    {
        string? dir = Dir();
        Assert.SkipWhen(dir is null, "the TNEF corpus is not fetched (tools/corpus/fetch-tnef.sh)");
        int n = 0;
        foreach (string path in Directory.EnumerateFiles(dir!, "*", SearchOption.AllDirectories))
        {
            byte[] bytes = File.ReadAllBytes(path);
            TnefDocument d = TnefDocument.Read(bytes);
            if (!d.HasSignature)
            {
                continue;
            }

            using var ms = new MemoryStream();
            d.WriteTo(ms);
            Assert.True(ms.ToArray().AsSpan().SequenceEqual(bytes), Path.GetFileName(path) + ": attribute layer differs");

            TnefMessage first = TnefMessage.Read(d);
            using var w = new MemoryStream();
            TnefMessageWriter.Write(first, w);
            TnefMessage second = TnefMessage.Read(w.ToArray());
            List<string> diffs = TnefTestHelpers.Differences(first, second);
            Assert.True(diffs.Count == 0, Path.GetFileName(path) + ": " + string.Join("; ", diffs.Take(3)));
            if (Path.GetFileName(path) == "oom.tnef")
            {
                Assert.Contains(first.TnefDefects, x => x.Kind == TnefDefectKind.LimitReached);
            }

            n++;
        }

        Assert.True(n > 0);
    }
}
