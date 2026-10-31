using System.Buffers;
using System.Text;
using System.Text.Unicode;
using Utf8Codec = System.Text.Unicode.Utf8;
using RecursiveAI.OneHtml;
using RecursiveAI.OneHtml.Encodings;

namespace RecursiveAI.OneMail.Text;

/// <summary>
/// A charset as mail uses it: everything in the WHATWG Encoding Standard (decoded by OneHtml's tables, so mail and web
/// agree), plus UTF-7, ISO-2022-JP and its variants (including the Windows 50221/50222 behaviours), ISO-2022-KR, HZ,
/// UTF-32, the DOS code pages Outlook stores by number, and a map between Windows code page numbers and labels.
/// </summary>
/// <remarks>
/// Instances are immutable singletons: obtain them with <see cref="ForLabel"/> or <see cref="ForCodePage"/> and compare
/// with <c>==</c>. Decoding never throws and never writes a byte-order mark into the text. Decoding follows the Encoding
/// Standard, so <c>us-ascii</c> and <c>iso-8859-1</c> decode as windows-1252, as every mail client does in practice.
/// </remarks>
public sealed class MailEncoding
{
    private enum Kind
    {
        Utf8,
        Utf16Le,
        Utf16Be,
        Utf32Le,
        Utf32Be,
        Whatwg,
        Utf7,
        Iso2022,
        CodePage,
    }

    private static readonly ParseLimits s_unlimited = new() { MaxInputLength = int.MaxValue, MaxOffsetMapRuns = int.MaxValue };

    private static readonly MailEncoding[] s_all = BuildTable();
    private static readonly Dictionary<int, MailEncoding> s_byCodePage = IndexByCodePage(s_all);
    private static readonly Dictionary<WhatwgEncoding, MailEncoding> s_byWhatwg = IndexByWhatwg(s_all);
    private static readonly Dictionary<string, MailEncoding> s_extraLabels = BuildExtraLabels();

    private readonly Kind _kind;
    private readonly WhatwgEncoding? _whatwg;
    private readonly Iso2022.Variant _variant;
    private Encoding? _strict;
    private Encoding? _lenient;

    private MailEncoding(string name, int codePage, Kind kind, WhatwgEncoding? whatwg = null, Iso2022.Variant variant = default)
    {
        Name = name;
        CodePage = codePage;
        _kind = kind;
        _whatwg = whatwg;
        _variant = variant;
    }

    /// <summary>UTF-8 (code page 65001).</summary>
    public static MailEncoding Utf8 { get; } = ForCodePage(65001)!;

    /// <summary>windows-1252 (code page 1252), which is also what <c>us-ascii</c> and <c>iso-8859-1</c> labels decode as.</summary>
    public static MailEncoding Windows1252 { get; } = ForCodePage(1252)!;

    /// <summary>The charset name to write in a header: the IANA / Outlook spelling, for example <c>utf-8</c>, <c>windows-1252</c>, <c>shift_jis</c>, <c>gb2312</c>.</summary>
    public string Name { get; }

    /// <summary>The Windows code page number (what MAPI stores in <c>PidTagInternetCodepage</c>), or 0 when the charset has none.</summary>
    public int CodePage { get; }

    /// <summary>True when <see cref="GetBytes"/> is available: UTF-8, UTF-7 and every charset that has a code page number.</summary>
    public bool CanEncode => _kind is Kind.Utf8 or Kind.Utf7 || CodePage != 0;

