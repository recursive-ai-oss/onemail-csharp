using System.Text;
using RecursiveAI.OneCrypto.OpenPgp;
using RecursiveAI.OneCrypto.OpenPgp.Packets;
using RecursiveAI.OneCrypto.Policy;
using RecursiveAI.OneMail.Cryptography.Smime;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Mime.Transfer;
using RecursiveAI.OneMail.Text;

namespace RecursiveAI.OneMail.Cryptography.OpenPgp;

/// <summary>Keys, passwords, time and limits for <see cref="PgpMimeProcessor"/>.</summary>
public sealed class PgpOpenOptions
{
    /// <summary>The reference time: signatures made after it, and keys expired by it, are not valid. Required: the library never reads a clock.</summary>
    public required DateTimeOffset Now { get; init; }

    /// <summary>Certificates whose keys may have made the signatures. Which certificates to supply is the caller's trust decision: a signature is judged for the key that made it, nothing more. Default none.</summary>
    public IReadOnlyList<PgpCertificate> Certificates { get; init; } = [];

    /// <summary>Certificates with secret keys used to decrypt. Their own public parts also count as <see cref="Certificates"/>.</summary>
    public IReadOnlyList<PgpCertificate> SecretKeys { get; init; } = [];

    /// <summary>Passwords (UTF-8 octets) that unlock the secret keys.</summary>
    public IReadOnlyList<byte[]> KeyPasswords { get; init; } = [];

    /// <summary>Passwords (UTF-8 octets) for password-encrypted messages.</summary>
    public IReadOnlyList<byte[]> MessagePasswords { get; init; } = [];

    /// <summary>The policy for hashes and key sizes; null means <see cref="CryptoPolicy.Standard"/>. Reading accepts what the policy flags and reports it; only a rejection fails a signature.</summary>
    public CryptoPolicy? Policy { get; init; }

    /// <summary>Open old Symmetrically Encrypted Data, which has no integrity check. Default false.</summary>
    public bool AllowUnauthenticatedEncryption { get; init; }

    /// <summary>
    /// The addresses this message was opened for, bare (<c>bob@example.org</c>). They are compared with the protected <c>To</c> and <c>Cc</c> of an authenticated, encrypted message
    /// to detect surreptitious forwarding (<see cref="PgpIssue.ProtectedRecipientMismatch"/>). Default none: the certified User IDs of <see cref="SecretKeys"/> are used instead.
    /// With neither, the check is not made.
    /// </summary>
    public IReadOnlyList<string> RecipientAddresses { get; init; } = [];

    /// <summary>The most layers unwrapped one inside another. Default 8.</summary>
    public int MaxDepth { get; init; } = 8;

    /// <summary>The most plaintext one decryption may produce, in bytes. Default 256 MiB; more ends the layer as <see cref="PgpStatus.LimitExceeded"/>.</summary>
    public long MaxPlaintextBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>The largest inline text body searched for armor, in bytes. Default 64 MiB.</summary>
    public int MaxInlineBytes { get; init; } = 64 * 1024 * 1024;

    /// <summary>OneCrypto's packet limits; null means <see cref="OpenPgpLimits.Default"/>.</summary>
    public OpenPgpLimits? Limits { get; init; }
}

/// <summary>
/// Opens PGP/MIME (RFC 3156) and inline OpenPGP (RFC 9580 §6, §7) mail: verifies signatures, decrypts, and reports every layer in a structured result.
/// OneCrypto does the OpenPGP work; this type finds the layers, supplies the bytes that were signed and says what the signature does not cover.
/// </summary>
public static class PgpMimeProcessor
{
    private enum Shape
    {
        None,
        Signed,
        Encrypted,
        Inline,
    }

    private static readonly byte[] BeginCleartext = "-----BEGIN PGP SIGNED MESSAGE-----"u8.ToArray();
    private static readonly byte[] BeginMessage = "-----BEGIN PGP MESSAGE-----"u8.ToArray();

    /// <summary>
    /// Examines <paramref name="message"/>. PGP/MIME entities and text bodies holding armored OpenPGP are unwrapped one inside another up to
    /// <see cref="PgpOpenOptions.MaxDepth"/>. If the root is a multipart wrapper that is not OpenPGP (for example <c>multipart/mixed</c>), the first OpenPGP entity
    /// below it is processed and <see cref="PgpOutside.UnprotectedParts"/> lists the rest. A message without OpenPGP gives an empty result.
    /// Never throws for message content; every failure is a layer status.
    /// </summary>
    /// <param name="message">The message or part, as parsed by <see cref="MimeEntity.Parse"/>.</param>
    /// <param name="options">Keys, time, policy and limits.</param>
    /// <returns>The layers outermost first, the content reached and what lies outside the protection.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static PgpResult Open(MimeEntity message, PgpOpenOptions options)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(options);
        var result = new PgpResult(message);
        var layers = new List<PgpLayer>();
        var unprotectedParts = new List<string>();
        List<PgpIssue> resultIssues = [];
        MimeEntity? start = message;
        if (Classify(message, options) == Shape.None)
        {
            start = FindNested(message, options, unprotectedParts, "");
            if (start is not null)
            {
                resultIssues.Add(PgpIssue.ProtectedPartInUnprotectedWrapper);
            }
        }

        if (start is null)
        {
            Scan(result, options);
            return result;
        }

        MimeEntity current = start;
        MimeEntity senderSource = message;
        for (int depth = 0; ; depth++)
        {
            Shape shape = Classify(current, options);
            if (shape == Shape.None)
            {
                break;
            }

            if (depth >= options.MaxDepth)
            {
                resultIssues.Add(PgpIssue.DepthLimitReached);
                break;
            }

            PgpLayer layer;
            MimeEntity? next;
            switch (shape)
            {
                case Shape.Signed:
                    layer = VerifyDetached((MimeMultipart)current, options, senderSource, out next);
                    break;
                case Shape.Encrypted:
                    layer = Decrypt((MimeMultipart)current, options, senderSource, out next);
                    break;
                default:
                    layer = OpenInline((MimePart)current, options, senderSource, out next);
                    break;
            }

            layer.Entity = current;
            layer.Content = next;
            layers.Add(layer);
            if (next is null)
            {
                break;
            }

            current = next;
            result.Content = next;
            if (!HasSender(senderSource) && HasSender(next))
            {
                senderSource = next;
            }
        }

