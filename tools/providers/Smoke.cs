using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneMail.Client.Imap;
using RecursiveAI.OneMail.Client.Pop3;
using RecursiveAI.OneMail.Client.Protocol;
using RecursiveAI.OneMail.Client.Sasl;
using RecursiveAI.OneMail.Client.Smtp;

namespace OneMail.Tools.Providers;

sealed record ProviderInfo(string Name, string AddressKey, string ImapHost, string SmtpHost, bool Smtp465, string PopHost);

/// <summary>One row of the result table: provider × protocol × feature.</summary>
sealed record Row(string Provider, string Protocol, string Feature, string Status, string Detail);

/// <summary>
/// The provider smoke set. Every message goes to the account itself and carries <c>X-OneMail-Smoke: onemail-smoke-&lt;run&gt;</c>, so the cleanup
/// (which runs even after failures, and also removes leftovers of earlier aborted runs) finds exactly ours. Rows never throw out of a step.
/// </summary>
sealed class Smoke
{
    public static readonly ProviderInfo Gmail = new("gmail", "GMAIL_ADDRESS", "imap.gmail.com", "smtp.gmail.com", true, "pop.gmail.com");
    // Outlook.com offers submission on 587 with STARTTLS only.
    public static readonly ProviderInfo Outlook = new("outlook", "MS_ADDRESS", "outlook.office365.com", "smtp-mail.outlook.com", false, "outlook.office365.com");

    const string Marker = "onemail-smoke-";
    readonly ProviderInfo _p;
    readonly string _address;
    readonly Func<Task<string>> _token;
    readonly bool _trace;
    readonly string _run = Marker + Guid.NewGuid().ToString("N")[..8];
    readonly List<Row> _rows = [];
    readonly HashSet<string> _secrets = [];

    public Smoke(ProviderInfo p, string address, Func<Task<string>> token, bool trace)
    {
        _p = p;
        _address = address;
        _token = token;
        _trace = trace;
    }

    public IReadOnlyList<Row> Rows => _rows;

    string Mask(string s)
    {
        foreach (string x in _secrets)
        {
            s = s.Replace(x, "***", StringComparison.Ordinal);
        }

        return s.Replace(_address, "<account>", StringComparison.OrdinalIgnoreCase);
    }

    async Task Step(string proto, string feature, Func<Task<string?>> body)
    {
        try
        {
            string? d = await body();
            Add(proto, feature, d is not null && d.StartsWith("not-offered", StringComparison.Ordinal) ? "not-offered" : "pass", d ?? "");
        }
        catch (Exception e) when (e.Message.Contains("not enabled for POP", StringComparison.OrdinalIgnoreCase))
        {
            Add(proto, feature, "not-offered", "POP access is switched off in the account settings");
        }
        catch (NotSupportedException e)
        {
            Add(proto, feature, "not-offered", Mask(e.Message));
        }
        catch (Exception e)
        {
            Add(proto, feature, "FAIL", Mask(e.GetType().Name + ": " + e.Message));
        }
    }

    void Add(string proto, string feature, string status, string detail)
    {
        _rows.Add(new Row(_p.Name, proto, feature, status, detail));
        Console.WriteLine($"{_p.Name,-8} {proto,-5} {feature,-34} {status,-11} {detail}");
    }

    // ---- connections ----------------------------------------------------------------------------------------------------------

    MaskingTrace? Trace() => _trace ? new MaskingTrace(Mask) : null;

    SaslMechanism Mech(string name, string token)
    {
        _secrets.Add(token);
        return name == "XOAUTH2" ? new SaslXOAuth2(_address, token) : new SaslOAuthBearer(_address, token);
    }

    async Task<SmtpClient> SmtpConnect(string mech, bool implicitTls, List<string> offered)
    {
        string token = await _token();
        return await SmtpClient.ConnectAsync(new SmtpClientOptions
        {
            Host = _p.SmtpHost,
            Port = implicitTls ? 465 : 587,
            Security = implicitTls ? SmtpSecurity.ImplicitTls : SmtpSecurity.StartTls,
            Credentials = new SaslCredentials { UserName = _address, AccessToken = token },
            ClientDomain = "smoke.onemail.invalid",
            Connection = new ProtocolConnectionOptions { Trace = Trace() },
            MechanismSelector = (names, conn) =>
            {
                offered.AddRange(names);
                return names.Contains(mech, StringComparer.OrdinalIgnoreCase) ? [Mech(mech, token)] : [];
            },
        });
    }

