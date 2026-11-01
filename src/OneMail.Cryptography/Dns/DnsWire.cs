using System.Text;

namespace RecursiveAI.OneMail.Cryptography.Dns;

/// <summary>What a reply parsed as, before it is turned into a <see cref="TxtLookupResult"/>.</summary>
internal enum DnsReplyKind
{
    /// <summary>A usable reply with TXT records.</summary>
    Records,

    /// <summary>NXDOMAIN.</summary>
    NameNotFound,

    /// <summary>The name exists and has no TXT records.</summary>
    NoData,

    /// <summary>The server reported a failure (SERVFAIL, REFUSED, ...).</summary>
    ServerFailure,

    /// <summary>The reply was cut off (TC bit); retry over TCP.</summary>
    Truncated,

    /// <summary>The reply did not answer this query, or was not a well-formed message.</summary>
    Malformed,
}

internal sealed class DnsReply
{
    public DnsReplyKind Kind;
    public int ResponseCode;
    public string? Detail;
    public List<string> Records = [];
}

/// <summary>RFC 1035 message building and parsing for the one question we ask: TXT, class IN. Every length is checked before use.</summary>
internal static class DnsWire
{
    public const int TypeCname = 5;
    public const int TypeTxt = 16;
    public const int TypeOpt = 41;
    public const int ClassIn = 1;

    // RFC 1035 §2.3.4: a name is at most 255 octets on the wire and a label at most 63.
    private const int MaxNameWire = 255;
    private const int MaxLabel = 63;
    private const int MaxAnswersExamined = 128;
    private const int MaxCnameHops = 8;
    private const int MaxPointerJumps = 32;

    /// <summary>Total TXT bytes kept from one reply; a larger answer is a failure, not an allocation.</summary>
    public const int MaxTxtBytes = 65536;

    /// <summary>Builds a recursive query for the TXT records of <paramref name="name"/>, with an EDNS0 OPT record advertising <paramref name="udpPayload"/> (RFC 6891).</summary>
    public static bool TryBuildQuery(string name, ushort id, ushort udpPayload, out byte[] query)
    {
        query = [];
        if (name.Length == 0 || name.Length > 253)
        {
            return false;
        }

        var buf = new List<byte>(name.Length + 40)
        {
            (byte)(id >> 8), (byte)id,
            0x01, 0x00, // QR=0 opcode=0 RD=1
            0, 1, 0, 0, 0, 0, 0, 1, // QDCOUNT=1 ARCOUNT=1
        };
        int labelStart = 0;
        for (int i = 0; i <= name.Length; i++)
        {
            if (i < name.Length && name[i] != '.')
            {
                if (name[i] > 0x7E || name[i] < 0x21)
                {
                    return false;
                }

                continue;
            }

            int len = i - labelStart;
            if (len == 0 || len > MaxLabel)
            {
                return false;
            }

            buf.Add((byte)len);
            for (int j = labelStart; j < i; j++)
            {
                buf.Add((byte)name[j]);
            }

            labelStart = i + 1;
        }

        buf.Add(0);
        buf.AddRange([0, TypeTxt, 0, ClassIn]);
        // OPT pseudo-record: root name, type 41, class = UDP payload size, ttl 0, no rdata.
        buf.AddRange([0, 0, TypeOpt, (byte)(udpPayload >> 8), (byte)udpPayload, 0, 0, 0, 0, 0, 0]);
        query = [.. buf];
        return true;
    }

