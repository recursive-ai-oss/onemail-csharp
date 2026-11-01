using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneCrypto.Primitives;
using RecursiveAI.OneMail.Mime;

namespace RecursiveAI.OneMail.Cryptography.Dkim;

/// <summary>
/// A private key for signing DKIM and ARC headers: RSA of at least 2048 bits, or Ed25519. Weak keys are refused at construction (RFC 8301 §3.2).
/// Disposing zeroes an Ed25519 seed; an RSA key passed in stays the caller's to dispose.
/// </summary>
public sealed class DkimSigningKey : IDisposable
{
    private static readonly byte[] Ed25519Pkcs8Prefix = [0x30, 0x2E, 0x02, 0x01, 0x00, 0x30, 0x05, 0x06, 0x03, 0x2B, 0x65, 0x70, 0x04, 0x22, 0x04, 0x20];

    private readonly RSA? _rsa;
    private readonly byte[]? _seed;

    private DkimSigningKey(RSA? rsa, byte[]? seed)
    {
        _rsa = rsa;
        _seed = seed;
    }

    /// <summary>The signature algorithm this key makes: <see cref="DkimAlgorithm.RsaSha256"/> or <see cref="DkimAlgorithm.Ed25519Sha256"/>.</summary>
    public DkimAlgorithm Algorithm => _rsa is not null ? DkimAlgorithm.RsaSha256 : DkimAlgorithm.Ed25519Sha256;

    /// <summary>The key size in bits: the RSA modulus, or 256.</summary>
    public int KeyBits => _rsa?.KeySize ?? 256;

    /// <summary>Wraps an RSA private key.</summary>
    /// <param name="rsa">A key with a private part, at least 2048 bits. It is not disposed by this object.</param>
    /// <returns>The signing key.</returns>
    /// <exception cref="ArgumentException">The key is smaller than 2048 bits.</exception>
    public static DkimSigningKey FromRsa(RSA rsa)
    {
        ArgumentNullException.ThrowIfNull(rsa);
        if (rsa.KeySize < 2048)
        {
            throw new ArgumentException("RSA keys under 2048 bits are refused for new DKIM signatures (RFC 8301).", nameof(rsa));
        }

        return new DkimSigningKey(rsa, null);
    }

    /// <summary>Wraps an Ed25519 private key given as its 32-byte seed (RFC 8032 §5.1.5).</summary>
    /// <param name="seed">The seed; copied.</param>
    /// <returns>The signing key.</returns>
    /// <exception cref="ArgumentException">The seed is not 32 bytes.</exception>
    public static DkimSigningKey FromEd25519Seed(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != Ed25519.SeedSize)
        {
            throw new ArgumentException("An Ed25519 seed is 32 bytes.", nameof(seed));
        }

        return new DkimSigningKey(null, seed.ToArray());
    }

    /// <summary>Reads a PEM private key: an RSA key (PKCS #1 or PKCS #8) or an Ed25519 PKCS #8 key.</summary>
    /// <param name="pem">The PEM text.</param>
    /// <returns>The signing key; for RSA this object owns the imported key.</returns>
    /// <exception cref="ArgumentException">The text is not a supported key, or the RSA key is under 2048 bits.</exception>
    public static DkimSigningKey FromPem(string pem)
    {
        ArgumentNullException.ThrowIfNull(pem);
        try
        {
            int begin = pem.IndexOf("-----BEGIN PRIVATE KEY-----", StringComparison.Ordinal);
            if (begin >= 0)
            {
                int end = pem.IndexOf("-----END PRIVATE KEY-----", begin, StringComparison.Ordinal);
                if (end > begin)
                {
                    byte[] der = Convert.FromBase64String(pem[(begin + 27)..end].Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal).Trim());
                    if (der.Length == 48 && der.AsSpan(0, 16).SequenceEqual(Ed25519Pkcs8Prefix))
                    {
                        return FromEd25519Seed(der.AsSpan(16));
                    }
                }
            }

            var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            if (rsa.KeySize < 2048)
            {
                rsa.Dispose();
                throw new ArgumentException("RSA keys under 2048 bits are refused for new DKIM signatures (RFC 8301).", nameof(pem));
            }

            return new DkimSigningKey(rsa, null);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            throw new ArgumentException("Not a supported private key.", nameof(pem), e);
        }
    }

    /// <summary>The DNS TXT record that publishes this key's public half, for example <c>v=DKIM1; k=rsa; p=...</c>.</summary>
    /// <returns>The record text.</returns>
    public string ToKeyRecord()
    {
        if (_rsa is not null)
        {
            return "v=DKIM1; k=rsa; p=" + Convert.ToBase64String(_rsa.ExportSubjectPublicKeyInfo());
        }

        byte[] pub = new byte[Ed25519.PublicKeySize];
        Ed25519.DerivePublicKey(_seed, pub);
        return "v=DKIM1; k=ed25519; p=" + Convert.ToBase64String(pub);
    }

    internal byte[] Sign(byte[] data)
    {
        if (_rsa is not null)
        {
            return _rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }

        // RFC 8463 §3: Ed25519 over the SHA-256 digest of the canonicalised data.
        byte[] sig = new byte[Ed25519.SignatureSize];
        if (!Ed25519.TrySign(_seed, SHA256.HashData(data), sig, out int n) || n != sig.Length)
        {
            throw new CryptographicException("Ed25519 signing failed.");
        }

        return sig;
    }

    /// <summary>Zeroes the Ed25519 seed, if any.</summary>
    public void Dispose()
    {
        if (_seed is not null)
        {
            CryptographicOperations.ZeroMemory(_seed);
        }
    }
}

