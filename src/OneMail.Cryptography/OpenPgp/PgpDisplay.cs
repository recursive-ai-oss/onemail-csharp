using System.Net;
using System.Text;
using RecursiveAI.OneMail.Display;
using RecursiveAI.OneMail.Mime;

namespace RecursiveAI.OneMail.Cryptography.OpenPgp;

/// <summary>Display HTML for a message that went through <see cref="PgpMimeProcessor"/>, with its security status shown above it.</summary>
public static class PgpDisplay
{
    /// <summary>
    /// Renders the verified or decrypted message as display HTML (the same sanitiser and policy as <see cref="MailItemDisplayExtensions.ToDisplayHtml"/>), with a status block on top.
    /// Only the protected content is shown: a part that sits beside the signed part in an unprotected wrapper, and text around an inline signature, are not shown as if they
    /// were signed, and the status says they exist. Headers come from the message; the ones the signature covers (<see cref="PgpOutside.ProtectedHeaders"/>) replace the outer ones only when the message is authenticated.
    /// </summary>
    /// <param name="result">The result of <see cref="PgpMimeProcessor.Open(MimeEntity, PgpOpenOptions)"/>.</param>
    /// <param name="options">Display choices; null for the defaults. A <see cref="DisplayOptions.StatusBanner"/> of its own is replaced.</param>
    /// <returns>The display result. For a message without OpenPGP protection there is no status block and the message is shown as it is.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is null.</exception>
    public static DisplayResult ToDisplayHtml(this PgpResult result, DisplayOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        options ??= DisplayOptions.Default;
        IMailItem item = result.InnerItem ?? MimeMailItem.Parse(result.Root.ToArray());
        string status = StatusHtml(result);
        DisplayOptions effective = options.WithStatusBanner(status.Length == 0 ? null : _ => status);
        return item.ToDisplayHtml(effective);
    }

    /// <summary>The status block as an HTML fragment (all message-derived text escaped); empty when the message has no OpenPGP protection.</summary>
    /// <param name="result">The result.</param>
    /// <returns>The fragment.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is null.</exception>
    public static string StatusHtml(PgpResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.IsPgp)
        {
            return string.Empty;
        }

        var lines = new List<(string Text, bool Bad)>();
        // The wrapper problem (a signed part beside unsigned ones) is reported on its own line below; the signed part itself is judged by its layers.
        bool layersOk = result.Layers.All(static l => l.Succeeded);
        PgpSignerResult? good = result.Signers.FirstOrDefault(static s => s.IsValid);
        bool senderOk = good is not null && result.Signers.Any(static s => s.IsValid && s.UserIdMatchesSender && s.KeyValidAtSigning == true);
        if (layersOk && senderOk && good is not null)
        {
            lines.Add(((result.WasEncrypted ? "Encrypted and signed by " : "Signed by ") + (good.UserId ?? "an unnamed key") + " (key " + Short(good) + "). The signature is valid.", false));
        }
        else if (layersOk && good is not null)
        {
            lines.Add(((result.WasEncrypted ? "Encrypted and signed" : "Signed") + " with key " + Short(good) + ", which does not name the sender shown in From. The sender is not verified.", true));
        }
        else if (layersOk && result.WasEncrypted)
        {
            lines.Add(("Encrypted but not signed: nobody vouches for who wrote it.", true));
        }
        else
        {
            PgpLayer? failed = result.Layers.FirstOrDefault(static l => !l.Succeeded);
            lines.Add(("Not verified: " + (failed?.Detail ?? failed?.Status.ToString() ?? "the protection could not be checked") + ".", true));
        }

        if (result.Issues.Contains(PgpIssue.ProtectedPartInUnprotectedWrapper))
        {
            string parts = result.Outside is { UnprotectedParts.Count: > 0 } o ? " (parts " + string.Join(", ", o.UnprotectedParts) + ")" : string.Empty;
            lines.Add(("Other parts of this message are not signed or encrypted and are not shown here" + parts + ".", true));
        }

        if (result.Layers.Any(static l => l.Issues.Contains(PgpIssue.InlineTextOutsideArmor)))
        {
            lines.Add(("Text around the signed block was not signed and is not shown here.", true));
        }

        if (result.Succeeded && result.Outside is { ProtectedHeaders.Count: 0 })
        {
            lines.Add(("The headers (Subject, From, To, Date) are not covered by the signature.", false));
        }

        if (result.Issues.Contains(PgpIssue.ProtectedRecipientMismatch))
        {
            lines.Add(("Warning: the signed recipient list does not include you. The message may have been forwarded by someone other than the author.", true));
        }

        if (result.Layers.Any(static l => l.Issues.Contains(PgpIssue.WeakAlgorithms)))
        {
            lines.Add(("The signature uses a weak algorithm.", true));
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

    private static string Short(PgpSignerResult s) => Convert.ToHexString(s.CertificateFingerprint).ToLowerInvariant() is { Length: > 16 } f ? f[^16..] : Convert.ToHexString(s.CertificateFingerprint).ToLowerInvariant();
}
