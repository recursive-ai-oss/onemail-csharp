using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using RecursiveAI.OneMail.Pst.Messaging;
using RecursiveAI.OneMail.Pst.Recovery;
using RecursiveAI.OneMail.Recovery;
using RecursiveAI.OneMail.Tests.Pst;

namespace RecursiveAI.OneMail.PstOracle;

/// <summary>
/// Recovery campaigns over a store: damage it in each class, recover, count what came back against the intact original (property-exact by message
/// signature), repair into a fresh PST and check that opens clean.
///   PstOracle pstdamage &lt;in&gt; &lt;out&gt; &lt;header|roots|amaps|density|corrupt|truncate&gt; [param] [seed]
///   PstOracle pstrecover &lt;in&gt; &lt;out.pst&gt; [report.json] [--forensic]
///   PstOracle pstcampaign &lt;intact&gt; &lt;work dir&gt;
/// </summary>
internal static class RecoveryCommands
{
    public static int Damage(string input, string output, string kind, double param, int seed)
    {
        File.Copy(input, output, true);
        using var f = new FileStream(output, FileMode.Open, FileAccess.ReadWrite);
        switch (kind)
        {
            case "header": PstDamage.ZeroHeader(f); break;
            case "roots": PstDamage.ZeroRoots(f); break;
            case "amaps": PstDamage.ZeroAllocationMaps(f); break;
            case "density": PstDamage.ZeroDensityList(f); break;
            case "corrupt": Console.WriteLine("units damaged " + PstDamage.Corrupt(f, param, seed)); break;
            case "truncate": PstDamage.Truncate(f, param); break;
            default: Console.Error.WriteLine("unknown class " + kind); return 2;
        }

        return 0;
    }

    public static int ScanDamaged(string path)
    {
        if (!RecursiveAI.OneMail.Pst.Ndb.NdbFile.TryOpen(path, out RecursiveAI.OneMail.Pst.Ndb.NdbFile? f, out _) || f is null) { return 1; }
        using (f)
        {
            var clean = new HashSet<long>();
            foreach (var it in f.Scan()) { clean.Add(it.Ib * 4 + (int)it.Kind); }
            int n = 0;
            foreach (var it in f.Scan(new RecursiveAI.OneMail.Pst.Ndb.NdbScanOptions { IncludeDamaged = true }))
            {
                if (clean.Contains(it.Ib * 4 + (int)it.Kind)) { continue; }
                if (n++ < 40) { Console.WriteLine($"{it.Kind} ib 0x{it.Ib:x} size {it.Size} type 0x{it.PageType:x2} bid 0x{it.Bid:x} cb {it.DataSize} sig {it.Check.SignatureValid} crc {it.Check.CrcValid}"); }
            }

            Console.WriteLine("not clean: " + n + ", clean: " + clean.Count);
        }

        return 0;
    }

    public static int Recover(string input, string output, string? report, bool forensic)
    {
        var sw = Stopwatch.StartNew();
        if (!PstRecovery.TryOpen(input, out PstRecovery? r, new PstRecoveryOptions { Forensic = forensic }) || r is null)
        {
            Console.WriteLine("nothing recoverable");
            return 1;
        }

        using (r)
        {
            PstRepairSummary s = r.RepairTo(output, report);
            Console.WriteLine($"written {s.Written} failed {s.Failed} live {s.ByStatus[0]} soft {s.ByStatus[1]} orphaned {s.ByStatus[2]} free {s.ByStatus[3]} partial {s.ByStatus[4]}; header usable {r.HeaderUsable}, rebuilt {r.IndexRebuilt}; {sw.ElapsedMilliseconds} ms; peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024)} MiB");
        }

        return 0;
    }

