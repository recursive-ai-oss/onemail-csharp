using System.Text;
using RecursiveAI.OneMail.Sources;
using RecursiveAI.OneMail.Stores;
using RecursiveAI.OneMail.Stores.Mbox;

namespace RecursiveAI.OneMail.Tests.Stores;

public class MboxReaderTests
{
    private const string Env = "From a@example.com Mon Jan  1 00:00:00 2024\n";

    private static byte[] B(string s) => Encoding.Latin1.GetBytes(s);

    private static List<MboxEntry> Read(string data, MboxReaderOptions? options = null)
    {
        using MemorySource src = new(B(data));
        return [.. MboxReader.Enumerate(src, options)];
    }

    private static string Slice(string data, MboxEntry e) => data.Substring((int)e.MessageOffset, (int)e.MessageLength);

    [Fact]
    public void SplitsOnEnvelopeLinesAndExcludesTheSeparatorLine()
    {
        string data = Env + "Subject: 1\n\nhello\n\n" + Env + "Subject: 2\n\nbye\n\n";
        List<MboxEntry> e = Read(data);
        Assert.Equal(2, e.Count);
        Assert.Equal("Subject: 1\n\nhello\n", Slice(data, e[0]));
        Assert.Equal("Subject: 2\n\nbye\n", Slice(data, e[1]));
        Assert.Equal(e[1].Offset, e[0].Offset + e[0].RecordLength);
        Assert.Equal(data.Length, e[1].Offset + e[1].RecordLength);
    }

    [Fact]
    public void KeepsBytesBeforeTheFirstEnvelopeAsPreamble()
    {
        string data = "junk\n" + Env + "Subject: 1\n\nx\n";
        var defects = new List<StoreDefect>();
        using MemorySource src = new(B(data));
        using MboxReader r = new(src, new MboxReaderOptions { DefectHandler = defects.Add });
        Assert.True(r.Read());
        Assert.Equal(5, r.PreambleLength);
        Assert.Equal(5, r.Current.Offset);
        Assert.Contains(defects, d => d.Kind == StoreDefects.PreambleBeforeFirstMessage && d.Offset == 0);
    }

    [Fact]
    public void MissingSeparatorLineIsNotedButStillSplits()
    {
        string data = Env + "Subject: 1\n\nx\n" + Env + "Subject: 2\n\ny\n";
        List<MboxEntry> e = Read(data);
        Assert.Equal(2, e.Count);
        Assert.Equal(StoreDefects.MissingSeparatorLine, e[0].Defects);
        Assert.Equal("Subject: 1\n\nx\n", Slice(data, e[0]));
    }

    [Fact]
    public void FromLineWithoutDateIsTextInStrictModeAndADelimiterInAnyMode()
    {
        string data = Env + "Subject: 1\n\nhi\n\nFrom the desk of x\nmore\n\n" + Env + "Subject: 2\n\ny\n";
        List<MboxEntry> strict = Read(data);
        Assert.Equal(2, strict.Count);
        Assert.Equal(StoreDefects.AmbiguousFromLine, strict[0].Defects);
        Assert.Contains("From the desk of x", Slice(data, strict[0]));

        List<MboxEntry> any = Read(data, new MboxReaderOptions { FromLineMode = MboxFromLineMode.Any });
        Assert.Equal(3, any.Count);
    }

    [Theory]
    [InlineData("From a@b.c Mon Jan  1 00:00:00 2024", true)]
    [InlineData("From a@b.c Mon Jan 01 00:00:00 +0000 2024", true)]
    [InlineData("From - Mon Jan 01 00:00:00 2024", true)]
    [InlineData("From a@b.c Mon, 1 Jan 2024 00:00:00", true)]
    [InlineData("From MAILER-DAEMON Fri Jul  8 12:08:34 2011\r", true)]
    [InlineData("From the desk of someone", false)]
    [InlineData("From a@b.c", false)]
    [InlineData("From a@b.c Mon Jan", false)]
    [InlineData("From ", false)]
    [InlineData("Fromage a b", false)]
    public void RecognisesEnvelopeLines(string line, bool expected) =>
        Assert.Equal(expected, MboxFromLine.IsEnvelope(B(line)));