    /// <summary>
    /// Finds a charset by label, ignoring ASCII case and surrounding whitespace. Looks first at the mail-only labels
    /// (<c>utf-7</c>, <c>iso-2022-jp-2</c>, <c>iso-2022-kr</c>, <c>hz-gb-2312</c>, <c>cp437</c>, <c>cp50221</c>, <c>windows-NNNN</c>,
    /// <c>codepage NNNN</c> ...), then at the Encoding Standard's label table.
    /// </summary>
    /// <param name="label">A charset label from a header or a MIME part.</param>
    /// <returns>The charset, or <see langword="null"/> for an unknown label, an empty one, <c>replacement</c> and the
    /// labels the standard maps to it (ISO-2022-CN, ISO-2022-CN-EXT, <c>unknown-8bit</c> is unknown too).</returns>
    public static MailEncoding? ForLabel(ReadOnlySpan<char> label)
    {
        label = label.Trim(" \t\r\n\f");
        if (label.IsEmpty || label.Length > 64)
        {
            return null;
        }

        // Bounded: the label was checked above to be at most 64 characters.
        Span<char> lower = stackalloc char[label.Length];
        for (int i = 0; i < label.Length; i++)
        {
            char c = label[i];
            lower[i] = c is >= 'A' and <= 'Z' ? (char)(c + 0x20) : c;
        }

        ReadOnlySpan<char> key = lower;
        if (s_extraLabels.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(key, out MailEncoding? extra))
        {
            return extra;
        }

        int number = NumberedLabel(key);
        if (number > 0)
        {
            return ForCodePage(number);
        }

        WhatwgEncoding? w = WhatwgEncoding.ForLabel(key);
        if (w is null || w == WhatwgEncoding.Replacement)
        {
            return null;
        }

        return s_byWhatwg.TryGetValue(w, out MailEncoding? known) ? known : new MailEncoding(w.Name, 0, Kind.Whatwg, w);
    }

    /// <summary>Finds a charset by Windows code page number, as Outlook stores it. Includes 50220/50221/50222, 20127 (US-ASCII), 437 and the other DOS pages, 65000 (UTF-7) and 28591..28605.</summary>
    /// <param name="codePage">For example 1252, 65001, 932, 50221.</param>
    /// <returns>The charset, or <see langword="null"/> when the number is not known.</returns>
    public static MailEncoding? ForCodePage(int codePage) => s_byCodePage.GetValueOrDefault(codePage);

    /// <summary>Decodes bytes to text. Never throws.</summary>
    /// <param name="bytes">Encoded bytes.</param>
    /// <param name="issues">Receives an issue for each repair (<see cref="TextIssueKind.InvalidBytes"/> with the offset of
    /// the first bad byte for the table-driven encodings; the ISO-2022 and UTF-7 decoders report each one). May be <see langword="null"/>.</param>
    /// <returns>The text. Invalid bytes become U+FFFD as the Encoding Standard says. A UTF-8 or UTF-16 byte order mark stays
    /// in the text as U+FEFF.</returns>
    public string GetString(ReadOnlySpan<byte> bytes, List<TextIssue>? issues = null)
    {
        switch (_kind)
        {
            case Kind.Utf8:
                return DecodeUtf8(bytes, issues);
            case Kind.Utf7:
                return Utf7.Decode(bytes, issues);
            case Kind.Iso2022:
                return Iso2022.Decode(_variant, bytes, issues);
            case Kind.Whatwg:
                return DecodeWhatwg(_whatwg!, bytes, issues);
            default:
                return DecodeBcl(bytes, issues);
        }
    }

    /// <summary>Encodes text. Characters the charset cannot hold become <c>?</c> and are reported.</summary>
    /// <param name="text">The text.</param>
    /// <param name="issues">Receives <see cref="TextIssueKind.Unencodable"/> (offset = UTF-16 index of the text, not a byte offset) when something was replaced; may be <see langword="null"/>.</param>
    /// <returns>The bytes, without a byte-order mark.</returns>
    /// <exception cref="NotSupportedException">The charset has no encoder (<see cref="CanEncode"/> is false).</exception>
    public byte[] GetBytes(string text, List<TextIssue>? issues = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_kind == Kind.Utf8)
        {
            return Encoding.UTF8.GetBytes(text);
        }

        if (_kind == Kind.Utf7)
        {
            return Encoding.ASCII.GetBytes(Utf7.Encode(text));
        }

        if (CodePage == 0)
        {
            throw new NotSupportedException("The charset " + Name + " has no encoder.");
        }