/// <summary>Settings for <see cref="DkimSigner"/>.</summary>
public sealed class DkimSignerOptions
{
    /// <summary>The signing domain (<c>d=</c>). Required.</summary>
    public string Domain { get; init; } = string.Empty;

    /// <summary>The selector (<c>s=</c>). Required.</summary>
    public string Selector { get; init; } = string.Empty;

    /// <summary>The signing identity (<c>i=</c>), for example <c>joe@mail.example.com</c>; it must be in <see cref="Domain"/>. Null leaves it out.</summary>
    public string? Identity { get; init; }

    /// <summary>Header canonicalisation. Default <see cref="DkimCanonicalization.Relaxed"/>, which survives rewrapping in transit.</summary>
    public DkimCanonicalization HeaderCanonicalization { get; init; } = DkimCanonicalization.Relaxed;

    /// <summary>Body canonicalisation. Default <see cref="DkimCanonicalization.Relaxed"/>.</summary>
    public DkimCanonicalization BodyCanonicalization { get; init; } = DkimCanonicalization.Relaxed;

    /// <summary>The header names to sign, case-insensitive; every instance present is signed. Null uses <see cref="DefaultHeaders"/>. From is always signed.</summary>
    public IReadOnlyList<string>? HeadersToSign { get; init; }

    /// <summary>
    /// Over-signing (RFC 6376 §8.15): list each header once more than it occurs, so that adding another copy afterwards, or adding a header
    /// that was absent, breaks the signature. Default false.
    /// </summary>
    public bool OverSign { get; init; }

    /// <summary>Add <c>l=</c> with the canonicalised body length. Default false; <c>l=</c> lets anyone append to the body without breaking the signature (RFC 6376 §8.2).</summary>
    public bool IncludeBodyLength { get; init; }

    /// <summary>Add <c>t=</c>, the signing time. Default true.</summary>
    public bool IncludeTimestamp { get; init; } = true;

    /// <summary>Add <c>x=</c> this long after the signing time. Null (the default) adds none.</summary>
    public TimeSpan? Lifetime { get; init; }

    /// <summary>The clock for <c>t=</c> and <c>x=</c>. Default <see cref="TimeProvider.System"/>; set a fixed one for reproducible output.</summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>The algorithm to use; null takes it from the key. <see cref="DkimAlgorithm.RsaSha1"/> is refused.</summary>
    public DkimAlgorithm? Algorithm { get; init; }

    /// <summary>The headers signed by default: the ones that carry a message's meaning and that a transit hop does not rewrite.</summary>
    public static IReadOnlyList<string> DefaultHeaders { get; } =
    [
        "From", "Sender", "Reply-To", "Subject", "Date", "Message-ID", "To", "Cc", "In-Reply-To", "References",
        "MIME-Version", "Content-Type", "Content-Transfer-Encoding", "Content-ID", "Content-Description",
        "List-Id", "List-Unsubscribe", "List-Unsubscribe-Post",
    ];
}

/// <summary>
/// Signs messages with DKIM (RFC 6376; RSA-SHA256, and Ed25519-SHA256 per RFC 8463). Canonicalisation works on the message's bytes as given.
/// New signatures refuse weak choices: RSA under 2048 bits and rsa-sha1 cannot be made.
/// </summary>
public sealed class DkimSigner
{
    private readonly DkimSigningKey _key;
    private readonly DkimSignerOptions _options;

