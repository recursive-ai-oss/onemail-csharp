using System.Text;
using RecursiveAI.OneMail.Rtf;

namespace RecursiveAI.OneMail.Tests.Rtf;

public class DeencapsulationTests
{
    private static string Data([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "testdata", "rtf"));

    private static RtfDeencapsulationResult De(string rtf) => RtfDeencapsulator.Deencapsulate(Encoding.Latin1.GetBytes(rtf));

    [Fact]
    public void SpecExampleGivesBackTheOriginalHtml()
    {
        byte[] rtf = File.ReadAllBytes(Path.Combine(Data(), "oxrtfex-3.1.rtf"));
        string html = File.ReadAllText(Path.Combine(Data(), "oxrtfex-3.1.html"));
        RtfDeencapsulationResult r = RtfDeencapsulator.Deencapsulate(rtf);
        Assert.Equal(RtfEncapsulation.Html, r.Kind);
        Assert.Equal(1251, r.DefaultCodePage);
        // The specification's RTF example has no space between the three entities where its HTML example has one:
        // the spaces were lost in the example (a space after \htmlrtf0 is that word's delimiter), so they are not expected.
        // (The same example's RTF also says "This is a bold text" where its HTML says "This is bold text".)
        Assert.Equal(html.Replace("&nbsp; &lt; &uml;", "&nbsp;&lt;&uml;", StringComparison.Ordinal).Replace("This is bold text", "This is a bold text", StringComparison.Ordinal), r.Text);
        Assert.Empty(r.Defects);
    }

    [Fact]
    public void DetectFollowsTheFirstTenTokensRule()
    {
        Assert.Equal(RtfEncapsulation.Html, RtfDeencapsulator.Detect("{\\rtf1\\ansi\\ansicpg1252\\fromhtml1 \\deff0{\\fonttbl}"u8));
        Assert.Equal(RtfEncapsulation.PlainText, RtfDeencapsulator.Detect("{\\rtf1\\ansi\\fromtext \\deff0"u8));
        Assert.Equal(RtfEncapsulation.None, RtfDeencapsulator.Detect("{\\rtf1\\ansi\\fromhtml \\deff0"u8)); // not \fromhtml1
        Assert.Equal(RtfEncapsulation.None, RtfDeencapsulator.Detect("{\\rtf1\\ansi\\fromhtml0 x"u8));
        Assert.Equal(RtfEncapsulation.None, RtfDeencapsulator.Detect("{\\rtf1\\ansi hello \\fromhtml1 "u8)); // text before it
        Assert.Equal(RtfEncapsulation.None, RtfDeencapsulator.Detect("{\\rtf1\\a\\b\\c\\d\\e\\f\\g\\h\\i\\fromhtml1 "u8)); // the eleventh token
        Assert.Equal(RtfEncapsulation.None, RtfDeencapsulator.Detect("{\\urtf1\\fromhtml1 "u8));
        Assert.Equal(RtfEncapsulation.None, RtfDeencapsulator.Detect(""u8));
        Assert.Equal(RtfEncapsulation.Html, RtfDeencapsulator.Detect("{\\rtf1\r\n\\ansi\\fromhtml1 "u8));
    }

    [Fact]
    public void NonEncapsulatedDocumentsExplainThemselves()
    {
        RtfDeencapsulationResult a = De("{\\rtf1\\ansi plain rtf}");
        Assert.Equal(RtfEncapsulation.None, a.Kind);
        Assert.Equal(RtfDefectKind.NotEncapsulated, Assert.Single(a.Defects).Kind);
        Assert.Equal(RtfDefectKind.NotRtf, Assert.Single(De("hello").Defects).Kind);
    }

