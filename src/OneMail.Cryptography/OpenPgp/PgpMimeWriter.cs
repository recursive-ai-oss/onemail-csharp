using System.Text;
using RecursiveAI.OneCrypto.OpenPgp;
using RecursiveAI.OneCrypto.OpenPgp.Packets;
using RecursiveAI.OneCrypto.Policy;
using RecursiveAI.OneMail.Cryptography.Smime;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Mime.Transfer;

namespace RecursiveAI.OneMail.Cryptography.OpenPgp;

/// <summary>Choices for signing a message with <see cref="PgpMimeSigner"/>.</summary>
public sealed class PgpMimeSignOptions
{
    /// <summary>The reference time written into the signatures. Required: the library never reads a clock.</summary>
    public required DateTimeOffset Now { get; init; }

    /// <summary>The unlocked signing keys (<see cref="PgpSecretKey.Unlock"/>). At least one is required.</summary>
    public required IReadOnlyList<PgpSecretKey> Signers { get; init; }

    /// <summary>The hash for new signatures; null lets OneCrypto choose (SHA-512, or the digest that matches the curve). Weak hashes are refused by the policy.</summary>
    public PgpHashAlgorithm? Hash { get; init; }

    /// <summary>The policy for new signatures; null means <see cref="CryptoPolicy.Standard"/>.</summary>
    public CryptoPolicy? Policy { get; init; }

    /// <summary>The boundary of the <c>multipart/signed</c>; null generates a random one. Pass one for reproducible output. An error when it occurs in the content.</summary>
    public string? Boundary { get; init; }

    /// <summary>
    /// Headers to put inside the signed part and repeat on the returned <c>multipart/signed</c> (<see cref="PgpProtectedHeaders"/>). Default null: the signature covers only the content,
    /// and the caller adds <c>From</c>, <c>To</c> … to the result, which then are not covered. The outer <c>Subject</c> is never obscured when only signing.
    /// </summary>
    public PgpProtectedHeaders? ProtectedHeaders { get; init; }
}

/// <summary>Choices for encrypting a message with <see cref="PgpMimeEncryptor"/>.</summary>
public sealed class PgpMimeEncryptOptions
{
    /// <summary>The reference time for key validity and for any signatures. Required: the library never reads a clock.</summary>
    public required DateTimeOffset Now { get; init; }

    /// <summary>The recipients' certificates. At least one, unless <see cref="Passwords"/> are given. A recipient without a usable encryption key at <see cref="Now"/> is an error.</summary>
    public IReadOnlyList<PgpCertificate> Recipients { get; init; } = [];

    /// <summary>Passwords (UTF-8 octets) for password-only recipients, in addition to <see cref="Recipients"/>.</summary>
    public IReadOnlyList<byte[]> Passwords { get; init; } = [];

    /// <summary>
    /// Signing keys for the combined method (RFC 3156 §6.2): the signature is inside the encrypted OpenPGP message, so it is hidden from everyone but the recipients.
    /// Empty writes an encrypted-only message. To sign and then encrypt a <c>multipart/signed</c>, pass the output of <see cref="PgpMimeSigner"/> as the content instead.
    /// </summary>
    public IReadOnlyList<PgpSecretKey> Signers { get; init; } = [];

    /// <summary>The hash for new signatures when <see cref="Signers"/> are given; null lets OneCrypto choose.</summary>
    public PgpHashAlgorithm? Hash { get; init; }

    /// <summary>The policy for new signatures and encryption; null means <see cref="CryptoPolicy.Standard"/>.</summary>
    public CryptoPolicy? Policy { get; init; }

    /// <summary>The boundary of the <c>multipart/encrypted</c>; null generates a random one.</summary>
    public string? Boundary { get; init; }

    /// <summary>Force the older SEIPD v1 container (readable by every OpenPGP implementation). Default false: OneCrypto picks from the recipients' feature flags.</summary>
    public bool ForceSeipd1 { get; init; }

    /// <summary>
    /// Write the encrypted session key packets without naming their recipients (a version 3 packet carries an all-zero key ID; a version 6 packet carries no key
    /// version or fingerprint), for blind copies. A reader then tries each secret key it holds. Default false.
    /// </summary>
    public bool HiddenRecipients { get; init; }

