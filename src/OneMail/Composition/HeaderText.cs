using System.Text;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Text;

namespace RecursiveAI.OneMail.Composition;

/// <summary>
/// How composed messages write text that may not be ASCII: display names, unstructured fields such as the subject, and parameter values such as
/// file names. By default the result is ASCII (RFC 2047 encoded-words, RFC 2231 extended parameters); with <c>utf8</c> set the text goes out as
/// raw UTF-8 (RFC 6532, for SMTPUTF8 transports).
/// </summary>
internal static class HeaderText
{
    /// <summary>An unstructured field value (the subject, Thread-Topic): encoded-words when it needs them, else as given.</summary>
    public static string Unstructured(string value, bool utf8) => utf8 ? value : Rfc2047.Encode(value);

    /// <summary>A header field. With <paramref name="utf8"/> free text such as the subject stays raw UTF-8 rather than being turned into encoded-words.</summary>
    public static MimeHeader Header(string name, string value, bool utf8) => utf8 ? new MimeHeader(name, value, "\r\n"u8, utf8Text: true) : new MimeHeader(name, value);

    /// <summary>One mailbox: <c>phrase &lt;address&gt;</c>, or the bare address when there is no name.</summary>
    public static string Mailbox(string? name, string address, bool utf8)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return address;
        }

        string display = name.Trim();
        string phrase;
        bool nonAscii = display.AsSpan().ContainsAnyExceptInRange((char)0x20, (char)0x7E);
        if (nonAscii && !utf8)
        {
            // RFC 2047 §5(3): in a phrase every word is an atom or an encoded-word, so the whole name is encoded; plain stretches left in
            // between could hold specials such as "@", a comma or a quote that would corrupt the address list.
            phrase = EncodeWords(display);
        }
        else if (IsAtomText(display))
        {
            phrase = display;
        }
        else
        {
            phrase = "\"" + display.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        }

        return phrase + " <" + address + ">";
    }

    /// <summary>
    /// A parameter for a structured field (<c>; filename=...</c>). Non-ASCII values become RFC 2231 extended parameters, or with <paramref name="utf8"/> a
    /// quoted string of raw UTF-8 (RFC 6532 §3.2).
    /// </summary>
    public static string Parameter(string name, string value, bool utf8)
    {
        if (utf8 && value.AsSpan().ContainsAnyExceptInRange((char)0x20, (char)0x7E) && value.AsSpan().IndexOfAny('\r', '\n') < 0)
        {
            return "; " + name + "=\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        }

        return MimeHeaderValue.Parameter(name, value);
    }

    // The text as B encoded-words of UTF-8, each at most 75 characters (RFC 2047 §2), never cutting a character.
    private static string EncodeWords(string text)
    {
        var words = new List<string>();
        var chunk = new List<byte>();
        Span<byte> buf = new byte[4];
        foreach (Rune r in text.EnumerateRunes())
        {
            int n = r.EncodeToUtf8(buf);
            if (chunk.Count + n > 42)
            {
                words.Add(Word(chunk));
                chunk.Clear();
            }

            chunk.AddRange(buf[..n].ToArray());
        }

        if (chunk.Count > 0)
        {
            words.Add(Word(chunk));
        }

        return string.Join(' ', words);

        static string Word(List<byte> bytes) => "=?utf-8?B?" + Convert.ToBase64String([.. bytes]) + "?=";
    }

    // A phrase made only of atoms and single spaces needs no quotes (RFC 5322 §3.2.3); with RFC 6532 non-ASCII letters count as atom text.
    private static bool IsAtomText(string s)
    {
        bool word = false;
        foreach (char c in s)
        {
            if (c == ' ')
            {
                if (!word)
                {
                    return false;
                }

                word = false;
                continue;
            }

            if (!(char.IsAsciiLetterOrDigit(c) || (c > 0x7F && !char.IsControl(c) && c is not ('\u2028' or '\u2029')) || "!#$%&'*+-/=?^_`{|}~".Contains(c, StringComparison.Ordinal)))
            {
                return false;
            }

            word = true;
        }

        return word;
    }
}
