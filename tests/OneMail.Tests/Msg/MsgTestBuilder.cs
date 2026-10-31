using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using RecursiveAI.OneArchive;
using RecursiveAI.OneArchive.Cfb;
using RecursiveAI.OneMail.Mapi;

namespace RecursiveAI.OneMail.Tests.Msg;

/// <summary>One property to write: a tag (or a name, for a named property) and a value of the matching CLR type.</summary>
/// <param name="Tag">The property tag. For a named property give the type in the low half and any ID in the high half; the builder assigns the real ID.</param>
/// <param name="Value">short, int, long, float, double, bool, ulong (FILETIME), Guid, string, byte[], short[], int[], long[], ulong[], string[], byte[][], or null for no value.</param>
/// <param name="Name">The name, for a named property.</param>
internal sealed record BuildProp(uint Tag, object? Value, MapiNamedProperty? Name = null);

/// <summary>A raw OLE-style storage for an attachment: stream names and bytes below the attachment's object storage.</summary>
internal sealed class BuildStorage
{
    public Guid ClassId { get; init; }

    public List<(string Path, byte[] Data)> Streams { get; } = [];
}

/// <summary>An object to write: a message, a recipient or an attachment.</summary>
internal class BuildObject
{
    public List<BuildProp> Props { get; } = [];

    public List<BuildObject> Recipients { get; } = [];

    public List<BuildAttachment> Attachments { get; } = [];
}

/// <summary>An attachment; give at most one of <see cref="Embedded"/> and <see cref="Ole"/>.</summary>
internal sealed class BuildAttachment : BuildObject
{
    public BuildObject? Embedded { get; init; }

    public BuildStorage? Ole { get; init; }
}

/// <summary>
/// Writes MSG files from a description, following MS-OXMSG, so tests and the fuzz targets have valid files to read and to damage.
/// It exists only for tests: the library does not write MSG files yet.
/// </summary>
internal static class MsgTestBuilder
{
    public static byte[] Build(BuildObject message, bool unicode = true, bool terminatorInStream = false)
    {
        (List<(string Path, byte[] Data)> streams, List<string> emptyStorages) = BuildStreams(message, unicode, terminatorInStream);
        return Pack(streams, emptyStorages);
    }

    /// <summary>The streams and empty storages of the file, before they are packed, so a test can damage them.</summary>
    public static (List<(string Path, byte[] Data)> Streams, List<string> EmptyStorages) BuildStreams(BuildObject message, bool unicode = true, bool terminatorInStream = false)
    {
        var streams = new List<(string Path, byte[] Data)>();
        var emptyStorages = new List<string>();
        var names = new List<MapiNamedProperty>();
        Assign(message, names);
        WriteObject(streams, emptyStorages, string.Empty, message, 0, unicode, terminatorInStream, names);
        WriteNameMap(streams, names);
        return (streams, emptyStorages);
    }

    public static byte[] Pack(List<(string Path, byte[] Data)> streams, List<string> emptyStorages)
    {
        using var ms = new MemoryStream();
        using (var w = new CompoundFileWriter(ms, null, leaveOpen: true))
        {
            foreach (string dir in emptyStorages)
            {
                w.WriteEntry(new ArchiveEntry { Name = dir, Kind = ArchiveEntryKind.Directory });
            }

            foreach ((string path, byte[] data) in streams)
            {
                w.WriteEntry(new ArchiveEntry { Name = path, Kind = ArchiveEntryKind.File, Size = data.Length }, data);
            }

            w.Finish();
        }

        return ms.ToArray();
    }

    // Named properties get consecutive local IDs from 0x8000 in the order they are first met.
    private static void Assign(BuildObject o, List<MapiNamedProperty> names)
    {
        foreach (BuildProp p in o.Props)
        {
            if (p.Name is { } n && !names.Contains(n))
            {
                names.Add(n);
            }
        }

        foreach (BuildObject r in o.Recipients)
        {
            Assign(r, names);
        }

        foreach (BuildAttachment a in o.Attachments)
        {
            Assign(a, names);
            if (a.Embedded is { } e)
            {
                Assign(e, names);
            }
        }
    }

