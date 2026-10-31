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
