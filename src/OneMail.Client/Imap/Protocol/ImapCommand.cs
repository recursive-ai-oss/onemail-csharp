using System.Text;
using RecursiveAI.OneMail.Text;

namespace RecursiveAI.OneMail.Client.Imap.Protocol;

/// <summary>How a literal in a command is announced (RFC 9051 §4.3, RFC 7888).</summary>
public enum ImapLiteralMode
{
    /// <summary>Non-synchronising (<c>{n+}</c>) when the server advertises LITERAL+, or LITERAL- and the literal is at most 4096 bytes; otherwise synchronising.</summary>
    Auto = 0,

    /// <summary>Always synchronising: <c>{n}</c>, then wait for the server's continuation before sending the bytes.</summary>
    Synchronizing,

    /// <summary>Always non-synchronising: <c>{n+}</c>. Only for servers known to accept it.</summary>
    NonSynchronizing,
}

/// <summary>
/// An IMAP command as a list of typed parts, serialised by <see cref="ImapRawConnection"/> when it is sent. Parts are separated by
/// single spaces automatically (none after <see cref="Open"/> or before <see cref="Close"/>). String-like parts are checked and
/// quoted or sent as literals as their content requires, so a value can never break out of its position in the command.
/// </summary>
public sealed class ImapCommand
{
    internal enum PartKind { Atom, Raw, Quoted, Astring, Mailbox, Open, Close, LiteralBytes, LiteralStream }

    internal readonly record struct Part(PartKind Kind, string? Text, ReadOnlyMemory<byte> Bytes, Stream? Stream, long Length, ImapLiteralMode Mode, bool Secret);

    private readonly List<Part> _parts = [];

    private ImapCommand(string name)
    {
        Name = name;
        Atom(name);
    }

    /// <summary>The command name as given (<c>FETCH</c>).</summary>
    public string Name { get; }

    /// <summary>The tag; null until sent, or set by the caller before sending to choose it.</summary>
    public string? Tag { get; set; }

    internal IReadOnlyList<Part> Parts => _parts;

    /// <summary>Starts a command.</summary>
    /// <param name="name">The command name, an atom (<c>UID FETCH</c> is two: use <c>Create("UID").Atom("FETCH")</c>).</param>
    /// <returns>The new command.</returns>
    /// <exception cref="ArgumentException">The name is not a valid atom.</exception>
    public static ImapCommand Create(string name) => new(name);

    /// <summary>Appends a keyword or other atom (<c>ALL</c>, <c>UNSEEN</c>, <c>BODY.PEEK[HEADER]</c>).</summary>
    /// <param name="atom">ASCII printable characters other than <c>( ) { SP % * " \</c>.</param>
    /// <returns>This command.</returns>
    /// <exception cref="ArgumentException">The text is empty or has a character an atom cannot contain.</exception>
    public ImapCommand Atom(string atom)
    {
        ArgumentNullException.ThrowIfNull(atom);
        if (atom.Length == 0 || atom.Any(c => c <= ' ' || c >= 0x7F || c is '(' or ')' or '{' or '%' or '*' or '"' or '\\'))
        {
            throw new ArgumentException("Not a valid IMAP atom: " + atom, nameof(atom));
        }

        _parts.Add(new Part(PartKind.Atom, atom, default, null, 0, default, false));
        return this;
    }

    /// <summary>Appends a number.</summary>
    /// <param name="value">A non-negative integer.</param>
    /// <returns>This command.</returns>
    public ImapCommand Number(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        _parts.Add(new Part(PartKind.Atom, value.ToString(System.Globalization.CultureInfo.InvariantCulture), default, null, 0, default, false));
        return this;
    }

    /// <summary>Appends a sequence set (<c>1,3:7,10:*</c>).</summary>
    /// <param name="set">Digits, <c>:</c>, <c>,</c> and <c>*</c> only (a UID set may also use <c>$</c>).</param>
    /// <returns>This command.</returns>
    /// <exception cref="ArgumentException">The set has another character.</exception>
    public ImapCommand SequenceSet(string set)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (set.Length == 0 || set.Any(c => !(c is >= '0' and <= '9' or ':' or ',' or '*' or '$')))
        {
            throw new ArgumentException("Not a sequence set: " + set, nameof(set));
        }

