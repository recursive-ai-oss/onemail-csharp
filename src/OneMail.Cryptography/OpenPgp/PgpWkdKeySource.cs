using RecursiveAI.OneCrypto.OpenPgp;
using RecursiveAI.OneCrypto.Online;

namespace RecursiveAI.OneMail.Cryptography.OpenPgp;

/// <summary>
/// An <see cref="IPgpKeySource"/> that asks a Web Key Directory (draft-koch-openpgp-webkey-service) for the certificates published for an address.
/// It adds nothing to OneCrypto's client: the request limits, the network guard (a directory on a private address is refused unless the fetcher allows it),
/// redirects, the HTTP cache and the choice between the advanced and the direct method all belong to the <see cref="WkdClient"/> and <see cref="WkdOptions"/> passed in.
/// The response is cut down to the User ID that names the address (OneCrypto's <c>KeyDiscovery.ForAddress</c>), so a directory cannot hand back other people's certificates.
/// </summary>
/// <remarks>
/// A directory is evidence that a key was published for the address, nothing more. <see cref="PgpKeySource.ResolveRecipientsAsync"/> still requires that the certificate's own key
/// certified the User ID and that a key can encrypt at the reference time.
/// </remarks>
public sealed class PgpWkdKeySource : IPgpKeySource
{
    private readonly WkdClient _client;
    private readonly WkdOptions? _options;

    /// <summary>Creates a source over a Web Key Directory client.</summary>
    /// <param name="client">OneCrypto's client; it owns the transport, limits and cache.</param>
    /// <param name="options">The lookup choices; null for OneCrypto's defaults.</param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is null.</exception>
    public PgpWkdKeySource(WkdClient client, WkdOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _options = options;
    }

    /// <summary>Looks the address up in its domain's directory.</summary>
    /// <param name="address">A bare address such as <c>alice@example.org</c>.</param>
    /// <param name="cancellationToken">Cancels the request; the only exception this method throws for a network operation.</param>
    /// <returns>The certificates with a User ID for the address; empty when the directory has none, is unreachable, or answers with something that is not a key (no network failure throws).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> is null.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public async ValueTask<IReadOnlyList<PgpCertificate>> FindAsync(string address, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        WkdLookupResult found = await _client.LookupAsync(address, _options, cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess)
        {
            return [];
        }

        return [.. KeyDiscovery.ForAddress(found.Body, address).Certificates];
    }
}