    /// <summary>
    /// Headers to put inside the encrypted content (and so, with <see cref="Signers"/>, inside the signature) and to repeat on the returned <c>multipart/encrypted</c>, with
    /// the outer <c>Subject</c> optionally replaced by <c>...</c>. Default null. When the content is a <c>multipart/signed</c> whose signed part already has
    /// <c>protected-headers</c> (made with <see cref="PgpMimeSignOptions.ProtectedHeaders"/>), the content is left as it is and only the outer fields are written; pass the same headers to both.
    /// </summary>
    public PgpProtectedHeaders? ProtectedHeaders { get; init; }
}

/// <summary>The canonical form RFC 3156 signs: 7 bit, CRLF line ends, no trailing white space.</summary>
internal static class PgpCanonical
{
    /// <summary>
    /// The bytes that get signed for <paramref name="content"/> (RFC 3156 §3 and §5): a copy with every 8-bit or binary leaf
    /// and every text leaf with trailing white space or a line starting <c>From </c> re-encoded (quoted-printable or base64), trailing white space
    /// removed from header lines, and CRLF line ends. An entity that already follows these rules keeps every stored byte.
    /// </summary>
    internal static byte[] Canonicalise(MimeEntity content)
    {
        byte[] stored = content.ToArray();
        MimeEntity copy = MimeEntity.Parse(stored);
        bool changed = Fix(copy);
        byte[] bytes = changed ? copy.ToArray() : stored;
        return SmimeContent.NormaliseLineEnds(bytes) ?? bytes;
    }

    private static bool Fix(MimeEntity entity)
    {
        bool changed = false;
        foreach (MimeHeader h in entity.Headers.ToArray())
        {
            if (HasTrailingWhitespace(h.RawValue.Span))
            {
                // RFC 3156 §3: trailing white space in header lines is removed; a line of only white space is removed whole.
                string value = string.Join("\r\n", h.Value.Split('\n').Select(static l => l.TrimEnd(' ', '\t', '\r')).Where(static l => l.Length > 0));
                h.SetValue(value.Length == 0 ? string.Empty : " " + value.TrimStart());
                changed = true;
            }
        }

        switch (entity)
        {
            case MimePart part:
                ContentTransferEncoding cte = part.TransferEncoding;
                if (cte is ContentTransferEncoding.EightBit or ContentTransferEncoding.Binary or ContentTransferEncoding.SevenBit or ContentTransferEncoding.Unknown)
                {
                    bool needsBase64 = false;
                    bool needs = cte == ContentTransferEncoding.Binary || Scan(part, out needsBase64);
                    if (needs)
                    {
                        byte[] decoded = part.GetDecodedContent();
                        bool text = part.MediaTopLevelType == "text" && cte != ContentTransferEncoding.Binary && !needsBase64;
                        part.SetDecodedContent(decoded, text ? ContentTransferEncoding.QuotedPrintable : ContentTransferEncoding.Base64);
                        changed = true;
                    }
                }

                break;
            case MimeMultipart multipart:
                foreach (MimeEntity child in multipart.Children)
                {
                    changed |= Fix(child);
                }

                break;
            case MimeMessagePart message when message.Message is { } inner:
                changed |= Fix(inner);
                break;
        }

        return changed;
    }

