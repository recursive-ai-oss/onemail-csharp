using RecursiveAI.OneCrypto;
using RecursiveAI.OneCrypto.Asn1;
using RecursiveAI.OneCrypto.Policy;
using RecursiveAI.OneCrypto.Validation;
using RecursiveAI.OneCrypto.Wire.Cms;
using RecursiveAI.OneCrypto.X509;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Text;

namespace RecursiveAI.OneMail.Cryptography.Smime;

/// <summary>Choices for reading S/MIME with <see cref="SmimeProcessor"/>. The defaults trust nothing and decrypt nothing.</summary>
public sealed class SmimeOpenOptions
{
    /// <summary>Private keys to try on encrypted layers, each with the certificate it belongs to when known. Default none: encrypted layers stay closed (<see cref="SmimeStatus.NoDecryptionKey"/>).</summary>
    public IReadOnlyList<CmsDecryptionKey> DecryptionKeys { get; init; } = [];

    /// <summary>Certificates to look signers up in besides those inside the message (for a signature that carries none). Default none.</summary>
    public IReadOnlyList<Certificate> Certificates { get; init; } = [];

    /// <summary>
    /// Trust options (anchors, reference time, revocation data) for building and validating each signer's chain. Null (default) builds no chain
    /// and uses no operating-system trust store: a valid signature then only proves the signer held the certificate's key.
    /// </summary>
    public ValidationOptions? Chain { get; init; }

    /// <summary>A crypto policy for signatures and ciphers. Null applies none (weak algorithms are still flagged in <see cref="SmimeSignerResult.WeakAlgorithms"/>). Needs <see cref="ReferenceTime"/>.</summary>
    public CryptoPolicy? Policy { get; init; }

    /// <summary>The instant <see cref="Policy"/> is asked about; required with it. The library never reads a clock.</summary>
    public DateTimeOffset? ReferenceTime { get; init; }

    /// <summary>Decrypt Triple-DES content. Default false: such a layer ends as <see cref="SmimeStatus.Unsupported"/>.</summary>
    public bool AllowLegacyCiphers { get; init; }

    /// <summary>Accept RSA PKCS #1 v1.5 key transport when decrypting (RFC 3218 §2.3 protections apply). Default true: it is what older clients write.</summary>
    public bool AllowPkcs1V15KeyTransport { get; init; } = true;

    /// <summary>The most S/MIME layers unwrapped one inside another. Default 8; deeper nesting stops with <see cref="SmimeIssue.DepthLimitReached"/>.</summary>
    public int MaxDepth { get; init; } = 8;

    /// <summary>Limits for the CMS parsers. Default: <see cref="CmsLimits.Default"/> with content and decompressed output cut to 256 MiB, which is more than mail carries.</summary>
    public CmsLimits Limits { get; init; } = new() { MaxContentBytes = 256L << 20, MaxDecompressedBytes = 256L << 20 };
}

/// <summary>
/// Opens S/MIME messages (RFC 8551): verifies <c>multipart/signed</c> and opaque signed-data, decrypts enveloped and authEnveloped data,
/// inflates compressed data, and walks nested combinations (signed then encrypted, triple-wrapped). The result is structured, never a bare
/// bool, and nothing here throws on a hostile message. CMS parsing and the cryptography are OneCrypto's.
/// </summary>
public static class SmimeProcessor
{
    private const string SmimeCapabilitiesOid = "1.2.840.113549.1.9.15";
    private const string SmimeEncryptionKeyPreferenceOid = "1.2.840.113549.1.9.16.2.11";
    private const string EmailProtectionOid = "1.3.6.1.5.5.7.3.4";
    private const string EmailAddressOid = "1.2.840.113549.1.9.1";

