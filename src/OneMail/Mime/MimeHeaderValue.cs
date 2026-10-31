using RecursiveAI.OneMail.Text;

namespace RecursiveAI.OneMail.Mime;

/// <summary>Builds the parameter part of structured header values such as <c>Content-Type</c> and <c>Content-Disposition</c>.</summary>
public static class MimeHeaderValue
{
    /// <summary>
    /// Formats one parameter as <c>; name=value</c>, ready to append to a value. A token value is written as it is, other printable
    /// ASCII as a quoted string, and anything else as an RFC 2231 extended value (<c>name*=utf-8''%C3%A9</c>), split into
    /// <c>name*0*</c>, <c>name*1*</c> ... continuations when it is long. Continuations never cut a character.
    /// </summary>
    /// <param name="name">The parameter name: ASCII.</param>
    /// <param name="value">The value. Must not contain a line break.</param>
    /// <returns>The text to append, starting with <c>; </c>.</returns>
    /// <exception cref="ArgumentException">The value contains CR or LF.</exception>
    public static string Parameter(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        if (value.AsSpan().IndexOfAny('\r', '\n') >= 0)
        {
            throw new ArgumentException("A parameter value cannot contain a line break.", nameof(value));
        }

        var sb = new System.Text.StringBuilder();
        foreach (Rfc2231.EncodedPart p in Rfc2231.Encode(name, value))
        {
            sb.Append("; ").Append(p.Name).Append('=');
            if (p.NeedsQuotes)
            {
                sb.Append('"').Append(p.Value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)).Append('"');
            }
            else
            {
                sb.Append(p.Value);
            }
        }

        return sb.ToString();
    }
}
