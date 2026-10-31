using System.Text;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.ContentLines;

namespace RecursiveAI.OneMail.Tests.Calendar;

public class ContentLineReaderTests
{
    internal static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    private static (List<(ContentLineKind Kind, string Group, string Name, string Params, string Value, bool Folded)> Lines, List<CalendarDefect> Defects) Read(
        byte[] data, ContentLineReaderOptions? options = null)
    {
        List<CalendarDefect> defects = [];
        List<(ContentLineKind, string, string, string, string, bool)> lines = [];
        ContentLineReader r = new(data, options, defects);
        while (r.TryRead(out ContentLine l))
        {
            lines.Add((l.Kind, Encoding.UTF8.GetString(l.Group), Encoding.UTF8.GetString(l.Name), Encoding.UTF8.GetString(l.Parameters),
                Encoding.UTF8.GetString(l.Value), l.IsFolded));
        }

        return (lines, defects);
    }

    private static CalendarDefectKind[] Kinds(List<CalendarDefect> d) => d.Select(x => x.Kind).ToArray();

    [Fact]
    public void ReadsGroupNameParametersAndValue()
    {
        var (lines, defects) = Read(B("item1.EMAIL;TYPE=work,home;PREF=1:a@b.example\r\n"));
        Assert.Equal((ContentLineKind.Property, "item1", "EMAIL", "TYPE=work,home;PREF=1", "a@b.example", false), lines[0]);
        Assert.Empty(defects);
    }

    [Fact]
    public void UnfoldsCrlfWithSpaceOrTabAndRemovesOnlyOneCharacter()
    {
        var (lines, _) = Read(B("SUMMARY:one\r\n two\r\n\t three\r\n  four\r\n"));
        Assert.Single(lines);
        Assert.Equal("onetwo three four", lines[0].Value);
        Assert.True(lines[0].Folded);
    }

    [Fact]
    public void FoldInsideMultiByteCharacterIsRepairedByUnfoldingBytes()
    {
        byte[] euro = B("\u20ac"); // E2 82 AC
        byte[] data = [.. B("SUMMARY:"), euro[0], .. B("\r\n "), euro[1], euro[2], .. B("\r\n")];
        var (lines, defects) = Read(data);
        Assert.Equal("\u20ac", lines[0].Value);
        Assert.Empty(defects);
    }

    [Fact]
    public void BareLineFeedIsALineEndAndReportedOnce()
    {
        var (lines, defects) = Read(B("A:1\nB:2\nC:3\n"));
        Assert.Equal(3, lines.Count);
        Assert.Equal([CalendarDefectKind.BareLineFeed], Kinds(defects));
        Assert.Equal(3, defects[0].Offset);
    }

    [Fact]
    public void BareCarriageReturnIsALineEndAndReportedOnce()
    {
        var (lines, defects) = Read(B("A:1\rB:2\rC:3\r"));
        Assert.Equal(3, lines.Count);
        Assert.Equal([CalendarDefectKind.BareCarriageReturn], Kinds(defects));
    }

    [Fact]
    public void CarriageReturnCarriageReturnLineFeedIsOneLineEndAndAFoldAfterItJoins()
    {
        // A CRLF file passed through a text-mode copy: every line ends CR CR LF.
        var (lines, defects) = Read(B("BEGIN:X\r\r\nSUMMARY:Hello\r\r\n World\r\r\nNOTE:a\r\r\r\n b\r\r\nEND:X\r\r\n"));
        Assert.Equal(4, lines.Count);
        Assert.Equal("HelloWorld", lines[1].Value);
        Assert.Equal("ab", lines[2].Value);
        Assert.Equal([CalendarDefectKind.AmbiguousDoubleCarriageReturn], Kinds(defects));
        Assert.Equal(7, defects[0].Offset);
        Assert.Equal(3, defects[0].Length);
    }

    [Fact]
    public void CarriageReturnsNotFollowedByALineFeedStayLineEnds()
    {
        var (lines, defects) = Read(B("A:1\r\rB:2\r"));
        Assert.Equal(["1", "", "2"], lines.Select(l => l.Value));
        Assert.Equal([CalendarDefectKind.BareCarriageReturn], Kinds(defects));
    }

