using System.Globalization;
using RecursiveAI.OneMail.Client.Imap.Protocol;

namespace RecursiveAI.OneMail.Client.Imap;

public sealed partial class ImapClient
{
    // ---- mailbox management ----

    /// <summary>
    /// Lists mailboxes (<c>LIST</c>, <c>LSUB</c>, or <c>LIST-EXTENDED</c> RFC 5258 when the options need it and the server has it).
    /// </summary>
    /// <param name="reference">The reference name (usually empty).</param>
    /// <param name="pattern">The pattern; <c>*</c> matches everything below, <c>%</c> one level. Default <c>*</c>.</param>
    /// <param name="options">Listing options, or null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The mailboxes in server order, names decoded.</returns>
    public async ValueTask<IReadOnlyList<ImapMailboxInfo>> ListAsync(string reference = "", string pattern = "*", ImapListOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new ImapListOptions();
        bool extended = HasCapability("LIST-EXTENDED") || HasCapability("IMAP4rev2");
        var cmd = ImapCommand.Create(options.SubscribedOnly && !extended ? "LSUB" : "LIST");
        if (options.SubscribedOnly && extended)
        {
            cmd.Raw("(SUBSCRIBED)");
        }

        cmd.Mailbox(reference).Mailbox(pattern);
        if (extended)
        {
            var returns = new List<string>();
            if (options.SpecialUse && HasCapability("SPECIAL-USE"))
            {
                returns.Add("SPECIAL-USE");
            }

            if (options.Children)
            {
                returns.Add("CHILDREN");
            }

            string items = StatusItemNames(options.Status);
            if (items.Length > 0 && HasCapability("LIST-STATUS"))
            {
                returns.Add("STATUS (" + items + ")");
            }

            if (returns.Count > 0)
            {
                cmd.Raw("RETURN (" + string.Join(' ', returns) + ")");
            }
        }

        var entries = new List<ImapListEntry>();
        var statuses = new Dictionary<string, ImapMailboxStatus>(StringComparer.Ordinal);
        await RunAsync(cmd, each: r =>
        {
            if (r.TryGetList(out ImapListEntry e))
            {
                entries.Add(e);
            }
            else if (r.TryGetStatus(out ImapStatusData s))
            {
                statuses[s.Mailbox] = new ImapMailboxStatus(s.Items);
            }
        }, ct: cancellationToken).ConfigureAwait(false);
        return entries.Select(e => { string name = MailboxName(e); return new ImapMailboxInfo(name, e.Delimiter, e.Attributes, statuses.GetValueOrDefault(e.Name)); }).ToList();
    }

    /// <summary>Synchronous twin of <see cref="ListAsync"/>.</summary>
    /// <param name="reference">The reference name.</param>
    /// <param name="pattern">The pattern.</param>
    /// <param name="options">Listing options.</param>
    /// <returns>The mailboxes.</returns>
    public IReadOnlyList<ImapMailboxInfo> List(string reference = "", string pattern = "*", ImapListOptions? options = null) =>
        Wait(ListAsync(reference, pattern, options));

    // The raw layer decodes names as modified UTF-7; under UTF8=ACCEPT (RFC 6855) the same bytes are UTF-8.
    private string MailboxName(ImapListEntry e) => _raw.Utf8Enabled ? System.Text.Encoding.UTF8.GetString(e.RawName.Span) : e.Name;

    private static string StatusItemNames(ImapStatusItems items)
    {
        var l = new List<string>();
        void Add(ImapStatusItems f, string n)
        {
            if (items.HasFlag(f)) { l.Add(n); }
        }

        Add(ImapStatusItems.Messages, "MESSAGES");
        Add(ImapStatusItems.Recent, "RECENT");
        Add(ImapStatusItems.UidNext, "UIDNEXT");
        Add(ImapStatusItems.UidValidity, "UIDVALIDITY");
        Add(ImapStatusItems.Unseen, "UNSEEN");
        Add(ImapStatusItems.HighestModSeq, "HIGHESTMODSEQ");
        Add(ImapStatusItems.Size, "SIZE");
        Add(ImapStatusItems.AppendLimit, "APPENDLIMIT");
        Add(ImapStatusItems.Deleted, "DELETED");
        return string.Join(' ', l);
    }

