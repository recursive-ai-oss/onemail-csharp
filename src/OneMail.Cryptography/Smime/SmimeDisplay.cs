using System.Net;
using System.Text;
using RecursiveAI.OneMail.Display;
using RecursiveAI.OneMail.Mime;

namespace RecursiveAI.OneMail.Cryptography.Smime;

/// <summary>Display HTML for a message that went through <see cref="SmimeProcessor"/>, with its security status shown above it.</summary>
public static class SmimeDisplay
{
    /// <summary>
    /// Renders the verified or decrypted message as display HTML (the same sanitiser and policy as <see cref="MailItemDisplayExtensions.ToDisplayHtml"/>), with a status block on top.
    /// Only the protected content is shown: a part that sits beside the signed part in an unsigned wrapper is not shown as if it were signed, and the status says it exists.
    /// The header block is the message's own (From, To, Subject, Date …); a signature over <c>multipart/signed</c> covers none of them, so the status says so unless the signed
    /// entity is itself a message whose headers (<see cref="SmimeOutside.ProtectedHeaders"/>) are then shown in place of the outer ones.
    /// </summary>
    /// <param name="result">The result of <see cref="SmimeProcessor.Open(MimeEntity, SmimeOpenOptions?)"/>.</param>
    /// <param name="options">Display choices; null for the defaults. A <see cref="DisplayOptions.StatusBanner"/> of its own is replaced.</param>
    /// <returns>The display result. For a message without S/MIME protection there is no status block and the message is shown as it is.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is null.</exception>
    public static DisplayResult ToDisplayHtml(this SmimeResult result, DisplayOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        options ??= DisplayOptions.Default;
        MimeMailItem item = ItemToShow(result);
        string status = StatusHtml(result);
        DisplayOptions effective = options.WithStatusBanner(status.Length == 0 ? null : _ => status);
        return item.ToDisplayHtml(effective);
    }

    /// <summary>The status block as an HTML fragment (all message-derived text escaped); empty when the message has no S/MIME protection.</summary>
    /// <param name="result">The result.</param>
    /// <returns>The fragment.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is null.</exception>
    public static string StatusHtml(SmimeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.IsSmime)
        {
            return string.Empty;
        }

        var lines = new List<(string Text, bool Bad)>();
        List<SmimeSignedLayer> signedLayers = result.Layers.OfType<SmimeSignedLayer>().ToList();
        bool encrypted = result.Layers.OfType<SmimeContentLayer>().Any(static l => l.Succeeded && l.Kind is SmimeLayerKind.Enveloped or SmimeLayerKind.AuthEnveloped);
        // A signed part beside unsigned ones is reported on its own line below; the signed part itself is judged by its layers.
        bool layersOk = result.Layers.All(static l => l.Succeeded);
        SmimeSignerResult? good = signedLayers.SelectMany(static l => l.Signers).FirstOrDefault(static s => s.IsValid);
        string prefix = encrypted ? "Encrypted and signed" : "Signed";
        if (layersOk && good is not null)
        {
            string who = good.CertificateAddresses.Count > 0 ? string.Join(", ", good.CertificateAddresses) : "an unnamed certificate";
            if (!good.IsTrusted)
            {
                lines.Add((prefix + " with a certificate for " + who + ". The signature is valid, but the certificate was not checked against a trusted root, so the signer is not verified: anyone can sign with a certificate naming any address.", true));
            }
            else if (good.SenderMatch == SmimeSenderMatch.Match)
            {
                lines.Add((prefix + " by " + who + ". The signature is valid and the certificate chains to a trusted root.", false));
            }
            else
            {
                string why = good.SenderMatch == SmimeSenderMatch.Mismatch
                    ? "which does not match the sender shown in From"
                    : "and the message names no sender to compare it with";
                lines.Add((prefix + " with a trusted certificate for " + who + ", " + why + ". The sender is not verified.", true));
            }
        }
        else if (layersOk && encrypted)
        {
            lines.Add(("Encrypted but not signed: nobody vouches for who wrote it.", true));
        }
        else
        {
            SmimeLayer? failed = result.Layers.FirstOrDefault(static l => !l.Succeeded);
            lines.Add(("Not verified: " + (failed?.Detail ?? failed?.Status.ToString() ?? "the protection could not be checked") + ".", true));
        }

