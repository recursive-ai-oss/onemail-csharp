using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Mime.Reading;
using static RecursiveAI.OneMail.Tests.Mime.T;

namespace RecursiveAI.OneMail.Tests.Mime;

public class MimeReaderTests
{
    public static TheoryData<string> Samples()
    {
        var d = new TheoryData<string>
        {
            "",
            "\r\n",
            "Subject: hi\r\n\r\nbody\r\n",
            "Subject: hi\r\n\r\nbody",
            "Subject: hi",
            "Subject: hi\n\nunix body\n",
            "no header at all\r\nsecond line\r\n",
            " leading fold\r\n\r\nbody",
            "Subject: a\r\n folded\r\n\tmore\r\nTo: x\r\n\r\n",
            Crlf("Content-Type: multipart/mixed; boundary=\"b1\"", "", "preamble", "--b1", "Content-Type: text/plain", "", "one", "--b1", "", "two", "--b1--", "epilogue"),
            Crlf("Content-Type: multipart/mixed; boundary=b1", "", "--b1", "", "no close"),
            Crlf("Content-Type: multipart/mixed; boundary=b1", "", "--b1--"),
            "Content-Type: multipart/mixed; boundary=b1\r\n\r\n--b1\r\n\r\nx\r\n--b1--",
            "Content-Type: multipart/mixed; boundary=b1\n\n--b1\n\nx\n--b1--\n",
            Crlf("Content-Type: multipart/mixed; boundary=out", "", "--out", "Content-Type: multipart/alternative; boundary=in", "", "--in", "", "a", "--in", "", "b", "--in--", "--out", "", "c", "--out--"),
            Crlf("Content-Type: multipart/mixed; boundary=out", "", "--out", "Content-Type: multipart/alternative; boundary=in", "", "--in", "", "a", "--out", "", "outer wins", "--out--"),
            Crlf("Content-Type: multipart/mixed; boundary=out", "", "--out", "Content-Type: message/rfc822", "", "Subject: inner", "", "inner body", "--out--"),
            Crlf("Content-Type: multipart/digest; boundary=d", "", "--d", "", "Subject: digest item", "", "text", "--d--"),
            Crlf("Content-Type: multipart/mixed; boundary=b", "", "--b  \t", "", "padding ok", "--b--  "),
            Crlf("Content-Type: multipart/mixed; boundary=b", "", "--b", "", "--bxyz is not a delimiter", "--b--"),
            Crlf("Content-Type: multipart/mixed", "", "--b", "", "no boundary param"),
            "Content-Type: text/plain\r\n\r\n--b\r\nnot multipart\r\n",
            "A: \xe9\xe8\r\nB: \u00c3\u00a9\r\n\r\n",
            "A: bare\rcr\r\nB: nul\0\r\n\r\n",
            "Content-Type: multipart/mixed; boundary=\"has space\"\r\n\r\n--has space\r\n\r\nx\r\n--has space--\r\n",
        };
        return d;
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void EventsCoverTheInputExactly(string text)
    {
        byte[] data = B(text);
        using var r = new MimeReader(data);
        (byte[] covered, _) = Cover(r);
        Assert.Equal(data, covered);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void StreamingInAnyChunkSizeGivesTheSameEvents(string text)
    {
        byte[] data = B(text);
        using var mem = new MimeReader(data);
        List<string> expected = Normalize(mem);
        foreach (int chunk in new[] { 1, 2, 3, 7, 4096 })
        {
            using var r = new MimeReader(new ChunkedStream(data, chunk));
            Assert.Equal(expected, Normalize(r));
        }
    }

    // Body, preamble and epilogue text may arrive in differently sized chunks; everything else must be identical.
    private static List<string> Normalize(MimeReader r)
    {
        var structure = new List<string>();
        var defects = new List<string>();
        MimeEventKind prev = MimeEventKind.None;
        while (r.Read())
        {
            bool chunk = r.Kind is MimeEventKind.Preamble or MimeEventKind.Body or MimeEventKind.Epilogue;
            if (r.Kind == MimeEventKind.Defect)
            {
                defects.Add(Describe(r));
            }
            else if (chunk && prev == r.Kind)
            {
                continue;
            }
            else
            {
                structure.Add(chunk ? $"{r.Kind}@{r.Offset} d{r.Depth}" : Describe(r));
                prev = r.Kind;
            }
        }

        defects.Sort(StringComparer.Ordinal);
        structure.AddRange(defects);
        return structure;
    }

    [Fact]
    public async Task AsyncReadMatchesSyncRead()
    {
        byte[] data = B(Crlf("Content-Type: multipart/mixed; boundary=b", "", "--b", "", "x", "--b--"));
        using var s = new MimeReader(data);
        List<string> expected = Events(s);
        using var a = new MimeReader(new ChunkedStream(data, 1000));
        var got = new List<string>();
        while (await a.ReadAsync(TestContext.Current.CancellationToken))
        {
            got.Add(Describe(a));
        }

        Assert.Equal(expected, got);
    }

    [Fact]
    public void EventSequenceOfASimpleMultipart()
    {
        byte[] data = B(Crlf("Content-Type: multipart/mixed; boundary=b", "", "pre", "--b", "Subject: s", "", "body", "--b--", "post"));
        using var r = new MimeReader(data);
        var kinds = new List<MimeEventKind>();
        while (r.Read())
        {
            kinds.Add(r.Kind);
        }

        Assert.Equal(
            [
                MimeEventKind.EntityStart, MimeEventKind.Header, MimeEventKind.HeadersEnd, MimeEventKind.Preamble,
                MimeEventKind.Delimiter, MimeEventKind.EntityStart, MimeEventKind.Header, MimeEventKind.HeadersEnd,
                MimeEventKind.Body, MimeEventKind.EntityEnd, MimeEventKind.Delimiter, MimeEventKind.Epilogue, MimeEventKind.EntityEnd,
            ],
            kinds);
    }

    [Fact]
    public void HeaderEventsExposeNameAndRawValue()
    {
        using var r = new MimeReader(B("Subject :  a\r\n b\r\n\r\n"));
        Assert.True(r.Read());
        Assert.True(r.Read());
        Assert.Equal(MimeEventKind.Header, r.Kind);
        Assert.Equal("Subject", System.Text.Encoding.ASCII.GetString(r.HeaderName));
        Assert.Equal("  a\r\n b", System.Text.Encoding.ASCII.GetString(r.HeaderValue));
        Assert.Equal("Subject :  a\r\n b\r\n", System.Text.Encoding.ASCII.GetString(r.Raw));
    }

    [Fact]
    public void ReaderCanBeResetAndReused()
    {
        using var r = new MimeReader(B("A: 1\r\n\r\nx"));
        List<string> first = Events(r);
        r.Reset(B("A: 1\r\n\r\nx"));
        Assert.Equal(first, Events(r));
    }

    [Fact]
    public void ReadsFromARandomAccessSource()
    {
        byte[] data = B("A: 1\r\n\r\nbody");
        using var src = new RecursiveAI.OneMail.Sources.MemorySource(data);
        using var r = new MimeReader(src);
        Assert.Equal(data, Cover(r).Covered);
    }

    // ---- defects: each kind with a small input of our own ----

    private static void Has(string message, MimeDefectKind kind, MimeReaderOptions? options = null)
    {
        List<MimeDefect> defects = Defects(message, options);
        Assert.Contains(defects, d => d.Kind == kind);
    }

    [Fact]
    public void MissingHeaderBodySeparator() => Has("Subject: x", MimeDefectKind.MissingHeaderBodySeparator);

    [Fact]
    public void NonHeaderLineEndsHeadersAndStartsBody()
    {
        using var r = new MimeReader(B("Subject: x\r\nthis is body\r\n"));
        List<string> ev = Events(r);
        Assert.Contains(ev, e => e.StartsWith("HeadersEnd@12+0", StringComparison.Ordinal));
        Assert.Contains(ev, e => e.StartsWith("Defect MissingHeaderBodySeparator@12", StringComparison.Ordinal));
    }

    [Fact]
    public void HeaderContinuationWithoutHeader() => Has(" x\r\n\r\n", MimeDefectKind.HeaderContinuationWithoutHeader);

    [Fact]
    public void BareCarriageReturnInHeader() => Has("A: x\ry\r\n\r\n", MimeDefectKind.BareCarriageReturnInHeader);

    [Fact]
    public void NulInHeader() => Has("A: x\0y\r\n\r\n", MimeDefectKind.NulInHeader);

    [Fact]
    public void Utf8Header() => Has("A: caf\u00c3\u00a9\r\n\r\n", MimeDefectKind.Utf8Header);

    [Fact]
    public void AmbiguousEightBitHeader() => Has("A: caf\u00e9\r\n\r\n", MimeDefectKind.AmbiguousEightBitHeader);

    [Fact]
    public void WhitespaceBeforeHeaderColon() => Has("Subject : x\r\n\r\n", MimeDefectKind.WhitespaceBeforeHeaderColon);

    [Fact]
    public void AmbiguousDuplicateContentType() => Has("Content-Type: text/plain\r\nContent-Type: text/html\r\n\r\n", MimeDefectKind.AmbiguousDuplicateContentType);

    [Fact]
    public void AmbiguousDuplicateTransferEncoding() => Has("Content-Transfer-Encoding: 7bit\r\nContent-Transfer-Encoding: base64\r\n\r\n", MimeDefectKind.AmbiguousDuplicateTransferEncoding);

    [Fact]
    public void InvalidContentType() => Has("Content-Type: nonsense\r\n\r\n", MimeDefectKind.InvalidContentType);

    [Fact]
    public void UnknownTransferEncoding() => Has("Content-Transfer-Encoding: x-weird\r\n\r\n", MimeDefectKind.UnknownTransferEncoding);

    [Fact]
    public void MultipartWithoutBoundary() => Has("Content-Type: multipart/mixed\r\n\r\n--x\r\n", MimeDefectKind.MultipartWithoutBoundary);

    [Fact]
    public void EmptyBoundary() => Has("Content-Type: multipart/mixed; boundary=\"\"\r\n\r\n", MimeDefectKind.EmptyBoundary);

    [Fact]
    public void BoundaryTooLong() => Has("Content-Type: multipart/mixed; boundary=" + new string('x', 300) + "\r\n\r\n", MimeDefectKind.BoundaryTooLong);

    [Fact]
    public void AmbiguousDuplicateBoundary() =>
        Has(Crlf("Content-Type: multipart/mixed; boundary=b", "", "--b", "Content-Type: multipart/mixed; boundary=b", "", "--b--"), MimeDefectKind.AmbiguousDuplicateBoundary);

    [Fact]
    public void AmbiguousBoundaryLikeLine() =>
        Has(Crlf("Content-Type: multipart/mixed; boundary=b", "", "--b", "", "--bcd", "--b--"), MimeDefectKind.AmbiguousBoundaryLikeLine);

    [Fact]
    public void AmbiguousEncodedComposite() =>
        Has("Content-Type: multipart/mixed; boundary=b\r\nContent-Transfer-Encoding: base64\r\n\r\nQUJD\r\n", MimeDefectKind.AmbiguousEncodedComposite);

    [Fact]
    public void MissingFinalBoundaryAtEndOfInput() =>
        Has(Crlf("Content-Type: multipart/mixed; boundary=b", "", "--b", "", "x"), MimeDefectKind.MissingFinalBoundary);

    [Fact]
    public void OuterDelimiterClosesInnerMultipartWithOneDefect()
    {
        string m = Crlf("Content-Type: multipart/mixed; boundary=out", "", "--out", "Content-Type: multipart/mixed; boundary=in", "", "--in", "", "a", "--out--");
        List<MimeDefect> d = Defects(m);
        Assert.Single(d, x => x.Kind == MimeDefectKind.MissingFinalBoundary);
    }

    [Fact]
    public void ClosedMultipartHasNoMissingFinalBoundary() =>
        Assert.DoesNotContain(Defects(Crlf("Content-Type: multipart/mixed; boundary=b", "", "--b", "", "x", "--b--")), d => d.Kind == MimeDefectKind.MissingFinalBoundary);

    [Fact]
    public void CleanMessageHasNoDefects() =>
        Assert.Empty(Defects(Crlf("From: a@example.com", "Subject: hello", "", "body")));

    [Fact]
    public void DefectOffsetsPointIntoTheInput()
    {
        string m = "A: 1\r\nB : 2\r\n\r\n";
        MimeDefect d = Assert.Single(Defects(m));
        Assert.Equal(MimeDefectKind.WhitespaceBeforeHeaderColon, d.Kind);
        Assert.Equal(6, d.Offset);
    }

    // ---- structure ----

    [Fact]
    public void DelimiterOwnsThePrecedingLineEnd()
    {
        using var r = new MimeReader(B("Content-Type: multipart/mixed; boundary=b\r\n\r\npre\r\n--b\r\n\r\nx\r\n--b--\r\n"));
        string all = string.Join("|", Events(r));
        Assert.Contains("Preamble@45+3", all, StringComparison.Ordinal);
        Assert.Contains("Delimiter@48+7", all, StringComparison.Ordinal); // \r\n--b\r\n
    }

    [Fact]
    public void EmbeddedMessageIsNested()
    {
        using var r = new MimeReader(B(Crlf("Content-Type: message/rfc822", "", "Subject: inner", "", "x")));
        List<string> ev = Events(r);
        Assert.Equal(2, ev.Count(e => e.StartsWith("EntityStart", StringComparison.Ordinal)));
    }

    [Fact]
    public void EmbeddedMessagesCanBeLeftAsLeaves()
    {
        using var r = new MimeReader(B(Crlf("Content-Type: message/rfc822", "", "Subject: inner", "", "x")), new MimeReaderOptions { ParseEmbeddedMessages = false });
        Assert.Single(Events(r), e => e.StartsWith("EntityStart", StringComparison.Ordinal));
    }

    [Fact]
    public void DefaultTypeInsideDigestIsAMessage()
    {
        using var r = new MimeReader(B(Crlf("Content-Type: multipart/digest; boundary=d", "", "--d", "", "Subject: s", "", "text", "--d--")));
        Assert.Equal(3, Events(r).Count(e => e.StartsWith("EntityStart", StringComparison.Ordinal)));
    }

    // ---- limits and hostile input ----

    [Fact]
    public void DeepNestingEndsInADepthDefectAndKeepsAllBytes()
    {
        var sb = new System.Text.StringBuilder();
        int levels = 500;
        for (int i = 0; i < levels; i++)
        {
            sb.Append("Content-Type: multipart/mixed; boundary=b").Append(i).Append("\r\n\r\n--b").Append(i).Append("\r\n");
        }

        sb.Append("\r\nleaf\r\n");
        byte[] data = B(sb.ToString());
        using var r = new MimeReader(data);
        (byte[] covered, List<MimeDefect> defects) = Cover(r);
        Assert.Equal(data, covered);
        Assert.Contains(defects, d => d.Kind == MimeDefectKind.DepthLimitExceeded);
    }

    [Fact]
    public void DeepNestingLimitIsConfigurable()
    {
        string m = Crlf("Content-Type: multipart/mixed; boundary=a", "", "--a", "Content-Type: multipart/mixed; boundary=b", "", "--b", "", "x", "--b--", "--a--");
        Has(m, MimeDefectKind.DepthLimitExceeded, new MimeReaderOptions { Limits = new MimeLimits { MaxDepth = 1 } });
        Assert.DoesNotContain(Defects(m, new MimeReaderOptions { Limits = new MimeLimits { MaxDepth = 2 } }), d => d.Kind == MimeDefectKind.DepthLimitExceeded);
    }

    [Fact]
    public void EndlessBoundariesStopAtTheEntityLimit()
    {
        var sb = new System.Text.StringBuilder("Content-Type: multipart/mixed; boundary=b\r\n\r\n");
        for (int i = 0; i < 5000; i++)
        {
            sb.Append("--b\r\n\r\nx\r\n");
        }

        byte[] data = B(sb.ToString());
        using var r = new MimeReader(data, new MimeReaderOptions { Limits = new MimeLimits { MaxEntityCount = 100 } });
        (byte[] covered, List<MimeDefect> defects) = Cover(r);
        Assert.Equal(data, covered);
        Assert.Single(defects, d => d.Kind == MimeDefectKind.EntityLimitExceeded);
    }

    [Fact]
    public void HugeHeaderBlockEndsInADefect()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 2000; i++)
        {
            sb.Append("X-").Append(i).Append(": ").Append(new string('v', 100)).Append("\r\n");
        }

        sb.Append("\r\nbody");
        byte[] data = B(sb.ToString());
        var opts = new MimeReaderOptions { Limits = new MimeLimits { MaxHeaderBlockBytes = 10_000 } };
        foreach (bool streamed in new[] { false, true })
        {
            using var r = streamed ? new MimeReader(new ChunkedStream(data, 777), opts) : new MimeReader(data, opts);
            (byte[] covered, List<MimeDefect> defects) = Cover(r);
            Assert.Equal(data, covered);
            Assert.Contains(defects, d => d.Kind == MimeDefectKind.HeaderLimitExceeded);
        }
    }

