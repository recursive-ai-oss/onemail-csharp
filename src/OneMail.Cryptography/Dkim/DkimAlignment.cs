using System.Globalization;

namespace RecursiveAI.OneMail.Cryptography.Dkim;

/// <summary>How a signature's signing domain relates to the domain of the From address (the question behind DMARC's DKIM alignment, without any policy).</summary>
public enum DkimAlignment
{
    /// <summary>The domains are unrelated, or one of them is missing or unreadable.</summary>
    None = 0,

    /// <summary>One domain is a parent of the other (<c>example.com</c> signed a message from <c>mail.example.com</c>, or the reverse). DMARC's relaxed alignment also accepts two sibling subdomains; telling those apart needs the public suffix list, which this library does not carry, so siblings report <see cref="None"/>.</summary>
    ParentOrChild = 1,

    /// <summary>The domains are the same (compared case-insensitively, internationalised names in their ASCII form).</summary>
    Exact = 2,
}

public sealed partial class DkimSignatureResult
{
    /// <summary>
    /// Compares the signing domain (<c>d=</c>) with the domain of a From address. Only a relation is reported: nothing here evaluates a DMARC policy,
    /// and it does not look at <see cref="Status"/>, so test <see cref="IsPass"/> as well before relying on it. The signature covers the From
    /// the verifier saw (unless <c>AllowUnsignedFromInstances</c> is set), so pass the address of that field.
    /// </summary>
    /// <param name="fromAddress">The From address (<c>user@example.com</c>, with or without angle brackets or a display name) or a bare domain.</param>
    /// <returns><see cref="DkimAlignment.None"/> when either domain is missing or cannot be read as a domain name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fromAddress"/> is null.</exception>
    public DkimAlignment CheckAlignment(string fromAddress)
    {
        ArgumentNullException.ThrowIfNull(fromAddress);
        string? signer = NormalizeDomain(Domain);
        string? from = NormalizeDomain(fromAddress.Contains('@', StringComparison.Ordinal) ? fromAddress[(fromAddress.LastIndexOf('@') + 1)..] : fromAddress);
        if (signer is null || from is null)
        {
            return DkimAlignment.None;
        }

        if (string.Equals(signer, from, StringComparison.Ordinal))
        {
            return DkimAlignment.Exact;
        }

        return from.EndsWith("." + signer, StringComparison.Ordinal) || signer.EndsWith("." + from, StringComparison.Ordinal)
            ? DkimAlignment.ParentOrChild
            : DkimAlignment.None;
    }

    private static string? NormalizeDomain(string? d)
    {
        if (d is null)
        {
            return null;
        }

        d = d.Trim().Trim('>', '<', ' ', '.');
        if (d.Length == 0 || d.Length > 253 || d.Contains(' ', StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return new IdnMapping().GetAscii(d).ToLower(CultureInfo.InvariantCulture);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
