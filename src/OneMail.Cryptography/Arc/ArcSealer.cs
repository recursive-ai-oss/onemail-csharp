using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneMail.Cryptography.Dkim;
using RecursiveAI.OneMail.Mime;

namespace RecursiveAI.OneMail.Cryptography.Arc;

/// <summary>Settings for <see cref="ArcSealer"/>.</summary>
public sealed class ArcSealerOptions
{
    /// <summary>The sealing domain (<c>d=</c> of both the message signature and the seal). Required.</summary>
    public string Domain { get; init; } = string.Empty;

    /// <summary>The selector (<c>s=</c>). Required.</summary>
    public string Selector { get; init; } = string.Empty;

    /// <summary>Header canonicalisation of the ARC-Message-Signature. Default relaxed. (The seal is always relaxed, RFC 8617 §4.1.3.)</summary>
    public DkimCanonicalization HeaderCanonicalization { get; init; } = DkimCanonicalization.Relaxed;

    /// <summary>Body canonicalisation of the ARC-Message-Signature. Default relaxed.</summary>
    public DkimCanonicalization BodyCanonicalization { get; init; } = DkimCanonicalization.Relaxed;

    /// <summary>
    /// Headers the ARC-Message-Signature covers. Null uses <see cref="DkimSignerOptions.DefaultHeaders"/> plus <c>DKIM-Signature</c>, which
    /// RFC 8617 §4.1.2 asks for. ARC headers and Authentication-Results are never signed (§4.1.2) even if listed.
    /// </summary>
    public IReadOnlyList<string>? HeadersToSign { get; init; }

    /// <summary>Over-sign the listed headers, as <see cref="DkimSignerOptions.OverSign"/>. Default false.</summary>
    public bool OverSign { get; init; }

    /// <summary>The clock for <c>t=</c>. Default <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>
    /// How to validate the chain already on the message when the caller does not pass a status. Its resolver is used for the key lookups.
    /// Null means a message with ARC headers needs the status passed in.
    /// </summary>
    public DkimVerifierOptions? Verification { get; init; }
}

/// <summary>What <see cref="ArcSealer"/> produced.</summary>
public sealed class ArcSealResult
{
    /// <summary>True when a set was added. False when the newest existing seal already says <c>cv=fail</c>, in which case RFC 8617 §5.1 says to stop and the message is returned unchanged.</summary>
    public bool Sealed { get; internal set; }

    /// <summary>The instance of the new set (1 if the message had no chain); 0 when nothing was sealed.</summary>
    public int Instance { get; internal set; }

    /// <summary>The chain validation status written into the new seal.</summary>
    public ArcChainStatus ChainStatus { get; internal set; }

    /// <summary>The complete <c>ARC-Seal</c> header field, with its line end.</summary>
    public string Seal { get; internal set; } = string.Empty;

    /// <summary>The complete <c>ARC-Message-Signature</c> header field.</summary>
    public string MessageSignature { get; internal set; } = string.Empty;

    /// <summary>The complete <c>ARC-Authentication-Results</c> header field.</summary>
    public string AuthenticationResults { get; internal set; } = string.Empty;

    /// <summary>The message with the three headers added at the top, in the order seal, message signature, results; all original bytes kept.</summary>
    public byte[] Message { get; internal set; } = [];
}

/// <summary>
/// Adds an ARC set to a message (RFC 8617 §5.1): an ARC-Authentication-Results recording what this hop concluded, an ARC-Message-Signature over the
/// message as it leaves, and an ARC-Seal binding the whole chain. Weak keys are refused (see <see cref="DkimSigningKey"/>).
/// </summary>
public sealed class ArcSealer
{
    private readonly DkimSigningKey _key;
    private readonly ArcSealerOptions _options;

