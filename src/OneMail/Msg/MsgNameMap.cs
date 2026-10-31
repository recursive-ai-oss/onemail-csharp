using System.Buffers.Binary;
using System.Text;
using RecursiveAI.OneArchive.Cfb;
using RecursiveAI.OneMail.Mapi;

namespace RecursiveAI.OneMail.Msg;

/// <summary>
/// The named property mapping of an MSG file: local property ID 0x8000 + n to a property set and a numeric ID or string
/// (MS-OXMSG 2.2.3). One map serves the message and everything embedded in it.
/// </summary>
/// <remarks>
/// Only the three property-ID-to-name streams are read. The streams that map names back to IDs (<c>__substg1.0_1000xxxx</c>
/// to <c>101Exxxx</c>) repeat the same information in a hashed form for fast lookup and are not needed to read.
/// </remarks>
internal sealed class MsgNameMap
{
    public static readonly MsgNameMap Empty = new([]);

    private readonly MapiNamedProperty?[] _names;

    private MsgNameMap(MapiNamedProperty?[] names)
    {
        _names = names;
    }

    public bool IsEmpty => _names.Length == 0;

    public int Count => _names.Length;

    /// <summary>The name of the local ID, or null when the ID is not mapped.</summary>
    public MapiNamedProperty? Get(ushort id) => id >= 0x8000 && id - 0x8000 < _names.Length ? _names[id - 0x8000] : null;

    private const string GuidStream = "__substg1.0_00020102";
    private const string EntryStream = "__substg1.0_00030102";
    private const string StringStream = "__substg1.0_00040102";

    /// <summary>Reads the map from the <c>__nameid_version1.0</c> storage. Never throws; damage becomes defects.</summary>
    public static MsgNameMap Read(MsgContainer c, CfbEntry storage, string path, MapiObject? owner)
    {
        byte[] guids = [];
        byte[] entries = [];
        byte[] strings = [];
        foreach (CfbEntry e in c.Children(storage))
        {
            if (e.ObjectType != CfbObjectType.Stream)
            {
                continue;
            }

            // Local IDs run from 0x8000 to 0xFFFE, so no more than 0x7FFF names, GUIDs or entries can be addressed; streams larger than
            // that (plus one partial element) are refused rather than read.
            byte[]? Read(long limit) => c.ReadAll(e, path + "/" + e.RawName, limit);
            if (e.RawName.Equals(GuidStream, StringComparison.OrdinalIgnoreCase))
            {
                guids = Read((0x7FFFL * 16) + 15) ?? [];
            }
            else if (e.RawName.Equals(EntryStream, StringComparison.OrdinalIgnoreCase))
            {
                entries = Read((0x7FFFL * 8) + 7) ?? [];
            }
            else if (e.RawName.Equals(StringStream, StringComparison.OrdinalIgnoreCase))
            {
                strings = Read(Math.Min(c.Options.MaxValueBytes, 16L << 20)) ?? [];
            }
        }

        if (guids.Length % 16 != 0)
        {
            c.Report(owner, MapiDefectKind.NamedPropertyGuidStreamInvalid, path + "/" + GuidStream, guids.Length - (guids.Length % 16), $"{guids.Length} bytes is not a whole number of GUIDs; the partial GUID is ignored");
        }

        if (entries.Length % 8 != 0)
        {
            c.Report(owner, MapiDefectKind.NamedPropertyEntryStreamInvalid, path + "/" + EntryStream, entries.Length - (entries.Length % 8), $"{entries.Length} bytes is not a whole number of 8-byte entries; the partial entry is ignored");
        }

        // Local IDs run from 0x8000 to 0xFFFE, so 0x7FFF entries are the most that can be addressed.
        int count = Math.Min(entries.Length / 8, 0x7FFF);
        var names = new MapiNamedProperty?[count];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> en = entries.AsSpan(i * 8, 8);
            uint nameOrOffset = BinaryPrimitives.ReadUInt32LittleEndian(en);
            uint kindInfo = BinaryPrimitives.ReadUInt32LittleEndian(en[4..]);
            int propertyIndex = (int)(kindInfo >> 16);
            int guidIndex = (int)((kindInfo & 0xFFFF) >> 1);
            bool isString = (kindInfo & 1) != 0;
            string at = path + "/" + EntryStream;
            long off = i * 8L;

            // MS-OXMSG 3.2.1: the entry for ID 0x8000 + n is entry n; the stored index should say the same.
            if (propertyIndex != i)
            {
                c.Report(owner, MapiDefectKind.NamedPropertyIndexMismatch, at, off, $"entry {i} says property index {propertyIndex}; the position is used");
            }

            Guid set;
            if (guidIndex == 1)
            {
                set = MapiPropertySets.Mapi;
            }
            else if (guidIndex == 2)
            {
                set = MapiPropertySets.PublicStrings;
            }
            else if (guidIndex >= 3 && (guidIndex - 3 + 1) * 16 <= guids.Length)
            {
                set = new Guid(guids.AsSpan((guidIndex - 3) * 16, 16));
            }
            else
            {
                c.Report(owner, MapiDefectKind.NamedPropertyReferenceInvalid, at, off, $"GUID index {guidIndex} is outside the {guids.Length / 16} GUIDs in the GUID stream");
                continue;
            }

            if (!isString)
            {
                names[i] = new MapiNamedProperty(set, nameOrOffset);
                continue;
            }

            // MS-OXMSG 2.2.3.1.3: a 4-byte length in bytes, then the UTF-16 name, each entry starting on a 4-byte boundary.
            long o = nameOrOffset;
            if (o + 4 > strings.Length)
            {
                c.Report(owner, MapiDefectKind.NamedPropertyReferenceInvalid, at, off, $"string offset {o} is outside the {strings.Length}-byte string stream");
                continue;
            }

            uint len = BinaryPrimitives.ReadUInt32LittleEndian(strings.AsSpan((int)o));
            if (len > strings.Length - o - 4)
            {
                c.Report(owner, MapiDefectKind.NamedPropertyReferenceInvalid, at, off, $"name of {len} bytes at string offset {o} runs past the {strings.Length}-byte string stream");
                continue;
            }

            names[i] = new MapiNamedProperty(set, Encoding.Unicode.GetString(strings, (int)o + 4, (int)(len & ~1u)));
        }

        return new MsgNameMap(names);
    }
}
