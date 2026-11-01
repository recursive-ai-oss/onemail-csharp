namespace RecursiveAI.OneMail.Cryptography.Dkim;

/// <summary>
/// The outcome of checking one <c>DKIM-Signature</c> (or ARC) header: a status with its reason, and everything a caller needs to judge it —
/// who signed, with what, over which bytes, and whether the clock and the key's strength are acceptable. Never a bare bool.
/// </summary>
public sealed class DkimSignatureResult
{
    /// <summary>The header's position among the message's headers of that name, counted from the top, starting at 0.</summary>
    public int Index { get; internal set; }

    /// <summary>The header field name as written, for example <c>DKIM-Signature</c> or <c>ARC-Message-Signature</c>.</summary>
    public string HeaderName { get; internal set; } = string.Empty;

    /// <summary>The byte range of the whole header field in the message that was checked.</summary>
    public DkimByteRange HeaderRange { get; internal set; }

    /// <summary>The result category.</summary>
    public DkimStatus Status { get; internal set; }

    /// <summary>Why the signature has this status; <see cref="DkimReason.None"/> for a pass.</summary>
    public DkimReason Reason { get; internal set; }

    /// <summary>A short human-readable explanation, for logs and the <c>Authentication-Results</c> comment. Null for a pass.</summary>
    public string? Detail { get; internal set; }

    /// <summary>The signing domain (<c>d=</c>), if the signature got that far.</summary>
    public string? Domain { get; internal set; }

    /// <summary>The selector (<c>s=</c>).</summary>
    public string? Selector { get; internal set; }

    /// <summary>The signing identity (<c>i=</c>); for an ARC set, null.</summary>
    public string? Identity { get; internal set; }

    /// <summary>The ARC instance (<c>i=</c> of an ARC header); 0 for a DKIM signature.</summary>
    public int Instance { get; internal set; }

    /// <summary>The algorithm (<c>a=</c>), when it is one this library knows.</summary>
    public DkimAlgorithm? Algorithm { get; internal set; }

    /// <summary>The key's size in bits (RSA modulus, or 256 for Ed25519); 0 when no key was read.</summary>
    public int KeyBits { get; internal set; }

    /// <summary>Header canonicalisation (<c>c=</c>, before the slash).</summary>
    public DkimCanonicalization HeaderCanonicalization { get; internal set; }

    /// <summary>Body canonicalisation (<c>c=</c>, after the slash).</summary>
    public DkimCanonicalization BodyCanonicalization { get; internal set; }

    /// <summary>The header names listed in <c>h=</c>, in order and as written.</summary>
    public IReadOnlyList<string> SignedHeaders { get; internal set; } = [];

    /// <summary>Header names that <c>h=</c> lists more often than the message holds them (over-signing, RFC 6376 §5.4): adding such a header later breaks the signature.</summary>
    public IReadOnlyList<string> OversignedHeaders { get; internal set; } = [];

    /// <summary>
    /// Names from <c>h=</c> of which the message holds more instances than the signature covers (an added second <c>From</c>, say). Those extra
    /// instances are not signed even when the status is a pass; a reader that shows the topmost instance shows unsigned text (RFC 6376 §8.15).
    /// </summary>
    public IReadOnlyList<string> UnsignedHeaderInstances { get; internal set; } = [];

    /// <summary>The ranges of the header fields the signature covered, in hashing order.</summary>
    public IReadOnlyList<DkimByteRange> CoveredHeaders { get; internal set; } = [];

    /// <summary>The range of body bytes the signature covered. With <c>l=</c> it stops at the end of the line holding the last covered byte.</summary>
    public DkimByteRange CoveredBody { get; internal set; }

    /// <summary>The <c>l=</c> value, or null when the whole body is signed.</summary>
    public long? BodyLengthTag { get; internal set; }

    /// <summary>How many canonicalised body bytes the hash covered.</summary>
    public long BodyBytesCovered { get; internal set; }

    /// <summary>True when the signature stops short of the end of the body (<c>l=</c> smaller than the canonicalised body): content follows the signed part and is not protected.</summary>
    public bool BodyHasUnsignedContent { get; internal set; }

    /// <summary>When the signature was made (<c>t=</c>), if stated.</summary>
    public DateTimeOffset? SignedAt { get; internal set; }

    /// <summary>When the signature expires (<c>x=</c>), if stated.</summary>
    public DateTimeOffset? ExpiresAt { get; internal set; }

    /// <summary>How the signature stands against the verifier's clock.</summary>
    public DkimTimeValidity TimeValidity { get; internal set; }

    /// <summary>Things worth knowing even when the signature passes.</summary>
    public DkimWarnings Warnings { get; internal set; }

    /// <summary>The first characters of <c>b=</c>, which <c>Authentication-Results</c> uses to tell signatures apart (RFC 8601 §2.7.1).</summary>
    public string? SignatureHint { get; internal set; }

    /// <summary>True when <see cref="Status"/> is <see cref="DkimStatus.Pass"/>.</summary>
    public bool IsPass => Status == DkimStatus.Pass;

    internal void Fail(DkimStatus status, DkimReason reason, string? detail)
    {
        Status = status;
        Reason = reason;
        Detail = detail;
    }

    /// <summary>Renders the result as an <c>Authentication-Results</c> method result (RFC 8601): <c>dkim=pass header.d=example.com header.s=sel header.b=abcdefgh</c>.</summary>
    /// <returns>The text without the authserv-id.</returns>
    public override string ToString()
    {
        string method = HeaderName.StartsWith("ARC-Seal", StringComparison.OrdinalIgnoreCase) ? "arc-seal" : HeaderName.StartsWith("ARC-", StringComparison.OrdinalIgnoreCase) ? "arc-ams" : "dkim";
        string status = Status switch
        {
            DkimStatus.Pass => "pass",
            DkimStatus.Fail => "fail",
            DkimStatus.Neutral => "neutral",
            DkimStatus.TempError => "temperror",
            _ => "permerror",
        };
        var sb = new System.Text.StringBuilder(method).Append('=').Append(status);
        if (Detail is not null)
        {
            sb.Append(" (").Append(Detail.Replace('(', '[').Replace(')', ']')).Append(')');
        }
        else if (Warnings.HasFlag(DkimWarnings.WeakAlgorithm) || Warnings.HasFlag(DkimWarnings.WeakKey))
        {
            sb.Append(" (weak ").Append(Warnings.HasFlag(DkimWarnings.WeakAlgorithm) ? "algorithm" : "key").Append(')');
        }

        if (Domain is not null && IsToken(Domain))
        {
            sb.Append(" header.d=").Append(Domain);
        }

        if (Selector is not null && IsToken(Selector))
        {
            sb.Append(" header.s=").Append(Selector);
        }

        if (SignatureHint is { Length: > 0 })
        {
            sb.Append(" header.b=").Append(SignatureHint);
        }

        return sb.ToString();
    }

    private static bool IsToken(string s)
    {
        foreach (char c in s)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
            {
                return false;
            }
        }

        return s.Length > 0;
    }
}
