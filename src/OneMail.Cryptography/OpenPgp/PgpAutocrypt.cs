using RecursiveAI.OneCrypto.OpenPgp;
using RecursiveAI.OneMail.Mime;

namespace RecursiveAI.OneMail.Cryptography.OpenPgp;

/// <summary>One <c>Autocrypt</c> or <c>Autocrypt-Gossip</c> header of a message, read.</summary>
public sealed class PgpAutocryptEntry
{
    internal PgpAutocryptEntry(string raw, AutocryptHeader? header, AutocryptHeaderError error, bool matchesFrom)
    {
        RawValue = raw;
        Header = header;
        Error = error;
        MatchesFrom = matchesFrom;
    }

    /// <summary>The header value as written, unfolded.</summary>
    public string RawValue { get; }

    /// <summary>The parsed header; null when it is invalid (see <see cref="Error"/>).</summary>
    public AutocryptHeader? Header { get; }

    /// <summary>Why the header is invalid; <see cref="AutocryptHeaderError.None"/> when it is valid.</summary>
    public AutocryptHeaderError Error { get; }

    /// <summary>For an <c>Autocrypt</c> header, whether its <c>addr</c> equals the message's From address (Autocrypt Level 1: other headers are ignored). Not set for gossip.</summary>
    public bool MatchesFrom { get; }
}

/// <summary>The Autocrypt headers of one message (Autocrypt Level 1, §2.2 and §3.2).</summary>
public sealed class PgpAutocryptInfo
{
    internal PgpAutocryptInfo(IReadOnlyList<PgpAutocryptEntry> headers, IReadOnlyList<PgpAutocryptEntry> gossip, string? from)
    {
        Headers = headers;
        Gossip = gossip;
        FromAddress = from;
    }

    /// <summary>Every <c>Autocrypt</c> header in the message's top-level header block, in order, valid or not.</summary>
    public IReadOnlyList<PgpAutocryptEntry> Headers { get; }

    /// <summary>Every <c>Autocrypt-Gossip</c> header found at the top level of the entity read. Gossip counts only when it came from the encrypted payload; the caller decides that.</summary>
    public IReadOnlyList<PgpAutocryptEntry> Gossip { get; }

    /// <summary>The single From address, or null when there is none or more than one.</summary>
    public string? FromAddress { get; }

    /// <summary>
    /// The one header Autocrypt would use: valid and naming the From address. If there is not exactly one such header, none is used (two valid headers are all discarded).
    /// </summary>
    public PgpAutocryptEntry? Selected
    {
        get
        {
            PgpAutocryptEntry[] usable = Headers.Where(static h => h.Header is not null && h.MatchesFrom).ToArray();
            return usable.Length == 1 ? usable[0] : null;
        }
    }
}

/// <summary>
/// Reads Autocrypt headers (Autocrypt Level 1, https://autocrypt.org/level1.html) from a message into typed values. The header grammar, the peer state and
/// the recommendation are OneCrypto's (<see cref="AutocryptHeader"/>, <see cref="AutocryptPeer"/>); this type finds the headers in the MIME tree.
/// </summary>
public static class PgpAutocrypt
{
    /// <summary>Reads the <c>Autocrypt</c> headers of the message's header block and the <c>Autocrypt-Gossip</c> headers of <paramref name="entity"/>. Never throws for content.</summary>
    /// <param name="entity">The message. For gossip pass the decrypted entity (<see cref="PgpResult.Content"/>); gossip sits in the headers of the encrypted payload.</param>
    /// <returns>The headers with the reason for each invalid one.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entity"/> is null.</exception>
    public static PgpAutocryptInfo Read(MimeEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var froms = new List<string>();
        foreach (MimeHeader h in entity.Headers.GetAll("From"))
        {
            foreach (MimeMailbox m in AddressParser.Flatten(AddressParser.Parse(h.RawValue.Span)))
            {
                froms.Add(m.UnquotedAddress);
            }
        }

        string? from = froms.Count == 1 ? froms[0] : null;
        var headers = new List<PgpAutocryptEntry>();
        foreach (MimeHeader h in entity.Headers.GetAll("Autocrypt"))
        {
            string raw = Unfold(h.Value);
            bool ok = AutocryptHeader.TryParse(raw, out AutocryptHeader? parsed, out AutocryptHeaderError error);
            headers.Add(new PgpAutocryptEntry(raw, ok ? parsed : null, ok ? AutocryptHeaderError.None : error, ok && from is not null && parsed!.IsValidFor(from)));
        }

        var gossip = new List<PgpAutocryptEntry>();
        foreach (MimeHeader h in entity.Headers.GetAll("Autocrypt-Gossip"))
        {
            string raw = Unfold(h.Value);
            bool ok = AutocryptHeader.TryParse(raw, out AutocryptHeader? parsed, out AutocryptHeaderError error);
            gossip.Add(new PgpAutocryptEntry(raw, ok ? parsed : null, ok ? AutocryptHeaderError.None : error, false));
        }

        return new PgpAutocryptInfo(headers, gossip, from);
    }

    /// <summary>
    /// Describes a message for <see cref="AutocryptPeer.Apply"/>: From address and count, <c>Date</c>, the raw <c>Autocrypt</c> values and whether it is a report.
    /// </summary>
    /// <param name="message">The message (its top-level headers).</param>
    /// <param name="received">When it was received; the library reads no clock.</param>
    /// <param name="isSpam">Whether the caller believes it is spam (Autocrypt ignores such messages).</param>
    /// <returns>The description, or null when the message has no From address.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    public static AutocryptMessage? Describe(MimeEntity message, DateTimeOffset received, bool isSpam = false)
    {
        ArgumentNullException.ThrowIfNull(message);
        var froms = new List<string>();
        foreach (MimeHeader h in message.Headers.GetAll("From"))
        {
            foreach (MimeMailbox m in AddressParser.Flatten(AddressParser.Parse(h.RawValue.Span)))
            {
                froms.Add(m.UnquotedAddress);
            }
        }

        if (froms.Count == 0)
        {
            return null;
        }

        DateTimeOffset? date = null;
        if (message.Headers.GetFirst("Date") is { } d && DateTimeOffset.TryParse(Unfold(d.Value), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTimeOffset parsed))
        {
            date = parsed;
        }

        return new AutocryptMessage
        {
            FromAddress = froms[0],
            FromAddressCount = froms.Count,
            Date = date,
            Received = received,
            AutocryptHeaders = message.Headers.GetAll("Autocrypt").Select(static h => Unfold(h.Value)).ToList(),
            IsAutoGeneratedReport = message.MediaType == "multipart/report",
            IsSpam = isSpam,
        };
    }

    private static string Unfold(string value) => value.Replace("\r\n", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal).Trim();
}
