using System.Security.Cryptography;
using RecursiveAI.OneMail.Cryptography.Dkim;
using RecursiveAI.OneMail.Mime;

namespace RecursiveAI.OneMail.Cryptography.Arc;

/// <summary>The Chain Validation Status of an Authenticated Received Chain (RFC 8617 §4.4).</summary>
public enum ArcChainStatus
{
    /// <summary>The message carries no ARC sets.</summary>
    None = 0,

    /// <summary>The chain validated.</summary>
    Pass = 1,

    /// <summary>The chain is invalid. Per RFC 8617 §5.2.1 every failure, including DNS trouble, is final.</summary>
    Fail = 2,
}

/// <summary>Why a chain failed. New members are only ever added at the end.</summary>
public enum ArcFailure
{
    /// <summary>The chain did not fail.</summary>
    None = 0,

    /// <summary>More than 50 sets (RFC 8617 §5.2 step 1).</summary>
    TooManySets = 1,

    /// <summary>The newest seal says <c>cv=fail</c> (step 2).</summary>
    LatestSealSaysFail = 2,

    /// <summary>A set is missing one of its three headers, or has a duplicate (step 3A).</summary>
    IncompleteSet = 3,

    /// <summary>The instances do not run 1..N without gaps or repeats (step 3B).</summary>
    InstanceSequence = 4,

    /// <summary>A seal's <c>cv=</c> is not what its position requires: <c>none</c> for instance 1, <c>pass</c> above it, never <c>fail</c> (step 3C).</summary>
    InvalidChainValidation = 5,

    /// <summary>An ARC header could not be parsed.</summary>
    InvalidHeader = 6,

    /// <summary>The newest ARC-Message-Signature does not verify (step 4).</summary>
    MessageSignatureFailed = 7,

    /// <summary>An ARC-Seal does not verify (step 6).</summary>
    SealFailed = 8,
}

/// <summary>One ARC set (RFC 8617 §4.2) as the validator saw it.</summary>
public sealed class ArcSetResult
{
    /// <summary>The instance number.</summary>
    public int Instance { get; internal set; }

    /// <summary>The ARC-Authentication-Results payload: what that hop recorded, without the <c>i=</c> prefix.</summary>
    public string? AuthenticationResults { get; internal set; }

    /// <summary>The check of the set's ARC-Message-Signature; null if it was not checked (an earlier failure ended validation, or the oldest-pass scan stopped before it).</summary>
    public DkimSignatureResult? MessageSignature { get; internal set; }

    /// <summary>The check of the set's ARC-Seal; null if not checked.</summary>
    public DkimSignatureResult? Seal { get; internal set; }

    /// <summary>The seal's <c>cv=</c> value, lower case.</summary>
    public string? ChainValidation { get; internal set; }

    /// <summary>The sealing domain (<c>d=</c> of the seal).</summary>
    public string? SealDomain { get; internal set; }
}

/// <summary>The outcome of validating a message's ARC chain: the aggregate status and every set.</summary>
public sealed class ArcVerificationResult
{
    /// <summary>The Chain Validation Status.</summary>
    public ArcChainStatus Status { get; internal set; }

    /// <summary>Why the chain failed; <see cref="ArcFailure.None"/> otherwise.</summary>
    public ArcFailure Failure { get; internal set; }

    /// <summary>A short explanation of a failure.</summary>
    public string? Detail { get; internal set; }

    /// <summary>The instance a failure was found at, or 0.</summary>
    public int FailedInstance { get; internal set; }

    /// <summary>The number of sets (N, the highest instance).</summary>
    public int InstanceCount { get; internal set; }

    /// <summary>
    /// The oldest-pass value (RFC 8617 §5.2 step 5): 0 when every ARC-Message-Signature still verifies, otherwise the lowest instance whose
    /// message signature (and all newer ones) verify, which is the first hop after the last one that changed the message. Null when
    /// not computed (the chain failed first, or there is one set).
    /// </summary>
    public int? OldestPass { get; internal set; }

