using System.Text;
using RecursiveAI.OneMail.Client.Protocol;

namespace RecursiveAI.OneMail.Client.Pop3.Protocol;

/// <summary>The kind of a POP3 status line (RFC 1939 §3).</summary>
public enum Pop3StatusKind
{
    /// <summary><c>+OK</c>.</summary>
    Ok = 0,

    /// <summary><c>-ERR</c>.</summary>
    Err,

    /// <summary><c>+ </c>: a SASL continuation (RFC 5034).</summary>
    Continuation,

    /// <summary>A line that is none of the above; <see cref="Pop3Response.Defects"/> says so.</summary>
    Unknown,
}

/// <summary>One POP3 status line: <c>+OK text</c> or <c>-ERR text</c> (with an optional bracketed response code, RFC 3206).</summary>
public sealed class Pop3Response
{
    internal Pop3Response(Pop3StatusKind kind, string text, string? code, IReadOnlyList<ProtocolDefect> defects)
    {
        Kind = kind;
        Text = text;
        Code = code;
        Defects = defects;
    }

    /// <summary>Which kind of line it is.</summary>
    public Pop3StatusKind Kind { get; }

    /// <summary>Whether this is <c>+OK</c>.</summary>
    public bool IsOk => Kind == Pop3StatusKind.Ok;

    /// <summary>The text after the status indicator, without a leading space and a response code.</summary>
    public string Text { get; }

    /// <summary>The extended response code in brackets at the start of the text (<c>SYS/TEMP</c>, <c>AUTH</c>, <c>IN-USE</c>), or null.</summary>
    public string? Code { get; }

    /// <summary>The repairs made while reading the line.</summary>
    public IReadOnlyList<ProtocolDefect> Defects { get; }

    /// <inheritdoc />
    public override string ToString() => (Kind == Pop3StatusKind.Ok ? "+OK " : Kind == Pop3StatusKind.Err ? "-ERR " : "") + Text;

    internal static Pop3Response Parse(ReadOnlySpan<byte> line)
    {
        string text = Encoding.UTF8.GetString(line);
        Pop3StatusKind kind;
        int skip;
        List<ProtocolDefect>? defects = null;
        // RFC 1939 §3: the indicators are case-sensitive "+OK" and "-ERR"; real servers differ, so match without case.
        if (StartsWith(line, "+OK") && (line.Length == 3 || line[3] == (byte)' '))
        {
            kind = Pop3StatusKind.Ok;
            skip = 3;
        }
        else if (StartsWith(line, "-ERR") && (line.Length == 4 || line[4] == (byte)' '))
        {
            kind = Pop3StatusKind.Err;
            skip = 4;
        }
        else if (line.Length > 0 && line[0] == (byte)'+' && (line.Length == 1 || line[1] == (byte)' '))
        {
            kind = Pop3StatusKind.Continuation;
            skip = 1;
        }
        else
        {
            kind = Pop3StatusKind.Unknown;
            skip = 0;
            defects = [new ProtocolDefect(ProtocolDefectKind.UnknownStatus, 0, "A status line that starts with neither +OK nor -ERR.")];
        }

        string rest = text.Length > skip ? text[skip..].TrimStart(' ') : "";
        if (kind is Pop3StatusKind.Ok or Pop3StatusKind.Err && rest.StartsWith('['))
        {
            int close = rest.IndexOf(']', StringComparison.Ordinal);
            if (close > 0)
            {
                return new Pop3Response(kind, rest[(close + 1)..].TrimStart(' '), rest[1..close], defects ?? []);
            }
        }

        return new Pop3Response(kind, rest, null, defects ?? []);
    }

    private static bool StartsWith(ReadOnlySpan<byte> line, string prefix) =>
        line.Length >= prefix.Length && Ascii.EqualsIgnoreCase(line[..prefix.Length], Encoding.ASCII.GetBytes(prefix));
}

/// <summary>The capabilities a server listed in answer to <c>CAPA</c> (RFC 2449): keywords with their arguments.</summary>
public sealed class Pop3Capabilities
{
    private readonly Dictionary<string, string[]> _items = new(StringComparer.OrdinalIgnoreCase);

    internal Pop3Capabilities(IEnumerable<string> lines)
    {
        foreach (string line in lines)
        {
            string[] words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 0)
            {
                _items[words[0]] = words[1..];
            }
        }
    }

    /// <summary>The announced keywords.</summary>
    public IEnumerable<string> Keywords => _items.Keys;

    /// <summary>Whether the keyword is announced (ASCII case-insensitive): <c>TOP</c>, <c>UIDL</c>, <c>STLS</c>, <c>PIPELINING</c>, <c>SASL</c>, <c>RESP-CODES</c>.</summary>
    /// <param name="keyword">The keyword.</param>
    /// <returns>True if announced.</returns>
    public bool Has(string keyword) => _items.ContainsKey(keyword);

    /// <summary>The arguments of a keyword (empty if none or absent).</summary>
    /// <param name="keyword">The keyword.</param>
    /// <returns>The words.</returns>
    public IReadOnlyList<string> Arguments(string keyword) => _items.TryGetValue(keyword, out string[]? a) ? a : [];

    /// <summary>The SASL mechanisms from the <c>SASL</c> keyword, upper case.</summary>
    public IReadOnlyList<string> SaslMechanisms => [.. Arguments("SASL").Select(m => m.ToUpperInvariant())];
}
