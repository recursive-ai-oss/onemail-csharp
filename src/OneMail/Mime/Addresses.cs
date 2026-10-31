using System.Text;
using RecursiveAI.OneMail.Mime.Reading;
using RecursiveAI.OneMail.Text;

namespace RecursiveAI.OneMail.Mime;

/// <summary>An entry of an address list: a <see cref="MimeMailbox"/> or a <see cref="MimeGroup"/>.</summary>
public abstract class MimeAddress
{
    internal MimeAddress()
    {
    }

    /// <summary>The display name (decoded), or null when there is none. For a group, the group name.</summary>
    public string? Name { get; internal set; }
}

/// <summary>A mailbox: an optional display name and an address.</summary>
public sealed class MimeMailbox : MimeAddress
{
    internal MimeMailbox(string? name, string address)
    {
        Name = name;
        Address = address;
    }

    /// <summary>
    /// The address as written, without comments or white space: <c>local@domain</c>. A quoted local part keeps its quotes
    /// (<c>"a b"@x.org</c>). It may have no domain, or be empty; the matching defects say so.
    /// </summary>
    public string Address { get; }

    /// <summary>The address with the quoting of the local part removed.</summary>
    public string UnquotedAddress
    {
        get
        {
            int at = Address.LastIndexOf('@');
            if (at > 1 && Address[0] == '"' && Address[at - 1] == '"')
            {
                var sb = new StringBuilder();
                for (int i = 1; i < at - 1; i++)
                {
                    if (Address[i] == '\\' && i + 1 < at - 1)
                    {
                        i++;
                    }

                    sb.Append(Address[i]);
                }

                return sb.Append(Address, at, Address.Length - at).ToString();
            }

            return Address;
        }
    }

    /// <summary>The part before the last <c>@</c>, or the whole address when there is no <c>@</c>.</summary>
    public string LocalPart
    {
        get
        {
            int at = Address.LastIndexOf('@');
            return at < 0 ? Address : Address[..at];
        }
    }

    /// <summary>The part after the last <c>@</c>, or empty.</summary>
    public string Domain
    {
        get
        {
            int at = Address.LastIndexOf('@');
            return at < 0 ? string.Empty : Address[(at + 1)..];
        }
    }

    /// <inheritdoc/>
    public override string ToString() => Name is null ? Address : Name + " <" + Address + ">";
}

/// <summary>A group: a name and the mailboxes listed in it (RFC 5322 §3.4). The list may be empty (<c>undisclosed-recipients:;</c>).</summary>
public sealed class MimeGroup : MimeAddress
{
    internal MimeGroup(string? name, List<MimeMailbox> members)
    {
        Name = name;
        Members = members;
    }

    /// <summary>The members, in order.</summary>
    public IReadOnlyList<MimeMailbox> Members { get; }
}

/// <summary>
/// Parses address lists (RFC 5322 §3.4, with the obsolete forms of §4.4 and the usual damage found in real mail) from raw
/// header bytes. It never throws; every repair or guess is a defect with an offset. The resolutions are in
/// <c>docs/ambiguities.md</c> (entries A1–A15).
/// </summary>
public static class AddressParser
{
    /// <summary>Parses an address list.</summary>
    /// <param name="rawValue">The header value as written (folds allowed).</param>
    /// <param name="defects">Receives the defects; may be null.</param>
    /// <param name="baseOffset">Added to defect offsets: the input offset of <paramref name="rawValue"/>.</param>
    /// <param name="depth">The entity depth to record in defects.</param>
    /// <returns>The mailboxes and groups in order.</returns>
    public static List<MimeAddress> Parse(ReadOnlySpan<byte> rawValue, List<MimeDefect>? defects = null, long baseOffset = 0, int depth = 0)
    {
        var p = new Parser(rawValue, defects, baseOffset, depth);
        return p.ParseList(false);
    }

    /// <summary>All mailboxes of a list, with group members in place of their group.</summary>
    /// <param name="list">A parsed list.</param>
    /// <returns>The mailboxes.</returns>
    public static List<MimeMailbox> Flatten(IEnumerable<MimeAddress> list)
    {
        var r = new List<MimeMailbox>();
        foreach (MimeAddress a in list)
        {
            if (a is MimeMailbox m)
            {
                r.Add(m);
            }
            else if (a is MimeGroup g)
            {
                r.AddRange(g.Members);
            }
        }

        return r;
    }

