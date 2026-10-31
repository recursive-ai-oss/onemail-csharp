using System.Text;
using RecursiveAI.OneMail.Text;

namespace RecursiveAI.OneMail.Tests.Text;

public class Utf7Tests
{
    // RFC 2152 §5 examples.
    [Theory]
    [InlineData("Hi Mom -+Jjo--!", "Hi Mom -\u263A-!")]
    [InlineData("+ZeVnLIqe-", "\u65E5\u672C\u8A9E")]
    [InlineData("A+ImIDkQ.", "A\u2262\u0391.")]
    [InlineData("Item 3 is +AKM-1.", "Item 3 is \u00A31.")]
    [InlineData("+-", "+")]
    [InlineData("1 +- 1", "1 + 1")]
    [InlineData("+2D3eAA-", "\U0001F600")]
    public void DecodesRfc2152Examples(string utf7, string expected)
    {
        var issues = new List<TextIssue>();
        Assert.Equal(expected, Utf7.Decode(Encoding.ASCII.GetBytes(utf7), issues));
        Assert.Empty(issues);
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain text")]
    [InlineData("a+b=c")]
    [InlineData("\u65E5\u672C\u8A9E and English")]
    [InlineData("x\U0001F600y")]
    [InlineData("~\\ \u00E9")]
    public void RoundTrips(string text)
    {
        string encoded = Utf7.Encode(text);
        Assert.True(Ascii.IsValid(encoded));
        Assert.Equal(text, Utf7.Decode(Encoding.ASCII.GetBytes(encoded)));
    }

    [Fact]
    public void MalformedInputIsRepairedAndReported()
    {
        var issues = new List<TextIssue>();
        Assert.Equal("a\uFFFDb", Utf7.Decode([(byte)'a', 0xFF, (byte)'b'], issues));
        Assert.Contains(issues, i => i.Kind == TextIssueKind.InvalidUtf7);

        issues.Clear();
        // A lone high surrogate (+2AA-) cannot be completed.
        Assert.Equal("\uFFFD", Utf7.Decode("+2AA-"u8, issues));
        Assert.Single(issues);
    }
}

public class ImapUtf7Tests
{
    // RFC 3501 §5.1.3.
    [Fact]
    public void DecodesTheRfcExample()
    {
        Assert.Equal("~peter/mail/\u53F0\u5317/\u65E5\u672C\u8A9E", ImapUtf7.Decode("~peter/mail/&U,BTFw-/&ZeVnLIqe-"u8));
        Assert.Equal("&", ImapUtf7.Decode("&-"u8));
        Assert.Equal("Entw\u00FCrfe", ImapUtf7.Decode("Entw&APw-rfe"u8));
    }

    [Theory]
    [InlineData("INBOX")]
    [InlineData("Sent & Trash")]
    [InlineData("\u65E5\u672C\u8A9E/\u53F0\u5317")]
    [InlineData("x\U0001F600y")]
    [InlineData("")]
    public void RoundTrips(string name)
    {
        string wire = ImapUtf7.Encode(name);
        Assert.True(Ascii.IsValid(wire));
        Assert.Equal(name, ImapUtf7.Decode(Encoding.ASCII.GetBytes(wire)));
    }

    [Fact]
    public void EncodesTheRfcExample() => Assert.Equal("~peter/mail/&U,BTFw-/&ZeVnLIqe-", ImapUtf7.Encode("~peter/mail/\u53F0\u5317/\u65E5\u672C\u8A9E"));

    [Fact]
    public void UnterminatedRunStillDecodes()
    {
        var issues = new List<TextIssue>();
        Assert.Equal("\u65E5\u672C\u8A9E", ImapUtf7.Decode("&ZeVnLIqe"u8, issues));
        Assert.Contains(issues, i => i.Kind == TextIssueKind.InvalidImapUtf7);
    }
}

public class Rfc2047Tests
{
    private static string Decode(string s, List<TextIssue>? issues = null) => Rfc2047.DecodeText(s, issues);

