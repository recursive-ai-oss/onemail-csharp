using System.Collections.Concurrent;
using System.Diagnostics;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Mime.Reading;

// Corpus round-trip and allocation driver for the MIME layer.
//
//   dir   <root> [--skip N] [--take M]   every file under <root> (sorted by path) is one message
//   mbox  <file> [--skip N] [--take M]   messages split at "From " lines (a test-only splitter; the mailbox readers are separate)
//   (both accept --threads N, default 8)
//   alloc <mbox file>                    bytes allocated per message while enumerating reader events with one reused reader
//
// For each message: parse into the entity tree without an exception, write it back, compare byte for byte, and check
// that the reader's events cover the input exactly. Non-identical or failing items are listed (path or index).
internal static class Program
{
    private static int threads = 8;

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: dir|mbox|alloc <path> [--skip N] [--take M]");
            return 2;
        }

        string mode = args[0];
        string path = args[1];
        int skip = IntOption(args, "--skip", 0);
        int take = IntOption(args, "--take", int.MaxValue);
        threads = IntOption(args, "--threads", 8);
        return mode switch
        {
            "dir" => RunDir(path, skip, take),
            "mbox" => RunMbox(path, skip, take),
            "alloc" => RunAlloc(path),
            _ => 2,
        };
    }

    private static int IntOption(string[] args, string name, int fallback)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? int.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture) : fallback;
    }

    private sealed class Totals
    {
        public long Checked;
        public long Identical;
        public long Bytes;
        public readonly ConcurrentBag<string> Bad = [];
        public readonly ConcurrentDictionary<MimeDefectKind, long> Defects = new();
        public long WithDefects;
    }

    private static int RunDir(string root, int skip, int take)
    {
        string[] files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Skip(skip).Take(take).ToArray();
        var t = new Totals();
        var sw = Stopwatch.StartNew();
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = threads }, f =>
        {
            byte[] data = File.ReadAllBytes(f);
            Check(t, data, Path.GetRelativePath(root, f));
        });
        return Report(t, sw);
    }

    private static int RunMbox(string file, int skip, int take)
    {
        byte[] all = File.ReadAllBytes(file);
        List<(int Start, int Length)> messages = Split(all);
        var t = new Totals();
        var sw = Stopwatch.StartNew();
        int endIndex = (int)Math.Min(messages.Count, (long)skip + take);
        Parallel.For(skip, endIndex, new ParallelOptions { MaxDegreeOfParallelism = threads }, i =>
        {
            (int start, int length) = messages[i];
            Check(t, all.AsMemory(start, length).ToArray(), "message #" + i);
        });
        Console.WriteLine($"messages in file: {messages.Count}");
        return Report(t, sw);
    }

    // Test-only splitter: a message starts after a line beginning "From " that follows an empty line or the file start.
    private static List<(int Start, int Length)> Split(byte[] all)
    {
        var starts = new List<int>();
        int lineStart = 0;
        bool prevBlank = true;
        while (lineStart < all.Length)
        {
            int nl = Array.IndexOf(all, (byte)'\n', lineStart);
            int next = nl < 0 ? all.Length : nl + 1;
            int lineEnd = nl < 0 ? all.Length : nl;
            if (lineEnd > lineStart && all[lineEnd - 1] == (byte)'\r')
            {
                lineEnd--;
            }

            ReadOnlySpan<byte> line = all.AsSpan(lineStart, lineEnd - lineStart);
            if (prevBlank && line.StartsWith("From "u8))
            {
                starts.Add(next);
            }

            prevBlank = line.IsEmpty;
            lineStart = next;
        }

        var result = new List<(int, int)>();
        for (int i = 0; i < starts.Count; i++)
        {
            int end = i + 1 < starts.Count ? FromLineStart(all, starts[i + 1]) : all.Length;
            result.Add((starts[i], end - starts[i]));
        }

        return result;
    }

    // The position where the "From " line before `afterFromLine` begins.
    private static int FromLineStart(byte[] all, int afterFromLine)
    {
        int i = afterFromLine - 1; // the '\n' that ends the From line
        while (i > 0 && all[i - 1] != (byte)'\n')
        {
            i--;
        }

        return i;
    }

    private static void Check(Totals t, byte[] data, string name)
    {
        try
        {
            MimeEntity root = MimeEntity.Parse(data);
            byte[] back = root.ToArray();
            bool same = back.AsSpan().SequenceEqual(data);

            using var reader = new MimeReader(data);
            long pos = 0;
            bool covered = true;
            while (reader.Read())
            {
                if (reader.Kind == MimeEventKind.Defect)
                {
                    continue;
                }

                if (reader.Length > 0)
                {
                    covered &= reader.Offset == pos && data.AsSpan((int)pos, reader.Length).SequenceEqual(reader.Raw);
                    pos += reader.Length;
                }
            }

            covered &= pos == data.Length;
            bool any = false;
            foreach (MimeDefect d in root.EnumerateDefects())
            {
                t.Defects.AddOrUpdate(d.Kind, 1, (_, n) => n + 1);
                any = true;
            }

            if (any)
            {
                Interlocked.Increment(ref t.WithDefects);
            }

            Interlocked.Increment(ref t.Checked);
            Interlocked.Add(ref t.Bytes, data.Length);
            if (same && covered)
            {
                Interlocked.Increment(ref t.Identical);
            }
            else
            {
                t.Bad.Add($"{name} (tree identical={same}, events cover={covered})");
            }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref t.Checked);
            t.Bad.Add($"{name} threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static int Report(Totals t, Stopwatch sw)
    {
        Console.WriteLine($"checked={t.Checked} byte-identical={t.Identical} not-identical-or-failed={t.Bad.Count} bytes={t.Bytes} messages-with-defects={t.WithDefects} elapsed={sw.Elapsed.TotalSeconds:F1}s peak-working-set-MB={Process.GetCurrentProcess().PeakWorkingSet64 / (1 << 20)}");
        foreach (KeyValuePair<MimeDefectKind, long> kv in t.Defects.OrderByDescending(k => k.Value))
        {
            Console.WriteLine($"  defect {kv.Key}: {kv.Value}");
        }

        foreach (string b in t.Bad.Order(StringComparer.Ordinal).Take(200))
        {
            Console.WriteLine("NOT IDENTICAL: " + b);
        }

        return t.Bad.IsEmpty ? 0 : 1;
    }

    private static int RunAlloc(string file)
    {
        byte[] all = File.ReadAllBytes(file);
        List<(int Start, int Length)> messages = Split(all);
        ReadOnlyMemory<byte> mem = all;
        using var reader = new MimeReader(mem.Slice(messages[0].Start, messages[0].Length));
        long events = Pass(reader, mem, messages);
        events = Pass(reader, mem, messages); // second pass: buffers and tables are warm
        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        events = Pass(reader, mem, messages);
        sw.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"messages={messages.Count} events={events} bytes={all.Length} allocated={allocated} B ({(double)allocated / messages.Count:F3} B/message) time={sw.Elapsed.TotalMilliseconds:F1} ms ({all.Length / 1e6 / sw.Elapsed.TotalSeconds:F0} MB/s)");
        return allocated < messages.Count ? 0 : 1; // under one byte per message: only runtime bookkeeping
    }

    private static long Pass(MimeReader reader, ReadOnlyMemory<byte> all, List<(int Start, int Length)> messages)
    {
        long events = 0;
        long sink = 0;
        foreach ((int start, int length) in messages)
        {
            reader.Reset(all.Slice(start, length));
            while (reader.Read())
            {
                events++;
                sink += reader.Length;
            }
        }

        return sink < 0 ? -1 : events; // uses `sink` so the loop cannot be dropped
    }
}
