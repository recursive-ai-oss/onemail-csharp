using System.Text;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Mime.Reading;
using RecursiveAI.OneMail.Mime.Transfer;
using RecursiveAI.OneMail.Mime.Writing;
using RecursiveAI.OneMail.Sources;
using static RecursiveAI.OneMail.Tests.Mime.T;

namespace RecursiveAI.OneMail.Tests.Mime;

public class MimeTreeTests
{
    [Theory]
    [MemberData(nameof(MimeReaderTests.Samples), MemberType = typeof(MimeReaderTests))]
    public void UntouchedTreeWritesBackByteIdentical(string text)
    {
        byte[] data = B(text);
        MimeEntity root = MimeEntity.Parse(data);
        Assert.False(root.IsModified);
        Assert.Equal(data, root.ToArray());
        // The same bytes must come out when every part is rebuilt from the tree's own pieces, not copied as one range.
        Assert.Equal(data, root.ToArrayAssembled());
    }

    [Theory]
    [MemberData(nameof(MimeReaderTests.Samples), MemberType = typeof(MimeReaderTests))]
    public async Task WriteAsyncAndStreamLoadAgree(string text)
    {
        byte[] data = B(text);
        MimeEntity viaStream = await MimeEntity.LoadAsync(new ChunkedStream(data, 5), null, TestContext.Current.CancellationToken);
        using var ms = new MemoryStream();
        await viaStream.WriteToAsync(ms, TestContext.Current.CancellationToken);
        Assert.Equal(data, ms.ToArray());

        string path = Path.Combine(Path.GetTempPath(), "onemail-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllBytesAsync(path, data, TestContext.Current.CancellationToken);
            using FileSource fs = FileSource.Open(path);
            MimeEntity viaFile = await MimeEntity.LoadAsync(fs, null, TestContext.Current.CancellationToken);
            Assert.Equal(data, viaFile.ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    private const string Nested =
        "From: a@example.com\r\nSubject: Hello\r\nContent-Type: multipart/mixed; boundary=\"out\"\r\n\r\npreamble\r\n" +
        "--out\r\nContent-Type: multipart/alternative; boundary=in\r\n\r\n" +
        "--in\r\nContent-Type: text/plain\r\n\r\nplain\r\n--in\r\nContent-Type: text/html\r\n\r\n<p>html</p>\r\n--in--\r\n" +
        "--out\r\nContent-Type: application/octet-stream\r\nContent-Transfer-Encoding: base64\r\n\r\nSGVsbG8gd29ybGQ=\r\n" +
        "--out\r\nContent-Type: message/rfc822\r\n\r\nSubject: inner\r\n\r\ninner body\r\n--out--\r\nepilogue\r\n";

    [Fact]
    public void TreeShapeAndAccessors()
    {
        MimeEntity root = MimeEntity.Parse(B(Nested));
        var top = Assert.IsType<MimeMultipart>(root);
        Assert.Equal("multipart/mixed", top.MediaType);
        Assert.Equal("out", top.Boundary);
        Assert.Equal(3, top.Children.Count);
        Assert.Equal("preamble", Encoding.ASCII.GetString(top.GetPreamble()));
        Assert.Equal("epilogue\r\n", Encoding.ASCII.GetString(top.GetEpilogue()));
        Assert.Equal("Hello", top.Headers.GetFirst("subject")!.Value);

        var alt = Assert.IsType<MimeMultipart>(top.Children[0]);
        Assert.Equal(2, alt.Children.Count);
        Assert.Equal("plain", Encoding.ASCII.GetString(((MimePart)alt.Children[0]).GetDecodedContent()));
        Assert.Equal("text/html", alt.Children[1].MediaType);

        var bin = Assert.IsType<MimePart>(top.Children[1]);
        Assert.Equal(ContentTransferEncoding.Base64, bin.TransferEncoding);
        Assert.Equal("Hello world", Encoding.ASCII.GetString(bin.GetDecodedContent()));
        Assert.Equal("SGVsbG8gd29ybGQ=", Encoding.ASCII.GetString(Read(bin.OpenRawContent())));

        var msg = Assert.IsType<MimeMessagePart>(top.Children[2]);
        MimeEntity inner = msg.Message!;
        Assert.Equal("inner", inner.Headers.GetFirst("Subject")!.Value);
        Assert.Equal(2, inner.Depth);
        Assert.Same(msg, inner.Parent);
        Assert.Equal("text/plain", inner.MediaType);
    }

    private static byte[] Read(Stream s)
    {
        using (s)
        {
            var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }

    [Fact]
    public void ContentTypeParametersAreUnquoted()
    {
        MimeEntity e = MimeEntity.Parse(B("Content-Type: text/plain; charset=\"utf-8\"; format=flowed; name=\"a \\\"b\\\".txt\"\r\n\r\n"));
        Assert.Equal("utf-8", e.GetContentTypeParameter("CHARSET"));
        Assert.Equal("flowed", e.GetContentTypeParameter("format"));
        Assert.Equal("a \"b\".txt", e.GetContentTypeParameter("name"));
        Assert.Null(e.GetContentTypeParameter("missing"));
    }

    [Fact]
    public void DefaultsWithoutContentType()
    {
        MimeEntity e = MimeEntity.Parse(B("A: 1\r\n\r\nx"));
        Assert.Equal("text/plain", e.MediaType);
        Assert.Equal(ContentTransferEncoding.SevenBit, e.TransferEncoding);
    }

    [Fact]
    public void DigestChildDefaultsToMessage()
    {
        var top = (MimeMultipart)MimeEntity.Parse(B(Crlf("Content-Type: multipart/digest; boundary=d", "", "--d", "", "Subject: s", "", "text", "--d--")));
        var item = Assert.IsType<MimeMessagePart>(top.Children[0]);
        Assert.Equal("message/rfc822", item.MediaType);
        Assert.Equal("s", item.Message!.Headers.GetFirst("subject")!.Value);
    }

    [Fact]
    public void DefectsAreOnTheEntityTheyHappenedIn()
    {
        string m = Crlf("Content-Type: multipart/mixed; boundary=b", "", "--b", "Content-Type: nonsense", "", "x", "--b--");
        var top = (MimeMultipart)MimeEntity.Parse(B(m));
        Assert.Empty(top.Defects);
        Assert.Contains(top.Children[0].Defects, d => d.Kind == MimeDefectKind.InvalidContentType);
        Assert.Single(top.EnumerateDefects());
    }

    [Fact]
    public void ModifyingAHeaderRewritesOnlyThatEntity()
    {
        byte[] data = B(Nested);
        var top = (MimeMultipart)MimeEntity.Parse(data);
        var alt = (MimeMultipart)top.Children[0];
        var plain = alt.Children[0];
        plain.Headers.Add("X-Test", "1");

        Assert.True(plain.IsModified);
        Assert.True(alt.IsModified);
        Assert.True(top.IsModified);
        Assert.False(alt.Children[1].IsModified);
        Assert.False(top.Children[1].IsModified);

        string expected = Nested.Replace("Content-Type: text/plain\r\n\r\nplain", "Content-Type: text/plain\r\nX-Test: 1\r\n\r\nplain", StringComparison.Ordinal);
        Assert.Equal(expected, Encoding.ASCII.GetString(top.ToArray()));

        // Reparse what was written: the edit survived.
        MimeEntity again = MimeEntity.Parse(top.ToArray());
        Assert.Equal("1", ((MimeMultipart)((MimeMultipart)again).Children[0]).Children[0].Headers.GetFirst("X-Test")!.Value);
    }

    [Fact]
    public void ChangingASubjectKeepsEveryOtherByte()
    {
        byte[] data = B(Nested);
        MimeEntity root = MimeEntity.Parse(data);
        root.Headers.Set("Subject", "Changed");
        string text = Encoding.ASCII.GetString(root.ToArray());
        Assert.Equal(Nested.Replace("Subject: Hello", "Subject: Changed", StringComparison.Ordinal), text);
    }

    [Fact]
    public void AddingAHeaderToATruncatedHeaderBlockGivesItALineEnd()
    {
        MimeEntity e = MimeEntity.Parse(B("Subject: x"));
        e.Headers.Add("To", "y");
        Assert.Equal("Subject: x\r\nTo: y\r\n", Encoding.ASCII.GetString(e.ToArray()));
    }

    [Fact]
    public void NewHeadersUseTheLineEndOfTheEntity()
    {
        MimeEntity e = MimeEntity.Parse(B("A: 1\n\nbody\n"));
        e.Headers.Add("B", "2");
        Assert.Equal("A: 1\nB: 2\n\nbody\n", Encoding.ASCII.GetString(e.ToArray()));
    }

    [Fact]
    public void HeaderValuesCannotInjectHeaders()
    {
        var e = new MimePart("text/plain");
        Assert.Throws<ArgumentException>(() => e.Headers.Add("Subject", "x\r\nBcc: evil@example.com"));
        Assert.Throws<ArgumentException>(() => e.Headers.Add("Bad:Name", "x"));
    }

    [Fact]
    public void LongHeaderValuesFoldAtWordBoundaries()
    {
        var e = new MimePart("text/plain");
        string words = string.Join(" ", Enumerable.Range(0, 40).Select(i => "word" + i));
        e.Headers.Add("Subject", words);
        MimeHeader h = e.Headers.GetFirst("Subject")!;
        string raw = Encoding.ASCII.GetString(h.Raw.Span);
        Assert.All(raw.Split("\r\n", StringSplitOptions.RemoveEmptyEntries), l => Assert.True(l.Length <= 78, l));
        Assert.Equal(words, h.Value);
    }

    [Fact]
    public void EditingBodiesReEncodes()
    {
        var part = new MimePart("application/octet-stream");
        byte[] payload = Enumerable.Range(0, 200).Select(i => (byte)i).ToArray();
        part.SetDecodedContent(payload);
        Assert.Equal(ContentTransferEncoding.Base64, part.TransferEncoding);
        Assert.Equal(payload, part.GetDecodedContent());
        Assert.All(Encoding.ASCII.GetString(Read(part.OpenRawContent())).Split("\r\n"), l => Assert.True(l.Length <= 76));

        part.SetDecodedContent("caf\u00e9 =x"u8.ToArray(), ContentTransferEncoding.QuotedPrintable);
        Assert.Equal("caf=C3=A9 =3Dx", Encoding.ASCII.GetString(Read(part.OpenRawContent())));
        Assert.Equal("caf\u00e9 =x", Encoding.UTF8.GetString(part.GetDecodedContent()));
    }

    [Fact]
    public void BuildingAMultipartFromScratchWritesAParseableMessage()
    {
        var top = new MimeMultipart("mixed", "BOUND");
        top.Headers.Add("Subject", "built");
        var text = new MimePart("text/plain");
        text.SetDecodedContent("hello"u8, ContentTransferEncoding.SevenBit);
        top.Add(text);
        var att = new MimePart("application/octet-stream");
        att.SetDecodedContent([1, 2, 3, 4]);
        top.Add(att);

        string written = Encoding.ASCII.GetString(top.ToArray());
        Assert.Equal(
            "Content-Type: multipart/mixed; boundary=\"BOUND\"\r\nSubject: built\r\n\r\n--BOUND\r\nContent-Type: text/plain\r\n\r\nhello\r\n--BOUND\r\nContent-Type: application/octet-stream\r\nContent-Transfer-Encoding: base64\r\n\r\nAQIDBA==\r\n--BOUND--\r\n",
            written);

        var back = (MimeMultipart)MimeEntity.Parse(B(written));
        Assert.Equal(2, back.Children.Count);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, ((MimePart)back.Children[1]).GetDecodedContent());
        Assert.Empty(back.EnumerateDefects());
    }

    [Fact]
    public void RemovingAChildDropsItsDelimiter()
    {
        var top = (MimeMultipart)MimeEntity.Parse(B(Nested));
        top.RemoveAt(1);
        var back = (MimeMultipart)MimeEntity.Parse(top.ToArray());
        Assert.Equal(2, back.Children.Count);
        Assert.Equal("message/rfc822", back.Children[1].MediaType);
        Assert.Empty(back.EnumerateDefects());
    }

    [Fact]
    public void AddingToAnUnclosedMultipartClosesIt()
    {
        var top = (MimeMultipart)MimeEntity.Parse(B(Crlf("Content-Type: multipart/mixed; boundary=b", "", "--b", "", "x")));
        Assert.Contains(top.Defects, d => d.Kind == MimeDefectKind.MissingFinalBoundary);
        var p = new MimePart("text/plain");
        top.Add(p);
        var back = (MimeMultipart)MimeEntity.Parse(top.ToArray());
        Assert.Equal(2, back.Children.Count);
        Assert.True(back.HasCloseDelimiter);
    }

    [Fact]
    public void LargeBodyIsALazyStream()
    {
        // 20 MB of body, parsed from a ranged source that counts the bytes it serves.
        byte[] head = B("Content-Type: application/octet-stream\r\n\r\n");
        long bodyLength = 20L * 1024 * 1024;
        long served = 0;
        var src = new RangedSource(
            head.Length + bodyLength,
            (off, mem, _) =>
            {
                int n = (int)Math.Min(mem.Length, head.Length + bodyLength - off);
                for (int i = 0; i < n; i++)
                {
                    long at = off + i;
                    mem.Span[i] = at < head.Length ? head[at] : (byte)'x';
                }

                served += n;
                return new ValueTask<int>(n);
            },
            (off, span) =>
            {
                int n = (int)Math.Min(span.Length, head.Length + bodyLength - off);
                for (int i = 0; i < n; i++)
                {
                    long at = off + i;
                    span[i] = at < head.Length ? head[at] : (byte)'x';
                }

                served += n;
                return n;
            });
        MimeEntity e = MimeEntity.Load(src);
        long afterParse = served;
        var part = (MimePart)e;
        Assert.Equal(bodyLength, part.RawContentLength);
        using Stream s = part.OpenRawContent();
        byte[] buf = new byte[10];
        Assert.Equal(10, s.Read(buf));
        Assert.True(served - afterParse < 1_000_000, "opening a body must not read the whole body");
    }

    [Fact]
    public void ParseNeverThrowsOnNoise()
    {
        var rng = new Random(7);
        for (int i = 0; i < 200; i++)
        {
            byte[] data = new byte[rng.Next(0, 500)];
            rng.NextBytes(data);
            if (data.Length > 60)
            {
                B("Content-Type: multipart/mixed; boundary=b\r\n\r\n--b\r\n").CopyTo(data, 0);
            }

            MimeEntity e = MimeEntity.Parse(data);
            Assert.Equal(data, e.ToArray());
            Assert.Equal(data, e.ToArrayAssembled());
        }
    }

    [Fact]
    public void WriterMirrorsTheReader()
    {
        byte[] data = B(Nested);
        using var ms = new MemoryStream();
        using (var w = new MimeWriter(ms))
        using (var r = new MimeReader(data))
        {
            while (r.Read())
            {
                w.WriteEvent(r);
            }
        }

        Assert.Equal(data, ms.ToArray());
    }

    [Fact]
    public void WriterGeneratesDelimiters()
    {
        using var ms = new MemoryStream();
        using (var w = new MimeWriter(ms))
        {
            w.WriteHeader("Content-Type", "multipart/mixed; boundary=\"x\"");
            w.WriteHeadersEnd();
            w.WriteDelimiter("x"u8, close: false, leadingNewline: false);
            w.WriteHeadersEnd();
            w.WriteRaw("body"u8);
            w.WriteDelimiter("x"u8, close: true);
            Assert.Equal(w.Position, w.Position);
        }

        Assert.Equal("Content-Type: multipart/mixed; boundary=\"x\"\r\n\r\n--x\r\n\r\nbody\r\n--x--\r\n", Encoding.ASCII.GetString(ms.ToArray()));
    }
}

public class TransferEncodingTests
{
    private static byte[] DecodeB64(string s, out TransferIssues issues)
    {
        var d = new Base64Decoder();
        byte[] src = Encoding.ASCII.GetBytes(s);
        byte[] outp = new byte[Base64Decoder.MaxOutputLength(src.Length) + 4];
        int n = d.Decode(src, outp);
        n += d.Finish(outp.AsSpan(n));
        issues = d.Issues;
        return outp.AsSpan(0, n).ToArray();
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("Zg==", "f")]
    [InlineData("Zm8=", "fo")]
    [InlineData("Zm9v", "foo")]
    [InlineData("Zm9vYg==", "foob")]
    [InlineData("Zm9vYmE=", "fooba")]
    [InlineData("Zm9vYmFy", "foobar")]
    public void Base64Rfc4648Vectors(string encoded, string expected)
    {
        Assert.Equal(expected, Encoding.ASCII.GetString(DecodeB64(encoded, out TransferIssues issues)));
        Assert.Equal(TransferIssues.None, issues);
    }

    [Fact]
    public void Base64IsLenient()
    {
        Assert.Equal("foobar", Encoding.ASCII.GetString(DecodeB64("Zm9v\r\n YmFy\r\n", out TransferIssues a)));
        Assert.Equal(TransferIssues.None, a);
        Assert.Equal("foobar", Encoding.ASCII.GetString(DecodeB64("Zm9v!YmFy", out TransferIssues b)));
        Assert.Equal(TransferIssues.InvalidBase64Character, b);
        Assert.Equal("fo", Encoding.ASCII.GetString(DecodeB64("Zm8", out TransferIssues c)));
        Assert.Equal(TransferIssues.TruncatedBase64Group, c);
        Assert.Equal("foobar", Encoding.ASCII.GetString(DecodeB64("Zm9v=YmFy", out TransferIssues d)));
        Assert.Equal(TransferIssues.MisplacedBase64Padding, d);
        Assert.Equal("fb", Encoding.ASCII.GetString(DecodeB64("Zg==Yg==", out _)));
    }

    [Fact]
    public void Base64SplitAnywhereDecodesTheSame()
    {
        byte[] payload = Enumerable.Range(0, 1000).Select(i => (byte)(i * 7)).ToArray();
        byte[] encoded = TransferEncoder.Base64(payload);
        for (int split = 0; split < 40; split++)
        {
            var d = new Base64Decoder();
            byte[] outp = new byte[2048];
            int n = d.Decode(encoded.AsSpan(0, split), outp);
            n += d.Decode(encoded.AsSpan(split), outp.AsSpan(n));
            n += d.Finish(outp.AsSpan(n));
            Assert.Equal(payload, outp.AsSpan(0, n).ToArray());
        }
    }

    private static string DecodeQp(string s, out TransferIssues issues, int split = -1)
    {
        var d = new QuotedPrintableDecoder();
        byte[] src = Encoding.Latin1.GetBytes(s);
        byte[] outp = new byte[QuotedPrintableDecoder.MaxOutputLength(src.Length) * 2];
        int n;
        if (split >= 0)
        {
            n = d.Decode(src.AsSpan(0, split), outp);
            n += d.Decode(src.AsSpan(split), outp.AsSpan(n));
        }
        else
        {
            n = d.Decode(src, outp);
        }

        n += d.Finish(outp.AsSpan(n));
        issues = d.Issues;
        return Encoding.Latin1.GetString(outp.AsSpan(0, n));
    }

    [Theory]
    [InlineData("caf=C3=A9", "caf\u00c3\u00a9", TransferIssues.None)]
    [InlineData("a=3db=3D", "a=b=", TransferIssues.None)]
    [InlineData("soft=\r\nbreak", "softbreak", TransferIssues.None)]
    [InlineData("soft=\nbreak", "softbreak", TransferIssues.None)]
    [InlineData("trailing  \r\nnext", "trailing\r\nnext", TransferIssues.None)]
    [InlineData("inner  space", "inner  space", TransferIssues.None)]
    [InlineData("bad=ZZescape", "bad=ZZescape", TransferIssues.InvalidQuotedPrintableEscape)]
    [InlineData("half=4", "half=4", TransferIssues.TruncatedQuotedPrintableEscape)]
    [InlineData("end=", "end=", TransferIssues.TruncatedQuotedPrintableEscape)]
    [InlineData("soft= \r\nblank", "softblank", TransferIssues.None)]
    public void QuotedPrintableCases(string encoded, string expected, TransferIssues issues)
    {
        for (int split = -1; split <= encoded.Length; split++)
        {
            Assert.Equal(expected, DecodeQp(encoded, out TransferIssues got, split));
            Assert.Equal(issues, got);
        }
    }

    [Fact]
    public void QuotedPrintableEncoderRoundTripsAndBoundsLines()
    {
        string text = "Caf\u00e9 \u2603 =sign and a very long line " + new string('x', 200) + " end \r\nsecond line  \r\n\ttabbed";
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        byte[] encoded = TransferEncoder.QuotedPrintable(bytes);
        string e = Encoding.ASCII.GetString(encoded);
        Assert.All(e.Split("\r\n"), l => Assert.True(l.Length <= 76, l));
        var d = new QuotedPrintableDecoder();
        byte[] outp = new byte[encoded.Length + 200];
        int n = d.Decode(encoded, outp);
        n += d.Finish(outp.AsSpan(n));
        Assert.Equal(text, Encoding.UTF8.GetString(outp.AsSpan(0, n)));
    }

    [Fact]
    public void DecodingStreamHandlesSmallReadsAndAsync()
    {
        byte[] payload = Enumerable.Range(0, 50_000).Select(i => (byte)(i % 251)).ToArray();
        byte[] encoded = TransferEncoder.Base64(payload);
        using var s = new TransferDecodingStream(new MemoryStream(encoded), ContentTransferEncoding.Base64);
        var ms = new MemoryStream();
        byte[] buf = new byte[13];
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0)
        {
            ms.Write(buf, 0, n);
        }

        Assert.Equal(payload, ms.ToArray());
        Assert.Equal(TransferIssues.None, s.Issues);
    }

    [Fact]
    public void IdentityEncodingsPassThrough()
    {
        using var s = new TransferDecodingStream(new MemoryStream("a=3D\r\n"u8.ToArray()), ContentTransferEncoding.SevenBit);
        var ms = new MemoryStream();
        s.CopyTo(ms);
        Assert.Equal("a=3D\r\n", Encoding.ASCII.GetString(ms.ToArray()));
    }
}

public class SourceTests
{
    [Fact]
    public void MemorySourceReadsAtOffsets()
    {
        using var s = new MemorySource(new byte[] { 1, 2, 3, 4, 5 });
        byte[] b = new byte[3];
        Assert.Equal(3, s.Read(1, b));
        Assert.Equal(new byte[] { 2, 3, 4 }, b);
        Assert.Equal(1, s.Read(4, b));
        Assert.Equal(0, s.Read(5, b));
        Assert.Equal(0, s.Read(100, b));
    }

    [Fact]
    public async Task StreamSourceSerialisesReads()
    {
        byte[] data = Enumerable.Range(0, 100_000).Select(i => (byte)i).ToArray();
        using var s = new StreamSource(new MemoryStream(data));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(async () =>
        {
            for (int i = 0; i < 200; i++)
            {
                long off = ((t * 977) + (i * 331)) % 90_000;
                byte[] b = new byte[100];
                Assert.Equal(100, await s.ReadFullyAsync(off, b));
                Assert.Equal(data.AsSpan((int)off, 100).ToArray(), b);
            }
        })));
    }

