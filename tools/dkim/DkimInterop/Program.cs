using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RecursiveAI.OneMail.Cryptography.Arc;
using RecursiveAI.OneMail.Cryptography.Dkim;
using RecursiveAI.OneMail.Cryptography.Dns;

// DKIM interoperability harness.
//
//   DkimInterop gen <dir>                          keys, DNS records, the case list, unsigned messages, and the messages this library signed
//   DkimInterop verify <dir> <subdir> <out.json>   verify every message in <dir>/<subdir> with this library
//   DkimInterop compare <dir> <oracle>...          compare what each oracle said about our signatures and what we say about theirs
//
// Oracle drivers (dkimpy_driver.py, mailauth_driver.js) read <dir>/cases.json and <dir>/dns.json, verify <dir>/ours-signed and
// write <dir>/<oracle>-verifies-ours.json; they also sign <dir>/unsigned into <dir>/<oracle>-signed.
internal static class Program
{
    private static readonly string[] Bodies = ["ascii", "empty", "whitespace", "utf8", "multipart", "folded", "nolf", "duplicates", "lf", "blank"];

    private static async Task<int> Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: DkimInterop gen|verify|compare <dir> ...");
            return 2;
        }

        string dir = args[1];
        switch (args[0])
        {
            case "gen": Gen(dir); return 0;
            case "verify": return await Verify(dir, args[2], args[3]);
            case "compare": return Compare(dir, args[2..]);
            case "arcsuite": return await ArcSuite(dir);
            case "arcgen": return await ArcGen(dir);
            case "arcverify": return await ArcVerify(dir, args[2], args[3]);
            case "arcover": return await ArcOver(dir, args[2], args[3]);
            default: return 2;
        }
    }

    private static byte[] Message(string body)
    {
        const string headers =
            "Received: from a.example (a.example [192.0.2.1])\r\n\tby mx.example.com with ESMTP; Mon, 1 Jan 2024 00:00:00 +0000\r\n" +
            "From: Joe <joe@example.com>\r\nTo: Suzie <suzie@example.net>\r\nSubject: Interop test\r\nDate: Mon, 1 Jan 2024 00:00:00 +0000\r\n" +
            "Message-ID: <interop@example.com>\r\nMIME-Version: 1.0\r\n";
        string text = body switch
        {
            "empty" => headers + "Content-Type: text/plain\r\n\r\n",
            "whitespace" => headers + "Content-Type: text/plain\r\n\r\nline with trailing   \r\n\ttabbed\t \r\nmany    inner   spaces\r\n   \r\n\r\n\r\n",
            "utf8" => headers + "Content-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: 8bit\r\n\r\nGr\u00fc\u00dfe aus M\u00fcnchen \u2014 \u65e5\u672c\u8a9e\r\nSecond line\r\n",
            "multipart" => headers + "Content-Type: multipart/mixed; boundary=\"b1\"\r\n\r\n--b1\r\nContent-Type: text/plain\r\n\r\nSee attached.\r\n--b1\r\nContent-Type: application/octet-stream\r\nContent-Transfer-Encoding: base64\r\nContent-Disposition: attachment; filename=a.bin\r\n\r\n" +
                string.Join("\r\n", Enumerable.Range(0, 20).Select(i => Convert.ToBase64String(Enumerable.Range(i, 57).Select(x => (byte)(x * 7)).ToArray()))) + "\r\n--b1--\r\n",
            "folded" => headers.Replace("Subject: Interop test", "Subject: Interop\r\n\t  test with   a\r\n  folded subject", StringComparison.Ordinal) +
                "Cc: a@example.net,\r\n b@example.net,\r\n\tc@example.net\r\nContent-Type: text/plain\r\n\r\nbody\r\n",
            "nolf" => headers + "Content-Type: text/plain\r\n\r\nno final line end",
            "duplicates" => "Received: from x\r\nReceived: from y\r\n" + headers + "Comments: one\r\nComments: two\r\nContent-Type: text/plain\r\n\r\nbody\r\n",
            "blank" => headers + "Content-Type: text/plain\r\n\r\n\r\n\r\n\r\n",
            _ => headers + "Content-Type: text/plain\r\n\r\nHello,\r\n\r\nThis is the body.  Two spaces above.\r\nLast line.\r\n",
        };
        if (body == "lf")
        {
            text = headers.Replace("\r\n", "\n", StringComparison.Ordinal) + "Content-Type: text/plain\n\nlf only\nsecond\n";
        }

        return body == "utf8" ? Encoding.UTF8.GetBytes(text) : Encoding.Latin1.GetBytes(text);
    }

    private static void Gen(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (string sub in new[] { "unsigned", "ours-signed", "keys" })
        {
            Directory.CreateDirectory(Path.Combine(dir, sub));
        }

        using var rsa = RSA.Create(2048);
        byte[] seed = SHA256.HashData("onemail dkim interop"u8);
        byte[] edPkcs8 = [0x30, 0x2E, 0x02, 0x01, 0x00, 0x30, 0x05, 0x06, 0x03, 0x2B, 0x65, 0x70, 0x04, 0x22, 0x04, 0x20, .. seed];
        File.WriteAllText(Path.Combine(dir, "keys", "rsa.pem"), rsa.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(Path.Combine(dir, "keys", "ed.pem"), "-----BEGIN PRIVATE KEY-----\n" + Convert.ToBase64String(edPkcs8) + "\n-----END PRIVATE KEY-----\n");
        File.WriteAllText(Path.Combine(dir, "keys", "ed.seed"), Convert.ToBase64String(seed));
        using DkimSigningKey rsaKey = DkimSigningKey.FromRsa(rsa);
        using DkimSigningKey edKey = DkimSigningKey.FromEd25519Seed(seed);
        var dns = new JsonObject
        {
            ["rsa._domainkey.example.com"] = new JsonArray(rsaKey.ToKeyRecord()),
            ["ed._domainkey.example.com"] = new JsonArray(edKey.ToKeyRecord()),
        };
        File.WriteAllText(Path.Combine(dir, "dns.json"), dns.ToJsonString());

        var cases = new JsonArray();
        int n = 0;
        foreach (string body in Bodies)
        {
            foreach (string alg in new[] { "rsa", "ed" })
            {
                foreach (string canon in new[] { "simple/simple", "simple/relaxed", "relaxed/simple", "relaxed/relaxed" })
                {
                    foreach (bool length in new[] { false, true })
                    {
                        foreach (bool oversign in new[] { false, true })
                        {
                            string id = $"c{n++:D4}";
                            cases.Add(new JsonObject { ["id"] = id, ["body"] = body, ["alg"] = alg, ["canon"] = canon, ["length"] = length, ["oversign"] = oversign, ["selector"] = alg });
                            byte[] unsigned = Message(body);
                            File.WriteAllBytes(Path.Combine(dir, "unsigned", id + ".eml"), unsigned);
                            string[] c = canon.Split('/');
                            var signer = new DkimSigner(alg == "rsa" ? rsaKey : edKey, new DkimSignerOptions
                            {
                                Domain = "example.com",
                                Selector = alg,
                                HeaderCanonicalization = c[0] == "simple" ? DkimCanonicalization.Simple : DkimCanonicalization.Relaxed,
                                BodyCanonicalization = c[1] == "simple" ? DkimCanonicalization.Simple : DkimCanonicalization.Relaxed,
                                IncludeBodyLength = length,
                                OverSign = oversign,
                            });
                            File.WriteAllBytes(Path.Combine(dir, "ours-signed", id + ".eml"), signer.Sign(unsigned));
                        }
                    }
                }
            }
        }

        File.WriteAllText(Path.Combine(dir, "cases.json"), cases.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{n} cases in {dir}");
    }

    private static async Task<int> Verify(string dir, string sub, string output)
    {
        var dnsDoc = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "dns.json")))!.AsObject();
        var resolver = new InMemoryTxtResolver();
        foreach (KeyValuePair<string, JsonNode?> kv in dnsDoc)
        {
            resolver.Add(kv.Key, kv.Value!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
        }

        var verifier = new DkimVerifier(new DkimVerifierOptions { Resolver = resolver });
        var result = new JsonObject();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(dir, sub), "*.eml").Order(StringComparer.Ordinal))
        {
            var list = new JsonArray();
            foreach (DkimSignatureResult r in await verifier.VerifyAsync(File.ReadAllBytes(file)))
            {
                list.Add(new JsonObject { ["status"] = r.Status.ToString().ToLowerInvariant(), ["d"] = r.Domain, ["s"] = r.Selector, ["reason"] = r.Reason.ToString(), ["detail"] = r.Detail, ["l"] = r.BodyLengthTag?.ToString() });
            }

            result[Path.GetFileNameWithoutExtension(file)] = list;
        }

        File.WriteAllText(output, result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"verified {result.Count} messages -> {output}");
        return 0;
    }

    private static InMemoryTxtResolver ArcResolver(string dir, out DkimSigningKey rsaKey, out DkimSigningKey edKey)
    {
        rsaKey = DkimSigningKey.FromPem(File.ReadAllText(Path.Combine(dir, "keys", "rsa.pem")));
        edKey = DkimSigningKey.FromEd25519Seed(Convert.FromBase64String(File.ReadAllText(Path.Combine(dir, "keys", "ed.seed"))));
        return new InMemoryTxtResolver().Add("arc._domainkey.example.net", rsaKey.ToKeyRecord()).Add("arced._domainkey.example.org", edKey.ToKeyRecord());
    }

    private static string ArcDns(DkimSigningKey rsaKey, DkimSigningKey edKey) =>
        new JsonObject { ["arc._domainkey.example.net"] = new JsonArray(rsaKey.ToKeyRecord()), ["arced._domainkey.example.org"] = new JsonArray(edKey.ToKeyRecord()) }.ToJsonString();

    // Messages sealed by this library: one to three hops with both algorithms, and a hop that edits the body after validating the chain.
    private static async Task<int> ArcGen(string dir)
    {
        string outDir = Path.Combine(dir, "arc");
        foreach (string sub in new[] { "unsigned", "ours-sealed" })
        {
            Directory.CreateDirectory(Path.Combine(outDir, sub));
        }

        InMemoryTxtResolver dns = ArcResolver(dir, out DkimSigningKey rsa, out DkimSigningKey ed);
        File.WriteAllText(Path.Combine(outDir, "dns.json"), ArcDns(rsa, ed));
        var verification = new DkimVerifierOptions { Resolver = dns };
        byte[] baseMsg = Message("ascii");
        File.WriteAllBytes(Path.Combine(outDir, "unsigned", "base.eml"), baseMsg);
        async Task<byte[]> Hop(byte[] m, DkimSigningKey k, string domain, string sel, ArcChainStatus? cv = null) =>
            (await new ArcSealer(k, new ArcSealerOptions { Domain = domain, Selector = sel, Verification = verification }).SealAsync(m, $"mx.{domain}; spf=pass smtp.mailfrom=joe@example.com", cv)).Message;
        byte[] r1 = await Hop(baseMsg, rsa, "example.net", "arc");
        byte[] e1 = await Hop(baseMsg, ed, "example.org", "arced");
        byte[] re = await Hop(r1, ed, "example.org", "arced");
        byte[] er = await Hop(e1, rsa, "example.net", "arc");
        byte[] rer = await Hop(re, rsa, "example.net", "arc");
        byte[] edited = await Hop(Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(r1).Replace("Last line.", "Last line, edited.", StringComparison.Ordinal)), ed, "example.org", "arced", ArcChainStatus.Pass);
        foreach ((string n, byte[] m) in new[] { ("rsa1", r1), ("ed1", e1), ("rsa-ed", re), ("ed-rsa", er), ("rsa-ed-rsa", rer), ("edited", edited) })
        {
            File.WriteAllBytes(Path.Combine(outDir, "ours-sealed", n + ".eml"), m);
        }

        Console.WriteLine("6 sealed messages");
        return 0;
    }

    private static async Task<int> ArcVerify(string dir, string sub, string output)
    {
        string arcDir = Path.Combine(dir, "arc");
        var dnsDoc = JsonNode.Parse(File.ReadAllText(Path.Combine(arcDir, "dns.json")))!.AsObject();
        var resolver = new InMemoryTxtResolver();
        foreach (KeyValuePair<string, JsonNode?> kv in dnsDoc)
        {
            resolver.Add(kv.Key, kv.Value!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
        }

        var result = new JsonObject();
        foreach (string f in Directory.EnumerateFiles(Path.Combine(arcDir, sub), "*.eml").Order(StringComparer.Ordinal))
        {
            ArcVerificationResult r = await new ArcVerifier(new DkimVerifierOptions { Resolver = resolver }).VerifyAsync(File.ReadAllBytes(f));
            result[Path.GetFileNameWithoutExtension(f)] = r.Status.ToString().ToLowerInvariant() + (r.Status == ArcChainStatus.Fail ? " (" + r.Detail + ")" : " n=" + r.InstanceCount + " oldest-pass=" + r.OldestPass);
        }

        File.WriteAllText(output, result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(string.Join(Environment.NewLine, result.Select(kv => $"  ours on {sub}/{kv.Key}: {kv.Value}")));
        return 0;
    }

    // Seals the messages another implementation sealed, one more hop on top (RSA with the first key).
    private static async Task<int> ArcOver(string dir, string sub, string outSub)
    {
        string arcDir = Path.Combine(dir, "arc");
        Directory.CreateDirectory(Path.Combine(arcDir, outSub));
        InMemoryTxtResolver dns = ArcResolver(dir, out DkimSigningKey rsa, out DkimSigningKey ed);
        foreach (string f in Directory.EnumerateFiles(Path.Combine(arcDir, sub), "*.eml").Order(StringComparer.Ordinal))
        {
            DkimSigningKey k = Path.GetFileName(f).Contains("ed", StringComparison.Ordinal) ? rsa : ed;
            string domain = k == rsa ? "example.net" : "example.org";
            ArcSealResult r = await new ArcSealer(k, new ArcSealerOptions { Domain = domain, Selector = k == rsa ? "arc" : "arced", Verification = new DkimVerifierOptions { Resolver = dns } }).SealAsync(File.ReadAllBytes(f), "mx." + domain + "; arc=pass");
            File.WriteAllBytes(Path.Combine(arcDir, outSub, Path.GetFileName(f)), r.Message);
            Console.WriteLine($"  sealed {sub}/{Path.GetFileName(f)}: sealed={r.Sealed} i={r.Instance} cv={r.ChainStatus}");
        }

        return 0;
    }

    private static byte[] Crlf(string s) => Encoding.UTF8.GetBytes(System.Text.RegularExpressions.Regex.Replace(s, "\r?\n", "\r\n", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(5)));

    private static string HeaderValue(string name, string value) => name + ": " + string.Join("\r\n ", value.TrimEnd('\n').Split('\n')) + "\r\n";

    // The ARC test suite's scenarios (suite.json from arc_suite_to_json.py): validation messages with an expected chain status, and
    // signing scenarios whose expected ARC set (made by another implementation with a known key) must verify here.
    private static async Task<int> ArcSuite(string file)
    {
        var tests = JsonNode.Parse(File.ReadAllText(file))!.AsArray();
        int pass = 0, fail = 0;
        var wrong = new List<string>();
        foreach (JsonNode? t in tests)
        {
            var resolver = new InMemoryTxtResolver();
            foreach (KeyValuePair<string, JsonNode?> kv in t!["txt"]!.AsObject())
            {
                resolver.Add(kv.Key, kv.Value!.GetValue<string>());
            }

            string kind = t["kind"]!.GetValue<string>();
            string message = t["message"]!.GetValue<string>();
            string expected;
            if (kind == "sign")
            {
                message = HeaderValue("ARC-Seal", t["AS"]!.GetValue<string>()) + HeaderValue("ARC-Message-Signature", t["AMS"]!.GetValue<string>()) + HeaderValue("ARC-Authentication-Results", t["AAR"]!.GetValue<string>()) + message;
                expected = "pass-or-none";
            }
            else
            {
                expected = t["cv"]!.GetValue<string>();
            }

            // The suite's signatures carry t= values from 2017 and no x=; the clock does not matter.
            ArcVerificationResult r = await new ArcVerifier(new DkimVerifierOptions { Resolver = resolver }).VerifyAsync(Crlf(message));
            string got = r.Status.ToString().ToLowerInvariant();
            bool ok = kind == "sign" ? got == "pass" : got == expected || (expected.Length == 0 && got == "fail");
            if (ok)
            {
                pass++;
            }
            else
            {
                fail++;
                wrong.Add($"  {kind} {t["name"]} [{t["spec"]}] {t["description"]}: expected {expected}, got {got} ({r.Failure}: {r.Detail})");
            }
        }

        // What the other implementations said about the same scenarios (arc_suite_oracles.py, arc_suite_mailauth.js), for explaining differences.
        string baseDir = Path.GetDirectoryName(Path.GetFullPath(file))!;
        foreach (string o in new[] { "dkimpy", "mailauth" })
        {
            string p = Path.Combine(baseDir, o + ".json");
            if (!File.Exists(p))
            {
                continue;
            }

            var doc = JsonNode.Parse(File.ReadAllText(p))!.AsObject();
            int agree = 0, differ = 0;
            foreach (JsonNode? t in tests)
            {
                string key = t!["kind"] + "/" + t["name"];
                if (!doc.TryGetPropertyValue(key, out JsonNode? v))
                {
                    continue;
                }

                string theirs = v!.GetValue<string>();
                bool sign = t["kind"]!.GetValue<string>() == "sign";
                string want = sign ? "pass" : t["cv"]!.GetValue<string>();
                if (theirs == want || (want.Length == 0 && theirs == "fail"))
                {
                    agree++;
                }
                else
                {
                    differ++;
                    Console.WriteLine($"  {o} differs from the suite on {key}: expected {want}, got {theirs}");
                }
            }

            Console.WriteLine($"ARC suite, {o}: {agree} agree, {differ} differ");
        }

        Console.WriteLine($"ARC suite, this library: {pass} agree, {fail} differ of {pass + fail}");
        foreach (string w in wrong)
        {
            Console.WriteLine(w);
        }

        return fail == 0 ? 0 : 1;
    }

    private static int Compare(string dir, string[] oracles)
    {
        var cases = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "cases.json")))!.AsArray();
        int bad = 0;
        foreach (string o in oracles)
        {
            foreach ((string label, string file) in new[] { ($"{o} verifies ours", $"{o}-verifies-ours.json"), ($"ours verifies {o}'s", $"ours-verifies-{o}.json") })
            {
                string path = Path.Combine(dir, file);
                if (!File.Exists(path))
                {
                    Console.WriteLine($"{label}: no results ({file})");
                    continue;
                }

                var doc = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                int pass = 0, fail = 0, absent = 0;
                var byBody = new SortedDictionary<string, (int Pass, int Fail)>();
                var details = new List<string>();
                foreach (JsonNode? c in cases)
                {
                    string id = c!["id"]!.GetValue<string>();
                    string body = c["body"]!.GetValue<string>();
                    if (!doc.TryGetPropertyValue(id, out JsonNode? v) || v is not JsonArray arr)
                    {
                        absent++;
                        continue;
                    }

                    string status = arr.Count == 0 ? "none" : arr[0]!["status"]!.GetValue<string>();
                    byBody.TryGetValue(body, out (int Pass, int Fail) cur);
                    if (status == "pass")
                    {
                        pass++;
                        byBody[body] = (cur.Pass + 1, cur.Fail);
                    }
                    else
                    {
                        fail++;
                        byBody[body] = (cur.Pass, cur.Fail + 1);
                        if (details.Count < 12)
                        {
                            details.Add($"    {id} {body} {c["alg"]} {c["canon"]} l={c["length"]} over={c["oversign"]}: {arr.FirstOrDefault()?.ToJsonString() ?? "no signature"}");
                        }
                    }
                }

                Console.WriteLine($"{label}: pass {pass}, not pass {fail}, no result {absent}  ({string.Join(", ", byBody.Select(kv => $"{kv.Key} {kv.Value.Pass}/{kv.Value.Pass + kv.Value.Fail}"))})");
                foreach (string d in details)
                {
                    Console.WriteLine(d);
                }

                bad += fail;
            }
        }

        return bad == 0 ? 0 : 1;
    }
}
