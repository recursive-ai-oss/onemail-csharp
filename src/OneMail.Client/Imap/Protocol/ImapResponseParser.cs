using System.Text;
using RecursiveAI.OneMail.Client.Protocol;
using static RecursiveAI.OneMail.Client.Imap.Protocol.ImapResponseReader;

namespace RecursiveAI.OneMail.Client.Imap.Protocol;

/// <summary>Parses one assembled response (its lines and literals) into an <see cref="ImapResponse"/>. Total: it never throws on content.</summary>
internal sealed class ImapResponseParser
{
    private readonly List<Segment> _segments;
    private readonly List<ProtocolDefect> _defects;
    private readonly ProtocolLimits _limits;
    private int _seg;
    private int _pos;
    private long _base;
    private int _items;

    public ImapResponseParser(List<Segment> segments, List<ProtocolDefect> defects, ProtocolLimits limits)
    {
        _segments = segments;
        _defects = defects;
        _limits = limits;
    }

    private byte[] Text => _segments[_seg].Text;

    private bool AtEndOfSegment => _pos >= Text.Length;

    private int Peek => _pos < Text.Length ? Text[_pos] : -1;

    private void Defect(ProtocolDefectKind kind, string message) =>
        _defects.Add(new ProtocolDefect(kind, (int)Math.Min(int.MaxValue, _base + _pos), message));

    private void NextSegment()
    {
        _base += Text.Length;
        _seg++;
        _pos = 0;
    }

    private void SkipSpaces()
    {
        while (_pos < Text.Length && Text[_pos] == (byte)' ')
        {
            _pos++;
        }
    }

    public ImapResponse Parse(Shape shape)
    {
        ImapResponse r;
        if (shape == Shape.Continuation)
        {
            r = new ImapResponse(ImapResponseKind.Continuation);
            _pos = 1;
            if (Peek == ' ')
            {
                _pos++;
            }

            r.Text = Decode(Text.AsSpan(_pos));
        }
        else if (shape == Shape.Tagged)
        {
            r = ParseTagged();
        }
        else
        {
            r = new ImapResponse(ImapResponseKind.Untagged);
            _pos = 2;
            ParseUntagged(r, shape);
        }

        r.Defects = _defects.Count == 0 ? [] : _defects.ToArray();
        return r;
    }

    private ImapResponse ParseTagged()
    {
        var r = new ImapResponse(ImapResponseKind.Tagged);
        string tag = ReadWord();
        r.Tag = tag;
        SkipSpaces();
        string word = ReadWord();
        r.Name = word.ToUpperInvariant();
        r.Status = StatusOf(r.Name);
        if (r.Status is ImapStatus.None or ImapStatus.PreAuth or ImapStatus.Bye)
        {
            Defect(ProtocolDefectKind.Malformed, "A tagged response without OK, NO or BAD.");
            r.Status = ImapStatus.None;
        }

        ParseStatusText(r);
        return r;
    }

    private void ParseUntagged(ImapResponse r, Shape shape)
    {
        SkipSpaces();
        string word = ReadWord();
        if (word.Length > 0 && long.TryParse(word, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long n))
        {
            r.Number = n;
            SkipSpaces();
            word = ReadWord();
        }

        r.Name = word.ToUpperInvariant();
        r.Status = r.Number is null ? StatusOf(r.Name) : ImapStatus.None;
        if (r.Status != ImapStatus.None)
        {
            ParseStatusText(r);
            return;
        }

        var data = new List<ImapValue>();
        while (true)
        {
            SkipSpaces();
            if (AtEndOfSegment && _segments[_seg].Literal is null)
            {
                break;
            }

            data.Add(ParseValue(0, false));
            if (data.Count > _limits.MaxItems)
            {
                throw new MailProtocolLimitException("A response has more than " + _limits.MaxItems + " items.");
            }
        }

        r.Data = data.Count == 0 ? ImapValue.NoItems : data;
        _ = shape;
    }