    /// <summary>The sets in instance order, from 1.</summary>
    public IReadOnlyList<ArcSetResult> Sets { get; internal set; } = [];

    /// <summary>Renders the status as an <c>Authentication-Results</c> method result: <c>arc=pass</c>, plus the newest sealer and the oldest-pass when known (RFC 8617 §6).</summary>
    /// <returns>For example <c>arc=pass header.s=sel header.d=example.org</c>.</returns>
    public override string ToString()
    {
        string s = "arc=" + Status.ToString().ToLowerInvariant();
        if (Status == ArcChainStatus.Fail && Detail is not null)
        {
            s += " (" + Detail.Replace('(', '[').Replace(')', ']') + ")";
        }

        if (Sets.Count > 0 && Sets[^1].Seal is { Domain: { } d, Selector: { } sel })
        {
            s += " header.s=" + sel + " header.d=" + d;
        }

        if (OldestPass is { } op)
        {
            s += " header.oldest-pass=" + op.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return s;
    }
}

/// <summary>The three ARC headers of every set, found by name and instance.</summary>
internal sealed class ArcChain
{
    public const string SealName = "ARC-Seal";
    public const string SignatureName = "ARC-Message-Signature";
    public const string ResultsName = "ARC-Authentication-Results";
    public const int MaxSets = 50;

    public SortedDictionary<int, List<int>> Seals { get; } = [];

    public SortedDictionary<int, List<int>> Signatures { get; } = [];

    public SortedDictionary<int, List<int>> Results { get; } = [];

    public int Highest { get; private set; }

    public int HeaderCount { get; private set; }

    public bool Unparsable { get; private set; }

    public string? UnparsableDetail { get; private set; }

    public static ArcChain Collect(MessageParts m)
    {
        var c = new ArcChain();
        for (int i = 0; i < m.Headers.Count; i++)
        {
            HeaderField h = m.Headers[i];
            SortedDictionary<int, List<int>>? bucket =
                h.Name.Equals(SealName, StringComparison.OrdinalIgnoreCase) ? c.Seals :
                h.Name.Equals(SignatureName, StringComparison.OrdinalIgnoreCase) ? c.Signatures :
                h.Name.Equals(ResultsName, StringComparison.OrdinalIgnoreCase) ? c.Results : null;
            if (bucket is null)
            {
                continue;
            }

            c.HeaderCount++;
            // The ABNF puts the instance first (RFC 8617 §4.1), but the seal and message signature are tag lists whose order
            // implementations do not keep (the ARC test suite sorts tags alphabetically), so the tag is found wherever it is.
            // The results header is not a tag list, so there it must come first.
            string value = m.ValueText(h);
            int inst = 0;
            bool found = bucket == c.Results
                ? TryInstance(value, out inst, out _)
                : TagList.TryParse(value, out List<Tag> tl, out _, out _) && TagList.Get(tl, "i") is { } iv && int.TryParse(iv, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out inst) && inst is >= 1 and <= 1000;
            if (!found)
            {
                c.Unparsable = true;
                c.UnparsableDetail ??= h.Name + " without a valid i= instance";
                continue;
            }

            if (!bucket.TryGetValue(inst, out List<int>? list))
            {
                bucket[inst] = list = [];
            }

            list.Add(i);
            c.Highest = Math.Max(c.Highest, inst);
        }

        return c;
    }

