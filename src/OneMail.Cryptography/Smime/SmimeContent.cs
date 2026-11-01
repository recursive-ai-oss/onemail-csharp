using System.Text;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Mime.Transfer;

namespace RecursiveAI.OneMail.Cryptography.Smime;

/// <summary>Canonical form and framing rules shared by signing, encryption and reading (RFC 8551 §3.1, RFC 1847 §2).</summary>
internal static class SmimeContent
{
    /// <summary>
    /// The bytes that get signed or encrypted for <paramref name="content"/>: a copy of the entity with every 8-bit or binary leaf
    /// re-encoded to 7-bit safe quoted-printable (text) or base64 (everything else), serialised with CRLF line ends.
    /// RFC 8551 §3.1.1 requires the canonical MIME form so transports that re-encode or re-wrap lines cannot break the signature.
    /// An entity that is already 7-bit with CRLF line ends keeps every one of its stored bytes.
    /// </summary>
    internal static byte[] Canonicalise(MimeEntity content)
    {
        byte[] stored = content.ToArray();
        MimeEntity copy = MimeEntity.Parse(stored);
        bool changed = Reencode(copy);
        byte[] bytes = changed ? copy.ToArray() : stored;
        return NormaliseLineEnds(bytes) ?? bytes;
    }

    private static bool Reencode(MimeEntity entity)
    {
        bool changed = false;
        switch (entity)
        {
            case MimePart part:
                ContentTransferEncoding cte = part.TransferEncoding;
                if (cte is ContentTransferEncoding.EightBit or ContentTransferEncoding.Binary or ContentTransferEncoding.SevenBit or ContentTransferEncoding.Unknown
                    && NeedsEncoding(part, cte))
                {
                    byte[] decoded = part.GetDecodedContent();
                    bool text = part.MediaTopLevelType == "text" && cte != ContentTransferEncoding.Binary;
                    part.SetDecodedContent(decoded, text ? ContentTransferEncoding.QuotedPrintable : ContentTransferEncoding.Base64);
                    changed = true;
                }

                break;
            case MimeMultipart multipart:
                foreach (MimeEntity child in multipart.Children)
                {
                    changed |= Reencode(child);
                }

                break;
            case MimeMessagePart message when message.Message is { } inner:
                changed |= Reencode(inner);
                break;
        }

        return changed;
    }

    private static bool NeedsEncoding(MimePart part, ContentTransferEncoding cte)
    {
        if (cte == ContentTransferEncoding.Binary)
        {
            return true;
        }

        using Stream s = part.OpenRawContent();
        byte[] buffer = new byte[8192];
        int n;
        while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
        {
            foreach (byte b in buffer.AsSpan(0, n))
            {
                if (b >= 0x80 || b == 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Converts bare LF and bare CR to CRLF. Returns null when the input already has only CRLF line ends.</summary>
    internal static byte[]? NormaliseLineEnds(ReadOnlySpan<byte> data)
    {
        bool needs = false;
        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            if (b == (byte)'\n' && (i == 0 || data[i - 1] != (byte)'\r'))
            {
                needs = true;
                break;
            }

            if (b == (byte)'\r' && (i + 1 >= data.Length || data[i + 1] != (byte)'\n'))
            {
                needs = true;
                break;
            }
        }

        if (!needs)
        {
            return null;
        }

        var output = new List<byte>(data.Length + 16);
        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            if (b == (byte)'\r')
            {
                output.Add((byte)'\r');
                output.Add((byte)'\n');
                if (i + 1 < data.Length && data[i + 1] == (byte)'\n')
                {
                    i++;
                }
            }
            else if (b == (byte)'\n')
            {
                output.Add((byte)'\r');
                output.Add((byte)'\n');
            }
            else
            {
                output.Add(b);
            }
        }

        return output.ToArray();
    }

    /// <summary>A body part carrying a CMS: base64, 64 columns, the usual S/MIME headers (RFC 8551 §3.2).</summary>
    internal static MimePart CmsPart(ReadOnlySpan<byte> cms, string contentType, string fileName, string? description)
    {
        var part = new MimePart();
        part.Headers.Add("Content-Type", contentType + "; name=\"" + fileName + "\"");
        part.Headers.Add("Content-Disposition", "attachment; filename=\"" + fileName + "\"");
        if (description is not null)
        {
            part.Headers.Add("Content-Description", description);
        }

        part.SetDecodedContent(cms, ContentTransferEncoding.Base64);
        return part;
    }

    /// <summary>The RFC 8551 §3.5.3.2 / RFC 5751 micalg token for a digest algorithm OID.</summary>
    internal static string MicalgFor(string digestOid) => digestOid switch
    {
        "1.2.840.113549.2.5" => "md5",
        "1.3.14.3.2.26" => "sha-1",
        "2.16.840.1.101.3.4.2.4" => "sha-224",
        "2.16.840.1.101.3.4.2.1" => "sha-256",
        "2.16.840.1.101.3.4.2.2" => "sha-384",
        "2.16.840.1.101.3.4.2.3" => "sha-512",
        "2.16.840.1.101.3.4.2.12" => "shake256",
        _ => "unknown",
    };

    internal static string DigestTag(string digestOid) => "digest:" + MicalgFor(digestOid);

    /// <summary>Whether <paramref name="boundary"/> occurs in <paramref name="data"/> as a line start (so it could end the part early).</summary>
    internal static bool ContainsBoundary(ReadOnlySpan<byte> data, string boundary)
    {
        byte[] needle = Encoding.ASCII.GetBytes("--" + boundary);
        return data.IndexOf(needle) >= 0;
    }
}
