using System.Globalization;
using System.Runtime.CompilerServices;
using RecursiveAI.OneMail.Client.Imap.Protocol;
using RecursiveAI.OneMail.Client.Protocol;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Mime.Reading;

namespace RecursiveAI.OneMail.Client.Imap;

public sealed partial class ImapClient
{
    // ---- fetch ----

    /// <summary>
    /// Fetches message data as a stream of results, one <see cref="ImapMessage"/> per FETCH response as it is read, so memory stays at one message
    /// plus the response being parsed. Requested body sections are buffered per message up to the connection's buffered-literal limit
    /// (<see cref="ProtocolLimits.MaxBufferedLiteral"/>, 16 MiB); a bigger section is skipped with a defect on <see cref="ImapMessage.Data"/> and should be
    /// taken with <see cref="DownloadAsync(long, string, Stream, long?, long?, CancellationToken)"/>.
    /// </summary>
    /// <remarks>
    /// The connection is busy until the enumeration ends. Stopping early reads and discards the rest of the response so the connection stays usable
    /// (cancellation instead leaves it mid-response). Unsolicited changes that arrive in between update <see cref="Mailbox"/> and raise <see cref="EventReceived"/>.
    /// </remarks>
    /// <param name="set">Sequence numbers, or UIDs when <paramref name="uid"/> is true.</param>
    /// <param name="query">What to fetch.</param>
    /// <param name="uid">True for <c>UID FETCH</c>. Default false.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The results in server order. The server may send them in any order and omit messages that no longer exist.</returns>
    /// <exception cref="ImapCommandException">Thrown when the enumeration reaches the end: the server answered NO or BAD.</exception>
    /// <exception cref="ArgumentException">The query asks for nothing.</exception>
    public async IAsyncEnumerable<ImapMessage> FetchAsync(ImapSet set, ImapFetchQuery query, bool uid = false, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ImapCommand cmd = BuildFetch(set, query, uid);
        EnterBusy();
        _inFetch = true;
        bool completed = false;
        try
        {
            await _raw.SendAsync(cmd, cancellationToken).ConfigureAwait(false);
            while (true)
            {
                (ImapResponse? r, ImapFetchData? plain) = await _raw.ReadFetchAsync(cancellationToken).ConfigureAwait(false);
                if (r is null)
                {
                    yield return new ImapMessage(plain!, null);
                    continue;
                }

                if (r.Kind == ImapResponseKind.Tagged)
                {
                    if (r.Tag != cmd.Tag)
                    {
                        continue;
                    }

                    completed = true;
                    if (r.Code is { } code)
                    {
                        ObserveCode(code);
                    }

                    if (r.Status != ImapStatus.Ok)
                    {
                        throw new ImapCommandException(r);
                    }

                    yield break;
                }

                if (r.Kind == ImapResponseKind.Untagged && r.Is("FETCH") && r.TryGetFetch(out ImapFetchData d))
                {
                    yield return new ImapMessage(d, Sections(d));
                }
                else
                {
                    Observe(r);
                }
            }
        }
        finally
        {
            if (!completed && !cancellationToken.IsCancellationRequested)
            {
                // The consumer stopped early: read the rest of the response so the next command starts on a clean stream.
                try
                {
                    while (true)
                    {
                        ImapResponse r = await _raw.ReadResponseAsync(null, cancellationToken).ConfigureAwait(false);
                        if (r.Kind == ImapResponseKind.Tagged && r.Tag == cmd.Tag)
                        {
                            break;
                        }

                        if (!(r.Kind == ImapResponseKind.Untagged && r.Is("FETCH")))
                        {
                            Observe(r);
                        }
                    }
                }
                catch (IOException)
                {
                    // The connection is gone; the next command will report it.
                }
            }

            _inFetch = false;
            _busy = false;
        }
    }

