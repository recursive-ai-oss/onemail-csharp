using System.Text;
using RecursiveAI.OneMail.Mime.Transfer;

namespace RecursiveAI.OneMail.Tests.Mime;

public class Base64EncoderTests
{
    // RFC 2045 §6.8: lines of at most 76 characters; the encoder must agree with a plain line-by-line encoding at every length.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void LinesAgreeWithTheSimpleEncodingAtEveryLength(bool trailing, bool lf)
    {
        var rng = new Random(7);
        string nl = lf ? "\n" : "\r\n";
        for (int n = 0; n <= 400; n++)
        {
            byte[] data = new byte[n];
            rng.NextBytes(data);
            string flat = Convert.ToBase64String(data);
            var expected = new StringBuilder();
            for (int i = 0; i < flat.Length; i += 76)
            {
                expected.Append(flat, i, Math.Min(76, flat.Length - i));
                if (i + 76 < flat.Length || (trailing && flat.Length > 0))
                {
                    expected.Append(nl);
                }
            }

            byte[] actual = TransferEncoder.Base64(data, Encoding.ASCII.GetBytes(nl), trailing);
            Assert.Equal(expected.ToString(), Encoding.ASCII.GetString(actual));
        }
    }
}
