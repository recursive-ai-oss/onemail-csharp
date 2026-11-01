using System.Runtime.CompilerServices;
using System.Text;
using RecursiveAI.OneMail.Client.Protocol;

namespace RecursiveAI.OneMail.Client.Imap.Protocol;

/// <summary>
/// Reads IMAP responses (RFC 9051 §7, formal syntax §9) from a <see cref="ProtocolReader"/> into <see cref="ImapResponse"/> trees.
/// </summary>
/// <remarks>
/// <para>A response is a line plus any literals announced at the end of a line (<c>{n}</c> then n bytes then the rest of the
/// response on the next line). The reader assembles all of it, then parses. FETCH literals can be streamed to a handler on the
/// way instead of buffered, which is how a 100 MB <c>BODY[]</c> reaches <c>MimeReader</c> in constant memory.</para>
/// <para>Parsing never throws on malformed content: the response carries <see cref="ImapResponse.Defects"/> and as much of the
/// tree as could be read. Limits (<see cref="ProtocolLimits"/>) and a closed connection do throw.</para>
/// </remarks>
public sealed class ImapResponseReader
{
    private readonly ProtocolReader _reader;
    private readonly ImapFetchFast.StringCache _cache = new();

    /// <summary>Creates a reader over the connection's reader.</summary>
    /// <param name="reader">Where lines come from.</param>
    public ImapResponseReader(ProtocolReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
    }

    /// <summary>Reads the next complete response.</summary>
    /// <param name="literalHandler">Receives FETCH literals as streams, or null to buffer them (up to <see cref="ProtocolLimits.MaxBufferedLiteral"/>).</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The response.</returns>
    /// <exception cref="MailConnectionClosedException">The connection ended inside a response.</exception>
    /// <exception cref="MailProtocolLimitException">A limit was exceeded.</exception>
    public ValueTask<ImapResponse> ReadResponseAsync(ImapLiteralHandler? literalHandler = null, CancellationToken cancellationToken = default) =>
        ReadCore(false, literalHandler, null, cancellationToken);

    /// <summary>Synchronous twin of <see cref="ReadResponseAsync"/>.</summary>
    /// <param name="literalHandler">Receives FETCH literals as streams, or null to buffer them.</param>
    /// <returns>The response.</returns>
    public ImapResponse ReadResponse(ImapLiteralHandlerSync? literalHandler = null) =>
        SyncBridge.Run(ReadCore(true, null, literalHandler, default));

    internal enum Shape { Tagged, Continuation, Status, Data, Fetch }