    /// <summary>Collects <see cref="FetchAsync"/> into a list. Synchronous twin.</summary>
    /// <param name="set">Sequence numbers or UIDs.</param>
    /// <param name="query">What to fetch.</param>
    /// <param name="uid">True for <c>UID FETCH</c>.</param>
    /// <returns>All results.</returns>
    public IReadOnlyList<ImapMessage> Fetch(ImapSet set, ImapFetchQuery query, bool uid = false)
    {
        var list = new List<ImapMessage>();
        IAsyncEnumerator<ImapMessage> e = FetchAsync(set, query, uid).GetAsyncEnumerator();
        try
        {
            while (Wait(e.MoveNextAsync()))
            {
                list.Add(e.Current);
            }
        }
        finally
        {
            e.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        return list;
    }

    private static readonly char[] SectionChars = ['.', '-', ' ', '(', ')', '_'];

    private static string CheckSection(string s)
    {
        foreach (char c in s)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || Array.IndexOf(SectionChars, c) >= 0))
            {
                throw new ArgumentException("Not a valid section specifier: " + s, nameof(s));
            }
        }

        return s;
    }

    private static ImapCommand BuildFetch(ImapSet set, ImapFetchQuery q, bool uid)
    {
        if (set.IsEmpty)
        {
            throw new ArgumentException("An empty sequence set.", nameof(set));
        }

        var attrs = new List<string>();
        if (q.Uid || uid) { attrs.Add("UID"); }
        if (q.Flags) { attrs.Add("FLAGS"); }
        if (q.InternalDate) { attrs.Add("INTERNALDATE"); }
        if (q.Size) { attrs.Add("RFC822.SIZE"); }
        if (q.Envelope) { attrs.Add("ENVELOPE"); }
        if (q.BodyStructure) { attrs.Add("BODYSTRUCTURE"); }
        if (q.ModSeq || q.ChangedSince is not null) { attrs.Add("MODSEQ"); }
        if (q.ObjectIds) { attrs.Add("EMAILID"); attrs.Add("THREADID"); }
        string body = q.MarkSeen ? "BODY" : "BODY.PEEK";
        if (q.Source) { attrs.Add(body + "[]"); }
        if (q.Header) { attrs.Add(body + "[HEADER]"); }
        if (q.HeaderFields.Count > 0)
        {
            foreach (string h in q.HeaderFields)
            {
                if (h.Length == 0 || h.Any(c => c <= ' ' || c >= 0x7F || c is ':' or '(' or ')' or '"' or '\\' or '{' or '%' or '*' or ']'))
                {
                    throw new ArgumentException("Not a header field name: " + h, nameof(q));
                }
            }

            attrs.Add(body + "[HEADER.FIELDS (" + string.Join(' ', q.HeaderFields) + ")]");
        }

        foreach (ImapSectionRequest s in q.Sections)
        {
            string kind = s.Binary ? (q.MarkSeen ? "BINARY" : "BINARY.PEEK") : body;
            string partial = s.Offset is { } o
                ? "<" + o.ToString(CultureInfo.InvariantCulture) + "." + (s.Count ?? 0x7FFFFFFF).ToString(CultureInfo.InvariantCulture) + ">"
                : "";
            attrs.Add(kind + "[" + CheckSection(s.Section) + "]" + partial);
        }

        if (attrs.Count == 0)
        {
            throw new ArgumentException("The fetch query asks for nothing.", nameof(q));
        }

        ImapCommand cmd = ImapCommand.Create(uid ? "UID" : "FETCH");
        if (uid)
        {
            cmd.Atom("FETCH");
        }

        cmd.SequenceSet(set.Text).Open();
        foreach (string a in attrs)
        {
            cmd.Raw(a);
        }

        cmd.Close();
        if (q.ChangedSince is { } cs)
        {
            cmd.Raw("(CHANGEDSINCE " + cs.ToString(CultureInfo.InvariantCulture) + (q.Vanished ? " VANISHED" : "") + ")");
        }

        return cmd;
    }

    // BODY[1.2]<0> -> "1.2"; BODY[HEADER.FIELDS (FROM)] -> "HEADER.FIELDS"; BINARY[1] -> "BINARY:1".
    private static Dictionary<string, ReadOnlyMemory<byte>>? Sections(ImapFetchData d)
    {
        if (d.Sections.Count == 0)
        {
            return null;
        }

        var map = new Dictionary<string, ReadOnlyMemory<byte>>(d.Sections.Count);
        foreach (ImapFetchSection s in d.Sections)
        {
            string name = s.Name;
            int open = name.IndexOf('[', StringComparison.Ordinal);
            int close = name.IndexOf(']', open + 1);
            if (open < 0 || close < 0 || s.Data.IsSkipped)
            {
                continue;
            }

            string inside = name[(open + 1)..close];
            int sp = inside.IndexOf(' ', StringComparison.Ordinal);
            if (sp >= 0)
            {
                inside = inside[..sp];
            }

            string key = inside.ToUpperInvariant();
            if (name.StartsWith("BINARY", StringComparison.OrdinalIgnoreCase))
            {
                key = "BINARY:" + key;
            }

            map[key] = s.Data.Bytes;
        }

        return map;
    }

    /// <summary>Streams one body section of one message to a stream, straight off the connection: memory stays at the reader's buffers however large the section is.</summary>
    /// <param name="uid">The message UID in the selected mailbox.</param>
    /// <param name="section">The section specifier: empty for the whole message, else <c>1.2</c>, <c>HEADER</c>, <c>TEXT</c>, <c>2.MIME</c> ...</param>
    /// <param name="destination">Receives the bytes.</param>
    /// <param name="offset">First octet for a partial fetch, or null for from the start.</param>
    /// <param name="count">Octets to fetch with <paramref name="offset"/>.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The number of bytes written, or -1 when the server returned no such message or section.</returns>
    public ValueTask<long> DownloadAsync(long uid, string section, Stream destination, long? offset = null, long? count = null, CancellationToken cancellationToken = default) =>
        DownloadCoreAsync(uid, section, offset, count, false, async (ctx, lit, ct) =>
        {
            await lit.CopyToAsync(destination, ct).ConfigureAwait(false);
            return true;
        }, destination, cancellationToken);

    /// <summary>Like <see cref="DownloadAsync(long, string, Stream, long?, long?, CancellationToken)"/> with <c>BINARY[]</c> (RFC 3516): the server removes the transfer encoding.</summary>
    /// <param name="uid">The message UID.</param>
    /// <param name="section">The part number (<c>1</c>, <c>2.1</c>); the whole-message section is not valid for BINARY.</param>
    /// <param name="destination">Receives the decoded bytes.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The number of bytes written, or -1 when nothing came back.</returns>
    /// <exception cref="NotSupportedException">The server has no BINARY.</exception>
    public ValueTask<long> DownloadBinaryAsync(long uid, string section, Stream destination, CancellationToken cancellationToken = default)
    {
        RequireCapability("BINARY");
        return DownloadCoreAsync(uid, section, null, null, true, async (ctx, lit, ct) =>
        {
            await lit.CopyToAsync(destination, ct).ConfigureAwait(false);
            return true;
        }, destination, cancellationToken);
    }

    /// <summary>Synchronous twin of <see cref="DownloadAsync(long, string, Stream, long?, long?, CancellationToken)"/>.</summary>
    /// <param name="uid">The message UID.</param>
    /// <param name="section">The section specifier.</param>
    /// <param name="destination">Receives the bytes.</param>
    /// <param name="offset">First octet, or null.</param>
    /// <param name="count">Octets with offset.</param>
    /// <returns>Bytes written, or -1.</returns>
    public long Download(long uid, string section, Stream destination, long? offset = null, long? count = null) =>
        Wait(DownloadAsync(uid, section, destination, offset, count));

    private async ValueTask<long> DownloadCoreAsync(long uid, string section, long? offset, long? count, bool binary, ImapLiteralHandler copy, Stream destination, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var query = new ImapFetchQuery { Sections = [new ImapSectionRequest(section ?? "", offset, count, binary)] };
        ImapCommand cmd = BuildFetch(ImapSet.Of(uid), query, uid: true);
        long written = -1;
        await RunAsync(cmd, async (ctx, lit, token) =>
        {
            long before = ctx.Length;
            bool done = await copy(ctx, lit, token).ConfigureAwait(false);
            written = before;
            return done;
        }, ct: ct).ConfigureAwait(false);
        return written;
    }

    /// <summary>Fetches one whole message and parses it into the MIME entity tree. The literal goes from the connection into the reader; the tree keeps the bytes (limited by the MIME reader's total-size limit).</summary>
    /// <param name="uid">The message UID in the selected mailbox.</param>
    /// <param name="options">MIME reader options, or null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The root entity, or null when the server returned no such message.</returns>
    public async ValueTask<MimeEntity?> GetMessageAsync(long uid, MimeReaderOptions? options = null, CancellationToken cancellationToken = default)
    {
        MimeEntity? entity = null;
        ImapCommand cmd = BuildFetch(ImapSet.Of(uid), new ImapFetchQuery { Source = true }, uid: true);
        await RunAsync(cmd, async (ctx, lit, token) =>
        {
            entity = await MimeEntity.LoadAsync(lit, options, token).ConfigureAwait(false);
            return true;
        }, ct: cancellationToken).ConfigureAwait(false);
        return entity;
    }

    /// <summary>Synchronous twin of <see cref="GetMessageAsync"/>.</summary>
    /// <param name="uid">The message UID.</param>
    /// <param name="options">MIME reader options.</param>
    /// <returns>The root entity, or null.</returns>
    public MimeEntity? GetMessage(long uid, MimeReaderOptions? options = null) => Wait(GetMessageAsync(uid, options));

    // ---- search, sort, thread ----

    /// <summary>Searches the selected mailbox (<c>SEARCH</c>; with return options the ESEARCH form, RFC 4731, when offered).</summary>
    /// <param name="criteria">The criterion.</param>
    /// <param name="uid">True for <c>UID SEARCH</c> (results are UIDs). Default false.</param>
    /// <param name="returns">What to report in addition; without ESEARCH the classic form is used and Min, Max and Count are computed from the list.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The result.</returns>
    public async ValueTask<ImapSearchResult> SearchAsync(ImapSearch criteria, bool uid = false, ImapSearchReturn returns = ImapSearchReturn.None, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        bool esearch = returns != ImapSearchReturn.None && HasCapability("ESEARCH");
        ImapCommand cmd = ImapCommand.Create(uid ? "UID" : "SEARCH");
        if (uid)
        {
            cmd.Atom("SEARCH");
        }

        if (esearch)
        {
            var o = new List<string>();
            if (returns.HasFlag(ImapSearchReturn.Min)) { o.Add("MIN"); }
            if (returns.HasFlag(ImapSearchReturn.Max)) { o.Add("MAX"); }
            if (returns.HasFlag(ImapSearchReturn.All)) { o.Add("ALL"); }
            if (returns.HasFlag(ImapSearchReturn.Count)) { o.Add("COUNT"); }
            cmd.Raw("RETURN (" + string.Join(' ', o) + ")");
        }

        if (criteria.HasNonAscii && !_raw.Utf8Enabled)
        {
            cmd.Atom("CHARSET").Atom("UTF-8");
        }

        criteria.Apply(cmd);
        List<long>? numbers = null;
        long? min = null, max = null, count = null, modSeq = null;
        await RunAsync(cmd, each: r =>
        {
            if (r.TryGetSearch(out IReadOnlyList<long> list))
            {
                numbers = [.. list];
                foreach (ImapValue v in r.Data)
                {
                    if (v.IsList && v.Items.Count == 2 && v.Items[0].IsAtom("MODSEQ") && v.Items[1].TryGetNumber(out long m))
                    {
                        modSeq = m;
                    }
                }
            }
            else if (r.TryGetEsearch(out ImapEsearchData e))
            {
                if (e.Get("MIN") is { } a && a.TryGetNumber(out long mn)) { min = mn; }
                if (e.Get("MAX") is { } b && b.TryGetNumber(out long mx)) { max = mx; }
                if (e.Get("COUNT") is { } c && c.TryGetNumber(out long cn)) { count = cn; }
                if (e.Get("MODSEQ") is { } d && d.TryGetNumber(out long ms)) { modSeq = ms; }
                if (e.Get("ALL") is { Text: { Length: > 0 } all })
                {
                    numbers = [.. ImapSequenceSet.Expand(all)];
                }
            }
        }, ct: cancellationToken).ConfigureAwait(false);
        numbers ??= [];
        if (!esearch && numbers.Count > 0)
        {
            min = numbers.Min();
            max = numbers.Max();
        }

        return new ImapSearchResult(numbers, min, max, count, modSeq, uid);
    }

    /// <summary>Synchronous twin of <see cref="SearchAsync"/>.</summary>
    /// <param name="criteria">The criterion.</param>
    /// <param name="uid">True for UIDs.</param>
    /// <param name="returns">ESEARCH return options.</param>
    /// <returns>The result.</returns>
    public ImapSearchResult Search(ImapSearch criteria, bool uid = false, ImapSearchReturn returns = ImapSearchReturn.None) =>
        Wait(SearchAsync(criteria, uid, returns));

    /// <summary>Sorts on the server (<c>SORT</c>, RFC 5256).</summary>
    /// <param name="sort">The keys in order of significance.</param>
    /// <param name="criteria">Which messages to sort.</param>
    /// <param name="uid">True for <c>UID SORT</c>.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The numbers (or UIDs) in sorted order.</returns>
    /// <exception cref="NotSupportedException">The server has no SORT.</exception>
    public async ValueTask<IReadOnlyList<long>> SortAsync(IReadOnlyList<ImapSortCriterion> sort, ImapSearch criteria, bool uid = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sort);
        ArgumentNullException.ThrowIfNull(criteria);
        if (sort.Count == 0)
        {
            throw new ArgumentException("At least one sort key.", nameof(sort));
        }

        RequireCapability("SORT");
        ImapCommand cmd = ImapCommand.Create(uid ? "UID" : "SORT");
        if (uid)
        {
            cmd.Atom("SORT");
        }

        cmd.Open();
        foreach (ImapSortCriterion s in sort)
        {
            if (s.Reverse) { cmd.Raw("REVERSE"); }
            cmd.Raw(s.Key.ToString().ToUpperInvariant());
        }

        cmd.Close().Atom("UTF-8");
        criteria.Apply(cmd);
        IReadOnlyList<long> result = [];
        await RunAsync(cmd, each: r =>
        {
            if (r.Is("SORT") && r.TryGetSearch(out IReadOnlyList<long> l))
            {
                result = l;
            }
        }, ct: cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>Threads on the server (<c>THREAD</c>, RFC 5256).</summary>
    /// <param name="algorithm">The algorithm.</param>
    /// <param name="criteria">Which messages to thread.</param>
    /// <param name="uid">True for <c>UID THREAD</c>.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The top-level threads in server order.</returns>
    /// <exception cref="NotSupportedException">The server lacks the algorithm.</exception>
    public async ValueTask<IReadOnlyList<ImapThread>> ThreadAsync(ImapThreadAlgorithm algorithm, ImapSearch criteria, bool uid = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        string name = algorithm == ImapThreadAlgorithm.References ? "REFERENCES" : "ORDEREDSUBJECT";
        RequireCapability("THREAD=" + name);
        ImapCommand cmd = ImapCommand.Create(uid ? "UID" : "THREAD");
        if (uid)
        {
            cmd.Atom("THREAD");
        }

        cmd.Atom(name).Atom("UTF-8");
        criteria.Apply(cmd);
        var roots = new List<ImapThread>();
        await RunAsync(cmd, each: r =>
        {
            if (r.Is("THREAD") && r.Kind == ImapResponseKind.Untagged)
            {
                foreach (ImapValue v in r.Data)
                {
                    if (v.IsList)
                    {
                        roots.Add(ParseThread(v, 0));
                    }
                }
            }
        }, ct: cancellationToken).ConfigureAwait(false);
        return roots;
    }

    // RFC 5256 §4: thread-list = "(" (thread-members / thread-nested) ")"; members are a chain, nested lists are branches of the last member.
    private static ImapThread ParseThread(ImapValue list, int depth)
    {
        ImapThread? first = null;
        ImapThread? last = null;
        List<ImapThread>? dummyChildren = null;
        foreach (ImapValue item in list.Items)
        {
            if (item.TryGetNumber(out long n))
            {
                var node = new ImapThread(n, []);
                if (last is null) { first = node; } else { ((List<ImapThread>)last.Children).Add(node); }
                last = node;
            }
            else if (item.IsList && depth < 256)
            {
                ImapThread child = ParseThread(item, depth + 1);
                if (last is null)
                {
                    // A branch with no member before it: the server's dummy parent for messages whose common ancestor is missing.
                    dummyChildren ??= [];
                    dummyChildren.Add(child);
                }
                else
                {
                    ((List<ImapThread>)last.Children).Add(child);
                }
            }
        }

        if (dummyChildren is not null && first is null)
        {
            return new ImapThread(null, dummyChildren);
        }

        return first ?? new ImapThread(null, []);
    }

    // ---- flags, copy, move, expunge ----

    /// <summary>Changes flags (<c>STORE</c>).</summary>
    /// <param name="set">Numbers, or UIDs when <paramref name="uid"/> is true.</param>
    /// <param name="mode">Replace, add or remove.</param>
    /// <param name="flags">Flags and keywords, e.g. <c>\Seen</c>, <c>$Forwarded</c>.</param>
    /// <param name="uid">True for <c>UID STORE</c>.</param>
    /// <param name="unchangedSince">Only change messages whose mod-sequence is not above this (CONDSTORE, RFC 7162). Default none.</param>
    /// <param name="silent">Use <c>.SILENT</c>, so the server does not send the new flags back. Default true.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The numbers or UIDs the server did not modify because of <paramref name="unchangedSince"/> (<c>[MODIFIED]</c>); empty otherwise.</returns>
    public async ValueTask<IReadOnlyList<long>> StoreAsync(ImapSet set, ImapStoreMode mode, IReadOnlyList<string> flags, bool uid = false, long? unchangedSince = null, bool silent = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(flags);
        ImapCommand cmd = ImapCommand.Create(uid ? "UID" : "STORE");
        if (uid)
        {
            cmd.Atom("STORE");
        }

        cmd.SequenceSet(set.Text);
        if (unchangedSince is { } us)
        {
            cmd.Raw("(UNCHANGEDSINCE " + us.ToString(CultureInfo.InvariantCulture) + ")");
        }

        string item = mode switch { ImapStoreMode.Add => "+FLAGS", ImapStoreMode.Remove => "-FLAGS", _ => "FLAGS" } + (silent ? ".SILENT" : "");
        cmd.Raw(item).Flags(flags);
        ImapCommandResult r = await RunAsync(cmd, ct: cancellationToken).ConfigureAwait(false);
        if (r.Completion.Code is { } c && c.Is("MODIFIED") && c.Arguments.Count > 0 && c.Arguments[0].Text is { } t)
        {
            return [.. ImapSequenceSet.Expand(t)];
        }

        return [];
    }

    /// <summary>Synchronous twin of <see cref="StoreAsync"/>.</summary>
    /// <param name="set">Numbers or UIDs.</param>
    /// <param name="mode">Replace, add or remove.</param>
    /// <param name="flags">The flags.</param>
    /// <param name="uid">True for UIDs.</param>
    /// <param name="unchangedSince">CONDSTORE guard.</param>
    /// <param name="silent">Suppress echo.</param>
    /// <returns>Messages not modified.</returns>
    public IReadOnlyList<long> Store(ImapSet set, ImapStoreMode mode, IReadOnlyList<string> flags, bool uid = false, long? unchangedSince = null, bool silent = true) =>
        Wait(StoreAsync(set, mode, flags, uid, unchangedSince, silent));

    /// <summary>Copies messages to another mailbox (<c>COPY</c>).</summary>
    /// <param name="set">Numbers or UIDs.</param>
    /// <param name="destination">The destination mailbox.</param>
    /// <param name="uid">True for <c>UID COPY</c>.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The source and new UIDs when the server reports <c>COPYUID</c> (UIDPLUS, RFC 4315), else an empty mapping with UID validity 0.</returns>
    public async ValueTask<ImapCopyResult> CopyAsync(ImapSet set, string destination, bool uid = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ImapCommand cmd = ImapCommand.Create(uid ? "UID" : "COPY");
        if (uid)
        {
            cmd.Atom("COPY");
        }

        cmd.SequenceSet(set.Text).Mailbox(destination);
        return await RunCopyAsync(cmd, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Synchronous twin of <see cref="CopyAsync"/>.</summary>
    /// <param name="set">Numbers or UIDs.</param>
    /// <param name="destination">The destination mailbox.</param>
    /// <param name="uid">True for UIDs.</param>
    /// <returns>The UID mapping.</returns>
    public ImapCopyResult Copy(ImapSet set, string destination, bool uid = false) => Wait(CopyAsync(set, destination, uid));

    /// <summary>
    /// Moves messages (<c>MOVE</c>, RFC 6851). Without MOVE, falls back to copy, mark <c>\Deleted</c> and <c>UID EXPUNGE</c> when the server has UIDPLUS and
    /// <paramref name="uid"/> is true; there is no safe fallback otherwise.
    /// </summary>
    /// <param name="set">Numbers or UIDs.</param>
    /// <param name="destination">The destination mailbox.</param>
    /// <param name="uid">True for <c>UID MOVE</c>.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The UID mapping when reported.</returns>
    /// <exception cref="NotSupportedException">No MOVE, and no safe fallback.</exception>
    public async ValueTask<ImapCopyResult> MoveAsync(ImapSet set, string destination, bool uid = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (HasCapability("MOVE") || HasCapability("IMAP4rev2"))
        {
            ImapCommand cmd = ImapCommand.Create(uid ? "UID" : "MOVE");
            if (uid)
            {
                cmd.Atom("MOVE");
            }

            cmd.SequenceSet(set.Text).Mailbox(destination);
            return await RunCopyAsync(cmd, cancellationToken).ConfigureAwait(false);
        }

        if (!uid || !HasCapability("UIDPLUS"))
        {
            throw new NotSupportedException("The server has no MOVE, and without UIDPLUS a copy-and-expunge fallback could remove other messages.");
        }

        ImapCopyResult copied = await CopyAsync(set, destination, true, cancellationToken).ConfigureAwait(false);
        await StoreAsync(set, ImapStoreMode.Add, ["\\Deleted"], true, null, true, cancellationToken).ConfigureAwait(false);
        await ExpungeAsync(set, cancellationToken).ConfigureAwait(false);
        return copied;
    }

    /// <summary>Synchronous twin of <see cref="MoveAsync"/>.</summary>
    /// <param name="set">Numbers or UIDs.</param>
    /// <param name="destination">The destination mailbox.</param>
    /// <param name="uid">True for UIDs.</param>
    /// <returns>The UID mapping.</returns>
    public ImapCopyResult Move(ImapSet set, string destination, bool uid = false) => Wait(MoveAsync(set, destination, uid));

    private async ValueTask<ImapCopyResult> RunCopyAsync(ImapCommand cmd, CancellationToken ct)
    {
        ImapResponseCode? copyUid = null;
        ImapCommandResult r = await RunAsync(cmd, each: resp =>
        {
            // MOVE reports COPYUID in an untagged OK before the EXPUNGE/VANISHED responses.
            if (resp.Code is { } c && c.Is("COPYUID"))
            {
                copyUid = c;
            }
        }, ct: ct).ConfigureAwait(false);
        if (r.Completion.Code is { } cc && cc.Is("COPYUID"))
        {
            copyUid = cc;
        }

        if (copyUid is { Arguments.Count: >= 3 } u && u.Arguments[0].TryGetNumber(out long validity))
        {
            return new ImapCopyResult(validity, [.. ImapSequenceSet.Expand(u.Arguments[1].Text ?? "")], [.. ImapSequenceSet.Expand(u.Arguments[2].Text ?? "")]);
        }

        return new ImapCopyResult(0, [], []);
    }

    /// <summary>Permanently removes messages marked <c>\Deleted</c> (<c>EXPUNGE</c>, or <c>UID EXPUNGE</c> for given UIDs with UIDPLUS, RFC 4315).</summary>
    /// <param name="uids">Only these UIDs, or null for every deleted message.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The expunge and vanished events the server sent.</returns>
    /// <exception cref="NotSupportedException">UIDs were given and the server has no UIDPLUS.</exception>
    public async ValueTask<IReadOnlyList<ImapEvent>> ExpungeAsync(ImapSet? uids = null, CancellationToken cancellationToken = default)
    {
        ImapCommand cmd = ImapCommand.Create(uids is null ? "EXPUNGE" : "UID");
        if (uids is { } set)
        {
            RequireCapability("UIDPLUS", "IMAP4rev2");
            cmd.Atom("EXPUNGE").SequenceSet(set.Text);
        }

        var events = new List<ImapEvent>();
        _collector = events;
        try
        {
            await RunAsync(cmd, ct: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _collector = null;
        }

        return events.Where(e => e.Kind is ImapEventKind.Expunge or ImapEventKind.Vanished).ToList();
    }

    /// <summary>Synchronous twin of <see cref="ExpungeAsync"/>.</summary>
    /// <param name="uids">Only these UIDs, or null.</param>
    /// <returns>The expunge events.</returns>
    public IReadOnlyList<ImapEvent> Expunge(ImapSet? uids = null) => Wait(ExpungeAsync(uids));

    // ---- append ----

    /// <summary>Uploads a message from memory (<c>APPEND</c>).</summary>
    /// <param name="mailbox">The destination mailbox.</param>
    /// <param name="message">The RFC 5322 bytes; sent as given.</param>
    /// <param name="flags">Flags to set, or null for none.</param>
    /// <param name="internalDate">The internal date, or null for the server's clock.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The new UID when the server reports <c>APPENDUID</c>.</returns>
    /// <exception cref="ArgumentException">The message is larger than the server's <c>APPENDLIMIT</c> (RFC 7889).</exception>
    /// <exception cref="ImapCommandException">The server refused (e.g. <c>TOOBIG</c>, <c>OVERQUOTA</c>).</exception>
    public ValueTask<ImapAppendResult> AppendAsync(string mailbox, ReadOnlyMemory<byte> message, IReadOnlyList<string>? flags = null, DateTimeOffset? internalDate = null, CancellationToken cancellationToken = default) =>
        AppendAsync(mailbox, [new ImapAppendItem(message) { Flags = flags ?? [], InternalDate = internalDate }], cancellationToken);

    /// <summary>Uploads a message from a stream, without holding it in memory.</summary>
    /// <param name="mailbox">The destination mailbox.</param>
    /// <param name="message">The stream, positioned at the start.</param>
    /// <param name="length">Exact number of bytes to send.</param>
    /// <param name="flags">Flags to set, or null.</param>
    /// <param name="internalDate">The internal date, or null.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The new UID when reported.</returns>
    public ValueTask<ImapAppendResult> AppendAsync(string mailbox, Stream message, long length, IReadOnlyList<string>? flags = null, DateTimeOffset? internalDate = null, CancellationToken cancellationToken = default) =>
        AppendAsync(mailbox, [new ImapAppendItem(message, length) { Flags = flags ?? [], InternalDate = internalDate }], cancellationToken);

    /// <summary>Uploads several messages in one command when the server has MULTIAPPEND (RFC 3502); one command each otherwise.</summary>
    /// <param name="mailbox">The destination mailbox.</param>
    /// <param name="items">The messages.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The new UIDs in order when reported.</returns>
    public async ValueTask<ImapAppendResult> AppendAsync(string mailbox, IReadOnlyList<ImapAppendItem> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            throw new ArgumentException("Nothing to append.", nameof(items));
        }

        long? limit = AppendLimit;
        if (limit is { } max && items.Any(i => i.Length > max))
        {
            throw new ArgumentException("A message is larger than the server's APPENDLIMIT of " + max.ToString(CultureInfo.InvariantCulture) + " octets.", nameof(items));
        }

        if (items.Count > 1 && !HasCapability("MULTIAPPEND"))
        {
            long validity = 0;
            var uids = new List<long>();
            foreach (ImapAppendItem item in items)
            {
                ImapAppendResult one = await AppendAsync(mailbox, [item], cancellationToken).ConfigureAwait(false);
                validity = one.UidValidity;
                uids.AddRange(one.Uids);
            }

            return new ImapAppendResult(validity, uids);
        }

        ImapCommand cmd = ImapCommand.Create("APPEND").Mailbox(mailbox);
        foreach (ImapAppendItem item in items)
        {
            if (item.Flags.Count > 0)
            {
                cmd.Flags(item.Flags);
            }

            if (item.InternalDate is { } d)
            {
                cmd.Quoted(FormatInternalDate(d));
            }

            if (item.Stream is { } s) { cmd.Literal(s, item.Length); } else { cmd.Literal(item.Bytes); }
        }

        ImapResponseCode? appendUid = null;
        ImapCommandResult r = await RunAsync(cmd, throwOnFailure: false, ct: cancellationToken).ConfigureAwait(false);
        if (!r.IsOk)
        {
            throw new ImapCommandException(r.Completion);
        }

        if (r.Completion.Code is { } c && c.Is("APPENDUID"))
        {
            appendUid = c;
        }

        if (appendUid is { Arguments.Count: >= 2 } a && a.Arguments[0].TryGetNumber(out long uv))
        {
            return new ImapAppendResult(uv, [.. ImapSequenceSet.Expand(a.Arguments[1].Text ?? "")]);
        }

        return new ImapAppendResult(0, []);
    }

    /// <summary>Synchronous twin of <see cref="AppendAsync(string, ReadOnlyMemory{byte}, IReadOnlyList{string}?, DateTimeOffset?, CancellationToken)"/>.</summary>
    /// <param name="mailbox">The destination mailbox.</param>
    /// <param name="message">The message bytes.</param>
    /// <param name="flags">Flags, or null.</param>
    /// <param name="internalDate">Internal date, or null.</param>
    /// <returns>The new UID when reported.</returns>
    public ImapAppendResult Append(string mailbox, ReadOnlyMemory<byte> message, IReadOnlyList<string>? flags = null, DateTimeOffset? internalDate = null) =>
        Wait(AppendAsync(mailbox, message, flags, internalDate));

    /// <summary>The server's global <c>APPENDLIMIT=n</c> in octets (RFC 7889), or null when none is announced.</summary>
    public long? AppendLimit
    {
        get
        {
            foreach (string c in Capabilities)
            {
                if (c.StartsWith("APPENDLIMIT=", StringComparison.OrdinalIgnoreCase) && long.TryParse(c.AsSpan(12), NumberStyles.None, CultureInfo.InvariantCulture, out long v))
                {
                    return v;
                }
            }

            return null;
        }
    }

    // RFC 9051 §9: date-time = DQUOTE date-day-fixed "-" date-month "-" date-year SP time SP zone DQUOTE
    private static string FormatInternalDate(DateTimeOffset d)
    {
        TimeSpan o = d.Offset;
        string zone = (o < TimeSpan.Zero ? "-" : "+") + Math.Abs(o.Hours).ToString("D2", CultureInfo.InvariantCulture) + Math.Abs(o.Minutes).ToString("D2", CultureInfo.InvariantCulture);
        return d.ToString("dd-MMM-yyyy HH:mm:ss", CultureInfo.InvariantCulture) + " " + zone;
    }
}
