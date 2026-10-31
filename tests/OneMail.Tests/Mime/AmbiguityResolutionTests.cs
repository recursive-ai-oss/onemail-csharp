using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Mime.Reading;
using static RecursiveAI.OneMail.Tests.Mime.T;

namespace RecursiveAI.OneMail.Tests.Mime;

/// <summary>One check per ambiguity-table resolution (docs/ambiguities.md) that the structured parsers decide.</summary>
public class AmbiguityResolutionTests
{
    private static MimeEntity Parse(string headers, string body = "x\r\n") => MimeEntity.Parse(B("From: a@example.com\r\n" + headers + "\r\n" + body));

    private static string Addr(string toValue)
    {
        MimeEntity e = Parse("To: " + toValue + "\r\n");
        return string.Join("; ", AddressParser.Flatten(e.GetAddresses("To")).Select(m => (m.Name ?? "") + "|" + m.Address));
    }

    [Theory]
    [InlineData("A1", "Undisclosed:;", "")]
    [InlineData("A1", "G: a@x.example, B <b@x.example>;, c@x.example", "|a@x.example; B|b@x.example; |c@x.example")]
    [InlineData("A2", "Doe, John <john@x.example>", "Doe, John|john@x.example")]
    [InlineData("A3", "john@x.example, John Doe jane@x.example", "|john@x.example; John Doe|jane@x.example")]
    [InlineData("A4", "john@x.example (John Doe)", "John Doe|john@x.example")]
    [InlineData("A5", "John (a (nested) comment) <(c)john(d)@(e)x.example(f)>", "John|john@x.example")]
    [InlineData("A5", "<@a.example,@b.example:john@x.example>", "|john@x.example")]
    [InlineData("A5", "a @ x . example", "|a@x.example")]
    [InlineData("A6", "\"john q\"@x.example", "|\"john q\"@x.example")]
    [InlineData("A7", "Bounce <>", "Bounce|")]
    [InlineData("A8", "john, mary@", "|john; |mary@")]
    [InlineData("A9", "j@[192.0.2.1], k@[IPv6:2001:db8::1]", "|j@[192.0.2.1]; |k@[IPv6:2001:db8::1]")]
    [InlineData("A9", "a@x.example,, ,b@y.example", "|a@x.example; |b@y.example")]
    [InlineData("A9", "John <john@x.example", "John|john@x.example")]
    [InlineData("A9", "J. Q. Doe <john@x.example>", "J. Q. Doe|john@x.example")]
    [InlineData("A10", "j\u00f6hn@ex\u00e4mple.example", "|j\u00f6hn@ex\u00e4mple.example")]
    [InlineData("A11", "\"John <john@x.example>", "John|john@x.example")]
    [InlineData("A12", "john@home <john@x.example>", "john@home|john@x.example")]
    [InlineData("A13", "<a@x.example><b@y.example>", "|a@x.example; |b@y.example")]
    [InlineData("A14", "a@x.example; b@y.example", "|a@x.example")]
    [InlineData("A15", ".a@x.example, b.@x.example, c..d@x.example", "|.a@x.example; |b.@x.example; |c..d@x.example")]
    [InlineData("N13", "=?utf-8?q?Zo=C3=AB?= <z@x.example>", "Zo\u00eb|z@x.example")]
    public void AddressResolutions(string entry, string value, string expected)
    {
        Assert.False(string.IsNullOrEmpty(entry));
        Assert.Equal(expected, Addr(value));
    }

    [Fact]
    public void QuotedLocalPartKeepsQuotesAndOffersAnUnquotedForm()
    {
        MimeMailbox m = Assert.IsType<MimeMailbox>(Parse("To: \"john q\"@x.example\r\n").GetAddresses("To")[0]);
        Assert.Equal("\"john q\"@x.example", m.Address);
        Assert.Equal("john q@x.example", m.UnquotedAddress);
    }

    [Fact]
    public void OnlyTheFirstFieldOfAHeaderIsRead()
    {
        MimeEntity e = Parse("To: one@x.example\r\nTo: two@x.example\r\nDate: Mon, 14 May 2001 23:39:00 +0000\r\nDate: 1 Jan 2000 00:00:00 +0000\r\n");
        Assert.Equal("one@x.example", Assert.IsType<MimeMailbox>(e.GetAddresses("To").Single()).Address);
        Assert.Equal(2001, e.GetDate()!.Value.Utc.Year);
    }