    private enum K : byte
    {
        End,
        Atom,
        Quoted,
        Comment,
        Dot,
        At,
        Lt,
        Gt,
        Comma,
        Colon,
        Semi,
        Literal,
    }

    private readonly record struct Tok(K Kind, int Start, int End, bool SpaceBefore, bool Unterminated);

    private sealed class Parser
    {
        private readonly byte[] _b;
        private readonly int[]? _map;
        private readonly List<MimeDefect>? _defects;
        private readonly long _base;
        private readonly int _depth;
        private int _p;
        private bool _stop;

        public Parser(ReadOnlySpan<byte> raw, List<MimeDefect>? defects, long baseOffset, int depth)
        {
            _defects = defects;
            _base = baseOffset;
            _depth = depth;
            if (raw.IndexOfAny((byte)'\r', (byte)'\n') < 0)
            {
                _b = raw.ToArray();
            }
            else
            {
                // RFC 5322 §2.2.3: unfolding removes the line breaks. A lone CR is data and is dropped with them.
                var bytes = new List<byte>(raw.Length);
                var map = new List<int>(raw.Length);
                for (int i = 0; i < raw.Length; i++)
                {
                    if (raw[i] is not ((byte)'\r' or (byte)'\n'))
                    {
                        bytes.Add(raw[i]);
                        map.Add(i);
                    }
                }

                _b = [.. bytes];
                _map = [.. map];
            }
        }

        private void Defect(MimeDefectKind kind, int index, int length = 0)
        {
            int raw = _map is null ? index : (index < _map.Length ? _map[index] : (_map.Length == 0 ? 0 : _map[^1] + 1));
            _defects?.Add(new MimeDefect(kind, _base + raw, length, _depth));
        }

        private static bool IsWsp(byte c) => c is (byte)' ' or (byte)'\t';

        private static bool IsSpecial(byte c) => c is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']'
            or (byte)':' or (byte)';' or (byte)'@' or (byte)',' or (byte)'.' or (byte)'"' || IsWsp(c);

        // ---- tokens ----

        private Tok Next(ref int p)
        {
            bool space = false;
            while (p < _b.Length && IsWsp(_b[p]))
            {
                p++;
                space = true;
            }

            if (p >= _b.Length)
            {
                return new Tok(K.End, p, p, space, false);
            }

            int s = p;
            byte c = _b[p];
            switch (c)
            {
                case (byte)'"':
                {
                    p++;
                    bool closes = HasClosingQuote(p);
                    while (p < _b.Length && _b[p] != (byte)'"')
                    {
                        if (_b[p] == (byte)'\\' && p + 1 < _b.Length)
                        {
                            p++;
                        }
                        else if (!closes && _b[p] == (byte)'<')
                        {
                            // An unclosed quote ends where the angle address starts (ambiguity A11).
                            Defect(MimeDefectKind.UnterminatedQuote, s);
                            return new Tok(K.Quoted, s, p, space, true);
                        }

                        p++;
                    }

                    if (p >= _b.Length)
                    {
                        Defect(MimeDefectKind.UnterminatedQuote, s);
                        return new Tok(K.Quoted, s, p, space, true);
                    }

                    p++;
                    return new Tok(K.Quoted, s, p, space, false);
                }

                case (byte)'(':
                {
                    int depth = 0;
                    while (p < _b.Length)
                    {
                        byte x = _b[p++];
                        if (x == (byte)'\\' && p < _b.Length)
                        {
                            p++;
                        }
                        else if (x == (byte)'(')
                        {
                            depth++;
                        }
                        else if (x == (byte)')' && --depth == 0)
                        {
                            return new Tok(K.Comment, s, p, space, false);
                        }
                    }

                    return new Tok(K.Comment, s, p, space, true);
                }

                case (byte)'[':
                {
                    while (p < _b.Length && _b[p] != (byte)']')
                    {
                        p++;
                    }

                    bool open = p >= _b.Length;
                    if (!open)
                    {
                        p++;
                    }

                    return new Tok(K.Literal, s, p, space, open);
                }
            }

            p++;
            K single = c switch
            {
                (byte)'.' => K.Dot,
                (byte)'@' => K.At,
                (byte)'<' => K.Lt,
                (byte)'>' => K.Gt,
                (byte)',' => K.Comma,
                (byte)':' => K.Colon,
                (byte)';' => K.Semi,
                _ => K.Atom,
            };
            if (single != K.Atom && c != (byte)')' && c != (byte)']')
            {
                return new Tok(single, s, p, space, false);
            }

            while (p < _b.Length && !IsSpecial(_b[p]))
            {
                p++;
            }

            return new Tok(K.Atom, s, p, space, false);
        }

