using System.Text;
using RecursiveAI.OneMail.Text;

namespace RecursiveAI.OneMail.Fuzz;

// Fuzz target "text": the first byte picks a decoder, the rest is its input. Decoders must neither throw nor hang; the
// encoders' round trips are asserted, so a mismatch is a finding too.
internal static class TextTarget
{
    private static readonly string[] Labels =
    [
        "utf-8", "utf-7", "windows-1252", "iso-8859-2", "koi8-r", "shift_jis", "euc-jp", "gbk", "gb18030", "big5", "euc-kr", "iso-2022-jp",
        "iso-2022-jp-1", "iso-2022-jp-2", "iso-2022-jp-3", "cp50221", "cp50222", "iso-2022-kr", "hz-gb-2312", "utf-16le", "utf-16be", "utf-32",
        "ibm437", "x-mac-cyrillic", "windows-874", "iso-8859-8-i",
    ];

    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        byte op = data[0];
        ReadOnlySpan<byte> rest = data[1..];
        var issues = new List<TextIssue>();
        switch (op % 7)
        {
            case 0:
                _ = Rfc2047.DecodeText(rest, issues, (op & 0x80) != 0 ? EncodedWordContext.QuotedString : EncodedWordContext.Unstructured);
                break;
            case 1:
                Parameters(rest, issues);
                break;
            case 2:
                RoundTripUtf7(rest, issues);
                break;
            case 3:
                RoundTripImap(rest, issues);
                break;
            case 4:
                if (!rest.IsEmpty)
                {
                    MailEncoding e = MailEncoding.ForLabel(Labels[rest[0] % Labels.Length])!;
                    _ = e.GetString(rest[1..], issues);
                }

                break;
            case 5:
                _ = MailEncoding.ForLabel(Encoding.Latin1.GetString(rest));
                break;
            default:
                RoundTripEncode(Encoding.UTF8.GetString(rest));
                break;
        }
    }

    // Splits the input into parameters at 0x01 and each parameter into name and value at 0x00.
    private static void Parameters(ReadOnlySpan<byte> rest, List<TextIssue> issues)
    {
        var collector = new ParameterCollector();
        int offset = 0;
        while (!rest.IsEmpty)
        {
            int end = rest.IndexOf((byte)1);
            ReadOnlySpan<byte> one = end < 0 ? rest : rest[..end];
            int split = one.IndexOf((byte)0);
            collector.Add(split < 0 ? one : one[..split], split < 0 ? default : one[(split + 1)..], offset);
            offset++;
            rest = end < 0 ? default : rest[(end + 1)..];
        }

        _ = collector.Resolve(issues);
    }

    private static void RoundTripUtf7(ReadOnlySpan<byte> rest, List<TextIssue> issues)
    {
        string text = Utf7.Decode(rest, issues);
        string again = Utf7.Decode(Encoding.ASCII.GetBytes(Utf7.Encode(text)));
        if (again != text)
        {
            throw new InvalidOperationException("UTF-7 round trip changed the text");
        }
    }

    private static void RoundTripImap(ReadOnlySpan<byte> rest, List<TextIssue> issues)
    {
        string text = ImapUtf7.Decode(rest, issues);
        string again = ImapUtf7.Decode(Encoding.ASCII.GetBytes(ImapUtf7.Encode(text)));
        if (again != text)
        {
            throw new InvalidOperationException("IMAP UTF-7 round trip changed the text");
        }
    }

    private static void RoundTripEncode(string text)
    {
        string encoded = Rfc2047.Encode(text);
        foreach (char c in encoded)
        {
            if (c > '~' || (c < ' ' && c != '\t' && c != '\r' && c != '\n'))
            {
                throw new InvalidOperationException("Rfc2047.Encode produced a non-ASCII character");
            }
        }

        foreach (string token in encoded.Split([' ', '\t', '\r', '\n']))
        {
            if (token.StartsWith("=?", StringComparison.Ordinal) && token.Length > 75)
            {
                throw new InvalidOperationException("encoded word longer than 75");
            }
        }

        bool plain = true;
        foreach (char c in text)
        {
            plain &= c >= ' ' && c != '\u007F';
        }

        if (plain && Rfc2047.DecodeText(encoded) != text)
        {
            throw new InvalidOperationException("RFC 2047 round trip changed the text");
        }

        foreach (Rfc2231.EncodedPart part in Rfc2231.Encode("n", text))
        {
            _ = part.Value;
        }
    }
}
