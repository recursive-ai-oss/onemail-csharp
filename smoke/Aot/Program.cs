using RecursiveAI.OneMail;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Client;
using RecursiveAI.OneMail.Cryptography;
using RecursiveAI.OneMail.Render;
using RecursiveAI.OneMail.Pst.Ndb;
using RecursiveAI.OneMail.Text;
using RecursiveAI.OneMail.Tests.Pst;

namespace RecursiveAI.OneMail.AotSmoke;

/// <summary>Touches every package under NativeAOT with trimming; the publish must produce no trim or AOT warning.</summary>
internal static class Program
{
    private static int Main()
    {
        string[] lines =
        [
            CalendarLibrary.Name,
            OneMailLibrary.Describe(),
            ClientLibrary.Describe(),
            CryptographyLibrary.Describe(),
            RenderLibrary.Describe(),
        ];
        foreach (string line in lines)
        {
            Console.WriteLine(line);
        }

        // Text layer: encoded words, charsets (OneHtml tables, ISO-2022, UTF-7), RFC 2231 parameters.
        var collector = new ParameterCollector();
        collector.Add("filename*0*"u8, "utf-8''caf%C3"u8);
        collector.Add("filename*1*"u8, "%A9.txt"u8);
        bool textOk =
            Rfc2047.DecodeText("=?utf-8?B?5pel5pys6Kqe?= =?iso-8859-1?Q?caf=E9?=") == "日本語café"
            && MailEncoding.ForLabel("shift_jis")!.GetString([0x93, 0xFA, 0x96, 0x7B]) == "日本"
            && MailEncoding.ForLabel("iso-2022-jp")!.GetString([0x1B, 0x24, 0x42, 0x46, 0x7C, 0x1B, 0x28, 0x42]) == "日"
            && MailEncoding.ForCodePage(50221) is not null
            && ImapUtf7.Decode("&ZeVnLIqe-"u8) == "日本語"
            && Utf7.Decode("+ZeVnLIqe-"u8) == "日本語"
            && collector.Resolve()[0].Value == "café.txt"
            && Rfc2047.DecodeText(Rfc2047.Encode("café 日")) == "café 日";
        Console.WriteLine("text: " + (textOk ? "ok" : "FAILED"));

        // Node database: write a small 4 KiB file (compressed blocks) and a Unicode one, read both back through the trees.
        bool ndbOk = true;
        foreach (NdbFormat format in new[] { NdbFormat.Unicode, NdbFormat.Unicode4K })
        {
            byte[] payload = new byte[3000];
            for (int i = 0; i < payload.Length; i++) { payload[i] = (byte)(i % 11); }
            byte[] file = NdbTestBuilder.Build(format, [new TestNode { Nid = 0x21, Data = payload }], new TestBuild { Compress = true }).Bytes;
            ndbOk &= NdbFile.TryOpen(file, out NdbFile? ndb, out _) && ndb is not null;
            if (ndb is null) { continue; }
            using (ndb)
            {
                ndbOk &= ndb.Nodes.TryGet(new Nid(0x21), out NodeEntry node);
                using NdbDataTree? data = ndb.OpenDataTree(node.Data);
                byte[] back = new byte[3000];
                ndbOk &= data is not null && data.Read(0, back) == 3000 && back.AsSpan().SequenceEqual(payload) && ndb.Defects.Count == 0;
            }
        }

        Console.WriteLine("ndb: " + (ndbOk ? "ok" : "FAILED"));

        return textOk && ndbOk && OneMailLibrary.Describe() == "RecursiveAI.OneMail (on RecursiveAI.OneMail.Calendar)" ? 0 : 1;
    }
}
