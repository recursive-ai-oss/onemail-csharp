using RecursiveAI.OneMail.Bodies;

namespace RecursiveAI.OneMail.Tests.Bodies;

public class HtmlToTextTests
{
    private static string T(string html, HtmlToTextOptions? o = null) => HtmlToText.Convert(html, o).Text;

    [Fact]
    public void ParagraphsHeadingsAndBreaks()
    {
        Assert.Equal("Title\n\nOne two\nthree\n\nFour", T("<h1>Title</h1><p>One   two<br>three</p><p>Four</p>"));
        Assert.Equal("a\nb", T("<div>a</div><div>b</div>"));
        Assert.Equal("a\n\n\nb", T("a<br><br><br>b"));
        Assert.Equal("a\n\nb", T("a<br><br>b"));
        Assert.Equal("a\n\n\nb", T("a<br><br><br><br><br><br>b"));
    }

    [Fact]
    public void InlineElementsJoinWithoutSpaces()
    {
        Assert.Equal("Hello world and more", T("<p>Hello <b>wor</b><i>ld</i> and\n   more</p>"));
    }

    [Fact]
    public void ListsNumberedNestedAndPlain()
    {
        Assert.Equal("Before\n\n* one\n* two\n  * inner a\n  * inner b\n* three\n\nAfter", T("<p>Before</p><ul><li>one</li><li>two<ul><li>inner a</li><li>inner b</li></ul></li><li>three</li></ul><p>After</p>"));
        Assert.Equal("3. c\n4. d\n5. e", T("<ol start=3><li>c<li>d<li>e</ol>"));
        Assert.Equal("* a\n  1. x\n  2. y", T("<ul><li>a<ol><li>x</li><li>y</li></ol></li></ul>"));
    }

    [Fact]
    public void LongItemsWrapUnderTheMarker()
    {
        Assert.Equal("* aaa bbb\n  ccc ddd\n  eee", T("<ul><li>aaa bbb ccc ddd eee</li></ul>", new HtmlToTextOptions { LineWidth = 11 }));
    }

    [Fact]
    public void TablesBecomeRows()
    {
        Assert.Equal("Name | Qty\nApple | 3\nPear | 12", T("<table><tr><th>Name</th><th>Qty</th></tr><tr><td>Apple</td><td>3</td></tr><tr><td>Pear</td><td>12</td></tr></table>"));
        Assert.Equal("x\n\nA | B\n\ny", T("<p>x</p><table><tr><td>A</td><td>B</td></tr></table><p>y</p>"));
    }

    [Fact]
    public void LinksShowTheAddressOnlyWhenItAddsSomething()
    {
        Assert.Equal("Go here <https://example.com/a>.", T("Go <a href='https://example.com/a'>here</a>."));
        Assert.Equal("https://example.com", T("<a href='https://example.com/'>https://example.com</a>"));
        Assert.Equal("me@example.com", T("<a href='mailto:me@example.com'>me@example.com</a>"));
        Assert.Equal("click", T("<a href='#top'>click</a> <a href='javascript:x()'></a>"));
        Assert.Equal("here", T("<a href='https://example.com/a'>here</a>", new HtmlToTextOptions { Links = LinkStyle.TextOnly }));
        Assert.Equal("one [1] two [2] one again [1]\n\n[1] https://a.example/\n[2] https://b.example/", T("<a href='https://a.example/'>one</a> <a href='https://b.example/'>two</a> <a href='https://a.example/'>one again</a>", new HtmlToTextOptions { Links = LinkStyle.Footnotes }));
    }

    [Fact]
    public void ImagesWriteTheirAltText()
    {
        Assert.Equal("a [Logo] b", T("a <img src=x alt='Logo'> b <img src=y> <img src=z alt='px' width=1 height=1>"));
        Assert.Equal("Logo", T("<img alt='Logo'>", new HtmlToTextOptions { Images = ImageStyle.AltText }));
        Assert.Equal("", T("<img alt='Logo'>", new HtmlToTextOptions { Images = ImageStyle.None }));
    }

    [Fact]
    public void PreKeepsItsLines()
    {
        Assert.Equal("code:\n\n  a  b\n\n    c\n\nafter", T("<p>code:</p><pre>  a  b\n\n    c</pre><p>after</p>"));
        Assert.Equal("l1\nl2 x", T("<div style='white-space:pre-line'>l1\n   l2   x</div>"));
    }

    [Fact]
    public void QuotesGetPrefixes()
    {
        Assert.Equal("On Monday wrote:\n\n> hello\n>\n> > inner\n>\n> back\n\nreply", T("<p>On Monday wrote:</p><blockquote><p>hello</p><blockquote>inner</blockquote><p>back</p></blockquote><p>reply</p>"));
        Assert.Equal("> cited\n\nnew", T("<div type=cite>cited</div><p>new"));
    }

