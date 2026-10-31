using RecursiveAI.OneHtml;
using RecursiveAI.OneMail.Bodies;

namespace RecursiveAI.OneMail.Tests.Bodies;

public class TextToHtmlTests
{
    private static string Body(string text, TextToHtmlOptions? o = null) => TextToHtml.Convert(text, (o ?? new TextToHtmlOptions()) with { FullDocument = false }).Text;

    [Fact]
    public void EscapesParagraphsAndBreaks()
    {
        Assert.Equal("<p>a &lt;b&gt; &amp; c<br>\nsecond</p>\n<p>next</p>\n", Body("a <b> & c\nsecond\n\nnext"));
    }

    [Fact]
    public void QuoteLevelsBecomeNestedBlockquotes()
    {
        string h = Body("top\n> one\n>> two\n> back\nend");
        Assert.Equal("<p>top</p>\n<blockquote type=\"cite\">\n<p>one</p>\n<blockquote type=\"cite\">\n<p>two</p>\n</blockquote>\n<p>back</p>\n</blockquote>\n<p>end</p>\n", h);
        Assert.Equal(h, Body("top\n> one\n> > two\n> back\nend"));
    }

    [Fact]
    public void SpacesAreKept()
    {
        Assert.Equal("<p>&nbsp;&nbsp;a &nbsp;b&nbsp;&nbsp;&nbsp;&nbsp;c</p>\n", Body("  a  b\tc"));
    }

    [Theory]
    [InlineData("see http://example.com/a?b=1&c=2.", "<p>see <a href=\"http://example.com/a?b=1&amp;c=2\">http://example.com/a?b=1&amp;c=2</a>.</p>\n")]
    [InlineData("(https://example.com/x_(y))", "<p>(<a href=\"https://example.com/x_(y)\">https://example.com/x_(y)</a>)</p>\n")]
    [InlineData("<https://example.com/>", "<p>&lt;<a href=\"https://example.com/\">https://example.com/</a>&gt;</p>\n")]
    [InlineData("mail mailto:a@b.example now", "<p>mail <a href=\"mailto:a@b.example\">mailto:a@b.example</a> now</p>\n")]
    [InlineData("www.example.org", "<p><a href=\"http://www.example.org\">www.example.org</a></p>\n")]
    [InlineData("xhttp://no and www and mailto:nobody and javascript:alert(1)", "<p>xhttp://no and www and mailto:nobody and javascript:alert(1)</p>\n")]
    public void LinksAreConservative(string input, string expected) => Assert.Equal(expected, Body(input));

    [Fact]
    public void FlowedIsUnwrappedFirst()
    {
        string h = Body("> a long \n> line\n\nplain", new TextToHtmlOptions { Flowed = true });
        Assert.Equal("<blockquote type=\"cite\">\n<p>a long line</p>\n</blockquote>\n<p>plain</p>\n", h);
    }

    [Fact]
    public void OutputParsesWithoutErrors()
    {
        string[] inputs =
        [
            "plain",
            "a <script>alert(1)</script> & \"q\" 'x'\n\n> quote\n>> deeper\n\n-- \nsig",
            "ctl \u0001\u0007\u007f\u0085 nonchar \uFFFE ok \uD83D\uDE00 lone \uD800 end",
            "http://a.example/\"onmouseover=\"x www.b.example,",
            "http://a.example/x\uFFFEy www.b.example/\u0001z https://c.example/\uD83D\uDE00\uD83F\uDFFF end",
            string.Join("\n", Enumerable.Range(0, 40).Select(i => new string('>', i % 7) + " line " + i)),
        ];
        foreach (string input in inputs)
        {
            BodyConversion c = TextToHtml.Convert(input);
            Document doc = Document.Parse(c.Text, new ParseOptions { RecordErrors = true }, TestContext.Current.CancellationToken);
            Assert.Empty(doc.ParseErrors);
        }
    }

    [Fact]
    public void QuoteDepthIsCapped()
    {
        BodyConversion c = TextToHtml.Convert(new string('>', 5000) + " x");
        Assert.Contains(c.Issues, i => i.Kind == BodyIssueKind.QuoteDepthCapped);
        Assert.Equal(32, c.Text.Split("<blockquote").Length - 1);
    }

    [Fact]
    public void LimitsAreReported()
    {
        BodyConversion c = TextToHtml.Convert(new string('a', 1000), new TextToHtmlOptions { MaxInputChars = 100 });
        Assert.Contains(c.Issues, i => i.Kind == BodyIssueKind.InputTruncated);
        BodyConversion d = TextToHtml.Convert(string.Join("\n", Enumerable.Repeat("0123456789", 1000)), new TextToHtmlOptions { MaxOutputChars = 500 });
        Assert.Contains(d.Issues, i => i.Kind == BodyIssueKind.OutputTruncated);
    }
}
