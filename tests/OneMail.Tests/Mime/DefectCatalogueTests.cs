using System.Text;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Mime.Reading;
using static RecursiveAI.OneMail.Tests.Mime.T;

namespace RecursiveAI.OneMail.Tests.Mime;

/// <summary>One minimised message per defect kind that the reader, the text layer and the structured-header parsers raise.</summary>
public class DefectCatalogueTests
{
    private const string Hdr = "From: a@x.example\r\n";

    /// <summary>Parses a message and runs every structured accessor on its root, returning all the defects found.</summary>
    internal static List<MimeDefectKind> Collect(string message)
    {
        MimeEntity root = MimeEntity.Parse(B(message));
        var all = new List<MimeDefect>(root.EnumerateDefects());
        Walk(root, all);
        return all.ConvertAll(d => d.Kind);
    }

    private static void Walk(MimeEntity e, List<MimeDefect> into)
    {
        foreach (MimeHeader h in e.Headers)
        {
            _ = h.GetValue(into);
        }

        foreach (string name in new[] { "From", "To", "Cc", "Reply-To", "Sender" })
        {
            _ = e.GetAddresses(name, into);
        }

        _ = e.GetDate(into);
        _ = e.GetMessageId(into);
        _ = e.GetReferences(into);
        _ = e.GetContentTypeParameters(into);
        _ = e.GetDispositionParameters(into);
        if (e is MimeMultipart m)
        {
            foreach (MimeEntity c in m.Children)
            {
                Walk(c, into);
            }
        }
        else if (e is MimeMessagePart { Message: { } inner })
        {
            Walk(inner, into);
        }
    }

    private static string Msg(string header, string body = "body\r\n") => Hdr + header + "\r\n" + body;