    // RFC 2047 §8.
    [Theory]
    [InlineData("=?US-ASCII?Q?Keith_Moore?=", "Keith Moore")]
    [InlineData("=?ISO-8859-1?Q?Keld_J=F8rn_Simonsen?=", "Keld J\u00F8rn Simonsen")]
    [InlineData("=?ISO-8859-1?Q?Andr=E9?=", "Andr\u00E9")]
    [InlineData("=?ISO-8859-1?B?SWYgeW91IGNhbiByZWFkIHRoaXMgeW8=?= =?ISO-8859-2?B?dSB1bmRlcnN0YW5kIHRoZSBleGFtcGxlLg==?=", "If you can read this you understand the example.")]
    [InlineData("(=?ISO-8859-1?Q?a?=)", "(a)")]
    [InlineData("(=?ISO-8859-1?Q?a?= b)", "(a b)")]
    [InlineData("(=?ISO-8859-1?Q?a?= =?ISO-8859-1?Q?b?=)", "(ab)")]
    [InlineData("(=?ISO-8859-1?Q?a?=  =?ISO-8859-1?Q?b?=)", "(ab)")]
    [InlineData("(=?ISO-8859-1?Q?a?=\r\n  =?ISO-8859-1?Q?b?=)", "(ab)")]
    [InlineData("(=?ISO-8859-1?Q?a_b?=)", "(a b)")]
    [InlineData("(=?ISO-8859-1?Q?a?= =?ISO-8859-2?Q?_b?=)", "(a b)")]
    public void DecodesTheRfcExamples(string input, string expected)
    {
        var issues = new List<TextIssue>();
        Assert.Equal(expected, Decode(input, issues));
        Assert.Empty(issues);
    }

    [Fact]
    public void PlainTextIsReturnedAsIs()
    {
        Assert.Equal("Re: hello (world) = ? ?= done", Decode("Re: hello (world) = ? ?= done"));
    }

    [Fact]
    public void FoldsAreRemoved()
    {
        Assert.Equal("one two  three", Decode("one\r\n two \r\n three"));
        Assert.Equal("a b", Decode("a\n b"));
    }

    [Fact]
    public void SplitMultibyteCharacterIsJoinedAndReported()
    {
        // U+20AC is E2 82 AC; the two words cut it after the first byte.
        var issues = new List<TextIssue>();
        Assert.Equal("\u20AC", Decode("=?utf-8?B?4g==?= =?utf-8?B?gqw=?=", issues));
        Assert.Contains(issues, i => i.Kind == TextIssueKind.AmbiguousSplitMultibyte);
    }

    [Fact]
    public void DifferentCharsetsAreNotJoined()
    {
        Assert.Equal("\u00E9\u00E9", Decode("=?iso-8859-1?Q?=E9?= =?windows-1252?Q?=E9?="));
    }

    [Fact]
    public void ControlAndCarriageReturnSurviveSingleByteCharsets()
    {
        // OneHtml's decoder normalizes CR; mail text must not change.
        Assert.Equal("a\r\nb\rc\u00E9", MailEncoding.Windows1252.GetString([(byte)'a', 13, 10, (byte)'b', 13, (byte)'c', 0xE9]));
        Assert.Equal("\u00FF\u00FEx", MailEncoding.Windows1252.GetString([0xFF, 0xFE, (byte)'x']));
    }

    [Theory]
    [InlineData("=?utf-8?Q?abc", TextIssueKind.MalformedEncodedWord)]
    [InlineData("=?utf-8?X?abc?=", TextIssueKind.MalformedEncodedWord)]
    [InlineData("=?utf-8?B?YWJj!?=", TextIssueKind.InvalidBase64)]
    [InlineData("=?utf-8?B?YWJjZA?=", TextIssueKind.InvalidBase64)]
    [InlineData("=?utf-8?Q?a=ZZb?=", TextIssueKind.InvalidQuotedPrintable)]
    [InlineData("=?x-no-such?Q?abc?=", TextIssueKind.AmbiguousUnknownCharset)]
    [InlineData("=?utf-8?Q?a b?=", TextIssueKind.MalformedEncodedWord)]
    public void MalformedWordsAreReported(string input, TextIssueKind kind)
    {
        var issues = new List<TextIssue>();
        _ = Decode(input, issues);
        Assert.Contains(issues, i => i.Kind == kind);
    }

    [Fact]
    public void UnknownCharsetFallsBackToUtf8ThenWindows1252()
    {
        Assert.Equal("\u00E9", Decode("=?x-no-such?Q?=C3=A9?="));
        Assert.Equal("\u00E9", Decode("=?x-no-such?Q?=E9?="));
    }

