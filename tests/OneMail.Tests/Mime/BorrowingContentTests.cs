using System.Buffers;
using System.Text;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Mime.Transfer;
using RecursiveAI.OneMail.Sources;

namespace RecursiveAI.OneMail.Tests.Mime;

/// <summary>The borrowing and copy-into content APIs must agree with <see cref="MimePart.GetDecodedContent"/> and with the copying setter.</summary>
public class BorrowingContentTests
{
    private static byte[] Message(string encoding, byte[] body)
    {
        var sb = new MemoryStream();
        sb.Write(Encoding.ASCII.GetBytes("Content-Type: application/octet-stream\r\n" + (encoding.Length > 0 ? "Content-Transfer-Encoding: " + encoding + "\r\n" : string.Empty) + "\r\n"));
        sb.Write(body);
        return sb.ToArray();
    }

    private static byte[] RandomBody(Random rnd, string encoding)
    {
        byte[] data = new byte[rnd.Next(0, 6000)];
        rnd.NextBytes(data);
        switch (encoding)
        {
            case "base64":
                return rnd.Next(4) == 0 ? mangle(TransferEncoder.Base64(data), rnd) : TransferEncoder.Base64(data);
            case "quoted-printable":
                return rnd.Next(4) == 0 ? mangle(TransferEncoder.QuotedPrintable(data), rnd) : TransferEncoder.QuotedPrintable(data);
            default:
                return data;
        }

        static byte[] mangle(byte[] b, Random rnd)
        {
            byte[] c = (byte[])b.Clone();
            for (int i = 0; i < 5 && c.Length > 0; i++)
            {
                c[rnd.Next(c.Length)] = (byte)rnd.Next(256);
            }

            return c;
        }
    }

    [Fact]
    public void CopyToSpanAndWriterMatchGetDecodedContentForEveryEncoding()
    {
        var rnd = new Random(11);
        foreach (string encoding in new[] { "", "7bit", "8bit", "binary", "base64", "quoted-printable", "x-unknown" })
        {
            for (int i = 0; i < 60; i++)
            {
                byte[] msg = Message(encoding, RandomBody(rnd, encoding));
                var part = (MimePart)MimeEntity.Parse(msg);
                byte[] expected = part.GetDecodedContent();

                byte[] room = new byte[part.GetMaxDecodedContentLength() + rnd.Next(0, 3)];
                int n = part.CopyDecodedContentTo(room);
                Assert.Equal(expected, room.AsSpan(0, n).ToArray());

                var writer = new ArrayBufferWriter<byte>();
                Assert.Equal(expected.Length, part.CopyDecodedContentTo(writer));
                Assert.Equal(expected, writer.WrittenSpan.ToArray());

                // The same part read from a stream source (not memory) takes the streaming path and must agree.
                using var ms = new MemoryStream(msg);
                var viaStream = (MimePart)MimeEntity.Load(new StreamSource(ms));
                byte[] room2 = new byte[viaStream.GetMaxDecodedContentLength()];
                Assert.Equal(expected, room2.AsSpan(0, viaStream.CopyDecodedContentTo(room2)).ToArray());
                var writer2 = new ArrayBufferWriter<byte>();
                viaStream.CopyDecodedContentTo(writer2);
                Assert.Equal(expected, writer2.WrittenSpan.ToArray());
            }
        }
    }

    [Fact]
    public void ASpanThatIsTooShortIsRefused()
    {
        var part = (MimePart)MimeEntity.Parse(Message("base64", TransferEncoder.Base64(new byte[100])));
        Assert.Throws<ArgumentException>(() => part.CopyDecodedContentTo(new byte[10]));
    }

