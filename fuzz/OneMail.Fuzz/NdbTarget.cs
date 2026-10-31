using RecursiveAI.OneMail.Pst.Ndb;
using RecursiveAI.OneMail.Tests.Pst;

namespace RecursiveAI.OneMail.Fuzz;

// The node-database reader on damaged files. A fuzzer rarely finds a valid header and CRCs from nothing, so most inputs are
// patch lists over a valid file made by the test builder:
//   byte 0, bits 0-1: which file (0 ANSI, 1 Unicode, 2 4 KiB; 3 = the input is the file)
//   then 4-byte records: 24-bit offset (little endian, taken modulo the file length) and the byte to put there.
// A patched header or page keeps its old checksum, so most patches test the defect paths; the reader must still never
// throw, hang or grow without bound.
internal static class NdbTarget
{
    private static readonly byte[][] Bases = [Make(NdbFormat.Ansi), Make(NdbFormat.Unicode), Make(NdbFormat.Unicode4K)];

    private static byte[] Make(NdbFormat format)
    {
        var big = new byte[20000];
        new Random(5).NextBytes(big);
        var nodes = new List<TestNode>();
        for (uint i = 1; i <= 12; i++)
        {
            nodes.Add(new TestNode { Nid = (i * 32) + 4, Data = big.AsSpan(0, (int)(i * 40)).ToArray() });
        }

        nodes.Add(new TestNode { Nid = 0x8082, Data = big });
        nodes.Add(new TestNode { Nid = 0x8003, Data = big[..50], Subnodes = [new TestNode { Nid = 0x671, Data = big[..9] }, new TestNode { Nid = 0x692, Data = big[..300] }] });
        return NdbTestBuilder.Build(format, nodes, new TestBuild { LeafCapacity = 4, Compress = format == NdbFormat.Unicode4K }).Bytes;
    }

    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        byte[] file;
        int which = data[0] & 3;
        if (which == 3)
        {
            file = data[1..].ToArray();
        }
        else
        {
            file = (byte[])Bases[which].Clone();
            for (int i = 1; i + 3 < data.Length; i += 4)
            {
                int pos = (data[i] | (data[i + 1] << 8) | (data[i + 2] << 16)) % file.Length;
                file[pos] = data[i + 3];
            }
        }

        // Defects are the expected outcome; only the library's own typed results may come out.
        var options = new NdbOptions { MaxDefects = 1000, MaxDataTreeLeaves = 10_000, MaxSubnodeEntries = 10_000 };
        if (!NdbFile.TryOpen(file, out NdbFile? f, out _, options) || f is null)
        {
            return;
        }

        using (f)
        {
            var buf = new byte[f.MaxBlockDataSize];
            int budget = 5000;
            foreach (NodeEntry n in f.Nodes)
            {
                if (--budget < 0)
                {
                    break;
                }

                using NdbDataTree? t = f.OpenDataTree(n.Data);
                t?.Read(0, buf.AsSpan(0, (int)Math.Min(t.Length, buf.Length)));
                if (!n.Sub.IsNull && f.OpenSubnodes(n.Sub) is { } st)
                {
                    foreach (SubnodeEntry se in st.Entries.Take(50))
                    {
                        using NdbDataTree? x = f.OpenDataTree(se.Data);
                    }
                }

                f.Nodes.TryGet(new Nid(n.Nid.Value ^ 0x20), out _);
            }

            budget = 5000;
            foreach (BlockEntry b in f.Blocks)
            {
                if (--budget < 0)
                {
                    break;
                }

                f.TryReadBlock(b, buf, out _, out _);
            }

            f.ValidateAllocation();
            f.ReadDensityList();
            foreach (NdbScanItem _ in f.Scan(new NdbScanOptions { IncludeDamaged = true }))
            {
            }
        }
    }
}