    [Fact]
    public void Raw8BitIsUtf8ElseFallback()
    {
        var issues = new List<TextIssue>();
        Assert.Equal("caf\u00E9", Rfc2047.DecodeText(Encoding.UTF8.GetBytes("caf\u00E9"), issues));
        Assert.Empty(issues);
        Assert.Equal("caf\u00E9", Rfc2047.DecodeText([(byte)'c', (byte)'a', (byte)'f', 0xE9], issues));
        Assert.Contains(issues, i => i.Kind == TextIssueKind.AmbiguousRaw8Bit);
    }

    [Fact]
    public void EncodedWordInQuotedStringIsDecodedAndReported()
    {
        var issues = new List<TextIssue>();
        Assert.Equal("\u00E9", Rfc2047.DecodeText("=?utf-8?Q?=C3=A9?=", issues, EncodedWordContext.QuotedString));
        Assert.Contains(issues, i => i.Kind == TextIssueKind.AmbiguousEncodedWordInQuotedString);
    }

    [Theory]
    [InlineData("plain ascii")]
    [InlineData("caf\u00E9")]
    [InlineData("Re: \u65E5\u672C\u8A9E \u30C6\u30B9\u30C8 and more")]
    [InlineData("a =? b")]
    [InlineData("\u041F\u0440\u0438\u0432\u0435\u0442 \u043C\u0438\u0440 \u041F\u0440\u0438\u0432\u0435\u0442 \u043C\u0438\u0440 \u041F\u0440\u0438\u0432\u0435\u0442 \u043C\u0438\u0440 \u041F\u0440\u0438\u0432\u0435\u0442 \u043C\u0438\u0440 \u041F\u0440\u0438\u0432\u0435\u0442 \u043C\u0438\u0440")]
    [InlineData("x\U0001F600y \U0001F600\U0001F600\U0001F600\U0001F600\U0001F600\U0001F600\U0001F600\U0001F600\U0001F600\U0001F600\U0001F600\U0001F600\U0001F600")]
    [InlineData("  lead \u00E9 \u00E8   trail  ")]
    public void EncodeThenDecodeRoundTripsWithShortWords(string text)
    {
        string encoded = Rfc2047.Encode(text);
        Assert.True(Ascii.IsValid(encoded));
        foreach (string token in encoded.Split(' '))
        {
            Assert.True(token.Length <= 75, token);
        }

        Assert.Equal(text, Rfc2047.DecodeText(encoded));
    }

    [Fact]
    public void EncodeLeavesPlainAsciiAlone() => Assert.Equal("Hello, world!", Rfc2047.Encode("Hello, world!"));

    [Fact]
    public void EncodeWithOtherCharsetsReportsUnencodable()
    {
        var issues = new List<TextIssue>();
        string encoded = Rfc2047.Encode("caf\u00E9 \u65E5", MailEncoding.ForLabel("iso-8859-1"), issues);
        Assert.Contains(issues, i => i.Kind == TextIssueKind.Unencodable);
        Assert.StartsWith("=?windows-1252?", encoded);
    }
}

public class Rfc2231Tests
{
    private static List<DecodedParameter> Resolve(List<TextIssue>? issues, params (string Name, string Value)[] raw)
    {
        var c = new ParameterCollector();
        foreach ((string n, string v) in raw)
        {
            c.Add(Encoding.ASCII.GetBytes(n), Encoding.ASCII.GetBytes(v));
        }

        return c.Resolve(issues);
    }

    // RFC 2231 §3, §4 and §5 examples.
    [Fact]
    public void ExtendedValueWithCharsetAndLanguage()
    {
        DecodedParameter p = Assert.Single(Resolve(null, ("title*", "us-ascii'en-us'This%20is%20%2A%2A%2Afun%2A%2A%2A")));
        Assert.Equal("This is ***fun***", p.Value);
        Assert.Equal("en-us", p.Language);
        Assert.True(p.IsExtended);
    }

    [Fact]
    public void ContinuationsAreJoined()
    {
        DecodedParameter p = Assert.Single(Resolve(null, ("URL*0", "ftp://"), ("URL*1", "cs.utk.edu/pub/moore/bulk-mailer/bulk-mailer.tar")));
        Assert.Equal("ftp://cs.utk.edu/pub/moore/bulk-mailer/bulk-mailer.tar", p.Value);
    }