    [Fact]
    public void DoubleCarriageReturnDocumentsWriteBackByteIdentical()
    {
        byte[] data = B("BEGIN:VCARD\r\r\nFN:Ann\r\r\n Lee\r\r\nEND:VCARD\r\r\n");
        ContentDocument d = ContentDocument.Parse(data);
        Assert.Equal(data, d.ToArray());
        Assert.Contains(d.Defects, x => x.Kind == CalendarDefectKind.AmbiguousDoubleCarriageReturn && x.IsAmbiguity);
    }

    [Fact]
    public void FoldWorksWithLfAndCr()
    {
        Assert.Equal("ab", Read(B("A:a\n b\n")).Lines[0].Value);
        Assert.Equal("ab", Read(B("A:a\r b\r")).Lines[0].Value);
    }

    [Fact]
    public void OrphanContinuationAtStartIsReadAsALine()
    {
        var (lines, defects) = Read(B(" A:1\r\nB:2\r\n"));
        Assert.Equal("A", lines[0].Name);
        Assert.Equal([CalendarDefectKind.OrphanContinuation], Kinds(defects));
    }

    [Fact]
    public void LineWithoutColonIsMalformed()
    {
        var (lines, defects) = Read(B("garbage here\r\nA:1\r\n"));
        Assert.Equal(ContentLineKind.Malformed, lines[0].Kind);
        Assert.Equal("garbage here", lines[0].Value);
        Assert.Equal([CalendarDefectKind.MissingColon], Kinds(defects));
    }

    [Fact]
    public void EmptyNameIsMalformed()
    {
        var (lines, defects) = Read(B(":value\r\n"));
        Assert.Equal(ContentLineKind.Malformed, lines[0].Kind);
        Assert.Equal([CalendarDefectKind.EmptyName], Kinds(defects));
    }

    [Fact]
    public void BlankLineIsBlank()
    {
        var (lines, defects) = Read(B("A:1\r\n\r\nB:2\r\n"));
        Assert.Equal(ContentLineKind.Blank, lines[1].Kind);
        Assert.Empty(defects);
    }

    [Fact]
    public void InvalidNameCharacterIsReportedAndKept()
    {
        var (lines, defects) = Read(B("X_FOO:1\r\n"));
        Assert.Equal("X_FOO", lines[0].Name);
        Assert.Equal([CalendarDefectKind.InvalidNameCharacter], Kinds(defects));
    }

    [Fact]
    public void WhitespaceBeforeColonIsTrimmed()
    {
        var (lines, defects) = Read(B("DTSTART :20260101\r\n"));
        Assert.Equal("DTSTART", lines[0].Name);
        Assert.Equal([CalendarDefectKind.WhitespaceAroundDelimiter], Kinds(defects));
    }

    [Fact]
    public void ColonInsideQuotedParameterDoesNotEndTheParameters()
    {
        var (lines, _) = Read(B("ATTENDEE;DELEGATED-FROM=\"mailto:a@b\";CN=x:mailto:c@d\r\n"));
        Assert.Equal("DELEGATED-FROM=\"mailto:a@b\";CN=x", lines[0].Params);
        Assert.Equal("mailto:c@d", lines[0].Value);
    }

    [Fact]
    public void UnterminatedQuoteIsAnOrdinaryCharacter()
    {
        var (lines, defects) = Read(B("X;CN=\"Bob:value\r\n"));
        Assert.Equal("CN=\"Bob", lines[0].Params);
        Assert.Equal("value", lines[0].Value);
        Assert.Equal([CalendarDefectKind.UnterminatedQuote], Kinds(defects));
    }

    [Fact]
    public void ParameterEnumeratorHandlesBareQuotedAndMultipleValues()
    {
        byte[] p = B("TYPE=work,\"a,b\";WORK;;CN=\"x;y\";EMPTY=");
        List<string> seen = [];
        foreach (ContentParameterSpan s in new ContentParameterEnumerator(p))
        {
            List<string> vals = [];
            foreach (ReadOnlySpan<byte> v in s.EnumerateValues())
            {
                vals.Add(Encoding.UTF8.GetString(v));
            }

            seen.Add(Encoding.UTF8.GetString(s.Name) + (s.HasValue ? "=" + string.Join("|", vals) : string.Empty));
        }

        Assert.Equal(["TYPE=work|a,b", "WORK", "CN=x;y", "EMPTY="], seen);
    }