    [Fact]
    public void ValidContentLengthKeepsAnUnquotedEnvelopeLookalikeInsideTheMessage()
    {
        string body = "hi\nFrom x@y.z Mon Jan  1 00:00:00 2024\nzz\n";
        string data = Env + "Content-Length: " + body.Length + "\n\n" + body + "\n" + Env + "Subject: 2\n\nq\n";
        List<MboxEntry> e = Read(data);
        Assert.Equal(2, e.Count);
        Assert.True(e[0].EndedByContentLength);
        Assert.Equal(body.Length, e[0].ContentLength);
        Assert.EndsWith(body, Slice(data, e[0]));

        // mboxrd ignores Content-Length: the lookalike splits the message.
        Assert.Equal(3, Read(data, new MboxReaderOptions { Variant = MboxVariant.Mboxrd }).Count);
    }

    [Fact]
    public void WrongContentLengthIsIgnoredAndReported()
    {
        string data = Env + "Content-Length: 5\n\nhello world\n\n" + Env + "Subject: 2\n\nq\n";
        List<MboxEntry> e = Read(data);
        Assert.Equal(2, e.Count);
        Assert.False(e[0].EndedByContentLength);
        Assert.True((e[0].Defects & StoreDefects.ContentLengthMismatch) != 0);

        data = Env + "Content-Length: 99999\n\nhello\n";
        e = Read(data);
        Assert.Single(e);
        Assert.True((e[0].Defects & StoreDefects.ContentLengthMismatch) != 0);
    }

    [Fact]
    public void ConflictingContentLengthsUseTheFirst()
    {
        string body = "abc\n";
        string data = Env + "Content-Length: 4\nContent-Length: 9\n\n" + body + "\n" + Env + "Subject: x\n\ny\n";
        List<MboxEntry> e = Read(data);
        Assert.Equal(2, e.Count);
        Assert.True((e[0].Defects & StoreDefects.AmbiguousContentLength) != 0);
        Assert.Equal(4, e[0].ContentLength);
    }

    [Fact]
    public void OverlongEnvelopeLineIsKeptWholeInAnyMode()
    {
        string envelope = "From " + new string('x', 300) + "\n";
        string data = envelope + "Subject: 1\n\nbody\n";
        var options = new MboxReaderOptions { FromLineMode = MboxFromLineMode.Any, MaxEnvelopeLineBytes = 64 };
        List<MboxEntry> e = Read(data, options);
        Assert.Single(e);
        Assert.Equal(envelope.Length, e[0].EnvelopeLength);
        Assert.Equal(StoreDefects.OverlongEnvelopeLine, e[0].Defects);

        // In strict mode the same line is text, so the file has no message at all.
        Assert.Empty(Read(data, new MboxReaderOptions { MaxEnvelopeLineBytes = 64 }));
    }

    [Fact]
    public void CrlfFilesStripTheCrlfSeparator()
    {
        string data = (Env + "Subject: 1\n\nhello\n\n" + Env + "Subject: 2\n\nbye\n\n").Replace("\n", "\r\n");
        List<MboxEntry> e = Read(data);
        Assert.Equal(2, e.Count);
        Assert.EndsWith("hello\r\n", Slice(data, e[0]));
        Assert.Equal(e[0].EnvelopeLength, B(Env.TrimEnd('\n') + "\r\n").Length);
    }

    [Fact]
    public void LongLinesAndWindowEdgesDoNotHideEnvelopeLines()
    {
        // Messages sized so that "\nFrom " straddles every possible window boundary.
        for (int pad = 0; pad < 40; pad++)
        {
            string long1 = new('x', 16384 - 60 + pad);
            string data = Env + "Subject: 1\n\n" + long1 + "\n\n" + Env + "Subject: 2\n\n" + new string('y', 70000) + "\n\n" + Env + "Subject: 3\n\nz\n";
            List<MboxEntry> e = Read(data, new MboxReaderOptions { BufferSize = 16384 });
            Assert.Equal(3, e.Count);
            Assert.Equal(data.IndexOf("From a", data.IndexOf("yyy", StringComparison.Ordinal), StringComparison.Ordinal), e[2].Offset);
        }
    }

    [Fact]
    public void AnEmptyFileHasNoMessagesAndAFileWithoutEnvelopeIsAllPreamble()
    {
        Assert.Empty(Read(""));
        using MemorySource src = new(B("not an mbox\nat all\n"));
        using MboxReader r = new(src);
        Assert.False(r.Read());
        Assert.Equal(19, r.PreambleLength);
    }