    [Fact]
    public void MixedEncodedAndPlainSegments()
    {
        DecodedParameter p = Assert.Single(Resolve(null,
            ("title*0*", "us-ascii'en'This%20is%20even%20more%20"),
            ("title*1*", "%2A%2A%2Afun%2A%2A%2A%20"),
            ("title*2", "isn't it!")));
        Assert.Equal("This is even more ***fun*** isn't it!", p.Value);
    }

    [Fact]
    public void SegmentsAreOrderedByNumberAndMultibyteSplitSurvives()
    {
        // U+20AC = E2 82 AC split between segments, given out of order.
        DecodedParameter p = Assert.Single(Resolve(null, ("f*1*", "%82%AC"), ("f*0*", "utf-8''%E2")));
        Assert.Equal("\u20AC", p.Value);
    }

    [Fact]
    public void GapAndDuplicateAreReported()
    {
        var issues = new List<TextIssue>();
        DecodedParameter p = Assert.Single(Resolve(issues, ("f*0", "a"), ("f*2", "c"), ("f*2", "d")));
        Assert.Equal("acd", p.Value);
        Assert.Contains(issues, i => i.Kind == TextIssueKind.AmbiguousParameterSegments);
        Assert.Contains(issues, i => i.Kind == TextIssueKind.DuplicateParameterSegment);
    }

    [Fact]
    public void ExtendedWinsOverPlain()
    {
        var issues = new List<TextIssue>();
        DecodedParameter p = Assert.Single(Resolve(issues, ("filename", "plain.txt"), ("filename*", "utf-8''%C3%A9.txt")));
        Assert.Equal("\u00E9.txt", p.Value);
        Assert.Contains(issues, i => i.Kind == TextIssueKind.AmbiguousParameterForms);
    }

    [Fact]
    public void MalformedExtendedValuesAreRepaired()
    {
        var issues = new List<TextIssue>();
        Assert.Equal("Gr\u00F6\u00DFe", Resolve(issues, ("f*", "Gr%C3%B6%C3%9Fe"))[0].Value);
        Assert.Contains(issues, i => i.Kind == TextIssueKind.MalformedExtendedValue);
        issues.Clear();
        Assert.Equal("a%ZZb%4", Resolve(issues, ("f*", "utf-8''a%ZZb%4"))[0].Value);
        Assert.Contains(issues, i => i.Kind == TextIssueKind.InvalidPercentEscape);
    }

    [Fact]
    public void PlainValuesDecodeEncodedWords()
    {
        var issues = new List<TextIssue>();
        Assert.Equal("\u00E9.txt", Resolve(issues, ("filename", "=?utf-8?Q?=C3=A9?=.txt"))[0].Value);
        Assert.Contains(issues, i => i.Kind == TextIssueKind.AmbiguousEncodedWordInQuotedString);
    }

    [Fact]
    public void NamesAreCaseInsensitiveAndOrderedByFirstAppearance()
    {
        List<DecodedParameter> r = Resolve(null, ("Name", "a"), ("CHARSET", "b"), ("name", "c"));
        Assert.Equal(["Name", "CHARSET"], r.Select(p => p.Name).ToArray());
        Assert.Equal("a", r[0].Value);
    }

    [Theory]
    [InlineData("report.pdf")]
    [InlineData("my file.txt")]
    [InlineData("caf\u00E9.txt")]
    [InlineData("\u65E5\u672C\u8A9E\u306E\u30D5\u30A1\u30A4\u30EB\u540D\u3067\u3059\u3002\u3068\u3066\u3082\u9577\u3044\u30D5\u30A1\u30A4\u30EB\u540D\u306B\u306A\u3063\u3066\u3057\u307E\u3044\u307E\u3057\u305F.pdf")]
    [InlineData("x\U0001F600.txt")]
    public void EncodeThenResolveRoundTrips(string value)
    {
        List<Rfc2231.EncodedPart> parts = Rfc2231.Encode("filename", value);
        var c = new ParameterCollector();
        foreach (Rfc2231.EncodedPart part in parts)
        {
            c.Add(Encoding.ASCII.GetBytes(part.Name), Encoding.ASCII.GetBytes(part.Value));
        }

        Assert.Equal(value, Assert.Single(c.Resolve()).Value);
        Assert.All(parts, p => Assert.True(p.Value.Length <= 80));
    }