    [Fact]
    public void QuotedPrintableSoftBreakJoinsLines()
    {
        var (lines, defects) = Read(B("NOTE;ENCODING=QUOTED-PRINTABLE:abc=\r\ndef=0D=0A=\r\n ghi\r\nX:1\r\n"));
        Assert.Equal("abcdef=0D=0A ghi", lines[0].Value);
        Assert.Equal("X", lines[1].Name);
        Assert.Contains(CalendarDefectKind.QuotedPrintableSoftBreak, Kinds(defects));
    }

    [Fact]
    public void EqualsAtEndOfNonQuotedPrintableLineIsNotASoftBreak()
    {
        var (lines, _) = Read(B("NOTE:abc=\r\nX:1\r\n"));
        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public void BareQuotedPrintableParameterIsRecognised()
    {
        var (lines, _) = Read(B("NOTE;QUOTED-PRINTABLE:a=\r\nb\r\n"));
        Assert.Equal("ab", lines[0].Value);
    }

    [Fact]
    public void UnindentedBase64LinesJoinAndTheBlankLineEndsTheBlock()
    {
        var (lines, defects) = Read(B("PHOTO;ENCODING=BASE64;JPEG:\r\nQUJD\r\nREVG\r\n\r\nEND:VCARD\r\n"));
        Assert.Equal(2, lines.Count);
        Assert.Equal("QUJDREVG", lines[0].Value.Replace("\r\n", string.Empty));
        Assert.Equal("END", lines[1].Name);
        Assert.Contains(CalendarDefectKind.Base64ContinuationWithoutFold, Kinds(defects));
    }

    [Fact]
    public void UnindentedBase64LookalikeAfterNonBase64LineStaysAMalformedLine()
    {
        var (lines, _) = Read(B("NOTE:hello\r\nQUJD\r\n"));
        Assert.Equal(ContentLineKind.Malformed, lines[1].Kind);
    }

    [Fact]
    public void ByteOrderMarkIsSkippedAndReported()
    {
        var (lines, defects) = Read([0xEF, 0xBB, 0xBF, .. B("A:1\r\n")]);
        Assert.Equal("A", lines[0].Name);
        Assert.Equal([CalendarDefectKind.ByteOrderMark], Kinds(defects));
    }

    [Fact]
    public void LongLineIsCutAtTheLimitButFullyConsumed()
    {
        ContentLineReaderOptions o = new() { MaxLineBytes = 20 };
        var (lines, defects) = Read(B("A:" + new string('x', 100) + "\r\nB:2\r\n"), o);
        Assert.Equal(2, lines.Count);
        Assert.Equal(18, lines[0].Value.Length);
        Assert.Contains(CalendarDefectKind.LineTooLong, Kinds(defects));
        Assert.Equal("2", lines[1].Value);
    }

    [Fact]
    public void LongFoldedLineIsCutAtTheLimit()
    {
        ContentLineReaderOptions o = new() { MaxLineBytes = 10 };
        string folded = "A:" + string.Join("\r\n ", Enumerable.Repeat("0123456789", 20)) + "\r\n";
        var (lines, defects) = Read(B(folded), o);
        Assert.Single(lines);
        Assert.Equal(8, lines[0].Value.Length);
        Assert.Contains(CalendarDefectKind.LineTooLong, Kinds(defects));
    }

    [Fact]
    public void FoldedReadingAllocatesNothingAfterTheBufferIsWarm()
    {
        byte[] data = B(string.Concat(Enumerable.Repeat("DESCRIPTION:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\r\n bbbbbbbbbbbbbbbbbbbbbbbbbbbb\r\nSUMMARY;LANGUAGE=en:x\r\n", 500)));
        long Run()
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            ContentLineReader r = new(data);
            int n = 0;
            while (r.TryRead(out ContentLine l))
            {
                foreach (ContentParameterSpan p in l.EnumerateParameters())
                {
                    n += p.Name.Length;
                }

                n++;
            }

            Assert.True(n > 0);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        long first = Run();
        long second = Run();
        Assert.True(second <= 1024, $"second pass allocated {second} bytes (first {first})");
    }
}