    /// <summary>Asks for counters of a mailbox without selecting it (<c>STATUS</c>).</summary>
    /// <param name="mailbox">The mailbox name.</param>
    /// <param name="items">The counters; default <see cref="ImapStatusItems.Standard"/>.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>What the server reported.</returns>
    public async ValueTask<ImapMailboxStatus> StatusAsync(string mailbox, ImapStatusItems items = ImapStatusItems.Standard, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        string names = StatusItemNames(items);
        if (names.Length == 0)
        {
            throw new ArgumentException("No status items requested.", nameof(items));
        }

        ImapStatusData? got = null;
        await RunAsync(ImapCommand.Create("STATUS").Mailbox(mailbox).Raw("(" + names + ")"), each: r =>
        {
            if (r.TryGetStatus(out ImapStatusData s))
            {
                got = s;
            }
        }, ct: cancellationToken).ConfigureAwait(false);
        return new ImapMailboxStatus(got?.Items ?? new Dictionary<string, long>());
    }

    /// <summary>Synchronous twin of <see cref="StatusAsync"/>.</summary>
    /// <param name="mailbox">The mailbox name.</param>
    /// <param name="items">The counters.</param>
    /// <returns>What the server reported.</returns>
    public ImapMailboxStatus Status(string mailbox, ImapStatusItems items = ImapStatusItems.Standard) => Wait(StatusAsync(mailbox, items));

    /// <summary>Creates a mailbox (<c>CREATE</c>). A trailing delimiter makes a mailbox that holds only other mailboxes on servers that distinguish.</summary>
    /// <param name="mailbox">The name.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>A task that completes with the command.</returns>
    public async ValueTask CreateAsync(string mailbox, CancellationToken cancellationToken = default) =>
        await RunAsync(ImapCommand.Create("CREATE").Mailbox(mailbox), ct: cancellationToken).ConfigureAwait(false);

    /// <summary>Deletes a mailbox (<c>DELETE</c>).</summary>
    /// <param name="mailbox">The name.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>A task that completes with the command.</returns>
    public async ValueTask DeleteAsync(string mailbox, CancellationToken cancellationToken = default) =>
        await RunAsync(ImapCommand.Create("DELETE").Mailbox(mailbox), ct: cancellationToken).ConfigureAwait(false);

    /// <summary>Renames a mailbox (<c>RENAME</c>).</summary>
    /// <param name="from">The current name.</param>
    /// <param name="to">The new name.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>A task that completes with the command.</returns>
    public async ValueTask RenameAsync(string from, string to, CancellationToken cancellationToken = default) =>
        await RunAsync(ImapCommand.Create("RENAME").Mailbox(from).Mailbox(to), ct: cancellationToken).ConfigureAwait(false);

    /// <summary>Subscribes to a mailbox (<c>SUBSCRIBE</c>).</summary>
    /// <param name="mailbox">The name.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>A task that completes with the command.</returns>
    public async ValueTask SubscribeAsync(string mailbox, CancellationToken cancellationToken = default) =>
        await RunAsync(ImapCommand.Create("SUBSCRIBE").Mailbox(mailbox), ct: cancellationToken).ConfigureAwait(false);

    /// <summary>Unsubscribes from a mailbox (<c>UNSUBSCRIBE</c>).</summary>
    /// <param name="mailbox">The name.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>A task that completes with the command.</returns>
    public async ValueTask UnsubscribeAsync(string mailbox, CancellationToken cancellationToken = default) =>
        await RunAsync(ImapCommand.Create("UNSUBSCRIBE").Mailbox(mailbox), ct: cancellationToken).ConfigureAwait(false);

    // ---- select ----