    /// <summary>Parses a reply to a query made by <see cref="TryBuildQuery"/>. Never throws on data.</summary>
    public static DnsReply ParseReply(ReadOnlySpan<byte> m, ushort expectedId, string qname)
    {
        var reply = new DnsReply();
        if (m.Length < 12)
        {
            return Bad(reply, "short message");
        }

        if (((m[0] << 8) | m[1]) != expectedId)
        {
            return Bad(reply, "id mismatch");
        }

        int flags = (m[2] << 8) | m[3];
        if ((flags & 0x8000) == 0 || ((flags >> 11) & 0xF) != 0)
        {
            return Bad(reply, "not a standard-query response");
        }

        int rcode = flags & 0xF;
        reply.ResponseCode = rcode;
        int qd = (m[4] << 8) | m[5];
        int an = (m[6] << 8) | m[7];
        if (qd != 1)
        {
            return Bad(reply, "question count");
        }

        int pos = 12;
        if (!TryReadName(m, pos, out string qn, out pos) || pos + 4 > m.Length)
        {
            return Bad(reply, "bad question");
        }

        int qtype = (m[pos] << 8) | m[pos + 1];
        int qclass = (m[pos + 2] << 8) | m[pos + 3];
        pos += 4;
        if (qtype != TypeTxt || qclass != ClassIn || !string.Equals(qn, qname, StringComparison.OrdinalIgnoreCase))
        {
            return Bad(reply, "question mismatch");
        }

        // A truncated reply is only a hint to retry over TCP; whatever it holds is not trusted.
        if ((flags & 0x0200) != 0)
        {
            reply.Kind = DnsReplyKind.Truncated;
            return reply;
        }

        if (rcode == 3)
        {
            reply.Kind = DnsReplyKind.NameNotFound;
            return reply;
        }

        if (rcode != 0)
        {
            reply.Kind = DnsReplyKind.ServerFailure;
            reply.Detail = "rcode " + rcode;
            return reply;
        }

        var cnames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var txts = new List<(string Owner, string Text)>();
        int totalTxt = 0;
        for (int i = 0; i < an && i < MaxAnswersExamined; i++)
        {
            if (!TryReadName(m, pos, out string owner, out pos) || pos + 10 > m.Length)
            {
                return Bad(reply, "bad answer");
            }

            int type = (m[pos] << 8) | m[pos + 1];
            int cls = (m[pos + 2] << 8) | m[pos + 3];
            int rdlen = (m[pos + 8] << 8) | m[pos + 9];
            pos += 10;
            if (pos + rdlen > m.Length)
            {
                return Bad(reply, "rdata past end");
            }

            ReadOnlySpan<byte> rdata = m.Slice(pos, rdlen);
            if (cls == ClassIn && type == TypeCname)
            {
                if (TryReadName(m, pos, out string target, out int after) && after == pos + rdlen)
                {
                    cnames.TryAdd(owner, target);
                }
            }
            else if (cls == ClassIn && type == TypeTxt)
            {
                // RFC 1035 §3.3.14: TXT-DATA is one or more <character-string>s, each a length octet and that many bytes. A record whose strings do
                // not exactly fill RDLENGTH is dropped rather than guessed at.
                if (TryJoinStrings(rdata, out string text))
                {
                    totalTxt += text.Length;
                    if (totalTxt > MaxTxtBytes)
                    {
                        reply.Kind = DnsReplyKind.ServerFailure;
                        reply.Detail = "answer too large";
                        return reply;
                    }

                    txts.Add((owner, text));
                }
            }

            pos += rdlen;
        }

        string current = qname;
        for (int hop = 0; hop < MaxCnameHops && cnames.TryGetValue(current, out string? next); hop++)
        {
            current = next;
        }

        foreach ((string owner, string text) in txts)
        {
            if (string.Equals(owner, current, StringComparison.OrdinalIgnoreCase))
            {
                reply.Records.Add(text);
            }
        }

        reply.Kind = reply.Records.Count > 0 ? DnsReplyKind.Records : DnsReplyKind.NoData;
        return reply;
    }

    private static DnsReply Bad(DnsReply r, string why)
    {
        r.Kind = DnsReplyKind.Malformed;
        r.Detail = why;
        return r;
    }

    private static bool TryJoinStrings(ReadOnlySpan<byte> rdata, out string text)
    {
        text = string.Empty;
        if (rdata.Length == 0)
        {
            return false;
        }

        var sb = new StringBuilder(rdata.Length);
        int p = 0;
        while (p < rdata.Length)
        {
            int len = rdata[p++];
            if (p + len > rdata.Length)
            {
                return false;
            }

            sb.Append(Encoding.Latin1.GetString(rdata.Slice(p, len)));
            p += len;
        }

        text = sb.ToString();
        return true;
    }

    /// <summary>Reads a possibly compressed name (RFC 1035 §4.1.4) as a lower-case dotted string without the root dot.</summary>
    internal static bool TryReadName(ReadOnlySpan<byte> m, int start, out string name, out int next)
    {
        name = string.Empty;
        next = start;
        var sb = new StringBuilder();
        int pos = start;
        int jumps = 0;
        bool jumped = false;
        int wire = 0;
        while (true)
        {
            if (pos >= m.Length)
            {
                return false;
            }

            int len = m[pos];
            if ((len & 0xC0) == 0xC0)
            {
                if (pos + 1 >= m.Length)
                {
                    return false;
                }

                int target = ((len & 0x3F) << 8) | m[pos + 1];
                if (!jumped)
                {
                    next = pos + 2;
                    jumped = true;
                }

                // A pointer must go backwards; that alone rules out loops, the jump limit bounds the work anyway.
                if (target >= pos || ++jumps > MaxPointerJumps)
                {
                    return false;
                }

                pos = target;
                continue;
            }

            if ((len & 0xC0) != 0)
            {
                return false;
            }

            pos++;
            if (len == 0)
            {
                if (!jumped)
                {
                    next = pos;
                }

                name = sb.ToString();
                return true;
            }

            wire += len + 1;
            if (len > MaxLabel || pos + len > m.Length || wire > MaxNameWire)
            {
                return false;
            }

            if (sb.Length > 0)
            {
                sb.Append('.');
            }

            for (int i = 0; i < len; i++)
            {
                byte b = m[pos + i];
                sb.Append(b is >= (byte)'A' and <= (byte)'Z' ? (char)(b + 32) : (char)b);
            }

            pos += len;
        }
    }
}