    /// <summary>Reads the leading <c>i=N</c> of an ARC header value; <paramref name="rest"/> is the index just after the following semicolon (or the end).</summary>
    public static bool TryInstance(string value, out int instance, out int rest)
    {
        instance = 0;
        rest = 0;
        int p = 0;
        while (p < value.Length && TagList.IsFws(value[p]))
        {
            p++;
        }

        if (p >= value.Length || value[p] != 'i')
        {
            return false;
        }

        p++;
        while (p < value.Length && TagList.IsFws(value[p]))
        {
            p++;
        }

        if (p >= value.Length || value[p] != '=')
        {
            return false;
        }

        p++;
        while (p < value.Length && TagList.IsFws(value[p]))
        {
            p++;
        }

        int start = p;
        long n = 0;
        while (p < value.Length && char.IsAsciiDigit(value[p]) && p - start < 6)
        {
            n = n * 10 + (value[p] - '0');
            p++;
        }

        if (p == start || n is < 1 or > 1000)
        {
            return false;
        }

        while (p < value.Length && TagList.IsFws(value[p]))
        {
            p++;
        }

        if (p < value.Length && value[p] != ';')
        {
            return false;
        }

        instance = (int)n;
        rest = Math.Min(p + 1, value.Length);
        return true;
    }
}

/// <summary>
/// Validates an Authenticated Received Chain (RFC 8617 §5.2): the structure, the newest message signature, optionally the oldest-pass scan,
/// and every seal from newest to oldest. All failures are final for the chain (§5.2.1), but each signature keeps its own detailed result.
/// </summary>
public sealed class ArcVerifier
{
    private readonly DkimVerifierOptions _options;

    /// <summary>Creates a verifier.</summary>
    /// <param name="options">The same settings as <see cref="DkimVerifier"/>; a resolver is required.</param>
    /// <exception cref="ArgumentException">No resolver was given.</exception>
    public ArcVerifier(DkimVerifierOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Resolver is null)
        {
            throw new ArgumentException("DkimVerifierOptions.Resolver is required.", nameof(options));
        }

        // ARC is newer than RFC 8301 and has no legacy of rsa-sha1 signatures to keep working: the ARC test suite expects a sha1 seal or
        // message signature to fail the chain, so it does here whatever AllowRsaSha1 says for DKIM.
        _options = new DkimVerifierOptions
        {
            Resolver = options.Resolver,
            TimeProvider = options.TimeProvider,
            MinimumRsaBits = options.MinimumRsaBits,
            AllowRsaSha1 = false,
            ClockSkew = options.ClockSkew,
            MaxSignatures = options.MaxSignatures,
            MaxSignedHeaders = options.MaxSignedHeaders,
        };
    }

    /// <summary>Whether to verify the older ARC-Message-Signatures to find the oldest-pass value. Default true.</summary>
    public bool ComputeOldestPass { get; init; } = true;

    /// <summary>Validates the chain on a parsed message.</summary>
    /// <param name="entity">The message entity.</param>
    /// <param name="cancellationToken">Cancels pending DNS lookups.</param>
    /// <returns>See <see cref="VerifyAsync(ReadOnlyMemory{byte}, CancellationToken)"/>.</returns>
    public Task<ArcVerificationResult> VerifyAsync(MimeEntity entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return VerifyAsync(entity.ToArray(), cancellationToken);
    }

    /// <summary>Validates the chain on a message.</summary>
    /// <param name="message">The complete message as bytes.</param>
    /// <param name="cancellationToken">Cancels pending DNS lookups.</param>
    /// <returns>The chain status with every set. A message with no ARC headers has status <see cref="ArcChainStatus.None"/>. Never throws on message content.</returns>
    public async Task<ArcVerificationResult> VerifyAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        var m = new MessageParts(message);
        ArcChain chain = ArcChain.Collect(m);
        var result = new ArcVerificationResult { Status = ArcChainStatus.None, InstanceCount = chain.Highest };
        if (chain.HeaderCount == 0)
        {
            return result;
        }

        ArcVerificationResult Fail(ArcFailure why, string detail, int instance = 0)
        {
            result.Status = ArcChainStatus.Fail;
            result.Failure = why;
            result.Detail = detail;
            result.FailedInstance = instance;
            return result;
        }

        // Step 1: more than 50 sets fail. A hostile message with thousands of ARC headers is cut off before any parsing.
        if (chain.Highest > ArcChain.MaxSets || chain.HeaderCount > ArcChain.MaxSets * 3 + 6)
        {
            return Fail(ArcFailure.TooManySets, "more than " + ArcChain.MaxSets + " ARC sets");
        }