    private static string Dt(string value)
    {
        MimeDate? d = Parse("Date: " + value + "\r\n").GetDate();
        return d is null ? "none" : d.Value.Utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") + " " + (d.Value.OffsetMinutes?.ToString() ?? "?");
    }

    [Theory]
    [InlineData("D1", "Mon, 14 May 2001 23:39:00 +0000", "2001-05-14T23:39:00Z 0")]
    [InlineData("D1", "Tue, 14 May 2001 23:39:00 +0000", "2001-05-14T23:39:00Z 0")]
    [InlineData("D1", "14 may 2001 23:39 EST", "2001-05-15T04:39:00Z -300")]
    [InlineData("D1", "Mon, 14 May 2001 23:39:00 +0000 (UTC)", "2001-05-14T23:39:00Z 0")]
    [InlineData("D1", "Mon, 14 May 2001 23:39:00 +1400", "2001-05-14T09:39:00Z 840")]
    [InlineData("D2", "Fri, 4 Jan 49 08:00:00 +0000", "2049-01-04T08:00:00Z 0")]
    [InlineData("D2", "Fri, 4 Jan 50 08:00:00 +0000", "2050-01-04T08:00:00Z 0")]
    [InlineData("D2", "Fri, 4 Jan 68 08:00:00 +0000", "2068-01-04T08:00:00Z 0")]
    [InlineData("D2", "Fri, 4 Jan 69 08:00:00 +0000", "1969-01-04T08:00:00Z 0")]
    [InlineData("D2", "Fri, 4 Jan 99 08:00:00 +0000", "1999-01-04T08:00:00Z 0")]
    [InlineData("D2", "Fri, 4 Jan 00 08:00:00 +0000", "2000-01-04T08:00:00Z 0")]
    [InlineData("D3", "Mon, 14 May 101 23:39:00 +0000", "2001-05-14T23:39:00Z 0")]
    [InlineData("D4", "Mon, 14 May 2001 23:39:00 -0000", "2001-05-14T23:39:00Z ?")]
    [InlineData("D4", "Mon, 14 May 2001 23:39:00 Z", "2001-05-14T23:39:00Z ?")]
    [InlineData("D4", "Mon, 14 May 2001 23:39:00", "2001-05-14T23:39:00Z ?")]
    [InlineData("D5", "Mon, 14 May 2001 23:59:60 +0000", "2001-05-14T23:59:59Z 0")]
    [InlineData("D6", "Mon, 14 May 2001 23:39:00 +2400", "2001-05-14T23:39:00Z ?")]
    [InlineData("D7", "2001-05-14 23:39:00", "none")]
    [InlineData("D8", "Mon, 14 May 2001 24:00:00 +0000", "none")]
    [InlineData("D9", "Sun, 1 Jan 1899 00:00:00 +0000", "1899-01-01T00:00:00Z 0")]
    [InlineData("D10", "Mon, 14 May 2001 23:39:00 PDT", "2001-05-15T06:39:00Z -420")]
    [InlineData("D11", "Mon, 14 May 2001 23:39:00.5 +0000", "2001-05-14T23:39:00Z 0")]
    [InlineData("D12", "garbage", "none")]
    [InlineData("D12", "2001-05-14T23:39:00Z", "none")]
    [InlineData("D12", "Mon, 31 Apr 2001 23:39:00 +0000", "none")]
    [InlineData("D12", "", "none")]
    [InlineData("D12", "Mon, 0 May 2001 23:39:00 +0000", "none")]
    [InlineData("D13", "Fri, 4 Jan 0002 08:00:00 +0000", "2002-01-04T08:00:00Z 0")]
    public void DateResolutions(string entry, string value, string expected)
    {
        Assert.False(string.IsNullOrEmpty(entry));
        Assert.Equal(expected, Dt(value));
    }

    [Fact]
    public void DateKeepsItsSourceText()
    {
        MimeDate d = Parse("Date: Mon, 14 May 2001 23:39:00 +0200 (CEST)\r\n").GetDate()!.Value;
        Assert.Contains("23:39:00 +0200", d.Source);
        Assert.Equal(120, d.OffsetMinutes);
    }