        if (result.Issues.Contains(SmimeIssue.SignedPartInUnsignedWrapper))
        {
            string parts = result.Outside is { UnsignedParts.Count: > 0 } o ? " (parts " + string.Join(", ", o.UnsignedParts) + ")" : string.Empty;
            lines.Add(("Other parts of this message are not signed and are not shown here" + parts + ".", true));
        }

        if (result.Layers.Any(static l => l.Issues.Contains(SmimeIssue.UnauthenticatedEncryption)))
        {
            lines.Add(("The encryption does not authenticate the content (CBC mode), so it could have been altered by someone else.", true));
        }

        if (layersOk && signedLayers.Count > 0 && result.Outside is { ProtectedHeaders.Count: 0 })
        {
            lines.Add(("The headers (Subject, From, To, Date) are not covered by the signature.", false));
        }

        List<string> weak = signedLayers.SelectMany(static l => l.Signers).SelectMany(static s => s.WeakAlgorithms).Distinct(StringComparer.Ordinal).ToList();
        if (weak.Count > 0)
        {
            lines.Add(("The signature uses a weak algorithm (" + string.Join(", ", weak) + ").", true));
        }

        if (result.Layers.Any(static l => l.Issues.Contains(SmimeIssue.LegacyCipherAllowed)))
        {
            lines.Add(("The message was encrypted with a legacy cipher that is no longer considered safe.", true));
        }

        var sb = new StringBuilder();
        bool anyBad = lines.Any(static l => l.Bad);
        sb.Append("<div style=\"border:1px solid ").Append(anyBad ? "#c60" : "#2a7").Append(";border-radius:4px;padding:4px 8px;margin-bottom:8px;font-size:90%\">");
        foreach ((string text, bool bad) in lines)
        {
            sb.Append("<div").Append(bad ? " style=\"color:#a30\"" : string.Empty).Append('>').Append(WebUtility.HtmlEncode(text)).Append("</div>");
        }

        return sb.Append("</div>").ToString();
    }

    // The item shown is the protected content with the message's own headers on top. Whatever non-Content headers the content carries are dropped
    // unless the signature covers them (a signed message/rfc822), so nothing the signature does not vouch for is presented as the content's own.
    private static MimeMailItem ItemToShow(SmimeResult result)
    {
        if (!result.IsSmime || ReferenceEquals(result.Content, result.Root))
        {
            return MimeMailItem.Parse(result.Root.ToArray());
        }

        IReadOnlyList<string> protectedNames = result.Outside?.ProtectedHeaders ?? [];
        bool authenticated = result.Layers.OfType<SmimeSignedLayer>().Any(static l => l.Succeeded);
        MimeEntity content = result.Content;
        MimeEntity target = content is MimeMessagePart { Message: { } m } ? m : content;
        MimeEntity merged = MimeEntity.Parse(target.ToArray());
        bool keepInner = authenticated && protectedNames.Count > 0;
        if (!keepInner)
        {
            foreach (MimeHeader h in merged.Headers.ToArray())
            {
                if (!IsContentHeader(h.Name))
                {
                    merged.Headers.Remove(h);
                }
            }
        }

        foreach (MimeHeader h in result.Root.Headers.ToArray())
        {
            if (!IsContentHeader(h.Name) && !(keepInner && protectedNames.Contains(h.Name, StringComparer.OrdinalIgnoreCase)))
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

    private static bool IsContentHeader(string name) =>
        name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) || name.Equals("MIME-Version", StringComparison.OrdinalIgnoreCase);
}
