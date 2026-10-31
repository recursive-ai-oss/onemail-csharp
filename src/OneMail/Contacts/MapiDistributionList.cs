using System.Buffers.Binary;
using RecursiveAI.OneMail.Conversion.OxcMail;
using RecursiveAI.OneMail.Mapi;

namespace RecursiveAI.OneMail.Contacts;

/// <summary>What a personal distribution list member's entry ID points to (MS-OXOCNTC 2.2.2.2.4.1.1).</summary>
public enum DistributionListMemberKind
{
    /// <summary>A one-off address: name, address type and address are in the entry ID itself (MS-OXCDATA 2.2.5.1).</summary>
    OneOff = 0,

    /// <summary>An electronic address of a contact in the store (a wrapped Message EntryID plus a slot number).</summary>
    Contact,

    /// <summary>Another personal distribution list in the store.</summary>
    DistributionList,

    /// <summary>A mail user in the global address list.</summary>
    GalUser,

    /// <summary>A distribution list in the global address list.</summary>
    GalList,

    /// <summary>An entry ID this reader does not recognise; the bytes are kept.</summary>
    Unknown,
}

/// <summary>One member of a personal distribution list.</summary>
/// <param name="Kind">What the entry ID points to.</param>
/// <param name="DisplayName">The name from the member's one-off entry ID, or null when the list holds none for it.</param>
/// <param name="AddressType">The address type from the one-off entry ID (<c>SMTP</c>, <c>EX</c>), or null.</param>
/// <param name="Address">The address from the one-off entry ID, or null.</param>
/// <param name="ContactSlot">For <see cref="DistributionListMemberKind.Contact"/>: which electronic address of the contact (0 business fax, 1 home fax, 2 primary fax, 4 to 6 email 1 to 3), else null.</param>
/// <param name="EntryId">The member's entry ID as stored.</param>
public sealed record DistributionListMember(
    DistributionListMemberKind Kind, string? DisplayName, string? AddressType, string? Address, int? ContactSlot, byte[] EntryId)
{
    /// <summary>The address when its type is <c>SMTP</c>, else null.</summary>
    public string? SmtpAddress => Address is { Length: > 0 } a && AddressType?.Equals("SMTP", StringComparison.OrdinalIgnoreCase) == true ? a : null;
}

/// <summary>
/// A typed read view over a personal distribution list item (<c>IPM.DistList</c>, MS-OXOCNTC 2.2.2). The member list comes from
/// <c>PidLidDistributionListStream</c> when the item has one (the specification says the other member properties are then ignored),
/// otherwise from <c>PidLidDistributionListMembers</c> with <c>PidLidDistributionListOneOffMembers</c> beside it.
/// </summary>
/// <remarks>Entry IDs of members that live in the store or the address book are returned as kept; resolving them needs the store or a directory.</remarks>
public sealed class MapiDistributionList
{
    private static readonly byte[] WrappedProviderUid = [0xC0, 0x91, 0xAD, 0xD3, 0x51, 0x9D, 0xCF, 0x11, 0xA4, 0xA9, 0x00, 0xAA, 0x00, 0x47, 0xFA, 0xA4];
    private static readonly byte[] OneOffProviderUid = [0x81, 0x2B, 0x1F, 0xA4, 0xBE, 0xA3, 0x10, 0x19, 0x9D, 0x6E, 0x00, 0xDD, 0x01, 0x0F, 0x54, 0x02];

    private readonly MapiMessage _m;
    private IReadOnlyList<DistributionListMember>? _members;

    /// <summary>Wraps a message.</summary>
    /// <param name="message">A distribution list message.</param>
    public MapiDistributionList(MapiMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _m = message;
    }

    /// <summary>The message behind the view.</summary>
    public MapiMessage Message => _m;

    // MS-OXOCNTC 2.2.2.2.1: the two member properties must stay under 15,000 bytes; a larger list goes in the stream.
    private const int MaxMemberPropertyBytes = 15_000;