        if (chain.Unparsable)
        {
            return Fail(ArcFailure.InvalidHeader, chain.UnparsableDetail!);
        }

        int n = chain.Highest;
        var sets = new List<ArcSetResult>();
        for (int i = 1; i <= n; i++)
        {
            sets.Add(new ArcSetResult { Instance = i });
        }

        result.Sets = sets;

        // Step 3 (checked before step 2's cv test so a malformed chain reports its structure): one of each header per instance, 1..N without gaps.
        for (int i = 1; i <= n; i++)
        {
            int s = Count(chain.Seals, i), g = Count(chain.Signatures, i), r = Count(chain.Results, i);
            if (s == 0 && g == 0 && r == 0)
            {
                return Fail(ArcFailure.InstanceSequence, "no ARC set with instance " + i, i);
            }

            if (s != 1 || g != 1 || r != 1)
            {
                return Fail(ArcFailure.IncompleteSet, "instance " + i + " has " + s + " seal(s), " + g + " message signature(s), " + r + " results header(s)", i);
            }
        }

        var parsedSeals = new ParsedSignature[n + 1];
        for (int i = 1; i <= n; i++)
        {
            HeaderField rh = m.Headers[chain.Results[i][0]];
            ArcChain.TryInstance(m.ValueText(rh), out _, out int rest);
            sets[i - 1].AuthenticationResults = m.ValueText(rh)[rest..].Trim(' ', '\t', '\r', '\n');
            var probe = new DkimSignatureResult();
            if (!SignatureEngine.TryParse(m, m.Headers[chain.Seals[i][0]], SigKind.ArcSeal, _options, probe, out ParsedSignature seal))
            {
                sets[i - 1].Seal = probe;
                return Fail(probe.Reason == DkimReason.ForbiddenHeader ? ArcFailure.InvalidChainValidation : ArcFailure.InvalidHeader, "ARC-Seal " + i + ": " + probe.Detail, i);
            }

            parsedSeals[i] = seal;
            sets[i - 1].ChainValidation = seal.Cv;
            sets[i - 1].SealDomain = seal.Domain;
        }

        // Step 2: a newest seal that says fail ends it.
        if (parsedSeals[n].Cv == "fail")
        {
            return Fail(ArcFailure.LatestSealSaysFail, "the newest ARC-Seal has cv=fail", n);
        }

        for (int i = 1; i <= n; i++)
        {
            string want = i == 1 ? "none" : "pass";
            if (parsedSeals[i].Cv != want)
            {
                return Fail(ArcFailure.InvalidChainValidation, "ARC-Seal " + i + " has cv=" + parsedSeals[i].Cv + ", expected " + want, i);
            }
        }

        var ctx = new VerifyContext(m, _options);
        // Step 4: the newest message signature.
        DkimSignatureResult ams = await VerifyMessageSignature(ctx, chain, n, cancellationToken).ConfigureAwait(false);
        sets[n - 1].MessageSignature = ams;
        if (ams.Status != DkimStatus.Pass)
        {
            return Fail(ArcFailure.MessageSignatureFailed, "ARC-Message-Signature " + n + ": " + (ams.Detail ?? ams.Status.ToString()), n);
        }

        // Step 5 (optional): how far back do the message signatures still hold?
        if (ComputeOldestPass && n > 1)
        {
            int oldest = 0;
            for (int i = n - 1; i >= 1; i--)
            {
                DkimSignatureResult r = await VerifyMessageSignature(ctx, chain, i, cancellationToken).ConfigureAwait(false);
                sets[i - 1].MessageSignature = r;
                if (r.Status != DkimStatus.Pass)
                {
                    oldest = i + 1;
                    break;
                }
            }

            result.OldestPass = oldest;
        }

