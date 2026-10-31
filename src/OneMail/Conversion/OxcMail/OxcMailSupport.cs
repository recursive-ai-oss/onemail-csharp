using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Mime;

namespace RecursiveAI.OneMail.Conversion.OxcMail;

/// <summary>Builds MAPI properties from .NET values in the raw layouts the property bag documents.</summary>
internal static class MapiProps
{
    public static MapiProperty Str(ushort id, string value) =>
        MapiProperty.CreateOwned(new MapiTag(id, MapiType.UnicodeString), Encoding.Unicode.GetBytes(value));

    public static MapiProperty Int(ushort id, int value)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, value);
        return MapiProperty.CreateOwned(new MapiTag(id, MapiType.Integer32), b);
    }

    public static MapiProperty Bool(ushort id, bool value) =>
        MapiProperty.CreateOwned(new MapiTag(id, MapiType.Boolean), [value ? (byte)1 : (byte)0]);

    // MS-DTYP FILETIME counts 100 ns ticks from 1601-01-01 UTC, so earlier instants have no value.
    private const long FileTimeEpochTicks = 504911232000000000L;

    public static bool FitsFileTime(DateTimeOffset value) => value.UtcTicks >= FileTimeEpochTicks;

    public static MapiProperty Time(ushort id, DateTimeOffset value)
    {
        byte[] b = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(b, Math.Max(0, value.UtcTicks - FileTimeEpochTicks));
        return MapiProperty.CreateOwned(new MapiTag(id, MapiType.Time), b);
    }

    public static MapiProperty Bin(ushort id, byte[] value) =>
        MapiProperty.CreateOwned(new MapiTag(id, MapiType.Binary), value);

    public static MapiProperty NamedStrings(ushort id, MapiNamedProperty name, IReadOnlyList<string> values)
    {
        var raw = new List<byte[]>(values.Count);
        foreach (string v in values)
        {
            raw.Add(Encoding.Unicode.GetBytes(v));
        }

        return MapiProperty.CreateMulti(new MapiTag(id, MapiType.MultipleString), raw, name);
    }

    public static MapiProperty NamedStr(ushort id, MapiNamedProperty name, string value) =>
        MapiProperty.CreateOwned(new MapiTag(id, MapiType.UnicodeString), Encoding.Unicode.GetBytes(value), name);
}

/// <summary>One header field of a header block: the name as written and the value with folding removed.</summary>
internal readonly record struct HeaderField(string Name, string Value);

/// <summary>Reads a header block held as text (PidTagTransportMessageHeaders) without throwing on anything.</summary>
internal static class HeaderBlock
{
    /// <summary>Splits a block into fields. Lines that are neither a field nor a continuation are counted in <paramref name="skipped"/>.</summary>
    public static List<HeaderField> Parse(string block, out int skipped)
    {
        skipped = 0;
        var fields = new List<HeaderField>();
        string? name = null;
        var value = new StringBuilder();

        void Flush()
        {
            if (name is not null)
            {
                fields.Add(new HeaderField(name, value.ToString().Trim(' ', '\t')));
                name = null;
                value.Clear();
            }
        }

        foreach (string line in block.Split('\n'))
        {
            string l = line.TrimEnd('\r');
            if (l.Length == 0)
            {
                // A blank line ends the block (RFC 5322 §2.1); anything after it is not header.
                if (name is not null || fields.Count > 0)
                {
                    break;
                }

                continue;
            }

            if (l[0] is ' ' or '\t')
            {
                if (name is not null)
                {
                    value.Append(' ').Append(l.Trim(' ', '\t'));
                }
                else
                {
                    skipped++;
                }

                continue;
            }

            int colon = l.IndexOf(':', StringComparison.Ordinal);

            // RFC 5322 §4.2 (obs-optional): white space may stand between the field name and the colon; the name is the text before it.
            int nameEnd = colon;
            while (nameEnd > 0 && l[nameEnd - 1] is ' ' or '\t')
            {
                nameEnd--;
            }

            if (colon <= 0 || nameEnd == 0 || !IsFieldName(l.AsSpan(0, nameEnd)))
            {
                Flush();
                skipped++;
                continue;
            }

            Flush();
            name = l[..nameEnd];
            value.Append(l.AsSpan(colon + 1).Trim(' '));
        }

        Flush();
        return fields;
    }

