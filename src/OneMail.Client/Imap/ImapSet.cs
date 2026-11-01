using System.Globalization;
using System.Text;

namespace RecursiveAI.OneMail.Client.Imap;

/// <summary>
/// A set of message numbers or UIDs in the IMAP sequence-set syntax (RFC 9051 §9): <c>1</c>, <c>2:4</c>, <c>1,5:7</c>, <c>3:*</c>.
/// Whether the numbers are sequence numbers or UIDs is decided by the command that takes the set.
/// </summary>
public readonly record struct ImapSet
{
    private readonly string? _text;

    /// <summary>Wraps a sequence-set text, validated against the grammar.</summary>
    /// <param name="text">For example <c>1:10,15</c>; <c>*</c> is the largest number in use; <c>$</c> is the saved search result (RFC 5182).</param>
    /// <exception cref="ArgumentException">The text is empty or contains characters outside digits, <c>: , * $</c>.</exception>
    public ImapSet(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0 || text.Any(c => !(c is >= '0' and <= '9' or ':' or ',' or '*' or '$')))
        {
            throw new ArgumentException("Not a sequence set: " + text, nameof(text));
        }

        _text = text;
    }

    /// <summary>The set in wire syntax. Empty for <c>default</c>.</summary>
    public string Text => _text ?? "";

    /// <summary>Whether this is the default (empty) value, which no command accepts.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(_text);

    /// <summary>Every message: <c>1:*</c>.</summary>
    public static ImapSet All { get; } = new("1:*");

    /// <summary>A single number.</summary>
    /// <param name="number">The number, at least 1.</param>
    /// <returns>The set.</returns>
    public static ImapSet Of(long number) => new(number.ToString(CultureInfo.InvariantCulture));

    /// <summary>A closed range.</summary>
    /// <param name="first">First number, at least 1.</param>
    /// <param name="last">Last number.</param>
    /// <returns>The set.</returns>
    public static ImapSet Range(long first, long last) =>
        new(first.ToString(CultureInfo.InvariantCulture) + ":" + last.ToString(CultureInfo.InvariantCulture));

    /// <summary>From <paramref name="first"/> to the last message: <c>n:*</c>.</summary>
    /// <param name="first">First number.</param>
    /// <returns>The set.</returns>
    public static ImapSet From(long first) => new(first.ToString(CultureInfo.InvariantCulture) + ":*");

    /// <summary>Builds a compact set from numbers (sorted, de-duplicated, adjacent numbers folded into ranges).</summary>
    /// <param name="numbers">The numbers; at least one.</param>
    /// <returns>The set.</returns>
    /// <exception cref="ArgumentException">There are no numbers.</exception>
    public static ImapSet Of(IEnumerable<long> numbers)
    {
        ArgumentNullException.ThrowIfNull(numbers);
        long[] sorted = numbers.Distinct().Order().ToArray();
        if (sorted.Length == 0)
        {
            throw new ArgumentException("A sequence set needs at least one number.", nameof(numbers));
        }

        var sb = new StringBuilder();
        for (int i = 0; i < sorted.Length;)
        {
            int j = i;
            while (j + 1 < sorted.Length && sorted[j + 1] == sorted[j] + 1)
            {
                j++;
            }

            if (sb.Length > 0)
            {
                sb.Append(',');
            }

            sb.Append(sorted[i].ToString(CultureInfo.InvariantCulture));
            if (j > i)
            {
                sb.Append(':').Append(sorted[j].ToString(CultureInfo.InvariantCulture));
            }

            i = j + 1;
        }

        return new ImapSet(sb.ToString());
    }

    /// <summary>Parses a sequence-set text.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The set.</returns>
    public static ImapSet Parse(string text) => new(text);

    /// <summary>Expands the set to numbers, with <c>*</c> taken as <paramref name="starValue"/>. Capped at 10 million numbers.</summary>
    /// <param name="starValue">The value of <c>*</c>.</param>
    /// <returns>The numbers in set order.</returns>
    public IEnumerable<long> Expand(long starValue = uint.MaxValue) => Protocol.ImapSequenceSet.Expand(Text, starValue);

    /// <summary>The wire syntax.</summary>
    /// <returns><see cref="Text"/>.</returns>
    public override string ToString() => Text;

    /// <summary>Converts a sequence-set text.</summary>
    /// <param name="text">The text.</param>
    public static implicit operator ImapSet(string text) => new(text);
}
