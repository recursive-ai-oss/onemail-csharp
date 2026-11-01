using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Pst.Messaging;
using RecursiveAI.OneMail.Pst.Recovery;
using RecursiveAI.OneMail.Recovery;
using RecursiveAI.OneMail.Sources;
using RecursiveAI.OneMail.Tests.Pst;

namespace RecursiveAI.OneMail.Fuzz;

// Scan-mode recovery over damaged stores. The input is a patch list over a small valid PST made by the builder:
//   byte 0: bit 0 forensic, bit 1 zero the header first, bit 2 repair into a new store as well, bits 3-4: how much of the end to cut (0, 1/8, 1/4, 1/2)
//   then 4-byte records: 24-bit offset (little endian, modulo the file length) and the byte to put there.
// Nothing may throw, every range handed out must lie in the file, and a repaired store must open.
internal static class PstRecoveryTarget
{
    private static readonly byte[] Base = Make();

    private static byte[] Make()
    {
        using var ms = new MemoryStream();
        using (PstBuilder b = PstBuilder.Create(ms, leaveOpen: true))
        {
            PstFolderWriter inbox = b.IpmSubtree.CreateFolder("Inbox");
            for (int i = 0; i < 6; i++)
            {
                inbox.AddMessage(Message("m" + i, 1 + (i % 3), i % 2, i == 3 ? new string('y', 20000) : "body"));
            }

            b.Complete();
        }

        return ms.ToArray();
    }

    private static MapiMessage Message(string subject, int recipients, int attachments, string body)
    {
        static MapiProperty Str(ushort id, string v) => MapiProperty.Create(new MapiTag(id, MapiType.UnicodeString), System.Text.Encoding.Unicode.GetBytes(v));
        var m = new MapiMessage();
        m.Properties.Add(Str(0x001A, "IPM.Note"));
        m.Properties.Add(Str(0x0037, subject));
        m.Properties.Add(Str(0x1000, body));
        for (int i = 0; i < recipients; i++)
        {
            var r = new MapiRecipient(m);
            r.Properties.Add(Str(0x3001, "Person " + i));
            m.AddRecipient(r);
        }

        for (int i = 0; i < attachments; i++)
        {
            var a = new MapiAttachment(m);
            a.Properties.Add(Str(0x3707, "file" + i));
            a.Properties.Add(MapiProperty.Create(new MapiTag(0x3701, MapiType.Binary), new byte[9000]));
            m.AddAttachment(a);
        }

        return m;
    }

    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        byte mode = data[0];
        byte[] file = (byte[])Base.Clone();
        for (int i = 1; i + 3 < data.Length; i += 4)
        {
            int at = (data[i] | (data[i + 1] << 8) | (data[i + 2] << 16)) % file.Length;
            file[at] = data[i + 3];
        }

        if ((mode & 2) != 0)
        {
            Array.Clear(file, 0, 564);
        }

        int cut = (mode >> 3) & 3;
        if (cut != 0)
        {
            Array.Resize(ref file, file.Length - (file.Length / (cut == 1 ? 8 : cut == 2 ? 4 : 2) / 64 * 64));
        }

        if (!PstRecovery.TryOpen(new MemorySource(file), false, out PstRecovery? r, new PstRecoveryOptions { Forensic = (mode & 1) != 0 }) || r is null)
        {
            return;
        }

        using (r)
        {
            foreach (PstRecoveredItem it in r.EnumerateItems())
            {
                _ = it.Message.Subject;
                foreach (var rc in it.Message.Recipients)
                {
                    _ = rc.Properties.Count;
                }

                foreach (var a in it.Message.Attachments)
                {
                    _ = a.Properties.Count;
                }

                foreach (ByteRange rg in it.Provenance.Ranges)
                {
                    if (rg.Offset < 0 || rg.End > file.Length)
                    {
                        throw new InvalidOperationException("a recovered range lies outside the file");
                    }
                }
            }

            if ((mode & 4) != 0)
            {
                using var output = new MemoryStream();
                r.RepairTo(output, null);
                output.Position = 0;
                if (!PstStore.TryOpen(output, true, out PstStore? s, out _) || s is null)
                {
                    throw new InvalidOperationException("the repaired store does not open");
                }

                s.Dispose();
            }
        }
    }
}