    [Fact]
    public void EndlessHeaderLineEndsInAFieldDefectWithoutGrowingTheBufferWithoutBound()
    {
        byte[] data = new byte[3_000_000];
        Array.Fill(data, (byte)'a');
        data[0] = (byte)'A';
        data[1] = (byte)':';
        using var r = new MimeReader(new ChunkedStream(data, 100_000));
        (byte[] covered, List<MimeDefect> defects) = Cover(r);
        Assert.Equal(data, covered);
        Assert.Contains(defects, d => d.Kind == MimeDefectKind.HeaderFieldLimitExceeded);
    }

    [Fact]
    public void TooManyHeadersEndsInADefect()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 50; i++)
        {
            sb.Append("A: b\r\n");
        }

        Has(sb.ToString() + "\r\n", MimeDefectKind.HeaderLimitExceeded, new MimeReaderOptions { Limits = new MimeLimits { MaxHeadersPerEntity = 10 } });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TotalSizeLimitStopsReading(bool streamed)
    {
        byte[] data = B("A: 1\r\n\r\n" + new string('x', 5000));
        var opts = new MimeReaderOptions { Limits = new MimeLimits { MaxTotalBytes = 1000 } };
        using var r = streamed ? new MimeReader(new ChunkedStream(data, 300), opts) : new MimeReader(data, opts);
        (byte[] covered, List<MimeDefect> defects) = Cover(r);
        Assert.Equal(data.AsSpan(0, 1000).ToArray(), covered);
        Assert.True(r.IsTruncated);
        Assert.Single(defects, d => d.Kind == MimeDefectKind.TotalSizeLimitExceeded);
    }

    [Fact]
    public void InputExactlyAtTheSizeLimitIsNotTruncated()
    {
        byte[] data = B("A: 1\r\n\r\nxx");
        using var r = new MimeReader(new ChunkedStream(data, 3), new MimeReaderOptions { Limits = new MimeLimits { MaxTotalBytes = data.Length } });
        (byte[] covered, List<MimeDefect> defects) = Cover(r);
        Assert.Equal(data, covered);
        Assert.False(r.IsTruncated);
        Assert.Empty(defects);
    }

    [Fact]
    public void BinaryNoiseNeverThrows()
    {
        var rng = new Random(12345);
        for (int i = 0; i < 300; i++)
        {
            byte[] data = new byte[rng.Next(0, 600)];
            rng.NextBytes(data);
            // Sprinkle structure so the interesting paths run.
            if (data.Length > 80 && i % 2 == 0)
            {
                B("Content-Type: multipart/mixed; boundary=b\r\n\r\n--b\r\n").CopyTo(data, 0);
            }

            using var r = new MimeReader(data);
            Assert.Equal(data, Cover(r).Covered);
            using var s = new MimeReader(new ChunkedStream(data, 1 + (i % 9)));
            Assert.Equal(data, Cover(s).Covered);
        }
    }

    [Fact]
    public void ParseMatchesBetweenMemoryAndStream()
    {
        byte[] data = B(Crlf("Content-Type: multipart/mixed; boundary=b", "", "--b", "", "x", "--b--"));
        MimeEntity a = MimeEntity.Parse(data);
        MimeEntity b = MimeEntity.Load(new ChunkedStream(data, 4));
        Assert.Equal(a.ToArray(), b.ToArray());
    }
}