    [Fact]
    public async Task AsyncOnlySourceRefusesSyncReadsButWorksAsync()
    {
        using var s = new RangedSource(10, (o, m, _) => new ValueTask<int>(m.Length));
        Assert.False(s.SupportsSyncRead);
        Assert.Throws<NotSupportedException>(() => s.Read(0, new byte[1]));
        Assert.Equal(3, await s.ReadAsync(0, new byte[3], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancellationIsHonouredByTheTreeLoader()
    {
        using var cts = new CancellationTokenSource();
        using var s = new RangedSource(1000, (o, m, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            cts.Cancel();
            m.Span[..Math.Min(5, m.Length)].Fill((byte)'a');
            return new ValueTask<int>(Math.Min(5, m.Length));
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await MimeEntity.LoadAsync(s, null, cts.Token));
    }

    [Fact]
    public void SourceStreamIsLazyAndSeekable()
    {
        using var s = new MemorySource(Encoding.ASCII.GetBytes("0123456789"));
        using Stream st = s.OpenRead(2, 5);
        Assert.Equal(5, st.Length);
        byte[] b = new byte[10];
        Assert.Equal(5, st.Read(b));
        Assert.Equal("23456", Encoding.ASCII.GetString(b, 0, 5));
        st.Seek(-2, SeekOrigin.End);
        Assert.Equal(2, st.Read(b));
        Assert.Equal("56", Encoding.ASCII.GetString(b, 0, 2));
    }
}
