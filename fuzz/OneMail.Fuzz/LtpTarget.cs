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
    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        byte[] file = LtpWalk.FuzzFile(data);

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
