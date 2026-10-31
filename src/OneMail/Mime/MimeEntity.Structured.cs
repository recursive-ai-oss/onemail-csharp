using RecursiveAI.OneMail.Mime.Reading;
using RecursiveAI.OneMail.Text;

namespace RecursiveAI.OneMail.Mime;

public abstract partial class MimeEntity
{
    /// <summary>
    /// The <c>Content-Type</c> parameters with RFC 2231 continuations and extended values resolved and decoded
    /// (<c>filename*</c> arrives as <c>filename</c>). Names are case-insensitive; the first of a repeated name wins. An unusable
    /// media type has no parameters.
    /// </summary>
    /// <param name="defects">Receives what was repaired (for example <see cref="MimeDefectKind.DuplicateParameter"/>); may be null.</param>
    /// <returns>The parameters in order of first appearance.</returns>
    public IReadOnlyList<DecodedParameter> GetContentTypeParameters(List<MimeDefect>? defects = null)
    {
        MimeHeader? h = Headers.GetFirst("Content-Type");
        int after = h is null ? -1 : HeaderDecoding.AfterMediaType(h);
        return after < 0 ? [] : HeaderDecoding.Parameters(h, after, defects, Depth);
    }

    /// <summary>One <c>Content-Type</c> parameter by name (ASCII case-insensitive), decoded.</summary>
    /// <param name="name">The parameter name without a <c>*</c> suffix, for example <c>charset</c>.</param>
    /// <returns>The value, or null if the parameter is absent.</returns>
    public string? GetContentTypeParameter(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (DecodedParameter p in GetContentTypeParameters())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return p.Value;
            }
        }

        return null;
    }

    /// <summary>The disposition from <c>Content-Disposition</c> in lower case (<c>attachment</c>, <c>inline</c> or as written), or null.</summary>
    public string? Disposition
    {
        get
        {
            MimeHeader? h = Headers.GetFirst("Content-Disposition");
            if (h is null)
            {
                return null;
            }

            ReadOnlySpan<byte> v = h.RawValue.Span;
            int s = StructuredHeader.SkipCfws(v, 0);
            int e = HeaderDecoding.AfterToken(v);
            string token = HeaderText.DecodeName(v[s..e]).Trim().ToLowerInvariant();
            return token.Length == 0 ? null : token;
        }
    }

    /// <summary>The <c>Content-Disposition</c> parameters, decoded as for <see cref="GetContentTypeParameters"/>.</summary>
    /// <param name="defects">Receives what was repaired; may be null.</param>
    /// <returns>The parameters.</returns>
    public IReadOnlyList<DecodedParameter> GetDispositionParameters(List<MimeDefect>? defects = null)
    {
        MimeHeader? h = Headers.GetFirst("Content-Disposition");
        return h is null ? [] : HeaderDecoding.Parameters(h, HeaderDecoding.AfterToken(h.RawValue.Span), defects, Depth);
    }

    /// <summary>The file name: the disposition's <c>filename</c>, else the media type's <c>name</c> (ambiguity N9), else null. Kept as written; it is not made safe for a file system.</summary>
    public string? FileName
    {
        get
        {
            foreach (DecodedParameter p in GetDispositionParameters())
            {
                if (string.Equals(p.Name, "filename", StringComparison.OrdinalIgnoreCase))
                {
                    return p.Value;
                }
            }

            return GetContentTypeParameter("name");
        }
    }

    /// <summary>
    /// An address header (<c>From</c>, <c>To</c>, <c>Cc</c>, <c>Bcc</c>, <c>Reply-To</c>, <c>Sender</c> …) parsed on demand. Only the
    /// first field of that name is read (ambiguity H2); the others stay in <see cref="Headers"/>.
    /// </summary>
    /// <param name="headerName">The field name, ASCII case-insensitive.</param>
    /// <param name="defects">Receives what was repaired; may be null.</param>
    /// <returns>The addresses and groups, or an empty list when the header is absent.</returns>
    public IReadOnlyList<MimeAddress> GetAddresses(string headerName, List<MimeDefect>? defects = null)
    {
        MimeHeader? h = Headers.GetFirst(headerName);
        return h is null ? [] : AddressParser.Parse(h.RawValue.Span, defects, Math.Max(0, h.ValueSourceOffset), Depth);
    }

    /// <summary>The first <c>Date</c> header, or null when absent or not a date (then <see cref="MimeDefectKind.InvalidDate"/> is reported).</summary>
    /// <param name="defects">Receives what was repaired; may be null.</param>
    /// <returns>The date or null.</returns>
    public MimeDate? GetDate(List<MimeDefect>? defects = null)
    {
        MimeHeader? h = Headers.GetFirst("Date");
        if (h is null)
        {
            return null;
        }

        return DateParser.TryParse(h.RawValue.Span, out MimeDate d, defects, Math.Max(0, h.ValueSourceOffset), Depth) ? d : null;
    }

    private string? FirstId(string name, List<MimeDefect>? defects)
    {
        List<string> ids = GetIds(name, defects);
        return ids.Count > 0 ? ids[0] : null;
    }

    private List<string> GetIds(string name, List<MimeDefect>? defects)
    {
        MimeHeader? h = Headers.GetFirst(name);
        return h is null ? [] : MessageIdParser.Parse(h.RawValue.Span, defects, Math.Max(0, h.ValueSourceOffset), Depth);
    }

    /// <summary>The <c>Message-ID</c> without angle brackets, or null.</summary>
    /// <param name="defects">Receives what was repaired; may be null.</param>
    /// <returns>The identifier or null.</returns>
    public string? GetMessageId(List<MimeDefect>? defects = null) => FirstId("Message-ID", defects);

    /// <summary>The <c>Content-ID</c> without angle brackets, or null.</summary>
    /// <param name="defects">Receives what was repaired; may be null.</param>
    /// <returns>The identifier or null.</returns>
    public string? GetContentId(List<MimeDefect>? defects = null) => FirstId("Content-ID", defects);

    /// <summary>The identifiers in <c>In-Reply-To</c>, without angle brackets.</summary>
    /// <param name="defects">Receives what was repaired; may be null.</param>
    /// <returns>The identifiers in order.</returns>
    public IReadOnlyList<string> GetInReplyTo(List<MimeDefect>? defects = null) => GetIds("In-Reply-To", defects);

    /// <summary>The identifiers in <c>References</c>, without angle brackets, oldest first as written.</summary>
    /// <param name="defects">Receives what was repaired; may be null.</param>
    /// <returns>The identifiers in order.</returns>
    public IReadOnlyList<string> GetReferences(List<MimeDefect>? defects = null) => GetIds("References", defects);

    /// <summary>The decoded <c>Content-Description</c>, or null.</summary>
    public string? ContentDescription => Headers.GetFirst("Content-Description")?.Value;

    /// <summary>The <c>MIME-Version</c> (RFC 2045 §4) as major and minor numbers, or null when absent or unreadable.</summary>
    public (int Major, int Minor)? MimeVersion
    {
        get
        {
            MimeHeader? h = Headers.GetFirst("MIME-Version");
            if (h is null)
            {
                return null;
            }

            ReadOnlySpan<byte> v = h.RawValue.Span;
            int i = StructuredHeader.SkipCfws(v, 0);
            int s = i;
            while (i < v.Length && v[i] is >= (byte)'0' and <= (byte)'9')
            {
                i++;
            }

            if (i == s || i - s > 4)
            {
                return null;
            }

            int major = int.Parse(HeaderText.DecodeName(v[s..i]), System.Globalization.CultureInfo.InvariantCulture);
            i = StructuredHeader.SkipCfws(v, i);
            if (i >= v.Length || v[i] != (byte)'.')
            {
                return null;
            }

            i = StructuredHeader.SkipCfws(v, i + 1);
            s = i;
            while (i < v.Length && v[i] is >= (byte)'0' and <= (byte)'9')
            {
                i++;
            }

            return i == s || i - s > 4 ? null : (major, int.Parse(HeaderText.DecodeName(v[s..i]), System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Every <c>Authentication-Results</c> field of this entity (RFC 8601), parsed, in the order they appear: the newest hop is
    /// first. Parsing only; the results say what each server claims.
    /// </summary>
    /// <param name="defects">Receives what was skipped or guessed; may be null.</param>
    /// <returns>The parsed fields; empty when there are none.</returns>
    public IReadOnlyList<Authentication.AuthenticationResults> GetAuthenticationResults(List<MimeDefect>? defects = null)
    {
        var r = new List<Authentication.AuthenticationResults>();
        foreach (MimeHeader h in Headers.GetAll("Authentication-Results"))
        {
            var a = Authentication.AuthenticationResults.Parse(h.RawValue.Span, Math.Max(0, h.ValueSourceOffset), Depth);
            defects?.AddRange(a.Defects);
            r.Add(a);
        }

        return r;
    }

    /// <summary>Every <c>Received-SPF</c> field of this entity (RFC 7208 section 9.1), parsed, in order. Parsing only.</summary>
    /// <param name="defects">Receives what was skipped or guessed; may be null.</param>
    /// <returns>The parsed fields; empty when there are none.</returns>
    public IReadOnlyList<Authentication.ReceivedSpf> GetReceivedSpf(List<MimeDefect>? defects = null)
    {
        var r = new List<Authentication.ReceivedSpf>();
        foreach (MimeHeader h in Headers.GetAll("Received-SPF"))
        {
            var a = Authentication.ReceivedSpf.Parse(h.RawValue.Span, Math.Max(0, h.ValueSourceOffset), Depth);
            defects?.AddRange(a.Defects);
            r.Add(a);
        }

        return r;
    }
}