    private static bool IsFieldName(ReadOnlySpan<char> s)
    {
        foreach (char c in s)
        {
            if (c <= ' ' || c > '~')
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// One-off EntryIDs and entry lists (MS-OXCDATA 2.2.5.1 and 2.3.2/2.3.3): how PidTagReplyRecipientEntries holds addresses.
/// </summary>
internal static class OneOffEntryId
{
    private static ReadOnlySpan<byte> ProviderUid => [0x81, 0x2B, 0x1F, 0xA4, 0xBE, 0xA3, 0x10, 0x19, 0x9D, 0x6E, 0x00, 0xDD, 0x01, 0x0F, 0x54, 0x02];

    // 0x0080 (U): Unicode strings, 0x0100 (M): send as pure MIME, 0x0010 (L): the server cannot look the address up.
    private const ushort WriteFlags = 0x0080 | 0x0100 | 0x0010;

    public static byte[] Build(string displayName, string addressType, string address)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[4]);
        ms.Write(ProviderUid);
        Span<byte> w = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(w, 0);
        ms.Write(w);
        BinaryPrimitives.WriteUInt16LittleEndian(w, WriteFlags);
        ms.Write(w);
        foreach (string s in new[] { displayName, addressType, address })
        {
            ms.Write(Encoding.Unicode.GetBytes(s));
            ms.WriteByte(0);
            ms.WriteByte(0);
        }

        return ms.ToArray();
    }

    /// <summary>Reads a one-off EntryID. Returns false for any other kind of EntryID or a damaged one.</summary>
    public static bool TryParse(ReadOnlySpan<byte> id, out string displayName, out string addressType, out string address)
    {
        displayName = addressType = address = string.Empty;
        if (id.Length < 24 || !id.Slice(4, 16).SequenceEqual(ProviderUid))
        {
            return false;
        }

        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(id[22..]);
        bool unicode = (flags & 0x0080) != 0;
        ReadOnlySpan<byte> rest = id[24..];
        string?[] parts = new string?[3];
        for (int i = 0; i < 3; i++)
        {
            int len = -1;
            if (unicode)
            {
                for (int j = 0; j + 1 < rest.Length; j += 2)
                {
                    if (rest[j] == 0 && rest[j + 1] == 0)
                    {
                        len = j;
                        break;
                    }
                }
            }
            else
            {
                len = rest.IndexOf((byte)0);
            }

            if (len < 0)
            {
                return false;
            }

            parts[i] = unicode ? Encoding.Unicode.GetString(rest[..len]) : Encoding.Latin1.GetString(rest[..len]);
            rest = rest[(len + (unicode ? 2 : 1))..];
        }

        displayName = parts[0]!;
        addressType = parts[1]!;
        address = parts[2]!;
        return true;
    }

    /// <summary>Builds a FlatEntryList: a count, the byte size of the entries, then each entry as its size, its bytes and padding to 4 bytes.</summary>
    public static byte[] BuildList(IReadOnlyList<byte[]> entries)
    {
        using var body = new MemoryStream();
        Span<byte> w = stackalloc byte[4];
        foreach (byte[] e in entries)
        {
            BinaryPrimitives.WriteInt32LittleEndian(w, e.Length);
            body.Write(w);
            body.Write(e);
            for (int pad = (4 - (e.Length & 3)) & 3; pad > 0; pad--)
            {
                body.WriteByte(0);
            }
        }

        using var ms = new MemoryStream();
        BinaryPrimitives.WriteInt32LittleEndian(w, entries.Count);
        ms.Write(w);
        BinaryPrimitives.WriteInt32LittleEndian(w, (int)body.Length);
        ms.Write(w);
        body.WriteTo(ms);
        return ms.ToArray();
    }

    /// <summary>Reads a FlatEntryList leniently: stops at the first entry that does not fit.</summary>
    public static List<byte[]> ParseList(ReadOnlySpan<byte> list)
    {
        var result = new List<byte[]>();
        if (list.Length < 8)
        {
            return result;
        }

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(list);
        int pos = 8;
        for (uint i = 0; i < count && pos + 4 <= list.Length; i++)
        {
            int size = BinaryPrimitives.ReadInt32LittleEndian(list[pos..]);
            pos += 4;
            if (size < 0 || size > list.Length - pos)
            {
                break;
            }

            result.Add(list.Slice(pos, size).ToArray());
            pos += (size + 3) & ~3;
        }

        return result;
    }
}

/// <summary>One address of a MAPI object as the conversions and the view use it.</summary>
/// <param name="Name">The display name, or null.</param>
/// <param name="Smtp">The SMTP address when one is known or could be resolved; else null.</param>
/// <param name="X500">The Exchange distinguished name when the address type is <c>EX</c>; else null.</param>
/// <param name="Other">The address as stored when it is of another type (X.400, fax) and so neither SMTP nor X.500; else null.</param>
internal readonly record struct ResolvedAddress(string? Name, string? Smtp, string? X500, string? Other)
{
    public bool IsEmpty => Name is null && Smtp is null && X500 is null && Other is null;
}

/// <summary>
/// Finds the SMTP address of the senders and recipients of one message (MS-OXCMAIL 2.1.3.1.7: addresses are looked up by their
/// proxy addresses). Without a directory, the resolution uses what the message itself holds, in this order:
/// the object's own PidTagSmtpAddress-family property; the stored address when its type is SMTP (or absent, and it looks like an address);
/// the SMTP address of another sender or recipient of the message with the same X.500 address; the address of a field in the transport
/// headers whose display name is the same and unique. Anything else stays unresolved with its X.500 address kept.
/// </summary>
internal sealed class MapiAddressResolver
{
    private readonly MapiMessage _message;
    private readonly Dictionary<string, string> _x500ToSmtp = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string?>? _headerNames;
    private List<HeaderField>? _transport;
    private bool _transportRead;