    [Fact]
    public void EncodeShapes()
    {
        Assert.Equal(new Rfc2231.EncodedPart("filename", "report.pdf", false), Rfc2231.Encode("filename", "report.pdf").Single());
        Assert.Equal(new Rfc2231.EncodedPart("filename", "my file.txt", true), Rfc2231.Encode("filename", "my file.txt").Single());
        Assert.Equal(new Rfc2231.EncodedPart("filename*", "utf-8''caf%C3%A9.txt", false), Rfc2231.Encode("filename", "caf\u00E9.txt").Single());
    }
}

public class MailEncodingTests
{
    [Theory]
    [InlineData("utf-8", 65001)]
    [InlineData("UTF8", 65001)]
    [InlineData(" Windows-1252 ", 1252)]
    [InlineData("latin1", 1252)]
    [InlineData("iso-8859-2", 28592)]
    [InlineData("shift_jis", 932)]
    [InlineData("x-sjis", 932)]
    [InlineData("gb2312", 936)]
    [InlineData("big5", 950)]
    [InlineData("euc-kr", 949)]
    [InlineData("ks_c_5601-1987", 949)]
    [InlineData("utf-7", 65000)]
    [InlineData("cp50221", 50221)]
    [InlineData("windows-50222", 50222)]
    [InlineData("iso-2022-jp", 50220)]
    [InlineData("cp437", 437)]
    [InlineData("ibm850", 850)]
    [InlineData("codepage-1251", 1251)]
    [InlineData("ANSI_X3.4-1968", 1252)]
    [InlineData("utf-16le", 1200)]
    public void LabelsResolveToCodePages(string label, int codePage)
    {
        Assert.Equal(codePage, MailEncoding.ForLabel(label)!.CodePage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("x-no-such")]
    [InlineData("replacement")]
    [InlineData("iso-2022-cn")]
    [InlineData("unknown-8bit")]
    public void UnknownLabelsAreNull(string label) => Assert.Null(MailEncoding.ForLabel(label));

    [Fact]
    public void CodePagesMapBack()
    {
        Assert.Equal("windows-1252", MailEncoding.ForCodePage(1252)!.Name);
        Assert.Equal("utf-8", MailEncoding.ForCodePage(65001)!.Name);
        Assert.Equal("gb2312", MailEncoding.ForCodePage(936)!.Name);
        Assert.Same(MailEncoding.ForCodePage(50221), MailEncoding.ForLabel("csISO2022JP-ms") ?? MailEncoding.ForLabel("cp50221"));
        Assert.Null(MailEncoding.ForCodePage(1));
    }

    [Fact]
    public void Windows1252DecodesTheC1RangeAsTheStandardSays()
    {
        Assert.Equal("\u20AC\u201C", MailEncoding.Windows1252.GetString([0x80, 0x93]));
    }

    [Fact]
    public void InvalidUtf8IsReplacedAndReported()
    {
        var issues = new List<TextIssue>();
        Assert.Equal("a\uFFFDb", MailEncoding.Utf8.GetString([(byte)'a', 0xFF, (byte)'b'], issues));
        Assert.Equal(new TextIssue(TextIssueKind.InvalidBytes, 1, 1), Assert.Single(issues));
    }

    [Fact]
    public void ShiftJisAndEucKrDecode()
    {
        Assert.Equal("\u65E5\u672C\u8A9E", MailEncoding.ForLabel("shift_jis")!.GetString([0x93, 0xFA, 0x96, 0x7B, 0x8C, 0xEA]));
        Assert.Equal("\uD55C\uAD6D\uC5B4", MailEncoding.ForLabel("euc-kr")!.GetString([0xC7, 0xD1, 0xB1, 0xB9, 0xBE, 0xEE]));
    }

    [Fact]
    public void DosCodePagesDecode()
    {
        Assert.Equal("\u00C7\u00FC", MailEncoding.ForCodePage(437)!.GetString([0x80, 0x81]));
    }

    [Fact]
    public void EncodeRoundTripsAcrossCharsets()
    {
        foreach (string label in new[] { "utf-8", "windows-1252", "iso-8859-2", "shift_jis", "euc-kr", "gb2312", "iso-2022-jp", "utf-7" })
        {
            MailEncoding e = MailEncoding.ForLabel(label)!;
            string text = label is "windows-1252" ? "caf\u00E9" : label is "iso-8859-2" ? "\u0141\u00F3d\u017A" : label is "utf-8" or "utf-7" ? "\u65E5\u672C \u00E9" : "\u65E5\u672C\u8A9E";
            if (label is "euc-kr")
            {
                text = "\uD55C\uAD6D\uC5B4";
            }

            Assert.True(e.CanEncode, label);
            Assert.Equal(text, e.GetString(e.GetBytes(text)));
        }
    }

    [Fact]
    public void Utf16HasNoByteOrderMarkHandling()
    {
        Assert.Equal("\uFEFFa", MailEncoding.ForLabel("utf-16le")!.GetString([0xFF, 0xFE, 0x61, 0x00]));
    }
}

public class Iso2022Tests
{
    private static string Dec(string label, byte[] bytes, List<TextIssue>? issues = null) => MailEncoding.ForLabel(label)!.GetString(bytes, issues);