    // The message's own properties only: what a message read from a block no node names can be compared on (its recipients and attachments were in subnodes).
    private static string PropsSignature(RecursiveAI.OneMail.Mapi.MapiMessage m)
    {
        var lines = m.Properties.Select(p => p.Tag.Value.ToString("x8", System.Globalization.CultureInfo.InvariantCulture) + "=" + string.Join(",", Enumerable.Range(0, p.ValueCount).Select(i => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(p.GetBytes(i).Span.ToArray()).AsSpan(0, 8))))).Order(StringComparer.Ordinal);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join(";", lines))));
    }

    private static Dictionary<string, int> PropSignatures(string path)
    {
        var d = new Dictionary<string, int>();
        if (!PstStore.TryOpen(path, out PstStore? s, out _) || s is null)
        {
            return d;
        }

        using (s)
        {
            foreach (PstMessage m in s.EnumerateMessages(PstMessageSet.All))
            {
                string k = PropsSignature(m);
                d[k] = d.GetValueOrDefault(k) + 1;
            }
        }

        return d;
    }

    private static Dictionary<string, int> Signatures(string path)
    {
        var d = new Dictionary<string, int>();
        if (!PstStore.TryOpen(path, out PstStore? s, out _) || s is null)
        {
            return d;
        }

        using (s)
        {
            foreach (PstMessage m in s.EnumerateMessages(PstMessageSet.All))
            {
                string k = PstStoreCompare.Signature(m);
                d[k] = d.GetValueOrDefault(k) + 1;
            }
        }

        return d;
    }

    public static int Campaign(string intact, string work)
    {
        Directory.CreateDirectory(work);
        Dictionary<string, int> original = Signatures(intact);
        Dictionary<string, int> originalProps = PropSignatures(intact);
        int total = original.Values.Sum();
        Console.WriteLine($"original {Path.GetFileName(intact)}: {total} messages, {new FileInfo(intact).Length} bytes");
        Console.WriteLine("class\titems\texact\texactProps\tpartial\tother\tlive\torphaned\tfree\texceptions\trepaired\trepairedClean\trecallExact");
        var classes = new List<(string Name, string Kind, double Param, int Seed)>
        {
            ("intact", "none", 0, 0), ("header", "header", 0, 0), ("roots", "roots", 0, 0), ("amaps", "amaps", 0, 0), ("density", "density", 0, 0),
            ("corrupt0.1%", "corrupt", 0.001, 1), ("corrupt1%", "corrupt", 0.01, 2), ("corrupt5%", "corrupt", 0.05, 3),
            ("truncate25", "truncate", 0.25, 0), ("truncate50", "truncate", 0.5, 0), ("truncate90", "truncate", 0.9, 0),
        };
        int bad = 0;
        foreach ((string name, string kind, double param, int seed) in classes)
        {
            string damaged = Path.Combine(work, name.Replace('%', 'p') + ".pst");
            if (kind == "none")
            {
                File.Copy(intact, damaged, true);
            }
            else
            {
                Damage(intact, damaged, kind, param, seed);
            }

            int items = 0, exact = 0, exactProps = 0, partial = 0, other = 0, live = 0, orphaned = 0, free = 0, exceptions = 0, repaired = 0;
            bool clean = false;
            var left = new Dictionary<string, int>(original);
            var leftProps = new Dictionary<string, int>(originalProps);
            try
            {
                if (PstRecovery.TryOpen(damaged, out PstRecovery? r) && r is not null)
                {
                    using (r)
                    {
                        foreach (PstRecoveredItem it in r.EnumerateItems())
                        {
                            items++;
                            switch (it.Provenance.Status)
                            {
                                case RecoveryStatus.Live: live++; break;
                                case RecoveryStatus.Orphaned: orphaned++; break;
                                case RecoveryStatus.FreeSpace: free++; break;
                            }

                            string sig = PstStoreCompare.Signature(it.Message);
                            bool nodeless = it.Provenance.Defects.Any(d => d.Code == nameof(PstRecoveryDefectKind.SubnodesNotFound));
                            string psig = PropsSignature(it.Message);
                            if (it.Provenance.Status == RecoveryStatus.Partial) { partial++; }
                            else if (nodeless && leftProps.TryGetValue(psig, out int pn) && pn > 0) { leftProps[psig] = pn - 1; exactProps++; }
                            else if (left.TryGetValue(sig, out int n) && n > 0) { left[sig] = n - 1; exact++; }
                            else { other++; }
                        }

                        string repairedPath = Path.Combine(work, name.Replace('%', 'p') + ".repaired.pst");
                        repaired = r.RepairTo(repairedPath, Path.Combine(work, name.Replace('%', 'p') + ".json")).Written;
                        if (PstStore.TryOpen(repairedPath, out PstStore? fresh, out _) && fresh is not null)
                        {
                            using (fresh)
                            {
                                int count = fresh.EnumerateMessages(PstMessageSet.All).Count();
                                clean = fresh.Database.Defects.Count == 0 && fresh.Defects.Count == 0 && count == repaired;
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                exceptions++;
                Console.Error.WriteLine(name + ": " + e);
                bad++;
            }

            if (!clean && repaired > 0) { bad++; }
            Console.WriteLine($"{name}\t{items}\t{exact}\t{exactProps}\t{partial}\t{other}\t{live}\t{orphaned}\t{free}\t{exceptions}\t{repaired}\t{clean}\t{(total == 0 ? 0 : 100.0 * (exact + exactProps) / total):F1}%");
        }

        return bad == 0 ? 0 : 3;
    }
}