    private static ImapStatus StatusOf(string upper) => upper switch
    {
        "OK" => ImapStatus.Ok,
        "NO" => ImapStatus.No,
        "BAD" => ImapStatus.Bad,
        "PREAUTH" => ImapStatus.PreAuth,
        "BYE" => ImapStatus.Bye,
        _ => ImapStatus.None,
    };

    // resp-text = ["[" resp-text-code "]" SP] text (RFC 9051 §9)
    private void ParseStatusText(ImapResponse r)
    {
        SkipSpaces();
        if (Peek == '[')
        {
            int close = IndexOfCodeEnd();
            if (close > 0)
            {
                int start = _pos + 1;
                string raw = Decode(Text.AsSpan(start, close - start));
                _pos = start;
                r.Code = ParseCode(raw, close);
                _pos = close + 1;
                SkipSpaces();
            }
            else
            {
                Defect(ProtocolDefectKind.Malformed, "A response code without a closing bracket.");
            }
        }

        r.Text = Decode(Text.AsSpan(Math.Min(_pos, Text.Length)));
    }

    // The code ends at the first "]" outside quotes and parentheses.
    private int IndexOfCodeEnd()
    {
        byte[] t = Text;
        bool quoted = false;
        int depth = 0;
        for (int i = _pos + 1; i < t.Length; i++)
        {
            byte c = t[i];
            if (quoted)
            {
                if (c == (byte)'\\') { i++; }
                else if (c == (byte)'"') { quoted = false; }
            }
            else if (c == (byte)'"') { quoted = true; }
            else if (c == (byte)'(') { depth++; }
            else if (c == (byte)')') { depth--; }
            else if (c == (byte)']' && depth <= 0) { return i; }
        }

        return -1;
    }

    private ImapResponseCode ParseCode(string raw, int close)
    {
        string name = ReadWord(codeMode: true);
        var args = new List<ImapValue>();
        while (true)
        {
            SkipSpaces();
            if (_pos >= close)
            {
                break;
            }

            int before = _pos;
            args.Add(ParseValue(0, true));
            if (_pos <= before)
            {
                _pos = close;
                break;
            }
        }

        return new ImapResponseCode(name.ToUpperInvariant(), args.Count == 0 ? ImapValue.NoItems : args, raw);
    }

    private string ReadWord(bool codeMode = false)
    {
        int start = _pos;
        byte[] t = Text;
        while (_pos < t.Length && t[_pos] != (byte)' ' && !(codeMode && t[_pos] == (byte)']'))
        {
            _pos++;
        }

        return Encoding.UTF8.GetString(t, start, _pos - start);
    }

    private static string Decode(ReadOnlySpan<byte> s) => Encoding.UTF8.GetString(s);

    private ImapValue ParseValue(int depth, bool inCode)
    {
        if (++_items > _limits.MaxItems)
        {
            throw new MailProtocolLimitException("A response has more than " + _limits.MaxItems + " items.");
        }

        if (AtEndOfSegment)
        {
            LiteralData? lit = _segments[_seg].Literal;
            if (lit is not null)
            {
                NextSegment();
                return new ImapValue(ImapValueKind.Literal, lit.Data ?? default, ImapValue.NoItems)
                {
                    LiteralLength = lit.Length,
                    IsStreamed = lit.Streamed,
                    IsSkipped = lit.Skipped,
                };
            }

            return ImapValue.Nil;
        }

        byte c = Text[_pos];
        switch (c)
        {
            case (byte)'(':
                return ParseList(depth, inCode);
            case (byte)'"':
                return ParseQuoted();
            default:
                return ParseAtom(depth, inCode);
        }
    }