        private bool HasClosingQuote(int from)
        {
            for (int j = from; j < _b.Length; j++)
            {
                if (_b[j] == (byte)'\\')
                {
                    j++;
                }
                else if (_b[j] == (byte)'"')
                {
                    return true;
                }
            }

            return false;
        }

        // Scans ahead (without consuming) over the rest of the current comma-separated segment.
        private (bool angle, bool at) Lookahead(int from)
        {
            int p = from;
            bool angle = false;
            bool at = false;
            while (true)
            {
                Tok t = Next(ref p);
                if (t.Kind is K.End or K.Comma or K.Semi)
                {
                    return (angle, at);
                }

                if (t.Kind == K.Lt)
                {
                    angle = true;
                    return (angle, at);
                }

                if (t.Kind == K.At)
                {
                    at = true;
                }
            }
        }

        // ---- grammar ----

        public List<MimeAddress> ParseList(bool inGroup, List<MimeMailbox>? groupMembers = null)
        {
            var result = new List<MimeAddress>();
            while (!_stop)
            {
                int save = _p;
                Tok t = Next(ref _p);
                if (t.Kind == K.End)
                {
                    break;
                }

                if (t.Kind == K.Comma)
                {
                    continue; // empty list elements are skipped (RFC 5322 §4.4 obs-addr-list)
                }

                if (t.Kind == K.Semi)
                {
                    if (inGroup)
                    {
                        _p = save;
                        break;
                    }

                    // A semicolon outside a group ends what is understood; the rest is kept as unparsed text (ambiguity A14).
                    Defect(MimeDefectKind.UnparsedAddressText, t.Start, _b.Length - t.Start);
                    _stop = true;
                    break;
                }

                _p = save;
                ParseElement(inGroup, result, groupMembers);
            }

            return result;
        }

        private void ParseElement(bool inGroup, List<MimeAddress> result, List<MimeMailbox>? groupMembers)
        {
            var words = new List<Tok>();
            var comments = new List<Tok>();
            bool sawAt = false;
            int domainStart = -1;
            while (true)
            {
                int save = _p;
                Tok t = Next(ref _p);
                switch (t.Kind)
                {
                    case K.End:
                        Finish(words, comments, sawAt, result, groupMembers);
                        return;

                    case K.Comma:
                        if (!sawAt && words.Count > 0 && Merges(words, _p))
                        {
                            // "Doe, John <j@x>": the comma belongs to the display name (ambiguity A2).
                            Defect(MimeDefectKind.AmbiguousUnquotedCommaInPhrase, t.Start);
                            words.Add(t with { Kind = K.Atom });
                            continue;
                        }

                        Finish(words, comments, sawAt, result, groupMembers);
                        return;

                    case K.Semi:
                        _p = save;
                        Finish(words, comments, sawAt, result, groupMembers);
                        return;

                    case K.Colon:
                        if (!inGroup && !sawAt && words.Count > 0)
                        {
                            ParseGroup(words, result);
                            return;
                        }

                        words.Add(t with { Kind = K.Atom });
                        break;

                    case K.Comment:
                        comments.Add(t);
                        break;

                    case K.Lt:
                        ParseAngle(words, comments, t, result, groupMembers);
                        return;

                    case K.At:
                        if (!sawAt)
                        {
                            (bool angle, _) = Lookahead(_p);
                            if (angle)
                            {
                                // "Bob@Work <bob@x>": the '@' is part of the display name (ambiguity A12).
                                words.Add(t with { Kind = K.Atom });
                                break;
                            }

                            sawAt = true;
                            domainStart = words.Count;
                        }

                        words.Add(t);
                        break;

                    case K.Gt:
                        Defect(MimeDefectKind.InvalidAddressSyntax, t.Start, 1);
                        break;

                    default:
                        if (sawAt && words.Count > domainStart + 1 && t.SpaceBefore && words[^1].Kind is not (K.Dot or K.At) && t.Kind is not K.Dot)
                        {
                            // A second address-like word after a complete addr-spec ("a@x b@y"): it starts a new mailbox.
                            _p = save;
                            Finish(words, comments, sawAt, result, groupMembers);
                            return;
                        }

                        words.Add(t);
                        break;
                }
            }
        }

        // Whether the segment after a comma completes a "Name, Name <addr>" display name.
        private bool Merges(List<Tok> words, int from)
        {
            (bool angle, bool at) = Lookahead(from);
            return angle && !at;
        }

