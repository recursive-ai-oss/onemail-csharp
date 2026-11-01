using System.Text;
using System.Text.Json;
using RecursiveAI.OneMail;
using RecursiveAI.OneMail.Composition;
using RecursiveAI.OneMail.Conversion.OxcMail;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Mime;

// CompositionTool: makes replies, reply-alls and forwards of every item under IN and checks them.
//   CompositionTool sweep IN [OUT] [--limit N] [--sample N]
//   CompositionTool names OUT [--count N] [--seed S]   (see Names.cs)
// IN is a file or a directory (read recursively; the content decides what each file is). With OUT the messages are written as
// OUT/eml/NNNNNN.eml with the expected header values in OUT/expected/NNNNNN.json and OUT/manifest.tsv, for check_compose.py
// to compare against what the black-box parsers read. Without OUT only the in-process checks run.
namespace CompositionTool;

internal static class Program
{
    private static readonly DateTimeOffset Fixed = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Fixed;
    }

    private sealed class Stats
    {
        public int Items;
        public int Composed;
        public int Failures;
        public int Prefixed;
        public int MapiChecked;
        public int EmbeddedChecked;
        public readonly SortedDictionary<string, int> Defects = [];
        public readonly List<string> Examples = [];
    }

    private static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "peek")
        {
            return Peek(args[1], args.Length > 2 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 3);
        }

        if (args.Length >= 2 && args[0] == "names")
        {
            return Names.Run(args);
        }

        if (args.Length < 2 || args[0] != "sweep")
        {
            Console.Error.WriteLine("usage: CompositionTool sweep IN [OUT] [--limit N] [--sample N]");
            return 2;
        }

        int limit = int.MaxValue;
        int sample = 1;
        var rest = new List<string>();
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--limit")
            {
                limit = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (args[i] == "--sample")
            {
                sample = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                rest.Add(args[i]);
            }
        }

        string? outDir = rest.Count > 1 ? rest[1] : null;
        if (outDir is not null)
        {
            Directory.CreateDirectory(Path.Combine(outDir, "eml"));
            Directory.CreateDirectory(Path.Combine(outDir, "expected"));
        }

        var stats = new SortedDictionary<string, Stats>();
        using StreamWriter? manifest = outDir is null ? null : new StreamWriter(Path.Combine(outDir, "manifest.tsv"), false, new UTF8Encoding(false));
        int number = 0;
        int total = 0;
        int fileNo = 0;
        foreach (string file in Files(rest[0]))
        {
            if (fileNo++ % sample != 0)
            {
                continue;
            }

            if (total >= limit)
            {
                break;
            }

            MailContainer? container = null;
            try
            {
                container = Mail.Open(file);
            }
            catch (UnsupportedMailFormatException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            using (container)
            {
                foreach (IMailItem item in container.Enumerate())
                {
                    if (total++ >= limit)
                    {
                        break;
                    }

                    if (item.Kind != MailItemKind.Message)
                    {
                        continue;
                    }

                    string format = item.Provenance.Format.ToString();
                    if (!stats.TryGetValue(format, out Stats? st))
                    {
                        stats[format] = st = new Stats();
                    }

                    st.Items++;
                    Item(item, format, st, outDir, manifest, ref number);
                }
            }
        }

        int failures = 0;
        foreach ((string format, Stats st) in stats)
        {
            failures += st.Failures;
            Console.WriteLine($"{format}: items {st.Items}, messages composed {st.Composed}, failures {st.Failures}, parents with a prefix {st.Prefixed}, mapi threading checked {st.MapiChecked}, embedded originals compared {st.EmbeddedChecked}");
            foreach ((string k, int n) in st.Defects)
            {
                Console.WriteLine($"  defect {k}: {n}");
            }

            foreach (string e in st.Examples.Take(10))
            {
                Console.WriteLine("  FAIL " + e);
            }
        }

        Console.WriteLine($"total failures {failures}");
        return failures == 0 ? 0 : 1;
    }

    // Shows the first items of a file: what the view reports and the start of the message bytes.
    private static int Peek(string path, int count)
    {
        using MailContainer c = Mail.Open(path);
        foreach (IMailItem item in c.Enumerate().Take(count))
        {
            Console.WriteLine($"subject={item.Subject} from={item.From.Count} to={item.ToRecipients.Count} id={item.MessageId} index={Convert.ToHexString(item.ConversationIndex.Span)} len={item.ConversationIndex.Length} parses={ConversationIndex.TryParse(item.ConversationIndex.Span, out _)}");
            if (item is MimeMailItem m)
            {
                byte[] b = m.Entity.ToArray();
                Console.WriteLine($"  bytes={b.Length} head={Encoding.Latin1.GetString(b, 0, Math.Min(160, b.Length)).Replace("\r", "\\r").Replace("\n", "\\n")}");
                byte[] again = MimeEntity.Parse(b).ToArray();
                Console.WriteLine($"  reparse identical={again.AsSpan().SequenceEqual(b)} defects={string.Join(",", item.Defects.Select(d => d.Code).Take(6))}");
            }
        }

        return 0;
    }

    private static IEnumerable<string> Files(string input)
    {
        if (File.Exists(input))
        {
            yield return input;
            yield break;
        }

        foreach (string f in Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            yield return f;
        }
    }

    private static void Item(IMailItem item, string format, Stats st, string? outDir, StreamWriter? manifest, ref int number)
    {
        MailAddress? me = item.ToRecipients.Count > 0 ? item.ToRecipients[0] : null;
        var opts = new CompositionOptions
        {
            TimeProvider = new Clock(),
            RandomBytes = b => b.Fill(0xA5),
            MessageIdDomain = "sweep.example",
            From = new MailAddress("Sweep", "sweep@sweep.example"),
            OwnAddresses = me is null ? [] : [me.Value.Address],
            Text = "reply text",
        };
        var attach = new CompositionOptions
        {
            TimeProvider = opts.TimeProvider,
            RandomBytes = opts.RandomBytes,
            MessageIdDomain = opts.MessageIdDomain,
            From = opts.From,
            ForwardStyle = ForwardStyle.Attachment,
        };
        string? subject = item.Subject;
        if (subject is not null && SubjectPrefixes.Strip(subject) != subject.TrimStart())
        {
            st.Prefixed++;
        }

        foreach ((string kind, Func<ComposedMessage> make, string prefix) in new (string, Func<ComposedMessage>, string)[]
        {
            ("reply", () => MessageComposer.Reply(item, opts), "Re: "),
            ("replyall", () => MessageComposer.ReplyAll(item, opts), "Re: "),
            ("forward", () => MessageComposer.Forward(item, opts), "Fwd: "),
            ("forward-attachment", () => MessageComposer.Forward(item, attach), "Fwd: "),
        })
        {
            ComposedMessage c;
            try
            {
                c = make();
            }
            catch (Exception e)
            {
                Fail(st, $"{kind} threw {e.GetType().Name}: {e.Message} ({item.Provenance.Location} #{item.Provenance.Index})");
                continue;
            }

            st.Composed++;
            foreach (CompositionDefect d in c.Defects)
            {
                st.Defects[d.Kind.ToString()] = st.Defects.GetValueOrDefault(d.Kind.ToString()) + 1;
            }

            byte[] bytes = c.Message.ToArray();
            MimeEntity back = MimeEntity.Parse(bytes);
            string where = $"{kind} of {item.Provenance.Location} #{item.Provenance.Index}";
            string? got = Mail.Parse(bytes).Subject;
            string expectedSubject = prefix + SubjectPrefixes.Strip(CleanLike(subject ?? string.Empty).TrimEnd());
            if (Squash(got) != Squash(expectedSubject) || c.Subject != expectedSubject)
            {
                Fail(st, $"{where}: subject '{got}' expected '{expectedSubject}'");
            }

            string rest = c.Subject[prefix.Length..];
            if (!c.Subject.StartsWith(prefix, StringComparison.Ordinal) || SubjectPrefixes.Strip(rest) != rest)
            {
                Fail(st, $"{where}: prefix stacked in '{c.Subject}'");
            }

            if (kind is "reply" or "replyall")
            {
                CheckThreading(item, back, st, where);
                var toBack = back.GetAddresses("To").SelectMany(a => a is MimeMailbox m ? [m.Address] : Array.Empty<string>()).ToArray();
                if (!toBack.SequenceEqual(c.To.Select(a => a.Address)))
                {
                    Fail(st, $"{where}: To {string.Join(",", toBack)} expected {string.Join(",", c.To.Select(a => a.Address))}");
                }
            }
            else if (back.Headers.GetFirst("In-Reply-To") is not null)
            {
                Fail(st, $"{where}: forward has In-Reply-To");
            }

            if (kind == "forward-attachment")
            {
                CheckEmbedded(item, back, st, where);
            }

            if (back.Headers.GetAll("Bcc").Any() || back.Headers.Count(h => h.IsNamed("Subject")) != 1)
            {
                Fail(st, $"{where}: unexpected Bcc or repeated Subject");
            }

            if (item is MapiMailItem mi)
            {
                CheckMapi(c, mi, st, where);
            }

            if (outDir is not null)
            {
                string id = (++number).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
                File.WriteAllBytes(Path.Combine(outDir, "eml", id + ".eml"), bytes);
                manifest!.WriteLine($"{id}\t{format}\t{kind}");
                var exp = new
                {
                    subject = c.Subject,
                    to = c.To.Select(a => a.Address.ToLowerInvariant()).Order().ToArray(),
                    cc = c.Cc.Select(a => a.Address.ToLowerInvariant()).Order().ToArray(),
                    from = "sweep@sweep.example",
                    inReplyTo = back.GetInReplyTo().ToArray(),
                    references = back.GetReferences().ToArray(),
                };
                File.WriteAllText(Path.Combine(outDir, "expected", id + ".json"), JsonSerializer.Serialize(exp));
            }
        }
    }

    private static string Squash(string? s) => string.Join(' ', (s ?? string.Empty).Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));

    private static string CleanLike(string s) => new(s.Select(ch => ch < ' ' || ch == (char)0x7F || ch == (char)0x85 || ch == (char)0x2028 || ch == (char)0x2029 ? ' ' : ch).ToArray());

    private static void CheckThreading(IMailItem parent, MimeEntity reply, Stats st, string where)
    {
        string? pid = parent.MessageId;
        string[] irt = reply.GetInReplyTo().ToArray();
        string[] refs = reply.GetReferences().ToArray();
        if (pid is null || pid.Length > 512 || pid.Any(ch => ch <= ' ' || ch is '<' or '>'))
        {
            if (irt.Length != 0)
            {
                Fail(st, $"{where}: In-Reply-To without a usable parent id");
            }

            return;
        }

        if (irt.Length != 1 || irt[0] != pid)
        {
            Fail(st, $"{where}: In-Reply-To {string.Join(",", irt)} expected {pid}");
        }

        if (refs.Length == 0 || refs[^1] != pid)
        {
            Fail(st, $"{where}: References does not end with the parent id");
        }

        var want = (parent.References.Count > 0 ? parent.References : parent.InReplyTo).Where(x => x.Length is > 0 and <= 512 && !x.Any(ch => ch <= ' ' || ch is '<' or '>')).ToList();
        if (want.Count == 0 || want[^1] != pid)
        {
            want.Add(pid);
        }

        if (want.Count > 20)
        {
            want.RemoveRange(1, want.Count - 20);
        }

        if (!refs.SequenceEqual(want))
        {
            Fail(st, $"{where}: References has {refs.Length} ids, expected {want.Count}");
        }
    }

    private static void CheckEmbedded(IMailItem parent, MimeEntity top, Stats st, string where)
    {
        MimeMessagePart? mp = top is MimeMultipart m ? m.Children.OfType<MimeMessagePart>().FirstOrDefault() : null;
        if (mp?.Message is not { } inner)
        {
            Fail(st, $"{where}: no embedded message");
            return;
        }

        // A zero-byte message cannot be told from a message of one empty line once it sits between boundary lines, so it is not compared.
        if (parent is MimeMailItem mime && mime.Entity.ToArray().Length > 0)
        {
            st.EmbeddedChecked++;
            if (!inner.ToArray().AsSpan().SequenceEqual(mime.Entity.ToArray()))
            {
                Fail(st, $"{where}: embedded original differs from the source bytes");
            }
        }
    }

    private static void CheckMapi(ComposedMessage c, MapiMailItem source, Stats st, string where)
    {
        if (c.Mapi is null)
        {
            Fail(st, $"{where}: no MAPI message for an Outlook item");
            return;
        }

        st.MapiChecked++;
        var view = new MapiMailItem(c.Mapi, default);
        MimeEntity again = new MapiToMimeConverter().Convert(c.Mapi).Value;
        var view2 = new MapiMailItem(new MimeToMapiConverter().Convert(again).Value, default);
        if (!view.ConversationIndex.Span.SequenceEqual(view2.ConversationIndex.Span)
            || !view.References.SequenceEqual(view2.References)
            || !view.InReplyTo.SequenceEqual(view2.InReplyTo)
            || view.ConversationTopic != view2.ConversationTopic)
        {
            Fail(st, $"{where}: threading properties changed through MAPI -> MIME -> MAPI");
        }

        if (c.Mapi.GetString(MapiPid.SubjectPrefix) is { } p && c.Mapi.GetString(MapiPid.NormalizedSubject) is { } n && p + n != c.Subject)
        {
            Fail(st, $"{where}: subject prefix and normalized subject do not add up");
        }

        if (ConversationIndex.TryParse(source.ConversationIndex.Span, out _) && view.ConversationIndex.Length == source.ConversationIndex.Length + 5)
        {
            if (!view.ConversationIndex.Span[..source.ConversationIndex.Length].SequenceEqual(source.ConversationIndex.Span))
            {
                Fail(st, $"{where}: the parent's index is not a prefix of the new one");
            }
        }
    }

    private static void Fail(Stats st, string text)
    {
        st.Failures++;
        if (st.Examples.Count < 10)
        {
            st.Examples.Add(text);
        }
    }
}