    [Fact]
    public async Task AsyncEnumerationMatchesSyncAndHonoursCancellation()
    {
        string data = Env + "Subject: 1\n\nx\n\n" + Env + "Subject: 2\n\ny\n";
        using MemorySource src = new(B(data));
        List<MboxEntry> viaAsync = [];
        await foreach (MboxEntry e in MboxReader.EnumerateAsync(src, null, TestContext.Current.CancellationToken))
        {
            viaAsync.Add(e);
        }

        Assert.Equal(Read(data).Select(x => (x.Offset, x.RecordLength)), viaAsync.Select(x => (x.Offset, x.RecordLength)));

        using CancellationTokenSource cts = new();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (MboxEntry unused in MboxReader.EnumerateAsync(src, null, cts.Token))
            {
            }
        });
    }

    [Fact]
    public void EnumerationAllocatesNothingPerMessage()
    {
#if DEBUG
        Assert.Skip("allocation counts are only meaningful in optimised builds (async state machines are classes in Debug)");
#endif
        StringBuilder sb = new();
        for (int i = 0; i < 2000; i++)
        {
            sb.Append(Env).Append("Subject: ").Append(i).Append("\n\nbody line\nFrom the desk\n\n");
        }

        using MemorySource src = new(B(sb.ToString()));

        // The runtime occasionally allocates a few kilobytes on the thread for its own work (tiered compilation), so the
        // smallest of three passes is the reader's own figure. One allocation per message would be at least 2000 x 24 bytes.
        long allocated = long.MaxValue;
        for (int pass = 0; pass < 3; pass++)
        {
            using MboxReader r = new(src);
            r.Read();
            long before = GC.GetAllocatedBytesForCurrentThread();
            int n = 1;
            while (r.Read())
            {
                n++;
            }

            allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
            Assert.Equal(2000, n);
        }

        Assert.True(allocated < 2000, $"allocated {allocated} bytes for 2000 messages");
    }

    [Fact]
    public void UnquotingFollowsTheVariant()
    {
        string stored = "Subject: x\n\n>From a\n>>From b\n>From\nplain\n";
        string data = Env + stored + "\n";
        using MemorySource src = new(B(data));
        MboxEntry e = MboxReader.Enumerate(src).Single();

        using (MboxReader rd = new(src, new MboxReaderOptions { Variant = MboxVariant.Mboxrd }))
        {
            Assert.Equal("Subject: x\n\nFrom a\n>From b\n>From\nplain\n", Encoding.Latin1.GetString(rd.ReadMessage(e)));
        }

        using (MboxReader ro = new(src, new MboxReaderOptions { Variant = MboxVariant.Mboxo }))
        {
            Assert.Equal("Subject: x\n\nFrom a\n>>From b\n>From\nplain\n", Encoding.Latin1.GetString(ro.ReadMessage(e)));
        }

        using (MboxReader r2 = new(src, new MboxReaderOptions { Variant = MboxVariant.Mboxcl2 }))
        {
            Assert.Equal(stored, Encoding.Latin1.GetString(r2.ReadMessage(e)));
            Assert.Equal(stored, Encoding.Latin1.GetString(r2.ReadMessage(e, unquote: false)));
        }
    }

    [Fact]
    public void QuoterGivesTheSameResultForAnyChunking()
    {
        string text = "From a\n>From b\n>>From c\nFromx\nplain\nFrom\nFrom a\r\n>From d\nlast From";
        byte[] input = B(text);
        foreach (bool quote in new[] { true, false })
        {
            foreach (bool rev in new[] { true, false })
            {
                byte[] whole = RunQuoter(input, input.Length, quote, rev);
                for (int chunk = 1; chunk < 12; chunk++)
                {
                    Assert.Equal(whole, RunQuoter(input, chunk, quote, rev));
                }
            }
        }
    }

    private static byte[] RunQuoter(byte[] input, int chunk, bool quote, bool reversible)
    {
        MboxQuoter q = new(quote, reversible);
        using MemoryStream ms = new();
        byte[] outBuf = new byte[MboxQuoter.MaxOutput(chunk)];
        for (int i = 0; i < input.Length; i += chunk)
        {
            int n = q.Process(input.AsSpan(i, Math.Min(chunk, input.Length - i)), outBuf);
            ms.Write(outBuf, 0, n);
        }

        byte[] fin = new byte[16];
        ms.Write(fin, 0, q.Finish(fin));
        return ms.ToArray();
    }

    [Fact]
    public void QuotingAndUnquotingAreInverseForRd()
    {
        byte[] input = B("From a\n>From b\n>>From c\nFromx\nplain\nFrom\n>From\n");
        byte[] quoted = RunQuoter(input, 7, quote: true, reversible: true);
        Assert.Equal("&From a\n&>From b\n&>>From c\nFromx\nplain\nFrom\n>From\n".Replace("&", ">"), Encoding.Latin1.GetString(quoted));
        Assert.Equal(input, RunQuoter(quoted, 5, quote: false, reversible: true));
    }

    [Fact]
    public void GmailTakeoutMetadataIsSurfaced()
    {
        byte[] msg = B("Subject: x\nX-Gmail-Labels: Inbox,Important,\"Work, Home\",=?UTF-8?Q?Caf=C3=A9?=\nX-GM-THRID: 1700000000000000001\nStatus: RO\nX-Status: AF\n\nbody\n");
        MboxMetadata m = MboxMetadata.Parse(msg);
        Assert.Equal(["Inbox", "Important", "Work, Home", "Café"], m.GmailLabels);
        Assert.Equal("1700000000000000001", m.GmailThreadId);
        Assert.Equal(MessageMarks.Seen | MessageMarks.Answered | MessageMarks.Flagged, m.Flags);
    }

    [Fact]
    public void EnvelopeParsesSenderAndDate()
    {
        MboxEnvelope e = MboxEnvelope.Parse(B("From 1706@xxx Mon Jan 01 05:06:07 +0200 2024\n"));
        Assert.Equal("1706@xxx", e.Sender);
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 5, 6, 7, TimeSpan.FromHours(2)), e.Date);
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), MboxEnvelope.Parse(B("From a@b Mon Jan  1 00:00:00 2024")).Date);
        Assert.Null(MboxEnvelope.Parse(B("From a@b nonsense")).Date);
    }

    [Fact]
    public void SnifferFindsTheVariantFromTheContent()
    {
        static MboxDetection Detect(string data)
        {
            using MemorySource s = new(B(data));
            return MboxSniffer.Detect(s);
        }

        Assert.False(Detect("Subject: x\n\nhi\n").IsMbox);
        Assert.Equal(MboxVariant.Unknown, Detect(Env + "Subject: x\n\nhi\n").Variant);
        Assert.Equal(MboxVariant.Mboxrd, Detect(Env + "Subject: x\n\n>>From x\n").Variant);
        Assert.Equal(MboxVariant.Mboxrd, Detect(Env + "Subject: x\n\n>From x\n").Variant);

        string body = "hi\n";
        Assert.Equal(MboxVariant.Mboxcl, Detect(Env + "Content-Length: 3\n\n" + body + "\n").Variant);
        string cl2body = "hi\nFrom x@y.z Mon Jan  1 00:00:00 2024\nzz\n";
        Assert.Equal(MboxVariant.Mboxcl2, Detect(Env + "Content-Length: " + cl2body.Length + "\n\n" + cl2body + "\n" + Env + "Subject: y\n\nq\n").Variant);
    }
}