    private static readonly byte[] Jp = [0x1B, 0x24, 0x42, 0x46, 0x7C, 0x4B, 0x5C, 0x38, 0x6C, 0x1B, 0x28, 0x42]; // ESC $ B  (nichi hon go)  ESC ( B

    [Fact]
    public void DecodesJisX0208() => Assert.Equal("\u65E5\u672C\u8A9E", Dec("iso-2022-jp", Jp));

    [Fact]
    public void AsciiAndRomanAndLineEnds()
    {
        byte[] b = [(byte)'a', 0x1B, 0x28, 0x4A, 0x5C, 0x7E, 0x1B, 0x28, 0x42, 0x5C, 0x0D, 0x0A, 0x1B, 0x24, 0x42, 0x46, 0x7C, 0x0A, (byte)'z'];
        // After the line end the mode is ASCII again even though ESC ( B is missing, so 'z' is not read as kanji.
        Assert.Equal("a\u00A5\u203E\\\r\n\u65E5\n" + "z", Dec("iso-2022-jp", b));
    }

    [Fact]
    public void HalfWidthKanaNeedsAllowingVariant()
    {
        byte[] kana = [0x1B, 0x28, 0x49, 0x31, 0x32, 0x1B, 0x28, 0x42]; // ESC ( I  1 2
        var issues = new List<TextIssue>();
        Assert.Equal("\uFF71\uFF72", Dec("cp50221", kana, issues));
        Assert.Empty(issues);
        Assert.Equal("\uFF71\uFF72", Dec("iso-2022-jp", kana, issues));
        Assert.Contains(issues, i => i.Kind == TextIssueKind.AmbiguousIso2022Repertoire);
    }

    [Fact]
    public void ShiftOutShiftInKana()
    {
        byte[] b = [0x0E, 0x31, 0x32, 0x0F, (byte)'x'];
        Assert.Equal("\uFF71\uFF72x", Dec("cp50222", b));
    }

    [Fact]
    public void Jp2HasGreekAndLatinSingleShifts()
    {
        byte[] b = [0x1B, 0x2E, 0x41, 0x1B, 0x4E, 0x69, 0x1B, 0x2E, 0x46, 0x1B, 0x4E, 0x41]; // ESC . A  ESC N i  ESC . F  ESC N A
        Assert.Equal("\u00E9\u0391", Dec("iso-2022-jp-2", b));
    }

    [Fact]
    public void Jis0212InJp1()
    {
        byte[] b = [0x1B, 0x24, 0x28, 0x44, 0x22, 0x2F, 0x1B, 0x28, 0x42];
        var issues = new List<TextIssue>();
        Assert.Equal("\u02D8", Dec("iso-2022-jp-1", b, issues));
        Assert.Empty(issues);
        _ = Dec("iso-2022-jp", b, issues);
        Assert.Contains(issues, i => i.Kind == TextIssueKind.AmbiguousIso2022Repertoire);
    }

    [Fact]
    public void KoreanAndHz()
    {
        byte[] kr = [0x1B, 0x24, 0x29, 0x43, 0x0E, 0x47, 0x51, 0x0F, (byte)'!'];
        Assert.Equal("\uD55C!", Dec("iso-2022-kr", kr));
        Assert.Equal("a\u4E2D~b", Dec("hz-gb-2312", "a~{VP~}~~b"u8.ToArray()));
    }

    [Fact]
    public void DamageIsRepairedNotThrown()
    {
        var issues = new List<TextIssue>();
        Assert.Contains('\uFFFD', Dec("iso-2022-jp", [0x1B, 0x24, 0x42, 0x46], issues)); // lone lead byte
        Assert.Contains('\uFFFD', Dec("iso-2022-jp", [0x1B, 0x99, 0x80], issues));
        Assert.NotEmpty(issues);
    }
}
