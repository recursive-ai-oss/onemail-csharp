using RecursiveAI.OneMail.Client.Protocol;

namespace RecursiveAI.OneMail.Client.Imap.Protocol;

/// <summary>Where a response came in the exchange.</summary>
public enum ImapResponseKind
{
    /// <summary>A line starting with <c>*</c>: data or an unsolicited status.</summary>
    Untagged = 0,

    /// <summary>A line starting with the client's tag: the completion of a command.</summary>
    Tagged,

    /// <summary>A line starting with <c>+</c>: the server asks for more (a literal, a SASL response, <c>IDLE</c> started).</summary>
    Continuation,
}

/// <summary>The status word of a status response (RFC 9051 §7.1). Append new values at the end.</summary>
public enum ImapStatus
{
    /// <summary>Not a status response (data such as <c>* 3 EXISTS</c>), or the status word was unrecognisable.</summary>
    None = 0,

    /// <summary><c>OK</c>.</summary>
    Ok,

    /// <summary><c>NO</c>.</summary>
    No,

    /// <summary><c>BAD</c>.</summary>
    Bad,

    /// <summary><c>PREAUTH</c> (untagged greeting only).</summary>
    PreAuth,

    /// <summary><c>BYE</c> (untagged).</summary>
    Bye,
}

/// <summary>A bracketed response code such as <c>[UIDVALIDITY 3]</c>, <c>[APPENDUID 38505 3955]</c> or <c>[CAPABILITY IMAP4rev1 IDLE]</c>.</summary>
public sealed class ImapResponseCode
{
    internal ImapResponseCode(string name, IReadOnlyList<ImapValue> arguments, string raw)
    {
        Name = name;
        Arguments = arguments;
        Raw = raw;
    }

    /// <summary>The code name, upper case (<c>UIDVALIDITY</c>).</summary>
    public string Name { get; }

    /// <summary>The arguments after the name, parsed as generic values. Unknown codes keep theirs.</summary>
    public IReadOnlyList<ImapValue> Arguments { get; }

    /// <summary>The text between the brackets, verbatim.</summary>
    public string Raw { get; }

    /// <summary>Whether the code is <paramref name="name"/>, ignoring ASCII case.</summary>
    /// <param name="name">The code name.</param>
    /// <returns>True when equal.</returns>
    public bool Is(string name) => string.Equals(Name, name, StringComparison.OrdinalIgnoreCase);
}

/// <summary>One response of an IMAP server, parsed into a tree. Nothing is thrown away: what the typed views do not know stays in <see cref="Data"/>.</summary>
public sealed partial class ImapResponse
{
    internal ImapResponse(ImapResponseKind kind)
    {
        Kind = kind;
    }

    /// <summary>Untagged, tagged or continuation.</summary>
    public ImapResponseKind Kind { get; internal set; }

    /// <summary>For a tagged response: the tag, verbatim.</summary>
    public string? Tag { get; internal set; }

    /// <summary>For a status response (tagged, or untagged OK/NO/BAD/PREAUTH/BYE): its status; otherwise <see cref="ImapStatus.None"/>.</summary>
    public ImapStatus Status { get; internal set; }

    /// <summary>For untagged data: the number before the keyword (<c>3</c> in <c>* 3 EXISTS</c>), or null.</summary>
    public long? Number { get; internal set; }

    /// <summary>
    /// For untagged responses: the keyword, upper case (<c>FETCH</c>, <c>LIST</c>, <c>OK</c>); empty if absent. For tagged
    /// ones: the status word. For continuations: empty.
    /// </summary>
    public string Name { get; internal set; } = "";

    /// <summary>The response code of a status response, or null.</summary>
    public ImapResponseCode? Code { get; internal set; }

    /// <summary>
    /// The human-readable text of a status response; for a continuation the text after <c>+ </c> (a base64 challenge in SASL, or
    /// the prompt of IDLE). Empty otherwise.
    /// </summary>
    public string Text { get; internal set; } = "";

    /// <summary>For untagged data: the values after the keyword. For FETCH: one list.</summary>
    public IReadOnlyList<ImapValue> Data { get; internal set; } = ImapValue.NoItems;

    /// <summary>The repairs made while parsing.</summary>
    public IReadOnlyList<ProtocolDefect> Defects { get; internal set; } = [];

    /// <summary>The first line, verbatim (without line end). Literal bytes are not included.</summary>
    public string FirstLine { get; internal set; } = "";

    /// <summary>Whether this is a tagged OK.</summary>
    public bool IsOk => Kind == ImapResponseKind.Tagged && Status == ImapStatus.Ok;

    /// <summary>Whether the keyword is <paramref name="name"/> (ASCII case-insensitive).</summary>
    /// <param name="name">The keyword.</param>
    /// <returns>True when equal.</returns>
    public bool Is(string name) => string.Equals(Name, name, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override string ToString() => FirstLine;
}
