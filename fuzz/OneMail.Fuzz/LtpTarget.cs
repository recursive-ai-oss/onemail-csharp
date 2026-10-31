using RecursiveAI.OneMail.Pst.Ndb;
using RecursiveAI.OneMail.Tests.Pst;

namespace RecursiveAI.OneMail.Fuzz;

// The heap, BTH, property-context and table-context readers on damaged blocks. A fuzzer will not find valid checksums and
// headers from nothing, so inputs are patch lists over valid files built by the test builder:
//   byte 0, bits 0-1: which file (0 ANSI, 1 Unicode, 2 4 KiB; 3 = the input is the file)
//   then 4-byte records: 24-bit offset (taken modulo the data region of the file) and the byte to put there.
// A patched block keeps its old checksum, which the block reader reports and carries on from, so patches reach the LTP code.
// Every PC and TC in the file is then opened and read in full; only typed results may come out.
internal static class LtpTarget
{
    private static readonly (byte[] File, long From, long To)[] Bases = [Make(NdbFormat.Ansi), Make(NdbFormat.Unicode), Make(NdbFormat.Unicode4K)];

    private static (byte[], long, long) Make(NdbFormat format)
    {
        var big = new byte[9000];
        new Random(5).NextBytes(big);
        var nodes = new List<TestNode>
        {
            PcBuilder.Build(0x2004, [new PcSpec(0x37, 0x1F, "subject"u8.ToArray()), new PcSpec(0x0E07, 3, [1, 0, 0, 0]), new PcSpec(0x1013, 0x102, big, InSubnode: true), new PcSpec(0x3701, 0x102, big[..3000]), new PcSpec(0x6801, 0x101F, [2, 0, 0, 0, 12, 0, 0, 0, 14, 0, 0, 0, 65, 0, 66, 0])], nodeCapacity: 2, blockCapacity: 120),
            TcBuilder.Build(0x80E, new TcSpec { Columns = [(0x3001, 0x1F), (0x0E08, 3), (0x3FF9, 0x48)], Rows = Enumerable.Range(0, 40).Select(i => ((uint)(0x2004 + (i * 32)), new byte[]?[] { i % 4 == 0 ? null : "abcdefgh"u8.ToArray(), BitConverter.GetBytes(i), new byte[16] })).ToList(), RowsInSubnode = true, MatrixBlockSize = 400, BlockCapacity = 300 }),
            TcBuilder.Build(0x80F, new TcSpec { Columns = [(0x3001, 0x1F)], Rows = Enumerable.Range(0, 6).Select(i => ((uint)(0x2004 + (i * 32)), new byte[]?[] { "xyz"u8.ToArray() })).ToList() }),
        };
        var built = NdbTestBuilder.Build(format, nodes, new TestBuild { Compress = format == NdbFormat.Unicode4K });
        return (built.Bytes, built.BlockOffsets.Values.Min(), built.BlockOffsets.Values.Max() + 64);
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
            (byte[] baseFile, long from, long to) = Bases[which];
            file = (byte[])baseFile.Clone();
            for (int i = 1; i + 3 < data.Length; i += 4)
            {
                long pos = from + ((data[i] | (data[i + 1] << 8) | (data[i + 2] << 16)) % (to - from));
                file[pos] = data[i + 3];
            }
        }

        var options = new NdbOptions { MaxDefects = 1000, MaxDataTreeLeaves = 10_000, MaxSubnodeEntries = 10_000 };
        if (!NdbFile.TryOpen(file, out NdbFile? f, out _, options) || f is null)
        {
            return;
        }

        using (f)
        {
            LtpWalk.ReadEverything(f, budget: 5000);
        }
    }
}
