namespace RecursiveAI.OneMail.Mime.Reading;

/// <summary>What <see cref="MimeHeaderBlock.TryParse"/> found at the start of a span.</summary>
/// <param name="HeaderCount">The number of header fields (a field and its folded continuation lines count once).</param>
/// <param name="Length">The bytes the header lines take, line ends included, up to the blank line or the first line that is not part of the block (or the end of the span, for an incomplete last line).</param>
/// <param name="BodyOffset">The offset of the first byte after the blank line that ends the block; equal to <paramref name="Length"/> when no blank line was found.</param>
/// <param name="Terminated">True when a blank line ended the block. False when the span ended first, a line that is no header field stopped the block, or the size limit was reached.</param>
/// <param name="BareLineFeeds">The number of lines that end in LF alone; the rest end in CR LF. A block with both kinds is mixed.</param>
public readonly record struct MimeHeaderBlockInfo(int HeaderCount, int Length, int BodyOffset, bool Terminated, int BareLineFeeds);

/// <summary>
/// Span-level recognition of a MIME header block (RFC 5322 §2.2): lines of <c>name: value</c> with folded continuation lines, ended by an empty line.
/// For tools that look for messages in data they cannot parse as a whole. Nothing throws and nothing is allocated.
/// </summary>
public static class MimeHeaderBlock
{
    /// <summary>
    /// Reads the header block at the start of <paramref name="data"/>. A field name is one or more printable ASCII characters other than the colon
    /// (RFC 5322 §3.6.8), optionally followed by blanks (accepted, as mail clients accept them) before the colon; a line that starts with a blank
    /// continues the field before it. The block ends at the first empty line, or at the first line that is neither a field nor a continuation.
    /// </summary>
    /// <param name="data">The bytes. May be cut anywhere.</param>
    /// <param name="info">What was found.</param>
    /// <param name="maxBytes">The most bytes to look at. Default 1 MiB, the reader's header block limit; a longer block is returned unterminated at the limit.</param>
    /// <returns>True when at least one header field was found at the very start of the span; a span that starts with anything else gives false.</returns>
    public static bool TryParse(ReadOnlySpan<byte> data, out MimeHeaderBlockInfo info, int maxBytes = 1 << 20)
    {
        info = default;
        int limit = Math.Min(data.Length, Math.Max(0, maxBytes));
        int pos = 0;
        int headers = 0;
        int bare = 0;
        int consumed = 0;
        while (pos < limit)
        {
            int lf = data[pos..limit].IndexOf((byte)'\n');
            int lineEnd = lf < 0 ? limit : pos + lf;           // index of the LF, or the end of what we may read
            bool hasLf = lf >= 0;
            int contentEnd = lineEnd > pos && data[lineEnd - 1] == (byte)'\r' && hasLf ? lineEnd - 1 : lineEnd;
            ReadOnlySpan<byte> line = data[pos..contentEnd];
            if (line.IsEmpty && hasLf)
            {
                if (hasLf && contentEnd == lineEnd)
                {
                    bare++;
                }

                if (headers == 0)
                {
                    return false;
                }

                info = new MimeHeaderBlockInfo(headers, pos, lineEnd + 1, true, bare);
                return true;
            }

            if (line.IsEmpty)
            {
                break;
            }

            if (line[0] is (byte)' ' or (byte)'\t')
            {
                if (headers == 0)
                {
                    return false;
                }
            }
            else
            {
                int colon = line.IndexOf((byte)':');
                if (colon <= 0)
                {
                    break;
                }

                ReadOnlySpan<byte> name = line[..colon];
                while (name.Length > 0 && name[^1] is (byte)' ' or (byte)'\t')
                {
                    name = name[..^1];
                }

                if (name.IsEmpty || !IsFieldName(name))
                {
                    break;
                }

                headers++;
            }

            if (hasLf && contentEnd == lineEnd)
            {
                bare++;
            }

            if (!hasLf)
            {
                // The span ends inside this line: the field is incomplete but present.
                consumed = limit;
                pos = limit;
                break;
            }

            pos = lineEnd + 1;
            consumed = pos;
        }

        if (headers == 0)
        {
            return false;
        }

        info = new MimeHeaderBlockInfo(headers, consumed, consumed, false, bare);
        return true;
    }

    // RFC 5322 §3.6.8 ftext: printable US-ASCII except the colon.
    private static bool IsFieldName(ReadOnlySpan<byte> name)
    {
        foreach (byte b in name)
        {
            if (b is < 33 or > 126 or (byte)':')
            {
                return false;
            }
        }

        return true;
    }
}
