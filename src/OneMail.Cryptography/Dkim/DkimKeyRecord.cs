using System.Security.Cryptography;
using RecursiveAI.OneCrypto.Primitives;

namespace RecursiveAI.OneMail.Cryptography.Dkim;

/// <summary>Why a key record was refused.</summary>
public enum DkimKeyError
{
    /// <summary>No error.</summary>
    None = 0,

    /// <summary>The record is longer than the limit.</summary>
    TooLarge = 1,

    /// <summary>The tag list is malformed.</summary>
    MalformedTagList = 2,

    /// <summary>A tag appears twice.</summary>
    DuplicateTag = 3,

    /// <summary><c>v=</c> is present and is not <c>DKIM1</c>.</summary>
    UnsupportedVersion = 4,

    /// <summary>There is no <c>p=</c> tag.</summary>
    MissingPublicKey = 5,

    /// <summary><c>p=</c> is not valid base64.</summary>
    InvalidBase64 = 6,

    /// <summary><c>k=</c> names a key type this library does not know.</summary>
    UnsupportedKeyType = 7,

    /// <summary><c>p=</c> does not hold a public key of the stated type.</summary>
    InvalidPublicKey = 8,

    /// <summary>The key is larger than the limit.</summary>
    KeyTooLarge = 9,
}

/// <summary>
/// A DKIM key record: the TXT record at <c>selector._domainkey.domain</c> (RFC 6376 §3.6.1, RFC 8463 §4.2). Records come from DNS, so
/// parsing treats them as hostile: sizes are checked before decoding and every failure is a typed <see cref="DkimKeyError"/>.
/// </summary>
public sealed class DkimKeyRecord
{
    /// <summary>The longest record text accepted, in characters. 8192 holds an RSA key of about 16000 bits.</summary>
    public const int MaxRecordLength = 8192;

    /// <summary>The largest RSA modulus accepted, in bits.</summary>
    public const int MaxRsaBits = 8192;

    private static readonly byte[] Ed25519SpkiPrefix = [0x30, 0x2A, 0x30, 0x05, 0x06, 0x03, 0x2B, 0x65, 0x70, 0x03, 0x21, 0x00];

    private DkimKeyRecord(string keyType, byte[] publicKey)
    {
        KeyType = keyType;
        PublicKey = publicKey;
    }

    /// <summary>The key type: <c>rsa</c> (the default) or <c>ed25519</c>.</summary>
    public string KeyType { get; private init; }

    /// <summary>The decoded <c>p=</c> value: a SubjectPublicKeyInfo (RSA; a bare PKCS #1 RSAPublicKey is also accepted) or the 32 raw bytes (Ed25519). Empty when <see cref="Revoked"/>.</summary>
    public byte[] PublicKey { get; private init; }

    /// <summary>True when <c>p=</c> is empty: the key was revoked (RFC 6376 §3.6.1).</summary>
    public bool Revoked => PublicKey.Length == 0;

    /// <summary>The key's size in bits: the RSA modulus, or 256 for Ed25519. 0 when revoked.</summary>
    public int KeyBits { get; private init; }

    /// <summary>The hash algorithms the record allows (<c>h=</c>, lower case), or null for any.</summary>
    public IReadOnlyList<string>? HashAlgorithms { get; private init; }

    /// <summary>The service types the record applies to (<c>s=</c>, lower case); <c>*</c> means all. Defaults to <c>*</c>.</summary>
    public IReadOnlyList<string> ServiceTypes { get; private init; } = ["*"];

    /// <summary>True when the record has the testing flag <c>t=y</c>.</summary>
    public bool Testing { get; private init; }

    /// <summary>True when the record has <c>t=s</c>: signatures must use an <c>i=</c> domain equal to <c>d=</c>.</summary>
    public bool Strict { get; private init; }

    /// <summary>The human-readable <c>n=</c> note, if any.</summary>
    public string? Notes { get; private init; }

    /// <summary>True when the record's service types include email.</summary>
    public bool AppliesToEmail => ServiceTypes.Contains("*") || ServiceTypes.Contains("email");

