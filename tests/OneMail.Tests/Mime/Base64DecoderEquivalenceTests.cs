using RecursiveAI.OneMail.Mime.Transfer;

namespace RecursiveAI.OneMail.Tests.Mime;

/// <summary>
/// The vectorised base64 path must give exactly what a byte-at-a-time decode gives, whatever the input and however it is cut
/// into pieces: same bytes, same repairs.
/// </summary>
public class Base64DecoderEquivalenceTests
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

    /// <summary>The decoder as specified in RFC 2045 §6.8 plus the lenient repairs, one byte at a time.</summary>
    private static byte[] Reference(byte[] input, out TransferIssues issues)
    {
        var d = new Base64Decoder();
        byte[] o = new byte[Base64Decoder.MaxOutputLength(1) + 8];
        var all = new List<byte>();
        foreach (byte b in input)
        {
            int n = d.Decode([b], o);
            all.AddRange(o.AsSpan(0, n).ToArray());
        }

        int f = d.Finish(o);
        all.AddRange(o.AsSpan(0, f).ToArray());
        issues = d.Issues;
        return [.. all];
    }

    private static byte[] Chunked(byte[] input, Random rnd, out TransferIssues issues)
    {
        var d = new Base64Decoder();
        var all = new List<byte>();
        int pos = 0;
        while (pos < input.Length)
        {
            int len = rnd.Next(5) == 0 ? rnd.Next(1, 40) : rnd.Next(1, 40000);
            len = Math.Min(len, input.Length - pos);
            byte[] o = new byte[Base64Decoder.MaxOutputLength(len)];
            int n = d.Decode(input.AsSpan(pos, len), o);
            all.AddRange(o.AsSpan(0, n).ToArray());
            pos += len;
        }

        byte[] fin = new byte[8];
        int f = d.Finish(fin);
        all.AddRange(fin.AsSpan(0, f).ToArray());
        issues = d.Issues;
        return [.. all];
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void VectorPathMatchesTheByteAtATimeDecoder(int seed)
    {
        var rnd = new Random(seed);
        for (int round = 0; round < 300; round++)
        {
            int len = rnd.Next(0, 3000);
            if (round % 5 == 0)
            {
                len = rnd.Next(3000, 40000);
            }

            if (round % 50 == 0)
            {
                len = rnd.Next(50000, 120000);
            }

            // A mix that favours clean wrapped lines but sprinkles padding, blanks and junk anywhere.
            var sb = new List<byte>();
            int lineWidth = rnd.Next(2) == 0 ? 4 * rnd.Next(4, 30) : rnd.Next(1, 100);
            bool crlf = rnd.Next(3) != 0;
            int junk = rnd.Next(0, 4) == 0 ? rnd.Next(1, 30) : 0;
            for (int i = 0; i < len; i++)
            {
                if (i % lineWidth == lineWidth - 1)
                {
                    if (crlf)
                    {
                        sb.Add((byte)'\r');
                    }

                    sb.Add((byte)'\n');
                }

                if (junk > 0 && rnd.Next(len / junk + 1) == 0)
                {
                    byte[] specials = "=== \t!\0\u00e9-_\n"u8.ToArray();
                    sb.Add(specials[rnd.Next(specials.Length)]);
                }
                else
                {
                    sb.Add((byte)Alphabet[rnd.Next(64)]);
                }
            }

            byte[] input = [.. sb];
            byte[] expected = Reference(input, out TransferIssues expectedIssues);
            byte[] actual = Chunked(input, rnd, out TransferIssues actualIssues);
            Assert.Equal(expected, actual);
            Assert.Equal(expectedIssues, actualIssues);
        }
    }
}