    [Fact]
    public void DecodedMemoryIsABorrowedViewOnlyWhenNoDecodingIsNeeded()
    {
        byte[] body = Encoding.ASCII.GetBytes("plain body\r\n");
        byte[] msg = Message("8bit", body);
        var part = (MimePart)MimeEntity.Parse(msg);
        Assert.True(part.TryGetDecodedContentMemory(out ReadOnlyMemory<byte> view));
        Assert.Equal(body, view.ToArray());
        Assert.True(view.Span.Overlaps(msg, out _)); // a view of the caller's buffer, not a copy

        var b64 = (MimePart)MimeEntity.Parse(Message("base64", TransferEncoder.Base64(body)));
        Assert.False(b64.TryGetDecodedContentMemory(out _));
        Assert.True(b64.TryGetRawContentMemory(out ReadOnlyMemory<byte> raw));
        Assert.Equal(b64.OpenRawContent().Length, raw.Length);

        using var ms = new MemoryStream(msg);
        var fromStream = (MimePart)MimeEntity.Load(new StreamSource(ms));
        Assert.False(fromStream.TryGetDecodedContentMemory(out _));
    }

    [Fact]
    public void BorrowedSettersWriteTheSameBytesAsTheCopyingSetter()
    {
        var rnd = new Random(5);
        foreach (ContentTransferEncoding enc in new[] { ContentTransferEncoding.Base64, ContentTransferEncoding.QuotedPrintable, ContentTransferEncoding.EightBit, ContentTransferEncoding.Binary, ContentTransferEncoding.SevenBit })
        {
            foreach (int length in new[] { 0, 1, 57, 58, 5000, 100000 })
            {
                byte[] data = new byte[length];
                rnd.NextBytes(data);
                if (enc == ContentTransferEncoding.SevenBit)
                {
                    for (int i = 0; i < data.Length; i++)
                    {
                        data[i] &= 0x7F;
                    }
                }

                var copied = new MimePart("application/octet-stream");
                copied.SetDecodedContent(data, enc);
                var borrowed = new MimePart("application/octet-stream");
                borrowed.SetDecodedContentBorrowed(data, enc);
                Assert.True(copied.ToArray().AsSpan().SequenceEqual(borrowed.ToArray()), $"{enc} {length}");
                // Quoted-printable in text mode turns a bare LF into CRLF, so only the other encodings give the input back.
                if (enc != ContentTransferEncoding.QuotedPrintable)
                {
                    Assert.Equal(data, borrowed.GetDecodedContent());
                }
            }
        }
    }

    [Fact]
    public void ABorrowedBodyReadsTheCallersMemoryWhenItIsWritten()
    {
        byte[] data = new byte[200];
        var part = new MimePart("application/octet-stream");
        part.SetDecodedContentBorrowed(data, ContentTransferEncoding.Binary);
        data[0] = 0x41; // changed before the write: the write sees it, as documented
        Assert.Equal((byte)0x41, part.GetDecodedContent()[0]);

        var copying = new MimePart("application/octet-stream");
        byte[] other = new byte[10];
        copying.SetDecodedContent(other, ContentTransferEncoding.Binary);
        other[0] = 0x41;
        Assert.Equal((byte)0, copying.GetDecodedContent()[0]);
    }
}

/// <summary>Headers of a parsed entity share one array; edits to one field must not touch the others or the source.</summary>
public class SharedHeaderBlockTests
{
    [Fact]
    public void EditingOneFieldLeavesTheOthersAndTheSourceAlone()
    {
        byte[] msg = Encoding.ASCII.GetBytes("Subject: one\r\nFrom: a@x.example\r\nX-Long: aaa\r\n bbb\r\n\r\nbody\r\n");
        byte[] copy = (byte[])msg.Clone();
        var e = MimeEntity.Parse(msg);
        Assert.Equal(copy, e.ToArray());
        e.Headers.Set("From", "b@y.example");
        Assert.Equal("one", e.Headers[0].Value);
        Assert.Equal("b@y.example", e.Headers[1].Value);
        Assert.Equal("aaa\r\n bbb", Encoding.ASCII.GetString(e.Headers[2].RawValue.Span).Trim());
        Assert.Equal(copy, msg);
        Assert.Equal("Subject: one\r\nFrom: b@y.example\r\nX-Long: aaa\r\n bbb\r\n\r\nbody\r\n", Encoding.ASCII.GetString(e.ToArray()));
    }
}