    private static bool HasTrailingWhitespace(ReadOnlySpan<byte> raw)
    {
        for (int i = 0; i < raw.Length; i++)
        {
            if ((raw[i] == ' ' || raw[i] == '\t') && (i + 1 == raw.Length || raw[i + 1] == '\r' || raw[i + 1] == '\n'))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a 7bit/8bit body needs re-encoding: a byte above 0x7F or NUL, trailing white space, or a line starting <c>From </c> (base64 then, as quoted-printable would not hide it).</summary>
    private static bool Scan(MimePart part, out bool needsBase64)
    {
        needsBase64 = false;
        bool needs = false;
        using Stream s = part.OpenRawContent();
        byte[] all;
        using (var ms = new MemoryStream())
        {
            s.CopyTo(ms);
            all = ms.ToArray();
        }

        int lineStart = 0;
        for (int i = 0; i <= all.Length; i++)
        {
            bool end = i == all.Length || all[i] == '\n' || all[i] == '\r';
            if (i < all.Length && (all[i] >= 0x80 || all[i] == 0))
            {
                needs = true;
            }

            if (end)
            {
                if (i > lineStart && (all[i - 1] == ' ' || all[i - 1] == '\t'))
                {
                    needs = true;
                }

                if (i - lineStart >= 5 && all.AsSpan(lineStart, 5).SequenceEqual("From "u8))
                {
                    needs = true;
                    needsBase64 = true;
                }

                lineStart = i + 1;
            }
        }

        return needs;
    }

    /// <summary>The micalg token for a hash (RFC 3156 §5: <c>pgp-</c> plus the lower-case registered hash name).</summary>
    internal static string MicalgFor(PgpHashAlgorithm hash) => hash switch
    {
        PgpHashAlgorithm.Md5 => "pgp-md5",
        PgpHashAlgorithm.Sha1 => "pgp-sha1",
        PgpHashAlgorithm.Ripemd160 => "pgp-ripemd160",
        PgpHashAlgorithm.Sha256 => "pgp-sha256",
        PgpHashAlgorithm.Sha384 => "pgp-sha384",
        PgpHashAlgorithm.Sha512 => "pgp-sha512",
        PgpHashAlgorithm.Sha224 => "pgp-sha224",
        PgpHashAlgorithm.Sha3256 => "pgp-sha3-256",
        PgpHashAlgorithm.Sha3512 => "pgp-sha3-512",
        _ => "pgp-unknown",
    };

    internal static string RandomBoundary(ReadOnlySpan<byte> data)
    {
        string boundary;
        do
        {
            boundary = "=_" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        }
        while (SmimeContent.ContainsBoundary(data, boundary));
        return boundary;
    }

    internal static MimePart ArmoredPart(byte[] armored, string contentType, string fileName, string description, bool inline)
    {
        var part = new MimePart();
        part.Headers.Add("Content-Type", contentType + "; name=\"" + fileName + "\"");
        part.Headers.Add("Content-Description", description);
        part.Headers.Add("Content-Disposition", (inline ? "inline" : "attachment") + "; filename=\"" + fileName + "\"");
        byte[] crlf = SmimeContent.NormaliseLineEnds(armored) ?? armored;
        part.SetRawContent(crlf);
        return part;
    }
}

/// <summary>
/// Creates PGP/MIME signed messages (RFC 3156 §5): <c>multipart/signed</c> with the signed entity as the first part and an armored detached
/// signature (<c>application/pgp-signature</c>) as the second. OneCrypto makes the signature; this type does the MIME side: the canonical form, the
/// <c>micalg</c> token and the framing.
/// </summary>
public static class PgpMimeSigner
{
    /// <summary>
    /// Signs <paramref name="content"/> and returns the <c>multipart/signed</c> entity. The caller adds the message headers (<c>From</c>, <c>To</c> …) to the
    /// result; they are not covered by the signature.
    /// </summary>
    /// <param name="content">The entity to sign. It is not modified; a copy is brought to the canonical form (7 bit, CRLF, no trailing white space) first.</param>
    /// <param name="options">The signers and choices.</param>
    /// <returns>The new entity, whose first child holds exactly the bytes that were signed.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">No signers, or <see cref="PgpMimeSignOptions.Boundary"/> occurs in the content.</exception>
    /// <exception cref="PgpException">A key cannot sign, or the policy refuses the hash or key (<see cref="PgpFailure.PolicyViolation"/>).</exception>
    public static MimeMultipart SignDetached(MimeEntity content, PgpMimeSignOptions options)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Signers.Count == 0)
        {
            throw new ArgumentException("At least one signer is required.", nameof(options));
        }

        byte[] signedBytes = PgpCanonical.Canonicalise(options.ProtectedHeaders is { } ph ? ph.ApplyTo(content) : content);
        var armored = new MemoryStream();
        IReadOnlyList<PgpHashAlgorithm> hashes = PgpMessageWriter.SignDetached(new MemoryStream(signedBytes, writable: false), armored, new PgpWriteOptions
        {
            Now = options.Now,
            Signers = options.Signers,
            SignatureHash = options.Hash,
            Policy = options.Policy,
            Armor = true,
        });
        string micalg = string.Join(',', hashes.Select(PgpCanonical.MicalgFor).Distinct(StringComparer.Ordinal));
        string? boundary = options.Boundary;
        if (boundary is null)
        {
            boundary = PgpCanonical.RandomBoundary(signedBytes);
        }
        else if (SmimeContent.ContainsBoundary(signedBytes, boundary))
        {
            throw new ArgumentException("The boundary occurs in the content to be signed.", nameof(options));
        }

        var multipart = new MimeMultipart("signed", boundary);
        // A comma is a tspecial (RFC 2045 §5.1): several hashes need a quoted value, or a reader sees only the first.
        string micalgValue = micalg.Contains(',', StringComparison.Ordinal) ? "\"" + micalg + "\"" : micalg;
        multipart.Headers.Set("Content-Type", "multipart/signed; protocol=\"application/pgp-signature\"; micalg=" + micalgValue + "; boundary=\"" + boundary + "\"");
        // The signed part goes in as parsed bytes so nothing later can change what the signature covers (RFC 3156 §5 (6)).
        multipart.Add(MimeEntity.Parse(signedBytes));
        multipart.Add(PgpCanonical.ArmoredPart(armored.ToArray(), "application/pgp-signature", "signature.asc", "OpenPGP digital signature", inline: false));
        options.ProtectedHeaders?.ApplyOuter(multipart, encrypted: false);
        return multipart;
    }
}

