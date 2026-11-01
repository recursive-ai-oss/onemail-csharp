using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Pst.Messaging;
using RecursiveAI.OneMail.Pst.Ndb;
using RecursiveAI.OneMail.Tests.Pst;

namespace RecursiveAI.OneMail.Durability;

/// <summary>A long random run of operations on a real file, checked after every commit.</summary>
internal static class Soak
{
    public static int Run(string dir, string baseName, int ops, int seed, int commitEvery, string corpus)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"soak-{baseName}-{seed}.pst");
        byte[] baseBytes = Bases.Load(baseName, corpus);
        File.WriteAllBytes(path, baseBytes);
        HashSet<string> baseline = [.. NdbChecker.Check(baseBytes).Errors];
        HashSet<string> baseSoft = [.. NdbChecker.Check(baseBytes).Soft];
        int softSeen = 0, checks = 0;
        var rnd = new Random(seed);
        int baseProblems;
        if (!PstStore.TryOpen(path, out PstStore? s0, out _))
        {
            Console.WriteLine("the base file does not open");
            return 1;
        }

        using (s0)
        {
            baseProblems = StoreChecks.Consistency(s0!).Count;
        }

        HashSet<long> initialFree = FreeSlots(path);
        var parents = new Dictionary<Nid, Nid>();        // folder -> parent
        var created = new HashSet<Nid>();                // folders this run made (the only ones it renames, moves and deletes)
        var messages = new Dictionary<Nid, Nid>();       // message -> folder
        var originals = new Dictionary<Nid, MapiMessage>();
        int adds = 0, deletes = 0, moves = 0, replaces = 0, folderOps = 0, commits = 0;
        var problems = new List<string>();

        using (PstWritableStore w = PstStore.OpenForWriting(path, new PstWriteOptions { Timestamp = Scenarios.Stamp, AutoCommitRows = 0 }))
        {
            foreach (PstFolder f in w.View.EnumerateFolders().Where(f => !f.IsSearchFolder))
            {
                if (f.ParentFolder is not null)
                {
                    parents[f.Nid] = f.ParentFolder.Nid;
                    foreach (Nid m in f.EnumerateMessageIds())
                    {
                        messages[m] = f.Nid;
                    }
                }
            }

            for (int op = 1; op <= ops; op++)
            {
                List<Nid> folders = [.. parents.Keys];
                int pick = rnd.Next(100);
                if (pick < 45 || messages.Count == 0)
                {
                    Nid folder = folders[rnd.Next(folders.Count)];
                    MapiMessage m = Scenarios.Message($"soak {op}", rnd.Next(0, 3), rnd.Next(10) == 0 ? rnd.Next(1, 3) : 0, rnd.Next(100, 30_000), rnd.Next(2) == 0, new string('s', rnd.Next(10, 2000)), rnd.Next(8) == 0 ? "Name" + rnd.Next(5) : null);
                    Nid nid = w.AddMessage(folder, m);
                    messages[nid] = folder;
                    originals[nid] = m;
                    adds++;
                }
                else if (pick < 65)
                {
                    Nid victim = messages.Keys.ElementAt(rnd.Next(messages.Count));
                    w.DeleteMessage(victim);
                    messages.Remove(victim);
                    originals.Remove(victim);
                    deletes++;
                }
                else if (pick < 78)
                {
                    Nid mover = messages.Keys.ElementAt(rnd.Next(messages.Count));
                    Nid to = folders[rnd.Next(folders.Count)];
                    w.MoveMessage(mover, to);
                    messages[mover] = to;
                    moves++;
                }
                else if (pick < 85)
                {
                    Nid target = messages.Keys.ElementAt(rnd.Next(messages.Count));
                    MapiMessage m = Scenarios.Message($"soak replaced {op}", rnd.Next(0, 3), rnd.Next(5) == 0 ? 1 : 0, rnd.Next(100, 20_000), rnd.Next(2) == 0, new string('r', rnd.Next(10, 1500)));
                    w.ReplaceMessage(target, m);
                    originals[target] = m;
                    replaces++;
                }
                else if (pick < 91)
                {
                    Nid parent = folders[rnd.Next(folders.Count)];
                    Nid made = w.CreateFolder(parent, "soak folder " + op);
                    parents[made] = parent;
                    created.Add(made);
                    folderOps++;
                }
                else if (pick < 94 && created.Count > 0)
                {
                    Nid f = created.ElementAt(rnd.Next(created.Count));
                    w.RenameFolder(f, "renamed " + op);
                    folderOps++;
                }
                else if (pick < 97 && created.Count > 0)
                {
                    Nid f = created.ElementAt(rnd.Next(created.Count));
                    Nid to = folders[rnd.Next(folders.Count)];
                    if (!IsWithin(parents, to, f))
                    {
                        w.MoveFolder(f, to);
                        parents[f] = to;
                        folderOps++;
                    }
                }
                else if (created.Count > 0)
                {
                    Nid f = created.ElementAt(rnd.Next(created.Count));
                    w.DeleteFolder(f);
                    foreach (Nid gone in folders.Where(x => IsWithin(parents, x, f)).ToList())
                    {
                        parents.Remove(gone);
                        created.Remove(gone);
                        foreach (Nid m in messages.Where(kv => kv.Value == gone).Select(kv => kv.Key).ToList())
                        {
                            messages.Remove(m);
                            originals.Remove(m);
                        }
                    }

                    folderOps++;
                }

                if (op % commitEvery == 0 || op == ops)
                {
                    w.Commit();
                    commits++;
                    Check(w.View, baseProblems, originals, problems, op, initialFree, baseline, baseSoft, ref softSeen, ref checks);
                }
            }
        }

        // Fresh open of the finished file, as another program would see it.
        if (!PstStore.TryOpen(path, out PstStore? fin, out _))
        {
            problems.Add("final file does not open");
        }
        else
        {
            using (fin)
            {
                Check(fin!, baseProblems, originals, problems, ops, initialFree, baseline, baseSoft, ref softSeen, ref checks);
                int unreadable = 0;
                foreach (Nid m in messages.Keys)
                {
                    if (!fin!.TryOpenMessage(m, out _))
                    {
                        unreadable++;
                    }
                }

                if (unreadable > 0)
                {
                    problems.Add($"{unreadable} items the run expects are not in the file");
                }
            }
        }

        Console.WriteLine($"soak {baseName} seed {seed}: {ops} operations ({adds} adds, {deletes} deletes, {moves} moves, {replaces} replaces, {folderOps} folder operations), {commits} commits, {messages.Count} items kept in sight, {checks} checker runs ({softSeen} soft findings: leaks and lagging counters), {problems.Count} problems");
        foreach (string k in SoftKinds)
        {
            Console.WriteLine("    soft: " + k);
        }

        foreach (string p in problems.Take(10))
        {
            Console.WriteLine("    " + p);
        }

        return problems.Count == 0 ? 0 : 1;
    }

    private static readonly HashSet<string> SoftKinds = [];

    private static bool IsWithin(Dictionary<Nid, Nid> parents, Nid folder, Nid ancestor)
    {
        for (Nid at = folder; ; )
        {
            if (at == ancestor)
            {
                return true;
            }

            if (!parents.TryGetValue(at, out Nid up) || up == at)
            {
                return false;
            }

            at = up;
        }
    }

    private static void Check(PstStore view, int baseProblems, Dictionary<Nid, MapiMessage> originals, List<string> problems, int op, HashSet<long> initialFree,
        HashSet<string> baseline, HashSet<string> baseSoft, ref int softSeen, ref int checks)
    {
        NdbFile db = view.Database;

        // The independent checker: recomputes references, space in use, free counts, the density list and the BID and NID counters from the two trees.
        byte[] image = new byte[db.Length];
        db.ReadRaw(0, image);
        NdbChecker.Report rep = NdbChecker.Check(image);
        checks++;
        foreach (string e in rep.Errors.Where(e => !baseline.Contains(e)).Take(3))
        {
            problems.Add($"op {op}: node database checker: {e}");
        }

        foreach (string e in rep.Soft.Where(e => !baseSoft.Contains(e)))
        {
            softSeen++;
            if (SoftKinds.Count < 12)
            {
                SoftKinds.Add(System.Text.RegularExpressions.Regex.Replace(e, "[0-9a-f]{3,}|[0-9]+", "#"));
            }
        }

        NdbAllocationSummary sum = db.ValidateAllocation();
        _ = db.ReadDensityList();
        if (sum.InvalidCount != 0 || sum.FreeBytes != sum.HeaderFreeBytes)
        {
            problems.Add($"op {op}: allocation maps: invalid {sum.InvalidCount}, maps free {sum.FreeBytes}, header {sum.HeaderFreeBytes}");
        }

        foreach (BlockEntry b in db.Blocks)
        {
            if (!db.IsAllocated((long)b.Block.Ib, ((b.Size + 16 + 63) / 64) * 64))
            {
                problems.Add($"op {op}: block {b.Bid} is not marked allocated");
                break;
            }
        }

        foreach (NdbDefect d in db.Defects.Take(3))
        {
            problems.Add($"op {op}: defect {d.Kind}: {d.Detail}");
        }

        List<string> consistency = StoreChecks.Consistency(view);
        if (consistency.Count > baseProblems)
        {
            problems.Add($"op {op}: {consistency.Count} consistency problems, the file began with {baseProblems}: {consistency[0]}");
        }

        // Every item the run added and still holds reads back property for property.
        PstStoreCompare.Result r = PstStoreCompare.Compare(originals.Values, view, includeAssociated: false);
        if (r.Exact + r.WithAdditions < r.Source || r.PropertiesNotCarried > 0)
        {
            problems.Add($"op {op}: {r.Source - r.Exact - r.WithAdditions} of {r.Source} added items do not read back exactly ({r.PropertiesNotCarried} properties differ)");
        }

        // Space that was not free when the run began, or lies beyond the file as it began, and is free now must have been wiped.
        int dirty = 0;
        byte[] slot = new byte[64];
        foreach (long ib in FreeSlots(db))
        {
            if (initialFree.Contains(ib) || ib + 64 > db.Length)
            {
                continue;
            }

            db.ReadRaw(ib, slot);
            if (slot.AsSpan().ContainsAnyExcept((byte)0))
            {
                dirty++;
            }
        }

        if (dirty > 0)
        {
            problems.Add($"op {op}: {dirty} freed 64-byte slots still hold data");
        }
    }

    private static HashSet<long> FreeSlots(string path)
    {
        if (!NdbFile.TryOpen(path, out NdbFile? f, out _))
        {
            return [];
        }

        using (f)
        {
            return [.. FreeSlots(f!)];
        }
    }

    private static IEnumerable<long> FreeSlots(NdbFile db)
    {
        byte[] bits = new byte[496];
        for (long k = 0; k < db.AMapCount; k++)
        {
            if (!db.TryReadAMap(k, bits, out _))
            {
                continue;
            }

            long start = db.AMapOffset(k);
            for (int s = 0; s < 3968; s++)
            {
                if ((bits[s >> 3] & (0x80 >> (s & 7))) == 0)
                {
                    yield return start + (s * 64L);
                }
            }
        }
    }
}
