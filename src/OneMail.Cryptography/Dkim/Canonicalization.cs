using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace RecursiveAI.OneMail.Cryptography.Dkim;

/// <summary>One header field of a message: its position in the source bytes and its name.</summary>
internal readonly record struct HeaderField(int Start, int End, int Colon, string Name);

/// <summary>
/// A message cut into header fields and body without changing a byte: DKIM and ARC canonicalise the bytes as they are, never a re-serialised tree.
/// Line ends may be CRLF or bare LF; canonicalisation writes CRLF (RFC 6376 §3.4 works on CRLF text, and a message that crossed a transport
/// as LF is signed and checked as the CRLF form it travels in).
/// </summary>
internal sealed class MessageParts
{
    public MessageParts(ReadOnlyMemory<byte> bytes)
    {
        Bytes = bytes;
        ReadOnlySpan<byte> m = bytes.Span;
        int pos = 0;
        HeaderField? open = null;
        int openStart = 0;
        int openColon = -1;
        string openName = string.Empty;
        while (pos < m.Length)
        {
            int nl = m[pos..].IndexOf((byte)'\n');
            int lineEnd = nl < 0 ? m.Length : pos + nl + 1;
            int contentEnd = nl < 0 ? m.Length : pos + nl;
            if (contentEnd > pos && m[contentEnd - 1] == '\r')
            {
                contentEnd--;
            }

            if (contentEnd == pos)
            {
                // The empty line ends the header section (RFC 5322 §2.1).
                Close();
                BodyStart = lineEnd;
                return;
            }

            if (m[pos] is (byte)' ' or (byte)'\t')
            {
                // A continuation line (folding). A continuation with no field before it belongs to nothing and is skipped.
                if (open is not null)
                {
                    open = new HeaderField(openStart, lineEnd, openColon, openName);
                }
            }
            else
            {
                Close();
                int colon = m[pos..contentEnd].IndexOf((byte)':');
                if (colon > 0)
                {
                    int ne = pos + colon;
                    while (ne > pos && m[ne - 1] is (byte)' ' or (byte)'\t')
                    {
                        ne--;
                    }

                    openStart = pos;
                    openColon = pos + colon;
                    openName = Encoding.Latin1.GetString(m[pos..ne]);
                    open = new HeaderField(pos, lineEnd, openColon, openName);
                }
                else
                {
                    // Not a field (an mbox "From " line, junk): it is not hashed and ends nothing.
                    open = null;
                }
            }

            pos = lineEnd;
        }

        Close();
        BodyStart = m.Length;

        void Close()
        {
            if (open is { } h)
            {
                Headers.Add(h);
                open = null;
            }
        }
    }

    public ReadOnlyMemory<byte> Bytes { get; }

    public List<HeaderField> Headers { get; } = [];

    /// <summary>Offset of the first body byte (after the blank line), or the message length if there is no body.</summary>
    public int BodyStart { get; private set; }

    public ReadOnlySpan<byte> Raw(HeaderField h) => Bytes.Span[h.Start..h.End];

    /// <summary>The header's value text as Latin-1 (every byte one char, so offsets in the string are offsets in the bytes), unfolded of nothing, without the name, colon or final line end.</summary>
    public string ValueText(HeaderField h)
    {
        ReadOnlySpan<byte> raw = Bytes.Span[(h.Colon + 1)..h.End];
        return Encoding.Latin1.GetString(TrimEol(raw));
    }

    public static ReadOnlySpan<byte> TrimEol(ReadOnlySpan<byte> s)
    {
        if (s.Length > 0 && s[^1] == '\n')
        {
            s = s[..^1];
            if (s.Length > 0 && s[^1] == '\r')
            {
                s = s[..^1];
            }
        }

        return s;
    }
}

/// <summary>Receives canonicalised body bytes, hashing the first <c>limit</c> of them and counting all.</summary>
internal sealed class BodySink : IDisposable
{
    private readonly IncrementalHash _hash;
    private readonly long _limit;
    private byte[]? _buffer = ArrayPool<byte>.Shared.Rent(8192);
    private int _used;

    public BodySink(HashAlgorithmName alg, long limit)
    {
        _hash = IncrementalHash.CreateHash(alg);
        _limit = limit;
    }

    /// <summary>All canonicalised bytes seen, hashed or not.</summary>
    public long Total { get; private set; }

    /// <summary>Canonicalised bytes that went into the hash.</summary>
    public long Hashed { get; private set; }

    /// <summary>The source offset the current line ends at; copied to <see cref="CoveredSourceEnd"/> whenever hashed bytes come from it.</summary>
    public int SourceMark { get; set; }

    public int CoveredSourceEnd { get; private set; }

    public void Append(ReadOnlySpan<byte> s)
    {
        Total += s.Length;
        int take = s.Length;
        if (_limit >= 0)
        {
            take = (int)Math.Min(s.Length, Math.Max(0, _limit - Hashed));
        }

        if (take == 0)
        {
            return;
        }

        CoveredSourceEnd = SourceMark;
        Hashed += take;
        s = s[..take];
        if (s.Length > _buffer!.Length - _used)
        {
            Flush();
            if (s.Length > _buffer.Length)
            {
                _hash.AppendData(s);
                return;
            }
        }

        s.CopyTo(_buffer.AsSpan(_used));
        _used += s.Length;
    }

    private void Flush()
    {
        if (_used > 0)
        {
            _hash.AppendData(_buffer!, 0, _used);
            _used = 0;
        }
    }

