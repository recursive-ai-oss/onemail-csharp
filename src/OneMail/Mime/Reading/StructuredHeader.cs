using RecursiveAI.OneMail.Mime.Transfer;

namespace RecursiveAI.OneMail.Mime.Reading;

/// <summary>
/// Scanning of structured header values (RFC 2045 §5 and §6.1, RFC 5322 §3.2.2): media type, parameters, transfer
/// encoding. Values may contain folded lines and comments; both are skipped. RFC 2231 parameter extensions are not
/// interpreted here (the text layer does that); the raw parameter names are returned as written.
/// </summary>
internal static class StructuredHeader
{
    private static bool IsWsp(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    /// <summary>Skips whitespace, folds and parenthesised comments (RFC 5322 §3.2.2), returning the next index.</summary>
    public static int SkipCfws(ReadOnlySpan<byte> v, int i)
    {
        while (i < v.Length)
        {
            if (IsWsp(v[i]))
            {
                i++;
            }
            else if (v[i] == (byte)'(')
            {
                int depth = 0;
                while (i < v.Length)
                {
                    byte c = v[i++];
                    if (c == (byte)'\\' && i < v.Length)
                    {
                        i++;
                    }
                    else if (c == (byte)'(')
                    {
                        depth++;
                    }
                    else if (c == (byte)')' && --depth == 0)
                    {
                        break;
                    }
                }
            }
            else
            {
                break;
            }
        }

        return i;
    }

    private static bool IsTokenEnd(byte b) => IsWsp(b) || b is (byte)';' or (byte)'(' or (byte)'=' or (byte)'"';

    /// <summary>Parses <c>type "/" subtype</c> at the start of a Content-Type value.</summary>
    /// <returns>False when the value is not a media type; <paramref name="next"/> is the index after it.</returns>
    public static bool TryParseMediaType(ReadOnlySpan<byte> v, out ReadOnlySpan<byte> type, out ReadOnlySpan<byte> subtype, out int next)
    {
        type = default;
        subtype = default;
        int i = SkipCfws(v, 0);
        int ts = i;
        while (i < v.Length && v[i] != (byte)'/' && !IsTokenEnd(v[i]))
        {
            i++;
        }

        int te = i;
        i = SkipCfws(v, i);
        next = i;
        if (te == ts || i >= v.Length || v[i] != (byte)'/')
        {
            return false;
        }

        i = SkipCfws(v, i + 1);
        int ss = i;
        while (i < v.Length && !IsTokenEnd(v[i]) && v[i] != (byte)'/')
        {
            i++;
        }

        if (i == ss)
        {
            return false;
        }

        type = v[ts..te];
        subtype = v[ss..i];
        next = i;
        return true;
    }

    /// <summary>Parses a Content-Transfer-Encoding value (a token, comments allowed).</summary>
    public static ContentTransferEncoding ParseTransferEncoding(ReadOnlySpan<byte> v)
    {
        int i = SkipCfws(v, 0);
        int s = i;
        while (i < v.Length && !IsTokenEnd(v[i]))
        {
            i++;
        }

        ReadOnlySpan<byte> t = v[s..i];
        if (EqualsIgnoreCase(t, "7bit"u8))
        {
            return ContentTransferEncoding.SevenBit;
        }

        if (EqualsIgnoreCase(t, "8bit"u8))
        {
            return ContentTransferEncoding.EightBit;
        }

        if (EqualsIgnoreCase(t, "binary"u8))
        {
            return ContentTransferEncoding.Binary;
        }

        if (EqualsIgnoreCase(t, "base64"u8))
        {
            return ContentTransferEncoding.Base64;
        }

        if (EqualsIgnoreCase(t, "quoted-printable"u8))
        {
            return ContentTransferEncoding.QuotedPrintable;
        }

        return ContentTransferEncoding.Unknown;
    }

    /// <summary>ASCII case-insensitive equality; <paramref name="lowerAscii"/> must already be lower case.</summary>
    public static bool EqualsIgnoreCase(ReadOnlySpan<byte> a, ReadOnlySpan<byte> lowerAscii)
    {
        if (a.Length != lowerAscii.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            byte c = a[i];
            if (c is >= (byte)'A' and <= (byte)'Z')
            {
                c = (byte)(c + 32);
            }

            if (c != lowerAscii[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Copies a quoted-string body into <paramref name="dest"/>, resolving backslash escapes and dropping fold line
    /// breaks (RFC 5322 §3.2.4 and §2.2.3).
    /// </summary>
    /// <returns>The number of bytes written; at most <c>inner.Length</c>.</returns>
    public static int Unquote(ReadOnlySpan<byte> inner, Span<byte> dest)
    {
        int n = 0;
        for (int i = 0; i < inner.Length; i++)
        {
            byte c = inner[i];
            if (c == (byte)'\\' && i + 1 < inner.Length)
            {
                c = inner[++i];
            }
            else if (c is (byte)'\r' or (byte)'\n')
            {
                continue;
            }

            dest[n++] = c;
        }

        return n;
    }

    /// <summary>Enumerates the <c>; name=value</c> parameters after the media type.</summary>
    public ref struct ParameterEnumerator
    {
        private readonly ReadOnlySpan<byte> _v;
        private int _i;

        /// <summary>Starts at <paramref name="start"/> in the header value.</summary>
        public ParameterEnumerator(ReadOnlySpan<byte> value, int start)
        {
            _v = value;
            _i = start;
            Name = default;
            Value = default;
            Quoted = false;
            Unterminated = false;
            NameStart = 0;
            FirstInvalid = -1;
        }

        /// <summary>Whether the current value was a quoted string with no closing quote (it then runs to the end of the value).</summary>
        public bool Unterminated { get; private set; }

        /// <summary>Index in the header value where the current parameter name starts.</summary>
        public int NameStart { get; private set; }

        /// <summary>Index of the first item that was skipped because it had no <c>=</c> (a parameter without a value), or -1.</summary>
        public int FirstInvalid { get; private set; }

        /// <summary>The parameter name as written.</summary>
        public ReadOnlySpan<byte> Name { get; private set; }

        /// <summary>The value; for quoted strings the text between the quotes, still escaped.</summary>
        public ReadOnlySpan<byte> Value { get; private set; }

        /// <summary>Whether the value was a quoted string.</summary>
        public bool Quoted { get; private set; }

        /// <summary>Moves to the next parameter. Parameters without <c>=</c> are skipped.</summary>
        public bool MoveNext()
        {
            ReadOnlySpan<byte> v = _v;
            while (true)
            {
                _i = SkipCfws(v, _i);
                while (_i < v.Length && v[_i] == (byte)';')
                {
                    _i = SkipCfws(v, _i + 1);
                }

                if (_i >= v.Length)
                {
                    return false;
                }

                int ns = _i;
                while (_i < v.Length && !IsTokenEnd(v[_i]))
                {
                    _i++;
                }

                int ne = _i;
                _i = SkipCfws(v, _i);
                if (_i >= v.Length || v[_i] != (byte)'=')
                {
                    if (FirstInvalid < 0)
                    {
                        FirstInvalid = ns;
                    }

                    if (ne == ns)
                    {
                        _i++; // a stray character; step over it so the scan always advances
                    }

                    continue;
                }

                _i = SkipCfws(v, _i + 1);
                Name = v[ns..ne];
                NameStart = ns;
                Unterminated = false;
                if (_i < v.Length && v[_i] == (byte)'"')
                {
                    int vs = ++_i;
                    while (_i < v.Length && v[_i] != (byte)'"')
                    {
                        _i += v[_i] == (byte)'\\' ? 2 : 1;
                    }

                    int ve = Math.Min(_i, v.Length);
                    Unterminated = _i >= v.Length;
                    Value = v[vs..ve];
                    Quoted = true;
                    _i = Math.Min(_i + 1, v.Length);
                }
                else
                {
                    // An unquoted value runs to the next semicolon, so "a b" and "a/b=c" stay whole; a trailing comment is not part of it.
                    int vs = _i;
                    while (_i < v.Length && v[_i] != (byte)';')
                    {
                        _i++;
                    }

                    int ve = _i;
                    while (ve > vs && IsWsp(v[ve - 1]))
                    {
                        ve--;
                    }

                    while (ve > vs && v[ve - 1] == (byte)')')
                    {
                        int open = v[vs..ve].LastIndexOf((byte)'(');
                        if (open < 0)
                        {
                            break;
                        }

                        ve = vs + open;
                        while (ve > vs && IsWsp(v[ve - 1]))
                        {
                            ve--;
                        }
                    }

                    Value = v[vs..ve];
                    Quoted = false;
                }

                return true;
            }
        }
    }
}
