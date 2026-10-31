using System.Text;

namespace RecursiveAI.OneMail.Rtf;

/// <summary>What <see cref="RtfDeencapsulator.Detect"/> found in an RTF document's first tokens.</summary>
public enum RtfEncapsulation
{
    /// <summary>An ordinary RTF document: nothing is encapsulated.</summary>
    None = 0,

    /// <summary>The document carries its original HTML (<c>\fromhtml1</c>).</summary>
    Html,

    /// <summary>The document was made from plain text (<c>\fromtext</c>).</summary>
    PlainText,
}

/// <summary>Settings for <see cref="RtfDeencapsulator"/>.</summary>
public sealed class RtfDeencapsulationOptions
{
    /// <summary>
    /// Supplies the decoder for a Windows code page number (1252, 932, 10000…). Return <see langword="null"/> when the
    /// code page is unknown; the document's default code page is then used and
    /// <see cref="RtfDefectKind.AmbiguousUnknownCodePage"/> is reported. <see langword="null"/> (the default) uses the
    /// platform's built-in encodings plus the .NET code-page provider, without registering anything globally.
    /// </summary>
    public Func<int, Encoding?>? CodePageDecoder { get; init; }

    /// <summary>The deepest group nesting accepted; see <see cref="RtfReaderOptions.MaxGroupDepth"/>. Default 1000.</summary>
    public int MaxGroupDepth { get; init; } = 1000;
}

/// <summary>The outcome of <see cref="RtfDeencapsulator.Deencapsulate"/>.</summary>
public sealed class RtfDeencapsulationResult
{
    internal RtfDeencapsulationResult(RtfEncapsulation kind, string text, int defaultCodePage, IReadOnlyList<RtfDefect> defects)
    {
        Kind = kind;
        Text = text;
        DefaultCodePage = defaultCodePage;
        Defects = defects;
    }

    /// <summary>What the document carried; <see cref="RtfEncapsulation.None"/> means <see cref="Text"/> is empty.</summary>
    public RtfEncapsulation Kind { get; }

    /// <summary>The original HTML or plain text, as a .NET string (UTF-16): all bytes are already decoded.</summary>
    public string Text { get; }

    /// <summary>The Windows code page the document declared (<c>\ansicpgN</c>), or the one its <c>\ansi</c>/<c>\mac</c>/<c>\pc</c>/<c>\pca</c> keyword implies; 1252 if neither.</summary>
    public int DefaultCodePage { get; }

    /// <summary>Everything repaired or guessed while reading.</summary>
    public IReadOnlyList<RtfDefect> Defects { get; }
}

/// <summary>
/// MS-OXRTFEX de-encapsulation: recovers the HTML or plain text that Outlook wraps inside an RTF body
/// (<c>PidTagRtfCompressed</c> after <see cref="Compression.RtfDecompressor"/>). Contains no dependency on the rest of OneMail.
/// </summary>
public static class RtfDeencapsulator
{
    private const int MaxInspectedTokens = 10;

    /// <summary>
    /// Decides whether an RTF document encapsulates HTML or text (MS-OXRTFEX 2.2.3.1): the document must begin
    /// <c>{\rtf</c>, and <c>\fromhtml1</c> or <c>\fromtext</c> must appear among its first ten tokens, all of which
    /// must be group starts or control words.
    /// </summary>
    /// <param name="rtf">The RTF bytes (decompressed).</param>
    /// <returns>The kind; <see cref="RtfEncapsulation.None"/> for anything else. Never throws.</returns>
    public static RtfEncapsulation Detect(ReadOnlySpan<byte> rtf)
    {
        var reader = new RtfReader(rtf, new RtfReaderOptions { MaxGroupDepth = 16 });
        int tokens = 0;
        bool sawRtf = false;
        while (tokens < MaxInspectedTokens && reader.Read())
        {
            tokens++;
            if (tokens == 1)
            {
                if (reader.Kind != RtfTokenKind.GroupStart)
                {
                    return RtfEncapsulation.None;
                }

                continue;
            }

            if (reader.Kind == RtfTokenKind.GroupStart)
            {
                continue;
            }

            if (reader.Kind != RtfTokenKind.ControlWord)
            {
                return RtfEncapsulation.None;
            }

            if (!sawRtf)
            {
                if (!reader.Name.SequenceEqual("rtf"u8))
                {
                    return RtfEncapsulation.None;
                }

                sawRtf = true;
            }
            else if (reader.Name.SequenceEqual("fromhtml"u8) && reader.HasParameter && reader.Parameter == 1)
            {
                // MS-OXRTFEX 2.1.3.1.2: only \fromhtml1 counts; \fromhtml and \fromhtml0 do not.
                return RtfEncapsulation.Html;
            }
            else if (reader.Name.SequenceEqual("fromtext"u8))
            {
                return RtfEncapsulation.PlainText;
            }
        }

        return RtfEncapsulation.None;
    }

