using System.Text;
using RecursiveAI.OneMail.Mime;
using static RecursiveAI.OneMail.Tests.Mime.T;

namespace RecursiveAI.OneMail.Tests.Mime;

public class WriterEncodingTests
{
    [Theory]
    [InlineData("Subject", "Caf\u00e9 au lait \u2603")]
    [InlineData("Comments", "plain ascii then \u00fcber")]
    [InlineData("X-Custom", "\u65e5\u672c\u8a9e\u306e\u30c6\u30b9\u30c8 mixed with ascii words and a very long tail that has to be folded across more than one line of output")]
    public void FreeTextIsWrittenAsEncodedWordsAndReadsBack(string name, string value)
    {
        var h = new MimeHeader(name, value);
        string raw = Encoding.Latin1.GetString(h.Raw.Span);
        Assert.True(System.Text.Ascii.IsValid(h.Raw.Span), raw);
        Assert.All(raw.Split("\r\n"), l => Assert.True(l.Length <= 78, l));
        Assert.Equal(value, h.Value);
    }

    [Fact]
    public void AsciiValuesAreLeftAlone()
    {
        Assert.Equal("Subject: plain text\r\n", Encoding.Latin1.GetString(new MimeHeader("Subject", "plain text").Raw.Span));
    }

    [Fact]
    public void FieldsWithTheirOwnSyntaxKeepUtf8()
    {
        var h = new MimeHeader("From", "J\u00f6rg <j@example.com>");
        Assert.Contains("J\u00f6rg", Encoding.UTF8.GetString(h.Raw.Span));
    }

    [Theory]
    [InlineData("filename", "report.pdf")]
    [InlineData("filename", "my report (final).pdf")]
    [InlineData("filename", "caf\u00e9 \u2603.txt")]
    [InlineData("filename", "q\"uote\\back.txt")]
    [InlineData("filename", "a-very-long-name-with-unicode-\u00fc\u00f1\u00ee\u00e7\u00f8d\u00e9-and-more-and-more-and-more-and-more-and-more-and-more.txt")]
    public void ParametersRoundTripThroughTheParser(string name, string value)
    {
        string header = "Content-Disposition: attachment" + MimeHeaderValue.Parameter(name, value);
        var h = new MimeHeader("Content-Disposition", header["Content-Disposition: ".Length..]);
        MimeEntity e = MimeEntity.Parse(B("From: a@example.com\r\n") .Concat(h.Raw.ToArray()).Concat("\r\nx"u8.ToArray()).ToArray());
        Assert.Equal("attachment", e.Disposition);
        Assert.Equal(value, e.FileName);
    }
}
