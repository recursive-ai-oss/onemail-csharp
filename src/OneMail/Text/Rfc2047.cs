using System.Buffers;
using System.Text;
using System.Text.Unicode;

namespace RecursiveAI.OneMail.Text;

/// <summary>Where an encoded word was found; RFC 2047 allows them in some places only.</summary>
public enum EncodedWordContext
{
    /// <summary>Unstructured text such as <c>Subject</c> (RFC 2047 §5(1)), a phrase or a comment: encoded words are expected.</summary>
    Unstructured,

    /// <summary>
    /// The inside of a quoted string or a parameter value. RFC 2047 §5 forbids encoded words there, but clients decode
    /// them, and so do we (reported as <see cref="TextIssueKind.AmbiguousEncodedWordInQuotedString"/>).
    /// </summary>
    QuotedString,
}

/// <summary>RFC 2047 encoded words: <c>=?charset?B?base64?=</c> and <c>=?charset?Q?quoted-printable?=</c>.</summary>
public static class Rfc2047
{
    [ThreadStatic]
    private static StringBuilder? t_builder;

    private const int MaxWordLength = 75;
    private const int MaxCharsetLength = 64;

    /// <summary>
    /// Decodes header text: encoded words are replaced by their text and everything else is kept.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>RFC 2047 §6.2: white space between two adjacent encoded words is dropped; white space between a word and plain text stays.</item>
    /// <item>RFC 5322 §2.2.3: a fold (CRLF, or a bare CR or LF, followed by space or tab) is removed first.</item>
    /// <item>Adjacent words with the same charset are decoded as one byte stream, so a multibyte character split between words survives.</item>
    /// <item>An encoded word must have no white space inside it; anything else that looks like one is kept as literal text and reported as <see cref="TextIssueKind.MalformedEncodedWord"/>.</item>
    /// <item>Plain text with 8-bit bytes is read as UTF-8 when it is valid UTF-8 (RFC 6532) and with <paramref name="fallback"/> otherwise.</item>
    /// <item>A word with an unknown charset is decoded as UTF-8 if valid, else windows-1252.</item>
    /// </list>
    /// Never throws. Allocates the result string and, for headers with encoded words, a pooled buffer.
    /// </remarks>
    /// <param name="value">The header value as bytes (unfolded or folded).</param>
    /// <param name="issues">Receives the repairs and guesses; offsets are into <paramref name="value"/>. May be <see langword="null"/>.</param>
    /// <param name="context">Where the text came from; <see cref="EncodedWordContext.QuotedString"/> adds an ambiguity issue per word.</param>
    /// <param name="fallback">The charset for undeclared 8-bit text that is not UTF-8; <see langword="null"/> means windows-1252.</param>
    /// <returns>The decoded text.</returns>
    public static string DecodeText(ReadOnlySpan<byte> value, List<TextIssue>? issues = null, EncodedWordContext context = EncodedWordContext.Unstructured, MailEncoding? fallback = null)
    {
        fallback ??= MailEncoding.Windows1252;
        if (value.IndexOf("=?"u8) < 0)
        {
            return DecodePlain(value, 0, issues, fallback);
        }

        // A cached builder and a pooled byte buffer keep a decode down to the result string.
        StringBuilder sb = t_builder ?? new StringBuilder(256);
        t_builder = null;
        sb.Clear();
        byte[] run = ArrayPool<byte>.Shared.Rent(Math.Max(64, value.Length));
        try
        {
            DecodeWords(value, sb, run, issues, context, fallback);
            return sb.ToString();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(run);
            if (sb.Capacity <= 16 * 1024)
            {
                t_builder = sb;
            }
        }
    }