    /// <summary>Opens a mailbox (<c>SELECT</c>/<c>EXAMINE</c>) and starts tracking it in <see cref="Mailbox"/>. A failed select leaves no mailbox selected (RFC 9051 §6.3.2).</summary>
    /// <param name="mailbox">The name.</param>
    /// <param name="options">Read-only, CONDSTORE and QRESYNC choices, or null for a plain read-write select.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The mailbox state (same object as <see cref="Mailbox"/>).</returns>
    /// <exception cref="ImapCommandException">The mailbox does not exist or cannot be opened.</exception>
    /// <exception cref="NotSupportedException">QRESYNC was asked for and the server lacks it.</exception>
    public async ValueTask<ImapMailbox> SelectAsync(string mailbox, ImapSelectOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        options ??= new ImapSelectOptions();
        var cmd = ImapCommand.Create(options.ReadOnly ? "EXAMINE" : "SELECT").Mailbox(mailbox);
        if (options.QResync is { } q)
        {
            RequireCapability("QRESYNC");
            if (!_enabled.Contains("QRESYNC"))
            {
                await EnableAsync(["QRESYNC"], cancellationToken).ConfigureAwait(false);
            }

            // RFC 7162 §3.2.5: (QRESYNC (uidvalidity modseq [known-uids]))
            cmd.Raw("(QRESYNC (" + q.UidValidity.ToString(CultureInfo.InvariantCulture) + " " + q.ModSeq.ToString(CultureInfo.InvariantCulture)
                + (q.KnownUids.IsEmpty ? "" : " " + q.KnownUids.Text) + "))");
        }
        else if (options.CondStore && HasCapability("CONDSTORE"))
        {
            cmd.Raw("(CONDSTORE)");
        }

        var pending = new ImapMailbox(mailbox) { ReadOnly = options.ReadOnly };
        var resync = new List<ImapEvent>();
        Mailbox = null;
        _pendingMailbox = pending;
        _collector = resync;
        try
        {
            ImapCommandResult r = await RunAsync(cmd, ct: cancellationToken).ConfigureAwait(false);
            if (r.Completion.Code?.Is("READ-ONLY") ?? false)
            {
                pending.ReadOnly = true;
            }
        }
        finally
        {
            _pendingMailbox = null;
            _collector = null;
        }

        // EXISTS reported while opening are part of the state, not news; only a resync report is handed back.
        pending.ResyncEvents = resync.Where(e => e.Kind is ImapEventKind.Vanished or ImapEventKind.Fetch).ToList();
        Mailbox = pending;
        return pending;
    }

    /// <summary>Synchronous twin of <see cref="SelectAsync"/>.</summary>
    /// <param name="mailbox">The name.</param>
    /// <param name="options">Select options.</param>
    /// <returns>The mailbox state.</returns>
    public ImapMailbox Select(string mailbox, ImapSelectOptions? options = null) => Wait(SelectAsync(mailbox, options));

    /// <summary>Closes the mailbox. With <paramref name="expunge"/> true, <c>CLOSE</c> silently removes <c>\Deleted</c> messages; otherwise <c>UNSELECT</c> (RFC 3691) leaves them alone.</summary>
    /// <param name="expunge">Whether to expunge on the way out. Default false.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>A task that completes with the command.</returns>
    /// <exception cref="NotSupportedException">No expunge was wanted and the server has no UNSELECT.</exception>
    public async ValueTask CloseMailboxAsync(bool expunge = false, CancellationToken cancellationToken = default)
    {
        string verb;
        if (expunge)
        {
            verb = "CLOSE";
        }
        else if (HasCapability("UNSELECT") || HasCapability("IMAP4rev2"))
        {
            verb = "UNSELECT";
        }
        else
        {
            throw new NotSupportedException("The server has no UNSELECT, and CLOSE would expunge deleted messages; pass expunge: true to accept that.");
        }

        await RunAsync(ImapCommand.Create(verb), ct: cancellationToken).ConfigureAwait(false);
        Mailbox = null;
    }

    /// <summary>Synchronous twin of <see cref="CloseMailboxAsync"/>.</summary>
    /// <param name="expunge">Whether to expunge on the way out.</param>
    public void CloseMailbox(bool expunge = false) => CloseMailboxAsync(expunge).AsTask().GetAwaiter().GetResult();

    /// <summary>Asks the server to commit pending changes and report new messages (<c>CHECK</c>).</summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>A task that completes with the command.</returns>
    public async ValueTask CheckAsync(CancellationToken cancellationToken = default) =>
        await RunAsync(ImapCommand.Create("CHECK"), ct: cancellationToken).ConfigureAwait(false);
}