    /// <summary>Creates a signer.</summary>
    /// <param name="key">The private key.</param>
    /// <param name="options">The signing settings.</param>
    /// <exception cref="ArgumentException">The domain or selector is not a plain ASCII DNS name, a header name to sign cannot be written in h=, the identity is outside the domain, or rsa-sha1 or an algorithm that does not fit the key was asked for.</exception>
    public DkimSigner(DkimSigningKey key, DkimSignerOptions options)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(options);
        _key = key;
        _options = options;
        if (!SignatureEngine.IsDomainName(options.Domain) || !SignatureEngine.IsDomainName(options.Selector))
        {
            throw new ArgumentException("Domain and Selector must be ASCII DNS names.", nameof(options));
        }

        if (options.Algorithm == DkimAlgorithm.RsaSha1)
        {
            throw new ArgumentException("rsa-sha1 is not allowed for new signatures (RFC 8301).", nameof(options));
        }

        if (options.Algorithm is { } a && a != key.Algorithm)
        {
            throw new ArgumentException("The key makes " + key.Algorithm + ", not " + a + ".", nameof(options));
        }

        if (options.HeadersToSign is { } names && names.Any(static n => n.Length == 0 || n.Any(static c => c is ':' or ';' or '=' || c <= 32 || c > 126)))
        {
            // A name is written into the h= tag; one with a colon, semicolon or space would add tags or headers to the signature.
            throw new ArgumentException("A header name to sign has characters that cannot be written in h=.", nameof(options));
        }

