namespace RecursiveAI.OneMail.Composition;

/// <summary>
/// Subject prefixes of replies and forwards, as mail clients write them in their languages, so a prefix is replaced instead of stacked.
/// </summary>
/// <remarks>
/// A leading prefix is a label from <see cref="Labels"/> (matched ignoring case), an optional counter in square brackets or
/// parentheses (<c>Re[2]:</c>, <c>Re(3):</c>), then a colon. White space around is ignored. Stripping repeats, so
/// <c>Re: Fwd: AW: topic</c> normalises to <c>topic</c>. A word that merely looks like a label but is not listed (<c>Status: x</c>) stays.
/// </remarks>
public static class SubjectPrefixes
{
    /// <summary>
    /// The recognised labels, matched ignoring case: English Re, Fw, Fwd; German AW, WG; Scandinavian SV, Svar, VS, VB, ATB; Dutch Antw, Doorst;
    /// French TR, Ré, Réf, Rép; Spanish RV, PD, Reenv; Portuguese ENC, RES, Resp; Italian R, RIF, Inoltro; Polish Odp; Finnish VL; Turkish YNT, ILT.
    /// These are the prefixes Outlook's language packs and common clients write. Labels in non-Latin scripts are not listed.
    /// </summary>
    public static IReadOnlyList<string> Labels { get; } =
    [
        "Re", "Fw", "Fwd", "AW", "WG", "SV", "Svar", "VS", "VB", "ATB", "Antw", "Doorst", "TR", "Ré", "Réf", "Rép", "RV", "PD", "Reenv",
        "ENC", "RES", "Resp", "R", "RIF", "Inoltro", "Odp", "VL", "YNT", "ILT", "İLT",
    ];

    /// <summary>The longest prefix chain that is stripped; a subject made of nothing but prefixes stops here.</summary>
    private const int MaxChain = 64;

    /// <summary>Removes every leading reply or forward prefix (and counters).</summary>
    /// <param name="subject">The subject; null counts as empty.</param>
    /// <returns>The subject without leading prefixes, trimmed of leading white space.</returns>
    public static string Strip(string? subject)
    {
        if (string.IsNullOrEmpty(subject))
        {
            return string.Empty;
        }

        int i = 0;
        for (int round = 0; round < MaxChain; round++)
        {
            int next = MatchOne(subject, i);
            if (next < 0)
            {
                break;
            }

            i = next;
        }

        return subject[i..].TrimStart(' ', '\t');
    }

    /// <summary>The subject of a reply: one <paramref name="prefix"/> then the normalised subject.</summary>
    /// <param name="subject">The original's subject.</param>
    /// <param name="prefix">The prefix, normally <c>Re: </c>.</param>
    /// <returns>The new subject.</returns>
    public static string WithPrefix(string? subject, string prefix) => prefix + Strip(subject);

    // Returns the index after one prefix starting at 'start' (skipping blanks), or -1.
    private static int MatchOne(string s, int start)
    {
        int i = start;
        while (i < s.Length && s[i] is ' ' or '\t')
        {
            i++;
        }

        int labelStart = i;
        while (i < s.Length && i - labelStart < 8 && (char.IsLetter(s[i]) || s[i] == '.'))
        {
            i++;
        }

        if (i == labelStart)
        {
            return -1;
        }

        ReadOnlySpan<char> label = s.AsSpan(labelStart, i - labelStart);
        bool known = false;
        foreach (string l in Labels)
        {
            if (label.Equals(l, StringComparison.OrdinalIgnoreCase))
            {
                known = true;
                break;
            }
        }

        if (!known)
        {
            return -1;
        }

        // Optional counter: [n] or (n).
        if (i < s.Length && s[i] is '[' or '(')
        {
            char close = s[i] == '[' ? ']' : ')';
            int j = i + 1;
            int digits = 0;
            while (j < s.Length && char.IsAsciiDigit(s[j]) && digits < 6)
            {
                j++;
                digits++;
            }

            if (digits > 0 && j < s.Length && s[j] == close)
            {
                i = j + 1;
            }
            else
            {
                return -1;
            }
        }

        // The colon may be preceded by blanks in some clients ("Re :"), and is the full-width one in East Asian clients.
        while (i < s.Length && s[i] is ' ' or '\t')
        {
            i++;
        }

        if (i < s.Length && s[i] is ':' or '：')
        {
            return i + 1;
        }

        return -1;
    }
}