    private static string Fn(string headers) => Parse(headers).FileName ?? "(none)";

    [Theory]
    [InlineData("N1", "Content-Disposition: attachment; filename=\"=?utf-8?q?caf=C3=A9.txt?=\"\r\n", "caf\u00e9.txt")]
    [InlineData("N2", "Content-Disposition: attachment; filename=plain.txt; filename*=utf-8''ext%C3%A9.txt\r\n", "ext\u00e9.txt")]
    [InlineData("N3", "Content-Disposition: attachment; filename*0*=utf-8''a%C3; filename*1*=%A9b.txt\r\n", "a\u00e9b.txt")]
    [InlineData("N3", "Content-Disposition: attachment; filename*1=b; filename*0=a\r\n", "ab")]
    [InlineData("N5", "Content-Disposition: attachment; filename*=caf%C3%A9.txt\r\n", "caf\u00e9.txt")]
    [InlineData("N6", "Content-Disposition: attachment; filename*1=b.txt; filename*2=c\r\n", "b.txtc")]
    [InlineData("N7", "Content-Disposition: attachment; filename=one.txt; filename=two.txt\r\n", "one.txt")]
    [InlineData("N8", "Content-Disposition: attachment; filename=has space.txt\r\n", "has space.txt")]
    [InlineData("N9", "Content-Type: text/plain; name=type.txt\r\nContent-Disposition: attachment; filename=disp.txt\r\n", "disp.txt")]
    [InlineData("N9", "Content-Type: text/plain; name=type.txt\r\nContent-Disposition: attachment\r\n", "type.txt")]
    [InlineData("N10", "Content-Disposition: attachment; filename=\"../../etc/passwd\"\r\n", "../../etc/passwd")]
    public void FileNameResolutions(string entry, string headers, string expected)
    {
        Assert.False(string.IsNullOrEmpty(entry));
        Assert.Equal(expected, Fn(headers));
    }

    [Theory]
    [InlineData("C1", "Content-Type: text\r\n", "text/plain")]
    [InlineData("C1", "Content-Type: text/\r\n", "text/plain")]
    [InlineData("C1", "Content-Type: ???\r\n", "text/plain")]
    [InlineData("C3", "Content-Type: text/plain (a comment); charset=us-ascii (another)\r\n", "text/plain")]
    [InlineData("C4", "Content-Type: TEXT/Plain; CHARSET=UTF-8\r\n", "text/plain")]
    public void MediaTypeResolutions(string entry, string headers, string expected)
    {
        Assert.False(string.IsNullOrEmpty(entry));
        Assert.Equal(expected, Parse(headers).MediaType);
    }

    [Fact]
    public void ContentTypeParameterDetails()
    {
        MimeEntity e = Parse("Content-Type: text/plain (a comment); charset=us-ascii (another); charset=latin1; x=\r\n");
        Assert.Equal("us-ascii", e.GetContentTypeParameter("charset")); // C3, C4: comments dropped, first duplicate wins
        MimeEntity m = Parse("Content-Type: multipart/mixed; boundary=a/b=c\r\n");
        Assert.Equal("a/b=c", m.GetContentTypeParameter("boundary")); // B12: an unquoted value runs to ';'
    }

    [Fact]
    public void MessageIdsAreReadWithoutAngleBrackets()
    {
        MimeEntity e = Parse("Message-ID: <id@example.com>\r\nIn-Reply-To: <a@x> <b@y>\r\nReferences: <r1@x>\r\n <r2@y>\r\nContent-ID: <c@z>\r\n");
        Assert.Equal("id@example.com", e.GetMessageId());
        Assert.Equal(["a@x", "b@y"], e.GetInReplyTo());
        Assert.Equal(["r1@x", "r2@y"], e.GetReferences());
        Assert.Equal("c@z", e.GetContentId());
    }

    [Fact]
    public void MimeVersionAndDescription()
    {
        MimeEntity e = Parse("MIME-Version: 1.0 (produced by x)\r\nContent-Description: =?utf-8?q?caf=C3=A9?=\r\n");
        Assert.Equal((1, 0), e.MimeVersion);
        Assert.Equal("caf\u00e9", e.ContentDescription);
        Assert.Null(Parse("MIME-Version: one\r\n").MimeVersion);
    }
}