    public MapiAddressResolver(MapiMessage message)
    {
        _message = message;
        // Index everything that carries both forms, so that a recipient without PidTagSmtpAddress can use the sender's, and the other way round.
        Index(message, MapiPid.SenderEmailAddress, MapiPid.SenderAddressType, MapiPid.SenderSmtpAddress);
        Index(message, MapiPid.SentRepresentingEmailAddress, MapiPid.SentRepresentingAddressType, MapiPid.SentRepresentingSmtpAddress);
        foreach (MapiRecipient r in message.Recipients)
        {
            Index(r, MapiPid.EmailAddress, MapiPid.AddressType, MapiPid.SmtpAddress);
        }
    }

    /// <summary>The transport header fields of the message, parsed once; empty when it has none.</summary>
    public IReadOnlyList<HeaderField> Transport
    {
        get
        {
            if (!_transportRead)
            {
                _transportRead = true;
                string? block = _message.GetString(MapiPid.TransportMessageHeaders);
                if (string.IsNullOrWhiteSpace(block))
                {
                    _transport = [];
                }
                else
                {
                    _transport = HeaderBlock.Parse(block, out int skipped);
                    SkippedLines = skipped;
                }
            }

            return _transport!;
        }
    }

    /// <summary>How many lines of the transport headers were neither a field nor a continuation; valid after <see cref="Transport"/> was read.</summary>
    public int SkippedLines { get; private set; }

    private static bool IsEx(string? type) => string.Equals(type, "EX", StringComparison.OrdinalIgnoreCase);

    private static bool LooksSmtp(string? s) => s is { Length: > 2 } && s.Contains('@', StringComparison.Ordinal) && !s.StartsWith('/');

    private void Index(MapiObject o, ushort emailId, ushort typeId, ushort smtpId)
    {
        string? email = o.GetString(emailId);
        string? smtp = o.GetString(smtpId);
        if (IsEx(o.GetString(typeId)) && !string.IsNullOrEmpty(email) && LooksSmtp(smtp))
        {
            _x500ToSmtp.TryAdd(email, smtp!);
        }
    }

    private enum Role
    {
        Recipient,
        SentRepresenting,
        Sender,
    }