    /// <summary>
    /// Extracts the original HTML (<c>\fromhtml1</c>) or plain text (<c>\fromtext</c>) from an RTF document, following
    /// MS-OXRTFEX 2.2.3.2 and 2.2.3.3. See <c>docs/rtf.md</c> for the rules this implements and each ambiguity's resolution.
    /// </summary>
    /// <param name="rtf">The RTF bytes (decompressed).</param>
    /// <param name="options">Code-page hook and limits; <see langword="null"/> for the defaults.</param>
    /// <returns>
    /// The result. For a document without encapsulation, <see cref="RtfDeencapsulationResult.Kind"/> is
    /// <see cref="RtfEncapsulation.None"/>, the text is empty and a defect says why. Never throws for malformed input.
    /// </returns>
    public static RtfDeencapsulationResult Deencapsulate(ReadOnlySpan<byte> rtf, RtfDeencapsulationOptions? options = null)
    {
        options ??= new RtfDeencapsulationOptions();
        RtfEncapsulation kind = Detect(rtf);
        if (kind == RtfEncapsulation.None)
        {
            var d = new RtfDefectList();
            ReadOnlySpan<byte> head = rtf.TrimStart((byte)'\r').TrimStart((byte)'\n');
            if (!head.StartsWith("{\\rtf"u8))
            {
                d.Add(RtfDefectKind.NotRtf, 0, "the input does not start with {\\rtf");
            }
            else
            {
                d.Add(RtfDefectKind.NotEncapsulated, 0, "no \\fromhtml1 or \\fromtext among the first tokens");
            }

            return new RtfDeencapsulationResult(RtfEncapsulation.None, string.Empty, 1252, d.Items);
        }

        return new Worker(kind, options).Run(rtf);
    }

    private enum Mode : byte
    {
        Normal,
        Skip,
        HtmlTag,
        FontTable,
    }

    private enum Word : byte
    {
        Unknown,
        Par,
        Tab,
        Char, // a word that is one fixed character; see CharOf
        HtmlRtf,
        F,
        Uc,
        U,
        AnsiCpg,
        Ansi,
        Mac,
        Pc,
        Pca,
        Deff,
        FontTbl,
        FCharset,
        Cpg,
        HtmlTag,
        SkipDestination,
    }

    private static readonly Dictionary<string, (Word Word, char Ch)> Words = BuildWords();

