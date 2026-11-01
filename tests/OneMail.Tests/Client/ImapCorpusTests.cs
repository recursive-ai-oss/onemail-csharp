using System.Diagnostics;
using System.Text;
using RecursiveAI.OneMail.Client.Imap;
using RecursiveAI.OneMail.Client.Imap.Protocol;
using RecursiveAI.OneMail.Mime;

namespace RecursiveAI.OneMail.Tests.Client;

// Mailbox round trips with real mail: APPEND a corpus, FETCH BODY[] back and compare the bytes; compare ENVELOPE and BODYSTRUCTURE with our own parse.
// Opt-in (the corpora are fetched by tools/, never committed):
//   ONEMAIL_IMAP_DOVECOT_DIR  directory of .eml files (the dovecot-crlf mbox split into messages)
//   ONEMAIL_IMAP_ENRON_DIR    root of the CMU Enron maildir; ONEMAIL_IMAP_ENRON_SAMPLE messages are taken (default 5000, every n-th file in sorted order)
//   ONEMAIL_IMAP_REPORT       file the summary is appended to
public class ImapCorpusTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<ServerInfo> Servers() => ServerHarness.ImapServers();

    [Theory]
    [MemberData(nameof(Servers))]
    public Task DovecotMboxRoundTrip(ServerInfo s)
    {
        string? dir = Environment.GetEnvironmentVariable("ONEMAIL_IMAP_DOVECOT_DIR");
        Assert.SkipWhen(dir is null, "ONEMAIL_IMAP_DOVECOT_DIR is not set");
        List<byte[]> messages = [.. Directory.EnumerateFiles(dir!, "*.eml").Order(StringComparer.Ordinal).Select(f => Crlf(File.ReadAllBytes(f)))];
        return RoundTripAsync(s, "dovecot-crlf", messages);
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public Task EnronSampleRoundTrip(ServerInfo s)
    {
        string? dir = Environment.GetEnvironmentVariable("ONEMAIL_IMAP_ENRON_DIR");
        Assert.SkipWhen(dir is null, "ONEMAIL_IMAP_ENRON_DIR is not set");
        int want = int.TryParse(Environment.GetEnvironmentVariable("ONEMAIL_IMAP_ENRON_SAMPLE"), out int n) ? n : 5000;
        string[] files = [.. Directory.EnumerateFiles(dir!, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];
        int step = Math.Max(1, files.Length / want);
        List<byte[]> messages = [.. files.Where((_, i) => i % step == 0).Take(want).Select(f => Crlf(File.ReadAllBytes(f)))];
        return RoundTripAsync(s, "enron-cmu", messages);
    }

    // IMAP carries CRLF line ends (RFC 9051 §2.2.1); the LF-only files are converted once, here, so the comparison is of what we sent against what came back.
    internal static byte[] Crlf(byte[] raw)
    {
        var ms = new MemoryStream(raw.Length + raw.Length / 20);
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] == (byte)'\n' && (i == 0 || raw[i - 1] != (byte)'\r'))
            {
                ms.WriteByte((byte)'\r');
            }

            ms.WriteByte(raw[i]);
        }

        return ms.ToArray();
    }

    private static async Task RoundTripAsync(ServerInfo s, string corpus, List<byte[]> messages)
    {
        ServerHarness.RequireServer(s);
        var report = new StringBuilder();
        await using ImapClient c = await ImapClientServerTests.ConnectAsync(s);
        string mb = await ImapClientServerTests.NewMailboxAsync(c);
        try
        {
            var sw = Stopwatch.StartNew();
            var accepted = new List<byte[]>(messages.Count);
            var rejected = new Dictionary<string, int>();
            const int batch = 50;
            for (int i = 0; i < messages.Count; i += batch)
            {
                List<byte[]> chunk = messages.GetRange(i, Math.Min(batch, messages.Count - i));
                try
                {
                    await c.AppendAsync(mb, [.. chunk.Select(m => new ImapAppendItem(m))], Ct);
                    accepted.AddRange(chunk);
                }
                catch (ImapCommandException)
                {
                    // MULTIAPPEND is all or nothing: retry one by one and count what the server refuses, and why.
                    foreach (byte[] m in chunk)
                    {
                        try
                        {
                            await c.AppendAsync(mb, m, cancellationToken: Ct);
                            accepted.Add(m);
                        }
                        catch (ImapCommandException e)
                        {
                            string why = e.ResponseCode ?? e.Message[..Math.Min(60, e.Message.Length)];
                            rejected[why] = rejected.GetValueOrDefault(why) + 1;
                        }
                    }
                }
            }

            TimeSpan appendTime = sw.Elapsed;
            ImapMailbox box = await c.SelectAsync(mb, cancellationToken: Ct);
            Assert.Equal(accepted.Count, box.Exists);

            sw.Restart();
            int identical = 0, differ = 0, seen = 0;
            int envDiff = 0, structDiff = 0, idDiff = 0, fromDiff = 0, subjectDiff = 0;
            var samples = new List<string>();
            var query = new ImapFetchQuery { Uid = true, Size = true, Envelope = true, BodyStructure = true, Source = true };
            await foreach (ImapMessage m in c.FetchAsync(ImapSet.All, query, false, Ct))
            {
                int idx = (int)m.SequenceNumber - 1;
                seen++;
                if (accepted[idx].AsSpan().SequenceEqual(m.Source.Span))
                {
                    identical++;
                }
                else
                {
                    differ++;
                    if (samples.Count < 8)
                    {
                        int at = accepted[idx].AsSpan().CommonPrefixLength(m.Source.Span);
                        string ctx(ReadOnlySpan<byte> b) => Convert.ToHexString(b.Slice(Math.Max(0, at - 6), Math.Min(14, b.Length - Math.Max(0, at - 6))));
                        samples.Add($"bytes differ at #{m.SequenceNumber} offset {at}: sent {accepted[idx].Length}, got {m.Source.Length}; sent ...{ctx(accepted[idx])} got ...{ctx(m.Source.Span)}");
                    }
                }

                // Compare the server's view with ours of the very same bytes.
                MimeEntity ours = MimeEntity.Parse(accepted[idx]);
                bool bad = false;
                string? oursId = ours.GetMessageId()?.Trim('<', '>');
                string? theirsId = m.Envelope?.MessageId?.Trim('<', '>');
                if (!string.Equals(oursId ?? "", theirsId ?? "", StringComparison.Ordinal)) { idDiff++; bad = true; AddSample(samples, $"message-id #{m.SequenceNumber}: ours '{oursId}' theirs '{theirsId}'"); }
                string oursSubject = Unfold(ours.Headers.GetFirst("Subject")?.RawValue ?? default);
                string theirsSubject = Unfold(Encoding.UTF8.GetBytes(m.Envelope?.Subject ?? ""));
                if (!string.Equals(oursSubject, theirsSubject, StringComparison.Ordinal)) { subjectDiff++; bad = true; AddSample(samples, $"subject #{m.SequenceNumber}: ours '{oursSubject}' theirs '{theirsSubject}'"); }
                string? oursFrom = ours.GetAddresses("From").OfType<MimeMailbox>().FirstOrDefault()?.Address;
                ImapAddress? f = m.Envelope is { From.Count: > 0 } env ? env.From[0] : null;
                string? theirsFrom = f is { Mailbox: not null, Host: not null } ? f.Mailbox + "@" + f.Host : null;
                if (!string.Equals(oursFrom ?? "", theirsFrom ?? "", StringComparison.OrdinalIgnoreCase)) { fromDiff++; bad = true; AddSample(samples, $"from #{m.SequenceNumber}: ours '{oursFrom}' theirs '{theirsFrom}'"); }
                if (bad) { envDiff++; }

                ImapBodyStructure bs = m.BodyStructure!;
                bool multipartOurs = ours is MimeMultipart;
                if (bs.IsMultipart != multipartOurs || (multipartOurs && bs.Parts!.Count != ((MimeMultipart)ours).Children.Count)
                    || (!multipartOurs && !string.Equals(bs.MediaType, ours.MediaType, StringComparison.OrdinalIgnoreCase)))
                {
                    structDiff++;
                    AddSample(samples, $"structure #{m.SequenceNumber}: ours {ours.MediaType} theirs {bs.MediaType}");
                }
            }

            TimeSpan fetchTime = sw.Elapsed;
            report.AppendLine($"[{s.Name}] {corpus}: sent {messages.Count}, accepted {accepted.Count}, rejected {rejected.Sum(r => r.Value)} ({string.Join(", ", rejected.Select(r => r.Key + "=" + r.Value))})");
            report.AppendLine($"[{s.Name}] {corpus}: fetched {seen}; BODY[] byte-identical {identical}, different {differ}; append {appendTime.TotalSeconds:F1}s, fetch {fetchTime.TotalSeconds:F1}s");
            report.AppendLine($"[{s.Name}] {corpus}: envelope differences vs our parse {envDiff} (message-id {idDiff}, subject {subjectDiff}, from {fromDiff}); body-structure differences {structDiff}");
            foreach (string sample in samples)
            {
                report.AppendLine("    " + sample);
            }

            Assert.Equal(accepted.Count, seen);
            Assert.Equal(0, differ);
        }
        finally
        {
            TestContext.Current.SendDiagnosticMessage(report.ToString());
            if (Environment.GetEnvironmentVariable("ONEMAIL_IMAP_REPORT") is { } path)
            {
                File.AppendAllText(path, report.ToString());
            }

            await ImapClientServerTests.DropAsync(c, mb);
        }
    }

    private static void AddSample(List<string> samples, string text)
    {
        if (samples.Count < 12)
        {
            samples.Add(text.Length > 220 ? text[..220] : text);
        }
    }

    // Header values are compared unfolded and trimmed; servers differ in how they unfold.
    private static string Unfold(ReadOnlyMemory<byte> raw)
    {
        string t = Encoding.UTF8.GetString(raw.Span).Replace("\r\n", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);
        return string.Join(' ', t.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
    }
}