        result.Layers = layers;
        result.Issues = resultIssues;
        if (layers.Count > 0)
        {
            // Headers repeated inside the protection are trusted only when the message is authenticated: an encrypted message with no
            // signature can be made by anyone who holds the recipient's public key, so its inner headers prove nothing.
            List<string> protectedHeaders = result.IsAuthenticated ? ProtectedHeaderNames(result.Content, layers) : [];
            result.Outside = new PgpOutside(
                message.Headers.Where(static h => !IsContentHeader(h.Name)).Select(static h => h.Name).ToList(),
                unprotectedParts,
                protectedHeaders);
            if (!ReferenceEquals(result.Content, message))
            {
                result.InnerItem = MakeItem(result.Content, message, protectedHeaders);
            }

            if (protectedHeaders.Count > 0)
            {
                layers[^1].Issues = [.. layers[^1].Issues, PgpIssue.ProtectedHeadersPresent];
                CheckRecipients(result, protectedHeaders, options, resultIssues);
                result.Issues = resultIssues;
            }
            else if (layers.Any(static l => l.Succeeded))
            {
                layers[0].Issues = [.. layers[0].Issues, PgpIssue.HeadersNotProtected];
            }
        }

        Scan(result, options);
        return result;
    }

    /// <summary>Parses <paramref name="message"/> and processes it as <see cref="Open(MimeEntity, PgpOpenOptions)"/> does.</summary>
    /// <param name="message">The message bytes.</param>
    /// <param name="options">Keys, time, policy and limits.</param>
    /// <returns>The result.</returns>
    public static PgpResult Open(ReadOnlyMemory<byte> message, PgpOpenOptions options) => Open(MimeEntity.Parse(message), options);

    // ---- structure ----

    private static Shape Classify(MimeEntity e, PgpOpenOptions options)
    {
        string type = e.MediaType;
        if (e is MimeMultipart mp)
        {
            if (type == "multipart/signed")
            {
                string? protocol = mp.GetContentTypeParameter("protocol")?.ToLowerInvariant();
                if (protocol == "application/pgp-signature" || (protocol is null && mp.Children.Count == 2 && mp.Children[1].MediaType == "application/pgp-signature"))
                {
                    return Shape.Signed;
                }

                // Some clients write a different protocol token but still use the pgp-signature part.
                return protocol is not null && !protocol.Contains("pkcs7", StringComparison.Ordinal) && mp.Children.Count == 2
                    && mp.Children[1].MediaType == "application/pgp-signature" ? Shape.Signed : Shape.None;
            }

            if (type == "multipart/encrypted")
            {
                string? protocol = mp.GetContentTypeParameter("protocol")?.ToLowerInvariant();
                return protocol == "application/pgp-encrypted" || (mp.Children.Count >= 1 && mp.Children[0].MediaType == "application/pgp-encrypted")
                    ? Shape.Encrypted : Shape.None;
            }

            return Shape.None;
        }

        if (e is MimePart part && (part.MediaTopLevelType == "text" || type == "application/pgp")
            && part.RawContentLength > 0 && part.RawContentLength <= options.MaxInlineBytes * 2L)
        {
            byte[] body = SafeDecode(part);
            bool html = part.MediaSubType == "html";
            // An HTML part is inline OpenPGP only when an armor line stands on its own line in the markup; an armored block quoted
            // or shown inside ordinary HTML is not the message's own protection.
            return (body.AsSpan().IndexOf(BeginCleartext) >= 0 || body.AsSpan().IndexOf(BeginMessage) >= 0) && (!html || HasArmorLine(body)) ? Shape.Inline : Shape.None;
        }

        return Shape.None;
    }

    private static bool HasArmorLine(byte[] body)
    {
        foreach (byte[] marker in new[] { BeginCleartext, BeginMessage })
        {
            ReadOnlySpan<byte> span = body;
            int from = 0;
            while (from < span.Length)
            {
                int at = span[from..].IndexOf(marker);
                if (at < 0)
                {
                    break;
                }

                at += from;
                bool lineStart = at == 0 || span[at - 1] == (byte)'\n';
                if (lineStart)
                {
                    return true;
                }

                from = at + marker.Length;
            }
        }

        return false;
    }

    private static MimeEntity? FindNested(MimeEntity e, PgpOpenOptions options, List<string> unprotectedParts, string path)
    {
        if (e is MimeMultipart mp)
        {
            MimeEntity? found = null;
            for (int i = 0; i < mp.Children.Count; i++)
            {
                MimeEntity child = mp.Children[i];
                string childPath = path.Length == 0 ? (i + 1).ToString() : path + "." + (i + 1);
                if (found is null)
                {
                    if (Classify(child, options) != Shape.None)
                    {
                        found = child;
                        continue;
                    }

                    found = FindNested(child, options, unprotectedParts, childPath);
                    if (found is not null)
                    {
                        continue;
                    }
                }

                unprotectedParts.Add(childPath);
            }

            return found;
        }

        return null;
    }

    private static bool IsContentHeader(string name) =>
        name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) || name.Equals("MIME-Version", StringComparison.OrdinalIgnoreCase);

    private static bool HasSender(MimeEntity e) => e.Headers.GetFirst("From") is not null || e.Headers.GetFirst("Sender") is not null;

    private static byte[] SafeDecode(MimePart part)
    {
        try
        {
            return part.GetDecodedContent();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return [];
        }
    }

    // ---- multipart/signed ----

    private static PgpLayer VerifyDetached(MimeMultipart mp, PgpOpenOptions options, MimeEntity senderSource, out MimeEntity? next)
    {
        var layer = new PgpLayer(PgpLayerKind.SignedDetached) { Micalg = mp.GetContentTypeParameter("micalg") };
        var issues = new List<PgpIssue>();
        next = null;
        string? protocol = mp.GetContentTypeParameter("protocol")?.ToLowerInvariant();
        if (protocol != "application/pgp-signature")
        {
            issues.Add(PgpIssue.ProtocolParameterUnexpected);
        }

        if (layer.Micalg is null)
        {
            issues.Add(PgpIssue.MicalgMissing);
        }

        // RFC 3156 §5: exactly two body parts. More than two is how unsigned content gets placed next to a valid signature.
        if (mp.Children.Count != 2)
        {
            return Fail(layer, issues, PgpStatus.StructureInvalid, "A multipart/signed has " + mp.Children.Count + " parts; RFC 3156 requires exactly two.");
        }

        MimeEntity signedPart = mp.Children[0];
        next = signedPart;
        if (mp.Children[1] is not MimePart sigPart)
        {
            return Fail(layer, issues, PgpStatus.StructureInvalid, "The second part of a multipart/signed is not a leaf part.");
        }

        if (sigPart.MediaType != "application/pgp-signature")
        {
            issues.Add(PgpIssue.SignaturePartTypeUnexpected);
        }

        byte[] signature;
        try
        {
            signature = sigPart.GetDecodedContent();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return Fail(layer, issues, PgpStatus.Malformed, "The signature part could not be decoded: " + e.GetType().Name);
        }

        byte[] covered = signedPart.ToArray();
        layer.CoveredBytes = covered;
        if (!signedPart.IsModified)
        {
            layer.CoveredSourceOffset = signedPart.SourceStart;
            layer.CoveredSourceLength = signedPart.SourceLength;
        }

        PgpReadResult? report = TryVerifyDetached(covered, signature, options, out PgpStatus failure, out string? why);
        if (report is null)
        {
            return Fail(layer, issues, failure, why!);
        }

        if (!report.HasValidSignature && report.Signatures.Any(static s => s.Status == PgpSignatureStatus.Invalid))
        {
            // RFC 3156 §5 signs the CRLF form; a store that kept LF ends changed the bytes, not the content.
            byte[]? crlf = SmimeContent.NormaliseLineEnds(covered);
            byte[] attempt = crlf ?? covered;
            PgpReadResult? retry = crlf is null ? null : TryVerifyDetached(crlf, signature, options, out _, out _);
            PgpIssue repair = PgpIssue.SignedPartLineEndingsNormalised;
            if (retry is null || !retry.HasValidSignature)
            {
                // RFC 3156 §3: senders strip trailing white space; a signer that did not, and a relay that did, differ only there.
                byte[] stripped = StripTrailingWhitespace(attempt);
                retry = stripped.Length == attempt.Length ? null : TryVerifyDetached(stripped, signature, options, out _, out _);
                attempt = stripped;
                repair = PgpIssue.SignedPartTrailingWhitespaceStripped;
            }

            if (retry is not null && retry.HasValidSignature)
            {
                report = retry;
                layer.CoveredBytes = attempt;
                issues.Add(repair);
            }
        }

        layer.Signers = MakeSigners(report, senderSource, options.Now);
        foreach (PgpSignerResult s in layer.Signers)
        {
            if (s.WeakAlgorithms.Count > 0 && !issues.Contains(PgpIssue.WeakAlgorithms))
            {
                issues.Add(PgpIssue.WeakAlgorithms);
            }
        }

        PgpStatus status = Judge(layer.Signers);
        if (status == PgpStatus.Valid && layer.Micalg is not null && !MicalgMatches(layer.Micalg, layer.Signers))
        {
            status = PgpStatus.MicalgMismatch;
            layer.Detail = "The micalg parameter '" + layer.Micalg + "' does not name the hash of the signature.";
        }

        layer.Status = status;
        layer.Succeeded = status == PgpStatus.Valid;
        layer.Detail ??= layer.Succeeded ? null : DetailFor(status, layer.Signers);
        layer.Issues = issues;
        return layer;
    }

    private static PgpReadResult? TryVerifyDetached(byte[] data, byte[] signature, PgpOpenOptions options, out PgpStatus failure, out string? why)
    {
        failure = PgpStatus.Malformed;
        why = null;
        try
        {
            return PgpMessageReader.VerifyDetached(new MemoryStream(data, writable: false), new MemoryStream(signature, writable: false), ReadOptions(options));
        }
        catch (PgpException e)
        {
            failure = e.Failure == PgpFailure.Unsupported ? PgpStatus.Unsupported : e.Failure == PgpFailure.LimitExceeded ? PgpStatus.LimitExceeded : PgpStatus.Malformed;
            why = "The signature part is not usable: " + e.Message;
        }
        catch (OpenPgpParseException e)
        {
            why = "The signature part is not a valid OpenPGP signature: " + e.Message;
        }

        return null;
    }

    private static byte[] StripTrailingWhitespace(byte[] data)
    {
        var o = new List<byte>(data.Length);
        int lineStart = 0;
        for (int i = 0; i <= data.Length; i++)
        {
            if (i == data.Length || data[i] == (byte)'\r' || data[i] == (byte)'\n')
            {
                int end = i;
                while (end > lineStart && (data[end - 1] == (byte)' ' || data[end - 1] == (byte)'\t'))
                {
                    end--;
                }

                for (int k = lineStart; k < end; k++)
                {
                    o.Add(data[k]);
                }

                if (i < data.Length)
                {
                    o.Add(data[i]);
                }

                lineStart = i + 1;
            }
        }

        return o.ToArray();
    }

    private static bool MicalgMatches(string micalg, IReadOnlyList<PgpSignerResult> signers)
    {
        string[] declared = micalg.ToLowerInvariant().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return signers.All(s => declared.Contains(PgpCanonical.MicalgFor(s.Report.Signature.Hash), StringComparer.Ordinal));
    }

    // ---- multipart/encrypted ----

    private static PgpLayer Decrypt(MimeMultipart mp, PgpOpenOptions options, MimeEntity senderSource, out MimeEntity? next)
    {
        var layer = new PgpLayer(PgpLayerKind.Encrypted);
        var issues = new List<PgpIssue>();
        next = null;
        string? protocol = mp.GetContentTypeParameter("protocol")?.ToLowerInvariant();
        if (protocol != "application/pgp-encrypted")
        {
            issues.Add(PgpIssue.ProtocolParameterUnexpected);
        }

        // RFC 3156 §4: exactly two parts, version control then data.
        if (mp.Children.Count != 2)
        {
            return Fail(layer, issues, PgpStatus.StructureInvalid, "A multipart/encrypted has " + mp.Children.Count + " parts; RFC 3156 requires exactly two.");
        }

        if (mp.Children[1] is not MimePart dataPart)
        {
            return Fail(layer, issues, PgpStatus.StructureInvalid, "The second part of a multipart/encrypted is not a leaf part.");
        }

        if (mp.Children[0] is not MimePart control || control.MediaType != "application/pgp-encrypted"
            || !System.Text.Encoding.ASCII.GetString(SafeDecode(control)).Contains("Version: 1", StringComparison.Ordinal))
        {
            issues.Add(PgpIssue.ControlPartUnexpected);
        }

        if (dataPart.MediaType != "application/octet-stream")
        {
            issues.Add(PgpIssue.SignaturePartTypeUnexpected);
        }

        byte[] data;
        try
        {
            data = dataPart.GetDecodedContent();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return Fail(layer, issues, PgpStatus.Malformed, "The data part could not be decoded: " + e.GetType().Name);
        }

        byte[]? plain = DecryptBytes(data, options, layer, issues, senderSource, mp, out PgpReadResult? report);
        if (plain is null)
        {
            layer.Issues = issues;
            return layer;
        }

        next = MimeEntity.Parse(plain);
        return Finish(layer, report!, issues, senderSource, options.Now);
    }

    /// <summary>Runs OneCrypto's reader and maps its failures to a layer status; null (with the layer filled in) on failure.</summary>
    private static byte[]? DecryptBytes(byte[] armoredOrBinary, PgpOpenOptions options, PgpLayer layer, List<PgpIssue> issues, MimeEntity senderSource, MimeEntity root, out PgpReadResult? report)
    {
        report = null;
        var output = new CappedStream(options.MaxPlaintextBytes);
        try
        {
            report = PgpMessageReader.Read(new MemoryStream(armoredOrBinary, writable: false), output, ReadOptions(options, decrypt: true));
        }
        catch (PgpException e)
        {
            // RFC 9580 §13: one status for every reason decryption fails, so the status does not become an oracle.
            layer.Status = e.Failure switch
            {
                PgpFailure.NoDecryptionKey => PgpStatus.NoDecryptionKey,
                PgpFailure.UnauthenticatedEncryption => PgpStatus.UnauthenticatedEncryption,
                PgpFailure.LimitExceeded => PgpStatus.LimitExceeded,
                PgpFailure.Unsupported => PgpStatus.Unsupported,
                PgpFailure.MalformedMessage => PgpStatus.Malformed,
                PgpFailure.PolicyViolation => PgpStatus.PolicyRejected,
                _ => PgpStatus.DecryptionFailed,
            };
            layer.Detail = layer.Status == PgpStatus.DecryptionFailed ? "The message could not be decrypted." : e.Message;
            return null;
        }
        catch (OpenPgpParseException e)
        {
            layer.Status = PgpStatus.Malformed;
            layer.Detail = "The OpenPGP data is malformed: " + e.Message;
            return null;
        }
        catch (CappedStream.LimitHit)
        {
            layer.Status = PgpStatus.LimitExceeded;
            layer.Detail = "The decrypted data is larger than the limit of " + options.MaxPlaintextBytes + " bytes.";
            return null;
        }

        return output.ToArray();
    }

    /// <summary>Common ending of an encrypted layer: signatures found inside, protection, issues.</summary>
    private static PgpLayer Finish(PgpLayer layer, PgpReadResult report, List<PgpIssue> issues, MimeEntity senderSource, DateTimeOffset now)
    {
        layer.Protection = report.Protection;
        layer.Signers = MakeSigners(report, senderSource, now);
        if (report.Protection == PgpProtection.None)
        {
            issues.Add(PgpIssue.UnauthenticatedEncryption);
        }

        if (layer.Signers.Count == 0)
        {
            issues.Add(PgpIssue.EncryptedWithoutSignature);
        }

        if (layer.Signers.Any(static s => s.WeakAlgorithms.Count > 0))
        {
            issues.Add(PgpIssue.WeakAlgorithms);
        }

        PgpStatus sig = layer.Signers.Count == 0 ? PgpStatus.Valid : Judge(layer.Signers);
        layer.Status = sig == PgpStatus.Valid ? PgpStatus.Decrypted : sig;
        layer.Succeeded = layer.Status == PgpStatus.Decrypted;
        layer.Detail = layer.Succeeded ? null : DetailFor(sig, layer.Signers);
        layer.Issues = issues;
        return layer;
    }

    // ---- inline ----

    private static PgpLayer OpenInline(MimePart part, PgpOpenOptions options, MimeEntity senderSource, out MimeEntity? next)
    {
        next = null;
        byte[] body = SafeDecode(part);
        var issues = new List<PgpIssue>();
        int cs = body.AsSpan().IndexOf(BeginCleartext);
        int msg = body.AsSpan().IndexOf(BeginMessage);
        bool cleartext = cs >= 0 && (msg < 0 || cs < msg);
        var layer = new PgpLayer(cleartext ? PgpLayerKind.InlineCleartextSigned : PgpLayerKind.InlineMessage);
        string charset = part.GetContentTypeParameter("charset") ?? "us-ascii";

        PgpReadResult? report = null;
        byte[]? text = null;
        // Try the text as received, then each repair in turn; the first reading that verifies or decrypts wins.
        foreach (Repair repair in new[] { Repair.None, Repair.QuotedPrintable, Repair.Charset })
        {
            byte[] candidate = body;
            var attemptIssues = new List<PgpIssue>();
            if (repair == Repair.QuotedPrintable)
            {
                if (!LooksQuotedPrintable(body))
                {
                    continue;
                }

                candidate = DecodeQuotedPrintable(body);
                attemptIssues.Add(PgpIssue.InlineQuotedPrintableRepaired);
            }

            if (repair == Repair.Charset)
            {
                if (!cleartext || IsUtf8Name(charset) || charset.Equals("us-ascii", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!TryReencode(body, charset, out candidate))
                {
                    continue;
                }

                attemptIssues.Add(PgpIssue.InlineCharsetReencoded);
            }

            byte[] block = ExtractBlock(candidate, cleartext, attemptIssues);
            if (block.Length == 0)
            {
                continue;
            }

            var output = new CappedStream(options.MaxPlaintextBytes);
            try
            {
                report = cleartext
                    ? PgpMessageReader.VerifyCleartext(new MemoryStream(block, writable: false), output, ReadOptions(options))
                    : PgpMessageReader.Read(new MemoryStream(block, writable: false), output, ReadOptions(options, decrypt: true));
            }
            catch (PgpException e)
            {
                layer.Status = cleartext ? PgpStatus.Malformed : e.Failure switch
                {
                    PgpFailure.NoDecryptionKey => PgpStatus.NoDecryptionKey,
                    PgpFailure.UnauthenticatedEncryption => PgpStatus.UnauthenticatedEncryption,
                    PgpFailure.LimitExceeded => PgpStatus.LimitExceeded,
                    PgpFailure.Unsupported => PgpStatus.Unsupported,
                    PgpFailure.MalformedMessage => PgpStatus.Malformed,
                    PgpFailure.PolicyViolation => PgpStatus.PolicyRejected,
                    _ => PgpStatus.DecryptionFailed,
                };
                layer.Detail = layer.Status == PgpStatus.DecryptionFailed ? "The message could not be decrypted." : e.Message;
                continue;
            }
            catch (OpenPgpParseException e)
            {
                layer.Status = PgpStatus.Malformed;
                layer.Detail = "The OpenPGP data is malformed: " + e.Message;
                continue;
            }
            catch (CappedStream.LimitHit)
            {
                layer.Status = PgpStatus.LimitExceeded;
                layer.Detail = "The decrypted data is larger than the limit of " + options.MaxPlaintextBytes + " bytes.";
                break;
            }

            text = output.ToArray();
            issues.AddRange(attemptIssues);
            if (cleartext && !report.HasValidSignature && report.Signatures.Any(static s => s.Status == PgpSignatureStatus.Invalid))
            {
                // Keep this reading as the result unless a repair verifies.
                continue;
            }

            break;
        }

        if (report is null || text is null)
        {
            if (layer.Detail is null)
            {
                layer.Status = PgpStatus.Malformed;
                layer.Detail = "No complete armored OpenPGP block was found in the text.";
            }

            layer.Issues = issues;
            return layer;
        }

        if (cleartext)
        {
            layer.Protection = PgpProtection.NotEncrypted;
            layer.Signers = MakeSigners(report, senderSource, options.Now);
            PgpStatus status = Judge(layer.Signers);
            layer.Status = status;
            layer.Succeeded = status == PgpStatus.Valid;
            layer.Detail = layer.Succeeded ? null : DetailFor(status, layer.Signers);
            if (layer.Signers.Any(static s => s.WeakAlgorithms.Count > 0))
            {
                issues.Add(PgpIssue.WeakAlgorithms);
            }

            layer.Issues = issues;
        }
        else
        {
            layer = Finish(layer, report, issues, senderSource, options.Now);
        }

        bool htmlPart = part.MediaSubType == "html";
        if (htmlPart)
        {
            layer.Issues = [.. layer.Issues, PgpIssue.InlineHtmlPart];
        }

        next = TextPart(text, charset, htmlPart);
        return layer;
    }

    private enum Repair
    {
        None,
        QuotedPrintable,
        Charset,
    }

    private static MimePart TextPart(byte[] text, string declaredCharset, bool html = false)
    {
        string type = html ? "text/html" : "text/plain";
        var part = new MimePart(type);
        string charset = IsValidUtf8(text) ? "utf-8" : declaredCharset;
        part.Headers.Set("Content-Type", type + "; charset=" + charset);
        part.SetDecodedContent(text, ContentTransferEncoding.QuotedPrintable);
        return part;
    }

    private static bool IsValidUtf8(byte[] data)
    {
        try
        {
            _ = new UTF8Encoding(false, true).GetCharCount(data);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsUtf8Name(string charset) => charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase) || charset.Equals("utf8", StringComparison.OrdinalIgnoreCase);

    private static bool TryReencode(byte[] body, string charset, out byte[] utf8)
    {
        utf8 = body;
        try
        {
            MailEncoding? enc = MailEncoding.ForLabel(charset);
            if (enc is null)
            {
                return false;
            }

            utf8 = System.Text.Encoding.UTF8.GetBytes(enc.GetString(body));
            return !utf8.AsSpan().SequenceEqual(body);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool LooksQuotedPrintable(byte[] body)
    {
        // Escapes such as =3D or =20 and soft line breaks (=CRLF) in armored text are not Base64: the armor alphabet has '=' only as padding and before the CRC.
        for (int i = 0; i + 2 < body.Length; i++)
        {
            if (body[i] == (byte)'=' && (IsHex(body[i + 1]) && IsHex(body[i + 2]) || body[i + 1] == (byte)'\r' || body[i + 1] == (byte)'\n'))
            {
                // "=ABCD" CRC lines are four Base64 characters; require an escape that is not at a line start.
                if (i > 0 && body[i - 1] != (byte)'\n')
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsHex(byte b) => b is >= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'F' or >= (byte)'a' and <= (byte)'f';

    private static byte[] DecodeQuotedPrintable(byte[] body)
    {
        using var input = new MemoryStream(body, writable: false);
        using var dec = new TransferDecodingStream(input, ContentTransferEncoding.QuotedPrintable, leaveOpen: false);
        using var ms = new MemoryStream();
        dec.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Cuts the first armored block out of the text, tolerating a uniform quote prefix; raises the matching issues.</summary>
    private static byte[] ExtractBlock(byte[] text, bool cleartext, List<PgpIssue> issues)
    {
        string s = Encoding.Latin1.GetString(text);
        string begin = cleartext ? "-----BEGIN PGP SIGNED MESSAGE-----" : "-----BEGIN PGP MESSAGE-----";
        string endMarker = cleartext ? "-----END PGP SIGNATURE-----" : "-----END PGP MESSAGE-----";
        int b = s.IndexOf(begin, StringComparison.Ordinal);
        if (b < 0)
        {
            return [];
        }

        int e = s.IndexOf(endMarker, b, StringComparison.Ordinal);
        if (e < 0)
        {
            return [];
        }

        int lineStart = s.LastIndexOf('\n', b) + 1;
        int lineEnd = s.IndexOf('\n', e);
        lineEnd = lineEnd < 0 ? s.Length : lineEnd + 1;
        string prefix = s[lineStart..b];
        string block = s[lineStart..lineEnd];
        if (prefix.Length > 0 && prefix.All(static c => c is '>' or ' ' or '|'))
        {
            // A reply quoted the whole block; every line carries the same prefix.
            string[] lines = block.Split('\n');
            block = string.Join('\n', lines.Select(l => l.StartsWith(prefix, StringComparison.Ordinal) ? l[prefix.Length..] : l.TrimStart('>', ' ', '|')));
            issues.Add(PgpIssue.InlineQuotePrefixRemoved);
        }
        else if (prefix.Trim().Length > 0)
        {
            block = s[b..lineEnd];
        }

        if (s[..lineStart].Trim().Length > 0 || s[lineEnd..].Trim().Length > 0 || prefix.Trim().Length > 0 && prefix.Any(static c => c is not ('>' or ' ' or '|')))
        {
            issues.Add(PgpIssue.InlineTextOutsideArmor);
        }

        return Encoding.Latin1.GetBytes(block);
    }

    // ---- results ----

    private static PgpReadOptions ReadOptions(PgpOpenOptions o, bool decrypt = false) => new()
    {
        Now = o.Now,
        Certificates = [.. o.Certificates, .. o.SecretKeys],
        SecretKeys = decrypt || o.SecretKeys.Count > 0 ? o.SecretKeys : [],
        KeyPasswords = o.KeyPasswords,
        MessagePasswords = o.MessagePasswords,
        Policy = o.Policy,
        AllowUnauthenticatedEncryption = o.AllowUnauthenticatedEncryption,
        MaxNesting = o.MaxDepth,
        Limits = o.Limits,
    };

    private static List<PgpSignerResult> MakeSigners(PgpReadResult report, MimeEntity senderSource, DateTimeOffset now)
    {
        List<string> senders = Senders(senderSource);
        var list = new List<PgpSignerResult>();
        foreach (PgpSignatureResult r in report.Signatures)
        {
            var s = new PgpSignerResult(r) { HashAlgorithm = PgpCanonical.MicalgFor(r.Signature.Hash) };
            if (r.Signer is { } key)
            {
                List<string> uids = key.Certificate.UserIds.ToList();
                s.UserIds = uids;
                // A User ID counts only when the key itself certified it (RFC 9580 §5.2.1.3) and had not revoked it by the time of signing:
                // anyone can add an uncertified User ID packet to a public certificate, and a key server or a copy in transit will carry it.
                DateTimeOffset at = r.CreatedAt ?? now;
                List<string> bound = uids.Where(u => PgpUserIds.HasSelfSignature(key.Certificate, u, at)).ToList();
                bool Names(string address) => bound.Any(u => UserIdAddress(u).Equals(address, StringComparison.OrdinalIgnoreCase));
                // Every address shown as the sender must be named: a second mailbox or a second From header is another way to show a different sender.
                s.UserIdMatchesSender = senders.Count > 0 && senders.All(Names);
                s.UserId = bound.FirstOrDefault(u => senders.Any(a => UserIdAddress(u).Equals(a, StringComparison.OrdinalIgnoreCase))) ?? bound.FirstOrDefault() ?? uids.FirstOrDefault();
                s.KeyValidAtSigning = r.CreatedAt is { } created ? key.StateAt(created).IsValid : null;
            }

            if (r.Policy is { Verdict: PolicyVerdict.Flagged } d)
            {
                s.WeakAlgorithms = [d.Algorithm];
            }

            list.Add(s);
        }

        return list;
    }

    /// <summary>The address in a user ID such as <c>Name (comment) &lt;a@b&gt;</c>, or the whole text when it has no angle brackets.</summary>
    internal static string UserIdAddress(string userId)
    {
        int lt = userId.LastIndexOf('<');
        int gt = userId.LastIndexOf('>');
        return lt >= 0 && gt > lt ? userId[(lt + 1)..gt].Trim() : userId.Trim();
    }

    private static PgpStatus Judge(IReadOnlyList<PgpSignerResult> signers)
    {
        if (signers.Count == 0)
        {
            return PgpStatus.NoSignature;
        }

        // Tampering and policy failures decide; a co-signer whose key we lack does not spoil a valid signature.
        if (signers.Any(static s => s.Status == PgpSignatureStatus.Invalid))
        {
            return PgpStatus.SignatureInvalid;
        }

        if (signers.Any(static s => s.Status == PgpSignatureStatus.Rejected))
        {
            return PgpStatus.PolicyRejected;
        }

        if (signers.Any(static s => s.Status == PgpSignatureStatus.KeyNotValid))
        {
            return PgpStatus.KeyNotValid;
        }

        if (signers.Any(static s => s.IsValid))
        {
            return PgpStatus.Valid;
        }

        return signers.Any(static s => s.Status == PgpSignatureStatus.Unsupported) ? PgpStatus.Unsupported : PgpStatus.SignerUnknown;
    }

    private static string DetailFor(PgpStatus status, IReadOnlyList<PgpSignerResult> signers)
    {
        string reasons = string.Join(" ", signers.SelectMany(static s => s.Reasons).Distinct(StringComparer.Ordinal));
        string head = status switch
        {
            PgpStatus.SignatureInvalid => "The signature does not match the signed bytes.",
            PgpStatus.SignerUnknown => "No supplied certificate carries the signing key.",
            PgpStatus.KeyNotValid => "The signing key was not valid when the signature was made.",
            PgpStatus.PolicyRejected => "The crypto policy rejects the signature.",
            PgpStatus.Unsupported => "The signature uses an algorithm that cannot be verified here.",
            PgpStatus.NoSignature => "The layer carries no signature.",
            _ => "The layer failed.",
        };
        return reasons.Length == 0 ? head : head + " " + reasons;
    }

    private static PgpLayer Fail(PgpLayer layer, List<PgpIssue> issues, PgpStatus status, string detail)
    {
        layer.Status = status;
        layer.Succeeded = false;
        layer.Detail = detail;
        layer.Issues = issues;
        return layer;
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

    // ---- surreptitious forwarding ----

    /// <summary>
    /// draft-ietf-lamps-header-protection §4: the protected <c>To</c>/<c>Cc</c> of a signed and encrypted message must name the recipient who reads it;
    /// otherwise someone may have decrypted it and re-encrypted the signed content to a third party.
    /// </summary>
    private static void CheckRecipients(PgpResult result, List<string> protectedHeaders, PgpOpenOptions options, List<PgpIssue> resultIssues)
    {
        if (!result.WasEncrypted)
        {
            return;
        }

        var mine = new List<string>(options.RecipientAddresses);
        if (mine.Count == 0)
        {
            foreach (PgpCertificate cert in options.SecretKeys)
            {
                foreach (string uid in cert.UserIds)
                {
                    if (PgpUserIds.HasSelfSignature(cert, uid, options.Now))
                    {
                        mine.Add(UserIdAddress(uid));
                    }
                }
            }
        }

        if (mine.Count == 0)
        {
            return;
        }

        MimeEntity? source = ProtectedHeaderSource(result.Content);
        var named = new List<string>();
        if (source is not null)
        {
            foreach (string field in new[] { "To", "Cc" })
            {
                if (!protectedHeaders.Contains(field, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (MimeHeader h in source.Headers.GetAll(field))
                {
                    foreach (MimeMailbox m in AddressParser.Flatten(AddressParser.Parse(h.RawValue.Span)))
                    {
                        named.Add(m.UnquotedAddress);
                    }
                }
            }
        }

        if (named.Count == 0)
        {
            resultIssues.Add(PgpIssue.ProtectedRecipientsMissing);
        }
        else if (!named.Any(a => mine.Contains(a, StringComparer.OrdinalIgnoreCase)))
        {
            resultIssues.Add(PgpIssue.ProtectedRecipientMismatch);
        }
    }

    private static MimeEntity? ProtectedHeaderSource(MimeEntity content) =>
        content is MimeMessagePart { Message: { } inner } ? inner : content.GetContentTypeParameter("protected-headers") is not null ? content : null;

    // ---- attachments ----

    private const int MaxScannedParts = 3000;
    private const int MaxAttachedKeys = 32;
    private const int MaxKeyPartBytes = 1 << 20;
    private const int MaxSniffedAttachmentBytes = 8 << 20;

    /// <summary>Finds attached public keys and attachments that hold OpenPGP data, in the outer message and in the protected content.</summary>
    private static void Scan(PgpResult result, PgpOpenOptions options)
    {
        var keys = new List<PgpAttachedKey>();
        var attachments = new List<PgpAttachmentRef>();
        var seen = new HashSet<MimeEntity>(ReferenceEqualityComparer.Instance);
        int budget = MaxScannedParts;
        var protectedRoots = new HashSet<MimeEntity>(ReferenceEqualityComparer.Instance);
        foreach (PgpLayer l in result.Layers)
        {
            if (l.Content is not null)
            {
                protectedRoots.Add(l.Content);
            }
        }

        bool limit = false;
        ScanEntity(result.Root, "", false);
        if (!ReferenceEquals(result.Content, result.Root) && !seen.Contains(result.Content))
        {
            ScanEntity(result.Content, "protected:", true);
        }

        if (limit)
        {
            result.Issues = [.. result.Issues, PgpIssue.AttachmentLimitReached];
        }

        result.AttachedKeys = keys;
        result.OpenPgpAttachments = attachments;

        void ScanEntity(MimeEntity e, string path, bool inProtected)
        {
            if (!seen.Add(e))
            {
                return;
            }

            if (budget-- <= 0)
            {
                limit = true;
                return;
            }

            inProtected |= protectedRoots.Contains(e);
            switch (e)
            {
                case MimeMultipart mp:
                    if (IsPgpStructure(mp))
                    {
                        return;
                    }

                    for (int i = 0; i < mp.Children.Count; i++)
                    {
                        string child = path.Length == 0 || path == "protected:" ? path + (i + 1) : path + "." + (i + 1);
                        ScanEntity(mp.Children[i], child, inProtected);
                    }

                    break;
                case MimeMessagePart { Message: { } inner }:
                    ScanEntity(inner, path, inProtected);
                    break;
                case MimePart part:
                    Classify(part, path.Length == 0 || path == "protected:" ? path + "0" : path, inProtected);
                    break;
            }
        }

        void Classify(MimePart part, string at, bool inProtected)
        {
            string type = part.MediaType;
            string? name = part.FileName;
            bool nameKey = name is not null && (name.EndsWith(".asc", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".pkr", StringComparison.OrdinalIgnoreCase));
            bool nameData = name is not null && (name.EndsWith(".pgp", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".gpg", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".asc", StringComparison.OrdinalIgnoreCase));
            if (type == "application/pgp-signature")
            {
                return;
            }

            if (type == "application/pgp-keys" || (nameKey && type is "application/octet-stream" or "text/plain" or "application/pgp"))
            {
                if (part.RawContentLength > MaxKeyPartBytes * 2L)
                {
                    limit = true;
                    return;
                }

                byte[] body = SafeDecode(part);
                bool block = body.AsSpan().IndexOf("-----BEGIN PGP PUBLIC KEY BLOCK-----"u8) >= 0;
                bool packets = type == "application/pgp-keys" && body.Length > 0 && (body[0] & 0x80) != 0;
                if (!block && !packets)
                {
                    if (type != "application/pgp-keys")
                    {
                        MaybeData(part, at, name, body);
                    }

                    return;
                }

                if (keys.Count >= MaxAttachedKeys || body.Length > MaxKeyPartBytes)
                {
                    limit = true;
                    return;
                }

                IReadOnlyList<PgpCertificate> certs = KeyDiscovery.ReadAll(body, null).Certificates;
                keys.Add(new PgpAttachedKey(at, name, certs, inProtected));
                return;
            }

            if (type is "application/pgp-encrypted" or "application/pgp" or "application/x-pgp-encrypted" || (nameData && type is "application/octet-stream" or "application/x-pgp"))
            {
                if (part.RawContentLength > MaxSniffedAttachmentBytes * 2L)
                {
                    attachments.Add(new PgpAttachmentRef(at, part, name));
                    return;
                }

                MaybeData(part, at, name, SafeDecode(part));
            }
        }

        void MaybeData(MimePart part, string at, string? name, byte[] body)
        {
            if (LooksLikeOpenPgpData(body))
            {
                attachments.Add(new PgpAttachmentRef(at, part, name));
            }
        }
    }

    /// <summary>The control and data parts of a PGP/MIME wrapper are the structure, not attachments.</summary>
    private static bool IsPgpStructure(MimeMultipart mp) =>
        mp.MediaType == "multipart/encrypted" && mp.Children.Count >= 1 && mp.Children[0].MediaType == "application/pgp-encrypted";

    /// <summary>An armored message (header within the first 4 KiB, after any preamble) or a binary packet whose tag is one a message starts with (RFC 9580 §4.2).</summary>
    private static bool LooksLikeOpenPgpData(byte[] body)
    {
        if (body.Length == 0)
        {
            return false;
        }

        if (body.AsSpan(0, Math.Min(body.Length, 4096)).IndexOf(BeginMessage) >= 0)
        {
            return true;
        }

        byte b = body[0];
        if ((b & 0x80) == 0)
        {
            return false;
        }

        // New format (bit 6): tag in bits 0-5. Old format: tag in bits 2-5.
        int tag = (b & 0x40) != 0 ? b & 0x3F : (b >> 2) & 0x0F;
        return tag is 1 or 3 or 4 or 8 or 9 or 18 or 20;
    }

    /// <summary>
    /// Decrypts (or verifies the signature of) an attachment that holds an OpenPGP message: an armored block, or the binary packets of a <c>.pgp</c>/<c>.gpg</c> file.
    /// The plaintext is the attachment's bytes, not MIME. Never throws for content; failures are the layer's status.
    /// </summary>
    /// <param name="part">The attachment part; transfer encoding is undone first.</param>
    /// <param name="options">Keys, time, policy and limits; <see cref="PgpOpenOptions.MaxPlaintextBytes"/> bounds the output.</param>
    /// <returns>The layer and the plaintext.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static PgpAttachmentResult OpenAttachment(MimePart part, PgpOpenOptions options)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(options);
        var layer = new PgpLayer(PgpLayerKind.Encrypted) { Entity = part };
        var issues = new List<PgpIssue>();
        string? name = StripPgpSuffix(part.FileName);
        byte[] data;
        try
        {
            data = part.GetDecodedContent();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            layer.Status = PgpStatus.Malformed;
            layer.Detail = "The attachment could not be decoded: " + e.GetType().Name;
            return new PgpAttachmentResult(layer, default, name);
        }

        if (!LooksLikeOpenPgpData(data))
        {
            layer.Status = PgpStatus.Malformed;
            layer.Detail = "The attachment holds no OpenPGP message.";
            return new PgpAttachmentResult(layer, default, name);
        }

        byte[] input = data;
        if (data.AsSpan().IndexOf(BeginMessage) >= 0)
        {
            input = ExtractBlock(data, cleartext: false, issues);
            if (input.Length == 0)
            {
                layer.Status = PgpStatus.Malformed;
                layer.Detail = "No complete armored OpenPGP block was found in the attachment.";
                layer.Issues = issues;
                return new PgpAttachmentResult(layer, default, name);
            }
        }

        byte[]? plain = DecryptBytes(input, options, layer, issues, part, part, out PgpReadResult? report);
        if (plain is null)
        {
            layer.Issues = issues;
            return new PgpAttachmentResult(layer, default, name);
        }

        Finish(layer, report!, issues, part, options.Now);
        return new PgpAttachmentResult(layer, plain, name);
    }

    private static string? StripPgpSuffix(string? name)
    {
        if (name is null)
        {
            return null;
        }

        foreach (string suffix in new[] { ".pgp", ".gpg", ".asc" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && name.Length > suffix.Length)
            {
                return name[..^suffix.Length];
            }
        }

        return name;
    }

    // ---- protected headers ----

    /// <summary>
    /// Names of the headers the innermost content repeats under the protection (draft-autocrypt-lamps-protected-headers: a top-level entity whose
    /// Content-Type carries <c>protected-headers</c>; or a <c>message/rfc822</c> inside a signed or encrypted layer).
    /// Only counted when some layer authenticates them: signed, or encrypted with integrity protection.
    /// </summary>
    private static List<string> ProtectedHeaderNames(MimeEntity content, List<PgpLayer> layers)
    {
        if (!layers.Any(static l => l.Succeeded))
        {
            return [];
        }

        MimeEntity e = content;
        if (e is MimeMessagePart { Message: { } inner })
        {
            return inner.Headers.Select(static h => h.Name).ToList();
        }

        if (e.GetContentTypeParameter("protected-headers") is null)
        {
            return [];
        }

        return e.Headers.Where(static h => !IsContentHeader(h.Name)).Select(static h => h.Name).ToList();
    }

    private static MimeMailItem? MakeItem(MimeEntity content, MimeEntity outer, List<string> protectedHeaders)
    {
        try
        {
            MimeEntity target = content is MimeMessagePart { Message: { } m } ? m : content;
            if (protectedHeaders.Count > 0 && content is not MimeMessagePart)
            {
                // The protected headers win over the outer, unprotected ones; the outer headers they do not replace stay (From, To, Date, Message-ID …).
                MimeEntity merged = MimeEntity.Parse(content.ToArray());
                foreach (MimeHeader h in outer.Headers.ToArray())
                {
                    if (!IsContentHeader(h.Name) && !protectedHeaders.Contains(h.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        merged.Headers.Add(h.Name, h.Value);
                    }
                }

                if (merged.Headers.GetFirst("MIME-Version") is null)
                {
                    merged.Headers.Insert(0, new MimeHeader("MIME-Version", "1.0"));
                }

                return MimeMailItem.Parse(merged.ToArray());
            }

            if (protectedHeaders.Count > 0)
            {
                return MimeMailItem.Parse(target.ToArray());
            }

            // Nothing inside is vouched for, so the message headers are the outer ones: whatever the content says about From or Subject is not shown as the message's.
            MimeEntity plain = MimeEntity.Parse(target.ToArray());
            foreach (MimeHeader h in plain.Headers.ToArray())
            {
                if (!IsContentHeader(h.Name))
                {
                    plain.Headers.Remove(h);
                }
            }

            foreach (MimeHeader h in outer.Headers.ToArray())
            {
                if (!IsContentHeader(h.Name))
                {
                    plain.Headers.Add(h.Name, h.Value);
                }
            }

            if (plain.Headers.GetFirst("MIME-Version") is null)
            {
                plain.Headers.Insert(0, new MimeHeader("MIME-Version", "1.0"));
            }

            return MimeMailItem.Parse(plain.ToArray());
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    // ---- bounded output ----

    /// <summary>A write-only buffer that stops with <see cref="LimitHit"/> when the output would pass the limit.</summary>
    private sealed class CappedStream : Stream
    {
        private readonly long _max;
        private readonly MemoryStream _inner = new();

        internal CappedStream(long max) => _max = max;

        internal sealed class LimitHit : Exception
        {
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_inner.Length + buffer.Length > _max)
            {
                throw new LimitHit();
            }

            _inner.Write(buffer);
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        internal byte[] ToArray() => _inner.ToArray();
    }
}
