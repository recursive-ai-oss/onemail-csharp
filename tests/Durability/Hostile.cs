using System.Buffers.Binary;
using System.Diagnostics;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Pst.Messaging;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Durability;

/// <summary>
/// Opens damaged and crafted files for writing: random byte changes in the header (checksums fixed up half the time so the change gets past them) and in
/// pages, then an edit and a commit. Anything other than a typed refusal, or a clean run, is reported with the exception type; a case that runs over
/// the limit counts as a hang.
/// </summary>
internal static class Hostile
{
    public static int Run(string baseName, int count, int seed, bool permissive, string corpus)
    {
        byte[] image = Bases.Load(baseName, corpus);
        var rnd = new Random(seed);
        var kinds = new SortedDictionary<string, (int Count, string Sample)>();
        int refused = 0, ok = 0, hangs = 0;
        for (int n = 0; n < count; n++)
        {
            byte[] file = (byte[])image.Clone();
            int caseSeed = rnd.Next();
            Mutate(file, new Random(caseSeed));
            string? outcome = null;
            var sw = Stopwatch.StartNew();
            Task<string?> t = Task.Run(() => Attempt(file, permissive));
            if (!t.Wait(TimeSpan.FromSeconds(20)))
            {
                hangs++;
                File.WriteAllBytes($"hostile-hang-{baseName}-{caseSeed}.pst", file);
                Console.WriteLine($"case seed {caseSeed}: still running after 20 s (file kept)");
                continue;
            }

            outcome = t.Result;
            if (outcome is null)
            {
                ok++;
            }
            else if (outcome.StartsWith("refused", StringComparison.Ordinal))
            {
                refused++;
            }
            else
            {
                kinds.TryGetValue(outcome[..Math.Min(80, outcome.Length)], out (int Count, string Sample) k);
                kinds[outcome[..Math.Min(80, outcome.Length)]] = (k.Count + 1, k.Sample ?? $"seed {caseSeed}: {outcome}");
            }
        }

        Console.WriteLine($"hostile {baseName} {(permissive ? "permissive" : "strict")}: {count} cases, {ok} opened and edited, {refused} refused, {hangs} hangs, {kinds.Values.Sum(v => v.Count)} other exceptions");
        foreach ((string key, (int c, string sample)) in kinds)
        {
            Console.WriteLine($"    {c,4} x {key}\n         {sample[..Math.Min(400, sample.Length)].Replace('\n', ' ')}");
        }

        return kinds.Count == 0 && hangs == 0 ? 0 : 1;
    }

    public static int Diff(string baseName, string file, string corpus)
    {
        byte[] a = Bases.Load(baseName, corpus), b = File.ReadAllBytes(file);
        int shown = 0;
        for (int i = 0; i < Math.Min(a.Length, b.Length) && shown < 30; i++)
        {
            if (a[i] != b[i])
            {
                Console.WriteLine($"0x{i:x}: {a[i]:x2} -> {b[i]:x2}");
                shown++;
            }
        }

        return 0;
    }

    /// <summary>Rebuilds one case from its seed, keeps the mutated file beside the program and prints what happens.</summary>
    public static int Case(string baseName, int caseSeed, bool permissive, string corpus)
    {
        byte[] file = Bases.Load(baseName, corpus);
        Mutate(file, new Random(caseSeed));
        File.WriteAllBytes($"case-{baseName}-{caseSeed}.pst", file);
        Console.WriteLine(Attempt(file, permissive) ?? "ok");
        return Diff(baseName, $"case-{baseName}-{caseSeed}.pst", corpus);
    }

    /// <summary>Lists the NID counters and every place a top-level NID is also the NID of a subnode somewhere (a collision between the two NID spaces).</summary>
    public static int Nids(string file)
    {
        if (!NdbFile.TryOpen(file, out NdbFile? db, out _))
        {
            Console.WriteLine("does not open");
            return 1;
        }

        using (db)
        {
            Console.WriteLine("counters: " + string.Join(" ", db!.Header.NodeCounters.Select((c, i) => c == 0 ? "" : $"{i:x}:{c:x}").Where(x => x.Length > 0)));
            var bids = db.Blocks.Select(b => b.Bid.Value).ToList();
            Console.WriteLine($"bidNextB {db.Header.NextBlockBid:x} bidNextP {db.Header.NextPageBid:x}; {bids.Count} blocks, BIDs {bids.Min():x}..{bids.Max():x}; first {string.Join(" ", bids.Take(8).Select(b => b.ToString("x")))}; internal {bids.Count(b => (b & 2) != 0)}; odd {bids.Count(b => (b & 1) != 0)}");
            var top = new HashSet<uint>();
            foreach (NodeEntry n in db.Nodes)
            {
                top.Add(n.Nid.Value);
            }

            int subnodes = 0, clashes = 0;
            foreach (NodeEntry n in db.Nodes)
            {
                if (n.Sub.IsNull || db.OpenSubnodes(n.Sub) is not { } tree)
                {
                    continue;
                }

                foreach (SubnodeEntry e in tree.Entries)
                {
                    subnodes++;
                    if (top.Contains(e.Nid.Value) && ++clashes <= 8)
                    {
                        Console.WriteLine($"subnode {e.Nid} of node {n.Nid} is also a top-level node");
                    }
                }
            }

            Console.WriteLine($"{subnodes} subnodes, {clashes} are also top-level NIDs");
            if (db.Nodes.TryGet(new Nid(0x2000e4), out NodeEntry item))
            {
                void Dump(Bid sub, string indent)
                {
                    if (sub.IsNull || db.OpenSubnodes(sub) is not { } t)
                    {
                        return;
                    }

                    foreach (SubnodeEntry e in t.Entries)
                    {
                        Console.WriteLine($"{indent}subnode {e.Nid} data {e.Data} sub {e.Sub}");
                        Dump(e.Sub, indent + "    ");
                    }
                }

                Console.WriteLine($"item {item.Nid} data {item.Data} sub {item.Sub} parent {item.Parent}");
                Dump(item.Sub, "  ");
            }
        }

        return 0;
    }