    public byte[] Finish()
    {
        Flush();
        return _hash.GetHashAndReset();
    }

    public void Dispose()
    {
        _hash.Dispose();
        if (_buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = null;
        }
    }
}

/// <summary>RFC 6376 §3.4 canonicalisation of headers and bodies.</summary>
internal static class DkimCanonicalizer
{
    private static readonly byte[] Crlf = [(byte)'\r', (byte)'\n'];

    private static bool IsWsp(byte b) => b is (byte)' ' or (byte)'\t';

    /// <summary>Appends a header field in canonical form. <paramref name="field"/> is the raw field from its first byte through its line end.</summary>
    public static void AppendHeader(List<byte> output, ReadOnlySpan<byte> field, DkimCanonicalization canon, bool trailingCrlf)
    {
        ReadOnlySpan<byte> body = MessageParts.TrimEol(field);
        if (canon == DkimCanonicalization.Simple)
        {
            // RFC 6376 §3.4.1: unchanged, except that line ends are CRLF.
            for (int i = 0; i < body.Length; i++)
            {
                if (body[i] == '\n' && (i == 0 || body[i - 1] != '\r'))
                {
                    output.Add((byte)'\r');
                }

                output.Add(body[i]);
            }

            if (trailingCrlf)
            {
                output.AddRange(Crlf);
            }

            return;
        }

        // RFC 6376 §3.4.2: lower-case the name, drop whitespace around the colon, unfold, collapse whitespace runs, trim the value.
        int colon = body.IndexOf((byte)':');
        if (colon < 0)
        {
            colon = body.Length;
        }

        ReadOnlySpan<byte> name = body[..colon];
        int ne = name.Length;
        while (ne > 0 && IsWsp(name[ne - 1]))
        {
            ne--;
        }

        for (int i = 0; i < ne; i++)
        {
            byte c = name[i];
            output.Add(c is >= (byte)'A' and <= (byte)'Z' ? (byte)(c + 32) : c);
        }

        output.Add((byte)':');
        if (colon < body.Length)
        {
            ReadOnlySpan<byte> value = body[(colon + 1)..];
            bool pendingSpace = false;
            bool any = false;
            for (int i = 0; i < value.Length; i++)
            {
                byte c = value[i];
                if (c == '\n' || (c == '\r' && i + 1 < value.Length && value[i + 1] == '\n'))
                {
                    continue; // unfolding removes the CRLF; the whitespace after it stays and collapses below
                }

                if (IsWsp(c))
                {
                    pendingSpace = any;
                    continue;
                }

                if (pendingSpace)
                {
                    output.Add((byte)' ');
                    pendingSpace = false;
                }

                output.Add(c);
                any = true;
            }
        }

        if (trailingCrlf)
        {
            output.AddRange(Crlf);
        }
    }

    /// <summary>Canonicalises <paramref name="body"/> into <paramref name="sink"/>. <paramref name="bodyStart"/> is the body's offset in the message, for source tracking.</summary>
    public static void CanonicalizeBody(ReadOnlySpan<byte> body, int bodyStart, DkimCanonicalization canon, BodySink sink)
    {
        int pendingEmpty = 0;
        bool emitted = false;
        int pos = 0;
        byte[] lineBuf = ArrayPool<byte>.Shared.Rent(1024);
        try
        {
            while (pos < body.Length)
            {
                int nl = body[pos..].IndexOf((byte)'\n');
                int lineEnd = nl < 0 ? body.Length : pos + nl + 1;
                int contentEnd = nl < 0 ? body.Length : pos + nl;
                if (contentEnd > pos && body[contentEnd - 1] == '\r' && nl >= 0)
                {
                    contentEnd--;
                }

                ReadOnlySpan<byte> line = body[pos..contentEnd];
                sink.SourceMark = bodyStart + lineEnd;
                if (canon == DkimCanonicalization.Relaxed)
                {
                    // RFC 6376 §3.4.4: strip trailing whitespace, collapse whitespace runs to one space.
                    if (lineBuf.Length < line.Length)
                    {
                        ArrayPool<byte>.Shared.Return(lineBuf);
                        lineBuf = ArrayPool<byte>.Shared.Rent(line.Length);
                    }

                    int n = 0;
                    bool space = false;
                    foreach (byte c in line)
                    {
                        if (IsWsp(c))
                        {
                            space = true;
                            continue;
                        }

                        if (space)
                        {
                            lineBuf[n++] = (byte)' ';
                            space = false;
                        }

                        lineBuf[n++] = c;
                    }

                    // A line of nothing but whitespace is empty here, so it counts towards the trailing empty lines that are dropped.
                    line = lineBuf.AsSpan(0, n);
                }

                if (line.Length == 0)
                {
                    pendingEmpty++;
                }
                else
                {
                    for (; pendingEmpty > 0; pendingEmpty--)
                    {
                        sink.Append(Crlf);
                    }

                    sink.Append(line);
                    sink.Append(Crlf);
                    emitted = true;
                }

                pos = lineEnd;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(lineBuf);
        }

        // RFC 6376 §3.4.3: an empty body (after dropping trailing empty lines) is a single CRLF in "simple"; §3.4.4: it is empty in "relaxed".
        if (!emitted && canon == DkimCanonicalization.Simple)
        {
            sink.SourceMark = bodyStart + body.Length;
            sink.Append(Crlf);
        }
    }
}