public class MboxWriterTests
{
    private static readonly DateTimeOffset Date = new(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static byte[] B(string s) => Encoding.Latin1.GetBytes(s);

    private static byte[] Write(MboxVariant v, params string[] messages)
    {
        using MemoryStream ms = new();
        MboxWriter w = new(ms, new MboxWriterOptions { Variant = v });
        foreach (string m in messages)
        {
            w.Append(B(m), "me@example.com", Date);
        }

        return ms.ToArray();
    }

    [Theory]
    [InlineData(MboxVariant.Mboxo)]
    [InlineData(MboxVariant.Mboxrd)]
    [InlineData(MboxVariant.Mboxcl)]
    [InlineData(MboxVariant.Mboxcl2)]
    public void WrittenFilesReadBackAsGiven(MboxVariant v)
    {
        string[] input =
        [
            "Subject: 1\n\nplain\n",
            "Subject: 2\n\nFrom the desk\nFrom x@y.z Mon Jan  1 00:00:00 2024\nend\n",
            "Subject: 3\n\nlast line without newline",
            "Subject: 4\n\n",
        ];
        byte[] file = Write(v, input);
        using MemorySource src = new(file);
        using MboxReader r = new(src, new MboxReaderOptions { Variant = v });
        int i = 0;
        while (r.Read())
        {
            string got = Encoding.Latin1.GetString(r.ReadMessage(r.Current));
            string want = input[i];
            if (v is MboxVariant.Mboxcl or MboxVariant.Mboxcl2)
            {
                // Content-Length is inserted as the last header; a body that lacks a final newline gets one.
                Assert.Matches(@"^Subject: \d\nContent-Length: \d+\n\n", got);
                got = got[(got.IndexOf("\n\n", StringComparison.Ordinal) + 2)..];
                want = input[i][(input[i].IndexOf("\n\n", StringComparison.Ordinal) + 2)..];
                if (want.Length > 0 && !want.EndsWith('\n'))
                {
                    want += "\n";
                }
            }
            else if (!want.EndsWith('\n'))
            {
                want += "\n";
            }

            Assert.Equal(want, got);
            i++;
        }

        Assert.Equal(input.Length, i);
        Assert.StartsWith("From me@example.com Tue Jan  2 03:04:05 2024\n", Encoding.Latin1.GetString(file));
    }

    [Fact]
    public void QuotingMatchesTheVariant()
    {
        string m = "Subject: x\n\nFrom a\n>From b\n";
        Assert.Contains("\n>From a\n>From b\n", Encoding.Latin1.GetString(Write(MboxVariant.Mboxo, m)));
        Assert.Contains("\n>From a\n>>From b\n", Encoding.Latin1.GetString(Write(MboxVariant.Mboxrd, m)));
        Assert.Contains("\nFrom a\n>From b\n", Encoding.Latin1.GetString(Write(MboxVariant.Mboxcl2, m)));
    }

    [Fact]
    public void ContentLengthVariantsReplaceAnExistingLengthAndCountStoredBytes()
    {
        string m = "Subject: x\nContent-Length: 999\n\nFrom a\nb\n";
        string file = Encoding.Latin1.GetString(Write(MboxVariant.Mboxcl, m));
        // body ">From a\nb\n" is 10 bytes once quoted
        Assert.Contains("Subject: x\nContent-Length: 10\n\n>From a\nb\n\n", file);
        Assert.DoesNotContain("999", file, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendingToAnExistingFileAddsTheMissingSeparator()
    {
        using MemoryStream ms = new();
        ms.Write(B("From a@b.c Mon Jan  1 00:00:00 2024\nSubject: 1\n\nx"));
        MboxWriter w = new(ms, new MboxWriterOptions { AppendToExisting = true });
        w.Append(B("Subject: 2\n\ny\n"), "me", Date);
        string file = Encoding.Latin1.GetString(ms.ToArray());
        Assert.Contains("x\n\nFrom me Tue Jan  2 03:04:05 2024\nSubject: 2\n\ny\n\n", file);
        using MemorySource src = new(ms.ToArray());
        Assert.Equal(2, MboxReader.Enumerate(src).Count());
    }

    [Fact]
    public void CopyingEveryRecordReproducesTheFileByteForByte()
    {
        string weird = "junk\nFrom a@b.c Mon Jan  1 00:00:00 2024\nSubject: 1\n\nx\nFrom a@b.c Mon Jan  1 00:00:00 2024\r\nSubject: 2\r\n\r\ny\r\n\r\nFrom z Mon Jan  1 00:00:00 2024\nContent-Length: 3\n\nab\n\n";
        byte[] data = B(weird);
        using MemorySource src = new(data);
        using MboxReader r = new(src);
        using MemoryStream ms = new();
        MboxWriter w = new(ms);
        bool first = true;
        while (r.Read())
        {
            if (first)
            {
                w.WritePreamble(src, r.PreambleLength);
                first = false;
            }

            w.AppendRecord(src, r.Current);
        }

        Assert.Equal(data, ms.ToArray());
    }

    [Fact]
    public async Task ReadingBackWithQuotingRoundTripsThroughAnAsyncAppend()
    {
        byte[] msg = B("Subject: x\n\nFrom a\n");
        using MemoryStream ms = new();
        MboxWriter w = new(ms, new MboxWriterOptions { Variant = MboxVariant.Mboxrd });
        await w.AppendAsync(new MemorySource(msg), 0, msg.Length, "me", Date, TestContext.Current.CancellationToken);
        using MemorySource src = new(ms.ToArray());
        using MboxReader r = new(src, new MboxReaderOptions { Variant = MboxVariant.Mboxrd });
        Assert.True(r.Read());
        Assert.Equal(msg, r.ReadMessage(r.Current));
    }
}