    [Fact]
    public void HtmlRtfSuppressionNestsAndRestoresOnGroupEnd()
    {
        RtfDeencapsulationResult r = De("{\\rtf1\\fromhtml1 a\\htmlrtf b{\\htmlrtf0 c\\htmlrtf d}e{\\htmlrtf1 f}\\htmlrtf0 g\\htmlrtf1 h}");
        // a visible; b suppressed; inside the group c visible (htmlrtf0), d suppressed; after the group the outer state (on) returns, so e is suppressed;
        // f suppressed; g visible after \htmlrtf0; h suppressed.
        Assert.Equal("acg", r.Text);
    }

    [Fact]
    public void HtmlTagContentIsCopiedEvenWhenSuppressedAndItsParameterIsIgnored()
    {
        RtfDeencapsulationResult r = De("{\\rtf1\\fromhtml1 \\htmlrtf {\\*\\htmltag148 <FONT face=\"x\">}\\htmlrtf0 hi{\\*\\htmltag <br>\\par}{\\*\\htmltag64}}");
        Assert.Equal("<FONT face=\"x\">hi<br>\r\n", r.Text);
    }

    [Fact]
    public void EscapesAndSymbolsInsideHtmlTag()
    {
        RtfDeencapsulationResult r = De("{\\rtf1\\fromhtml1 {\\*\\htmltag \\{a\\}\\\\\\tab\\lquote\\rquote\\ldblquote\\rdblquote\\bullet\\endash\\emdash\\~\\_\\par\\'e9\\u8364?x}}");
        Assert.Equal("{a}\\\t\u2018\u2019\u201C\u201D\u2022\u2013\u2014\u00A0\u00AD\r\n\u00E9\u20ACx", r.Text);
    }

    [Fact]
    public void UnicodeEscapesSkipTheFallbackCharacters()
    {
        Assert.Equal("a\u20ACb", De("{\\rtf1\\fromtext a\\u8364?b}").Text);
        Assert.Equal("a\u20ACb", De("{\\rtf1\\fromtext \\uc2 a\\u8364\\'80\\'80b}").Text);
        Assert.Equal("a\u20ACb", De("{\\rtf1\\fromtext \\uc0 a\\u8364b}").Text);
        Assert.Equal("a\u20AC?b", De("{\\rtf1\\fromtext \\uc0 a\\u8364?b}").Text);
        Assert.Equal("a\u20ACb", De("{\\rtf1\\fromtext a\\u8364 ?b}").Text); // the space ends \u8364; the ? is the fallback
        Assert.Equal("\uD83D\uDE00", De("{\\rtf1\\fromtext \\u-10179?\\u-8704?}").Text);
    }

    [Fact]
    public void UcIsScopedToItsGroup()
    {
        Assert.Equal("\u20ACxy\u20AC", De("{\\rtf1\\fromtext {\\uc0 \\u8364}xy\\u8364?}").Text);
    }

    [Fact]
    public void LoneSurrogateBecomesReplacementCharacter()
    {
        RtfDeencapsulationResult r = De("{\\rtf1\\fromtext \\u-10179?x}");
        Assert.Equal("\uFFFDx", r.Text);
        Assert.Contains(r.Defects, d => d.Kind == RtfDefectKind.LoneSurrogate);
    }

    [Fact]
    public void BytesAreDecodedInTheCurrentFontsCodePage()
    {
        // 1251 default; font 1 is declared charset 0 (windows-1252); font 2 has \cpg932 (Shift JIS, a two-byte pair)
        string rtf = "{\\rtf1\\ansi\\ansicpg1251\\fromtext\\deff0{\\fonttbl{\\f0\\fswiss Arial;}{\\f1\\fswiss\\fcharset0 Arial;}{\\f2\\fnil\\cpg932 MS Gothic;}}"
            + "\\f0\\'cf\\f1\\'e9\\f2\\'93\\'fa\\f0\\'cf}";
        RtfDeencapsulationResult r = De(rtf);
        Assert.Equal("\u041F\u00E9\u65E5\u041F", r.Text);
        Assert.Empty(r.Defects);
    }

