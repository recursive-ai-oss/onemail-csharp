using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace RecursiveAI.OneMail.Cryptography.Dns;

/// <summary>Settings for <see cref="DnsTxtClient"/>.</summary>
public sealed class DnsTxtClientOptions
{
    /// <summary>The recursive resolvers to ask, in order. Null or empty uses the system's configured resolvers.</summary>
    public IReadOnlyList<IPEndPoint>? Servers { get; init; }

    /// <summary>How long one attempt (one server, one transport) may take. Default 3 seconds.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How many times each server is tried over UDP before the next one. Default 2.</summary>
    public int Attempts { get; init; } = 2;

    /// <summary>The UDP payload size advertised through EDNS0 (RFC 6891). Default 1232, which avoids IP fragmentation. 512 turns EDNS0 sizing off in effect.</summary>
    public ushort UdpPayloadSize { get; init; } = 1232;

    /// <summary>The largest TCP reply accepted, in bytes. Default 65535 (the protocol's maximum).</summary>
    public int MaxTcpResponseBytes { get; init; } = 65535;
}

/// <summary>
/// A small DNS client for TXT records, enough for DKIM and ARC key records: UDP first, TCP when the reply is truncated (RFC 1035 §4.2,
/// RFC 7766). It asks a recursive resolver and trusts it; there is no DNSSEC validation. Replies are checked against the query (id,
/// question, source address) and parsed with every length verified before use, so a hostile reply yields a failed lookup, never an exception.
/// </summary>
public sealed class DnsTxtClient : ITxtResolver
{
    private readonly DnsTxtClientOptions _options;
    private readonly IReadOnlyList<IPEndPoint> _servers;

    /// <summary>Creates a client.</summary>
    /// <param name="options">Settings; null uses the defaults and the system's resolvers.</param>
    public DnsTxtClient(DnsTxtClientOptions? options = null)
    {
        _options = options ?? new DnsTxtClientOptions();
        _servers = _options.Servers is { Count: > 0 } s ? s : SystemServers();
    }

    /// <summary>The servers this client asks.</summary>
    public IReadOnlyList<IPEndPoint> Servers => _servers;

    /// <summary>The resolvers the operating system is configured with (port 53), or empty if it reports none.</summary>
    public static IReadOnlyList<IPEndPoint> SystemServers()
    {
        var list = new List<IPEndPoint>();
        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                foreach (IPAddress a in nic.GetIPProperties().DnsAddresses)
                {
                    // Site-local IPv6 resolver addresses (fec0::/10) are deprecated and usually unroutable.
                    if (a.AddressFamily == AddressFamily.InterNetworkV6 && a.IsIPv6SiteLocal)
                    {
                        continue;
                    }

                    if (!list.Any(e => e.Address.Equals(a)))
                    {
                        list.Add(new IPEndPoint(a, 53));
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
            // No resolver list available: lookups will report a temporary failure.
        }

        return list;
    }

    /// <inheritdoc />
    public async ValueTask<TxtLookupResult> LookupTxtAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        name = name.TrimEnd('.');
        if (_servers.Count == 0)
        {
            return new TxtLookupResult(TxtLookupStatus.TemporaryFailure, null, "no DNS servers configured");
        }

        string detail = "no reply";
        foreach (IPEndPoint server in _servers)
        {
            for (int attempt = 0; attempt < Math.Max(1, _options.Attempts); attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ushort id = (ushort)RandomNumberGenerator.GetInt32(0, 65536);
                if (!DnsWire.TryBuildQuery(name, id, _options.UdpPayloadSize, out byte[] query))
                {
                    return new TxtLookupResult(TxtLookupStatus.NameNotFound, null, "not a valid DNS name");
                }

                DnsReply? reply = await Attempt(server, query, id, name, tcp: false, cancellationToken).ConfigureAwait(false);
                if (reply is { Kind: DnsReplyKind.Truncated })
                {
                    reply = await Attempt(server, query, id, name, tcp: true, cancellationToken).ConfigureAwait(false);
                }

                if (reply is null)
                {
                    detail = "timeout or network error from " + server;
                    continue;
                }

                switch (reply.Kind)
                {
                    case DnsReplyKind.Records:
                        return new TxtLookupResult(TxtLookupStatus.Success, reply.Records);
                    case DnsReplyKind.NameNotFound:
                        return new TxtLookupResult(TxtLookupStatus.NameNotFound);
                    case DnsReplyKind.NoData:
                        return new TxtLookupResult(TxtLookupStatus.NoRecords);
                    default:
                        detail = reply.Detail ?? reply.Kind.ToString();
                        break;
                }

                // A server that answered badly will not do better on a retry; go to the next one.
                if (reply.Kind != DnsReplyKind.Malformed)
                {
                    break;
                }
            }
        }

        return new TxtLookupResult(TxtLookupStatus.TemporaryFailure, null, detail);
    }

    private async Task<DnsReply?> Attempt(IPEndPoint server, byte[] query, ushort id, string name, bool tcp, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.Timeout);
        try
        {
            byte[]? raw = tcp
                ? await QueryTcp(server, query, timeout.Token).ConfigureAwait(false)
                : await QueryUdp(server, query, timeout.Token).ConfigureAwait(false);
            return raw is null ? null : DnsWire.ParseReply(raw, id, name);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private async Task<byte[]?> QueryUdp(IPEndPoint server, byte[] query, CancellationToken ct)
    {
        using var socket = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        // Connecting filters out datagrams from any other source (RFC 5452 §9.2).
        await socket.ConnectAsync(server, ct).ConfigureAwait(false);
        await socket.SendAsync(query, SocketFlags.None, ct).ConfigureAwait(false);
        byte[] buffer = new byte[Math.Max((int)_options.UdpPayloadSize, 512) + 64];
        while (true)
        {
            int n = await socket.ReceiveAsync(buffer, SocketFlags.None, ct).ConfigureAwait(false);
            // Ignore datagrams that cannot be our answer instead of letting an off-path packet end the wait.
            if (n >= 12 && buffer[0] == query[0] && buffer[1] == query[1])
            {
                return buffer.AsSpan(0, n).ToArray();
            }
        }
    }

    private async Task<byte[]?> QueryTcp(IPEndPoint server, byte[] query, CancellationToken ct)
    {
        using var socket = new Socket(server.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(server, ct).ConfigureAwait(false);
        // RFC 1035 §4.2.2: each message is preceded by a two-octet length.
        byte[] framed = new byte[query.Length + 2];
        framed[0] = (byte)(query.Length >> 8);
        framed[1] = (byte)query.Length;
        query.CopyTo(framed, 2);
        await socket.SendAsync(framed, SocketFlags.None, ct).ConfigureAwait(false);
        byte[] lenBuf = new byte[2];
        if (!await ReadExactly(socket, lenBuf, ct).ConfigureAwait(false))
        {
            return null;
        }

        int len = (lenBuf[0] << 8) | lenBuf[1];
        if (len < 12 || len > _options.MaxTcpResponseBytes)
        {
            return null;
        }

        byte[] body = new byte[len];
        return await ReadExactly(socket, body, ct).ConfigureAwait(false) ? body : null;
    }

    private static async Task<bool> ReadExactly(Socket s, byte[] buffer, CancellationToken ct)
    {
        int got = 0;
        while (got < buffer.Length)
        {
            int n = await s.ReceiveAsync(buffer.AsMemory(got), SocketFlags.None, ct).ConfigureAwait(false);
            if (n == 0)
            {
                return false;
            }

            got += n;
        }

        return true;
    }
}
