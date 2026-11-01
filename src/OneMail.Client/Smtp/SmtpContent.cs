using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Mime.Transfer;

namespace RecursiveAI.OneMail.Client.Smtp;

/// <summary>What the client needs to know about a message before choosing SMTP parameters.</summary>
internal sealed class ContentFacts
{
    /// <summary>A header field holds bytes above 0x7F (raw UTF-8 headers, RFC 6532).</summary>
    public bool HeaderNonAscii;

    /// <summary>A body (or multipart preamble/epilogue) holds bytes above 0x7F outside base64/quoted-printable.</summary>
    public bool BodyNonAscii;

    /// <summary>A leaf declares <c>Content-Transfer-Encoding: binary</c>.</summary>
    public bool HasBinaryPart;

    /// <summary>Re-encoding would break a signature: S/MIME or PGP structure, or a DKIM/ARC signature header.</summary>
    public bool Signed;
}

internal static class SmtpContent
{
    private static readonly string[] SigningHeaders = ["DKIM-Signature", "ARC-Seal", "ARC-Message-Signature", "DomainKey-Signature"];

    public static bool HasHighBit(ReadOnlySpan<byte> s) => s.IndexOfAnyExceptInRange((byte)0, (byte)0x7F) >= 0;

    // Scans a part's stored (not decoded) bytes for 8-bit data through a pooled buffer, stopping at the first one.
    private static bool HasHighBit(MimePart part)
    {
        using Stream s = part.OpenRawContent();
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            int n;
            while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (HasHighBit(buffer.AsSpan(0, n)))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static ContentFacts Inspect(MimeEntity root)
    {
        var facts = new ContentFacts();
        foreach (MimeHeader h in root.Headers)
        {
            foreach (string n in SigningHeaders)
            {
                if (h.IsNamed(n))
                {
                    facts.Signed = true;
                }
            }
        }

        Walk(root, facts);
        return facts;
    }

    private static void Walk(MimeEntity e, ContentFacts facts)
    {
        foreach (MimeHeader h in e.Headers)
        {
            if (HasHighBit(h.Raw.Span))
            {
                facts.HeaderNonAscii = true;
            }
        }

        string type = e.MediaType;
        if (type is "multipart/signed" or "application/pkcs7-mime" or "application/pkcs7-signature" or "application/x-pkcs7-mime" or "application/pgp-signature" or "application/pgp-encrypted")
        {
            facts.Signed = true;
        }

        switch (e)
        {
            case MimePart part:
                ContentTransferEncoding cte = part.TransferEncoding;
                if (cte == ContentTransferEncoding.Binary)
                {
                    facts.HasBinaryPart = true;
                }

                if (cte is not (ContentTransferEncoding.Base64 or ContentTransferEncoding.QuotedPrintable) && part.RawContentLength > 0)
                {
                    facts.BodyNonAscii |= HasHighBit(part);
                }

                break;
            case MimeMultipart mp:
                if (HasHighBit(mp.GetPreamble()) || HasHighBit(mp.GetEpilogue()))
                {
                    facts.BodyNonAscii = true;
                }

                foreach (MimeEntity c in mp.Children)
                {
                    Walk(c, facts);
                }

                break;
            case MimeMessagePart mm:
                if (mm.Message is { } inner)
                {
                    Walk(inner, facts);
                }

                break;
            default:
                break;
        }
    }

    /// <summary>Facts for a message held as bytes: the first blank line splits headers from the rest.</summary>
    public static ContentFacts Inspect(ReadOnlySpan<byte> message)
    {
        int end = message.IndexOf("\r\n\r\n"u8);
        int lf = message.IndexOf("\n\n"u8);
        if (end < 0 || (lf >= 0 && lf < end)) { end = lf; }
        ReadOnlySpan<byte> headers = end < 0 ? message : message[..end];
        var facts = new ContentFacts { HeaderNonAscii = HasHighBit(headers) };
        if (end >= 0 && HasHighBit(message[end..]))
        {
            facts.BodyNonAscii = true;
        }

        foreach (string name in SigningHeaders)
        {
            if (ContainsHeader(headers, name))
            {
                facts.Signed = true;
            }
        }

        if (!facts.Signed && (message.IndexOf("multipart/signed"u8) >= 0 || message.IndexOf("application/pkcs7"u8) >= 0 || message.IndexOf("application/pgp-"u8) >= 0))
        {
            // Over-cautious on purpose: a signature structure anywhere (even quoted text) makes the downgrade refuse rather than risk breaking it.
            facts.Signed = true;
        }

        return facts;
    }

    private static bool ContainsHeader(ReadOnlySpan<byte> headers, string name)
    {
        byte[] n = System.Text.Encoding.ASCII.GetBytes(name + ":");
        int start = 0;
        while (start < headers.Length)
        {
            int eol = headers[start..].IndexOf((byte)'\n');
            ReadOnlySpan<byte> line = eol < 0 ? headers[start..] : headers.Slice(start, eol);
            if (line.Length >= n.Length && System.Text.Encoding.ASCII.GetString(line[..n.Length]).Equals(name + ":", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (eol < 0) { break; }
            start += eol + 1;
        }

        return false;
    }

    /// <summary>
    /// Re-encodes the 8-bit leaves of a tree for a 7-bit transfer (RFC 6152 §2): text becomes quoted-printable, everything else base64.
    /// Works on the tree it is given; callers pass a private copy.
    /// </summary>
    /// <returns>False when some 8-bit content cannot be re-encoded (a <c>message/rfc822</c> declared 8bit or binary).</returns>
    public static bool Downgrade(MimeEntity e)
    {
        switch (e)
        {
            case MimePart part:
                ContentTransferEncoding cte = part.TransferEncoding;
                bool needs = cte is ContentTransferEncoding.EightBit or ContentTransferEncoding.Binary;
                if (!needs && cte is not (ContentTransferEncoding.Base64 or ContentTransferEncoding.QuotedPrintable) && part.RawContentLength > 0)
                {
                    needs = HasHighBit(part);
                }

                if (needs)
                {
                    byte[] decoded = part.GetDecodedContent();
                    bool text = part.MediaTopLevelType == "text" && cte != ContentTransferEncoding.Binary;
                    part.SetDecodedContent(decoded, text ? ContentTransferEncoding.QuotedPrintable : ContentTransferEncoding.Base64);
                }

                return true;
            case MimeMultipart mp:
                if (HasHighBit(mp.GetPreamble())) { mp.SetPreamble(ReadOnlyMemory<byte>.Empty); }
                if (HasHighBit(mp.GetEpilogue())) { mp.SetEpilogue(ReadOnlyMemory<byte>.Empty); }
                return mp.Children.All(Downgrade);
            case MimeMessagePart mm:
                // RFC 2046 §5.2.1: message/rfc822 may only use 7bit, 8bit or binary, so its own encoding cannot be changed; its content can.
                return mm.Message is null || (mm.TransferEncoding is not (ContentTransferEncoding.EightBit or ContentTransferEncoding.Binary) && Downgrade(mm.Message));
            default:
                return true;
        }
    }
}