    private ImapValue ParseList(int depth, bool inCode)
    {
        if (depth >= _limits.MaxNesting)
        {
            Defect(ProtocolDefectKind.NestingTooDeep, "Lists nest deeper than " + _limits.MaxNesting + ".");
            // Keep the rest of the line raw so parsing stops cleanly.
            var raw = new ImapValue(ImapValueKind.Atom, Text.AsMemory(_pos), ImapValue.NoItems);
            _pos = Text.Length;
            return raw;
        }

        _pos++; // (
        var items = new List<ImapValue>();
        while (true)
        {
            SkipSpaces();
            if (AtEndOfSegment && _segments[_seg].Literal is null)
            {
                Defect(ProtocolDefectKind.UnterminatedList, "A list was not closed.");
                break;
            }

            if (!AtEndOfSegment && Text[_pos] == (byte)')')
            {
                _pos++;
                break;
            }

            int seg = _seg;
            int pos = _pos;
            items.Add(ParseValue(depth + 1, inCode));
            if (_seg == seg && _pos == pos)
            {
                // No progress (a stray byte); take it as an atom so the loop ends.
                _pos++;
            }
        }

        return new ImapValue(ImapValueKind.List, default, items);
    }

    private ImapValue ParseQuoted()
    {
        byte[] t = Text;
        _pos++; // opening quote
        int start = _pos;
        bool escaped = false;
        while (_pos < t.Length)
        {
            byte c = t[_pos];
            if (c == (byte)'\\' && _pos + 1 < t.Length)
            {
                escaped = true;
                _pos += 2;
                continue;
            }

            if (c == (byte)'"')
            {
                break;
            }

            _pos++;
        }

        ReadOnlyMemory<byte> body = t.AsMemory(start, Math.Min(_pos, t.Length) - start);
        if (_pos >= t.Length)
        {
            Defect(ProtocolDefectKind.UnterminatedQuoted, "A quoted string was not closed.");
        }
        else
        {
            _pos++; // closing quote
        }

        if (escaped)
        {
            // RFC 9051 §9: quoted-specials are DQUOTE and backslash; "\x" is x.
            var sb = new byte[body.Length];
            int n = 0;
            ReadOnlySpan<byte> s = body.Span;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == (byte)'\\' && i + 1 < s.Length)
                {
                    i++;
                }

                sb[n++] = s[i];
            }

            body = sb.AsMemory(0, n);
        }

        return new ImapValue(ImapValueKind.Quoted, body, ImapValue.NoItems);
    }

    private ImapValue ParseAtom(int depth, bool inCode)
    {
        byte[] t = Text;
        int start = _pos;
        while (_pos < t.Length)
        {
            byte c = t[_pos];
            if (c == (byte)' ' || c == (byte)'(' || c == (byte)')' || c == (byte)'"' || (inCode && c == (byte)']'))
            {
                break;
            }

            if (c == (byte)'[')
            {
                // A section specifier (BODY[HEADER.FIELDS (FROM TO)]<0>) is part of the atom, spaces and all.
                int end = FindSectionEnd(t, _pos);
                if (end > 0)
                {
                    _pos = end + 1;
                    continue;
                }
            }

            _pos++;
        }

        if (_pos == start)
        {
            // A byte that cannot start an atom here (stray ")" or "]"): take it so the caller makes progress.
            Defect(ProtocolDefectKind.Malformed, "Unexpected byte " + t[_pos] + ".");
            _pos++;
        }

        ReadOnlyMemory<byte> bytes = t.AsMemory(start, _pos - start);
        if (bytes.Length == 3 && Ascii.EqualsIgnoreCase(bytes.Span, "NIL"u8))
        {
            return ImapValue.Nil;
        }

        _ = depth;
        return new ImapValue(ImapValueKind.Atom, bytes, ImapValue.NoItems);
    }

    private static int FindSectionEnd(byte[] t, int open)
    {
        bool quoted = false;
        int depth = 0;
        for (int i = open + 1; i < t.Length; i++)
        {
            byte c = t[i];
            if (quoted)
            {
                if (c == (byte)'\\') { i++; }
                else if (c == (byte)'"') { quoted = false; }
            }
            else if (c == (byte)'"') { quoted = true; }
            else if (c == (byte)'(') { depth++; }
            else if (c == (byte)')') { depth--; }
            else if (c == (byte)']' && depth <= 0) { return i; }
        }

        return -1;
    }
}