        private void ParseGroup(List<Tok> nameWords, List<MimeAddress> result)
        {
            string? name = PhraseText(nameWords);
            var members = new List<MimeMailbox>();
            List<MimeAddress> inner = ParseList(true, members);
            foreach (MimeAddress a in inner)
            {
                if (a is MimeMailbox m)
                {
                    members.Add(m);
                }
            }

            int save = _p;
            Tok t = Next(ref _p);
            if (t.Kind != K.Semi)
            {
                _p = save;
            }

            result.Add(new MimeGroup(name, members));
            int save2 = _p;
            Tok c = Next(ref _p);
            if (c.Kind != K.Comma)
            {
                _p = save2;
            }
        }

        private void ParseAngle(List<Tok> nameWords, List<Tok> comments, Tok lt, List<MimeAddress> result, List<MimeMailbox>? groupMembers)
        {
            string? name = PhraseText(nameWords) ?? CommentName(comments);
            Tok open = lt;
            while (true)
            {
                var parts = new List<Tok>();
                bool closed = false;
                bool first = true;
                while (true)
                {
                    int save = _p;
                    Tok t = Next(ref _p);
                    if (t.Kind == K.End)
                    {
                        break;
                    }

                    if (t.Kind == K.Gt)
                    {
                        closed = true;
                        break;
                    }

                    if (t.Kind is K.Comma or K.Semi && !first || t.Kind is K.Lt)
                    {
                        _p = save;
                        break;
                    }

                    if (t.Kind == K.Comment)
                    {
                        comments.Add(t);
                        continue;
                    }

                    if (first && t.Kind == K.At)
                    {
                        // RFC 5322 §4.4 obs-route: "@a.example,@b.example:" before the real address; it is dropped.
                        Defect(MimeDefectKind.ObsoleteRoute, t.Start);
                        while (true)
                        {
                            int s2 = _p;
                            Tok r = Next(ref _p);
                            if (r.Kind is K.End or K.Gt)
                            {
                                _p = s2;
                                break;
                            }

                            if (r.Kind == K.Colon)
                            {
                                break;
                            }
                        }

                        first = false;
                        continue;
                    }

                    first = false;
                    parts.Add(t);
                }

                if (!closed)
                {
                    Defect(MimeDefectKind.UnclosedAngle, open.Start);
                }

                AddMailbox(name, AddressText(parts), open.Start, result, groupMembers);

                // "<a@x><b@y>": a second angle address directly after the first is a second mailbox (ambiguity A13).
                int save3 = _p;
                Tok n = Next(ref _p);
                while (n.Kind == K.Comment)
                {
                    comments.Add(n);
                    save3 = _p;
                    n = Next(ref _p);
                }

                if (n.Kind == K.Lt && closed)
                {
                    Defect(MimeDefectKind.AmbiguousAdjacentAngleAddrs, n.Start);
                    open = n;
                    continue;
                }

                if (n.Kind != K.Comma)
                {
                    _p = save3;
                }

                return;
            }
        }

        private void Finish(List<Tok> words, List<Tok> comments, bool sawAt, List<MimeAddress> result, List<MimeMailbox>? groupMembers)
        {
            if (words.Count == 0)
            {
                if (comments.Count > 0)
                {
                    Defect(MimeDefectKind.InvalidAddressSyntax, comments[0].Start);
                }

                return;
            }

            if (!sawAt)
            {
                // No '@' and no angle brackets: a bare local part (ambiguity A8), e.g. "mary".
                AddMailbox(CommentName(comments), AddressText(words), words[0].Start, result, groupMembers);
                return;
            }

            int at = words.FindIndex(w => w.Kind == K.At);
            int localStart = at;
            while (localStart > 0)
            {
                Tok prev = words[localStart - 1];
                Tok cur = words[localStart];
                bool joined = !cur.SpaceBefore || prev.Kind == K.Dot || cur.Kind is K.Dot or K.At;
                if (!joined)
                {
                    break;
                }

                localStart--;
            }

            var nameWords = words.GetRange(0, localStart);
            string? name = PhraseText(nameWords);
            if (nameWords.Count > 0)
            {
                Defect(MimeDefectKind.MissingAngleBrackets, words[0].Start);
            }

            name ??= CommentName(comments);
            AddMailbox(name, AddressText(words.GetRange(localStart, words.Count - localStart)), words[localStart].Start, result, groupMembers);
        }

