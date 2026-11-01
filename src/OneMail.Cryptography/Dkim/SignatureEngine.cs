using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneCrypto.Primitives;
using RecursiveAI.OneMail.Cryptography.Dns;

namespace RecursiveAI.OneMail.Cryptography.Dkim;

/// <summary>Settings for <see cref="DkimVerifier"/> and the ARC verifier.</summary>
public sealed class DkimVerifierOptions
{
    /// <summary>Looks up key records. Required.</summary>
    public ITxtResolver? Resolver { get; init; }

    /// <summary>The clock for <c>x=</c> and <c>t=</c> checks. Default <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>RSA keys smaller than this fail with <see cref="DkimReason.KeyTooWeak"/> (RFC 8301 §3.2 forbids under 1024). Default 1024. Keys under 2048 pass with <see cref="DkimWarnings.WeakKey"/>.</summary>
    public int MinimumRsaBits { get; init; } = 1024;

    /// <summary>
    /// Whether <c>rsa-sha1</c> signatures are checked. Default true: they verify and carry <see cref="DkimWarnings.WeakAlgorithm"/>. RFC 8301 §3.1 says
    /// verifiers must not accept them; set false to follow that and get <see cref="DkimReason.Sha1Refused"/> instead.
    /// </summary>
    public bool AllowRsaSha1 { get; init; } = true;

    /// <summary>How far in the future a <c>t=</c> may be before the signature is flagged <see cref="DkimTimeValidity.NotYetValid"/>. Default 5 minutes.</summary>
    public TimeSpan ClockSkew { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The most signatures examined per message; the rest are reported as <see cref="DkimReason.TooManySignatures"/>. Default 10.</summary>
    public int MaxSignatures { get; init; } = 10;

    /// <summary>The longest <c>h=</c> list accepted. Default 100.</summary>
    public int MaxSignedHeaders { get; init; } = 100;
}

/// <summary>A parsed signature header, ready to verify.</summary>
internal sealed class ParsedSignature
{
    public SigKind Kind;
    public HeaderField Field;
    public List<Tag> Tags = [];
    public DkimAlgorithm Algorithm;
    public DkimCanonicalization HeaderCanon;
    public DkimCanonicalization BodyCanon;
    public string Domain = string.Empty;
    public string Selector = string.Empty;
    public string? Identity;
    public int Instance;
    public List<string> SignedHeaders = [];
    public long? BodyLength;
    public byte[] BodyHash = [];
    public byte[] Signature = [];
    public long? Timestamp;
    public long? Expiry;
    public Tag BTag;
    public string? Cv;
}

internal enum SigKind
{
    Dkim,
    ArcMessageSignature,
    ArcSeal,
}

internal sealed record BodyHashInfo(byte[] Hash, long Total, long Hashed, int CoveredEnd);

/// <summary>Everything one verification pass shares: the message, the resolver, per-pass caches of key lookups and body hashes.</summary>
internal sealed class VerifyContext
{
    private readonly Dictionary<string, TxtLookupResult> _dns = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(DkimCanonicalization, long, bool), BodyHashInfo> _bodies = [];

    public VerifyContext(MessageParts message, DkimVerifierOptions options)
    {
        Message = message;
        Options = options;
        Resolver = options.Resolver ?? throw new ArgumentException("DkimVerifierOptions.Resolver is required.", nameof(options));
        Time = options.TimeProvider ?? TimeProvider.System;
    }

    public MessageParts Message { get; }

    public DkimVerifierOptions Options { get; }

    public ITxtResolver Resolver { get; }

    public TimeProvider Time { get; }

    public async Task<TxtLookupResult> LookupAsync(string name, CancellationToken ct)
    {
        if (!_dns.TryGetValue(name, out TxtLookupResult? r))
        {
            r = await Resolver.LookupTxtAsync(name, ct).ConfigureAwait(false);
            if (r.Status != TxtLookupStatus.TemporaryFailure)
            {
                _dns[name] = r;
            }
        }

        return r;
    }

    public BodyHashInfo BodyHash(DkimCanonicalization canon, long? length, bool sha1)
    {
        var key = (canon, length ?? -1, sha1);
        if (!_bodies.TryGetValue(key, out BodyHashInfo? info))
        {
            using var sink = new BodySink(sha1 ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA256, length ?? -1);
            DkimCanonicalizer.CanonicalizeBody(Message.Bytes.Span[Message.BodyStart..], Message.BodyStart, canon, sink);
            info = new BodyHashInfo(sink.Finish(), sink.Total, sink.Hashed, sink.CoveredSourceEnd);
            _bodies[key] = info;
        }

        return info;
    }
}

/// <summary>The shared parsing and checking steps of DKIM signatures, ARC message signatures and ARC seals (RFC 6376 §3.5, §6; RFC 8617 §4.1).</summary>
internal static class SignatureEngine
{
    public const int MaxSignatureBytes = 4096;
    public const int MaxHashBytes = 64;