    /// <summary>
    /// Builds a personal distribution list item (MS-OXOCNTC 2.2.2) from its members: <c>IPM.DistList</c> with the name in PidTagDisplayName,
    /// PidLidDistributionListName and PidLidFileUnder, and the members in PidLidDistributionListMembers, PidLidDistributionListOneOffMembers and
    /// PidLidDistributionListChecksum (section 3.1.5.11). A list whose member properties would pass 15,000 bytes, or that has a member without a one-off
    /// entry ID, is written in PidLidDistributionListStream instead (section 2.2.2.2.4), which the reader above then prefers.
    /// </summary>
    /// <param name="name">The list's name.</param>
    /// <param name="members">The members. A <see cref="DistributionListMemberKind.OneOff"/> member needs <see cref="DistributionListMember.Address"/> (its name and type default to the address and <c>SMTP</c>) and gets a one-off entry ID made for it; any other kind must carry its <see cref="DistributionListMember.EntryId"/>, which is kept as it is.</param>
    /// <returns>The item, with no recipients or attachments. Reading it with <see cref="MapiDistributionList"/> gives the members back.</returns>
    /// <exception cref="ArgumentException">A member of another kind than one-off has no entry ID, or a one-off member has no address.</exception>
    public static MapiMessage Create(string name, IEnumerable<DistributionListMember> members)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(members);
        var ids = new List<byte[]>();
        var oneOffs = new List<byte[]>();
        bool everyOneHasOneOff = true;
        foreach (DistributionListMember m in members)
        {
            if (m.Kind == DistributionListMemberKind.OneOff && m.EntryId.Length == 0)
            {
                if (string.IsNullOrEmpty(m.Address))
                {
                    throw new ArgumentException("A one-off member needs an address.", nameof(members));
                }

                byte[] oneOff = OneOffEntryId.Build(m.DisplayName ?? m.Address, m.AddressType ?? "SMTP", m.Address);
                ids.Add(Wrap(0, oneOff));
                oneOffs.Add(oneOff);
            }
            else
            {
                if (m.EntryId.Length == 0)
                {
                    throw new ArgumentException("A member that is not a one-off address needs its entry ID.", nameof(members));
                }

                ids.Add(m.EntryId);
                if (m.DisplayName is not null && m.Address is not null)
                {
                    oneOffs.Add(OneOffEntryId.Build(m.DisplayName, m.AddressType ?? "SMTP", m.Address));
                }
                else
                {
                    everyOneHasOneOff = false;
                    oneOffs.Add([]);
                }
            }
        }

        var msg = new MapiMessage();
        msg.Properties.Add(UnicodeProperty(MapiPid.MessageClass, "IPM.DistList", null));
        msg.Properties.Add(UnicodeProperty(MapiPid.DisplayName, name, null));
        msg.Properties.Add(UnicodeProperty(0x8000, name, new MapiNamedProperty(MapiPropertySets.Address, 0x8053u)));
        msg.Properties.Add(UnicodeProperty(0x8000, name, new MapiNamedProperty(MapiPropertySets.Address, 0x8005u)));
        bool tooBig = ids.Sum(i => i.Length) >= MaxMemberPropertyBytes || oneOffs.Sum(i => i.Length) >= MaxMemberPropertyBytes;
        if (tooBig || !everyOneHasOneOff)
        {
            msg.Properties.Add(MapiProperty.CreateOwned(new MapiTag(0x8000, MapiType.Binary), BuildStream(ids, oneOffs), new MapiNamedProperty(MapiPropertySets.Address, 0x8064u)));
        }
        else
        {
            msg.Properties.Add(MapiProperty.CreateMulti(new MapiTag(0x8000, MapiType.MultipleBinary), ids, new MapiNamedProperty(MapiPropertySets.Address, 0x8055u)));
            msg.Properties.Add(MapiProperty.CreateMulti(new MapiTag(0x8000, MapiType.MultipleBinary), oneOffs, new MapiNamedProperty(MapiPropertySets.Address, 0x8054u)));
            msg.Properties.Add(MapiProperty.CreateOwned(new MapiTag(0x8000, MapiType.Integer32), BitConverter.GetBytes(Checksum(ids)), new MapiNamedProperty(MapiPropertySets.Address, 0x804Cu)));
        }