    [Fact]
    public void DefaultFontApplies()
    {
        Assert.Equal("\u041F", De("{\\rtf1\\ansi\\ansicpg1252\\fromtext\\deff1{\\fonttbl{\\f1\\fswiss\\fcharset204 Arial;}}\\'cf}").Text);
    }

    [Fact]
    public void HtmlTagContentUsesTheDocumentCodePageNotTheFont()
    {
        string rtf = "{\\rtf1\\ansi\\ansicpg1252\\fromhtml1\\deff0{\\fonttbl{\\f0\\fswiss\\fcharset204 Arial;}}\\f0 {\\*\\htmltag \\'cf}\\'cf}";
        Assert.Equal("\u00CF\u041F", De(rtf).Text);
    }

    [Fact]
    public void CodePageHookIsUsedAndUnknownCodePagesFallBack()
    {
        var o = new RtfDeencapsulationOptions { CodePageDecoder = cp => cp == 1250 ? Encoding.Latin1 : null };
        RtfDeencapsulationResult r = RtfDeencapsulator.Deencapsulate("{\\rtf1\\ansi\\ansicpg1250\\fromtext\\'e9}"u8, o);
        Assert.Equal("\u00E9", r.Text);
        RtfDeencapsulationResult r2 = RtfDeencapsulator.Deencapsulate("{\\rtf1\\ansi\\ansicpg1252\\fromtext\\'e9}"u8, o);
        Assert.Contains(r2.Defects, d => d.Kind == RtfDefectKind.AmbiguousUnknownCodePage);
    }

    [Fact]
    public void PlainTextDocumentsSkipHeaderDestinationsAndMapParAndTab()
    {
        string rtf = "{\\rtf1\\ansi\\fromtext{\\fonttbl{\\f0 Arial;}}{\\colortbl;\\red0\\green0\\blue0;}{\\*\\generator X;}{\\info{\\title t}}\\pard a\\par\r\nb\\line c\\tab d\\{\\}\\\\ {\\pict\\bin3 abc}}";
        Assert.Equal("a\r\nb\r\nc\td{}\\ ", De(rtf).Text);
    }

    [Fact]
    public void HtmlRtfInPlainTextIsIgnoredWithADefect()
    {
        RtfDeencapsulationResult r = De("{\\rtf1\\fromtext a\\htmlrtf b\\htmlrtf0 c}");
        Assert.Equal("abc", r.Text);
        Assert.Contains(r.Defects, d => d.Kind == RtfDefectKind.AmbiguousHtmlRtfInPlainText);
    }

    [Fact]
    public void TextAfterTheRootGroupAndBinaryDataAreDropped()
    {
        Assert.Equal("a", De("{\\rtf1\\fromtext a}\0\0\0trailing").Text);
        Assert.Equal("ab", De("{\\rtf1\\fromtext a{\\*\\x \\bin4 {}\\}}b}").Text);
    }

    [Fact]
    public void HostileInputNeverThrows()
    {
        Assert.Equal(RtfEncapsulation.Html, De("{\\rtf1\\fromhtml1 " + new string('{', 5000)).Kind);
        RtfDeencapsulationResult r = De("{\\rtf1\\fromhtml1 {\\*\\htmltag " + new string('{', 5000));
        Assert.Contains(r.Defects, d => d.Kind == RtfDefectKind.NestingLimitExceeded);
        De("{\\rtf1\\fromhtml1 \\u99999999999999999999 \\'z \\bin999999999 xx");
        De("{\\rtf1\\fromhtml1 \\");
    }
}

public class EncapsulationTests
{
    private static string Data([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "testdata", "rtf"));

    private static string RoundTripHtml(string html)
    {
        byte[] rtf = RtfEncapsulator.EncapsulateHtml(html);
        Assert.True(Ascii.IsValid(rtf));
        RtfDeencapsulationResult r = RtfDeencapsulator.Deencapsulate(rtf);
        Assert.Equal(RtfEncapsulation.Html, r.Kind);
        Assert.Empty(r.Defects);
        return r.Text;
    }