    private static Dictionary<string, (Word, char)> BuildWords()
    {
        var w = new Dictionary<string, (Word, char)>(StringComparer.Ordinal)
        {
            ["par"] = (Word.Par, '\0'),
            ["line"] = (Word.Par, '\0'), // MS-OXRTFEX 2.2.3.2: \par and \line are both CRLF
            ["tab"] = (Word.Tab, '\0'),
            ["lquote"] = (Word.Char, '\u2018'),
            ["rquote"] = (Word.Char, '\u2019'),
            ["ldblquote"] = (Word.Char, '\u201C'),
            ["rdblquote"] = (Word.Char, '\u201D'),
            ["bullet"] = (Word.Char, '\u2022'),
            ["endash"] = (Word.Char, '\u2013'),
            ["emdash"] = (Word.Char, '\u2014'),
            ["enspace"] = (Word.Char, '\u2002'),
            ["emspace"] = (Word.Char, '\u2003'),
            ["qmspace"] = (Word.Char, '\u2005'),
            ["zwj"] = (Word.Char, '\u200D'),
            ["zwnj"] = (Word.Char, '\u200C'),
            ["ltrmark"] = (Word.Char, '\u200E'),
            ["rtlmark"] = (Word.Char, '\u200F'),
            ["htmlrtf"] = (Word.HtmlRtf, '\0'),
            ["f"] = (Word.F, '\0'),
            ["uc"] = (Word.Uc, '\0'),
            ["u"] = (Word.U, '\0'),
            ["ansicpg"] = (Word.AnsiCpg, '\0'),
            ["ansi"] = (Word.Ansi, '\0'),
            ["mac"] = (Word.Mac, '\0'),
            ["pc"] = (Word.Pc, '\0'),
            ["pca"] = (Word.Pca, '\0'),
            ["deff"] = (Word.Deff, '\0'),
            ["fonttbl"] = (Word.FontTbl, '\0'),
            ["fcharset"] = (Word.FCharset, '\0'),
            ["cpg"] = (Word.Cpg, '\0'),
            ["htmltag"] = (Word.HtmlTag, '\0'),
        };

        // Destinations that hold no body text (MS-OXRTFEX 2.2.3.2: "standard RTF destination groups that do not produce
        // visible text"), plus the generated list-number and field-instruction groups.
        foreach (string name in new[]
        {
            "colortbl", "stylesheet", "info", "pict", "object", "header", "headerl", "headerr", "headerf", "footer", "footerl",
            "footerr", "footerf", "footnote", "listtable", "listoverridetable", "rsidtbl", "generator", "filetbl", "revtbl",
            "themedata", "colorschememapping", "datastore", "latentstyles", "xmlnstbl", "fldinst", "pntext", "pn", "pnseclvl",
            "listtext", "bkmkstart", "bkmkend", "shpinst", "nonshppict", "mhtmltag", "private", "wgrffmtfilter", "template",
            "docvar", "defchp", "defpap", "background", "falt", "panose", "fname", "fontemb", "fontfile", "xe", "tc", "txe",
        })
        {
            w[name] = (Word.SkipDestination, '\0');
        }

        return w;
    }

    private struct State
    {
        public Mode Mode;
        public bool HtmlRtf;
        public int Uc;
        public int Skip; // characters still to drop after a \uN
        public int Font;
    }

    private sealed class Worker
    {
        private readonly RtfEncapsulation _kind;
        private readonly RtfDeencapsulationOptions _options;
        private readonly RtfDefectList _defects = new();
        private readonly StringBuilder _sb = new();
        private readonly List<State> _stack = [];
        private readonly Dictionary<int, int> _fontCodePage = [];
        private readonly Dictionary<int, Encoding?> _encodings = [];
        private readonly List<byte> _pending = [];
        private readonly char[] _nameBuffer = new char[32]; // control word names are ASCII and at most 32 long
        private int _pendingCodePage;
        private int _explicitCodePage; // \ansicpgN; 0 = none
        private int _impliedCodePage; // from \ansi, \mac, \pc, \pca; 0 = none
        private int _defaultFont = -1;
        private bool _reportedHtmlRtf;

        public Worker(RtfEncapsulation kind, RtfDeencapsulationOptions options)
        {
            _kind = kind;
            _options = options;
        }

        private int DefaultCodePage => _explicitCodePage != 0 ? _explicitCodePage : _impliedCodePage != 0 ? _impliedCodePage : 1252;