    /// <summary>
    /// Reads the next response for a FETCH enumeration. A plain single-line <c>n FETCH (...)</c> of the items the typed model
    /// knows is decoded straight from the line into the second result with no generic tree; anything else (a literal, a
    /// BODYSTRUCTURE, an unknown item, a malformed line) is parsed as <see cref="ReadResponseAsync"/> would, so the result is the same.
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    internal async ValueTask<(ImapResponse? Response, ImapFetchData? Fetch)> ReadFetchAsync(CancellationToken ct)
    {
        ReadOnlyMemory<byte> line = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
        if (!_reader.LastLineWasBareLf && line.Length <= _reader.Limits.MaxResponseSize
            && Sniff(line.Span, out long number) == Shape.Fetch && !TryFindLiteralMarker(line.Span, out _, out _, out _)
            && ImapFetchFast.TryParse(line.Span, number, _cache, out ImapFetchData? data))
        {
            return (null, data);
        }

        return (await ReadCore(false, null, null, ct, line).ConfigureAwait(false), null);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<ImapResponse> ReadCore(bool sync, ImapLiteralHandler? asyncHandler, ImapLiteralHandlerSync? syncHandler, CancellationToken ct, ReadOnlyMemory<byte>? preRead = null)
    {
        ProtocolLimits limits = _reader.Limits;
        var segments = new List<Segment>(2);
        var defects = new List<ProtocolDefect>();
        long total = 0;
        Shape shape = Shape.Tagged;
        string firstLine = "";
        long messageNumber = 0;
        bool first = true;
        while (true)
        {
            ReadOnlyMemory<byte> line = preRead ?? (sync ? _reader.ReadLine() : await _reader.ReadLineAsync(ct).ConfigureAwait(false));
            preRead = null;
            if (_reader.LastLineWasBareLf)
            {
                defects.Add(new ProtocolDefect(ProtocolDefectKind.BareLineFeed, (int)Math.Min(total, int.MaxValue), "A line ended in LF without CR."));
            }

            total += line.Length;
            if (total > limits.MaxResponseSize)
            {
                throw new MailProtocolLimitException("A response is longer than " + limits.MaxResponseSize + " bytes.");
            }

            if (first)
            {
                shape = Sniff(line.Span, out messageNumber);
                firstLine = Encoding.UTF8.GetString(line.Span);
                first = false;
            }

            if (shape is Shape.Data or Shape.Fetch && TryFindLiteralMarker(line.Span, out long size, out int markerAt, out bool literal8))
            {
                if (size > limits.MaxLiteralSize)
                {
                    throw new MailProtocolLimitException("A literal announces " + size + " bytes; the limit is " + limits.MaxLiteralSize + ".");
                }

                byte[] text = line.Span[..markerAt].ToArray();
                var lit = new LiteralData { Length = size, Literal8 = literal8 };
                bool handled = false;
                if (shape == Shape.Fetch && (asyncHandler is not null || syncHandler is not null) && TryItemName(text, out string? item))
                {
                    ProtocolReader.LiteralStream stream = _reader.OpenLiteral(size);
                    var context = new ImapLiteralContext(messageNumber, item, size);
                    handled = asyncHandler is not null
                        ? await asyncHandler(context, stream, ct).ConfigureAwait(false)
                        : syncHandler!(context, stream);
                    if (handled || stream.Remaining != size)
                    {
                        // Whatever the handler left unread is discarded so the next line starts in the right place.
                        if (sync)
                        {
                            stream.Skip();
                        }
                        else
                        {
                            await stream.SkipAsync(ct).ConfigureAwait(false);
                        }

                        lit.Streamed = true;
                        handled = true;
                    }
                }

                if (!handled)
                {
                    if (size <= limits.MaxBufferedLiteral && total + size <= limits.MaxResponseSize)
                    {
                        var data = new byte[size];
                        int got = 0;
                        while (got < data.Length)
                        {
                            int n = sync ? _reader.Read(data.AsSpan(got)) : await _reader.ReadAsync(data.AsMemory(got), ct).ConfigureAwait(false);
                            if (n == 0)
                            {
                                throw new MailConnectionClosedException("The connection was closed in the middle of a literal.");
                            }

                            got += n;
                        }

                        lit.Data = data;
                        total += size;
                    }
                    else
                    {
                        if (sync)
                        {
                            _reader.Skip(size);
                        }
                        else
                        {
                            await _reader.SkipAsync(size, ct).ConfigureAwait(false);
                        }

                        lit.Skipped = true;
                        defects.Add(new ProtocolDefect(ProtocolDefectKind.LiteralSkipped, (int)Math.Min(total, int.MaxValue), "A literal of " + size + " bytes was not buffered."));
                    }
                }

                segments.Add(new Segment(text, lit));
                if (segments.Count > limits.MaxItems)
                {
                    throw new MailProtocolLimitException("A response has more than " + limits.MaxItems + " literals.");
                }

                continue;
            }

            segments.Add(new Segment(line.ToArray(), null));
            break;
        }

        var parser = new ImapResponseParser(segments, defects, limits);
        ImapResponse response = parser.Parse(shape);
        response.FirstLine = firstLine;
        return response;
    }

    // The first line decides what the response can contain: only untagged data can carry literals (status text is free text).
    private static Shape Sniff(ReadOnlySpan<byte> line, out long messageNumber)
    {
        messageNumber = 0;
        if (line.Length > 0 && line[0] == (byte)'+')
        {
            return Shape.Continuation;
        }

        if (line.Length < 2 || line[0] != (byte)'*' || line[1] != (byte)' ')
        {
            return Shape.Tagged;
        }

        ReadOnlySpan<byte> rest = line[2..];
        int end = rest.IndexOf((byte)' ');
        ReadOnlySpan<byte> word = end < 0 ? rest : rest[..end];
        if (Ascii.EqualsIgnoreCase(word, "OK"u8) || Ascii.EqualsIgnoreCase(word, "NO"u8) || Ascii.EqualsIgnoreCase(word, "BAD"u8)
            || Ascii.EqualsIgnoreCase(word, "PREAUTH"u8) || Ascii.EqualsIgnoreCase(word, "BYE"u8))
        {
            return Shape.Status;
        }

        if (end > 0 && word.Length is > 0 and < 19 && System.Buffers.Text.Utf8Parser.TryParse(word, out long number, out int used) && used == word.Length)
        {
            messageNumber = number;
            ReadOnlySpan<byte> after = rest[(end + 1)..];
            if (after.Length >= 5 && Ascii.EqualsIgnoreCase(after[..5], "FETCH"u8) && (after.Length == 5 || after[5] == (byte)' '))
            {
                return Shape.Fetch;
            }
        }

        return Shape.Data;
    }

    // RFC 9051 §4.3: a literal is "{" number "}" CRLF, at the very end of the line; RFC 7888 adds "+" and "-"; RFC 3516 adds "~".
    internal static bool TryFindLiteralMarker(ReadOnlySpan<byte> line, out long size, out int markerStart, out bool literal8)
    {
        size = 0;
        markerStart = 0;
        literal8 = false;
        if (line.Length < 3 || line[^1] != (byte)'}')
        {
            return false;
        }

        int open = line.LastIndexOf((byte)'{');
        if (open < 0)
        {
            return false;
        }

        ReadOnlySpan<byte> digits = line[(open + 1)..^1];
        if (digits.Length > 0 && (digits[^1] == (byte)'+' || digits[^1] == (byte)'-'))
        {
            digits = digits[..^1];
        }

        if (digits.Length is 0 or > 18 || !System.Buffers.Text.Utf8Parser.TryParse(digits, out size, out int used) || used != digits.Length || size < 0)
        {
            return false;
        }

        markerStart = open;
        if (open > 0 && line[open - 1] == (byte)'~')
        {
            markerStart = open - 1;
            literal8 = true;
        }

        return true;
    }

    // The FETCH data item a literal belongs to: the token before the marker, or the whole BODY[...]/BINARY[...] with its section.
    private static bool TryItemName(ReadOnlySpan<byte> text, out string item)
    {
        item = "";
        if (text.Length == 0 || text[^1] != (byte)' ')
        {
            return false;
        }

        ReadOnlySpan<byte> t = text[..^1];
        int start;
        if (t.Length > 0 && (t[^1] == (byte)']' || t[^1] == (byte)'>'))
        {
            int br = t.LastIndexOf((byte)'[');
            if (br < 0)
            {
                return false;
            }

            start = br;
            while (start > 0 && t[start - 1] is not ((byte)' ' or (byte)'(')) { start--; }
        }
        else
        {
            start = t.Length;
            while (start > 0 && t[start - 1] is not ((byte)' ' or (byte)'(')) { start--; }
        }

        if (start == t.Length)
        {
            return false;
        }

        item = Encoding.ASCII.GetString(t[start..]);
        return true;
    }

    internal sealed class LiteralData
    {
        public long Length;
        public byte[]? Data;
        public bool Streamed;
        public bool Skipped;
        public bool Literal8;
    }

    internal readonly record struct Segment(byte[] Text, LiteralData? Literal);
}