    private static string CrLf(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\n", "\r\n", StringComparison.Ordinal);

    public static IEnumerable<object[]> Htmls()
    {
        yield return new object[] { string.Empty };
        yield return new object[] { "plain" };
        yield return new object[] { "<p>Hello <b>wor\\ld</b> {x}</p>\r\n<p>caf\u00E9 \u65E5\u672C \U0001F600 &amp; &lt; &nbsp; &#233; &#x1F600; &unknown; & alone</p>" };
        yield return new object[] { "<!DOCTYPE html>\r\n<html><head><style>p { color: red }\r\n</style><script>if (a<b && c>d) { x = \"</p>\"; }</script></head>\r\n<body onload=\"a('>')\">\ttext\twith\ttabs\r\n<!-- a > comment\r\n-->\r\n<br/></body></html>" };
        yield return new object[] { "text < not a tag, <3 and a<b, 5 > 4 and an unterminated <a href=\"x" };
        yield return new object[] { "ctrl \u0001 \u007F chars and a literal ?" };
        yield return new object[] { "<a href=\"http://example.com/?a=1&amp;b=2\">link</a>\r\n\r\n\r\n<p>\r\n</p>" };
    }

    [Theory]
    [MemberData(nameof(Htmls))]
    public void HtmlRoundTripsExactlyUpToLineEndings(string html) => Assert.Equal(CrLf(html), RoundTripHtml(html));

    [Fact]
    public void HtmlWithCrlfLineEndingsRoundTripsExactly()
    {
        string html = File.ReadAllText(Path.Combine(Data(), "oxrtfex-3.1.html"));
        Assert.Equal(html, RoundTripHtml(html));
    }

    [Fact]
    public void LoneCrAndLfBecomeCrlf() => Assert.Equal("a\r\nb\r\nc\r\n", RoundTripHtml("a\nb\rc\r\n"));

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("two  spaces,\ttab,\r\nline, back\\slash {braces} caf\u00E9 \u65E5 \U0001F600 ?\u0001")]
    [InlineData("\r\n\r\nleading and trailing\r\n")]
    [InlineData("1")]
    public void TextRoundTripsExactly(string text)
    {
        byte[] rtf = RtfEncapsulator.EncapsulateText(text);
        Assert.True(Ascii.IsValid(rtf));
        RtfDeencapsulationResult r = RtfDeencapsulator.Deencapsulate(rtf);
        Assert.Equal(RtfEncapsulation.PlainText, r.Kind);
        Assert.Empty(r.Defects);
        Assert.Equal(text, r.Text);
    }

    [Fact]
    public void EncapsulatedRtfStartsLikeTheSpecSuggests()
    {
        string rtf = Encoding.ASCII.GetString(RtfEncapsulator.EncapsulateHtml("<p>x</p>"));
        Assert.StartsWith("{\\rtf1\\ansi\\ansicpg1252\\fromhtml1 ", rtf, StringComparison.Ordinal);
        Assert.Contains("{\\*\\htmltag64}", rtf, StringComparison.Ordinal);
        Assert.StartsWith("{\\rtf1\\ansi\\ansicpg1252\\fromtext ", Encoding.ASCII.GetString(RtfEncapsulator.EncapsulateText("x")), StringComparison.Ordinal);
    }

    [Fact]
    public void EncapsulatedRtfSurvivesCompression()
    {
        string html = File.ReadAllText(Path.Combine(Data(), "oxrtfex-3.1.html"));
        byte[] packed = RecursiveAI.OneMail.Rtf.Compression.RtfCompressor.Compress(RtfEncapsulator.EncapsulateHtml(html));
        byte[] rtf = RecursiveAI.OneMail.Rtf.Compression.RtfDecompressor.Decompress(packed).Data;
        Assert.Equal(html, RtfDeencapsulator.Deencapsulate(rtf).Text);
    }
}