    /// <summary>Decodes header text given as a string of ASCII (or already decoded) characters; see <see cref="DecodeText(ReadOnlySpan{byte}, List{TextIssue}?, EncodedWordContext, MailEncoding?)"/>. Characters above U+00FF are encoded as UTF-8 first.</summary>
    /// <param name="value">The header value.</param>
    /// <param name="issues">Receives the repairs and guesses; offsets are into the UTF-8 form of <paramref name="value"/>. May be <see langword="null"/>.</param>
    /// <param name="context">Where the text came from.</param>
    /// <param name="fallback">As for the byte overload.</param>
    /// <returns>The decoded text.</returns>
    public static string DecodeText(string value, List<TextIssue>? issues = null, EncodedWordContext context = EncodedWordContext.Unstructured, MailEncoding? fallback = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        int max = Encoding.UTF8.GetMaxByteCount(value.Length);
        byte[] rented = ArrayPool<byte>.Shared.Rent(max);
        try
        {
            int n = Encoding.UTF8.GetBytes(value, rented);
            return DecodeText(rented.AsSpan(0, n), issues, context, fallback);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private enum WordStatus
    {
        NotAWord,
        Malformed,
        Word,
    }

    private static void DecodeWords(ReadOnlySpan<byte> value, StringBuilder sb, byte[] run, List<TextIssue>? issues, EncodedWordContext context, MailEncoding fallback)
    {
        int p = 0;                  // start of text not yet emitted
        int search = 0;             // where to look for the next "=?"
        int runLength = 0;          // bytes pending in 'run'
        int runWordOffset = 0;      // source offset of the first word in the pending run
        int runCharsetStart = 0;
        int runCharsetLength = 0;
        bool runActive = false;
        int prevWordStart = 0;
        int prevWordBytes = 0;

        while (true)
        {
            int q = -1;
            int wordEnd = 0;
            int charsetStart = 0;
            int charsetLength = 0;
            int textStart = 0;
            int textLength = 0;
            bool isB = false;
            bool spaces = false;
            while (search < value.Length)
            {
                int idx = value[search..].IndexOf("=?"u8);
                if (idx < 0)
                {
                    break;
                }

                int at = search + idx;
                WordStatus status = ParseWord(value, at, out wordEnd, out charsetStart, out charsetLength, out isB, out textStart, out textLength, out spaces);
                if (status == WordStatus.Word)
                {
                    q = at;
                    break;
                }

                if (status == WordStatus.Malformed)
                {
                    issues?.Add(new TextIssue(TextIssueKind.MalformedEncodedWord, at, 2));
                }

                search = at + 2;
            }

            if (q < 0)
            {
                FlushRun(sb, run, runLength, value, runCharsetStart, runCharsetLength, runWordOffset, issues, fallback);
                AppendPlain(sb, value[p..], p, issues, fallback);
                return;
            }

            ReadOnlySpan<byte> gap = value[p..q];
            bool joins = runActive && IsAllWhitespace(gap);
            if (!joins)
            {
                FlushRun(sb, run, runLength, value, runCharsetStart, runCharsetLength, runWordOffset, issues, fallback);
                runLength = 0;
                runActive = false;
                AppendPlain(sb, gap, p, issues, fallback);
            }

            if (spaces)
            {
                issues?.Add(new TextIssue(TextIssueKind.MalformedEncodedWord, q, wordEnd - q));
            }

            if (wordEnd - q > MaxWordLength)
            {
                issues?.Add(new TextIssue(TextIssueKind.EncodedWordTooLong, q, wordEnd - q));
            }

            if (context == EncodedWordContext.QuotedString)
            {
                issues?.Add(new TextIssue(TextIssueKind.AmbiguousEncodedWordInQuotedString, q, wordEnd - q));
            }

            ReadOnlySpan<byte> charset = value.Slice(charsetStart, charsetLength);
            if (joins && !CharsetEquals(value.Slice(runCharsetStart, runCharsetLength), charset))
            {
                // Different charset: the run ends here (the white space between the two words is still dropped).
                FlushRun(sb, run, runLength, value, runCharsetStart, runCharsetLength, runWordOffset, issues, fallback);
                runLength = 0;
                runActive = false;
            }

            if (!runActive)
            {
                runActive = true;
                runWordOffset = q;
                runCharsetStart = charsetStart;
                runCharsetLength = charsetLength;
            }
            else if (runLength > 0 && issues is not null && CharsetIsUtf8(charset) && !Utf8.IsValid(run.AsSpan(prevWordBytes == 0 ? 0 : runLength - prevWordBytes, prevWordBytes)))
            {
                issues.Add(new TextIssue(TextIssueKind.AmbiguousSplitMultibyte, prevWordStart, wordEnd - prevWordStart));
            }

            ReadOnlySpan<byte> text = value.Slice(textStart, textLength);
            // The run buffer is as long as the whole value, which bounds the bytes of every word together.
            int written = isB
                ? DecodeBase64(text, run.AsSpan(runLength), issues, q)
                : DecodeQ(text, run.AsSpan(runLength), issues, q);
            prevWordBytes = written;
            prevWordStart = q;
            runLength += written;
            p = wordEnd;
            search = wordEnd;
        }
    }

    private static bool CharsetIsUtf8(ReadOnlySpan<byte> charset) =>
        System.Text.Ascii.EqualsIgnoreCase(charset, "utf-8"u8) || System.Text.Ascii.EqualsIgnoreCase(charset, "utf8"u8);

    private static bool CharsetEquals(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => System.Text.Ascii.EqualsIgnoreCase(a, b);

    private static bool IsAllWhitespace(ReadOnlySpan<byte> s)
    {
        foreach (byte b in s)
        {
            if (b is not (0x20 or 0x09 or 0x0D or 0x0A))
            {
                return false;
            }
        }

        return true;
    }

    // Parses "=?charset[*language]?B|Q?text?=" at value[at..].
    private static WordStatus ParseWord(ReadOnlySpan<byte> value, int at, out int end, out int charsetStart, out int charsetLength, out bool isB, out int textStart, out int textLength, out bool spaces)
    {
        spaces = false;
        end = 0;
        charsetStart = at + 2;
        charsetLength = 0;
        isB = false;
        textStart = 0;
        textLength = 0;
        int i = charsetStart;
        while (i < value.Length && value[i] != (byte)'?' && value[i] != (byte)'*')
        {
            byte c = value[i];
            if (c <= 0x20 || c >= 0x7F || i - charsetStart > MaxCharsetLength)
            {
                return WordStatus.NotAWord;
            }

            i++;
        }

        charsetLength = i - charsetStart;
        if (charsetLength == 0 || i >= value.Length)
        {
            return WordStatus.NotAWord;
        }

        if (value[i] == (byte)'*')
        {
            // RFC 2231 §5: "charset*language" - the language is ignored.
            while (i < value.Length && value[i] != (byte)'?')
            {
                if (value[i] <= 0x20 || value[i] >= 0x7F)
                {
                    return WordStatus.NotAWord;
                }

                i++;
            }
        }

        if (i + 2 >= value.Length || value[i] != (byte)'?' || value[i + 2] != (byte)'?')
        {
            return WordStatus.NotAWord;
        }

        byte e = (byte)(value[i + 1] | 0x20);
        if (e != (byte)'b' && e != (byte)'q')
        {
            return WordStatus.Malformed;
        }

        isB = e == (byte)'b';
        textStart = i + 3;
        int k = textStart;
        while (k < value.Length)
        {
            byte c = value[k];
            if (c == (byte)'?' && k + 1 < value.Length && value[k + 1] == (byte)'=')
            {
                textLength = k - textStart;
                end = k + 2;
                return WordStatus.Word;
            }

            if (c is 0x20 or 0x09)
            {
                // RFC 2047 §2 forbids white space inside an encoded word, but Python, Go and MimeKit all decode it, so we do too.
                spaces = true;
            }
            else if (c is 0x0D or 0x0A)
            {
                return WordStatus.Malformed;
            }

            k++;
        }

        return WordStatus.Malformed;
    }

    private static int DecodeBase64(ReadOnlySpan<byte> text, Span<byte> destination, List<TextIssue>? issues, int wordOffset)
    {
        int w = 0;
        uint bits = 0;
        int nbits = 0;
        bool bad = false;
        int chars = 0;
        bool paddingSeen = false;
        foreach (byte b in text)
        {
            if (b is 0x20 or 0x09)
            {
                continue;
            }

            if (b == (byte)'=')
            {
                paddingSeen = true;
                continue;
            }

            int v = Base64Alphabet.Value(b);
            if (v < 0 || paddingSeen)
            {
                // Characters outside the alphabet, and data after the padding, are skipped.
                bad = true;
                continue;
            }

            chars++;
            bits = (bits << 6) | (uint)v;
            nbits += 6;
            if (nbits >= 8)
            {
                nbits -= 8;
                if (w < destination.Length)
                {
                    destination[w++] = (byte)(bits >> nbits);
                }

                bits &= (1u << nbits) - 1;
            }
        }

        // A group has 4 characters; 2 or 3 leave a correct tail only with the '=' padding RFC 2045 §6.8 requires. A single
        // trailing character carries no whole byte and is dropped.
        int tail = chars % 4;
        int padding = 0;
        foreach (byte b in text)
        {
            if (b == (byte)'=')
            {
                padding++;
            }
        }

        if (bad || tail == 1 || (tail != 0 && padding != 4 - tail) || (tail == 0 && padding != 0))
        {
            issues?.Add(new TextIssue(TextIssueKind.InvalidBase64, wordOffset, text.Length));
        }

        return w;
    }

    private static int DecodeQ(ReadOnlySpan<byte> text, Span<byte> destination, List<TextIssue>? issues, int wordOffset)
    {
        int w = 0;
        bool bad = false;
        for (int i = 0; i < text.Length && w < destination.Length; i++)
        {
            byte b = text[i];
            if (b == (byte)'_')
            {
                // RFC 2047 §4.2(2): underscore is a space.
                destination[w++] = 0x20;
            }
            else if (b == (byte)'=')
            {
                int hi = i + 2 < text.Length ? HexValue(text[i + 1]) : -1;
                int lo = i + 2 < text.Length ? HexValue(text[i + 2]) : -1;
                if (hi >= 0 && lo >= 0)
                {
                    destination[w++] = (byte)((hi << 4) | lo);
                    i += 2;
                }
                else
                {
                    // A '=' without two hex digits stays a literal '='.
                    bad = true;
                    destination[w++] = b;
                }
            }
            else
            {
                destination[w++] = b;
            }
        }

        if (bad)
        {
            issues?.Add(new TextIssue(TextIssueKind.InvalidQuotedPrintable, wordOffset, text.Length));
        }

        return w;
    }

    internal static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        _ => -1,
    };

    private static void FlushRun(StringBuilder sb, byte[] run, int length, ReadOnlySpan<byte> value, int charsetStart, int charsetLength, int wordOffset, List<TextIssue>? issues, MailEncoding fallback)
    {
        if (length == 0)
        {
            return;
        }

        ReadOnlySpan<byte> bytes = run.AsSpan(0, length);
        // Bounded: at most MaxCharsetLength (64) characters, which ParseWord enforced.
        Span<char> label = stackalloc char[Math.Min(charsetLength, MaxCharsetLength)];
        for (int i = 0; i < label.Length; i++)
        {
            label[i] = (char)value[charsetStart + i];
        }

        MailEncoding? encoding = MailEncoding.ForLabel(label);
        List<TextIssue>? local = issues is null ? null : new List<TextIssue>();
        if (encoding is null)
        {
            issues?.Add(new TextIssue(TextIssueKind.AmbiguousUnknownCharset, wordOffset, charsetLength));
            encoding = Utf8.IsValid(bytes) ? MailEncoding.Utf8 : MailEncoding.Windows1252;
        }

        sb.Append(encoding.GetString(bytes, local));
        if (local is not null)
        {
            foreach (TextIssue t in local)
            {
                // Offsets inside the decoded run have no meaning in the header; point at the run's first word.
                issues!.Add(new TextIssue(t.Kind, wordOffset, 0));
            }
        }
    }

    private static void AppendPlain(StringBuilder sb, ReadOnlySpan<byte> plain, int offset, List<TextIssue>? issues, MailEncoding fallback)
    {
        if (plain.IsEmpty)
        {
            return;
        }

        // Short ASCII text without line breaks (the usual gap between words) goes straight into the builder.
        if (plain.Length <= 256 && System.Text.Ascii.IsValid(plain) && plain.IndexOfAny((byte)0x0D, (byte)0x0A) < 0)
        {
            // Fixed 512 bytes; the length was checked to be at most 256 above.
            Span<char> chars = stackalloc char[256];
            System.Text.Ascii.ToUtf16(plain, chars, out int written);
            sb.Append(chars[..written]);
            return;
        }

        sb.Append(DecodePlain(plain, offset, issues, fallback));
    }

    // Plain header text: unfold, then ASCII, UTF-8 (RFC 6532) or the fallback charset.
    private static string DecodePlain(ReadOnlySpan<byte> plain, int offset, List<TextIssue>? issues, MailEncoding fallback)
    {
        if (plain.IsEmpty)
        {
            return string.Empty;
        }

        string text;
        if (System.Text.Ascii.IsValid(plain))
        {
            text = Encoding.ASCII.GetString(plain);
        }
        else if (Utf8.IsValid(plain))
        {
            text = Encoding.UTF8.GetString(plain);
        }
        else
        {
            issues?.Add(new TextIssue(TextIssueKind.AmbiguousRaw8Bit, offset, plain.Length));
            text = fallback.GetString(plain, null);
        }

        return Unfold(text);
    }

    // RFC 5322 §2.2.3: remove the line break of a fold, keep the white space after it.
    internal static string Unfold(string text)
    {
        int first = text.AsSpan().IndexOfAny('\r', '\n');
        if (first < 0)
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '\r' or '\n')
            {
                int j = i;
                if (c == '\r' && j + 1 < text.Length && text[j + 1] == '\n')
                {
                    j++;
                }

                if (j + 1 < text.Length && text[j + 1] is ' ' or '\t')
                {
                    i = j;
                    continue;
                }
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    // --- encoding ------------------------------------------------------------------------------------------

    /// <summary>
    /// Encodes header text. Text that is printable ASCII stays as it is (unless it contains <c>=?</c>); each stretch that
    /// needs it becomes encoded words, with whole characters per word and at most 75 characters per word.
    /// </summary>
    /// <remarks>
    /// Q or B is chosen per stretch, whichever is shorter. Q uses only the characters safe in a phrase, comment or unstructured
    /// text, so the result is valid wherever it is written. Words are separated by a single space; folding the header there is legal.
    /// Adjacent stretches that need encoding are merged with the white space between them, because a decoder drops that white space.
    /// </remarks>
    /// <param name="text">The text.</param>
    /// <param name="charset">The charset to encode with; <see langword="null"/> means UTF-8.</param>
    /// <param name="issues">Receives <see cref="TextIssueKind.Unencodable"/> if the charset could not hold a character; may be <see langword="null"/>.</param>
    /// <returns>ASCII text.</returns>
    public static string Encode(string text, MailEncoding? charset = null, List<TextIssue>? issues = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        charset ??= MailEncoding.Utf8;
        if (!NeedsEncoding(text.AsSpan()))
        {
            return text;
        }

        var sb = new StringBuilder(text.Length * 2);
        int n = text.Length;
        int cursor = 0;
        int i = 0;
        while (true)
        {
            while (i < n && IsBlank(text[i]))
            {
                i++;
            }

            if (i >= n)
            {
                break;
            }

            int te = TokenEnd(text, i);
            if (!NeedsEncoding(text.AsSpan(i, te - i)))
            {
                i = te;
                continue;
            }

            // A decoder drops the white space between two encoded words, so neighbouring tokens that need encoding
            // go into one group together with the white space between them.
            int groupEnd = te;
            int j = te;
            while (true)
            {
                int k = j;
                while (k < n && IsBlank(text[k]))
                {
                    k++;
                }

                if (k >= n)
                {
                    break;
                }

                int e = TokenEnd(text, k);
                if (!NeedsEncoding(text.AsSpan(k, e - k)))
                {
                    break;
                }

                groupEnd = e;
                j = e;
            }

            sb.Append(text, cursor, i - cursor);
            AppendWords(sb, text.AsSpan(i, groupEnd - i), charset, issues);
            cursor = groupEnd;
            i = groupEnd;
        }

        sb.Append(text, cursor, n - cursor);
        return sb.ToString();
    }

    private static bool IsBlank(char c) => c is ' ' or '	';

    private static int TokenEnd(string text, int start)
    {
        int e = start;
        while (e < text.Length && !IsBlank(text[e]))
        {
            e++;
        }

        return e;
    }

    private static bool NeedsEncoding(ReadOnlySpan<char> s)
    {
        foreach (char c in s)
        {
            if (c is < ' ' or > '~')
            {
                return true;
            }
        }

        return s.IndexOf("=?") >= 0;
    }

    private static void AppendWords(StringBuilder sb, ReadOnlySpan<char> group, MailEncoding charset, List<TextIssue>? issues)
    {
        string name = charset.Name;
        // "=?" + charset + "?B?" ... "?=" is 7 characters of framing plus the charset name.
        int capacity = MaxWordLength - 7 - name.Length;
        bool first = true;
        int pos = 0;
        while (pos < group.Length)
        {
            // Take as many whole characters as fit in one word under the cheaper encoding.
            int take = FitChars(group[pos..], charset, capacity, out bool useB);
            string chunk = group.Slice(pos, take).ToString();
            byte[] bytes = charset.GetBytes(chunk, issues);
            if (!first)
            {
                sb.Append(' ');
            }

            first = false;
            sb.Append("=?").Append(name).Append('?').Append(useB ? 'B' : 'Q').Append('?');
            if (useB)
            {
                sb.Append(Convert.ToBase64String(bytes));
            }
            else
            {
                AppendQ(sb, bytes);
            }

            sb.Append("?=");
            pos += take;
        }
    }

    // How many chars (never splitting a surrogate pair) fit in 'capacity' encoded characters, and which encoding is shorter.
    private static int FitChars(ReadOnlySpan<char> rest, MailEncoding charset, int capacity, out bool useB)
    {
        // Estimate with UTF-8 sizes for stateless charsets; stateful ones (ISO-2022) re-check by encoding.
        int take = 0;
        int qCost = 0;
        int bytes = 0;
        while (take < rest.Length)
        {
            int len = char.IsHighSurrogate(rest[take]) && take + 1 < rest.Length ? 2 : 1;
            byte[] enc = charset.GetBytes(rest.Slice(take, len).ToString(), null);
            int q = 0;
            foreach (byte b in enc)
            {
                q += IsQSafe(b) ? 1 : 3;
            }

            int bAfter = (bytes + enc.Length + 2) / 3 * 4;
            if (Math.Min(qCost + q, bAfter) > capacity && take > 0)
            {
                break;
            }

            qCost += q;
            bytes += enc.Length;
            take += len;
        }

        useB = (bytes + 2) / 3 * 4 < qCost;
        return take;
    }

    private static bool IsQSafe(byte b) => b is >= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or (byte)'!' or (byte)'*' or (byte)'+' or (byte)'-' or (byte)'/' or (byte)' ';

    private static void AppendQ(StringBuilder sb, byte[] bytes)
    {
        const string Hex = "0123456789ABCDEF";
        foreach (byte b in bytes)
        {
            if (b == (byte)' ')
            {
                sb.Append('_');
            }
            else if (IsQSafe(b))
            {
                sb.Append((char)b);
            }
            else
            {
                sb.Append('=').Append(Hex[b >> 4]).Append(Hex[b & 15]);
            }
        }
    }
}