    private static void WriteObject(List<(string Path, byte[] Data)> streams, List<string> emptyStorages, string dir, BuildObject o, int kind, bool unicode, bool terminatorInStream, List<MapiNamedProperty> names)
    {
        // kind: 0 message, 1 embedded message, 2 recipient or attachment
        string P(string n) => dir.Length == 0 ? n : dir + "/" + n;
        using var ps = new MemoryStream();
        var header = new byte[kind switch { 0 => 32, 1 => 24, _ => 8 }];
        if (kind < 2)
        {
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), o.Recipients.Count);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), o.Attachments.Count);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), o.Recipients.Count);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20), o.Attachments.Count);
        }

        ps.Write(header);
        foreach (BuildProp p in o.Props)
        {
            uint tag = p.Tag;
            if (p.Name is { } n)
            {
                tag = ((uint)(0x8000 + names.IndexOf(n)) << 16) | (tag & 0xFFFF);
            }

            WriteProperty(streams, ps, dir, tag, p.Value, terminatorInStream);
        }

        if (o is BuildAttachment { Embedded: not null })
        {
            // 0x3701000D: size 0xFFFFFFFF, reserved 1 (MS-OXMSG 2.4.2.2)
            WriteEntry(ps, 0x3701000D, 2, 0xFFFFFFFFUL | (1UL << 32));
        }
        else if (o is BuildAttachment { Ole: not null })
        {
            WriteEntry(ps, 0x3701000D, 2, 0xFFFFFFFFUL | (4UL << 32));
        }

        streams.Add((P("__properties_version1.0"), ps.ToArray()));

        int i = 0;
        foreach (BuildObject r in o.Recipients)
        {
            WriteObject(streams, emptyStorages, P("__recip_version1.0_#" + (i++).ToString("X8", CultureInfo.InvariantCulture)), r, 2, unicode, terminatorInStream, names);
        }

        i = 0;
        foreach (BuildAttachment a in o.Attachments)
        {
            string ad = P("__attach_version1.0_#" + (i++).ToString("X8", CultureInfo.InvariantCulture));
            WriteObject(streams, emptyStorages, ad, a, 2, unicode, terminatorInStream, names);
            if (a.Embedded is { } e)
            {
                WriteObject(streams, emptyStorages, ad + "/__substg1.0_3701000D", e, 1, unicode, terminatorInStream, names);
            }

            if (a.Ole is { } ole)
            {
                emptyStorages.Add(ad + "/__substg1.0_3701000D");
                foreach ((string path, byte[] data) in ole.Streams)
                {
                    streams.Add((ad + "/__substg1.0_3701000D/" + path, data));
                }
            }
        }
    }

    private static void WriteEntry(MemoryStream ps, uint tag, uint flags, ulong tail)
    {
        var e = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(e, tag);
        BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(4), flags);
        BinaryPrimitives.WriteUInt64LittleEndian(e.AsSpan(8), tail);
        ps.Write(e);
    }

    private static void WriteProperty(List<(string Path, byte[] Data)> streams, MemoryStream ps, string dir, uint tag, object? value, bool terminatorInStream)
    {
        ushort type = (ushort)tag;
        string stream = (dir.Length == 0 ? string.Empty : dir + "/") + "__substg1.0_" + tag.ToString("X8", CultureInfo.InvariantCulture);
        var v8 = new byte[8];
        switch (value)
        {
            case short s:
                BinaryPrimitives.WriteInt16LittleEndian(v8, s);
                WriteEntry(ps, tag, 6, BinaryPrimitives.ReadUInt64LittleEndian(v8));
                return;
            case int n:
                BinaryPrimitives.WriteInt32LittleEndian(v8, n);
                WriteEntry(ps, tag, 6, BinaryPrimitives.ReadUInt64LittleEndian(v8));
                return;
            case long l:
                BinaryPrimitives.WriteInt64LittleEndian(v8, l);
                WriteEntry(ps, tag, 6, BinaryPrimitives.ReadUInt64LittleEndian(v8));
                return;
            case ulong u:
                BinaryPrimitives.WriteUInt64LittleEndian(v8, u);
                WriteEntry(ps, tag, 6, BinaryPrimitives.ReadUInt64LittleEndian(v8));
                return;
            case float f:
                BinaryPrimitives.WriteSingleLittleEndian(v8, f);
                WriteEntry(ps, tag, 6, BinaryPrimitives.ReadUInt64LittleEndian(v8));
                return;
            case double d:
                BinaryPrimitives.WriteDoubleLittleEndian(v8, d);
                WriteEntry(ps, tag, 6, BinaryPrimitives.ReadUInt64LittleEndian(v8));
                return;
            case bool b:
                v8[0] = (byte)(b ? 1 : 0);
                WriteEntry(ps, tag, 6, BinaryPrimitives.ReadUInt64LittleEndian(v8));
                return;
            case Guid g:
                streams.Add((stream, g.ToByteArray()));
                WriteEntry(ps, tag, 6, 16);
                return;
            case string str:
            {
                bool wide = type == (ushort)MapiType.UnicodeString;
                byte[] bytes = wide ? Encoding.Unicode.GetBytes(str) : Encoding.Latin1.GetBytes(str);
                int term = wide ? 2 : 1;
                if (terminatorInStream)
                {
                    bytes = [.. bytes, .. new byte[term]];
                    term = 0;
                }

                streams.Add((stream, bytes));
                WriteEntry(ps, tag, 6, (uint)(bytes.Length + term));
                return;
            }

            case byte[] bin:
                streams.Add((stream, bin));
                WriteEntry(ps, tag, 6, (uint)bin.Length);
                return;
            case short[] sh:
                WriteMultiFixed(streams, ps, stream, tag, sh.SelectMany(x => BitConverter.GetBytes(x)).ToArray());
                return;
            case int[] ia:
                WriteMultiFixed(streams, ps, stream, tag, ia.SelectMany(x => BitConverter.GetBytes(x)).ToArray());
                return;
            case long[] la:
                WriteMultiFixed(streams, ps, stream, tag, la.SelectMany(x => BitConverter.GetBytes(x)).ToArray());
                return;
            case ulong[] ua:
                WriteMultiFixed(streams, ps, stream, tag, ua.SelectMany(x => BitConverter.GetBytes(x)).ToArray());
                return;
            case string[] sa:
            {
                bool wide = type == (ushort)MapiType.MultipleString;
                var lengths = new MemoryStream();
                for (int i = 0; i < sa.Length; i++)
                {
                    byte[] bytes = wide ? Encoding.Unicode.GetBytes(sa[i] + "\0") : Encoding.Latin1.GetBytes(sa[i] + "\0");
                    streams.Add((stream + "-" + i.ToString("X8", CultureInfo.InvariantCulture), bytes));
                    lengths.Write(BitConverter.GetBytes(bytes.Length));
                }

                streams.Add((stream, lengths.ToArray()));
                WriteEntry(ps, tag, 6, (uint)lengths.Length);
                return;
            }

            case byte[][] ba:
            {
                var lengths = new MemoryStream();
                for (int i = 0; i < ba.Length; i++)
                {
                    streams.Add((stream + "-" + i.ToString("X8", CultureInfo.InvariantCulture), ba[i]));
                    lengths.Write(BitConverter.GetBytes(ba[i].Length));
                    lengths.Write(new byte[4]);
                }

                streams.Add((stream, lengths.ToArray()));
                WriteEntry(ps, tag, 6, (uint)lengths.Length);
                return;
            }

            default:
                WriteEntry(ps, tag, 6, 0);
                return;
        }
    }

    private static void WriteMultiFixed(List<(string Path, byte[] Data)> streams, MemoryStream ps, string stream, uint tag, byte[] data)
    {
        streams.Add((stream, data));
        WriteEntry(ps, tag, 6, (uint)data.Length);
    }

    // MS-OXMSG 2.2.3: the GUID, entry and string streams, then the hashed name-to-ID streams.
    private static void WriteNameMap(List<(string Path, byte[] Data)> streams, List<MapiNamedProperty> names)
    {
        const string dir = "__nameid_version1.0/";
        var guids = new List<Guid>();
        var entries = new MemoryStream();
        var strings = new MemoryStream();
        var hashed = new Dictionary<int, MemoryStream>();
        for (int i = 0; i < names.Count; i++)
        {
            MapiNamedProperty n = names[i];
            int guidIndex;
            if (n.PropertySet == MapiPropertySets.Mapi)
            {
                guidIndex = 1;
            }
            else if (n.PropertySet == MapiPropertySets.PublicStrings)
            {
                guidIndex = 2;
            }
            else
            {
                int at = guids.IndexOf(n.PropertySet);
                if (at < 0)
                {
                    guids.Add(n.PropertySet);
                    at = guids.Count - 1;
                }

                guidIndex = at + 3;
            }

            uint kindInfo = ((uint)i << 16) | (uint)(guidIndex << 1) | (n.IsString ? 1u : 0u);
            uint idOrOffset;
            uint crc;
            if (n.IsString)
            {
                idOrOffset = (uint)strings.Length;
                byte[] name = Encoding.Unicode.GetBytes(n.Name!);
                strings.Write(BitConverter.GetBytes(name.Length));
                strings.Write(name);
                while (strings.Length % 4 != 0)
                {
                    strings.WriteByte(0);
                }

                crc = Crc32(name);
            }
            else
            {
                idOrOffset = n.Lid;
                crc = n.Lid;
            }

            var entry = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(entry, idOrOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(4), kindInfo);
            entries.Write(entry);

            // Stream ID = 0x1000 + (ID XOR (GUID index << 1 | kind)) MOD 0x1F
            int streamId = 0x1000 + (int)((crc ^ (uint)((guidIndex << 1) | (n.IsString ? 1 : 0))) % 0x1F);
            if (!hashed.TryGetValue(streamId, out MemoryStream? h))
            {
                hashed[streamId] = h = new MemoryStream();
            }

            var he = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(he, crc);
            BinaryPrimitives.WriteUInt32LittleEndian(he.AsSpan(4), kindInfo);
            h.Write(he);
        }

        streams.Add((dir + "__substg1.0_00020102", guids.SelectMany(g => g.ToByteArray()).ToArray()));
        streams.Add((dir + "__substg1.0_00030102", entries.ToArray()));
        streams.Add((dir + "__substg1.0_00040102", strings.ToArray()));
        foreach ((int id, MemoryStream h) in hashed.OrderBy(k => k.Key))
        {
            streams.Add((dir + "__substg1.0_" + ((uint)(id << 16) | 0x102).ToString("X8", CultureInfo.InvariantCulture), h.ToArray()));
        }
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        return ~crc;
    }

    public static ulong FileTime(DateTime utc) => (ulong)utc.ToFileTimeUtc();
}