    /// <summary>Parses a key record.</summary>
    /// <param name="record">The TXT record text (character-strings already joined).</param>
    /// <param name="key">The parsed record on success.</param>
    /// <param name="error">Why parsing failed.</param>
    /// <param name="detail">A short explanation for diagnostics.</param>
    /// <returns>True on success. Never throws on malformed input.</returns>
    public static bool TryParse(string record, out DkimKeyRecord? key, out DkimKeyError error, out string? detail)
    {
        ArgumentNullException.ThrowIfNull(record);
        key = null;
        detail = null;
        if (record.Length > MaxRecordLength)
        {
            error = DkimKeyError.TooLarge;
            detail = "record longer than " + MaxRecordLength;
            return false;
        }

        if (!TagList.TryParse(record, out List<Tag> tags, out TagListError tle, out detail))
        {
            error = tle == TagListError.DuplicateTag ? DkimKeyError.DuplicateTag : DkimKeyError.MalformedTagList;
            return false;
        }

        string? v = TagList.Get(tags, "v");
        if (v is not null && v != "DKIM1")
        {
            error = DkimKeyError.UnsupportedVersion;
            detail = "v= is not DKIM1";
            return false;
        }

        string kType = (TagList.Get(tags, "k") ?? "rsa").ToLowerInvariant();
        if (kType is not ("rsa" or "ed25519"))
        {
            error = DkimKeyError.UnsupportedKeyType;
            detail = "key type '" + (kType.Length > 32 ? kType[..32] : kType) + "'";
            return false;
        }

        string? p = TagList.Get(tags, "p");
        if (p is null)
        {
            error = DkimKeyError.MissingPublicKey;
            return false;
        }

        byte[] der = [];
        int bits = 0;
        if (p.Any(c => !TagList.IsFws(c)))
        {
            if (!TagList.TryDecodeBase64(p, 4096, out der) || der.Length == 0)
            {
                error = DkimKeyError.InvalidBase64;
                return false;
            }

            if (kType == "ed25519")
            {
                // RFC 8463 §4.2: the raw 32-byte key. Records that wrap it in a SubjectPublicKeyInfo are seen in the wild and accepted.
                if (der.Length == 44 && der.AsSpan(0, 12).SequenceEqual(Ed25519SpkiPrefix))
                {
                    der = der[12..];
                }

                if (der.Length != Ed25519.PublicKeySize)
                {
                    error = DkimKeyError.InvalidPublicKey;
                    detail = "Ed25519 key is not 32 bytes";
                    return false;
                }

                bits = 256;
            }
            else
            {
                if (der.Length > 2400)
                {
                    error = DkimKeyError.KeyTooLarge;
                    return false;
                }

                if (!TryRsaBits(der, out bits))
                {
                    error = DkimKeyError.InvalidPublicKey;
                    detail = "p= is not an RSA public key";
                    return false;
                }

                if (bits > MaxRsaBits)
                {
                    error = DkimKeyError.KeyTooLarge;
                    return false;
                }
            }
        }

        List<string>? hashes = null;
        if (TagList.Get(tags, "h") is { } h)
        {
            hashes = [.. TagList.SplitColon(h).Select(x => x.ToLowerInvariant())];
        }

        List<string> services = ["*"];
        if (TagList.Get(tags, "s") is { } s)
        {
            services = [.. TagList.SplitColon(s).Select(x => x.ToLowerInvariant())];
        }

        List<string> flags = [];
        if (TagList.Get(tags, "t") is { } t)
        {
            flags = [.. TagList.SplitColon(t).Select(x => x.ToLowerInvariant())];
        }

        key = new DkimKeyRecord(kType, der)
        {
            KeyBits = bits,
            HashAlgorithms = hashes,
            ServiceTypes = services,
            Testing = flags.Contains("y"),
            Strict = flags.Contains("s"),
            Notes = TagList.Get(tags, "n"),
        };
        error = DkimKeyError.None;
        return true;
    }

    /// <summary>Imports an RSA public key as SubjectPublicKeyInfo, falling back to a bare PKCS #1 RSAPublicKey.</summary>
    internal static bool TryRsaBits(byte[] der, out int bits)
    {
        using RSA rsa = RSA.Create();
        try
        {
            rsa.ImportSubjectPublicKeyInfo(der, out _);
        }
        catch (CryptographicException)
        {
            try
            {
                rsa.ImportRSAPublicKey(der, out _);
            }
            catch (CryptographicException)
            {
                bits = 0;
                return false;
            }
        }

        bits = rsa.KeySize;
        return true;
    }

    internal RSA CreateRsa()
    {
        RSA rsa = RSA.Create();
        try
        {
            rsa.ImportSubjectPublicKeyInfo(PublicKey, out _);
        }
        catch (CryptographicException)
        {
            rsa.ImportRSAPublicKey(PublicKey, out _);
        }

        return rsa;
    }
}