        public RtfDeencapsulationResult Run(ReadOnlySpan<byte> rtf)
        {
            var reader = new RtfReader(rtf, new RtfReaderOptions { MaxGroupDepth = _options.MaxGroupDepth });
            State cur = new() { Mode = Mode.Normal, Uc = 1, Font = -1 };
            bool star = false;
            while (reader.Read())
            {
                switch (reader.Kind)
                {
                    case RtfTokenKind.GroupStart:
                        _stack.Add(cur);
                        cur.Skip = 0; // a pending \uN fallback never reaches across a group boundary
                        star = false;
                        break;
                    case RtfTokenKind.GroupEnd:
                        Flush();
                        if (_stack.Count > 0)
                        {
                            cur = _stack[^1];
                            _stack.RemoveAt(_stack.Count - 1);
                        }

                        star = false;
                        break;
                    case RtfTokenKind.ControlSymbol:
                        if (reader.Name[0] == (byte)'*')
                        {
                            star = true;
                            break;
                        }

                        star = false;
                        ControlSymbol(reader.Name[0], ref cur);
                        break;
                    case RtfTokenKind.ControlWord:
                        ControlWord(ref reader, ref cur, star);
                        star = false;
                        break;
                    case RtfTokenKind.HexEscape:
                        star = false;
                        if (reader.Depth == 0)
                        {
                            break;
                        }

                        if (cur.Skip > 0)
                        {
                            cur.Skip--;
                        }
                        else if (Emitting(cur))
                        {
                            AddByte(reader.HexValue, cur);
                        }

                        break;
                    case RtfTokenKind.Text:
                        star = false;
                        if (reader.Depth == 0)
                        {
                            break; // bytes after the outermost group (padding) are not part of the document
                        }

                        ReadOnlySpan<byte> text = reader.Text;
                        if (cur.Skip > 0)
                        {
                            int drop = Math.Min(cur.Skip, text.Length);
                            cur.Skip -= drop;
                            text = text[drop..];
                        }

                        if (Emitting(cur))
                        {
                            foreach (byte b in text)
                            {
                                AddByte(b, cur);
                            }
                        }

                        break;
                    default:
                        star = false;
                        break;
                }
            }

            Flush();
            foreach (RtfDefect d in reader.Defects)
            {
                _defects.Add(d.Kind, d.Offset, d.Message);
            }

            string text2 = RepairSurrogates(_sb.ToString());
            return new RtfDeencapsulationResult(_kind, text2, DefaultCodePage, _defects.Items);
        }

        // MS-OXRTFEX 2.2.3.2: text inside \htmltag is always copied; elsewhere \htmlrtf suppresses it (HTML only).
        private bool Emitting(State s) =>
            s.Mode == Mode.HtmlTag || (s.Mode == Mode.Normal && !(s.HtmlRtf && _kind == RtfEncapsulation.Html));

        private void ControlSymbol(byte symbol, ref State cur)
        {
            if (cur.Mode is Mode.Skip or Mode.FontTable || !Emitting(cur))
            {
                return;
            }

            switch (symbol)
            {
                case (byte)'{':
                case (byte)'}':
                case (byte)'\\':
                    Append(((char)symbol).ToString());
                    break;
                case (byte)'~':
                    Append("\u00A0"); // non-breaking space
                    break;
                case (byte)'_':
                    // MS-OXRTFEX 2.1.3.1.4.2 maps \_ to a soft hyphen inside \htmltag; in the RTF specification it is the non-breaking hyphen.
                    Append(cur.Mode == Mode.HtmlTag ? "\u00AD" : "\u2011");
                    break;
                case (byte)'\r':
                case (byte)'\n':
                    Append("\r\n"); // a backslash before a line break is a \par
                    break;
            }
        }