    async Task<ImapClient> ImapConnect(string mech, bool compress = false)
    {
        string token = await _token();
        ImapClient c = await ImapClient.ConnectAsync(_p.ImapHost, 993, new ImapClientOptions
        {
            Connection = new ProtocolConnectionOptions { ImplicitTls = true, Trace = Trace() },
        });
        try
        {
            await c.AuthenticateAsync(Mech(mech, token));
            if (compress && c.HasCapability("COMPRESS=DEFLATE"))
            {
                await c.CompressAsync();
            }

            return c;
        }
        catch
        {
            await c.DisposeAsync();
            throw;
        }
    }

    // ---- the message we send --------------------------------------------------------------------------------------------------

    byte[] Build(string subjectHeader, int attachmentBytes, string tag)
    {
        var sb = new StringBuilder();
        string id = $"<{_run}.{tag}@smoke.onemail.invalid>";
        sb.Append($"From: <{_address}>\r\nTo: <{_address}>\r\nSubject: {subjectHeader}\r\n");
        sb.Append($"Date: {DateTimeOffset.UtcNow:ddd, dd MMM yyyy HH:mm:ss} +0000\r\nMessage-ID: {id}\r\nX-OneMail-Smoke: {_run}\r\nMIME-Version: 1.0\r\n");
        if (attachmentBytes == 0)
        {
            sb.Append("Content-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n");
            sb.Append(Wrap(Convert.ToBase64String(Encoding.UTF8.GetBytes("OneMail provider smoke " + tag + " – ünïcode ✓\r\n"))));
        }
        else
        {
            string b = "=_smoke_" + _run;
            sb.Append($"Content-Type: multipart/mixed; boundary=\"{b}\"\r\n\r\n--{b}\r\nContent-Type: text/plain; charset=us-ascii\r\n\r\nattachment test {tag}\r\n");
            sb.Append($"--{b}\r\nContent-Type: application/octet-stream; name=\"data.bin\"\r\nContent-Disposition: attachment; filename=\"data.bin\"\r\nContent-Transfer-Encoding: base64\r\n\r\n");
            sb.Append(Wrap(Convert.ToBase64String(RandomNumberGenerator.GetBytes(attachmentBytes))));
            sb.Append($"--{b}--\r\n");
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    static string Wrap(string b64)
    {
        var sb = new StringBuilder(b64.Length + b64.Length / 38);
        for (int i = 0; i < b64.Length; i += 76)
        {
            sb.Append(b64, i, Math.Min(76, b64.Length - i)).Append("\r\n");
        }

        return sb.ToString();
    }

    static string Encoded(string s) => "=?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(s)) + "?=";

    // ---- the run ---------------------------------------------------------------------------------------------------------------

    public async Task RunAsync(bool imap, bool smtp, bool pop)
    {
        Console.WriteLine($"run {_run}");
        var sent = new List<(string Tag, byte[] Bytes)>();
        try
        {
            if (smtp)
            {
                await SmtpRows(sent);
            }

            if (imap)
            {
                await ImapRows(sent);
            }

            if (pop)
            {
                await PopRows(sent);
            }
        }
        finally
        {
            await Step("imap", "cleanup", Cleanup);
        }
    }

    async Task SmtpRows(List<(string, byte[])> sent)
    {
        var combos = new List<(string Mech, bool Impl)> { ("XOAUTH2", false), ("OAUTHBEARER", false) };
        if (_p.Smtp465)
        {
            combos.Add(("XOAUTH2", true));
            combos.Add(("OAUTHBEARER", true));
        }

        bool bigDone = false;
        foreach ((string mech, bool impl) in combos)
        {
            string label = $"{(impl ? "465 implicit" : "587 starttls")} {mech}";
            var offered = new List<string>();
            await Step("smtp", label + " auth+send", async () =>
            {
                SmtpClient c;
                try
                {
                    c = await SmtpConnect(mech, impl, offered);
                }
                catch (Exception) when (offered.Count > 0 && !offered.Contains(mech, StringComparer.OrdinalIgnoreCase))
                {
                    return "not-offered; AUTH offers " + string.Join(' ', offered);
                }

                await using SmtpClient _ = c;
                string caps = string.Join(' ', c.Capabilities.Keywords.Where(k => !k.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase)));
                byte[] small = Build("smoke " + mech + (impl ? " 465" : " 587"), 0, $"{mech}{(impl ? "i" : "s")}");
                if (!(await c.SendAsync(Msg(small))).Delivered)
                {
                    throw new InvalidOperationException("DATA not accepted");
                }

                sent.Add(($"{mech}{(impl ? "i" : "s")}", small));
                if (!bigDone)
                {
                    bigDone = true;
                    await SendOn(c, "unicode-attach", Build(Encoded("Prüfung ✓ 試験 – smoke"), 40_000, "unicode-attach"), sent, " attachment+non-ASCII subject (encoded-word)");
                    if (c.Capabilities.Has("SMTPUTF8"))
                    {
                        await SendOn(c, "utf8-subject", Build("Prüfung ✓ 試験 raw UTF-8", 0, "utf8-subject"), sent, " SMTPUTF8 raw UTF-8 subject");
                    }
                    else
                    {
                        Add("smtp", "SMTPUTF8 raw subject", "not-offered", "SMTPUTF8 not advertised");
                    }

                    await SendOn(c, "big", Build("smoke 5MB", 3_900_000, "big"), sent, " 5 MB message");
                }

                await c.QuitAsync();
                return "extensions: " + caps;
            });
        }
    }

    static SmtpMessage Msg(byte[] bytes) => SmtpMessage.FromBytes(bytes);

    async Task SendOn(SmtpClient c, string tag, byte[] bytes, List<(string, byte[])> sent, string what)
    {
        await Step("smtp", what.Trim(), async () =>
        {
            if (!(await c.SendAsync(Msg(bytes))).Delivered)
            {
                throw new InvalidOperationException("DATA not accepted");
            }

            sent.Add((tag, bytes));
            return bytes.Length + " bytes";
        });
    }

    async Task ImapRows(List<(string Tag, byte[] Bytes)> sent)
    {
        foreach (string mech in new[] { "XOAUTH2", "OAUTHBEARER" })
        {
            await Step("imap", mech + " login", async () =>
            {
                await using ImapClient c = await ImapConnect(mech);
                await c.LogoutAsync();
                return "capabilities: " + string.Join(' ', c.Capabilities.Where(x => !x.StartsWith("AUTH=", StringComparison.Ordinal)));
            });
        }

        // Services that reject a mechanism fail "login" above; the remaining rows use whichever works.
        string use = _rows.Any(r => r.Protocol == "imap" && r.Feature == "XOAUTH2 login" && r.Status == "pass") ? "XOAUTH2" : "OAUTHBEARER";
        await using ImapClient imap = await ImapConnect(use, compress: false);
        await Step("imap", "list + SPECIAL-USE", async () =>
        {
            IReadOnlyList<ImapMailboxInfo> boxes = await imap.ListAsync();
            if (!imap.HasCapability("SPECIAL-USE"))
            {
                return "not-offered: no SPECIAL-USE capability; " + boxes.Count + " folders";
            }

            return boxes.Count + " folders; special-use: " + string.Join(',', boxes.Select(b => b.SpecialUse).Where(s => s != ImapSpecialUse.None).Distinct());
        });
        await Step("imap", "select INBOX", async () => (await imap.SelectAsync("INBOX")).Exists + " messages");

        // Delivery to the account itself can lag; wait for each of our messages by Message-ID.
        foreach ((string tag, byte[] bytes) in sent)
        {
            await Step("imap", $"fetch+compare {tag}", async () =>
            {
                long uid = await WaitFor(imap, $"<{_run}.{tag}@smoke.onemail.invalid>", TimeSpan.FromSeconds(90));
                ImapMessage m = (await imap.FetchAsync(ImapSet.Of(uid), new ImapFetchQuery { Uid = true, Flags = true, Size = true, Envelope = true, Source = true }, uid: true).ToListAsync()).First();
                return CompareSource(bytes, m.Source.ToArray()) + "; size " + m.Size + ", sent " + bytes.Length;
            });
        }

        await Step("imap", "search", async () =>
        {
            ImapSearchResult r = await imap.SearchAsync(ImapSearch.Header("X-OneMail-Smoke", _run), uid: true);
            return r.Numbers.Count + " hits for run header (sent " + sent.Count + ")";
        });

        long? first = null;
        await Step("imap", "flags store", async () =>
        {
            first = (await imap.SearchAsync(ImapSearch.Header("X-OneMail-Smoke", _run), uid: true)).Numbers is { Count: > 0 } n1 ? n1[0] : 0;
            if (first is null or 0)
            {
                // An imap-only run has no sent messages; append one to flag.
                first = (await imap.AppendAsync("INBOX", Build("smoke flags", 0, "flags"))).Uids[0];
            }

            await imap.StoreAsync(ImapSet.Of(first.Value), ImapStoreMode.Add, ["\\Flagged", "$OneMailSmoke"], uid: true);
            ImapMessage m = (await imap.FetchAsync(ImapSet.Of(first.Value), new ImapFetchQuery { Uid = true, Flags = true }, uid: true).ToListAsync()).First();
            return "flags: " + string.Join(' ', m.Flags ?? []);
        });

        await Step("imap", "CONDSTORE select+modseq", async () =>
        {
            if (!imap.HasCapability("CONDSTORE"))
            {
                return "not-offered";
            }

            ImapMailbox mb = await imap.SelectAsync("INBOX", new ImapSelectOptions { CondStore = true });
            return "HIGHESTMODSEQ " + (mb.NoModSeq ? "NOMODSEQ" : mb.HighestModSeq.ToString());
        });

        await Step("imap", "QRESYNC reselect", async () =>
        {
            if (!imap.HasCapability("QRESYNC"))
            {
                return "not-offered";
            }

            await imap.EnableAsync(["QRESYNC"]);
            ImapMailbox a = await imap.SelectAsync("INBOX", new ImapSelectOptions { CondStore = true });
            ImapMailbox b = await imap.SelectAsync("INBOX", new ImapSelectOptions { QResync = new ImapQResync(a.UidValidity, a.HighestModSeq) });
            return "resync events " + b.ResyncEvents.Count;
        });

        await Step("imap", "APPEND + fetch", async () =>
        {
            byte[] bytes = Build("smoke append", 0, "append");
            ImapAppendResult r = await imap.AppendAsync("INBOX", bytes, ["\\Seen"]);
            long uid = r.Uids.Count > 0 ? r.Uids[0] : await WaitFor(imap, $"<{_run}.append@smoke.onemail.invalid>", TimeSpan.FromSeconds(30));
            ImapMessage m = (await imap.FetchAsync(ImapSet.Of(uid), new ImapFetchQuery { Source = true }, uid: true).ToListAsync()).First();
            return (r.Uids.Count > 0 ? "APPENDUID" : "no APPENDUID, found by search") + "; " + CompareSource(bytes, m.Source.ToArray());
        });

        await Step("imap", "MOVE to Trash/All", async () =>
        {
            if (!imap.HasCapability("MOVE"))
            {
                return "not-offered";
            }

            string dest = await Special(imap, ImapSpecialUse.Trash) ?? throw new InvalidOperationException("no Trash folder");
            long uid = (await imap.SearchAsync(ImapSearch.Header("Message-ID", $"<{_run}.append@smoke.onemail.invalid>"), uid: true)).Numbers is { Count: > 0 } n2 ? n2[0] : 0;
            if (uid == 0)
            {
                throw new InvalidOperationException("appended message not found");
            }

            ImapCopyResult r = await imap.MoveAsync(ImapSet.Of(uid), dest, uid: true);
            return "moved to " + (r.DestinationUids.Count > 0 ? "uid " + r.DestinationUids[0] : "(no COPYUID)");
        });

        await Step("imap", "IDLE sees new message", async () =>
        {
            if (!imap.HasCapability("IDLE"))
            {
                return "not-offered";
            }

            await imap.SelectAsync("INBOX");
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var offered = new List<string>();
            Task send = Task.Run(async () =>
            {
                await Task.Delay(3000);
                await using SmtpClient c = await SmtpConnect("XOAUTH2", false, offered);
                byte[] b = Build("smoke idle", 0, "idle");
                await c.SendAsync(Msg(b));
                await c.QuitAsync();
            });
            bool seen = false;
            await foreach (ImapEvent ev in imap.IdleAsync(null, stop.Token))
            {
                if (ev.Kind == ImapEventKind.Exists)
                {
                    seen = true;
                    break;
                }
            }

            await send;
            return seen ? "EXISTS pushed" : throw new TimeoutException("no EXISTS within 90 s");
        });

        await Step("imap", "COMPRESS=DEFLATE", async () =>
        {
            if (!imap.HasCapability("COMPRESS=DEFLATE"))
            {
                return "not-offered";
            }

            await using ImapClient c = await ImapConnect(use, compress: true);
            ImapMailbox mb = await c.SelectAsync("INBOX");
            await c.LogoutAsync();
            return "compressed session, INBOX " + mb.Exists;
        });
    }

    async Task<long> WaitFor(ImapClient imap, string messageId, TimeSpan limit)
    {
        DateTime end = DateTime.UtcNow + limit;
        while (true)
        {
            await imap.SelectAsync("INBOX");
            ImapSearchResult r = await imap.SearchAsync(ImapSearch.Header("Message-ID", messageId), uid: true);
            if (r.Numbers.Count > 0)
            {
                return r.Numbers[0];
            }

            if (DateTime.UtcNow > end)
            {
                throw new TimeoutException("message not in INBOX within " + limit.TotalSeconds + " s");
            }

            await Task.Delay(3000);
        }
    }

    static async Task<string?> Special(ImapClient imap, ImapSpecialUse use) =>
        (await imap.ListAsync()).FirstOrDefault(b => b.SpecialUse == use)?.Name;

    /// <summary>Servers add trace headers, so compare the body bytes exactly and require every header we sent to come back unchanged.</summary>
    static string CompareSource(byte[] sent, byte[] got)
    {
        (string[] sh, byte[] sb) = Split(sent);
        (string[] gh, byte[] gb) = Split(got);
        bool body = sb.AsSpan().SequenceEqual(gb);
        string[] missing = [.. sh.Where(h => !gh.Contains(h, StringComparer.Ordinal))];
        if (!body || missing.Length > 0)
        {
            throw new InvalidOperationException($"mismatch: body {(body ? "equal" : $"differs ({sb.Length} sent, {gb.Length} got)")}; headers missing/changed: {missing.Length}"
                + (missing.Length > 0 ? " (" + string.Join(", ", missing.Select(m => m.Split(':')[0])) + ")" : ""));
        }

        return $"BODY[] body equal ({sb.Length} bytes), {sh.Length} sent headers intact, {gh.Length - sh.Length} added";
    }

    static (string[] Headers, byte[] Body) Split(byte[] m)
    {
        int at = m.AsSpan().IndexOf("\r\n\r\n"u8);
        string head = Encoding.UTF8.GetString(m, 0, at < 0 ? m.Length : at);
        string[] headers = [.. head.Replace("\r\n ", " ").Replace("\r\n\t", " ").Split("\r\n")];
        return (headers, at < 0 ? [] : m[(at + 4)..]);
    }

    async Task PopRows(List<(string Tag, byte[] Bytes)> sent)
    {
        await Step("pop3", "XOAUTH2 + UIDL + RETR", async () =>
        {
            // A pop-only run sends its own message first.
            string tag = sent.Count > 0 ? sent[0].Tag : "pop";
            if (sent.Count == 0)
            {
                var offered = new List<string>();
                await using SmtpClient sc = await SmtpConnect("XOAUTH2", false, offered);
                byte[] b = Build("smoke pop", 0, tag);
                await sc.SendAsync(Msg(b));
                sent.Add((tag, b));
                await sc.QuitAsync();
            }

            string target = $"<{_run}.{tag}@smoke.onemail.invalid>";
            DateTime end = DateTime.UtcNow.AddSeconds(90);
            while (true)
            {
                // Gmail shows a new message to POP only on a fresh session, so each look reconnects.
                string token = await _token();
                await using Pop3Client c = await Pop3Client.ConnectAsync(_p.PopHost, 995, new Pop3ClientOptions { Connection = new ProtocolConnectionOptions { ImplicitTls = true, Trace = Trace() } });
                await c.AuthenticateAsync(Mech("XOAUTH2", token));
                IReadOnlyList<Pop3MessageInfo> uids = await c.UidlAsync();
                foreach (Pop3MessageInfo m in uids.AsEnumerable().Reverse())
                {
                    var ms = new MemoryStream();
                    await c.RetrieveAsync(m.Number, ms);
                    if (Encoding.UTF8.GetString(ms.ToArray()).Contains(target, StringComparison.Ordinal))
                    {
                        await c.QuitAsync();
                        return $"{uids.Count} messages, UIDL {(m.Uid is { Length: > 0 } ? "present" : "missing")}; test message retrieved, {ms.Length} bytes";
                    }
                }

                await c.QuitAsync();
                if (DateTime.UtcNow > end)
                {
                    throw new InvalidOperationException("test message not in POP3 listing (" + uids.Count + " messages)");
                }

                await Task.Delay(5000);
            }
        });
    }

    /// <summary>Removes every message carrying our marker (any run) from the special-use folders, ending in Trash, where it is deleted and expunged.</summary>
    async Task<string?> Cleanup()
    {
        await using ImapClient imap = await ImapConnect(_rows.Any(r => r.Feature == "XOAUTH2 login" && r.Status == "pass") ? "XOAUTH2" : "OAUTHBEARER");
        IReadOnlyList<ImapMailboxInfo> boxes = await imap.ListAsync();
        string? trash = boxes.FirstOrDefault(b => b.SpecialUse == ImapSpecialUse.Trash)?.Name;
        int removed = 0;
        foreach (ImapMailboxInfo b in boxes.Where(b => !b.HasAttribute("\\Noselect") && !b.HasAttribute("\\NonExistent")))
        {
            if (b.SpecialUse is ImapSpecialUse.All or ImapSpecialUse.Flagged or ImapSpecialUse.Important)
            {
                continue; // Gmail's virtual views: the same messages show in INBOX/Trash.
            }

            await imap.SelectAsync(b.Name);
            IReadOnlyList<long> hits = (await imap.SearchAsync(ImapSearch.Header("X-OneMail-Smoke", Marker), uid: true)).Numbers;
            if (hits.Count == 0)
            {
                continue;
            }

            ImapSet set = ImapSet.Of(hits);
            if (trash is not null && !string.Equals(b.Name, trash, StringComparison.Ordinal) && imap.HasCapability("MOVE"))
            {
                await imap.MoveAsync(set, trash, uid: true);
            }
            else
            {
                await imap.StoreAsync(set, ImapStoreMode.Add, ["\\Deleted"], uid: true);
                await imap.ExpungeAsync();
            }

            removed += hits.Count;
        }

        if (trash is not null)
        {
            await imap.SelectAsync(trash);
            IReadOnlyList<long> hits = (await imap.SearchAsync(ImapSearch.Header("X-OneMail-Smoke", Marker), uid: true)).Numbers;
            if (hits.Count > 0)
            {
                await imap.StoreAsync(ImapSet.Of(hits), ImapStoreMode.Add, ["\\Deleted"], uid: true);
                await imap.ExpungeAsync();
            }
        }

        await imap.LogoutAsync();
        return $"{removed} moved/removed";
    }
}

/// <summary>Writes the wire trace with tokens, base64 credentials blobs and the account address masked.</summary>
sealed class MaskingTrace : IProtocolTrace
{
    readonly TextWriterProtocolTrace _inner;

    public MaskingTrace(Func<string, string> mask) => _inner = new TextWriterProtocolTrace(new MaskWriter(Console.Error, mask));

    public void Sent(ReadOnlySpan<byte> data) => _inner.Sent(data);

    public void Received(ReadOnlySpan<byte> data) => _inner.Received(data);

    sealed class MaskWriter(TextWriter inner, Func<string, string> mask) : TextWriter
    {
        public override Encoding Encoding => inner.Encoding;

        public override void WriteLine(string? value)
        {
            string s = mask(value ?? "");
            // AUTHENTICATE lines and their continuations carry credentials as one long base64 word.
            s = System.Text.RegularExpressions.Regex.Replace(s, @"[A-Za-z0-9+/=_-]{40,}", "<b64>");
            inner.WriteLine(s);
        }
    }
}
