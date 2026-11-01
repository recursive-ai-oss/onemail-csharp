using System.Runtime.CompilerServices;
using System.Text;
using RecursiveAI.OneMail.Client.Protocol;

namespace RecursiveAI.OneMail.Client.Smtp.Protocol;

/// <summary>An enhanced status code (RFC 3463): <c>class.subject.detail</c>, e.g. <c>5.7.1</c>.</summary>
/// <param name="Class">2 success, 4 persistent transient failure, 5 permanent failure.</param>
/// <param name="Subject">The subject, 0 to 999.</param>
/// <param name="Detail">The detail, 0 to 999.</param>
public readonly record struct SmtpEnhancedStatus(int Class, int Subject, int Detail)
{
    /// <inheritdoc />
    public override string ToString() => $"{Class}.{Subject}.{Detail}";
}

/// <summary>One SMTP reply (RFC 5321 §4.2): a three-digit code and one or more lines of text.</summary>
public sealed class SmtpReply
{
    internal SmtpReply(int code, IReadOnlyList<string> lines, SmtpEnhancedStatus? enhanced, IReadOnlyList<ProtocolDefect> defects)
    {
        Code = code;
        Lines = lines;
        EnhancedStatus = enhanced;
        Defects = defects;
    }

    /// <summary>The reply code, or 0 when the first line had none (see <see cref="Defects"/>).</summary>
    public int Code { get; }

    /// <summary>The text of each line, without the code and separator (an enhanced status code stays in the text).</summary>
    public IReadOnlyList<string> Lines { get; }

    /// <summary>The enhanced status code that starts the first line, when the server uses them (RFC 2034), else null.</summary>
    public SmtpEnhancedStatus? EnhancedStatus { get; }

    /// <summary>The repairs made while reading the reply.</summary>
    public IReadOnlyList<ProtocolDefect> Defects { get; }

    /// <summary>All lines joined with LF.</summary>
    public string Text => string.Join('\n', Lines);

    /// <summary>2yz: the command was accepted.</summary>
    public bool IsPositiveCompletion => Code is >= 200 and < 300;

    /// <summary>3yz: the server wants more (after DATA, or an AUTH challenge).</summary>
    public bool IsPositiveIntermediate => Code is >= 300 and < 400;

    /// <summary>4yz: a temporary failure; try again later.</summary>
    public bool IsTransientFailure => Code is >= 400 and < 500;

    /// <summary>5yz: a permanent failure.</summary>
    public bool IsPermanentFailure => Code is >= 500 and < 600;

    /// <inheritdoc />
    public override string ToString() => Code.ToString("D3", System.Globalization.CultureInfo.InvariantCulture) + " " + (Lines.Count > 0 ? Lines[0] : "");
}

/// <summary>Reads SMTP replies (RFC 5321 §4.2.1) from a <see cref="ProtocolReader"/>. Never throws on malformed content.</summary>
public sealed class SmtpReplyReader
{
    private readonly ProtocolReader _reader;

    /// <summary>Creates a reader.</summary>
    /// <param name="reader">Where lines come from.</param>
    public SmtpReplyReader(ProtocolReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
    }

    /// <summary>Reads one complete reply: lines <c>ddd-text</c> up to the line <c>ddd text</c> (or bare <c>ddd</c>).</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="MailConnectionClosedException">The connection ended inside the reply.</exception>
    /// <exception cref="MailProtocolLimitException">A line or the reply is too long.</exception>
    public ValueTask<SmtpReply> ReadReplyAsync(CancellationToken cancellationToken = default) => ReadCore(false, cancellationToken);

    /// <summary>Synchronous twin of <see cref="ReadReplyAsync"/>.</summary>
    /// <returns>The reply.</returns>
    public SmtpReply ReadReply() => SyncBridge.Run(ReadCore(true, default));

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<SmtpReply> ReadCore(bool sync, CancellationToken ct)
    {
        var lines = new List<string>(1);
        var defects = new List<ProtocolDefect>();
        int code = 0;
        bool haveCode = false;
        long total = 0;
        while (true)
        {
            ReadOnlyMemory<byte> raw = sync ? _reader.ReadLine() : await _reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (_reader.LastLineWasBareLf)
            {
                defects.Add(new ProtocolDefect(ProtocolDefectKind.BareLineFeed, lines.Count, "A reply line ended in LF without CR."));
            }

            total += raw.Length;
            if (lines.Count >= _reader.Limits.MaxReplyLines || total > _reader.Limits.MaxResponseSize)
            {
                throw new MailProtocolLimitException("A reply has more than " + _reader.Limits.MaxReplyLines + " lines.");
            }

            ReadOnlySpan<byte> line = raw.Span;
            bool lineHasCode = line.Length >= 3 && char.IsAsciiDigit((char)line[0]) && char.IsAsciiDigit((char)line[1]) && char.IsAsciiDigit((char)line[2]);
            bool last;
            if (lineHasCode)
            {
                int c = (line[0] - '0') * 100 + (line[1] - '0') * 10 + (line[2] - '0');
                if (!haveCode)
                {
                    code = c;
                    haveCode = true;
                }
                else if (c != code)
                {
                    defects.Add(new ProtocolDefect(ProtocolDefectKind.ReplyCodeChanged, lines.Count, "Reply code " + c + " in a reply that started with " + code + "."));
                }

                char sep = line.Length > 3 ? (char)line[3] : ' ';
                last = sep != '-';
                if (sep is not ('-' or ' '))
                {
                    defects.Add(new ProtocolDefect(ProtocolDefectKind.Malformed, 3, "A reply code must be followed by a space or a hyphen."));
                }

                lines.Add(Encoding.UTF8.GetString(line.Length > 4 ? line[(sep is '-' or ' ' ? 4 : 3)..] : []));
            }
            else
            {
                defects.Add(new ProtocolDefect(ProtocolDefectKind.ReplyLineWithoutCode, lines.Count, "A reply line without a code."));
                lines.Add(Encoding.UTF8.GetString(line));
                // A codeless first line is the whole reply (code 0); inside a multi-line reply it is one more line of text.
                last = !haveCode;
            }

            if (last)
            {
                break;
            }
        }

        return new SmtpReply(code, lines, EnhancedOf(lines), defects.Count == 0 ? [] : defects);
    }