        private void ControlWord(ref RtfReader reader, ref State cur, bool star)
        {
            Word word = Lookup(reader.Name, out char ch);
            int p = reader.Parameter;
            bool hasP = reader.HasParameter;

            if (cur.Mode == Mode.Skip)
            {
                return;
            }

            if (star)
            {
                // MS-OXRTFEX 2.2.3.2: of the ignorable destinations only \htmltag is kept (and only in HTML documents).
                if (word == Word.HtmlTag && _kind == RtfEncapsulation.Html && cur.Mode == Mode.Normal)
                {
                    Flush();
                    cur.Mode = Mode.HtmlTag;
                }
                else
                {
                    Flush();
                    cur.Mode = Mode.Skip;
                }

                return;
            }

            if (cur.Mode == Mode.FontTable)
            {
                switch (word)
                {
                    case Word.F:
                        cur.Font = p;
                        if (!_fontCodePage.ContainsKey(p))
                        {
                            _fontCodePage[p] = 0;
                        }

                        break;
                    case Word.FCharset:
                        if (cur.Font >= 0 && hasP && (!_fontCodePage.TryGetValue(cur.Font, out int existing) || existing == 0))
                        {
                            _fontCodePage[cur.Font] = CharsetToCodePage(p);
                        }

                        break;
                    case Word.Cpg:
                        if (cur.Font >= 0 && hasP)
                        {
                            _fontCodePage[cur.Font] = p; // \cpgN names the code page outright and wins over \fcharset
                        }

                        break;
                    case Word.SkipDestination:
                        cur.Mode = Mode.Skip;
                        break;
                }

                return;
            }

            switch (word)
            {
                case Word.SkipDestination:
                    if (cur.Mode == Mode.Normal)
                    {
                        Flush();
                        cur.Mode = Mode.Skip;
                    }

                    return;
                case Word.FontTbl:
                    if (cur.Mode == Mode.Normal)
                    {
                        Flush();
                        cur.Mode = Mode.FontTable;
                    }

                    return;
                case Word.HtmlRtf:
                    if (_kind != RtfEncapsulation.Html)
                    {
                        if (!_reportedHtmlRtf)
                        {
                            _reportedHtmlRtf = true;
                            _defects.Add(RtfDefectKind.AmbiguousHtmlRtfInPlainText, reader.Offset, "\\htmlrtf in a document without \\fromhtml1 was ignored");
                        }
                    }
                    else if (cur.Mode == Mode.Normal)
                    {
                        Flush();
                        cur.HtmlRtf = !hasP || p != 0; // \htmlrtf and \htmlrtf1 enable it, \htmlrtf0 disables it
                    }

                    return;
                case Word.F:
                    if (hasP && cur.Mode == Mode.Normal)
                    {
                        // The current font is tracked even inside suppressed text (MS-OXRTFEX 2.2.3.2).
                        cur.Font = p;
                    }

                    return;
                case Word.Uc:
                    cur.Uc = hasP ? Math.Max(p, 0) : 1;
                    return;
                case Word.U:
                    if (hasP)
                    {
                        // RTF 1.9.1: \uN gives a UTF-16 unit as a signed 16-bit number; the next \ucN characters are its fallback.
                        if (Emitting(cur))
                        {
                            Flush();
                            _sb.Append((char)(ushort)p);
                        }

                        cur.Skip = cur.Uc;
                    }

                    return;
                case Word.AnsiCpg:
                    if (hasP && p > 0)
                    {
                        _explicitCodePage = p;
                    }

                    return;
                case Word.Ansi:
                    _impliedCodePage = 1252;
                    return;
                case Word.Mac:
                    _impliedCodePage = 10000;
                    return;
                case Word.Pc:
                    _impliedCodePage = 437;
                    return;
                case Word.Pca:
                    _impliedCodePage = 850;
                    return;
                case Word.Deff:
                    if (hasP)
                    {
                        _defaultFont = p;
                    }

                    return;
            }

            if (!Emitting(cur))
            {
                return;
            }

            switch (word)
            {
                case Word.Par:
                    Append("\r\n");
                    break;
                case Word.Tab:
                    Append("\t");
                    break;
                case Word.Char:
                    Append(ch.ToString());
                    break;
            }
        }

        private Word Lookup(ReadOnlySpan<byte> name, out char ch)
        {
            ch = '\0';
            if (name.Length > 32)
            {
                return Word.Unknown;
            }

            char[] chars = _nameBuffer;
            for (int i = 0; i < name.Length; i++)
            {
                chars[i] = (char)name[i];
            }

            if (Words.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(chars.AsSpan(0, name.Length), out (Word Word, char Ch) found))
            {
                ch = found.Ch;
                return found.Word;
            }

            return Word.Unknown;
        }

