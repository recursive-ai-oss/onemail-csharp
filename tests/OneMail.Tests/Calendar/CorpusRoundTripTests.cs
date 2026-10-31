using System.Runtime.CompilerServices;
using RecursiveAI.OneMail.Calendar;

namespace RecursiveAI.OneMail.Tests.Calendar;

/// <summary>
/// Every committed ICS and vCard file (RFC example blocks and generated fixtures, malformed ones included) parses and
/// writes back byte for byte. The fetched public corpus is checked by tools/calendar (CalendarDump roundtrip) on the
/// same rule.
/// </summary>
public class CorpusRoundTripTests
{
    private static string DataRoot([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "testdata", "calendar"));

    public static IEnumerable<object[]> Files() =>
        Directory.EnumerateFiles(DataRoot(), "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".ics", StringComparison.Ordinal) || f.EndsWith(".vcf", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(f => new object[] { Path.GetRelativePath(DataRoot(), f).Replace('\\', '/') });

    [Theory]
    [MemberData(nameof(Files))]
    public void WritesBackByteIdentical(string relative)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(DataRoot(), relative));
        ContentDocument d = ContentDocument.Parse(data);
        Assert.Equal(data, d.ToArray());
    }

    [Fact]
    public void CorpusIsPresent() => Assert.True(Files().Count() > 400);
}
