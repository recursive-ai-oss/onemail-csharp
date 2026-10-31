using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Msg;
using static RecursiveAI.OneMail.Tests.Msg.MsgTestHelpers;

namespace RecursiveAI.OneMail.Tests.Msg;

/// <summary>
/// The public MSG data files fetched by tools/corpus/fetch-msg.sh, when they are present: every one opens or is refused with a defect,
/// and every one that opens re-encodes to the bytes it was read from.
/// </summary>
public class MsgCorpusTests
{
    private static string? Directory_()
    {
        string root = Environment.GetEnvironmentVariable("ONEMAIL_CORPUS") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "onemail", "corpus");
        string dir = Path.Combine(root, "msg");
        return Directory.Exists(dir) ? dir : null;
    }

    [Fact]
    public void CorpusFilesOpenAndRoundTripExactly()
    {
        string? dir = Directory_();
        Assert.SkipWhen(dir is null, "the MSG corpus is not fetched (tools/corpus/fetch-msg.sh)");
        int opened = 0;
        int refused = 0;
        foreach (string path in Directory.EnumerateFiles(dir!, "*.msg", SearchOption.AllDirectories))
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (!MsgFile.TryOpen(bytes, out MsgFile? f, out IReadOnlyList<MapiDefect> defects))
            {
                Assert.NotEmpty(defects);
                refused++;
                continue;
            }

            using (f)
            {
                f.ReadEverything();
                List<string> diffs = RoundTripDifferences(bytes, f);
                // A file that was read with a layout defect (missing stream, size mismatch ...) may not re-encode to the same bytes.
                if (!f.Defects.Any(d => d.Kind is MapiDefectKind.ValueStreamMissing or MapiDefectKind.ValueSizeMismatch or MapiDefectKind.UnreferencedStream))
                {
                    Assert.True(diffs.Count == 0, Path.GetFileName(path) + ": " + string.Join("; ", diffs.Take(3)));
                }

                opened++;
            }
        }

        Assert.True(opened > 0);
        _ = refused;
    }
}
