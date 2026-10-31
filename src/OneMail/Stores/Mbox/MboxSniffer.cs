using RecursiveAI.OneMail.Sources;

namespace RecursiveAI.OneMail.Stores.Mbox;

/// <summary>What <see cref="MboxSniffer"/> based its variant guess on.</summary>
public enum MboxVariantEvidence
{
    /// <summary>Nothing in the examined part distinguishes the variants.</summary>
    None = 0,

    /// <summary>A message's <c>Content-Length</c> lands exactly on the next envelope line, and quoted body lines were seen (mboxcl).</summary>
    ContentLengthWithQuoting,

    /// <summary>A valid <c>Content-Length</c> was found and a body inside it holds an unquoted envelope line (mboxcl2).</summary>
    ContentLengthWithUnquotedEnvelope,

    /// <summary>A valid <c>Content-Length</c> was found and no quoted or unquoted envelope line settles cl against cl2 (mboxcl is chosen).</summary>
    ContentLengthOnly,

    /// <summary>A line with two or more <c>&gt;</c> before <c>From </c>: only mboxrd writes that for ordinary text.</summary>
    MultiplyQuotedFrom,

    /// <summary>Only lines with a single <c>&gt;From </c> were seen; mboxo and mboxrd agree, mboxrd is chosen.</summary>
    SingleQuotedFrom,
}

/// <summary>The result of <see cref="MboxSniffer.Detect"/>.</summary>
/// <param name="IsMbox">Whether the source starts like an mbox file.</param>
/// <param name="Variant">The guessed variant; <see cref="MboxVariant.Unknown"/> when the examined part shows no sign.</param>
/// <param name="Evidence">What the guess rests on.</param>
/// <param name="MessagesExamined">How many messages the guess looked at.</param>
public readonly record struct MboxDetection(bool IsMbox, MboxVariant Variant, MboxVariantEvidence Evidence, int MessagesExamined);

/// <summary>Content-based detection of mbox files and their variant.</summary>
public static class MboxSniffer
{
    /// <summary>
    /// Whether the source starts with an envelope line (after any leading empty lines), checking only the first 4 KiB.
    /// </summary>
    /// <param name="source">The bytes to look at.</param>
    /// <returns>True for an mbox file.</returns>
    public static bool LooksLikeMbox(IRandomAccessSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        byte[] b = new byte[4096];
        int n = source.ReadFully(0, b);
        return LooksLikeMbox(b.AsSpan(0, n));
    }

    /// <summary>Whether the bytes start with an envelope line (after any leading empty lines).</summary>
    /// <param name="head">The first bytes of a file.</param>
    /// <returns>True for an mbox file.</returns>
    public static bool LooksLikeMbox(ReadOnlySpan<byte> head)
    {
        int i = 0;
        while (i < head.Length && (head[i] == (byte)'\n' || head[i] == (byte)'\r'))
        {
            i++;
        }

        head = head[i..];
        if (!head.StartsWith("From "u8))
        {
            return false;
        }

        int nl = head.IndexOf((byte)'\n');
        return MboxFromLine.IsEnvelope(nl < 0 ? head : head[..nl]);
    }