        return msg;
    }

    private static MapiProperty UnicodeProperty(ushort id, string value, MapiNamedProperty? name) =>
        MapiProperty.CreateOwned(new MapiTag(id, MapiType.UnicodeString), System.Text.Encoding.Unicode.GetBytes(value), name);

    // MS-OXOCNTC 2.2.2.2.4.1.1: Flags (4 bytes, zero), ProviderUID, Type (one byte; 0 for a one-off address) and the embedded entry ID.
    private static byte[] Wrap(byte type, byte[] embedded)
    {
        byte[] id = new byte[4 + 16 + 1 + embedded.Length];
        WrappedProviderUid.CopyTo(id, 4);
        id[20] = type;
        embedded.CopyTo(id, 21);
        return id;
    }

    // MS-OXOCNTC 3.1.5.11: CRC with the IEEE 802.3 polynomial 0xEDB88320 and a start value of 0, over every byte of every member entry ID, in order.
    private static int Checksum(List<byte[]> ids)
    {
        uint crc = 0;
        foreach (byte[] id in ids)
        {
            foreach (byte b in id)
            {
                crc ^= b;
                for (int k = 0; k < 8; k++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
                }
            }
        }

        return unchecked((int)crc);
    }

    // MS-OXOCNTC 2.2.2.2.4: version 1, build version, flags (1 = always use the stream), the counts and totals, the members, two terminators.
    private static byte[] BuildStream(List<byte[]> ids, List<byte[]> oneOffs)
    {
        using var ms = new MemoryStream();
        byte[] w = new byte[4];
        void U32(uint v)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(w, v);
            ms.Write(w);
        }

        U32(1);
        U32(0);
        U32(1);
        U32((uint)ids.Count);
        U32((uint)ids.Sum(i => i.Length));
        U32((uint)oneOffs.Sum(i => i.Length));
        U32(0);
        for (int i = 0; i < ids.Count; i++)
        {
            U32((uint)ids[i].Length);
            ms.Write(ids[i]);
            U32((uint)oneOffs[i].Length);
            ms.Write(oneOffs[i]);
            U32(0);
        }

        U32(0);
        U32(0);
        return ms.ToArray();
    }

    /// <summary>The list's name: <c>PidLidDistributionListName</c>, else <c>PidTagDisplayName</c>.</summary>
    public string? Name =>
        NonEmpty(_m.Properties.Get(new MapiNamedProperty(MapiPropertySets.Address, 0x8053u))?.AsString(_m.String8Encoding)) ?? NonEmpty(_m.GetString(MapiPid.DisplayName));

    private static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    /// <summary>The members, in list order.</summary>
    public IReadOnlyList<DistributionListMember> Members => _members ??= ReadMembers();

    private List<DistributionListMember> ReadMembers()
    {
        MapiProperty? stream = _m.Properties.Get(new MapiNamedProperty(MapiPropertySets.Address, 0x8064u), MapiType.Binary);
        if (stream is not null)
        {
            return ReadStream(stream.GetBytes().Span);
        }

        MapiProperty? members = _m.Properties.Get(new MapiNamedProperty(MapiPropertySets.Address, 0x8055u));
        MapiProperty? oneOffs = _m.Properties.Get(new MapiNamedProperty(MapiPropertySets.Address, 0x8054u));
        List<DistributionListMember> result = [];
        if (members is null || !members.IsMultiValued)
        {
            return result;
        }

        for (int i = 0; i < members.ValueCount; i++)
        {
            byte[] id = members.GetBytes(i).ToArray();
            byte[]? oneOff = oneOffs is { IsMultiValued: true } && i < oneOffs.ValueCount ? oneOffs.GetBytes(i).ToArray() : null;
            result.Add(Classify(id, oneOff));
        }

        return result;
    }

    // MS-OXOCNTC 2.2.2.2.4: the stream header is 28 bytes, then one DistListMemberInfo per member (an entry ID, a one-off entry ID and an extra block, each with a 4-byte size).
    private static List<DistributionListMember> ReadStream(ReadOnlySpan<byte> s)
    {
        List<DistributionListMember> result = [];
        if (s.Length < 28)
        {
            return result;
        }

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(s[12..]);
        int pos = 28;
        for (uint i = 0; i < count; i++)
        {
            if (!TakeBlock(s, ref pos, out ReadOnlySpan<byte> entry) || !TakeBlock(s, ref pos, out ReadOnlySpan<byte> oneOff) || !TakeBlock(s, ref pos, out _))
            {
                break;
            }

            result.Add(Classify(entry.ToArray(), oneOff.Length > 0 ? oneOff.ToArray() : null));
        }

        return result;
    }

    private static bool TakeBlock(ReadOnlySpan<byte> s, ref int pos, out ReadOnlySpan<byte> block)
    {
        block = default;
        if (s.Length - pos < 4)
        {
            return false;
        }

        uint size = BinaryPrimitives.ReadUInt32LittleEndian(s[pos..]);
        pos += 4;
        if (size > (uint)(s.Length - pos))
        {
            return false;
        }

        block = s.Slice(pos, (int)size);
        pos += (int)size;
        return true;
    }

    private static DistributionListMember Classify(byte[] id, byte[]? oneOff)
    {
        string? name = null, type = null, address = null;
        if (oneOff is not null && OneOffEntryId.TryParse(oneOff, out string n0, out string t0, out string a0))
        {
            (name, type, address) = (n0, t0, a0);
        }

        // A bare one-off entry ID.
        if (id.Length >= 24 && id.AsSpan(4, 16).SequenceEqual(OneOffProviderUid))
        {
            if (OneOffEntryId.TryParse(id, out string n, out string t, out string a))
            {
                return new DistributionListMember(DistributionListMemberKind.OneOff, n, t, a, null, id);
            }

            return new DistributionListMember(DistributionListMemberKind.Unknown, name, type, address, null, id);
        }

        // MS-OXOCNTC 2.2.2.2.4.1.1: Flags (4), ProviderUID (16), Type (1), EmbeddedEntryID.
        if (id.Length >= 21 && id.AsSpan(4, 16).SequenceEqual(WrappedProviderUid))
        {
            byte t = id[20];
            switch (t & 0x0F)
            {
                case 0:
                    if (OneOffEntryId.TryParse(id.AsSpan(21), out string n, out string ty, out string a))
                    {
                        return new DistributionListMember(DistributionListMemberKind.OneOff, n, ty, a, null, id);
                    }

                    break;
                case 3:
                    return new DistributionListMember(DistributionListMemberKind.Contact, name, type, address, (t >> 4) & 7, id);
                case 4:
                    return new DistributionListMember(DistributionListMemberKind.DistributionList, name, type, address, null, id);
                case 5:
                    return new DistributionListMember(DistributionListMemberKind.GalUser, name, type, address, null, id);
                case 6:
                    return new DistributionListMember(DistributionListMemberKind.GalList, name, type, address, null, id);
            }
        }

        return new DistributionListMember(DistributionListMemberKind.Unknown, name, type, address, null, id);
    }
}
