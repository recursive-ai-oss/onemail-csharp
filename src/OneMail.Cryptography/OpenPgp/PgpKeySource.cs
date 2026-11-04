using RecursiveAI.OneCrypto.OpenPgp;
using RecursiveAI.OneMail.Mime;

namespace RecursiveAI.OneMail.Cryptography.OpenPgp;

/// <summary>
/// Where the certificates for an address come from. OneMail keeps no keys of its own: a caller supplies a source (an in-memory list from
/// <see cref="PgpKeySource.FromCertificates"/>, a GnuPG keyring read by OneCrypto, a Web Key Directory lookup through OneCrypto's online package, a database).
/// </summary>
public interface IPgpKeySource
{
    /// <summary>Finds the certificates that name <paramref name="address"/> in one of their user IDs.</summary>
    /// <param name="address">A bare address such as <c>alice@example.org</c>. Matched without regard to case.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The certificates found, possibly none. Whether a certificate is usable is decided later, at the reference time of the operation.</returns>
    ValueTask<IReadOnlyList<PgpCertificate>> FindAsync(string address, CancellationToken cancellationToken = default);
}

/// <summary>Lookups of certificates by recipient address over an <see cref="IPgpKeySource"/>.</summary>
public static class PgpKeySource
{
    private sealed class ListSource(IReadOnlyList<PgpCertificate> certificates) : IPgpKeySource
    {
        public ValueTask<IReadOnlyList<PgpCertificate>> FindAsync(string address, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<PgpCertificate> found = certificates
                .Where(c => c.UserIds.Any(u => PgpMimeProcessor.UserIdAddress(u).Equals(address, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            return ValueTask.FromResult(found);
        }
    }

    /// <summary>A source over a fixed list of certificates, matching by the address in each user ID.</summary>
    /// <param name="certificates">The certificates; the list is copied.</param>
    /// <returns>The source.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="certificates"/> is null.</exception>
    public static IPgpKeySource FromCertificates(IEnumerable<PgpCertificate> certificates)
    {
        ArgumentNullException.ThrowIfNull(certificates);
        return new ListSource(certificates.ToList());
    }

    /// <summary>
    /// Finds one certificate per address that can encrypt at <paramref name="at"/> (bound, unexpired, not revoked, with an encryption-capable key) and whose own key certified a User ID naming the address.
    /// </summary>
    /// <param name="source">Where to look.</param>
    /// <param name="addresses">The recipient addresses.</param>
    /// <param name="at">The reference time.</param>
    /// <param name="cancellationToken">Cancels the lookups.</param>
    /// <returns>The usable certificates in address order, and the addresses for which none was found or none was usable.</returns>
    public static async Task<(IReadOnlyList<PgpCertificate> Certificates, IReadOnlyList<string> Missing)> ResolveRecipientsAsync(
        IPgpKeySource source, IEnumerable<string> addresses, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(addresses);
        var certificates = new List<PgpCertificate>();
        var missing = new List<string>();
        foreach (string address in addresses)
        {
            IReadOnlyList<PgpCertificate> found = await source.FindAsync(address, cancellationToken).ConfigureAwait(false);
            // A User ID counts only when the certificate's own key certified it: an uncertified User ID packet can be added to anyone's public certificate,
            // and mail encrypted to the address would then go to the key of whoever added it.
            PgpCertificate? usable = found.Where(c => c.UserIds.Any(u => PgpMimeProcessor.UserIdAddress(u).Equals(address, StringComparison.OrdinalIgnoreCase) && PgpUserIds.HasSelfSignature(c, u, at)))
                .FirstOrDefault(c => c.Keys.Any(k => k.StateAt(at) is { IsValid: true } st && (st.Usage & (PgpKeyUsage.EncryptCommunications | PgpKeyUsage.EncryptStorage)) != 0));
            if (usable is null)
            {
                missing.Add(address);
            }
            else if (!certificates.Any(c => c.Fingerprint.AsSpan().SequenceEqual(usable.Fingerprint)))
            {
                certificates.Add(usable);
            }
        }

        return (certificates, missing);
    }

    /// <summary>The bare addresses of the <c>To</c>, <c>Cc</c> and <c>Bcc</c> headers of <paramref name="message"/>, without duplicates, for <see cref="ResolveRecipientsAsync"/>.</summary>
    /// <param name="message">The message whose headers are read.</param>
    /// <returns>The addresses in header order.</returns>
    public static IReadOnlyList<string> RecipientAddresses(MimeEntity message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var list = new List<string>();
        foreach (string field in new[] { "To", "Cc", "Bcc" })
        {
            foreach (MimeHeader h in message.Headers.GetAll(field))
            {
                foreach (MimeMailbox m in AddressParser.Flatten(AddressParser.Parse(h.RawValue.Span)))
                {
                    if (!list.Contains(m.UnquotedAddress, StringComparer.OrdinalIgnoreCase))
                    {
                        list.Add(m.UnquotedAddress);
                    }
                }
            }
        }

        return list;
    }
}
