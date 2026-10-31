using RecursiveAI.OneMail.Stores.Olm;

namespace RecursiveAI.OneMail.Tests.Olm;

/// <summary>
/// The generated archive in testdata/olm (written by tools/olm/make_olm.py, seed 1) and, when fetched by tools/corpus/fetch-olm.sh,
/// the two public sample archives. Their contents were checked against the released olmreader jar and the olm-reader npm package
/// (tools/olm/run.sh); here the counts and a few values pin them.
/// </summary>
public class OlmCorpusTests
{
    private static string Generated([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "testdata", "olm", "generated.olm"));

    private static string? Sample(string name)
    {
        string root = Environment.GetEnvironmentVariable("ONEMAIL_CORPUS") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "onemail", "corpus");
        string path = Path.Combine(root, "olm", name);
        return File.Exists(path) ? path : null;
    }

    private static Dictionary<OlmItemKind, int> Counts(string path, out OlmArchive archive)
    {
        archive = OlmArchive.Open(path);
        var c = new Dictionary<OlmItemKind, int>();
        foreach (OlmItem it in archive.Enumerate())
        {
            c[it.OlmKind] = c.GetValueOrDefault(it.OlmKind) + 1;
            _ = it.Attachments;
            _ = it.OlmDefects;
        }

        return c;
    }

    [Fact]
    public void GeneratedArchiveReadsEveryKind()
    {
        Dictionary<OlmItemKind, int> c = Counts(Generated(), out OlmArchive olm);
        using (olm)
        {
            Assert.Equal(40, c[OlmItemKind.Email]);
            Assert.Equal(4, c[OlmItemKind.Appointment]);
            Assert.Equal(3, c[OlmItemKind.Contact]);
            Assert.Equal(2, c[OlmItemKind.Task]);
            Assert.Equal(1, c[OlmItemKind.Note]);
            Assert.Equal(1, c[OlmItemKind.Group]);
            Assert.Equal(3, c[OlmItemKind.Category]);
            Assert.DoesNotContain(olm.Defects, d => true);
            // Non-ASCII, nested folders and a raw source all survive.
            Assert.Contains(olm.GetFolders(), f => f.Path == "Inbox/Projects/2019 ☀" && f.IsMail);
            Assert.Contains(olm.Enumerate(), i => i.Subject == "日本語の件名 #2");
            Assert.Contains(olm.Enumerate(), i => i.MimeSource is { } s && s.StartsWith("From: a@example.org", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void SampleArchivesHaveTheCountsTheOraclesSaw()
    {
        string? a = Sample("outlook_example.olm");
        string? b = Sample("bill_rapp.olm");
        Assert.SkipWhen(a is null || b is null, "the OLM sample archives are not fetched (tools/corpus/fetch-olm.sh)");
        Dictionary<OlmItemKind, int> c = Counts(a!, out OlmArchive olm1);
        using (olm1)
        {
            Assert.Equal((7, 8, 5, 5, 1, 2, 10), (c[OlmItemKind.Email], c[OlmItemKind.Appointment], c[OlmItemKind.Contact], c[OlmItemKind.Task], c[OlmItemKind.Note], c[OlmItemKind.Group], c[OlmItemKind.Category]));
            Assert.DoesNotContain(olm1.Defects, d => true);
        }

        c = Counts(b!, out OlmArchive olm2);
        using (olm2)
        {
            Assert.Equal((483, 1, 8), (c[OlmItemKind.Email], c[OlmItemKind.Contact], c[OlmItemKind.Category]));
            Assert.DoesNotContain(olm2.Defects, d => true);
            Assert.Equal(59, olm2.Enumerate().Sum(i => i.Attachments.Count));
            Assert.Equal(MailFormat.Olm, MailFormatSniffer.Sniff(new Sources.MemorySource(File.ReadAllBytes(b!))));
        }
    }
}
