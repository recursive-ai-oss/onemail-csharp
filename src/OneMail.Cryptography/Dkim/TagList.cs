using System.Text;

namespace RecursiveAI.OneMail.Cryptography.Dkim;

/// <summary>One <c>name=value</c> pair of a tag list. Offsets index the string that was parsed.</summary>
internal readonly record struct Tag(string Name, string Value, int ValueStart, int ValueEnd);

internal enum TagListError
{
    None,
    TooManyTags,
    MissingEquals,
    BadName,
    DuplicateTag,
}

/// <summary>
/// RFC 6376 §3.2 tag lists: <c>[FWS] tag-name [FWS] "=" [FWS] tag-value [FWS]</c> separated by semicolons, the last semicolon optional.
/// Names are case-sensitive and may not repeat. Parsing is lenient about empty elements (<c>;;</c>) and strict about the rest.
/// </summary>
internal static class TagList
{
    public const int MaxTags = 64;

    public static bool IsFws(char c) => c is ' ' or '\t' or '\r' or '\n';

    public static bool TryParse(string s, out List<Tag> tags, out TagListError error, out string? detail)
    {
        tags = [];
        error = TagListError.None;
        detail = null;
        int pos = 0;
        while (pos < s.Length)
        {
            int end = s.IndexOf(';', pos);
            if (end < 0)
            {
                end = s.Length;
            }

            int a = pos;
            int b = end;
            while (a < b && IsFws(s[a]))
            {
                a++;
            }

            while (b > a && IsFws(s[b - 1]))
            {
                b--;
            }

            pos = end + 1;
            if (a == b)
            {
                continue;
            }

            int eq = s.IndexOf('=', a, b - a);
            if (eq < 0)
            {
                error = TagListError.MissingEquals;
                detail = "tag without '='";
                return false;
            }

            int ne = eq;
            while (ne > a && IsFws(s[ne - 1]))
            {
                ne--;
            }

            string name = s[a..ne];
            if (!IsTagName(name))
            {
                error = TagListError.BadName;
                detail = "invalid tag name";
                return false;
            }

            int va = eq + 1;
            while (va < b && IsFws(s[va]))
            {
                va++;
            }

            if (tags.Count >= MaxTags)
            {
                error = TagListError.TooManyTags;
                detail = "more than " + MaxTags + " tags";
                return false;
            }

            foreach (Tag t in tags)
            {
                if (t.Name == name)
                {
                    error = TagListError.DuplicateTag;
                    detail = "duplicate tag '" + name + "'";
                    return false;
                }
            }

            tags.Add(new Tag(name, s[va..b], va, b));
        }

        return true;
    }

    private static bool IsTagName(string n)
    {
        if (n.Length == 0 || !(char.IsAsciiLetter(n[0])))
        {
            return false;
        }

        foreach (char c in n)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                return false;
            }
        }

        return true;
    }

    public static string? Get(List<Tag> tags, string name)
    {
        foreach (Tag t in tags)
        {
            if (t.Name == name)
            {
                return t.Value;
            }
        }

        return null;
    }

    public static bool TryGet(List<Tag> tags, string name, out Tag tag)
    {
        foreach (Tag t in tags)
        {
            if (t.Name == name)
            {
                tag = t;
                return true;
            }
        }

        tag = default;
        return false;
    }

    /// <summary>Decodes base64 that may contain folding whitespace (RFC 6376 §3.5, <c>b=</c> and <c>bh=</c>). Missing padding is tolerated.</summary>
    public static bool TryDecodeBase64(string value, int maxBytes, out byte[] bytes)
    {
        bytes = [];
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (IsFws(c))
            {
                continue;
            }

            if (!(char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '='))
            {
                return false;
            }

            sb.Append(c);
        }

        if (sb.Length > maxBytes / 3 * 4 + 8)
        {
            return false;
        }

        while (sb.Length % 4 != 0 && sb.Length > 0 && sb[^1] != '=')
        {
            sb.Append('=');
        }

        try
        {
            bytes = Convert.FromBase64String(sb.ToString());
        }
        catch (FormatException)
        {
            return false;
        }

        return bytes.Length <= maxBytes;
    }

    /// <summary>Splits a colon-separated list (<c>h=</c>, key <c>h=</c>, <c>s=</c>, <c>t=</c>), trimming whitespace and dropping empty items.</summary>
    public static List<string> SplitColon(string value)
    {
        var list = new List<string>();
        foreach (string part in value.Split(':'))
        {
            string t = part.Trim(' ', '\t', '\r', '\n');
            if (t.Length > 0)
            {
                list.Add(t);
            }
        }

        return list;
    }
}