    private ResolvedAddress Resolve(MapiObject o, ushort nameId, ushort emailId, ushort typeId, ushort smtpId, Role role)
    {
        string? name = o.GetString(nameId);
        if (string.IsNullOrEmpty(name) && o is MapiRecipient)
        {
            name = o.GetString(MapiPid.RecipientDisplayName);
        }

        string? email = o.GetString(emailId);
        string? type = o.GetString(typeId);
        string? smtp = o.GetString(smtpId);
        string? x500 = IsEx(type) && !string.IsNullOrEmpty(email) ? email : null;
        string? other = null;

        string? result = null;
        if (LooksSmtp(smtp))
        {
            result = smtp;
        }
        else if ((string.IsNullOrEmpty(type) || string.Equals(type, "SMTP", StringComparison.OrdinalIgnoreCase)) && LooksSmtp(email))
        {
            result = email;
        }
        else if (x500 is not null && _x500ToSmtp.TryGetValue(x500, out string? known))
        {
            result = known;
        }
        else if (x500 is not null && FromTransportField(role) is { } viaField)
        {
            result = viaField;
        }
        else if (x500 is not null && !string.IsNullOrEmpty(name))
        {
            result = FromTransportByName(name);
        }
        else if (!string.IsNullOrEmpty(email) && !IsEx(type) && !string.Equals(type, "SMTP", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(type))
        {
            other = email;
        }

        if (string.IsNullOrEmpty(name))
        {
            name = null;
        }

        return new ResolvedAddress(name, result, x500, other);
    }

    public ResolvedAddress Sender() =>
        Resolve(_message, MapiPid.SenderName, MapiPid.SenderEmailAddress, MapiPid.SenderAddressType, MapiPid.SenderSmtpAddress, Role.Sender);

    public ResolvedAddress SentRepresenting() =>
        Resolve(_message, MapiPid.SentRepresentingName, MapiPid.SentRepresentingEmailAddress, MapiPid.SentRepresentingAddressType, MapiPid.SentRepresentingSmtpAddress, Role.SentRepresenting);

    public ResolvedAddress Recipient(MapiRecipient r) =>
        Resolve(r, MapiPid.DisplayName, MapiPid.EmailAddress, MapiPid.AddressType, MapiPid.SmtpAddress, Role.Recipient);

    // MS-OXCMAIL 2.1.3.1.3 maps the From header to the sent-representing group and 2.1.3.1.4 the Sender header to the sender group. A
    // received message keeps the headers it arrived with, so when the group only has an Exchange address, the one mailbox of the matching
    // field is the same person's SMTP address. A sender group with no Sender field of its own is the From field's author.
    private string? FromTransportField(Role role)
    {
        if (role == Role.Recipient)
        {
            return null;
        }

        string? sender = role == Role.Sender ? SingleMailbox("Sender") : null;
        if (sender is not null)
        {
            return sender;
        }

        if (role == Role.Sender && HasField("Sender"))
        {
            return null;
        }

        return SingleMailbox("From");
    }

    private bool HasField(string name) => Transport.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private string? SingleMailbox(string field)
    {
        foreach (HeaderField f in Transport)
        {
            if (f.Name.Equals(field, StringComparison.OrdinalIgnoreCase))
            {
                List<MimeMailbox> l = AddressParser.Flatten(AddressParser.Parse(Encoding.UTF8.GetBytes(f.Value)));
                return l.Count == 1 && LooksSmtp(l[0].Address) ? l[0].Address : null;
            }
        }

        return null;
    }

    // A name seen in the transport headers' From, To, Cc and Reply-To fields with exactly one address is the same person.
    // Two different addresses under one name make the match ambiguous and no guess is taken.
    private string? FromTransportByName(string name)
    {
        if (_headerNames is null)
        {
            _headerNames = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (HeaderField f in Transport)
            {
                if (f.Name.Equals("From", StringComparison.OrdinalIgnoreCase) || f.Name.Equals("To", StringComparison.OrdinalIgnoreCase)
                    || f.Name.Equals("Cc", StringComparison.OrdinalIgnoreCase) || f.Name.Equals("Sender", StringComparison.OrdinalIgnoreCase)
                    || f.Name.Equals("Reply-To", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (MimeMailbox m in AddressParser.Flatten(AddressParser.Parse(Encoding.UTF8.GetBytes(f.Value))))
                    {
                        if (string.IsNullOrEmpty(m.Name))
                        {
                            continue;
                        }

                        if (_headerNames.TryGetValue(m.Name, out string? prior) && !string.Equals(prior, m.Address, StringComparison.OrdinalIgnoreCase))
                        {
                            _headerNames[m.Name] = null;
                        }
                        else
                        {
                            _headerNames[m.Name] = m.Address;
                        }
                    }
                }
            }
        }

        return _headerNames.GetValueOrDefault(name);
    }

    /// <summary>Formats the instant as an RFC 5322 date in UTC.</summary>
    public static string FormatDate(DateTimeOffset t) =>
        t.UtcDateTime.ToString("ddd, dd MMM yyyy HH:mm:ss", CultureInfo.InvariantCulture) + " +0000";
}