    /// <summary>Creates a sealer.</summary>
    /// <param name="key">The private key.</param>
    /// <param name="options">The sealing settings.</param>
    /// <exception cref="ArgumentException">The domain or selector is not an ASCII DNS name.</exception>
    public ArcSealer(DkimSigningKey key, ArcSealerOptions options)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(options);
        _key = key;
        _options = options;
        if (!SignatureEngine.IsDomainName(options.Domain) || !SignatureEngine.IsDomainName(options.Selector))
        {
            throw new ArgumentException("Domain and Selector must be ASCII DNS names.", nameof(options));
        }
    }

    /// <summary>Seals a message.</summary>
    /// <param name="message">The complete message as bytes, after all modifications and after any DKIM signing (RFC 8617 §5.1 step 1).</param>
    /// <param name="authenticationResults">
    /// What this hop concluded, as the value of an <c>Authentication-Results</c> header (RFC 8601): <c>authserv-id; method=result ...</c>. It becomes
    /// the payload of the ARC-Authentication-Results header after <c>i=N;</c>. <see cref="AuthenticationResultsFormatter"/> can build it from verification results.
    /// </param>
    /// <param name="chainStatus">The validation status of the incoming chain, if the caller already has it. Null validates the chain with <see cref="ArcSealerOptions.Verification"/>.</param>
    /// <param name="cancellationToken">Cancels pending DNS lookups.</param>
    /// <returns>The new headers and the sealed message.</returns>
    /// <exception cref="ArgumentException">The message has ARC headers and neither <paramref name="chainStatus"/> nor <see cref="ArcSealerOptions.Verification"/> was given, the results text has a line break, or the message has no From.</exception>
    public async Task<ArcSealResult> SealAsync(ReadOnlyMemory<byte> message, string authenticationResults, ArcChainStatus? chainStatus = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authenticationResults);
        if (authenticationResults.Any(c => c is '\r' or '\n' || c > 126))
        {
            throw new ArgumentException("The results text must be one line of ASCII.", nameof(authenticationResults));
        }

        var m = new MessageParts(message);
        ArcChain chain = ArcChain.Collect(m);
        string eol = message.Span.IndexOf("\r\n"u8) < 0 && message.Span.IndexOf((byte)'\n') >= 0 ? "\n" : "\r\n";
        ArcChainStatus cv;
        if (chainStatus is { } given)
        {
            cv = given;
        }
        else if (chain.HeaderCount == 0)
        {
            cv = ArcChainStatus.None;
        }
        else
        {
            if (_options.Verification is null)
            {
                throw new ArgumentException("The message has ARC headers: pass the chain status or set ArcSealerOptions.Verification.", nameof(chainStatus));
            }

            ArcVerificationResult v = await new ArcVerifier(_options.Verification).VerifyAsync(message, cancellationToken).ConfigureAwait(false);
            cv = v.Status;
        }

        // Step 2: a chain whose newest seal already says fail is not sealed again.
        if (chain.Highest > 0 && chain.Seals.TryGetValue(chain.Highest, out List<int>? top) && top.Count == 1 &&
            TagList.TryParse(m.ValueText(m.Headers[top[0]]), out List<Tag> topTags, out _, out _) && TagList.Get(topTags, "cv")?.ToLowerInvariant() == "fail")
        {
            return new ArcSealResult { Sealed = false, ChainStatus = ArcChainStatus.Fail, Message = message.ToArray() };
        }

        // Step 3: the instance is one more than the highest found.
        int instance = chain.Highest + 1;
        if (instance > ArcChain.MaxSets)
        {
            return new ArcSealResult { Sealed = false, ChainStatus = ArcChainStatus.Fail, Message = message.ToArray() };
        }

        string inst = instance.ToString(System.Globalization.CultureInfo.InvariantCulture);
        TimeProvider time = _options.TimeProvider ?? TimeProvider.System;
        string now = time.GetUtcNow().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        string algName = _key.Algorithm == DkimAlgorithm.Ed25519Sha256 ? "ed25519-sha256" : "rsa-sha256";

        // 4A: ARC-Authentication-Results.
        string aar = FoldResults("ARC-Authentication-Results:", "i=" + inst + "; " + authenticationResults, eol);

        // 4B: ARC-Message-Signature, signed like a DKIM signature but with i= as the instance (RFC 8617 §4.1.2).
        IEnumerable<string> wanted = (_options.HeadersToSign ?? [.. DkimSignerOptions.DefaultHeaders, "DKIM-Signature"])
            .Where(h => !h.StartsWith("ARC-", StringComparison.OrdinalIgnoreCase) && !h.Equals("Authentication-Results", StringComparison.OrdinalIgnoreCase));
        var names = new List<string>();
        bool hasFrom = false;
        foreach (string w in wanted.Append("From").Distinct(StringComparer.OrdinalIgnoreCase))
        {
            int present = m.Headers.Count(h => h.Name.Equals(w, StringComparison.OrdinalIgnoreCase));
            hasFrom |= present > 0 && w.Equals("From", StringComparison.OrdinalIgnoreCase);
            for (int i = 0; i < present + (_options.OverSign ? 1 : 0); i++)
            {
                names.Add(w);
            }
        }

        if (!hasFrom)
        {
            throw new ArgumentException("A message without a From header cannot be sealed.", nameof(message));
        }

        byte[] bodyHash;
        using (var sink = new BodySink(HashAlgorithmName.SHA256, -1))
        {
            DkimCanonicalizer.CanonicalizeBody(m.Bytes.Span[m.BodyStart..], m.BodyStart, _options.BodyCanonicalization, sink);
            bodyHash = sink.Finish();
        }

        var amsTags = new List<string>
        {
            "i=" + inst, "a=" + algName,
            "c=" + (_options.HeaderCanonicalization == DkimCanonicalization.Simple ? "simple" : "relaxed") + "/" + (_options.BodyCanonicalization == DkimCanonicalization.Simple ? "simple" : "relaxed"),
            "d=" + _options.Domain, "s=" + _options.Selector, "t=" + now, "h=" + string.Join(':', names), "bh=" + Convert.ToBase64String(bodyHash),
        };
        string amsPrefix = DkimSigner.Fold("ARC-Message-Signature:", amsTags, eol, out int amsCol);
        var amsData = new List<byte>(1024);
        SignatureEngine.AppendSelectedHeaders(m, names, _options.HeaderCanonicalization, -1, amsData, [], []);
        DkimCanonicalizer.AppendHeader(amsData, Encoding.Latin1.GetBytes(amsPrefix), _options.HeaderCanonicalization, trailingCrlf: false);
        string ams = amsPrefix + FoldSignature(_key.Sign([.. amsData]), amsCol, eol) + eol;

        // 4C: ARC-Seal. A failed chain is sealed as if the new set were the only one (§5.1.2).
        var sealTags = new List<string> { "i=" + inst, "a=" + algName, "t=" + now, "cv=" + cv.ToString().ToLowerInvariant(), "d=" + _options.Domain, "s=" + _options.Selector };
        string sealPrefix = DkimSigner.Fold("ARC-Seal:", sealTags, eol, out int sealCol);
        var sealData = new List<byte>(4096);
        if (cv != ArcChainStatus.Fail)
        {
            for (int j = 1; j < instance; j++)
            {
                foreach (SortedDictionary<int, List<int>> d in new[] { chain.Results, chain.Signatures, chain.Seals })
                {
                    if (!d.TryGetValue(j, out List<int>? l) || l.Count != 1)
                    {
                        throw new ArgumentException("The existing ARC chain is incomplete (set " + j + "); seal it as cv=fail.", nameof(chainStatus));
                    }

                    DkimCanonicalizer.AppendHeader(sealData, m.Raw(m.Headers[l[0]]), DkimCanonicalization.Relaxed, trailingCrlf: true);
                }
            }
        }

        DkimCanonicalizer.AppendHeader(sealData, Encoding.Latin1.GetBytes(aar), DkimCanonicalization.Relaxed, trailingCrlf: true);
        DkimCanonicalizer.AppendHeader(sealData, Encoding.Latin1.GetBytes(ams), DkimCanonicalization.Relaxed, trailingCrlf: true);
        DkimCanonicalizer.AppendHeader(sealData, Encoding.Latin1.GetBytes(sealPrefix), DkimCanonicalization.Relaxed, trailingCrlf: false);
        string seal = sealPrefix + FoldSignature(_key.Sign([.. sealData]), sealCol, eol) + eol;

        byte[] head = Encoding.Latin1.GetBytes(seal + ams + aar);
        byte[] all = new byte[head.Length + message.Length];
        head.CopyTo(all, 0);
        message.Span.CopyTo(all.AsSpan(head.Length));
        return new ArcSealResult { Sealed = true, Instance = instance, ChainStatus = cv, Seal = seal, MessageSignature = ams, AuthenticationResults = aar, Message = all };
    }

    private static string FoldSignature(byte[] signature, int col, string eol)
    {
        string b64 = Convert.ToBase64String(signature);
        var sb = new StringBuilder();
        int i = 0;
        while (i < b64.Length)
        {
            int room = 78 - col;
            if (room < 4)
            {
                sb.Append(eol).Append(' ');
                col = 1;
                room = 77;
            }

            int take = Math.Min(room, b64.Length - i);
            sb.Append(b64, i, take);
            i += take;
            col += take;
        }

        return sb.ToString();
    }

    /// <summary>Folds an Authentication-Results style value after semicolons so lines stay near 78 characters.</summary>
    private static string FoldResults(string name, string text, string eol)
    {
        var sb = new StringBuilder(name);
        int col = name.Length;
        foreach (string piece in text.Split(' '))
        {
            if (piece.Length == 0)
            {
                continue;
            }

            if (col + 1 + piece.Length > 78 && col > 1)
            {
                sb.Append(eol).Append(' ');
                col = 1;
            }
            else
            {
                sb.Append(' ');
                col++;
            }

            sb.Append(piece);
            col += piece.Length;
        }

        return sb.Append(eol).ToString();
    }
}

/// <summary>Builds <c>Authentication-Results</c> values (RFC 8601) from verification results.</summary>
public static class AuthenticationResultsFormatter
{
    /// <summary>Renders <c>authserv-id; dkim=pass header.d=...; arc=pass ...</c>.</summary>
    /// <param name="authservId">The identifier of the server that did the checking, usually its host name.</param>
    /// <param name="dkim">The DKIM results; with none, <c>dkim=none</c> is written.</param>
    /// <param name="arc">The ARC result, or null to leave ARC out.</param>
    /// <returns>The header value, on one line.</returns>
    public static string Format(string authservId, IEnumerable<DkimSignatureResult>? dkim, ArcVerificationResult? arc = null)
    {
        ArgumentNullException.ThrowIfNull(authservId);
        var sb = new StringBuilder(authservId);
        bool any = false;
        foreach (DkimSignatureResult r in dkim ?? [])
        {
            sb.Append("; ").Append(r.ToString());
            any = true;
        }

        if (!any)
        {
            sb.Append("; dkim=none");
        }

        if (arc is not null)
        {
            sb.Append("; ").Append(arc.ToString());
        }

        return sb.ToString();
    }
}