        private void AddMailbox(string? name, string address, int at, List<MimeAddress> result, List<MimeMailbox>? groupMembers)
        {
            if (address.Length == 0)
            {
                Defect(MimeDefectKind.EmptyAddress, at);
            }
            else
            {
                int atSign = address.LastIndexOf('@');
                if (atSign < 0)
                {
                    Defect(MimeDefectKind.MissingDomain, at);
                }
                else if (atSign == address.Length - 1)
                {
                    Defect(MimeDefectKind.MissingDomain, at);
                }

                string local = atSign < 0 ? address : address[..atSign];
                if (local.Length > 0 && local[0] != '"' && (local[0] == '.' || local[^1] == '.' || local.Contains("..", StringComparison.Ordinal)))
                {
                    Defect(MimeDefectKind.ObsoleteLocalPart, at);
                }
            }

            result.Add(new MimeMailbox(name, address));
        }

        // The address text: tokens joined without white space or comments; quoted strings keep their quotes.
        private string AddressText(List<Tok> toks)
        {
            var sb = new List<byte>();
            foreach (Tok t in toks)
            {
                if (t.Kind == K.Comment)
                {
                    continue;
                }

                int end = t.End;
                if (t.Kind == K.Quoted && t.Unterminated)
                {
                    sb.AddRange(_b.AsSpan(t.Start, end - t.Start).ToArray());
                    sb.Add((byte)'"');
                    continue;
                }

                sb.AddRange(_b.AsSpan(t.Start, end - t.Start).ToArray());
            }

            return DecodeBytes([.. sb]);
        }

        private static string DecodeBytes(byte[] bytes)
        {
            return System.Text.Unicode.Utf8.IsValid(bytes) ? Encoding.UTF8.GetString(bytes) : MailEncoding.Windows1252.GetString(bytes);
        }

        // The display name: words joined as written (one space where white space was), quoted strings unquoted, then
        // RFC 2047 words decoded. Returns null when there is no name.
        private string? PhraseText(List<Tok> words)
        {
            if (words.Count == 0)
            {
                return null;
            }

            var bytes = new List<byte>();
            bool quoted = false;
            Tok? prev = null;
            foreach (Tok t in words)
            {
                if (prev is not null && (t.SpaceBefore || prev.Value.Kind == K.Quoted && t.Kind == K.Quoted) && bytes.Count > 0 && t.Kind != K.Dot)
                {
                    bytes.Add((byte)' ');
                }

                if (t.Kind == K.Quoted)
                {
                    quoted = true;
                    int s = t.Start + 1;
                    int e = t.Unterminated ? t.End : t.End - 1;
                    var tmp = new byte[Math.Max(0, e - s)];
                    int n = StructuredHeader.Unquote(_b.AsSpan(s, tmp.Length), tmp);
                    bytes.AddRange(tmp.AsSpan(0, n).ToArray());
                }
                else
                {
                    bytes.AddRange(_b.AsSpan(t.Start, t.End - t.Start).ToArray());
                }

                prev = t;
            }

            if (bytes.Count == 0)
            {
                return null;
            }

            var issues = new List<TextIssue>();
            string text = Rfc2047.DecodeText([.. bytes], issues, quoted ? EncodedWordContext.QuotedString : EncodedWordContext.Unstructured);
            foreach (TextIssue i in issues)
            {
                Defect(HeaderDecoding.Map(i.Kind), words[0].Start, 0);
            }

            text = text.Trim();
            return text.Length == 0 ? null : text;
        }

        // RFC 5322 §3.2.2 comments are not part of the address; when a mailbox has no phrase, the first comment names it (A4).
        private string? CommentName(List<Tok> comments)
        {
            if (comments.Count == 0)
            {
                return null;
            }

            Tok c = comments[0];
            int e = c.Unterminated ? c.End : c.End - 1;
            var sb = new List<byte>();
            int depth = 0;
            for (int i = c.Start + 1; i < e; i++)
            {
                byte x = _b[i];
                if (x == (byte)'\\' && i + 1 < e)
                {
                    sb.Add(_b[++i]);
                }
                else if (x == (byte)'(')
                {
                    depth++;
                    sb.Add(x);
                }
                else if (x == (byte)')')
                {
                    depth--;
                    sb.Add(x);
                }
                else
                {
                    sb.Add(x);
                }
            }

            _ = depth;
            string text = Rfc2047.DecodeText([.. sb], null, EncodedWordContext.Unstructured).Trim();
            return text.Length == 0 ? null : text;
        }
    }
}
