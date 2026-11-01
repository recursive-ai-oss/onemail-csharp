using System.Security.Cryptography;
using RecursiveAI.OneMail.Mime;

namespace RecursiveAI.OneMail.Cryptography.Dkim;

/// <summary>
/// Verifies the <c>DKIM-Signature</c> headers of a message (RFC 6376, with RFC 8301's algorithm rules and RFC 8463's Ed25519). Every signature
/// gets its own <see cref="DkimSignatureResult"/>; nothing is collapsed into a yes/no. The message is canonicalised from its bytes exactly as
/// they are; bare LF line ends are treated as the CRLF they would be on the wire.
/// </summary>
public sealed class DkimVerifier
{
    private readonly DkimVerifierOptions _options;

    /// <summary>Creates a verifier.</summary>
    /// <param name="options">Settings; <see cref="DkimVerifierOptions.Resolver"/> is required.</param>
    /// <exception cref="ArgumentException">No resolver was given.</exception>
    public DkimVerifier(DkimVerifierOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        if (options.Resolver is null)
        {
            throw new ArgumentException("DkimVerifierOptions.Resolver is required.", nameof(options));
        }
    }

    /// <summary>Verifies every DKIM signature on a message.</summary>
    /// <param name="message">The complete message as bytes: headers, blank line, body.</param>
    /// <param name="cancellationToken">Cancels pending DNS lookups.</param>
    /// <returns>One result per <c>DKIM-Signature</c> header, top to bottom; empty if the message has none. Malformed signatures and DNS failures are results, not exceptions.</returns>
    public async Task<IReadOnlyList<DkimSignatureResult>> VerifyAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        var parts = new MessageParts(message);
        var ctx = new VerifyContext(parts, _options);
        var results = new List<DkimSignatureResult>();
        int index = 0;
        for (int i = 0; i < parts.Headers.Count; i++)
        {
            HeaderField h = parts.Headers[i];
            if (!h.Name.Equals("DKIM-Signature", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var res = new DkimSignatureResult { Index = index, HeaderName = h.Name, HeaderRange = new DkimByteRange(h.Start, h.End - h.Start) };
            if (index >= _options.MaxSignatures)
            {
                res.Fail(DkimStatus.Neutral, DkimReason.TooManySignatures, "more than " + _options.MaxSignatures + " signatures; this one was not examined");
            }
            else
            {
                await VerifyOneAsync(ctx, i, res, cancellationToken).ConfigureAwait(false);
            }

            results.Add(res);
            index++;
        }

        return results;
    }

    /// <summary>Verifies every DKIM signature on a parsed message, using the bytes the tree was read from (or writes back).</summary>
    /// <param name="entity">The message entity.</param>
    /// <param name="cancellationToken">Cancels pending DNS lookups.</param>
    /// <returns>As <see cref="VerifyAsync(ReadOnlyMemory{byte}, CancellationToken)"/>.</returns>
    public Task<IReadOnlyList<DkimSignatureResult>> VerifyAsync(MimeEntity entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return VerifyAsync(entity.ToArray(), cancellationToken);
    }

    private static async Task VerifyOneAsync(VerifyContext ctx, int headerIndex, DkimSignatureResult res, CancellationToken ct)
    {
        MessageParts m = ctx.Message;
        HeaderField h = m.Headers[headerIndex];
        if (!SignatureEngine.TryParse(m, h, SigKind.Dkim, ctx.Options, res, out ParsedSignature sig))
        {
            return;
        }

        await VerifyParsedAsync(ctx, sig, headerIndex, res, ct).ConfigureAwait(false);
    }

    /// <summary>The part DKIM signatures and ARC message signatures share: clock, key, body hash, header signature.</summary>
    internal static async Task VerifyParsedAsync(VerifyContext ctx, ParsedSignature sig, int headerIndex, DkimSignatureResult res, CancellationToken ct)
    {
        MessageParts m = ctx.Message;
        if (sig.Algorithm == DkimAlgorithm.RsaSha1 && !ctx.Options.AllowRsaSha1)
        {
            res.Fail(DkimStatus.Fail, DkimReason.Sha1Refused, "rsa-sha1 is refused by this verifier's policy (RFC 8301)");
            return;
        }

        if (!SignatureEngine.CheckTime(ctx, sig, res))
        {
            return;
        }

        List<DkimKeyRecord>? keys = await SignatureEngine.GetKeysAsync(ctx, sig, res, ct).ConfigureAwait(false);
        if (keys is null)
        {
            return;
        }

        // Body hash (RFC 6376 §3.7, §5.1): the first l bytes of the canonicalised body, or all of it.
        BodyHashInfo body = ctx.BodyHash(sig.BodyCanon, sig.BodyLength, sig.Algorithm == DkimAlgorithm.RsaSha1);
        res.BodyBytesCovered = body.Hashed;
        res.CoveredBody = new DkimByteRange(m.BodyStart, Math.Max(0, body.CoveredEnd - m.BodyStart));
        res.BodyHasUnsignedContent = sig.BodyLength is { } l && body.Total > l;
        // The whole-body length is what l= is compared with (RFC 6376 §3.5: l larger than the body is a signature error).
        bool lengthTooBig = sig.BodyLength is { } want && want > body.Total;

        var covered = new List<DkimByteRange>();
        var oversigned = new List<string>();
        var data = new List<byte>(1024);
        SignatureEngine.AppendSelectedHeaders(m, sig.SignedHeaders, sig.HeaderCanon, headerIndex, data, covered, oversigned);
        SignatureEngine.AppendSignatureHeader(m, sig, sig.HeaderCanon, data);
        res.CoveredHeaders = covered;
        res.OversignedHeaders = oversigned;
        byte[] headerData = [.. data];

        (DkimStatus Status, DkimReason Reason, string Detail)? firstFailure = null;
        foreach (DkimKeyRecord key in keys)
        {
            if (SignatureEngine.CheckKey(ctx, sig, key) is { } bad)
            {
                firstFailure ??= bad;
                continue;
            }

            SignatureEngine.Annotate(res, key);
            if (lengthTooBig)
            {
                res.Fail(DkimStatus.Fail, DkimReason.BodyLengthExceedsBody, "l= is larger than the body");
                return;
            }

            if (!CryptographicOperations.FixedTimeEquals(body.Hash, sig.BodyHash))
            {
                res.Fail(DkimStatus.Fail, DkimReason.BodyHashMismatch, "body hash did not verify");
                return;
            }

            if (SignatureEngine.CheckSignature(sig.Algorithm, key, headerData, sig.Signature))
            {
                res.Status = DkimStatus.Pass;
                res.Reason = DkimReason.None;
                res.Detail = null;
                return;
            }

            firstFailure ??= (DkimStatus.Fail, DkimReason.SignatureMismatch, "signature did not verify");
        }

        (DkimStatus status, DkimReason reason, string detail) = firstFailure!.Value;
        res.Fail(status, reason, detail);
        if (reason is DkimReason.KeyTooWeak or DkimReason.KeyRevoked)
        {
            res.KeyBits = keys[0].KeyBits;
        }
    }
}
