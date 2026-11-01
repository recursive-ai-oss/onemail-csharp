using System.IO.Compression;
using System.Text;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Mime.Reading;
using RecursiveAI.OneMail.Msg;
using RecursiveAI.OneMail.Pst.Ndb;
using RecursiveAI.OneMail.Recovery;
using RecursiveAI.OneMail.Sources;
using RecursiveAI.OneMail.Stores.Dbx;
using RecursiveAI.OneMail.Stores.Olm;
using RecursiveAI.OneMail.Tests.Stores;

namespace RecursiveAI.OneMail.Fuzz;

// The span-level TryParse entry points and the two recovery readers. Nothing may throw except the documented InvalidDataException of OLM open,
// and the sizes and ranges that come back must lie inside the input.
internal static class RecoveryTargets
{
    // span-ndb: byte 0 low 2 bits the format, then a 4-byte offset, then the span.
    public static void Ndb(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5)
        {
            return;
        }

        NdbFormat format = (data[0] & 3) switch { 0 => NdbFormat.Ansi, 1 => NdbFormat.Unicode, _ => NdbFormat.Unicode4K };
        long ib = (data[1] | ((long)data[2] << 8) | ((long)data[3] << 16) | ((long)data[4] << 24)) & 0x7FFFFFFF;
        ReadOnlySpan<byte> span = data[5..];
        var defects = new List<NdbDefect>();
        if (NdbSpan.TryParsePage(span, format, ib, out NdbPageInfo p, defects) && (p.PageType is < 0x80 or > 0x86))
        {
            throw new InvalidOperationException("page type outside the page types");
        }