    /// <summary>
    /// Examines <paramref name="message"/>. If it is an S/MIME entity the layers are processed and unwrapped as far as the options allow.
    /// If it is a multipart wrapper (for example <c>multipart/mixed</c>) the first S/MIME entity below it is processed and
    /// <see cref="SmimeResult.Outside"/> lists what the wrapper leaves unsigned. A message without S/MIME gives an empty result.
    /// </summary>
    /// <param name="message">The message or part, as parsed by <see cref="MimeEntity.Parse"/>.</param>
    /// <param name="options">Keys, certificates, trust and policy; null for the defaults.</param>
    /// <returns>The layers outermost first, the content reached and what lies outside the signatures.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><see cref="SmimeOpenOptions.Policy"/> is set without <see cref="SmimeOpenOptions.ReferenceTime"/>, or the reverse.</exception>
    public static SmimeResult Open(MimeEntity message, SmimeOpenOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        options ??= new SmimeOpenOptions();
        if ((options.Policy is null) != (options.ReferenceTime is null))
        {
            throw new ArgumentException("Policy and ReferenceTime are given together.", nameof(options));
        }

        var result = new SmimeResult(message) { Content = message };
        var layers = new List<SmimeLayer>();
        var unsignedParts = new List<string>();
        MimeEntity? start = message;
        if (Classify(message, out _) == Shape.None)
        {
            start = FindNested(message, unsignedParts, "");
        }

        MimeEntity current = start ?? message;
        List<SmimeIssue> resultIssues = [];
        if (start is not null && start != message)
        {
            resultIssues.Add(SmimeIssue.SignedPartInUnsignedWrapper);
        }

        if (start is null)
        {
            result.Layers = [];
            return result;
        }

        MimeEntity? senderSource = message;
        for (int depth = 0; ; depth++)
        {
            Shape shape = Classify(current, out List<SmimeIssue> classIssues);
            if (shape == Shape.None)
            {
                break;
            }

            if (depth >= options.MaxDepth)
            {
                resultIssues.Add(SmimeIssue.DepthLimitReached);
                break;
            }

            SmimeLayer layer;
            MimeEntity? next;
            if (shape == Shape.MultipartSigned)
            {
                layer = VerifyDetached((MimeMultipart)current, options, senderSource, classIssues, out next);
            }
            else
            {
                layer = OpenPkcs7(current, options, senderSource, classIssues, out next);
            }

            layer.Entity = current;
            layers.Add(layer);
            if (next is null)
            {
                break;
            }

            current = next;
            result.Content = next;
            if (senderSource is not null && HasSender(senderSource) == false && HasSender(next))
            {
                senderSource = next;
            }
        }

        result.Layers = layers;
        result.Issues = resultIssues;
        if (layers.Count > 0 && !ReferenceEquals(result.Content, message))
        {
            result.InnerItem = MakeItem(result.Content);
        }

        if (layers.Any(static l => l is SmimeSignedLayer))
        {
            List<string>? protectedHeaders = null;
            SmimeSignedLayer firstSigned = layers.OfType<SmimeSignedLayer>().First();
            if (firstSigned.SignedEntity is MimeMessagePart { Message: { } inner })
            {
                protectedHeaders = inner.Headers.Select(static h => h.Name).ToList();
            }
            else if (firstSigned.SignedEntity is { } signedEntity && IsMessageLike(signedEntity))
            {
                protectedHeaders = signedEntity.Headers.Where(static h => !IsContentHeader(h.Name)).Select(static h => h.Name).ToList();
            }

            result.Outside = new SmimeOutside(
                message.Headers.Where(static h => !IsContentHeader(h.Name)).Select(static h => h.Name).ToList(),
                unsignedParts,
                protectedHeaders ?? []);
        }

        return result;
    }

    /// <summary>
    /// Parses the bytes of a message and processes it as <see cref="Open(MimeEntity, SmimeOpenOptions?)"/> does.
    /// </summary>
    /// <param name="message">The message bytes.</param>
    /// <param name="options">Keys, certificates, trust and policy; null for the defaults.</param>
    /// <returns>The result.</returns>
    /// <exception cref="ArgumentException">Policy and reference time are not given together.</exception>
    public static SmimeResult Open(ReadOnlyMemory<byte> message, SmimeOpenOptions? options = null) => Open(MimeEntity.Parse(message), options);

    // ---- structure ----

    private enum Shape
    {
        None,
        MultipartSigned,
        Pkcs7Mime,
    }

    private static Shape Classify(MimeEntity e, out List<SmimeIssue> issues)
    {
        issues = [];
        string type = e.MediaType;
        if (e is MimeMultipart && type == "multipart/signed")
        {
            return Shape.MultipartSigned;
        }

        if (e is MimePart && type is "application/pkcs7-mime" or "application/x-pkcs7-mime")
        {
            return Shape.Pkcs7Mime;
        }

        // MS-OXOSMIME 2.1.3.2.1.1: octet-stream with a .p7m name is an opaque-signed or encrypted message.
        if (e is MimePart && type == "application/octet-stream" && HasP7mName(e))
        {
            issues.Add(SmimeIssue.RecognisedByFileName);
            return Shape.Pkcs7Mime;
        }

        return Shape.None;
    }