public class RtfReaderTests
{
    private static string Tokens(string rtf, RtfReaderOptions? o = null)
    {
        var list = new List<string>();
        var r = new RtfReader(Encoding.Latin1.GetBytes(rtf), o);
        while (r.Read())
        {
            list.Add(r.Kind switch
            {
                RtfTokenKind.GroupStart => "{",
                RtfTokenKind.GroupEnd => "}",
                RtfTokenKind.ControlWord => "\\" + Encoding.ASCII.GetString(r.Name) + (r.HasParameter ? r.Parameter.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty),
                RtfTokenKind.ControlSymbol => "sym:" + (char)r.Name[0],
                RtfTokenKind.HexEscape => "hex:" + r.HexValue.ToString("x2", System.Globalization.CultureInfo.InvariantCulture),
                RtfTokenKind.Text => "text:" + Encoding.Latin1.GetString(r.Text),
                RtfTokenKind.Binary => "bin:" + Encoding.Latin1.GetString(r.Text),
                _ => "?",
            });
        }

        return string.Join(" ", list);
    }

    [Fact]
    public void TokenizesGroupsWordsSymbolsEscapesAndText()
    {
        Assert.Equal(
            "{ \\rtf1 \\ansi \\f-12 text:a b sym:{ sym:\\ hex:e9 text:x sym:* \\par }",
            Tokens("{\\rtf1\\ansi\\f-12 a b\\{\\\\\\'e9x\\*\\par }"));
    }

    [Fact]
    public void OneSpaceEndsAControlWordAndLineBreaksAreNotText()
    {
        Assert.Equal("\\b text: x text:y", Tokens("\\b  x\r\ny"));
        Assert.Equal("\\b text:1", Tokens("\\b\r\n1"));
        Assert.Equal("\\b1 text:2", Tokens("\\b1 2"));
    }

    [Fact]
    public void BinaryDataIsDeliveredRawAndNotParsed()
    {
        Assert.Equal("{ \\bin4 bin:{}\\x text:y }", Tokens("{\\bin4 {}\\xy}"));
    }

    [Fact]
    public void BackslashBeforeLineBreakIsAControlSymbol()
    {
        Assert.Equal("sym:\r text:a", Tokens("\\\r\na"));
    }

    [Fact]
    public void MalformedInputGivesDefectsNotExceptions()
    {
        var r = new RtfReader("}{\\bin99 ab\\'zz\\f99999999999 \\"u8);
        while (r.Read())
        {
        }

        Assert.Contains(r.Defects, d => d.Kind == RtfDefectKind.UnbalancedGroupEnd);
        Assert.Contains(r.Defects, d => d.Kind == RtfDefectKind.BinaryDataTruncated);
        Assert.Contains(r.Defects, d => d.Kind == RtfDefectKind.UnclosedGroups);
    }

    [Fact]
    public void ParameterOverflowAndHexEscapeDefects()
    {
        var r = new RtfReader("\\f99999999999 \\'zz\\"u8);
        var kinds = new List<RtfTokenKind>();
        while (r.Read())
        {
            kinds.Add(r.Kind);
        }

        Assert.Contains(r.Defects, d => d.Kind == RtfDefectKind.ParameterOverflow);
        Assert.Contains(r.Defects, d => d.Kind == RtfDefectKind.InvalidHexEscape);
        Assert.Contains(r.Defects, d => d.Kind == RtfDefectKind.TruncatedToken);
    }

    [Fact]
    public void NestingLimitStopsTheReader()
    {
        var r = new RtfReader(Encoding.ASCII.GetBytes(new string('{', 50)), new RtfReaderOptions { MaxGroupDepth = 10 });
        int n = 0;
        while (r.Read())
        {
            n++;
        }

        Assert.Equal(10, n);
        Assert.Contains(r.Defects, d => d.Kind == RtfDefectKind.NestingLimitExceeded);
    }
}