    public static bool IsDomainName(string s)
    {
        if (s.Length is 0 or > 253)
        {
            return false;
        }

        int label = 0;
        foreach (char c in s)
        {
            if (c == '.')
            {
                if (label == 0)
                {
                    return false;
                }

                label = 0;
                continue;
            }

            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_') || ++label > 63)
            {
                return false;
            }
        }

        return label > 0;
    }

    private static bool TryNumber(string s, out long value)
    {
        value = 0;
        if (s.Length is 0 or > 18)
        {
            return false;
        }

        foreach (char c in s)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        value = long.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    private static DateTimeOffset FromUnix(long seconds) => DateTimeOffset.FromUnixTimeSeconds(Math.Min(seconds, 253402300799L));

    /// <summary>Parses a signature header into <paramref name="sig"/>; on failure fills <paramref name="res"/> and returns false.</summary>
    public static bool TryParse(MessageParts m, HeaderField h, SigKind kind, DkimVerifierOptions opt, DkimSignatureResult res, out ParsedSignature sig)
    {
        sig = new ParsedSignature { Kind = kind, Field = h };
        res.HeaderName = h.Name;
        res.HeaderRange = new DkimByteRange(h.Start, h.End - h.Start);
        string value = m.ValueText(h);
        if (!TagList.TryParse(value, out List<Tag> tags, out TagListError tle, out string? detail))
        {
            res.Fail(DkimStatus.Neutral, tle == TagListError.DuplicateTag ? DkimReason.DuplicateTag : DkimReason.MalformedSignature, detail);
            return false;
        }

        sig.Tags = tags;
        string[] required = kind switch
        {
            SigKind.Dkim => ["v", "a", "b", "bh", "d", "h", "s"],
            SigKind.ArcMessageSignature => ["i", "a", "b", "bh", "d", "h", "s"],
            _ => ["i", "a", "b", "d", "s", "cv"],
        };
        foreach (string r in required)
        {
            if (!TagList.TryGet(tags, r, out _))
            {
                res.Fail(DkimStatus.Neutral, DkimReason.MissingTag, "missing tag '" + r + "'");
                return false;
            }
        }

        if (kind == SigKind.Dkim && TagList.Get(tags, "v") != "1")
        {
            res.Fail(DkimStatus.Neutral, DkimReason.UnsupportedVersion, "v= is not 1");
            return false;
        }

        if (kind != SigKind.Dkim)
        {
            // RFC 8617 §4.2.1: instances run from 1 to 50.
            if (!TryNumber(TagList.Get(tags, "i")!, out long inst) || inst is < 1 or > 50)
            {
                res.Fail(DkimStatus.Neutral, DkimReason.InvalidTagValue, "ARC instance is not 1-50");
                return false;
            }

            sig.Instance = (int)inst;
            res.Instance = sig.Instance;
        }

        string a = TagList.Get(tags, "a")!.ToLowerInvariant();
        switch (a)
        {
            case "rsa-sha256": sig.Algorithm = DkimAlgorithm.RsaSha256; break;
            case "rsa-sha1": sig.Algorithm = DkimAlgorithm.RsaSha1; break;
            case "ed25519-sha256": sig.Algorithm = DkimAlgorithm.Ed25519Sha256; break;
            default:
                res.Fail(DkimStatus.Neutral, DkimReason.UnsupportedAlgorithm, "algorithm '" + (a.Length > 32 ? a[..32] : a) + "'");
                return false;
        }

        res.Algorithm = sig.Algorithm;
        if (sig.Algorithm == DkimAlgorithm.RsaSha1)
        {
            res.Warnings |= DkimWarnings.WeakAlgorithm;
        }

        sig.HeaderCanon = DkimCanonicalization.Simple;
        sig.BodyCanon = DkimCanonicalization.Simple;
        if (kind == SigKind.ArcSeal)
        {
            // RFC 8617 §4.1.3: only relaxed header canonicalisation, and no body.
            sig.HeaderCanon = DkimCanonicalization.Relaxed;
        }
        else if (TagList.Get(tags, "c") is { } c)
        {
            string[] parts = c.Trim().ToLowerInvariant().Split('/');
            if (parts.Length > 2 || !TryCanon(parts[0], out sig.HeaderCanon) || (parts.Length == 2 && !TryCanon(parts[1], out sig.BodyCanon)))
            {
                res.Fail(DkimStatus.Neutral, DkimReason.UnsupportedCanonicalization, "c= is not simple/relaxed");
                return false;
            }
        }

        res.HeaderCanonicalization = sig.HeaderCanon;
        res.BodyCanonicalization = sig.BodyCanon;

        sig.Domain = TagList.Get(tags, "d")!;
        sig.Selector = TagList.Get(tags, "s")!;
        res.Domain = sig.Domain;
        res.Selector = sig.Selector;
        if (!IsDomainName(sig.Domain) || !IsDomainName(sig.Selector))
        {
            res.Fail(DkimStatus.Neutral, DkimReason.InvalidDomain, "d= or s= is not a plain ASCII domain name");
            return false;
        }

        if (kind == SigKind.Dkim)
        {
            if (TagList.Get(tags, "i") is { } id)
            {
                sig.Identity = id;
                res.Identity = id;
                int at = id.LastIndexOf('@');
                string idDomain = at < 0 ? string.Empty : id[(at + 1)..];
                if (at < 0 || !(idDomain.Equals(sig.Domain, StringComparison.OrdinalIgnoreCase)
                    || idDomain.EndsWith("." + sig.Domain, StringComparison.OrdinalIgnoreCase)))
                {
                    // RFC 6376 §6.1.1: i= must be in the domain of d=.
                    res.Fail(DkimStatus.Neutral, DkimReason.IdentityNotInDomain, "i= is not in the domain of d=");
                    return false;
                }
            }
            else
            {
                sig.Identity = "@" + sig.Domain;
                res.Identity = sig.Identity;
            }
        }

        if (TagList.Get(tags, "h") is { } hv)
        {
            sig.SignedHeaders = TagList.SplitColon(hv);
            if (kind == SigKind.ArcSeal)
            {
                // RFC 8617 §4.1.3: an h= on an ARC-Seal makes the chain fail.
                res.Fail(DkimStatus.Fail, DkimReason.ForbiddenHeader, "h= is not allowed in an ARC-Seal");
                return false;
            }

            if (sig.SignedHeaders.Count > opt.MaxSignedHeaders)
            {
                res.Fail(DkimStatus.Neutral, DkimReason.InvalidTagValue, "h= lists more than " + opt.MaxSignedHeaders + " headers");
                return false;
            }

            res.SignedHeaders = sig.SignedHeaders;
            // RFC 6376 §3.5: h= must include From.
            if (kind == SigKind.Dkim && !sig.SignedHeaders.Any(x => x.Equals("from", StringComparison.OrdinalIgnoreCase)))
            {
                res.Fail(DkimStatus.Neutral, DkimReason.FromNotSigned, "h= does not include From");
                return false;
            }
        }

        if (TagList.Get(tags, "l") is { } l)
        {
            if (!TryNumber(l, out long len))
            {
                res.Fail(DkimStatus.Neutral, DkimReason.InvalidTagValue, "l= is not a number");
                return false;
            }

            sig.BodyLength = len;
            res.BodyLengthTag = len;
            res.Warnings |= DkimWarnings.BodyLengthLimit;
        }

        if (TagList.Get(tags, "t") is { } t)
        {
            if (!TryNumber(t, out long ts))
            {
                res.Fail(DkimStatus.Neutral, DkimReason.InvalidTagValue, "t= is not a number");
                return false;
            }

            sig.Timestamp = ts;
            res.SignedAt = FromUnix(ts);
        }

        if (TagList.Get(tags, "x") is { } x)
        {
            if (!TryNumber(x, out long xs))
            {
                res.Fail(DkimStatus.Neutral, DkimReason.InvalidTagValue, "x= is not a number");
                return false;
            }

            sig.Expiry = xs;
            res.ExpiresAt = FromUnix(xs);
        }
        else
        {
            res.Warnings |= DkimWarnings.NoExpiry;
        }

        if (TagList.Get(tags, "q") is { } q && !TagList.SplitColon(q).Any(x2 => x2.Equals("dns/txt", StringComparison.OrdinalIgnoreCase)))
        {
            res.Fail(DkimStatus.Neutral, DkimReason.UnsupportedQueryMethod, "q= does not offer dns/txt");
            return false;
        }

        TagList.TryGet(tags, "b", out sig.BTag);
        if (!TagList.TryDecodeBase64(sig.BTag.Value, MaxSignatureBytes, out sig.Signature) || sig.Signature.Length == 0)
        {
            res.Fail(DkimStatus.Neutral, DkimReason.InvalidTagValue, "b= is not base64");
            return false;
        }

        string compact = new([.. sig.BTag.Value.Where(ch => !TagList.IsFws(ch))]);
        res.SignatureHint = compact.Length > 8 ? compact[..8] : compact;
        if (kind != SigKind.ArcSeal)
        {
            if (!TagList.TryDecodeBase64(TagList.Get(tags, "bh")!, MaxHashBytes, out sig.BodyHash) || sig.BodyHash.Length == 0)
            {
                res.Fail(DkimStatus.Neutral, DkimReason.InvalidTagValue, "bh= is not base64");
                return false;
            }
        }
        else
        {
            sig.Cv = TagList.Get(tags, "cv")!.ToLowerInvariant();
        }

        return true;
    }

    private static bool TryCanon(string s, out DkimCanonicalization c)
    {
        c = DkimCanonicalization.Simple;
        if (s == "simple")
        {
            return true;
        }

        if (s == "relaxed")
        {
            c = DkimCanonicalization.Relaxed;
            return true;
        }

        return false;
    }

    /// <summary>Checks the clock rules (RFC 6376 §6.1.1): an expired signature fails; a future <c>t=</c> is flagged.</summary>
    public static bool CheckTime(VerifyContext ctx, ParsedSignature sig, DkimSignatureResult res)
    {
        DateTimeOffset now = ctx.Time.GetUtcNow();
        if (sig.Expiry is { } x && FromUnix(x) < now)
        {
            res.TimeValidity = DkimTimeValidity.Expired;
            res.Fail(DkimStatus.Fail, DkimReason.Expired, "signature expired");
            return false;
        }

        if (sig.Timestamp is { } t && FromUnix(t) > now + ctx.Options.ClockSkew)
        {
            res.TimeValidity = DkimTimeValidity.NotYetValid;
            res.Warnings |= DkimWarnings.SignedInFuture;
        }

        return true;
    }

    /// <summary>
    /// Fetches the key records at <c>selector._domainkey.domain</c> (RFC 6376 §3.6.2) and parses them. Several records are all kept: the
    /// signature passes if any matches. Fills <paramref name="res"/> and returns null when no record is usable.
    /// </summary>
    public static async Task<List<DkimKeyRecord>?> GetKeysAsync(VerifyContext ctx, ParsedSignature sig, DkimSignatureResult res, CancellationToken ct)
    {
        TxtLookupResult r = await ctx.LookupAsync(sig.Selector + "._domainkey." + sig.Domain, ct).ConfigureAwait(false);
        switch (r.Status)
        {
            case TxtLookupStatus.TemporaryFailure:
                res.Fail(DkimStatus.TempError, DkimReason.DnsTemporaryFailure, "key lookup failed: " + (r.Detail ?? "temporary DNS failure"));
                return null;
            case TxtLookupStatus.NameNotFound:
            case TxtLookupStatus.NoRecords:
                res.Fail(DkimStatus.PermError, DkimReason.KeyNotFound, "no key for " + sig.Selector + "._domainkey." + sig.Domain);
                return null;
        }

        var keys = new List<DkimKeyRecord>();
        string? firstError = null;
        foreach (string rec in r.Records)
        {
            if (DkimKeyRecord.TryParse(rec, out DkimKeyRecord? k, out DkimKeyError err, out string? d))
            {
                keys.Add(k!);
            }
            else
            {
                firstError ??= err + (d is null ? string.Empty : ": " + d);
            }
        }

        if (keys.Count == 0)
        {
            res.Fail(DkimStatus.PermError, DkimReason.KeyRecordInvalid, "key record unusable (" + firstError + ")");
            return null;
        }

        return keys;
    }

    /// <summary>Checks that a key may be used for this signature. Returns null if so, otherwise a failed copy of the reason.</summary>
    public static (DkimStatus Status, DkimReason Reason, string Detail)? CheckKey(VerifyContext ctx, ParsedSignature sig, DkimKeyRecord key)
    {
        if (key.Revoked)
        {
            return (DkimStatus.Fail, DkimReason.KeyRevoked, "key revoked");
        }

        if (!key.AppliesToEmail)
        {
            return (DkimStatus.PermError, DkimReason.ServiceTypeMismatch, "key record s= excludes email");
        }

        bool edAlg = sig.Algorithm == DkimAlgorithm.Ed25519Sha256;
        if (edAlg != (key.KeyType == "ed25519"))
        {
            return (DkimStatus.PermError, DkimReason.KeyAlgorithmMismatch, "key type " + key.KeyType + " does not fit " + sig.Algorithm);
        }

        string hash = sig.Algorithm == DkimAlgorithm.RsaSha1 ? "sha1" : "sha256";
        if (key.HashAlgorithms is { } hs && !hs.Contains(hash))
        {
            return (DkimStatus.PermError, DkimReason.KeyAlgorithmMismatch, "key record h= does not allow " + hash);
        }

        if (!edAlg && key.KeyBits < ctx.Options.MinimumRsaBits)
        {
            return (DkimStatus.Fail, DkimReason.KeyTooWeak, "RSA key of " + key.KeyBits + " bits is below the minimum of " + ctx.Options.MinimumRsaBits);
        }

        if (key.Strict && sig.Kind == SigKind.Dkim)
        {
            string id = sig.Identity ?? string.Empty;
            string idDomain = id[(id.LastIndexOf('@') + 1)..];
            if (!idDomain.Equals(sig.Domain, StringComparison.OrdinalIgnoreCase))
            {
                return (DkimStatus.Fail, DkimReason.StrictIdentityMismatch, "key record t=s requires i= to be d= itself");
            }
        }

        return null;
    }

    /// <summary>Verifies <paramref name="signature"/> over <paramref name="data"/> with the key. Never throws on data.</summary>
    public static bool CheckSignature(DkimAlgorithm alg, DkimKeyRecord key, byte[] data, byte[] signature)
    {
        try
        {
            if (alg == DkimAlgorithm.Ed25519Sha256)
            {
                // RFC 8463 §3: PureEdDSA Ed25519 over the SHA-256 digest of the canonicalised data.
                return Ed25519.Verify(key.PublicKey, SHA256.HashData(data), signature);
            }

            using RSA rsa = key.CreateRsa();
            return rsa.VerifyData(data, signature, alg == DkimAlgorithm.RsaSha1 ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Applies the key-strength warnings once the key is accepted.</summary>
    public static void Annotate(DkimSignatureResult res, DkimKeyRecord key)
    {
        res.KeyBits = key.KeyBits;
        if (key.KeyType == "rsa" && key.KeyBits < 2048)
        {
            res.Warnings |= DkimWarnings.WeakKey;
        }

        if (key.Testing)
        {
            res.Warnings |= DkimWarnings.TestingKey;
        }
    }

    /// <summary>
    /// Appends the headers named by <paramref name="names"/> in canonical form (RFC 6376 §5.4.2): for each name, the nearest not-yet-used instance
    /// counting from the bottom of the header block; a name with no instance left contributes nothing. <paramref name="exclude"/> is the header
    /// being verified, which is never in its own scope.
    /// </summary>
    public static void AppendSelectedHeaders(MessageParts m, IReadOnlyList<string> names, DkimCanonicalization canon, int exclude, List<byte> output, List<DkimByteRange> covered, List<string> oversigned)
    {
        var used = new bool[m.Headers.Count];
        foreach (string name in names)
        {
            bool found = false;
            for (int i = m.Headers.Count - 1; i >= 0; i--)
            {
                if (used[i] || i == exclude || !m.Headers[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                used[i] = true;
                found = true;
                HeaderField f = m.Headers[i];
                DkimCanonicalizer.AppendHeader(output, m.Raw(f), canon, trailingCrlf: true);
                covered.Add(new DkimByteRange(f.Start, f.End - f.Start));
                break;
            }

            if (!found && !oversigned.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                oversigned.Add(name);
            }
        }
    }

    /// <summary>Appends the signature header itself with its <c>b=</c> value emptied and no final line end (RFC 6376 §3.7).</summary>
    public static void AppendSignatureHeader(MessageParts m, ParsedSignature sig, DkimCanonicalization canon, List<byte> output)
    {
        ReadOnlySpan<byte> raw = m.Raw(sig.Field);
        int valueBase = sig.Field.Colon + 1 - sig.Field.Start;
        int cutStart = valueBase + sig.BTag.ValueStart;
        int cutEnd = valueBase + sig.BTag.ValueEnd;
        byte[] spliced = [.. raw[..cutStart], .. raw[cutEnd..]];
        DkimCanonicalizer.AppendHeader(output, spliced, canon, trailingCrlf: false);
    }

    public static string Latin1(ReadOnlySpan<byte> b) => Encoding.Latin1.GetString(b);
}