    [Fact]
    public void HiddenContentIsSkipped()
    {
        string html = "<html><head><title>T</title><style>.h{display:none}</style></head><body>" +
            "<div class=h>css hidden</div><div style='display:none'>inline hidden</div><span style='mso-hide:all'>mso</span>" +
            "<div hidden>attr</div><script>var x=1</script><p style='visibility:hidden'>invisible</p><p>shown</p></body></html>";
        Assert.Equal("shown", T(html));
        Assert.Contains("css hidden", T(html, new HtmlToTextOptions { ApplyStyleSheets = false }));
    }

    [Fact]
    public void MediaQueryHidingFollowsTheScreen()
    {
        string html = "<style>.m{display:none}@media (max-width:1px){.d{display:none}}</style><p class=m>mobile</p><p class=d>desktop</p>";
        Assert.Equal("desktop", T(html));
    }

    [Fact]
    public void EntitiesAndInvisibleCharacters()
    {
        Assert.Equal("a & b < c © d e f", T("<p>a &amp; b &lt; c &copy; d&nbsp;e\u200B\u00AD f"));
    }

    [Fact]
    public void WrapsOnlyWhenAsked()
    {
        string html = "<p>aaa bbb ccc ddd eee fff ggg</p>";
        Assert.Equal("aaa bbb ccc ddd eee fff ggg", T(html));
        Assert.Equal("aaa bbb ccc\nddd eee fff\nggg", T(html, new HtmlToTextOptions { LineWidth = 11 }));
        Assert.Equal("> aaa bbb\n> ccc ddd\n> eee fff\n> ggg", T("<blockquote>aaa bbb ccc ddd eee fff ggg</blockquote>", new HtmlToTextOptions { LineWidth = 11 }));
    }

    [Fact]
    public void IsDeterministic()
    {
        string html = "<table><tr><td>a<td>b</table><ul><li>x<li>y</ul><p>z<a href='http://q.example/'>q</a>";
        Assert.Equal(T(html), T(html));
    }

    // ---- hostile input ----

    [Fact]
    public void DeepNestingIsBounded()
    {
        string html = string.Concat(Enumerable.Repeat("<blockquote><ul><li>", 100)) + "deep";
        BodyConversion c = HtmlToText.Convert(html);
        Assert.Contains("deep", c.Text);
        Assert.Contains(c.Issues, i => i.Kind == BodyIssueKind.NestingTooDeep);
        Assert.True(c.Text.Length < 4096);

        // Past OneHtml's own limit the document is cut and the text before the cut is kept.
        BodyConversion d = HtmlToText.Convert("start " + string.Concat(Enumerable.Repeat("<blockquote><ul><li>", 3000)) + "deep");
        Assert.Contains(d.Issues, i => i.Kind == BodyIssueKind.HtmlLimitExceeded);
        Assert.Contains("start", d.Text);
    }

    [Fact]
    public void HugeTablesAreCut()
    {
        string html = "<table>" + string.Concat(Enumerable.Repeat("<tr><td>a<td>b<td>c</tr>", 2000)) + "</table>";
        BodyConversion c = HtmlToText.Convert(html, new HtmlToTextOptions { MaxTableCells = 1000 });
        Assert.Contains(c.Issues, i => i.Kind == BodyIssueKind.TableTooLarge);
        Assert.Equal(334, c.Text.Split('\n').Length);
    }

    [Fact]
    public void PathologicalWhitespaceIsBounded()
    {
        string html = "<pre>" + new string(' ', 1_000_000) + "x</pre><p>" + string.Concat(Enumerable.Repeat("&nbsp;", 100_000)) + "y</p>";
        BodyConversion c = HtmlToText.Convert(html);
        Assert.Contains(c.Issues, i => i.Kind == BodyIssueKind.WhitespaceRunBounded);
        Assert.True(c.Text.Length < 1000, c.Text.Length.ToString());
    }

    [Fact]
    public void OutputAndInputLimitsAreReported()
    {
        string html = string.Concat(Enumerable.Repeat("<p>0123456789</p>", 100_000));
        BodyConversion c = HtmlToText.Convert(html, new HtmlToTextOptions { MaxOutputChars = 10_000 });
        Assert.Contains(c.Issues, i => i.Kind == BodyIssueKind.OutputTruncated);
        Assert.True(c.Text.Length < 12_000);
        BodyConversion d = HtmlToText.Convert(html, new HtmlToTextOptions { MaxInputChars = 1000 });
        Assert.Contains(d.Issues, i => i.Kind == BodyIssueKind.InputTruncated);
    }

    [Fact]
    public void GarbageDoesNotThrow()
    {
        foreach (string s in new[] { "", "<", "<<<>>>", "<a href=", "&#x110000;&bogus;<table><td><li>", "\0\u0001<p\0>x", "<svg><p>x</p></svg><math>y</math>" })
        {
            Assert.NotNull(HtmlToText.Convert(s).Text);
        }
    }
}