        Encoding strict = _strict ??= CreateBcl(throwOnInvalid: true);
        try
        {
            return strict.GetBytes(text);
        }
        catch (EncoderFallbackException e)
        {
            issues?.Add(new TextIssue(TextIssueKind.Unencodable, e.Index, 0));
            return (_lenient ??= CreateBcl(throwOnInvalid: false)).GetBytes(text);
        }
    }

    /// <inheritdoc />
    public override string ToString() => Name;

    private static string DecodeUtf8(ReadOnlySpan<byte> bytes, List<TextIssue>? issues)
    {
        if (Utf8Codec.IsValid(bytes))
        {
            return Encoding.UTF8.GetString(bytes);
        }

        if (issues is not null)
        {
            // Offset of the first invalid sequence.
            // A small scratch buffer so Utf8.ToUtf16 can report where the invalid data starts; fixed size.
            Span<char> probe = stackalloc char[16];
            int offset = bytes.Length;
            int read = 0;
            while (read < bytes.Length)
            {
                OperationStatus status = Utf8Codec.ToUtf16(bytes[read..], probe, out int consumed, out _, replaceInvalidSequences: false, isFinalBlock: true);
                read += consumed;
                if (status == OperationStatus.InvalidData)
                {
                    offset = read;
                    break;
                }

                if (status == OperationStatus.Done)
                {
                    break;
                }
            }

            issues.Add(new TextIssue(TextIssueKind.InvalidBytes, offset, 1));
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private string DecodeBcl(ReadOnlySpan<byte> bytes, List<TextIssue>? issues)
    {
        Encoding strict = _strict ??= CreateBcl(throwOnInvalid: true);
        try
        {
            return strict.GetString(bytes);
        }
        catch (DecoderFallbackException e)
        {
            issues?.Add(new TextIssue(TextIssueKind.InvalidBytes, e.Index < 0 ? 0 : e.Index, 1));
            return (_lenient ??= CreateBcl(throwOnInvalid: false)).GetString(bytes);
        }
    }

    private Encoding CreateBcl(bool throwOnInvalid)
    {
        EncoderFallback enc = throwOnInvalid ? EncoderFallback.ExceptionFallback : new EncoderReplacementFallback("?");
        DecoderFallback dec = throwOnInvalid ? DecoderFallback.ExceptionFallback : DecoderFallback.ReplacementFallback;
        int cp = _kind switch
        {
            Kind.Utf16Le => 1200,
            Kind.Utf16Be => 1201,
            Kind.Utf32Le => 12000,
            Kind.Utf32Be => 12001,
            _ => CodePage,
        };

        // CodePagesEncodingProvider knows the DOS, EBCDIC-free Windows and CJK pages without ICU (the build runs with invariant globalization).
        return cp switch
        {
            1200 => new UnicodeEncoding(false, false, throwOnInvalid),
            1201 => new UnicodeEncoding(true, false, throwOnInvalid),
            12000 => new UTF32Encoding(false, false, throwOnInvalid),
            12001 => new UTF32Encoding(true, false, throwOnInvalid),
            _ => CodePagesEncodingProvider.Instance.GetEncoding(cp, enc, dec) ?? BuiltIn(cp, enc, dec),
        };
    }

    // The provider lists only the encodings the runtime does not have itself; ISO-8859-1 (28591) and US-ASCII (20127) are the runtime's own.
    private static Encoding BuiltIn(int cp, EncoderFallback enc, DecoderFallback dec)
    {
        try
        {
            return Encoding.GetEncoding(cp, enc, dec);
        }
        catch (ArgumentException e)
        {
            throw new NotSupportedException("The runtime has no encoder for code page " + cp + ".", e);
        }
    }

    // Decodes with an Encoding Standard decoder (OneHtml). OneHtml's SourceText is built for HTML: it lets a byte order
    // mark override the encoding and turns CR and CRLF into LF. Mail text must survive unchanged, so the input is cut at
    // every CR (a CR byte is a character of its own in all of these encodings, never half of one) and each piece gets a
    // leading space that stops it being taken for a byte order mark.
    internal static string DecodeWhatwg(WhatwgEncoding encoding, ReadOnlySpan<byte> bytes, List<TextIssue>? issues)
    {
        return DecodeWhatwg(encoding, bytes, issues, out _);
    }

    internal static string DecodeWhatwg(WhatwgEncoding encoding, ReadOnlySpan<byte> bytes, List<TextIssue>? issues, out int errors)
    {
        errors = 0;
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        if (encoding.IsUtf16)
        {
            return encoding == WhatwgEncoding.Utf16Le ? new UnicodeEncoding(false, false).GetString(bytes) : new UnicodeEncoding(true, false).GetString(bytes);
        }

        bool ascii = System.Text.Ascii.IsValid(bytes);
        if (ascii && encoding != WhatwgEncoding.XUserDefined)
        {
            // Every other Encoding Standard decoder maps ASCII bytes to themselves.
            return Encoding.ASCII.GetString(bytes);
        }

        int firstError = -1;
        if (bytes.IndexOf((byte)'\r') < 0)
        {
            string only = DecodePiece(encoding, bytes, 0, ref errors, ref firstError);
            ReportErrors(issues, errors, firstError);
            return only;
        }

        var sb = new StringBuilder(bytes.Length);
        int pos = 0;
        while (true)
        {
            int cr = bytes[pos..].IndexOf((byte)'\r');
            int end = cr < 0 ? bytes.Length : pos + cr;
            sb.Append(DecodePiece(encoding, bytes[pos..end], pos, ref errors, ref firstError));
            if (cr < 0)
            {
                break;
            }

            sb.Append('\r');
            pos = end + 1;
        }

        ReportErrors(issues, errors, firstError);
        return sb.ToString();
    }

    private static void ReportErrors(List<TextIssue>? issues, int errors, int firstError)
    {
        if (errors > 0)
        {
            issues?.Add(new TextIssue(TextIssueKind.InvalidBytes, Math.Max(firstError, 0), 1));
        }
    }

    private static string DecodePiece(WhatwgEncoding encoding, ReadOnlySpan<byte> piece, int baseOffset, ref int errors, ref int firstError)
    {
        if (piece.IsEmpty)
        {
            return string.Empty;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(piece.Length + 1);
        try
        {
            rented[0] = 0x20;
            piece.CopyTo(rented.AsSpan(1));
            SourceText st = SourceText.Decode(new ReadOnlyMemory<byte>(rented, 0, piece.Length + 1), encoding, EncodingConfidence.Certain, s_unlimited);
            string text = st.Text;
            if (st.DecodeErrorCount > 0)
            {
                errors += st.DecodeErrorCount;
                if (firstError < 0)
                {
                    int idx = text.IndexOf('\uFFFD');
                    firstError = baseOffset + Math.Max(st.ToByteOffset(Math.Max(idx, 1)) - 1, 0);
                }
            }

            return text.Substring(1);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    // --- tables -----------------------------------------------------------------------------------------------

    private static MailEncoding[] BuildTable()
    {
        var list = new List<MailEncoding>();
        void W(int cp, string name, string whatwgLabel) => list.Add(new MailEncoding(name, cp, Kind.Whatwg, WhatwgEncoding.ForLabel(whatwgLabel)));
        void B(int cp, string name) => list.Add(new MailEncoding(name, cp, Kind.CodePage));
        void I(int cp, string name, Iso2022.Variant v) => list.Add(new MailEncoding(name, cp, Kind.Iso2022, variant: v));

        list.Add(new MailEncoding("utf-8", 65001, Kind.Utf8, WhatwgEncoding.ForLabel("utf-8")));
        list.Add(new MailEncoding("utf-16", 1200, Kind.Utf16Le));
        list.Add(new MailEncoding("utf-16be", 1201, Kind.Utf16Be));
        list.Add(new MailEncoding("utf-32", 12000, Kind.Utf32Le));
        list.Add(new MailEncoding("utf-32be", 12001, Kind.Utf32Be));
        list.Add(new MailEncoding("utf-7", 65000, Kind.Utf7));
        W(1250, "windows-1250", "windows-1250");
        W(1251, "windows-1251", "windows-1251");
        W(1252, "windows-1252", "windows-1252");
        W(1253, "windows-1253", "windows-1253");
        W(1254, "windows-1254", "windows-1254");
        W(1255, "windows-1255", "windows-1255");
        W(1256, "windows-1256", "windows-1256");
        W(1257, "windows-1257", "windows-1257");
        W(1258, "windows-1258", "windows-1258");
        W(874, "windows-874", "windows-874");
        W(28591, "iso-8859-1", "iso-8859-1");
        W(28592, "iso-8859-2", "iso-8859-2");
        W(28593, "iso-8859-3", "iso-8859-3");
        W(28594, "iso-8859-4", "iso-8859-4");
        W(28595, "iso-8859-5", "iso-8859-5");
        W(28596, "iso-8859-6", "iso-8859-6");
        W(28597, "iso-8859-7", "iso-8859-7");
        W(28598, "iso-8859-8", "iso-8859-8");
        W(28599, "iso-8859-9", "iso-8859-9");
        W(28600, "iso-8859-10", "iso-8859-10");
        W(28603, "iso-8859-13", "iso-8859-13");
        W(28604, "iso-8859-14", "iso-8859-14");
        W(28605, "iso-8859-15", "iso-8859-15");
        W(28606, "iso-8859-16", "iso-8859-16");
        W(38598, "iso-8859-8-i", "iso-8859-8-i");
        W(20866, "koi8-r", "koi8-r");
        W(21866, "koi8-u", "koi8-u");
        W(10000, "macintosh", "macintosh");
        W(10007, "x-mac-cyrillic", "x-mac-cyrillic");
        W(866, "cp866", "ibm866");
        W(932, "shift_jis", "shift_jis");
        W(936, "gb2312", "gbk");
        W(949, "ks_c_5601-1987", "euc-kr");
        W(950, "big5", "big5");
        W(51932, "euc-jp", "euc-jp");
        W(51949, "euc-kr", "euc-kr");
        W(54936, "gb18030", "gb18030");
        W(20127, "us-ascii", "windows-1252");
        I(50220, "iso-2022-jp", Iso2022.Variant.Jp);
        I(50221, "csISO2022JP", Iso2022.Variant.Cp50221);
        I(50222, "iso-2022-jp", Iso2022.Variant.Cp50222);
        I(50225, "iso-2022-kr", Iso2022.Variant.Kr);
        I(52936, "hz-gb-2312", Iso2022.Variant.Hz);
        // Not code pages, but labels for the same decoder.
        B(437, "ibm437");
        B(737, "ibm737");
        B(775, "ibm775");
        B(850, "ibm850");
        B(852, "ibm852");
        B(855, "ibm855");
        B(857, "ibm857");
        B(858, "ibm00858");
        B(860, "ibm860");
        B(861, "ibm861");
        B(862, "dos-862");
        B(863, "ibm863");
        B(864, "ibm864");
        B(865, "ibm865");
        B(869, "ibm869");
        B(1361, "johab");
        return list.ToArray();
    }

    private static Dictionary<int, MailEncoding> IndexByCodePage(MailEncoding[] all)
    {
        var map = new Dictionary<int, MailEncoding>();
        foreach (MailEncoding e in all)
        {
            map[e.CodePage] = e;
        }

        return map;
    }

    private static Dictionary<WhatwgEncoding, MailEncoding> IndexByWhatwg(MailEncoding[] all)
    {
        // The preferred MailEncoding for each Encoding Standard encoding: the first table row that uses it, except that
        // the standard's windows-1252 is reached through windows-1252 itself rather than iso-8859-1 or us-ascii, and the
        // CJK encodings through their well-known names.
        var map = new Dictionary<WhatwgEncoding, MailEncoding>();
        foreach (MailEncoding e in all)
        {
            if (e._whatwg is not null && !map.ContainsKey(e._whatwg))
            {
                map[e._whatwg] = e;
            }
        }

        map[WhatwgEncoding.Gbk] = all.First(e => e.CodePage == 936);
        map[WhatwgEncoding.EucKr] = all.First(e => e.CodePage == 949);
        map[WhatwgEncoding.Iso2022Jp] = all.First(e => e.CodePage == 50220);
        map[WhatwgEncoding.Utf16Le] = all.First(e => e.CodePage == 1200);
        map[WhatwgEncoding.Utf16Be] = all.First(e => e.CodePage == 1201);
        return map;
    }

    private static Dictionary<string, MailEncoding> BuildExtraLabels()
    {
        MailEncoding Cp(int n) => s_byCodePage[n];
        var map = new Dictionary<string, MailEncoding>(StringComparer.Ordinal);
        void Add(MailEncoding e, params string[] labels)
        {
            foreach (string l in labels)
            {
                map[l] = e;
            }
        }

        Add(Cp(65000), "utf-7", "utf7", "unicode-1-1-utf-7", "unicode-2-0-utf-7", "x-unicode-2-0-utf-7", "x-unicode11utf7", "csunicode11utf7");
        Add(Cp(12000), "utf-32", "utf-32le", "utf32", "ucs-4", "ucs-4le", "cp12000");
        Add(Cp(12001), "utf-32be", "ucs-4be", "cp12001");
        // These four are "replacement" in the Encoding Standard because browsers do not decode them; mail clients do.
        Add(Cp(50225), "iso-2022-kr", "csiso2022kr", "cp50225", "iso2022kr");
        Add(Cp(52936), "hz-gb-2312", "hz", "cp52936");
        // The Windows spellings differ from the plain label: 50221 allows half-width katakana by escape, 50222 by SO/SI.
        Add(Cp(50221), "cp50221", "windows-50221", "x-windows-50221", "iso-2022-jp-ms", "csiso2022jp-ms");
        Add(Cp(50222), "cp50222", "windows-50222", "x-windows-50222");
        Add(Cp(50220), "cp50220", "windows-50220", "x-windows-50220");
        var jp1 = new MailEncoding("iso-2022-jp-1", 0, Kind.Iso2022, variant: Iso2022.Variant.Jp1);
        var jp2 = new MailEncoding("iso-2022-jp-2", 0, Kind.Iso2022, variant: Iso2022.Variant.Jp2);
        var jp3 = new MailEncoding("iso-2022-jp-3", 0, Kind.Iso2022, variant: Iso2022.Variant.Jp3);
        Add(jp1, "iso-2022-jp-1", "iso2022jp1");
        Add(jp2, "iso-2022-jp-2", "csiso2022jp2", "iso2022jp2");
        Add(jp3, "iso-2022-jp-3", "iso-2022-jp-2004", "iso2022jp3");
        // DOS code pages by their usual labels.
        foreach (int n in new[] { 437, 737, 775, 850, 852, 855, 857, 858, 860, 861, 862, 863, 864, 865, 869 })
        {
            Add(Cp(n), "ibm" + n, "cp" + n, "ibm0" + n, "csibm" + n, "oem" + n, "cspc" + n);
        }

        Add(Cp(858), "ibm00858", "cp00858");
        Add(Cp(1361), "johab", "ms1361", "x-johab");
        return map;
    }

    // "cp1252", "windows-1252", "x-windows-1252", "codepage1252", "codepage-1252", "cp_1252": a code page number in a Windows-style label.
    private static int NumberedLabel(ReadOnlySpan<char> key)
    {
        foreach (string prefix in new[] { "x-windows-", "windows-", "codepage-", "codepage", "code page ", "code_page_", "cp_", "cp-", "cp" })
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(key[prefix.Length..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n) && s_byCodePage.ContainsKey(n))
            {
                return n;
            }
        }

        return 0;
    }
}