    // RFC 2034 / RFC 3463 §2: "class.subject.detail" at the start of the text, class 2, 4 or 5.
    private static SmtpEnhancedStatus? EnhancedOf(List<string> lines)
    {
        if (lines.Count == 0)
        {
            return null;
        }

        string t = lines[0];
        int i = 0;
        int Num(int maxDigits)
        {
            int start = i;
            int v = 0;
            while (i < t.Length && char.IsAsciiDigit(t[i]) && i - start < maxDigits)
            {
                v = v * 10 + (t[i] - '0');
                i++;
            }

            return i == start ? -1 : v;
        }

        int cls = Num(1);
        if (cls is not (2 or 4 or 5) || i >= t.Length || t[i] != '.')
        {
            return null;
        }

        i++;
        int subject = Num(3);
        if (subject < 0 || i >= t.Length || t[i] != '.')
        {
            return null;
        }

        i++;
        int detail = Num(3);
        if (detail < 0 || (i < t.Length && t[i] != ' '))
        {
            return null;
        }

        return new SmtpEnhancedStatus(cls, subject, detail);
    }
}

/// <summary>The extensions a server announced in its EHLO reply (RFC 5321 §4.1.1.1), keywords with their parameters.</summary>
public sealed class SmtpCapabilities
{
    private readonly Dictionary<string, string[]> _keywords = new(StringComparer.OrdinalIgnoreCase);

    internal SmtpCapabilities(SmtpReply ehlo)
    {
        Greeting = ehlo.Lines.Count > 0 ? ehlo.Lines[0] : "";
        Domain = Greeting.Split(' ', 2)[0];
        for (int i = 1; i < ehlo.Lines.Count; i++)
        {
            string[] words = ehlo.Lines[i].Split([' ', '='], StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                continue;
            }

            // Old servers write "AUTH=PLAIN LOGIN" next to "AUTH PLAIN LOGIN"; both are merged under AUTH.
            _keywords[words[0]] = _keywords.TryGetValue(words[0], out string[]? existing) ? [.. existing, .. words[1..]] : words[1..];
        }
    }

    /// <summary>The first line of the reply: the server's domain and any greeting words.</summary>
    public string Greeting { get; }

    /// <summary>The server's domain (first word of <see cref="Greeting"/>).</summary>
    public string Domain { get; }

    /// <summary>The announced keywords, upper or lower case as sent.</summary>
    public IEnumerable<string> Keywords => _keywords.Keys;

    /// <summary>Whether the keyword was announced (ASCII case-insensitive).</summary>
    /// <param name="keyword">e.g. <c>PIPELINING</c>, <c>CHUNKING</c>, <c>STARTTLS</c>.</param>
    /// <returns>True if announced.</returns>
    public bool Has(string keyword) => _keywords.ContainsKey(keyword);

    /// <summary>The parameters announced with a keyword (empty if none or absent).</summary>
    /// <param name="keyword">The keyword.</param>
    /// <returns>The parameter words.</returns>
    public IReadOnlyList<string> Parameters(string keyword) => _keywords.TryGetValue(keyword, out string[]? p) ? p : [];

    /// <summary>The SASL mechanisms announced with AUTH, upper case.</summary>
    public IReadOnlyList<string> AuthMechanisms => [.. Parameters("AUTH").Select(m => m.ToUpperInvariant())];

    /// <summary>The SIZE limit in bytes, 0 for "no declared limit", or null if SIZE was not announced (RFC 1870).</summary>
    public long? MaxMessageSize => Has("SIZE") ? (Parameters("SIZE") is { Count: > 0 } p && long.TryParse(p[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long n) ? n : 0) : null;
}
