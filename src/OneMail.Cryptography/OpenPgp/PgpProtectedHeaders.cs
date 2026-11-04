using RecursiveAI.OneMail.Mime;

namespace RecursiveAI.OneMail.Cryptography.OpenPgp;

/// <summary>
/// The message headers to put inside the signature and encryption (draft-ietf-lamps-header-protection, the "memory hole" form): the headers are repeated on the
/// protected entity, whose <c>Content-Type</c> gets the parameter <c>protected-headers="v1"</c>, and also written on the outer message so a client that
/// does not know the scheme still shows them. For an encrypted message the outer <c>Subject</c> can be replaced by <c>...</c> so it does not leak.
/// </summary>
/// <remarks>
/// The entity that is signed or encrypted keeps its own <c>Content-*</c> headers and gets exactly these fields; any other message header it carries is dropped from the copy.
/// <c>Bcc</c> is never written, in either place: it would tell every recipient who else received the message. <c>Received</c>, <c>Return-Path</c> and the
/// <c>Content-*</c> headers are not protected headers; <c>MIME-Version</c> is written on the outer message only.
/// </remarks>
public sealed class PgpProtectedHeaders
{
    private static readonly string[] DefaultNames =
    [
        "From", "Sender", "To", "Cc", "Reply-To", "Subject", "Date", "Message-ID", "In-Reply-To", "References", "Keywords", "Comments",
    ];

    private readonly List<byte[]> _fields;

    /// <summary>Protects the given header fields, copied as they are (raw bytes, so a parsed field is not re-encoded).</summary>
    /// <param name="headers">The fields. <c>Bcc</c> and <c>Content-*</c>/<c>MIME-Version</c> fields are an error.</param>
    /// <exception cref="ArgumentNullException"><paramref name="headers"/> is null.</exception>
    /// <exception cref="ArgumentException">A field is <c>Bcc</c>, <c>MIME-Version</c> or a <c>Content-*</c> header.</exception>
    public PgpProtectedHeaders(IEnumerable<MimeHeader> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        _fields = [];
        foreach (MimeHeader h in headers)
        {
            string name = h.Name;
            if (name.Equals("Bcc", StringComparison.OrdinalIgnoreCase) || name.Equals("MIME-Version", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The header '" + name + "' cannot be a protected header.", nameof(headers));
            }

            byte[] raw = h.Raw.ToArray();
            if (raw.Length == 0 || raw[^1] != (byte)'\n')
            {
                raw = [.. raw, (byte)'\r', (byte)'\n'];
            }

            _fields.Add(raw);
        }
    }

    /// <summary>Whether the outer <c>Subject</c> of an encrypted message becomes <see cref="ObscuredSubject"/>. Default false. Ignored when signing without encrypting.</summary>
    public bool ObscureSubject { get; init; }

    /// <summary>What the outer <c>Subject</c> shows when <see cref="ObscureSubject"/> is set. Default <c>...</c>.</summary>
    public string ObscuredSubject { get; init; } = "...";

    /// <summary>The names of the protected fields in order.</summary>
    public IReadOnlyList<string> Names => [.. Fresh().Select(static h => h.Name)];

    /// <summary>
    /// Takes the protected headers from a message: <c>From</c>, <c>Sender</c>, <c>To</c>, <c>Cc</c>, <c>Reply-To</c>, <c>Subject</c>, <c>Date</c>, <c>Message-ID</c>,
    /// <c>In-Reply-To</c>, <c>References</c>, <c>Keywords</c> and <c>Comments</c> that it has, or the fields named in <paramref name="names"/>.
    /// </summary>
    /// <param name="message">The message whose headers are copied; not modified.</param>
    /// <param name="obscureSubject">Sets <see cref="ObscureSubject"/>.</param>
    /// <param name="names">The fields to copy instead of the default set; <c>Bcc</c> and <c>Content-*</c> names are an error.</param>
    /// <returns>The headers.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException">A name is <c>Bcc</c>, <c>MIME-Version</c> or a <c>Content-*</c> header.</exception>
    public static PgpProtectedHeaders FromMessage(MimeEntity message, bool obscureSubject = false, IEnumerable<string>? names = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        string[] wanted = names is null ? DefaultNames : [.. names];
        return new PgpProtectedHeaders(message.Headers.Where(h => wanted.Contains(h.Name, StringComparer.OrdinalIgnoreCase)).ToArray())
        {
            ObscureSubject = obscureSubject,
        };
    }

    /// <summary>New, unowned copies of the fields.</summary>
    private List<MimeHeader> Fresh()
    {
        var list = new List<MimeHeader>();
        foreach (byte[] raw in _fields)
        {
            MimeEntity holder = MimeEntity.Parse(Concat(raw, "\r\n"u8.ToArray()));
            foreach (MimeHeader h in holder.Headers.ToArray())
            {
                holder.Headers.Remove(h);
                list.Add(h);
            }
        }

        return list;
    }

    private static byte[] Concat(byte[] a, byte[] b) => [.. a, .. b];

    /// <summary>A copy of <paramref name="content"/> with these headers on it and <c>protected-headers="v1"</c> on its Content-Type.</summary>
    internal MimeEntity ApplyTo(MimeEntity content)
    {
        MimeEntity copy = MimeEntity.Parse(content.ToArray());
        List<MimeHeader> fields = Fresh();
        // The protected entity carries exactly these fields and its own Content-* headers: any other message header already on the content (a Bcc, a second
        // Subject) would be a second, conflicting or leaking value inside the signature.
        foreach (MimeHeader h in copy.Headers.ToArray())
        {
            if (!h.Name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) && !h.Name.Equals("MIME-Version", StringComparison.OrdinalIgnoreCase))
            {
                copy.Headers.Remove(h);
            }
        }

        for (int i = 0; i < fields.Count; i++)
        {
            copy.Headers.Insert(i, fields[i]);
        }

        MimeHeader? ct = copy.Headers.GetFirst("Content-Type");
        if (ct is null)
        {
            copy.Headers.Add("Content-Type", "text/plain; charset=us-ascii; protected-headers=\"v1\"");
        }
        else if (copy.GetContentTypeParameter("protected-headers") is null)
        {
            ct.SetValue(ct.Value.TrimEnd().TrimEnd(';') + "; protected-headers=\"v1\"");
        }

        return copy;
    }

    /// <summary>Writes the fields on the outer message ahead of its own headers, with <c>MIME-Version</c> when it has none.</summary>
    internal void ApplyOuter(MimeEntity outer, bool encrypted)
    {
        List<MimeHeader> fields = Fresh();
        int at = 0;
        foreach (MimeHeader f in fields)
        {
            if (encrypted && ObscureSubject && f.Name.Equals("Subject", StringComparison.OrdinalIgnoreCase))
            {
                outer.Headers.Insert(at++, new MimeHeader("Subject", ObscuredSubject));
            }
            else
            {
                outer.Headers.Insert(at++, f);
            }
        }

        if (outer.Headers.GetFirst("MIME-Version") is null)
        {
            outer.Headers.Insert(at, new MimeHeader("MIME-Version", "1.0"));
        }
    }

    /// <summary>Whether the content is already a PGP/MIME signed wrapper whose signed part carries the headers (so only the outer fields are written).</summary>
    internal static bool IsSignedWrapperWithProtectedHeaders(MimeEntity content) =>
        content is MimeMultipart { Children.Count: > 0 } mp && mp.MediaType == "multipart/signed" && mp.Children[0].GetContentTypeParameter("protected-headers") is not null;
}