        _parts.Add(new Part(PartKind.Raw, set, default, null, 0, default, false));
        return this;
    }

    /// <summary>
    /// Appends text exactly as given, unchecked and unquoted, for syntax the typed parts do not cover (a FETCH item list
    /// <c>(UID BODY.PEEK[HEADER.FIELDS (FROM)])</c>, a SEARCH key). The caller is responsible for it being valid and for not including untrusted text.
    /// </summary>
    /// <param name="text">ASCII text without CR or LF.</param>
    /// <returns>This command.</returns>
    /// <exception cref="ArgumentException">The text contains CR, LF or NUL, or non-ASCII.</exception>
    public ImapCommand Raw(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Any(c => c is '\r' or '\n' or '\0' || c >= 0x80))
        {
            throw new ArgumentException("Raw text must be ASCII without CR, LF or NUL.", nameof(text));
        }

        _parts.Add(new Part(PartKind.Raw, text, default, null, 0, default, false));
        return this;
    }

    /// <summary>Appends a string argument (an astring): an atom when it is safe as one, a quoted string when it is printable, otherwise a literal.</summary>
    /// <param name="text">The text. Non-ASCII text is sent as a UTF-8 literal.</param>
    /// <param name="secret">When true the trace shows asterisks instead of the text (a password).</param>
    /// <returns>This command.</returns>
    public ImapCommand Astring(string text, bool secret = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        _parts.Add(new Part(PartKind.Astring, text, default, null, 0, default, secret));
        return this;
    }

    /// <summary>Appends a string argument that is always quoted when possible (never a bare atom), e.g. the user and password of LOGIN.</summary>
    /// <param name="text">The text; text that cannot be quoted (CR, LF, non-ASCII) becomes a literal.</param>
    /// <param name="secret">When true the trace shows asterisks.</param>
    /// <returns>This command.</returns>
    public ImapCommand Quoted(string text, bool secret = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        _parts.Add(new Part(PartKind.Quoted, text, default, null, 0, default, secret));
        return this;
    }

    /// <summary>
    /// Appends a mailbox name. It is converted to modified UTF-7 (RFC 3501 §5.1.3) unless UTF8=ACCEPT is enabled on the connection,
    /// then sent like <see cref="Astring"/>. <c>INBOX</c> in any case is the inbox.
    /// </summary>
    /// <param name="name">The name as text, with a hierarchy delimiter as the server uses it.</param>
    /// <returns>This command.</returns>
    public ImapCommand Mailbox(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        _parts.Add(new Part(PartKind.Mailbox, name, default, null, 0, default, false));
        return this;
    }

    /// <summary>Opens a parenthesised list.</summary>
    /// <returns>This command.</returns>
    public ImapCommand Open()
    {
        _parts.Add(new Part(PartKind.Open, null, default, null, 0, default, false));
        return this;
    }

    /// <summary>Closes a parenthesised list.</summary>
    /// <returns>This command.</returns>
    public ImapCommand Close()
    {
        _parts.Add(new Part(PartKind.Close, null, default, null, 0, default, false));
        return this;
    }

    /// <summary>Appends a parenthesised list of flags or keywords (<c>(\Seen \Flagged $Junk)</c>).</summary>
    /// <param name="flags">System flags start with a backslash; keywords are atoms.</param>
    /// <returns>This command.</returns>
    /// <exception cref="ArgumentException">A flag contains a character a flag cannot.</exception>
    public ImapCommand Flags(IEnumerable<string> flags)
    {
        ArgumentNullException.ThrowIfNull(flags);
        Open();
        foreach (string f in flags)
        {
            string body = f.StartsWith('\\') ? f[1..] : f;
            if (body.Length == 0 || body.Any(c => c <= ' ' || c >= 0x7F || c is '(' or ')' or '{' or '%' or '*' or '"' or '\\' or ']'))
            {
                throw new ArgumentException("Not a valid flag: " + f, nameof(flags));
            }

            _parts.Add(new Part(PartKind.Raw, f, default, null, 0, default, false));
        }

        return Close();
    }

    /// <summary>Appends a literal from memory.</summary>
    /// <param name="data">The bytes.</param>
    /// <param name="mode">How to announce it. Default <see cref="ImapLiteralMode.Auto"/>.</param>
    /// <returns>This command.</returns>
    public ImapCommand Literal(ReadOnlyMemory<byte> data, ImapLiteralMode mode = ImapLiteralMode.Auto)
    {
        _parts.Add(new Part(PartKind.LiteralBytes, null, data, null, data.Length, mode, false));
        return this;
    }

    /// <summary>Appends a literal read from a stream when the command is sent (an APPEND of a large message), without buffering it.</summary>
    /// <param name="stream">The stream, positioned at the first byte.</param>
    /// <param name="length">The exact number of bytes to send; the announcement needs it up front.</param>
    /// <param name="mode">How to announce it. Default <see cref="ImapLiteralMode.Auto"/>.</param>
    /// <returns>This command.</returns>
    public ImapCommand Literal(Stream stream, long length, ImapLiteralMode mode = ImapLiteralMode.Auto)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _parts.Add(new Part(PartKind.LiteralStream, null, default, stream, length, mode, false));
        return this;
    }

    internal static bool IsAtomSafe(string s) =>
        s.Length > 0 && s.All(c => c > ' ' && c < 0x7F && c is not ('(' or ')' or '{' or '%' or '*' or '"' or '\\' or ']'));

    internal static string EncodeMailbox(string name, bool utf8) =>
        utf8 ? name : ImapUtf7.Encode(name);

    internal static byte[] ToUtf8(string s) => Encoding.UTF8.GetBytes(s);
}