        // Step 6: the seals, newest first.
        for (int i = n; i >= 1; i--)
        {
            DkimSignatureResult r = await VerifySeal(ctx, chain, parsedSeals[i], i, cancellationToken).ConfigureAwait(false);
            sets[i - 1].Seal = r;
            if (r.Status != DkimStatus.Pass)
            {
                return Fail(ArcFailure.SealFailed, "ARC-Seal " + i + ": " + (r.Detail ?? r.Status.ToString()), i);
            }
        }

        result.Status = ArcChainStatus.Pass;
        return result;
    }

    private static int Count(SortedDictionary<int, List<int>> d, int i) => d.TryGetValue(i, out List<int>? l) ? l.Count : 0;

    private async Task<DkimSignatureResult> VerifyMessageSignature(VerifyContext ctx, ArcChain chain, int instance, CancellationToken ct)
    {
        int idx = chain.Signatures[instance][0];
        var res = new DkimSignatureResult { Index = instance - 1 };
        if (SignatureEngine.TryParse(ctx.Message, ctx.Message.Headers[idx], SigKind.ArcMessageSignature, _options, res, out ParsedSignature sig))
        {
            await DkimVerifier.VerifyParsedAsync(ctx, sig, idx, res, ct).ConfigureAwait(false);
        }

        return res;
    }

    private async Task<DkimSignatureResult> VerifySeal(VerifyContext ctx, ArcChain chain, ParsedSignature sig, int instance, CancellationToken ct)
    {
        MessageParts m = ctx.Message;
        var res = new DkimSignatureResult { Index = instance - 1 };
        SignatureEngine.TryParse(m, sig.Field, SigKind.ArcSeal, _options, res, out _);
        if (sig.Algorithm == DkimAlgorithm.RsaSha1 && !_options.AllowRsaSha1)
        {
            res.Fail(DkimStatus.Fail, DkimReason.Sha1Refused, "rsa-sha1 is refused by this verifier's policy (RFC 8301)");
            return res;
        }

        if (!SignatureEngine.CheckTime(ctx, sig, res))
        {
            return res;
        }

        List<DkimKeyRecord>? keys = await SignatureEngine.GetKeysAsync(ctx, sig, res, ct).ConfigureAwait(false);
        if (keys is null)
        {
            return res;
        }

        // RFC 8617 §5.1.1: the seal signs sets 1..i in order, each as AAR, AMS, AS, relaxed; the last header (this seal) has its b= emptied and no line end.
        var data = new List<byte>(4096);
        for (int j = 1; j <= instance; j++)
        {
            DkimCanonicalizer.AppendHeader(data, m.Raw(m.Headers[chain.Results[j][0]]), DkimCanonicalization.Relaxed, trailingCrlf: true);
            DkimCanonicalizer.AppendHeader(data, m.Raw(m.Headers[chain.Signatures[j][0]]), DkimCanonicalization.Relaxed, trailingCrlf: true);
            if (j < instance)
            {
                DkimCanonicalizer.AppendHeader(data, m.Raw(m.Headers[chain.Seals[j][0]]), DkimCanonicalization.Relaxed, trailingCrlf: true);
            }
        }

        SignatureEngine.AppendSignatureHeader(m, sig, DkimCanonicalization.Relaxed, data);
        byte[] bytes = [.. data];
        (DkimStatus Status, DkimReason Reason, string Detail)? first = null;
        foreach (DkimKeyRecord key in keys)
        {
            if (SignatureEngine.CheckKey(ctx, sig, key) is { } bad)
            {
                first ??= bad;
                continue;
            }

            SignatureEngine.Annotate(res, key);
            if (SignatureEngine.CheckSignature(sig.Algorithm, key, bytes, sig.Signature))
            {
                res.Status = DkimStatus.Pass;
                res.Reason = DkimReason.None;
                res.Detail = null;
                return res;
            }

            first ??= (DkimStatus.Fail, DkimReason.SignatureMismatch, "seal signature did not verify");
        }

        res.Fail(first!.Value.Status, first.Value.Reason, first.Value.Detail);
        return res;
    }
}