/// <summary>
/// Creates PGP/MIME encrypted messages (RFC 3156 §4 and §6): <c>multipart/encrypted</c> with the <c>application/pgp-encrypted</c> control part and the
/// armored OpenPGP message. Sign-then-encrypt is the output of <see cref="PgpMimeSigner"/> given as the content; the combined method puts the
/// signature inside the encrypted message (<see cref="PgpMimeEncryptOptions.Signers"/>).
/// </summary>
public static class PgpMimeEncryptor
{
    /// <summary>
    /// Encrypts <paramref name="content"/> (headers and body in MIME canonical form) to the recipients and returns the <c>multipart/encrypted</c> entity.
    /// </summary>
    /// <param name="content">The entity to encrypt; not modified. With signers it is brought to the signing canonical form (RFC 3156 §6.2); encrypt-only content keeps 8-bit bytes (RFC 3156 §3) and gets CRLF line ends.</param>
    /// <param name="options">Recipients, signers and choices.</param>
    /// <returns>The new entity.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">Neither recipients nor passwords, or <see cref="PgpMimeEncryptOptions.Boundary"/> occurs in the encrypted data.</exception>
    /// <exception cref="PgpException">A recipient has no usable encryption key, a signer cannot sign, or the policy refuses the choice.</exception>
    public static MimeMultipart Encrypt(MimeEntity content, PgpMimeEncryptOptions options)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Recipients.Count == 0 && options.Passwords.Count == 0)
        {
            throw new ArgumentException("At least one recipient or password is required.", nameof(options));
        }

        PgpProtectedHeaders? protectedHeaders = options.ProtectedHeaders;
        if (protectedHeaders is not null && !PgpProtectedHeaders.IsSignedWrapperWithProtectedHeaders(content))
        {
            content = protectedHeaders.ApplyTo(content);
        }

        byte[] plain;
        if (options.Signers.Count > 0)
        {
            plain = PgpCanonical.Canonicalise(content);
        }
        else
        {
            byte[] stored = content.ToArray();
            plain = SmimeContent.NormaliseLineEnds(stored) ?? stored;
        }

        var armored = new MemoryStream();
        PgpMessageWriter.Write(new MemoryStream(plain, writable: false), armored, new PgpWriteOptions
        {
            Now = options.Now,
            Recipients = options.Recipients,
            Passwords = options.Passwords,
            Signers = options.Signers,
            SignatureHash = options.Hash,
            Policy = options.Policy,
            ForceSeipd1 = options.ForceSeipd1,
            HiddenRecipients = options.HiddenRecipients,
            Armor = true,
        });
        byte[] armoredBytes = armored.ToArray();
        string boundary = options.Boundary ?? PgpCanonical.RandomBoundary(armoredBytes);
        if (options.Boundary is not null && SmimeContent.ContainsBoundary(armoredBytes, boundary))
        {
            throw new ArgumentException("The boundary occurs in the encrypted data.", nameof(options));
        }

        var multipart = new MimeMultipart("encrypted", boundary);
        multipart.Headers.Set("Content-Type", "multipart/encrypted; protocol=\"application/pgp-encrypted\"; boundary=\"" + boundary + "\"");
        var control = new MimePart();
        control.Headers.Add("Content-Type", "application/pgp-encrypted");
        control.Headers.Add("Content-Description", "PGP/MIME version identification");
        control.SetRawContent("Version: 1\r\n"u8.ToArray());
        multipart.Add(control);
        multipart.Add(PgpCanonical.ArmoredPart(armoredBytes, "application/octet-stream", "encrypted.asc", "OpenPGP encrypted message", inline: true));
        protectedHeaders?.ApplyOuter(multipart, encrypted: true);
        return multipart;
    }
}
