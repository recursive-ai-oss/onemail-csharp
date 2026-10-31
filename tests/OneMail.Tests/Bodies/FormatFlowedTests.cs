using RecursiveAI.OneMail.Bodies;

namespace RecursiveAI.OneMail.Tests.Bodies;

public class FormatFlowedTests
{
    private static string Texts(IReadOnlyList<FlowedParagraph> p) => string.Join("|", p.Select(x => $"{x.QuoteDepth}:{x.Text}"));

    [Fact]
    public void SoftBreaksJoinAndFixedBreaksStay()
    {
        // RFC 3676 §4.5: a line ending in a space is flowed and joins the next line.
        Assert.Equal("0:Hello world|0:next", Texts(FormatFlowed.Parse("Hello \r\nworld\r\nnext\r\n")));
    }

    [Fact]
    public void DelSpRemovesTheSoftBreakSpace()
    {
        Assert.Equal("0:foobar", Texts(FormatFlowed.Parse("foo \nbar", delSp: true)));
        Assert.Equal("0:foo bar", Texts(FormatFlowed.Parse("foo \nbar", delSp: false)));
    }

    [Fact]
    public void QuoteDepthIsReadAndAChangeEndsTheParagraph()
    {
        Assert.Equal("1:a b|0:plain", Texts(FormatFlowed.Parse("> a \n> b\nplain")));
        Assert.Equal("1:a |0:b", Texts(FormatFlowed.Parse(">a \nb")));
        Assert.Equal("2:deep|1:", Texts(FormatFlowed.Parse(">> deep\n>")));
    }

    [Fact]
    public void SpaceStuffingIsRemoved()
    {
        Assert.Equal("0:>foo|0:From x|0: indented", Texts(FormatFlowed.Parse(" >foo\n From x\n  indented")));
    }

    [Fact]
    public void SignatureSeparatorIsNotFlowed()
    {
        Assert.Equal("0:text|0:-- |0:Name", Texts(FormatFlowed.Parse("text\n-- \nName")));
    }

    [Fact]
    public void DecodeWritesQuotePrefixes()
    {
        Assert.Equal("> one two\n>\nplain", FormatFlowed.Decode("> one \n> two\n>\nplain"));
    }

    [Fact]
    public void EncodeWrapsStuffsAndQuotes()
    {
        string enc = FormatFlowed.Encode("aaa bbb ccc ddd eee fff ggg hhh iii jjj kkk lll\n> quoted line that is long enough to need wrapping at the width\n From here\n>>deep", 24);
        string[] lines = enc.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.All(lines, l => Assert.True(l.Length <= 24, l));
        Assert.Contains(lines, l => l == "> quoted line that is ");
        Assert.Contains("  From here", lines);
        Assert.Contains(">> deep", lines);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EncodeThenParseGivesTheParagraphs(bool delSp)
    {
        Random rng = new(2810);
        string[] words = ["a", "bb", "ccc", "hello", "world", "quite-a-long-word-here", "x", "From", ">", "ünï", "日本語", "😀"];
        for (int iter = 0; iter < 300; iter++)
        {
            List<FlowedParagraph> input = [];
            int n = rng.Next(1, 6);
            for (int i = 0; i < n; i++)
            {
                int wc = rng.Next(0, 40);
                List<string> w = [];
                for (int j = 0; j < wc; j++)
                {
                    w.Add(words[rng.Next(words.Length)]);
                }

                string text = string.Join(rng.Next(6) == 0 ? "  " : " ", w);
                input.Add(new FlowedParagraph(rng.Next(0, 4), text));
            }

            int width = rng.Next(20, 80);
            string enc = FormatFlowed.Encode(input, width, delSp);
            IReadOnlyList<FlowedParagraph> back = FormatFlowed.Parse(enc, delSp);
            Assert.Equal(Texts(input.Select(p => p with { Text = p.Text.TrimEnd(' ') }).ToList()), Texts(back));
            if (!delSp)
            {
                foreach (string line in enc.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
                {
                    // A line may exceed the width only when it is a single word that does not fit.
                    Assert.True(line.Length <= width || !line.TrimEnd(' ').Contains(' ') || line.TrimStart('>', ' ').Split(' ').Length <= 2, line);
                }
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LongRunsOfSpacesSurviveWrapping(bool delSp)
    {
        // Found by fuzzing: a line of only spaces is still a flowed line when it ends in a space.
        FlowedParagraph[] input = [new FlowedParagraph(0, "north delta" + new string(' ', 60) + "epsilon contract")];
        string enc = FormatFlowed.Encode(input, 24, delSp);
        Assert.Equal(input[0].Text, Assert.Single(FormatFlowed.Parse(enc, delSp)).Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QuotePrefixLongerThanTheWidthStillRoundTrips(bool delSp)
    {
        // Found by fuzzing: 100 quote markers with width 20 left no room for text.
        FlowedParagraph[] input = [new FlowedParagraph(100, "some words that need wrapping around"), new FlowedParagraph(0, "after")];
        string enc = FormatFlowed.Encode(input, 20, delSp);
        Assert.Equal(Texts(input), Texts(FormatFlowed.Parse(enc, delSp)));
    }

    [Fact]
    public void PathologicalInputDoesNotThrow()
    {
        string spaces = new(' ', 100_000);
        Assert.NotEmpty(FormatFlowed.Parse(spaces + "\n" + spaces));
        Assert.NotNull(FormatFlowed.Encode(new string('x', 50_000) + " " + new string('y', 50_000), 78, true));
        Assert.NotNull(FormatFlowed.Encode(">>>>>>>>>>" + new string('x', 5000), 20, false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BreakNeverLeavesALineThatReadsAsASignatureSeparator(bool delSp)
    {
        // Found by fuzzing: "--" and its soft-break space on a line of their own is the signature separator, which does not flow.
        foreach (int depth in new[] { 0, 1, 32 })
        {
            foreach (int width in new[] { 20, 21, 35, 36, 40 })
            {
                FlowedParagraph[] input =
                [
                    new FlowedParagraph(depth, new string('-', 120)),
                    new FlowedParagraph(depth, "aaa bbb ccc ddd eee fff ggg -- hhh iii jjj kkk lll mmm nnn ooo ppp -- qqq"),
                ];
                string enc = FormatFlowed.Encode(input, width, delSp);
                Assert.Equal(Texts(input), Texts(FormatFlowed.Parse(enc, delSp)));
            }
        }
    }
}