        if (NdbSpan.TryParseBlock(span, format, ib, out NdbBlockInfo b, defects) && (b.TotalLength > span.Length || b.DataLength > b.TotalLength || b.TotalLength % 64 != 0))
        {
            throw new InvalidOperationException("block sizes do not fit the span");
        }
    }

    public static void Mime(ReadOnlySpan<byte> data)
    {
        if (MimeHeaderBlock.TryParse(data, out MimeHeaderBlockInfo info))
        {
            if (info.Length > data.Length || info.BodyOffset > data.Length || info.HeaderCount < 1)
            {
                throw new InvalidOperationException("header block sizes do not fit the span");
            }
        }

        if (MimeEntity.TryParse(data, out MimeEntity? e) && e is not null)
        {
            foreach (MimeDefect _ in e.EnumerateDefects())
            {
            }

            _ = e.Headers.GetFirst("Subject");
        }
    }

    public static void Cfb(ReadOnlySpan<byte> data)
    {
        if (CfbSpan.TryParseHeader(data, out CfbHeaderInfo h) && (h.SectorSize < 128 || h.SectorSize > 65536))
        {
            throw new InvalidOperationException("sector size outside the format");
        }

        _ = CfbSpan.TryParseDirectoryEntry(data, out _);
        if (data.Length > 128)
        {
            _ = CfbSpan.TryParseDirectoryEntry(data[128..], out _);
        }
    }

    private static readonly byte[][] DbxBases = [MakeDbx(false), MakeDbx(true)];

    private static byte[] MakeDbx(bool shuffled)
    {
        List<DbxTestMessage> m = [];
        for (int i = 0; i < 24; i++)
        {
            byte[] text = new byte[100 + ((i * 211) % 1800)];
            new Random(i).NextBytes(text);
            m.Add(new DbxTestMessage { Text = text, Subject = "s" + i });
        }

        return DbxTestBuilder.MessageDatabase(m, new DbxBuild
        {
            ShuffleSeed = shuffled ? 5 : 0,
            Deleted = new HashSet<int> { 2, 5, 9, 14 },
            Unlinked = new HashSet<int> { 3, 11 },
        });
    }

    // span-dbx: byte 0 bit 0 which base, bit 1 also parse segments and records from every offset; the rest is 4-byte patches as in the dbx target.
    public static void Dbx(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        byte[] file = (byte[])DbxBases[data[0] & 1].Clone();
        for (int i = 1; i + 3 < data.Length; i += 4)
        {
            int pos = (data[i] | (data[i + 1] << 8) | (data[i + 2] << 16)) % file.Length;
            file[pos] = data[i + 3];
        }

        using MemorySource src = new(file);
        var reader = new DbxReader(src, new DbxReaderOptions { MaxMessageBytes = 1 << 22 });
        int budget = 500;
        foreach (DbxRecoveredMessage m in reader.RecoverDeleted(new DbxRecoveryOptions { IncludeDuplicatesOfLive = (data[0] & 4) != 0 }))
        {
            if (--budget < 0)
            {
                break;
            }

            RecoveryProvenance p = m.Provenance;
            if (p.Status == RecoveryStatus.Live)
            {
                throw new InvalidOperationException("a recovered message was tagged Live");
            }

            foreach (ByteRange r in p.Ranges.Concat(p.Lost))
            {
                if (r.Offset < 0 || r.Length < 0 || r.End > file.Length)
                {
                    throw new InvalidOperationException("range outside the file");
                }
            }

            if (m.Content.Length > file.Length)
            {
                throw new InvalidOperationException("content larger than the file");
            }
        }

        if ((data[0] & 2) != 0)
        {
            for (int p = 0; p + 20 <= file.Length && p < 200_000; p += 4)
            {
                _ = DbxSpan.TryParseSegment(file.AsSpan(p, Math.Min(0x210, file.Length - p)), p, out _);
                _ = DbxSpan.TryParseRecord(file.AsSpan(p), p, out _, out _);
            }
        }
    }

    private static readonly byte[] OlmBase = MakeOlm();

    private static byte[] MakeOlm()
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (int i = 0; i < 4; i++)
            {
                using (Stream s = z.CreateEntry("Local/com.microsoft.__Messages/F" + i + "/message_00000.xml", CompressionLevel.Optimal).Open())
                {
                    s.Write(Encoding.UTF8.GetBytes("<emails><email><OPFMessageCopySubject>m" + i + "</OPFMessageCopySubject><OPFMessageCopyBody>" + new string('b', 300 + (i * 50)) + "</OPFMessageCopyBody></email></emails>"));
                }

                using (Stream s = z.CreateEntry("Local/com.microsoft.__Messages/F" + i + "/com.microsoft.__Attachments/a_000" + i, CompressionLevel.Optimal).Open())
                {
                    s.Write(Encoding.ASCII.GetBytes("attachment bytes " + i));
                }
            }
        }

        return ms.ToArray();
    }

    private static readonly OlmReaderOptions OlmRecover = new() { Recovery = true, MaxXmlBytes = 1 << 20, MaxExpansionRatio = 200, MaxTextLength = 1 << 18 };

    // olm-recovery: 4-byte patches over a valid archive, then a cut: byte 0 = how much to cut off the end (in 1/256 of the archive).
    public static void OlmRecovery(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        byte[] zip = (byte[])OlmBase.Clone();
        for (int i = 1; i + 3 < data.Length; i += 4)
        {
            int pos = (data[i] | (data[i + 1] << 8) | (data[i + 2] << 16)) % zip.Length;
            zip[pos] = data[i + 3];
        }

        Array.Resize(ref zip, zip.Length - (zip.Length * data[0] / 256));
        OlmArchive olm;
        try
        {
            olm = OlmArchive.Open(new MemorySource(zip), false, null, OlmRecover);
        }
        catch (InvalidDataException)
        {
            return;
        }

        using (olm)
        {
            int n = 0;
            foreach (OlmItem it in olm.Enumerate())
            {
                if (++n > 500)
                {
                    break;
                }

                RecoveryProvenance? p = it.Recovery;
                if (p is null)
                {
                    throw new InvalidOperationException("recovery mode item without a tag");
                }

                foreach (ByteRange r in p.Ranges.Concat(p.Lost))
                {
                    if (r.Offset < 0 || r.Length < 0 || r.End > zip.Length)
                    {
                        throw new InvalidOperationException("range outside the archive");
                    }
                }

                _ = it.Subject;
                foreach (IMailAttachment a in it.Attachments)
                {
                    try
                    {
                        using Stream s = a.Open();
                        s.CopyTo(Stream.Null);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or RecursiveAI.OneCompress.CompressionException)
                    {
                    }
                }
            }

            _ = olm.Damage.Count;
        }
    }
}