    public static int One(string file, bool permissive)
    {
        string? r = Attempt(File.ReadAllBytes(file), permissive);
        Console.WriteLine(r ?? "ok");
        return 0;
    }

    private static string? Attempt(byte[] file, bool permissive)
    {
        var ms = new MemoryStream();
        ms.Write(file);
        try
        {
            var opts = new PstWriteOptions
            {
                Timestamp = Scenarios.Stamp,
                AutoCommitRows = 0,
                Allow = permissive ? NdbOpenRisks.HeaderChecksum | NdbOpenRisks.AllocationMaps | NdbOpenRisks.DensityList | NdbOpenRisks.Damage : NdbOpenRisks.None,
            };
            using PstWritableStore w = PstStore.OpenForWriting(ms, leaveOpen: true, opts);
            List<PstFolder> folders = Scenarios.Folders(w.View);
            if (folders.Count == 0)
            {
                return null;
            }

            PstFolder f = folders[0];
            Nid added = w.AddMessage(f.Nid, Scenarios.Message("hostile", 1, 1, 3000, true, "h"));
            w.Commit();
            w.DeleteMessage(added);
            w.Commit();
            return null;
        }
        catch (NdbEditRefusedException)
        {
            return "refused";
        }
        catch (Exception e)
        {
            return $"{e.GetType().Name}: {e.Message} @ {e.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("OneMail", StringComparison.Ordinal))?.Trim()}";
        }
    }

    private static void Mutate(byte[] file, Random rnd)
    {
        int edits = rnd.Next(1, 5);
        for (int i = 0; i < edits; i++)
        {
            int region = rnd.Next(4);
            int at = region switch
            {
                0 => rnd.Next(0, 564),
                1 => rnd.Next(180, 252),      // the ROOT
                _ => rnd.Next(0x4200, file.Length),
            };
            if (region == 3)
            {
                at = rnd.Next(0, file.Length);
            }

            switch (rnd.Next(4))
            {
                case 0: file[at] = (byte)rnd.Next(256); break;
                case 1: file[at] ^= (byte)(1 << rnd.Next(8)); break;
                case 2:
                    // a large 64-bit value in an aligned field
                    int a8 = at & ~7;
                    if (a8 + 8 <= file.Length)
                    {
                        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(a8), rnd.Next(3) == 0 ? ulong.MaxValue : (ulong)rnd.NextInt64(0, 1L << rnd.Next(10, 62)));
                    }

                    break;
                default: file[at] = 0; break;
            }

            if (rnd.Next(2) == 0)
            {
                FixUp(file, at);
            }
        }

        if (rnd.Next(2) == 0)
        {
            FixHeader(file);
        }
    }

    private static void FixHeader(byte[] f)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(4), NdbChecksums.Crc32(f.AsSpan(8, 471)));
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(524), NdbChecksums.Crc32(f.AsSpan(8, 516)));
    }

    // Recomputes the trailer of the 512-byte page holding the changed byte, so the change is judged on its content and not on its checksum.
    private static void FixUp(byte[] f, int at)
    {
        if (at < 564)
        {
            FixHeader(f);
            return;
        }

        int ib = at & ~511;
        if (ib + 512 > f.Length)
        {
            return;
        }

        Span<byte> p = f.AsSpan(ib, 512);
        if (p[496] is 0x80 or 0x81 or 0x84 or 0x86)
        {
            ulong bid = BinaryPrimitives.ReadUInt64LittleEndian(p[504..]);
            BinaryPrimitives.WriteUInt32LittleEndian(p[500..], NdbChecksums.Crc32(p[..496]));
            if (p[496] is 0x80 or 0x81)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(p[498..], NdbChecksums.Signature((ulong)ib, bid));
            }
        }
    }
}