        // RTF 1.9.1 \fcharset values and the Windows code page of each. 1 (default) and unknown values mean "the document's".
        private static int CharsetToCodePage(int charset) => charset switch
        {
            0 => 1252,
            77 => 10000,
            128 => 932,
            129 => 949,
            130 => 1361,
            134 => 936,
            136 => 950,
            161 => 1253,
            162 => 1254,
            163 => 1258,
            177 => 1255,
            178 => 1256,
            186 => 1257,
            204 => 1251,
            222 => 874,
            238 => 1250,
            254 or 255 => 437,
            _ => 0,
        };

        // The code page for bytes in this state: \htmltag content uses the document default (MS-OXRTFEX 2.2.3.2);
        // other text uses the current font's, or the default font's when no \f was seen.
        private int CodePageFor(State s)
        {
            if (s.Mode == Mode.HtmlTag)
            {
                return DefaultCodePage;
            }

            int font = s.Font >= 0 ? s.Font : _defaultFont;
            return font >= 0 && _fontCodePage.TryGetValue(font, out int cp) && cp != 0 ? cp : DefaultCodePage;
        }

        private void AddByte(byte b, State s)
        {
            int cp = CodePageFor(s);
            if (_pending.Count > 0 && cp != _pendingCodePage)
            {
                Flush();
            }

            _pendingCodePage = cp;
            _pending.Add(b);
        }

        private void Append(string s)
        {
            Flush();
            _sb.Append(s);
        }

        private void Flush()
        {
            if (_pending.Count == 0)
            {
                return;
            }

            Encoding? enc = GetEncoding(_pendingCodePage);
            if (enc is null)
            {
                _defects.Add(RtfDefectKind.AmbiguousUnknownCodePage, 0, "no decoder for code page " + _pendingCodePage + "; the default was used");
                enc = GetEncoding(DefaultCodePage) ?? Encoding.Latin1;
            }

            byte[] bytes = [.. _pending];
            _pending.Clear();
            _sb.Append(enc.GetString(bytes));
        }

        private Encoding? GetEncoding(int codePage)
        {
            if (_encodings.TryGetValue(codePage, out Encoding? known))
            {
                return known;
            }

            Encoding? enc = _options.CodePageDecoder is { } hook ? hook(codePage) : DefaultDecoder(codePage);
            _encodings[codePage] = enc;
            return enc;
        }

        private static Encoding? DefaultDecoder(int codePage)
        {
            var decoderFallback = new DecoderReplacementFallback("\uFFFD");
            try
            {
                switch (codePage)
                {
                    case 65001:
                    case 1200:
                    case 1201:
                    case 12000:
                    case 12001:
                    case 20127:
                    case 28591:
                        return Encoding.GetEncoding(codePage, EncoderFallback.ReplacementFallback, decoderFallback);
                    default:
                        return CodePagesEncodingProvider.Instance.GetEncoding(codePage, EncoderFallback.ReplacementFallback, decoderFallback);
                }
            }
            catch (ArgumentException)
            {
                return null; // no such code page on this platform
            }
        }

        // A \uN escape can leave half a surrogate pair; a .NET string should not carry one.
        private string RepairSurrogates(string s)
        {
            StringBuilder? fixedUp = null;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool bad = false;
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                    {
                        fixedUp?.Append(c).Append(s[i + 1]);
                        i++;
                        continue;
                    }

                    bad = true;
                }
                else if (char.IsLowSurrogate(c))
                {
                    bad = true;
                }

                if (bad)
                {
                    if (fixedUp is null)
                    {
                        fixedUp = new StringBuilder(s.Length).Append(s, 0, i);
                        _defects.Add(RtfDefectKind.LoneSurrogate, i, "unpaired surrogate from a \\u escape replaced by U+FFFD");
                    }

                    fixedUp.Append('\uFFFD');
                }
                else
                {
                    fixedUp?.Append(c);
                }
            }

            return fixedUp?.ToString() ?? s;
        }
    }
}