        if (options.Identity is { } id)
        {
            int at = id.LastIndexOf('@');
            string d = at < 0 ? string.Empty : id[(at + 1)..];
            if (at < 0 || !(d.Equals(options.Domain, StringComparison.OrdinalIgnoreCase) || d.EndsWith("." + options.Domain, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException("Identity must be in the signing domain.", nameof(options));
            }

            if (id.Any(c => c is ';' or '\r' or '\n' || c > 126 || c < 33))
            {
                throw new ArgumentException("Identity has characters that cannot be written in a tag.", nameof(options));
            }
        }
    }

    /// <summary>Makes the <c>DKIM-Signature</c> header field for a message.</summary>
    /// <param name="message">The complete message as bytes.</param>
    /// <returns>The header field text, folded, ending in the message's line end style. Put it above the other headers.</returns>
    /// <exception cref="ArgumentException">The message has no From header.</exception>
    public string CreateSignatureHeader(ReadOnlyMemory<byte> message)
    {
        var m = new MessageParts(message);
        string eol = message.Span.IndexOf("\r\n"u8) < 0 && message.Span.IndexOf((byte)'\n') >= 0 ? "\n" : "\r\n";
        IReadOnlyList<string> wanted = _options.HeadersToSign ?? DkimSignerOptions.DefaultHeaders;
        var names = new List<string>();
        bool hasFrom = false;
        foreach (string w in wanted.Append("From").Distinct(StringComparer.OrdinalIgnoreCase))
        {
            int present = m.Headers.Count(h => h.Name.Equals(w, StringComparison.OrdinalIgnoreCase));
            if (w.Equals("From", StringComparison.OrdinalIgnoreCase))
            {
                hasFrom = present > 0;
            }

            int times = present + (_options.OverSign ? 1 : 0);
            for (int i = 0; i < times; i++)
            {
                names.Add(w);
            }
        }

        if (!hasFrom)
        {
            throw new ArgumentException("A message without a From header cannot be signed.", nameof(message));
        }

        BodyHashInfo body;
        using (var sink = new BodySink(HashAlgorithmName.SHA256, -1))
        {
            DkimCanonicalizer.CanonicalizeBody(m.Bytes.Span[m.BodyStart..], m.BodyStart, _options.BodyCanonicalization, sink);
            body = new BodyHashInfo(sink.Finish(), sink.Total, sink.Hashed, sink.CoveredSourceEnd);
        }

        TimeProvider time = _options.TimeProvider ?? TimeProvider.System;
        long now = time.GetUtcNow().ToUnixTimeSeconds();
        var tags = new List<string>
        {
            "v=1",
            "a=" + (_key.Algorithm == DkimAlgorithm.Ed25519Sha256 ? "ed25519-sha256" : "rsa-sha256"),
            "c=" + Canon(_options.HeaderCanonicalization) + "/" + Canon(_options.BodyCanonicalization),
            "d=" + _options.Domain,
            "s=" + _options.Selector,
        };
        if (_options.Identity is { } ident)
        {
            tags.Add("i=" + ident);
        }

        if (_options.IncludeTimestamp)
        {
            tags.Add("t=" + now.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (_options.Lifetime is { } life)
        {
            tags.Add("x=" + (now + (long)life.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (_options.IncludeBodyLength)
        {
            tags.Add("l=" + body.Total.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        tags.Add("h=" + string.Join(':', names));
        tags.Add("bh=" + Convert.ToBase64String(body.Hash));

        string prefix = Fold("DKIM-Signature:", tags, eol, out int lastLineLength);

        // Phase one: the header as it is hashed, "b=" empty at the end (RFC 6376 §3.7).
        var data = new List<byte>(1024);
        SignatureEngine.AppendSelectedHeaders(m, names, _options.HeaderCanonicalization, -1, data, [], []);
        DkimCanonicalizer.AppendHeader(data, Encoding.Latin1.GetBytes(prefix), _options.HeaderCanonicalization, trailingCrlf: false);
        byte[] signature = _key.Sign([.. data]);

        // Phase two: the same text with the signature folded in after "b=".
        string b64 = Convert.ToBase64String(signature);
        var sb = new StringBuilder(prefix);
        int col = lastLineLength;
        int i2 = 0;
        while (i2 < b64.Length)
        {
            int room = 78 - col;
            if (room < 4)
            {
                sb.Append(eol).Append(' ');
                col = 1;
                room = 77;
            }

            int take = Math.Min(room, b64.Length - i2);
            sb.Append(b64, i2, take);
            i2 += take;
            col += take;
        }

        sb.Append(eol);
        return sb.ToString();
    }

    /// <summary>Signs a message.</summary>
    /// <param name="message">The complete message as bytes.</param>
    /// <returns>The message with the <c>DKIM-Signature</c> header added above its headers; every original byte is kept.</returns>
    public byte[] Sign(ReadOnlyMemory<byte> message)
    {
        byte[] header = Encoding.Latin1.GetBytes(CreateSignatureHeader(message));
        byte[] result = new byte[header.Length + message.Length];
        header.CopyTo(result, 0);
        message.Span.CopyTo(result.AsSpan(header.Length));
        return result;
    }

    /// <summary>Signs a parsed message.</summary>
    /// <param name="entity">The message entity; it is not changed.</param>
    /// <returns>The signed message bytes (the entity's bytes with the header added above).</returns>
    public byte[] Sign(MimeEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return Sign(entity.ToArray());
    }

    private static string Canon(DkimCanonicalization c) => c == DkimCanonicalization.Simple ? "simple" : "relaxed";

    /// <summary>
    /// Lays the tags out in lines of at most 78 characters, breaking only between tags, and after a colon inside <c>h=</c>, with continuation lines
    /// starting with one space (RFC 5322 §2.2.3). Ends with "b=" and no line end, leaving room for the signature on the same line.
    /// </summary>
    internal static string Fold(string name, List<string> tags, string eol, out int lastLineLength)
    {
        var sb = new StringBuilder(name);
        int col = name.Length;
        void Put(string text, bool canBreak, bool space)
        {
            int need = text.Length + (space ? 1 : 0);
            if (canBreak && col + need > 78)
            {
                sb.Append(eol).Append(' ');
                col = 1;
                space = false;
            }
            else if (space)
            {
                sb.Append(' ');
                col++;
            }

            sb.Append(text);
            col += text.Length;
        }

        foreach (string tag in tags)
        {
            if (tag.StartsWith("h=", StringComparison.Ordinal) && tag.Length > 60)
            {
                string[] parts = tag[2..].Split(':');
                Put("h=" + parts[0] + (parts.Length > 1 ? ":" : ";"), true, true);
                for (int i = 1; i < parts.Length; i++)
                {
                    Put(parts[i] + (i < parts.Length - 1 ? ":" : ";"), true, false);
                }
            }
            else if (tag.StartsWith("bh=", StringComparison.Ordinal) && tag.Length > 70)
            {
                // bh= is 47 characters for SHA-256, so it fits a line; this guards a longer hash.
                Put(tag + ";", true, true);
            }
            else
            {
                Put(tag + ";", true, true);
            }
        }

        // The b tag needs its name and at least a few characters of value on the line.
        if (col + 1 + 2 + 4 > 78)
        {
            sb.Append(eol).Append(' ');
            col = 1;
        }
        else
        {
            sb.Append(' ');
            col++;
        }

        sb.Append("b=");
        col += 2;
        lastLineLength = col;
        return sb.ToString();
    }
}
