using System.Globalization;
using System.Text;

namespace RecursiveAI.OneMail.Client.Imap.Protocol;

/// <summary>What an <see cref="ImapValue"/> is.</summary>
public enum ImapValueKind
{
    /// <summary><c>NIL</c>.</summary>
    Nil = 0,

    /// <summary>An unquoted token: a keyword, a number, a flag, a sequence set, <c>BODY[HEADER]</c>.</summary>
    Atom,

    /// <summary>A quoted string, unescaped.</summary>
    Quoted,

    /// <summary>A literal. Its bytes are in <see cref="ImapValue.Bytes"/>, or it was streamed or skipped.</summary>
    Literal,

    /// <summary>A parenthesised list.</summary>
    List,
}

/// <summary>One node of the generic tree an IMAP response line is parsed into (RFC 9051 §9, formal syntax).</summary>
public sealed class ImapValue
{
    internal static readonly ImapValue[] NoItems = [];

    private static readonly ImapValue NilValue = new(ImapValueKind.Nil, default, NoItems);

    internal ImapValue(ImapValueKind kind, ReadOnlyMemory<byte> bytes, IReadOnlyList<ImapValue> items)
    {
        Kind = kind;
        Bytes = bytes;
        Items = items;
    }

    internal static ImapValue Nil => NilValue;

    /// <summary>The kind of value.</summary>
    public ImapValueKind Kind { get; }

    /// <summary>The raw bytes of an atom, quoted string or buffered literal (empty otherwise).</summary>
    public ReadOnlyMemory<byte> Bytes { get; }

    /// <summary>The elements of a list (empty for other kinds).</summary>
    public IReadOnlyList<ImapValue> Items { get; }

    /// <summary>For a literal: the announced size.</summary>
    public long LiteralLength { get; internal init; }

    /// <summary>For a literal: it was handed to the caller's literal handler as a stream, so <see cref="Bytes"/> is empty.</summary>
    public bool IsStreamed { get; internal init; }

    /// <summary>For a literal: it was larger than the buffering limit and was discarded, so <see cref="Bytes"/> is empty.</summary>
    public bool IsSkipped { get; internal init; }

    /// <summary>Whether this is <c>NIL</c>.</summary>
    public bool IsNil => Kind == ImapValueKind.Nil;

    /// <summary>Whether this is a list.</summary>
    public bool IsList => Kind == ImapValueKind.List;

    /// <summary>The text of an atom, quoted string or literal decoded as UTF-8 (invalid bytes become U+FFFD); null for NIL; empty for a list.</summary>
    public string? Text => Kind switch
    {
        ImapValueKind.Nil => null,
        ImapValueKind.List => "",
        _ => Encoding.UTF8.GetString(Bytes.Span),
    };

    /// <summary>Whether this is an atom equal to <paramref name="name"/>, ignoring ASCII case.</summary>
    /// <param name="name">The keyword, ASCII.</param>
    /// <returns>True when equal.</returns>
    public bool IsAtom(string name) =>
        Kind == ImapValueKind.Atom && Bytes.Length == name.Length && Ascii.EqualsIgnoreCase(Bytes.Span, name);

    /// <summary>Reads an atom (or quoted string) as a non-negative integer.</summary>
    /// <param name="value">The number.</param>
    /// <returns>True when the text is a decimal number that fits in 63 bits.</returns>
    public bool TryGetNumber(out long value)
    {
        value = 0;
        if (Kind is not (ImapValueKind.Atom or ImapValueKind.Quoted) || Bytes.Length == 0 || Bytes.Length > 19)
        {
            return false;
        }

        return long.TryParse(Bytes.Span, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <inheritdoc />
    public override string ToString() => Kind switch
    {
        ImapValueKind.Nil => "NIL",
        ImapValueKind.Atom => Text!,
        ImapValueKind.Quoted => "\"" + Text + "\"",
        ImapValueKind.Literal => IsStreamed ? "{" + LiteralLength + " streamed}" : IsSkipped ? "{" + LiteralLength + " skipped}" : "{" + LiteralLength + "}" + Text,
        _ => "(" + string.Join(' ', Items) + ")",
    };
}
