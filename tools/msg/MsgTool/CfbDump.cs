using System.Text;
using RecursiveAI.OneArchive.Cfb;

namespace MsgTool;

/// <summary>Lists the storages and streams of a message file with their sizes and the entries of every property stream (tag, flags, inline value),
/// to set the files we write beside the ones Outlook writes: <c>MsgTool cfb &lt;file&gt;</c>.</summary>
internal static class CfbDump
{
    public static int Run(string path)
    {
        using var reader = new CompoundFileReader(File.OpenRead(path), null, leaveOpen: false);
        Walk(reader, reader.Root, "", true);
        return 0;
    }

    private static void Walk(CompoundFileReader r, CfbEntry dir, string prefix, bool top)
    {
        foreach (CfbEntry e in r.GetChildren(dir))
        {
            string name = prefix + "/" + e.Name;
            if (e.ObjectType == CfbObjectType.Stream)
            {
                Console.WriteLine($"{name}\t{e.Size}");
                if (e.Name == "__properties_version1.0")
                {
                    var buf = new byte[(int)e.Size];
                    using Stream s = r.OpenEntryStream(e);
                    s.ReadExactly(buf);
                    int header = prefix.Contains("__substg1.0_3701000D") || prefix.Contains("__attach") || prefix.Contains("__recip") ? 8 : (top ? 32 : 24);
                    Console.WriteLine($"    header {Convert.ToHexString(buf.AsSpan(0, Math.Min(header, buf.Length)))}");
                    for (int i = header; i + 16 <= buf.Length; i += 16)
                    {
                        uint tag = BitConverter.ToUInt32(buf, i);
                        uint flags = BitConverter.ToUInt32(buf, i + 4);
                        Console.WriteLine($"    {tag >> 16:X4}{tag & 0xFFFF:X4} flags {flags:X} {Convert.ToHexString(buf.AsSpan(i + 8, 8))}");
                    }
                }
            }
            else
            {
                Console.WriteLine($"{name}/\t{e.ObjectType}");
                Walk(r, e, name, false);
            }
        }
    }
}