    public static TheoryData<MimeDefectKind, string> Cases() => new()
    {
        // reader
        { MimeDefectKind.InvalidParameter, Msg("Content-Type: text/plain; charset\r\n") },
        { MimeDefectKind.MissingStartBoundary, Msg("Content-Type: multipart/mixed; boundary=b\r\n", "just text\r\n") },
        { MimeDefectKind.EpilogueHasDelimiter, Msg("Content-Type: multipart/mixed; boundary=b\r\n", "--b\r\n\r\nx\r\n--b--\r\n--b\r\nagain\r\n") },
        { MimeDefectKind.InvalidHeaderName, Msg("X Bad Name: v\r\n") },
        { MimeDefectKind.MixedLineEndings, "A: 1\r\nB: 2\n\r\nbody" },
        { MimeDefectKind.ControlCharacterInHeader, Msg("Subject: a\u0001b\r\n") },
        { MimeDefectKind.BoundaryHasTrailingSpace, Msg("Content-Type: multipart/mixed; boundary=\"b \"\r\n", "--b \r\n\r\nx\r\n--b --\r\n") },

        // text layer, through header values and parameters
        { MimeDefectKind.InvalidCharsetBytes, Msg("Subject: =?utf-8?q?=FF?=\r\n") },
        { MimeDefectKind.AmbiguousUnknownCharset, Msg("Subject: =?x-no-such-charset?q?abc?=\r\n") },
        { MimeDefectKind.MalformedEncodedWord, Msg("Subject: =?utf-8?x?abc?=\r\n") },
        { MimeDefectKind.InvalidEncodedWordBase64, Msg("Subject: =?utf-8?b?@@@@?=\r\n") },
        { MimeDefectKind.InvalidEncodedWordQuotedPrintable, Msg("Subject: =?utf-8?q?a=ZZ?=\r\n") },
        { MimeDefectKind.EncodedWordTooLong, Msg("Subject: =?utf-8?q?" + new string('a', 70) + "?=\r\n") },
        { MimeDefectKind.AmbiguousEncodedWordInQuotedString, Msg("Content-Type: application/x; name=\"=?utf-8?q?a?=\"\r\n") },
        { MimeDefectKind.AmbiguousSplitMultibyte, Msg("Subject: =?utf-8?q?=C3?= =?utf-8?q?=A9?=\r\n") },
        { MimeDefectKind.InvalidParameterEscape, Msg("Content-Type: application/x; name*=utf-8''a%ZZ\r\n") },
        { MimeDefectKind.MalformedExtendedParameter, Msg("Content-Type: application/x; name*=nocharset\r\n") },
        { MimeDefectKind.AmbiguousParameterSegments, Msg("Content-Type: application/x; name*1=b; name*2=c\r\n") },
        { MimeDefectKind.DuplicateParameterSegment, Msg("Content-Type: application/x; name*0=a; name*0=b\r\n") },
        { MimeDefectKind.AmbiguousParameterForms, Msg("Content-Type: application/x; name=plain; name*=utf-8''ext\r\n") },
        { MimeDefectKind.DuplicateParameter, Msg("Content-Type: text/plain; charset=a; charset=b\r\n") },
        { MimeDefectKind.TextLimitExceeded, Msg("Content-Type: application/x; " + string.Join("; ", Enumerable.Range(0, 1030).Select(i => $"p{i}=v")) + "\r\n") },

        // addresses
        { MimeDefectKind.AmbiguousUnquotedCommaInPhrase, Msg("To: Doe, John <j@x.example>\r\n") },
        { MimeDefectKind.MissingAngleBrackets, Msg("To: john@x.example, John Doe jane@x.example\r\n") },
        { MimeDefectKind.ObsoleteRoute, Msg("To: <@a.example:j@x.example>\r\n") },
        { MimeDefectKind.EmptyAddress, Msg("To: Bounce <>\r\n") },
        { MimeDefectKind.MissingDomain, Msg("To: john\r\n") },
        { MimeDefectKind.UnclosedAngle, Msg("To: John <j@x.example\r\n") },
        { MimeDefectKind.UnterminatedQuote, Msg("To: \"John <j@x.example>\r\n") },
        { MimeDefectKind.AmbiguousAdjacentAngleAddrs, Msg("To: <a@x.example><b@y.example>\r\n") },
        { MimeDefectKind.UnparsedAddressText, Msg("To: a@x.example; b@y.example\r\n") },
        { MimeDefectKind.ObsoleteLocalPart, Msg("To: .a@x.example\r\n") },
        { MimeDefectKind.InvalidAddressSyntax, Msg("To: <<>>\r\n") },
        { MimeDefectKind.InvalidAddressSyntax, Msg("To: <a@b@gw.example>\r\n") },
        { MimeDefectKind.InvalidHeaderName, Msg("", "").Replace("\r\n\r\n", "\r\n: empty name\r\n\r\n") },

        // dates
        { MimeDefectKind.WeekdayMismatch, Msg("Date: Tue, 14 May 2001 23:39:00 +0000\r\n") },
        { MimeDefectKind.AmbiguousTwoDigitYear, Msg("Date: 14 May 01 23:39:00 +0000\r\n") },
        { MimeDefectKind.ThreeDigitYear, Msg("Date: 14 May 101 23:39:00 +0000\r\n") },
        { MimeDefectKind.UnknownTimeZone, Msg("Date: 14 May 2001 23:39:00 -0000\r\n") },
        { MimeDefectKind.InvalidTimeZone, Msg("Date: 14 May 2001 23:39:00 +2400\r\n") },
        { MimeDefectKind.LeapSecond, Msg("Date: 14 May 2001 23:59:60 +0000\r\n") },
        { MimeDefectKind.FractionalSeconds, Msg("Date: 14 May 2001 23:39:00.5 +0000\r\n") },
        { MimeDefectKind.InvalidDate, Msg("Date: not a date\r\n") },

        // identifiers
        { MimeDefectKind.InvalidMessageId, Msg("Message-ID: not an id\r\n") },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void RaisesTheDefect(MimeDefectKind kind, string message)
    {
        List<MimeDefectKind> kinds = Collect(message);
        Assert.Contains(kind, kinds);
    }

    [Fact]
    public void CleanMessageRaisesNothing()
    {
        string m = "From: Alice <alice@example.com>\r\nTo: Bob <bob@example.com>, \"Q, R\" <q@example.com>\r\n" +
                   "Date: Mon, 14 May 2001 23:39:00 +0000\r\nMessage-ID: <id@example.com>\r\nSubject: =?utf-8?q?caf=C3=A9?=\r\n" +
                   "Content-Type: text/plain; charset=utf-8; name*=utf-8''a%20b\r\n\r\nbody\r\n";
        Assert.Empty(Collect(m));
    }
}

public class EnvelopeLineTests
{
    [Theory]
    [InlineData("From alice@example.com Mon May 14 23:39:00 2001\r\nSubject: hi\r\n\r\nbody\r\n")]
    [InlineData("From alice@example.com Mon May 14 23:39:00 2001\nSubject: hi\n\nbody\n")]
    [InlineData("From alice@example.com")]
    public void AnMboxFromLineIsNotAHeaderAndSurvivesARoundTrip(string text)
    {
        byte[] data = T.B(text);
        MimeEntity e = MimeEntity.Parse(data);
        Assert.StartsWith("From alice@example.com", System.Text.Encoding.Latin1.GetString(e.EnvelopeLine.Span));
        Assert.DoesNotContain(e.Headers, h => h.Name.StartsWith("From", StringComparison.Ordinal));
        Assert.Equal(data, e.ToArray());
        Assert.Equal(data, e.ToArrayAssembled());
        if (text.Contains("Subject", StringComparison.Ordinal))
        {
            Assert.Equal("hi", e.Headers.GetFirst("Subject")!.Value);
        }
    }

    [Fact]
    public void OnlyTheFirstLineCounts()
    {
        MimeEntity e = MimeEntity.Parse(T.B("Subject: hi\r\nFrom alice@example.com\r\n\r\nbody"));
        Assert.True(e.EnvelopeLine.IsEmpty);
    }
}

public class DoubledDelimiterTests
{
    [Theory]
    [InlineData("Content-Type: multipart/mixed; boundary=b\r\n\r\n--b\r\n--b\r\nContent-Type: text/plain\r\n\r\none\r\n--b--\r\n", 1)]
    [InlineData("Content-Type: multipart/mixed; boundary=b\r\n\r\npre\r\n--b\r\nContent-Type: text/plain\r\n\r\none\r\n--b\r\n--b\r\n\r\ntwo\r\n--b--\r\n", 2)]
    [InlineData("Content-Type: multipart/mixed; boundary=b\r\n\r\n--b\r\n\r\none\r\n--b\r\n--b--\r\n", 1)]
    [InlineData("Content-Type: multipart/mixed; boundary=b\r\n\r\n--b\r\n", 1)]
    public void ConsecutiveDelimitersAreOneAndTheBytesSurvive(string text, int parts)
    {
        byte[] data = T.B(text);
        var root = (MimeMultipart)MimeEntity.Parse(data);
        Assert.Equal(parts, root.Children.Count);
        Assert.Equal(data, root.ToArray());
        Assert.Equal(data, root.ToArrayAssembled());
    }
}
