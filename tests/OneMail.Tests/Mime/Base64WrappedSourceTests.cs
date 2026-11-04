using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Mime.Transfer;

namespace RecursiveAI.OneMail.Tests.Mime;

public class Base64WrappedSourceTests
{
    [Fact]
    public void ReadsAgreeWithTheEagerEncoderAtAnyOffsetAndSize()
    {
        var rnd = new Random(7);
        foreach (int length in new[] { 0, 1, 2, 3, 56, 57, 58, 113, 114, 115, 1000, 5700, 20000 })
        {
            byte[] data = new byte[length];
            rnd.NextBytes(data);
            byte[] expected = TransferEncoder.Base64(data);
            var source = new Base64WrappedSource(data);
            Assert.Equal(expected.Length, source.Length);
            for (int round = 0; round < 40; round++)
            {
                long offset = expected.Length == 0 ? 0 : rnd.Next(expected.Length);
                int size = rnd.Next(1, 3000);
                byte[] got = new byte[size];
                int n = source.Read(offset, got);
                Assert.Equal(expected.AsSpan((int)offset, n).ToArray(), got.AsSpan(0, n).ToArray());
                Assert.True(n > 0 || offset >= expected.Length);
            }

            // Reading in 64 KiB-style chunks end to end reproduces the whole text.
            var all = new List<byte>();
            byte[] chunk = new byte[4099];
            long pos = 0;
            while (true)
            {
                int n = source.Read(pos, chunk);
                if (n == 0)
                {
                    break;
                }

                all.AddRange(chunk.AsSpan(0, n).ToArray());
                pos += n;
            }

            Assert.Equal(expected, all.ToArray());
        }
    }

    [Fact]
    public void BuiltPartWritesAndDecodesBack()
    {
        byte[] data = new byte[300_001];
        new Random(3).NextBytes(data);
        var part = new MimePart("application/octet-stream");
        part.SetDecodedContent(data);
        Assert.Equal(data, part.GetDecodedContent());
        byte[] wire = part.ToArray();
        MimeEntity back = MimeEntity.Parse(wire);
        Assert.Equal(data, ((MimePart)back).GetDecodedContent());
    }
}