    /// <summary>
    /// Guesses the variant from the first messages: a valid <c>Content-Length</c> means mboxcl or mboxcl2, doubly quoted
    /// <c>&gt;&gt;From </c> lines mean mboxrd, a lone <c>&gt;From </c> is taken as mboxrd. Reading a file with
    /// <see cref="MboxVariant.Unknown"/> works without this call; the guess only chooses how quoting is undone.
    /// </summary>
    /// <param name="source">The mbox bytes.</param>
    /// <param name="maxBytes">How much of the start to examine. Default 8 MiB.</param>
    /// <param name="maxMessages">How many messages to examine at most. Default 2000.</param>
    /// <returns>The detection.</returns>
    public static MboxDetection Detect(IRandomAccessSource source, long maxBytes = 8 << 20, int maxMessages = 2000)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!LooksLikeMbox(source))
        {
            return new MboxDetection(false, MboxVariant.Unknown, MboxVariantEvidence.None, 0);
        }

        using PrefixSource prefix = new(source, maxBytes);
        int messages = 0;
        int clValid = 0;
        int clQuoted = 0;
        int clUnquoted = 0;
        int multi = 0;
        int single = 0;
        byte[] buf = new byte[65536];
        using MboxReader reader = new(prefix, new MboxReaderOptions { Variant = MboxVariant.Unknown });
        while (messages < maxMessages && reader.Read())
        {
            MboxEntry e = reader.Current;
            messages++;
            bool cl = e.EndedByContentLength;
            if (cl)
            {
                clValid++;
            }

            long done = 0;
            while (done < e.MessageLength)
            {
                int n = prefix.ReadFully(e.MessageOffset + done, buf.AsSpan(0, (int)Math.Min(buf.Length, e.MessageLength - done)));
                if (n == 0)
                {
                    break;
                }

                ReadOnlySpan<byte> w = buf.AsSpan(0, n);
                CountQuoted(w, ref multi, ref single, cl, ref clQuoted);
                if (cl)
                {
                    int idx = 0;
                    while (idx < w.Length)
                    {
                        int k = w[idx..].IndexOf("\nFrom "u8);
                        if (k < 0)
                        {
                            break;
                        }

                        int ls = idx + k + 1;
                        int le = w[ls..].IndexOf((byte)'\n');
                        if (MboxFromLine.IsEnvelope(le < 0 ? w[ls..] : w.Slice(ls, le)))
                        {
                            clUnquoted++;
                        }

                        idx = ls;
                    }
                }

                done += n;
            }
        }

        if (clValid > 0)
        {
            if (clUnquoted > 0)
            {
                return new MboxDetection(true, MboxVariant.Mboxcl2, MboxVariantEvidence.ContentLengthWithUnquotedEnvelope, messages);
            }

            return clQuoted > 0
                ? new MboxDetection(true, MboxVariant.Mboxcl, MboxVariantEvidence.ContentLengthWithQuoting, messages)
                : new MboxDetection(true, MboxVariant.Mboxcl, MboxVariantEvidence.ContentLengthOnly, messages);
        }

        if (multi > 0)
        {
            return new MboxDetection(true, MboxVariant.Mboxrd, MboxVariantEvidence.MultiplyQuotedFrom, messages);
        }

        return single > 0
            ? new MboxDetection(true, MboxVariant.Mboxrd, MboxVariantEvidence.SingleQuotedFrom, messages)
            : new MboxDetection(true, MboxVariant.Unknown, MboxVariantEvidence.None, messages);
    }

    // Counts lines of the form ^>+From in a chunk (a line cut by the chunk edge is missed; this only gathers evidence).
    private static void CountQuoted(ReadOnlySpan<byte> w, ref int multi, ref int single, bool inContentLength, ref int clQuoted)
    {
        int idx = 0;
        while (idx < w.Length)
        {
            int k = w[idx..].IndexOf(">From "u8);
            if (k < 0)
            {
                return;
            }

            int at = idx + k;
            int start = at;
            while (start > 0 && w[start - 1] == (byte)'>')
            {
                start--;
            }

            if (start == 0 || w[start - 1] == (byte)'\n')
            {
                int gt = at - start + 1;
                if (inContentLength)
                {
                    clQuoted++;
                }
                else if (gt >= 2)
                {
                    multi++;
                }
                else
                {
                    single++;
                }
            }

            idx = at + 1;
        }
    }

    // A view of the first part of a source.
    private sealed class PrefixSource(IRandomAccessSource inner, long limit) : IRandomAccessSource
    {
        public long Length { get; } = Math.Min(limit, inner.Length);

        public bool SupportsSyncRead => inner.SupportsSyncRead;

        public int Read(long offset, Span<byte> buffer)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            if (offset >= Length)
            {
                return 0;
            }

            return inner.Read(offset, buffer[..(int)Math.Min(buffer.Length, Length - offset)]);
        }

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            if (offset >= Length)
            {
                return new ValueTask<int>(0);
            }

            return inner.ReadAsync(offset, buffer[..(int)Math.Min(buffer.Length, Length - offset)], cancellationToken);
        }

        public void Dispose()
        {
        }
    }
}
