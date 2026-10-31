using System.Text;

namespace RecursiveAI.OneMail.Stores.Maildir;

/// <summary>A Maildir file name taken apart: the unique part and the flag information after <c>:2,</c> (or <c>;2,</c>, see <see cref="InfoSeparators"/>).</summary>
/// <param name="Unique">
/// The part before the info separator: the identity of the message, which never changes while flags change. A name without a
/// separator is all unique part.
/// </param>
/// <param name="Marks">The standard flags (<c>P R S T D F</c>) in the info section.</param>
/// <param name="KeywordLetters">The lowercase letters <c>a</c> to <c>z</c> in the info section: Dovecot keyword slots. In ASCII order.</param>
/// <param name="OtherFlags">Info-section letters that are neither standard flags nor lowercase keywords, in written order.</param>
/// <param name="Extra">Dovecot's non-standard fields after a comma in the info section, without the comma; empty if none.</param>
/// <param name="RawInfo">The text after the separator exactly as written (for example <c>2,FS</c>); empty if the name has none.</param>
/// <param name="Size">The <c>S=</c> size from the unique part, or -1.</param>
/// <param name="VirtualSize">The <c>W=</c> size from the unique part, or -1.</param>
/// <param name="Defects">What was irregular about the name.</param>
public readonly record struct MaildirName(
    string Unique,
    MessageMarks Marks,
    string KeywordLetters,
    string OtherFlags,
    string Extra,
    string RawInfo,
    long Size,
    long VirtualSize,
    StoreDefects Defects)
{
    /// <summary>
    /// The characters that may separate the unique part from the info section when reading: <c>:</c> (the Maildir specification) and
    /// <c>;</c> (what Windows tools use, because Windows file names cannot hold a colon). Both are accepted in every store
    /// regardless of platform; which one is written is <see cref="MaildirStoreOptions.InfoSeparator"/>.
    /// </summary>
    public static ReadOnlySpan<char> InfoSeparators => ":;";

    /// <summary>The separator written by default: <c>;</c> on Windows, <c>:</c> elsewhere.</summary>
    public static char DefaultInfoSeparator => OperatingSystem.IsWindows() ? ';' : ':';

    /// <summary>
    /// Finds where the info section starts: the first colon, or the first semicolon that is followed by the info version
    /// <c>2,</c> (a semicolon elsewhere in a name is just a character of the unique part).
    /// </summary>
    /// <param name="fileName">A file name without any directory.</param>
    /// <returns>The index of the separator, or -1.</returns>
    public static int FindInfoSeparator(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        int colon = fileName.IndexOf(':', StringComparison.Ordinal);
        int semi = fileName.IndexOf(";2,", StringComparison.Ordinal);
        return colon < 0 ? semi : semi < 0 ? colon : Math.Min(colon, semi);
    }

    /// <summary>
    /// Takes a Maildir file name apart. Never fails: a name that does not follow <c>unique:2,flags</c> is all unique part
    /// (<see cref="StoreDefects.UnrecognisedFileName"/> when it has a colon with another info version). Both <c>:</c> and <c>;</c> are read as the separator.
    /// </summary>
    /// <param name="fileName">The file name without any directory.</param>
    /// <returns>The parsed name.</returns>
    public static MaildirName Parse(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        int colon = FindInfoSeparator(fileName);
        string unique = colon < 0 ? fileName : fileName[..colon];
        string info = colon < 0 ? string.Empty : fileName[(colon + 1)..];
        (long size, long vsize) = ParseSizes(unique);

        MessageMarks marks = MessageMarks.None;
        StringBuilder keywords = new();
        StringBuilder other = new();
        string extra = string.Empty;
        StoreDefects defects = StoreDefects.None;

        if (colon >= 0)
        {
            if (info.StartsWith("2,", StringComparison.Ordinal))
            {
                string flags = info[2..];
                int comma = flags.IndexOf(',', StringComparison.Ordinal);
                if (comma >= 0)
                {
                    extra = flags[(comma + 1)..];
                    flags = flags[..comma];
                }

                char prev = '\0';
                foreach (char c in flags)
                {
                    switch (c)
                    {
                        case 'P': marks |= MessageMarks.Forwarded; break;
                        case 'R': marks |= MessageMarks.Answered; break;
                        case 'S': marks |= MessageMarks.Seen; break;
                        case 'T': marks |= MessageMarks.Deleted; break;
                        case 'D': marks |= MessageMarks.Draft; break;
                        case 'F': marks |= MessageMarks.Flagged; break;
                        default:
                            if (c is >= 'a' and <= 'z')
                            {
                                keywords.Append(c);
                            }
                            else
                            {
                                other.Append(c);
                            }

                            break;
                    }

                    if (c <= prev)
                    {
                        defects |= StoreDefects.UnsortedFlags;
                    }

                    prev = c;
                }
            }
            else
            {
                // Another info version ("1," is experimental): kept in RawInfo, not interpreted.
                defects |= StoreDefects.UnrecognisedFileName;
            }
        }

        return new MaildirName(unique, marks, keywords.ToString(), other.ToString(), extra, info, size, vsize, defects);
    }

    /// <summary>
    /// Builds the name for a message: the unique part, then <c>:2,</c> and the flags in ASCII order (as the Maildir
    /// convention requires) when there are any.
    /// </summary>
    /// <param name="unique">The unique part.</param>
    /// <param name="marks">Standard flags. <see cref="MessageMarks.Recent"/> is not a file-name flag and is ignored.</param>
    /// <param name="keywordLetters">Keyword letters <c>a</c> to <c>z</c>; any order, duplicates removed.</param>
    /// <param name="extra">Dovecot's non-standard fields, written after a comma; null or empty for none.</param>
    /// <param name="separator">The character before the info section: <c>:</c> (default) or <c>;</c>.</param>
    /// <returns>The file name.</returns>
    /// <exception cref="ArgumentException"><paramref name="separator"/> is neither <c>:</c> nor <c>;</c>.</exception>
    public static string Build(string unique, MessageMarks marks, string keywordLetters = "", string? extra = null, char separator = ':')
    {
        ArgumentNullException.ThrowIfNull(unique);
        if (separator is not (':' or ';'))
        {
            throw new ArgumentException("The info separator must be ':' or ';'.", nameof(separator));
        }

        StringBuilder sb = new(unique);
        List<char> letters = [];
        if ((marks & MessageMarks.Draft) != 0) { letters.Add('D'); }
        if ((marks & MessageMarks.Flagged) != 0) { letters.Add('F'); }
        if ((marks & MessageMarks.Forwarded) != 0) { letters.Add('P'); }
        if ((marks & MessageMarks.Answered) != 0) { letters.Add('R'); }
        if ((marks & MessageMarks.Seen) != 0) { letters.Add('S'); }
        if ((marks & MessageMarks.Deleted) != 0) { letters.Add('T'); }
        foreach (char c in keywordLetters.Distinct().Order())
        {
            if (c is >= 'a' and <= 'z')
            {
                letters.Add(c);
            }
        }

        if (letters.Count > 0 || !string.IsNullOrEmpty(extra))
        {
            sb.Append(separator).Append("2,");
            foreach (char c in letters)
            {
                sb.Append(c);
            }

            if (!string.IsNullOrEmpty(extra))
            {
                sb.Append(',').Append(extra);
            }
        }

        return sb.ToString();
    }

    // Dovecot writes ",S=<size>" and ",W=<vsize>" into the unique part.
    private static (long Size, long VirtualSize) ParseSizes(string unique)
    {
        long size = -1;
        long vsize = -1;
        foreach (string part in unique.Split(','))
        {
            if (part.StartsWith("S=", StringComparison.Ordinal) && long.TryParse(part.AsSpan(2), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long s))
            {
                size = s;
            }
            else if (part.StartsWith("W=", StringComparison.Ordinal) && long.TryParse(part.AsSpan(2), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long w))
            {
                vsize = w;
            }
        }

        return (size, vsize);
    }
}