    private static bool HasP7mName(MimeEntity e)
    {
        string? name = e.GetContentTypeParameter("name");
        if (name is not null && name.EndsWith(".p7m", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (DecodedParameter p in e.GetDispositionParameters())
        {
            if (string.Equals(p.Name, "filename", StringComparison.OrdinalIgnoreCase) && p.Value.EndsWith(".p7m", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // Finds the first S/MIME entity below non-S/MIME multiparts; records the paths of the siblings that sit beside it (outside any signature).
    private static MimeEntity? FindNested(MimeEntity e, List<string> siblings, string path)
    {
        if (e is not MimeMultipart mp)
        {
            return null;
        }

        for (int i = 0; i < mp.Children.Count; i++)
        {
            MimeEntity child = mp.Children[i];
            string childPath = path.Length == 0 ? (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : path + "." + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (Classify(child, out _) != Shape.None)
            {
                for (int j = 0; j < mp.Children.Count; j++)
                {
                    if (j != i)
                    {
                        siblings.Add(path.Length == 0 ? (j + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : path + "." + (j + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                }

                return child;
            }

            var deeper = new List<string>();
            if (FindNested(child, deeper, childPath) is { } found)
            {
                for (int j = 0; j < mp.Children.Count; j++)
                {
                    if (j != i)
                    {
                        siblings.Add(path.Length == 0 ? (j + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : path + "." + (j + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                }

                siblings.AddRange(deeper);
                return found;
            }
        }

        return null;
    }

    private static bool IsContentHeader(string name) =>
        name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) || name.Equals("MIME-Version", StringComparison.OrdinalIgnoreCase);

    private static bool IsMessageLike(MimeEntity e) => e.Headers.Any(static h => !IsContentHeader(h.Name));

    private static MimeEntity? SignedSender(MimeEntity? signed)
    {
        if (signed is MimeMessagePart { Message: { } inner } && HasSender(inner))
        {
            return inner;
        }

        return signed is not null && HasSender(signed) ? signed : null;
    }

    private static bool HasSender(MimeEntity e) => e.Headers.GetFirst("From") is not null || e.Headers.GetFirst("Sender") is not null;

    private static MimeMailItem? MakeItem(MimeEntity content)
    {
        try
        {
            return MimeMailItem.Parse(content.ToArray());
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    // ---- multipart/signed ----

    private static SmimeSignedLayer VerifyDetached(MimeMultipart mp, SmimeOpenOptions options, MimeEntity? senderSource, List<SmimeIssue> issues, out MimeEntity? next)
    {
        var layer = new SmimeSignedLayer(SmimeLayerKind.SignedDetached) { Micalg = mp.GetContentTypeParameter("micalg") };
        next = null;
        string? protocol = mp.GetContentTypeParameter("protocol")?.ToLowerInvariant();
        if (protocol is not ("application/pkcs7-signature" or "application/x-pkcs7-signature"))
        {
            issues.Add(SmimeIssue.ProtocolParameterUnexpected);
        }

        if (layer.Micalg is null)
        {
            issues.Add(SmimeIssue.MicalgMissing);
        }

        // RFC 1847 §2.1: exactly two body parts. More than two is how "boundary games" hide unsigned content next to a valid signature.
        if (mp.Children.Count != 2)
        {
            return Fail(layer, issues, SmimeStatus.StructureInvalid, "A multipart/signed has " + mp.Children.Count + " parts; RFC 1847 requires exactly two.");
        }

        MimeEntity signedPart = mp.Children[0];
        layer.SignedEntity = signedPart;
        next = signedPart;
        if (mp.Children[1] is not MimePart sigPart)
        {
            return Fail(layer, issues, SmimeStatus.StructureInvalid, "The second part of a multipart/signed is not a leaf part.");
        }

        if (sigPart.MediaType is not ("application/pkcs7-signature" or "application/x-pkcs7-signature"))
        {
            issues.Add(SmimeIssue.SignaturePartTypeUnexpected);
        }

        byte[] cms;
        try
        {
            cms = sigPart.GetDecodedContent();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return Fail(layer, issues, SmimeStatus.Malformed, "The signature part could not be decoded: " + e.GetType().Name);
        }

        if (!SignedData.TryParse(cms, out SignedData? sd, options.Limits) || sd is null)
        {
            return Fail(layer, issues, SmimeStatus.Malformed, "The signature part is not a CMS SignedData.");
        }

        if (sd.HasEncapsulatedContent)
        {
            // A detached signature must not carry content of its own; a verifier that preferred it would show different text than it checked.
            return Fail(layer, issues, SmimeStatus.StructureInvalid, "The detached signature carries its own content.");
        }

        byte[] covered = signedPart.ToArray();
        layer.CoveredBytes = covered;
        if (!signedPart.IsModified)
        {
            layer.CoveredSourceOffset = signedPart.SourceStart;
            layer.CoveredSourceLength = signedPart.SourceLength;
        }

        SignedDataReport report = Verify(sd, covered, options);
        if (!report.IsValid && report.Signers.Any(static s => s.Find("message-digest") is { Status: CmsCheckStatus.Failed })
            && SmimeContent.NormaliseLineEnds(covered) is { } canonical)
        {
            // RFC 8551 §3.1.1: the signature is over the canonical (CRLF) form; a store that kept LF endings changed the bytes, not the content.
            SignedDataReport retry = Verify(sd, canonical, options);
            if (retry.IsValid)
            {
                report = retry;
                layer.CoveredBytes = canonical;
                issues.Add(SmimeIssue.SignedPartLineEndingsNormalised);
            }
        }

        if (layer.Micalg is not null && report.Signers.Count > 0)
        {
            string[] declared = layer.Micalg.ToLowerInvariant().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            IEnumerable<string> actual = report.Signers.Select(static s => SmimeContent.MicalgFor(s.Signer.DigestAlgorithm.Oid));
            if (!actual.All(a => declared.Contains(a, StringComparer.Ordinal)))
            {
                issues.Add(SmimeIssue.MicalgMismatch);
            }
        }

        Fill(layer, sd, report, options, senderSource, issues);
        return layer;
    }

    // ---- application/pkcs7-mime ----

    private static SmimeLayer OpenPkcs7(MimeEntity entity, SmimeOpenOptions options, MimeEntity? senderSource, List<SmimeIssue> issues, out MimeEntity? next)
    {
        next = null;
        string? declared = entity.GetContentTypeParameter("smime-type")?.ToLowerInvariant();
        byte[] body;
        try
        {
            body = ((MimePart)entity).GetDecodedContent();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return FailContent(SmimeLayerKind.SignedOpaque, issues, SmimeStatus.Malformed, "The body could not be decoded: " + e.GetType().Name);
        }

        if (!ContentInfo.TryParse(body, out ContentInfo? ci, options.Limits, allowTrailingZeros: true) || ci is null)
        {
            return FailContent(KindFromDeclared(declared), issues, SmimeStatus.Malformed, "The body is not a CMS ContentInfo.");
        }

        SmimeLayerKind kind = ci.ContentType switch
        {
            CmsOids.EnvelopedData => SmimeLayerKind.Enveloped,
            CmsOids.AuthEnvelopedData => SmimeLayerKind.AuthEnveloped,
            CmsOids.CompressedData => SmimeLayerKind.Compressed,
            CmsOids.SignedData => SmimeLayerKind.SignedOpaque,
            _ => KindFromDeclared(declared),
        };
        if (ci.ContentType is not (CmsOids.EnvelopedData or CmsOids.AuthEnvelopedData or CmsOids.CompressedData or CmsOids.SignedData))
        {
            return FailContent(kind, issues, SmimeStatus.Unsupported, "The CMS content type " + ci.ContentType + " is not an S/MIME type.");
        }

        string? expected = kind switch
        {
            SmimeLayerKind.Enveloped => "enveloped-data",
            SmimeLayerKind.AuthEnveloped => "authenveloped-data",
            SmimeLayerKind.Compressed => "compressed-data",
            _ => null,
        };
        if (declared is null)
        {
            issues.Add(SmimeIssue.SmimeTypeMissing);
        }
        else if (expected is not null ? declared != expected : declared is not ("signed-data" or "certs-only"))
        {
            issues.Add(SmimeIssue.SmimeTypeMismatch);
        }

        switch (kind)
        {
            case SmimeLayerKind.SignedOpaque:
                return OpenSigned(body, options, senderSource, issues, out next);
            case SmimeLayerKind.Enveloped:
            case SmimeLayerKind.AuthEnveloped:
                return Decrypt(kind, body, options, issues, out next);
            default:
                return Decompress(body, options, issues, out next);
        }
    }

    private static SmimeLayerKind KindFromDeclared(string? declared) => declared switch
    {
        "enveloped-data" => SmimeLayerKind.Enveloped,
        "authenveloped-data" => SmimeLayerKind.AuthEnveloped,
        "compressed-data" => SmimeLayerKind.Compressed,
        "certs-only" => SmimeLayerKind.CertsOnly,
        _ => SmimeLayerKind.SignedOpaque,
    };

    private static SmimeSignedLayer OpenSigned(byte[] cms, SmimeOpenOptions options, MimeEntity? senderSource, List<SmimeIssue> issues, out MimeEntity? next)
    {
        var layer = new SmimeSignedLayer(SmimeLayerKind.SignedOpaque);
        next = null;
        if (!SignedData.TryParse(cms, out SignedData? sd, options.Limits) || sd is null)
        {
            return Fail(layer, issues, SmimeStatus.Malformed, "The body is not a CMS SignedData.");
        }

        if (sd.Signers.Count == 0 && !sd.HasEncapsulatedContent)
        {
            // RFC 8551 §3.7: a degenerate SignedData carries certificates only.
            return new SmimeSignedLayer(SmimeLayerKind.CertsOnly)
            {
                Status = SmimeStatus.CertificatesOnly,
                Certificates = sd.Certificates,
                Issues = issues,
                Detail = "The message carries certificates and no content.",
            };
        }

        if (!sd.HasEncapsulatedContent)
        {
            return Fail(layer, issues, SmimeStatus.StructureInvalid, "An opaque signed message holds no content.");
        }

        byte[] content = sd.EncapsulatedContent.ToArray();
        layer.CoveredBytes = content;
        SignedDataReport report = Verify(sd, null, options);
        MimeEntity inner = ParseInner(content, issues);
        layer.SignedEntity = inner;
        next = inner;
        Fill(layer, sd, report, options, senderSource, issues);
        return layer;
    }

    private static SmimeLayer Decrypt(SmimeLayerKind kind, byte[] cms, SmimeOpenOptions options, List<SmimeIssue> issues, out MimeEntity? next)
    {
        var layer = new SmimeContentLayer(kind);
        next = null;
        Func<CmsDecryptionKey, CmsDecryptOptions, byte[]> decrypt;
        if (kind == SmimeLayerKind.Enveloped)
        {
            if (!EnvelopedData.TryParse(cms, out EnvelopedData? ed, options.Limits) || ed is null)
            {
                return FailContent(kind, issues, SmimeStatus.Malformed, "The body is not a CMS EnvelopedData.");
            }

            layer.ContentAlgorithm = ed.ContentEncryptionAlgorithm.Oid;
            layer.RecipientCount = ed.RecipientInfos.Count;
            layer.IsAuthenticated = false;
            decrypt = (k, o) => ed.Decrypt(k, o);
            // RFC 8551 §2.7: CBC cannot detect changes that keep the padding valid.
            issues.Add(SmimeIssue.UnauthenticatedEncryption);
        }
        else
        {
            if (!AuthEnvelopedData.TryParse(cms, out AuthEnvelopedData? ad, options.Limits) || ad is null)
            {
                return FailContent(kind, issues, SmimeStatus.Malformed, "The body is not a CMS AuthEnvelopedData.");
            }

            layer.ContentAlgorithm = ad.ContentEncryptionAlgorithm.Oid;
            layer.RecipientCount = ad.RecipientInfos.Count;
            layer.IsAuthenticated = true;
            decrypt = (k, o) => ad.Decrypt(k, o);
        }

        if (layer.ContentAlgorithm == CmsOids.TripleDesCbc && options.AllowLegacyCiphers)
        {
            issues.Add(SmimeIssue.LegacyCipherAllowed);
        }

        layer.Issues = issues;
        if (options.DecryptionKeys.Count == 0)
        {
            layer.Status = SmimeStatus.NoDecryptionKey;
            layer.Detail = "No decryption key was supplied.";
            return layer;
        }

        var dopts = new CmsDecryptOptions
        {
            Limits = options.Limits,
            AllowLegacyCiphers = options.AllowLegacyCiphers,
            AllowPkcs1V15KeyTransport = options.AllowPkcs1V15KeyTransport,
            Policy = options.Policy,
            ReferenceTime = options.ReferenceTime,
        };
        SmimeStatus status = SmimeStatus.NoMatchingRecipient;
        string detail = "No supplied key is addressed by a recipient of the message.";
        foreach (CmsDecryptionKey key in options.DecryptionKeys)
        {
            try
            {
                byte[] plain = decrypt(key, dopts);
                layer.Status = SmimeStatus.Decrypted;
                layer.RecipientCertificate = key.Certificate;
                layer.Detail = null;
                next = ParseInner(plain, issues);
                layer.Issues = issues;
                return layer;
            }
            catch (CmsParseException e) when (e.Error == CmsError.NoMatchingRecipient)
            {
                // Try the next key.
            }
            catch (CmsParseException e) when (e.Error == CmsError.Unsupported)
            {
                status = SmimeStatus.Unsupported;
                detail = e.Message;
            }
            catch (CmsParseException e) when (e.Error == CmsError.LimitExceeded)
            {
                status = SmimeStatus.LimitExceeded;
                detail = e.Message;
            }
            catch (CmsParseException)
            {
                // One error for every failure after a recipient matched (OneCrypto's rule), so a wrong key, a damaged key and a bad tag look alike.
                status = SmimeStatus.DecryptionFailed;
                detail = "A recipient matched, but the message could not be decrypted.";
            }
            catch (PolicyViolationException e)
            {
                status = SmimeStatus.PolicyRejected;
                detail = e.Message;
                layer.Policy = e.Report;
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                // A padding or tag failure from the platform must look like every other failure after a recipient matched.
                status = SmimeStatus.DecryptionFailed;
                detail = "A recipient matched, but the message could not be decrypted.";
            }
            catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException or FormatException or AlgorithmNotSupportedException)
            {
                status = SmimeStatus.Unsupported;
                detail = e.GetType().Name + ": " + e.Message;
            }
        }

        layer.Status = status;
        layer.Detail = detail;
        return layer;
    }

    private static SmimeLayer Decompress(byte[] cms, SmimeOpenOptions options, List<SmimeIssue> issues, out MimeEntity? next)
    {
        next = null;
        if (!CompressedData.TryParse(cms, out CompressedData? cd, options.Limits) || cd is null)
        {
            return FailContent(SmimeLayerKind.Compressed, issues, SmimeStatus.Malformed, "The body is not a CMS CompressedData.");
        }

        var layer = new SmimeContentLayer(SmimeLayerKind.Compressed) { ContentAlgorithm = cd.CompressionAlgorithm.Oid, Issues = issues };
        try
        {
            next = ParseInner(cd.Decompress(options.Limits), issues);
            layer.Status = SmimeStatus.Decompressed;
        }
        catch (CmsParseException e)
        {
            layer.Status = e.Error == CmsError.LimitExceeded ? SmimeStatus.LimitExceeded : SmimeStatus.Malformed;
            layer.Detail = e.Message;
        }

        return layer;
    }

    private static MimeEntity ParseInner(byte[] bytes, List<SmimeIssue> issues)
    {
        MimeEntity entity = MimeEntity.Parse(bytes);
        // Content that parses with no header at all is not a MIME entity (RFC 8551 §3.1 requires one); keep the bytes as a binary part.
        if (entity.Headers.Count == 0 && entity is MimePart)
        {
            var wrapped = new MimePart("application/octet-stream");
            wrapped.SetDecodedContent(bytes);
            issues.Add(SmimeIssue.InnerContentNotMime);
            return wrapped;
        }

        return entity;
    }

    // ---- verification ----

    private static SignedDataReport Verify(SignedData sd, byte[]? detached, SmimeOpenOptions options)
    {
        var vopts = new SignedDataVerifyOptions
        {
            ExtraCertificates = options.Certificates,
            Policy = options.Policy,
            ReferenceTime = options.ReferenceTime,
            Chain = options.Chain,
            Limits = options.Limits,
        };
        return detached is null ? sd.Verify(vopts) : sd.Verify(detached, vopts);
    }

    private static void Fill(SmimeSignedLayer layer, SignedData sd, SignedDataReport report, SmimeOpenOptions options, MimeEntity? senderSource, List<SmimeIssue> issues)
    {
        layer.Report = report;
        layer.Certificates = sd.Certificates;
        // RFC 8551 §3.2.2: the address to check is the one in the signed message. When the signed entity carries its own From, that is the one;
        // the headers around a multipart/signed are not covered, so they are only used when the signed entity has none.
        List<string> senders = Senders(SignedSender(layer.SignedEntity) ?? senderSource);
        var signers = new List<SmimeSignerResult>(report.Signers.Count);
        foreach (SignerReport r in report.Signers)
        {
            signers.Add(BuildSigner(r, senders, options));
        }

        layer.Signers = signers;
        layer.Issues = issues;
        if (report.Signers.Count == 0)
        {
            layer.Status = SmimeStatus.Malformed;
            layer.Detail = "The SignedData has no signer.";
        }
        else if (signers.All(static s => s.IsValid) && report.Checks.All(static c => c.Status is not (CmsCheckStatus.Failed or CmsCheckStatus.Unsupported)))
        {
            layer.Status = SmimeStatus.Valid;
        }
        else
        {
            SmimeSignerResult bad = signers.FirstOrDefault(static s => !s.IsValid) ?? signers[0];
            layer.Status = bad.Status == SmimeStatus.Valid ? SmimeStatus.Malformed : bad.Status;
            layer.Detail = "At least one signer did not validate.";
        }
    }

    private static SmimeSignerResult BuildSigner(SignerReport r, List<string> senders, SmimeOpenOptions options)
    {
        var s = new SmimeSignerResult(r)
        {
            DigestAlgorithm = r.Signer.DigestAlgorithm.Oid,
            SignatureAlgorithm = r.Signer.SignatureAlgorithm.Oid,
        };

        CmsCheck? Find(string rule) => r.Checks.FirstOrDefault(c => c.Rule == rule);
        bool Failed(string rule) => Find(rule) is { Status: CmsCheckStatus.Failed };

        if (Failed("signer-certificate"))
        {
            s.Status = SmimeStatus.CertificateNotFound;
        }
        else if (Failed("message-digest"))
        {
            s.Status = SmimeStatus.ContentModified;
        }
        else if (Failed("signature"))
        {
            s.Status = SmimeStatus.SignatureInvalid;
        }
        else if (Find("signature") is { Status: CmsCheckStatus.Unsupported } || Find("digest-algorithm") is { Status: CmsCheckStatus.Unsupported or CmsCheckStatus.Failed })
        {
            s.Status = SmimeStatus.Unsupported;
        }
        else if (Failed("policy"))
        {
            s.Status = SmimeStatus.PolicyRejected;
        }
        else if (r.Checks.Any(static c => c.Status == CmsCheckStatus.Failed && c.Rule != "chain"))
        {
            s.Status = SmimeStatus.RuleFailed;
        }
        else if (Find("chain") is { } chain)
        {
            s.Chain = chain.Status switch
            {
                CmsCheckStatus.Passed => SmimeChainStatus.Passed,
                CmsCheckStatus.Warning => SmimeChainStatus.Warning,
                CmsCheckStatus.Unsupported => SmimeChainStatus.Undecided,
                CmsCheckStatus.Failed => SmimeChainStatus.Failed,
                _ => SmimeChainStatus.NotChecked,
            };
            s.Status = s.Chain is SmimeChainStatus.Failed or SmimeChainStatus.Undecided ? SmimeStatus.UntrustedChain : SmimeStatus.Valid;
        }
        else if (r.Checks.Any(static c => c.Status == CmsCheckStatus.Unsupported))
        {
            s.Status = SmimeStatus.Unsupported;
        }
        else
        {
            s.Status = SmimeStatus.Valid;
        }

        if (Find("chain") is { } ch && s.Chain == SmimeChainStatus.NotChecked)
        {
            s.Chain = ch.Status switch
            {
                CmsCheckStatus.Passed => SmimeChainStatus.Passed,
                CmsCheckStatus.Warning => SmimeChainStatus.Warning,
                CmsCheckStatus.Unsupported => SmimeChainStatus.Undecided,
                CmsCheckStatus.Failed => SmimeChainStatus.Failed,
                _ => SmimeChainStatus.NotChecked,
            };
        }

        // Weak algorithms: reading accepts them, the result flags them.
        var weak = new List<string>();
        if (Find("digest-algorithm") is { Status: CmsCheckStatus.Warning })
        {
            weak.Add(SmimeContent.DigestTag(s.DigestAlgorithm));
        }

        if (Find("key-strength") is { Status: CmsCheckStatus.Warning })
        {
            weak.Add("key:weak");
        }

        if (r.Policy is { } pol)
        {
            weak.AddRange(pol.Findings.Select(static d => d.Rule));
        }

        if (r.Certificate is { } cert)
        {
            try
            {
                if (cert.ExtendedKeyUsage is { } eku && !eku.Allows(EmailProtectionOid))
                {
                    // RFC 8550 §4.4.4: when the extended key usage is present it has to allow e-mail protection.
                    weak.Add("eku:no-emailProtection");
                }
            }
            catch (X509ParseException)
            {
                // A certificate inside a message can carry a damaged extension value; report it rather than fail the layer.
                weak.Add("eku:malformed");
            }

            s.CertificateAddresses = AddressesOf(cert);
            s.SenderMatch = senders.Count == 0
                ? SmimeSenderMatch.NoSenderAddress
                : s.CertificateAddresses.Count == 0
                    ? SmimeSenderMatch.NoCertificateAddress
                    : senders.All(a => s.CertificateAddresses.Contains(a, StringComparer.OrdinalIgnoreCase)) ? SmimeSenderMatch.Match : SmimeSenderMatch.Mismatch;
        }
        else
        {
            s.SenderMatch = SmimeSenderMatch.NoCertificate;
        }

        s.WeakAlgorithms = weak;
        ReadSignedAttributes(r.Signer, s);
        return s;
    }

    private static void ReadSignedAttributes(SignerInfo signer, SmimeSignerResult s)
    {
        s.HasEncryptionKeyPreference = signer.FindSigned(SmimeEncryptionKeyPreferenceOid) is not null;
        if (signer.FindSigned(SmimeCapabilitiesOid) is not { Values.Count: > 0 } caps)
        {
            return;
        }

        var oids = new List<string>();
        try
        {
            var reader = new Asn1Reader(caps.Values[0].Span);
            Asn1Reader seq = reader.ReadSequence();
            while (seq.HasData)
            {
                Asn1Reader item = seq.ReadSequence();
                oids.Add(item.ReadObjectIdentifier());
                if (oids.Count >= 64)
                {
                    break;
                }
            }
        }
        catch (Asn1ParseException)
        {
            // A damaged capabilities list is advisory data; keep what was read.
        }

        s.Capabilities = oids;
    }

    private static List<string> AddressesOf(Certificate cert)
    {
        var list = new List<string>();
        try
        {
            if (cert.SubjectAlternativeName is { } san)
            {
                foreach (GeneralName n in san.Names)
                {
                    if (n.Kind == GeneralNameKind.Rfc822Name && n.Value is { Length: > 0 } v)
                    {
                        list.Add(v);
                    }
                }
            }
        }
        catch (X509ParseException)
        {
            // A malformed extension value: the certificate stays usable, it just yields no SAN addresses.
        }

        foreach (RelativeDistinguishedName rdn in cert.Subject.Rdns)
        {
            foreach (AttributeTypeAndValue a in rdn.Attributes)
            {
                if (a.Type == EmailAddressOid && a.Text is { Length: > 0 } t && !list.Contains(t, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(t);
                }
            }
        }

        return list;
    }

    private static List<string> Senders(MimeEntity? source)
    {
        var list = new List<string>();
        if (source is null)
        {
            return list;
        }

        foreach (string field in new[] { "From", "Sender" })
        {
            foreach (MimeHeader h in source.Headers.GetAll(field))
            {
                foreach (MimeMailbox m in AddressParser.Flatten(AddressParser.Parse(h.RawValue.Span)))
                {
                    list.Add(m.UnquotedAddress);
                }
            }

            if (list.Count > 0)
            {
                break;
            }
        }

        return list;
    }

    // ---- helpers ----

    private static SmimeSignedLayer Fail(SmimeSignedLayer layer, List<SmimeIssue> issues, SmimeStatus status, string detail)
    {
        layer.Status = status;
        layer.Detail = detail;
        layer.Issues = issues;
        return layer;
    }

    private static SmimeLayer FailContent(SmimeLayerKind kind, List<SmimeIssue> issues, SmimeStatus status, string detail) =>
        kind is SmimeLayerKind.SignedOpaque or SmimeLayerKind.SignedDetached or SmimeLayerKind.CertsOnly
            ? new SmimeSignedLayer(kind) { Status = status, Detail = detail, Issues = issues }
            : new SmimeContentLayer(kind) { Status = status, Detail = detail, Issues = issues };
}
