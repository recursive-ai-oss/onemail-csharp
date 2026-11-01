using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneMail.Cryptography.Arc;
using RecursiveAI.OneMail.Cryptography.Dkim;
using RecursiveAI.OneMail.Cryptography.Dns;

namespace RecursiveAI.OneMail.Fuzz;

// DKIM and ARC parsers on hostile input. Only typed results may come out: signature headers, key records and DNS replies are
// attacker-controlled, so none of them may throw, hang or allocate without bound.
internal static class DkimTargets
{
    private sealed class AnyKeyResolver : ITxtResolver
    {
        public static readonly string Record = MakeRecord();

        private static string MakeRecord()
        {
            using var rsa = System.Security.Cryptography.RSA.Create(2048);
            return "v=DKIM1; k=rsa; p=" + Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
        }

        public ValueTask<TxtLookupResult> LookupTxtAsync(string name, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new TxtLookupResult(TxtLookupStatus.Success, [Record]));
    }

    private static readonly DkimVerifierOptions Options = new() { Resolver = new AnyKeyResolver(), TimeProvider = TimeProvider.System };

    /// <summary>The input is the value of a DKIM-Signature header, and also of the three ARC headers.</summary>
    public static void Tags(ReadOnlySpan<byte> data)
    {
        string value = Encoding.Latin1.GetString(data);
        _ = TagList.TryParse(value, out _, out _, out _);
        string safe = value.Replace('\r', ' ').Replace('\n', ' ');
        byte[] message = Encoding.Latin1.GetBytes(
            "DKIM-Signature: " + safe + "\r\nARC-Seal: " + safe + "\r\nARC-Message-Signature: " + safe + "\r\nARC-Authentication-Results: " + safe +
            "\r\nFrom: a@example.com\r\nSubject: x\r\n\r\nbody\r\n");
        IReadOnlyList<DkimSignatureResult> r = new DkimVerifier(Options).VerifyAsync(message).GetAwaiter().GetResult();
        foreach (DkimSignatureResult s in r)
        {
            if (s.Status == DkimStatus.Pass)
            {
                throw new InvalidOperationException("a signature made from fuzz input verified");
            }
        }

        ArcVerificationResult a = new ArcVerifier(Options).VerifyAsync(message).GetAwaiter().GetResult();
        if (a.Status == ArcChainStatus.Pass)
        {
            throw new InvalidOperationException("an ARC chain made from fuzz input validated");
        }
    }

    /// <summary>The input is a key record as it comes out of DNS.</summary>
    public static void KeyRecord(ReadOnlySpan<byte> data)
    {
        if (DkimKeyRecord.TryParse(Encoding.Latin1.GetString(data), out DkimKeyRecord? k, out DkimKeyError e, out _))
        {
            if (k!.KeyBits > DkimKeyRecord.MaxRsaBits || (k.Revoked && k.KeyBits != 0))
            {
                throw new InvalidOperationException("inconsistent key record");
            }
        }
        else if (e == DkimKeyError.None)
        {
            throw new InvalidOperationException("failure without an error");
        }
    }

    /// <summary>The first two bytes are the query id, the rest is a DNS reply to a query for x.example.com.</summary>
    public static void DnsReply(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            return;
        }

        DnsReply r = DnsWire.ParseReply(data[2..], (ushort)((data[0] << 8) | data[1]), "x.example.com");
        if (r.Records.Sum(x => x.Length) > DnsWire.MaxTxtBytes)
        {
            throw new InvalidOperationException("answer larger than the limit");
        }
    }
}
