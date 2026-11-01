using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using RecursiveAI.OneArchive.Cfb;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Text;

namespace RecursiveAI.OneMail.Msg;

/// <summary>One stream or storage of the file to be written, before anything is written.</summary>
internal sealed class MsgPart
{
    public required string Path { get; init; }

    public bool IsStorage { get; init; }

    public Guid ClassId { get; init; }

    public long Size { get; init; }

    public ReadOnlyMemory<byte> Data { get; init; }

    public Func<Stream>? Open { get; init; }
}

internal sealed class MsgWritePlan
{
    public List<MsgPart> Parts { get; } = [];

    public List<MsgWriteDefect> Defects { get; } = [];

    public int Properties { get; set; }

    public int Recipients { get; set; }

    public int Attachments { get; set; }

    public int NamedProperties { get; set; }
}

/// <summary>
/// Checks a model against the limits and turns it into the list of streams and storages MS-OXMSG describes. Nothing is written here, so a model
/// that fails a check leaves the output untouched. Errors are thrown as <see cref="MsgWriteException"/> at the first one found.
/// </summary>
internal sealed class MsgPlanner
{
    // Ceilings over the whole file, so that a model that shares one embedded message many times cannot make the plan grow without bound.
    private const int MaxObjects = 1 << 20;
    private const long MaxAllProperties = 1L << 24;

    private const uint StoreSupportMaskTag = 0x340D0003;
    private const uint MessageCodepageTag = 0x3FFD0003;
    private const uint MessageFlagsTag = 0x0E070003;
    private const uint AttachMethodTag = 0x37050003;
    private const uint AttachObjectTag = 0x3701000D;
    private const int StoreUnicodeOk = 0x00040000;
    private const int MsgFlagUnsent = 0x8;

    // A readable and writable property (MS-OXMSG 2.4.2.1 PROPATTR_READABLE | PROPATTR_WRITABLE).
    private const uint DefaultFlags = 6;

    private static readonly Guid CompoundFileClassId = new("00020D0B-0000-0000-C000-000000000046");

    private readonly MsgWriterOptions _o;
    private readonly MsgWritePlan _plan = new();
    private readonly Dictionary<MapiNamedProperty, int> _nameIds = [];
    private readonly List<MapiNamedProperty> _names = [];
    private readonly MailEncoding? _ansi;
    private long _total;
    private long _allProps;
    private int _objects;

    public MsgPlanner(MsgWriterOptions options)
    {
        _o = options;
        if (_o.Strings == MsgStringMode.Ansi)
        {
            _ansi = MailEncoding.ForCodePage(_o.AnsiCodePage);
            if (_ansi is null || !_ansi.CanEncode || _ansi.CodePage == 0)
            {
                Fail(MsgWriteDefectKind.UnsupportedCodePage, string.Empty, $"code page {_o.AnsiCodePage} has no encoder in the library");
            }
        }
    }

    /// <summary>One property as it will be written: its final tag and where the value bytes come from.</summary>
    private sealed class Prep
    {
        public required MapiTag Tag { get; set; }

        public MapiProperty? Source { get; init; }

        public byte[][]? Values { get; set; }

        public bool Converted { get; init; }

        public int Count => Values?.Length ?? Source!.ValueCount;

        public long Length(int i) => Values is not null ? Values[i].Length : Source!.GetByteLength(i);

        public ReadOnlyMemory<byte> Bytes(int i) => Values is not null ? Values[i] : Source!.GetBytes(i);
    }

    private enum Kind
    {
        Message,
        Embedded,
        Recipient,
        Attachment,
    }

    public MsgWritePlan Plan(MapiMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        PlanMessage(message, string.Empty, depth: 0);
        WriteNameMap();
        _plan.NamedProperties = _names.Count;
        return _plan;
    }

    private void Warn(MsgWriteDefectKind kind, string where, string detail) => _plan.Defects.Add(new MsgWriteDefect(kind, where, detail));

    private void Fail(MsgWriteDefectKind kind, string where, string detail)
    {
        var all = new List<MsgWriteDefect>(_plan.Defects.Count + 1) { new(kind, where, detail) };
        all.AddRange(_plan.Defects);
        throw new MsgWriteException(all);
    }

    private static string Join(string dir, string name) => dir.Length == 0 ? name : dir + "/" + name;

    private static string Hex8(int n) => n.ToString("X8", CultureInfo.InvariantCulture);

    private void AddBytes(string path, byte[] data) => _plan.Parts.Add(new MsgPart { Path = path, Size = data.Length, Data = data });

    private void AddValue(string path, Prep p, int index)
    {
        long len = p.Length(index);
        if (len > _o.MaxValueBytes)
        {
            Fail(MsgWriteDefectKind.ValueTooLarge, path, $"a value of {len} bytes is larger than the limit of {_o.MaxValueBytes}");
        }

        _total += len;
        if (_total > _o.MaxTotalBytes)
        {
            Fail(MsgWriteDefectKind.TotalTooLarge, path, $"the values add up to more than the limit of {_o.MaxTotalBytes} bytes");
        }

        if (p.Values is not null || !p.Source!.IsLazy)
        {
            _plan.Parts.Add(new MsgPart { Path = path, Size = len, Data = p.Bytes(index) });
        }
        else
        {
            MapiProperty src = p.Source;
            int i = index;
            _plan.Parts.Add(new MsgPart { Path = path, Size = len, Open = () => src.OpenRead(i) });
        }
    }

    private void AddJoinedBytes(string path, byte[] data)
    {
        _total += data.Length;
        if (data.Length > _o.MaxValueBytes)
        {
            Fail(MsgWriteDefectKind.ValueTooLarge, path, $"a value of {data.Length} bytes is larger than the limit of {_o.MaxValueBytes}");
        }

        if (_total > _o.MaxTotalBytes)
        {
            Fail(MsgWriteDefectKind.TotalTooLarge, path, $"the values add up to more than the limit of {_o.MaxTotalBytes} bytes");
        }

        AddBytes(path, data);
    }

    // ---- objects

    private void PlanMessage(MapiMessage m, string dir, int depth)
    {
        if (depth > _o.MaxEmbeddingDepth)
        {
            Fail(MsgWriteDefectKind.EmbeddingTooDeep, dir, $"embedded messages are nested deeper than {_o.MaxEmbeddingDepth}");
        }

        CountObject(dir);
        IReadOnlyList<MapiRecipient> recipients = m.Recipients;
        IReadOnlyList<MapiAttachment> attachments = m.Attachments;
        if (recipients.Count > _o.MaxRecipients)
        {
            Fail(MsgWriteDefectKind.TooManyRecipients, dir, $"{recipients.Count} recipients; the limit is {_o.MaxRecipients}");
        }

        if (attachments.Count > _o.MaxAttachments)
        {
            Fail(MsgWriteDefectKind.TooManyAttachments, dir, $"{attachments.Count} attachments; the limit is {_o.MaxAttachments}");
        }

        bool top = depth == 0;
        List<Prep> props = Prepare(m, dir, top ? Kind.Message : Kind.Embedded, hasObject: false);

        // MS-OXMSG 2.4.1.1 / 2.4.1.2: 8 reserved bytes, next recipient ID, next attachment ID, recipient count, attachment count; the top level adds 8 more reserved bytes.
        byte[] header = new byte[top ? 32 : 24];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), recipients.Count);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), attachments.Count);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), recipients.Count);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20), attachments.Count);
        EmitProperties(dir, header, props, embeddedMarker: null);

        for (int i = 0; i < recipients.Count; i++)
        {
            string rd = Join(dir, "__recip_version1.0_#" + Hex8(i));
            CountObject(rd);
            _plan.Recipients++;
            EmitProperties(rd, new byte[8], Prepare(recipients[i], rd, Kind.Recipient, hasObject: false), embeddedMarker: null);
        }

        for (int i = 0; i < attachments.Count; i++)
        {
            MapiAttachment a = attachments[i];
            string ad = Join(dir, "__attach_version1.0_#" + Hex8(i));
            CountObject(ad);
            _plan.Attachments++;
            MapiMessage? embedded = a.EmbeddedMessage;
            MapiRawStorage? storage = embedded is null ? a.ObjectStorage : null;
            if (embedded is not null && a.ObjectStorage is not null)
            {
                Warn(MsgWriteDefectKind.EmbeddedAndStorage, ad, "the attachment has an embedded message and a raw storage; only the message is written");
            }

            List<Prep> ap = Prepare(a, ad, Kind.Attachment, hasObject: embedded is not null || storage is not null);
            AdjustAttachMethod(ap, ad, embedded is not null, storage is not null);
            uint? marker = embedded is not null ? 1u : storage is not null ? (a.AttachMethodValue == 5 ? 1u : 4u) : null;
            EmitProperties(ad, new byte[8], ap, marker);
            string objectPath = Join(ad, MsgObjectReader.EmbeddedObjectName);
            if (embedded is not null)
            {
                PlanMessage(embedded, objectPath, depth + 1);
            }
            else if (storage is not null)
            {
                PlanRawStorage(storage, objectPath);
            }
            else if (a.AttachMethodValue is 5 or 6)
            {
                Warn(MsgWriteDefectKind.EmbeddedObjectMissing, ad, "PidTagAttachMethod says an embedded message or object but the attachment has none");
            }
        }
    }

    private void CountObject(string where)
    {
        if (++_objects > MaxObjects)
        {
            Fail(MsgWriteDefectKind.TooManyProperties, where, $"the model has more than {MaxObjects} objects");
        }
    }

    private void PlanRawStorage(MapiRawStorage storage, string path)
    {
        _plan.Parts.Add(new MsgPart { Path = path, IsStorage = true, ClassId = storage.ClassId });
        IReadOnlyList<MapiRawStorageEntry> entries = storage.Entries;
        if (entries.Count > _o.MaxProperties)
        {
            Fail(MsgWriteDefectKind.TooManyProperties, path, $"the object storage has {entries.Count} entries; the limit is {_o.MaxProperties}");
        }

        foreach (MapiRawStorageEntry e in entries)
        {
            string[] segments = e.Path.Split('/');
            bool ok = true;
            var sanitized = new StringBuilder();
            foreach (string seg in segments)
            {
                if (seg.Length is 0 or > 31 || seg.AsSpan().IndexOfAny("\\:!\0") >= 0)
                {
                    ok = false;
                    break;
                }

                sanitized.Append('/').Append(CompoundFileReader.SanitizeName(seg));
            }

            if (!ok)
            {
                Warn(MsgWriteDefectKind.RawStorageEntryDropped, path, $"the entry '{CompoundFileReader.SanitizeName(e.Path)}' has a name a compound file cannot hold and was left out");
                continue;
            }

            string full = path + sanitized;
            if (e.IsStorage)
            {
                _plan.Parts.Add(new MsgPart { Path = full, IsStorage = true });
            }
            else
            {
                if (e.Length > _o.MaxValueBytes)
                {
                    Fail(MsgWriteDefectKind.ValueTooLarge, full, $"a stream of {e.Length} bytes is larger than the limit of {_o.MaxValueBytes}");
                }

                _total += e.Length;
                if (_total > _o.MaxTotalBytes)
                {
                    Fail(MsgWriteDefectKind.TotalTooLarge, full, $"the values add up to more than the limit of {_o.MaxTotalBytes} bytes");
                }

                string source = e.Path;
                _plan.Parts.Add(new MsgPart { Path = full, Size = e.Length, Open = () => storage.OpenStream(source) });
            }
        }
    }

    // ---- properties

    private List<Prep> Prepare(MapiObject obj, string dir, Kind kind, bool hasObject)
    {
        var list = new List<Prep>();
        var byTag = new Dictionary<uint, int>();
        MailEncoding objectEncoding = obj.String8Encoding;
        int seen = 0;
        foreach (MapiProperty p in obj.Properties)
        {
            if (++seen > _o.MaxProperties)
            {
                Fail(MsgWriteDefectKind.TooManyProperties, dir, $"more than {_o.MaxProperties} properties");
            }

            MapiTag tag = p.Tag;
            // The attachment's object property stands for the storage that is written below (MS-OXMSG 2.2.2.1); its own bytes, a reference in other formats, are not stored.
            // With no storage to stand for it is an ordinary value.
            if (kind == Kind.Attachment && tag.Value == AttachObjectTag && hasObject)
            {
                continue;
            }

            if (tag.IsNamed)
            {
                if (p.Name is not { } name)
                {
                    Warn(MsgWriteDefectKind.NamedPropertyUnmapped, dir + " " + tag, $"property {tag} is a named property with no name; the file format stores names, so it was left out");
                    continue;
                }

                tag = new MapiTag((ushort)AssignName(name, dir), tag.Type);
            }

            Prep? prep = Convert(p, tag, dir, objectEncoding);
            if (prep is not null)
            {
                Add(list, byTag, prep, dir);
            }
        }

        _allProps += list.Count;
        if (_allProps > MaxAllProperties)
        {
            Fail(MsgWriteDefectKind.TooManyProperties, dir, $"the model has more than {MaxAllProperties} properties in all");
        }

        Adjust(list, byTag, dir, kind, obj);
        return list;
    }

    private int AssignName(MapiNamedProperty name, string where)
    {
        if (_nameIds.TryGetValue(name, out int id))
        {
            return id;
        }

        if (_names.Count >= _o.MaxNamedProperties || _names.Count >= 0x7FFF)
        {
            Fail(MsgWriteDefectKind.TooManyNamedProperties, where, $"more than {Math.Min(_o.MaxNamedProperties, 0x7FFF)} named properties");
        }

        id = 0x8000 + _names.Count;
        _nameIds.Add(name, id);
        _names.Add(name);
        return id;
    }

    private void Add(List<Prep> list, Dictionary<uint, int> byTag, Prep prep, string dir)
    {
        if (byTag.TryGetValue(prep.Tag.Value, out int at))
        {
            Prep old = list[at];
            Warn(MsgWriteDefectKind.DuplicateProperty, dir + " " + prep.Tag, $"property {prep.Tag} appears more than once; one value is kept");
            if (old.Converted && !prep.Converted)
            {
                list[at] = prep;
            }
            else if (!old.Converted && prep.Converted)
            {
                // The original Unicode (or 8-bit) property wins over one made by converting its twin.
            }
            else
            {
                list[at] = prep;
            }

            return;
        }

        byTag.Add(prep.Tag.Value, list.Count);
        list.Add(prep);
    }

    private Prep? Convert(MapiProperty p, MapiTag tag, string dir, MailEncoding objectEncoding)
    {
        ushort type = tag.Type;
        ushort elem = MapiTypes.ElementType(type);
        bool multi = MapiTypes.IsMultiValued(type);
        string where = dir + " " + tag;
        int count = p.ValueCount;
        if (multi && count > _o.MaxMultiValueCount)
        {
            Fail(MsgWriteDefectKind.TooManyValues, where, $"{count} values; the limit is {_o.MaxMultiValueCount}");
        }

        if (elem is (ushort)MapiType.Null or (ushort)MapiType.Unspecified)
        {
            return new Prep { Tag = tag, Source = p, Values = [[]] };
        }

        if (MapiTypes.IsString(elem))
        {
            return ConvertString(p, tag, where, objectEncoding, elem, multi, count);
        }

        int fixedSize = MapiTypes.FixedSize(elem);
        if (fixedSize > 0 && !(multi && elem == (ushort)MapiType.Boolean))
        {
            // Every element has a size of its own type; a short or long one is padded or cut so the stream stays a whole number of elements.
            byte[][]? fixedValues = null;
            for (int i = 0; i < count; i++)
            {
                long len = p.GetByteLength(i);
                if (len != fixedSize)
                {
                    fixedValues ??= Materialise(p, count, where);
                    byte[] v = new byte[fixedSize];
                    fixedValues[i].AsSpan(0, Math.Min(fixedSize, fixedValues[i].Length)).CopyTo(v);
                    fixedValues[i] = v;
                    Warn(MsgWriteDefectKind.FixedValueResized, where, $"value {i} has {len} bytes but the type has {fixedSize}; it was {(len < fixedSize ? "padded with zeros" : "cut")}");
                }
            }

            if (!multi && count != 1)
            {
                return null;
            }

            return new Prep { Tag = tag, Source = p, Values = fixedValues };
        }

        return new Prep { Tag = tag, Source = p };
    }

    private byte[][] Materialise(MapiProperty p, int count, string where)
    {
        var values = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            long len = p.GetByteLength(i);
            if (len > _o.MaxValueBytes)
            {
                Fail(MsgWriteDefectKind.ValueTooLarge, where, $"a value of {len} bytes is larger than the limit of {_o.MaxValueBytes}");
            }

            try
            {
                values[i] = p.GetBytes(i).ToArray();
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or InvalidDataException)
            {
                Fail(MsgWriteDefectKind.ValueUnreadable, where, e.Message);
            }
        }

        return values;
    }

    private Prep? ConvertString(MapiProperty p, MapiTag tag, string where, MailEncoding objectEncoding, ushort elem, bool multi, int count)
    {
        bool toUnicode = _o.Strings == MsgStringMode.Unicode && elem == (ushort)MapiType.String8;
        bool toAnsi = _o.Strings == MsgStringMode.Ansi && (elem == (ushort)MapiType.UnicodeString || _ansi!.CodePage != objectEncoding.CodePage);
        bool unicodeOut = toUnicode || (elem == (ushort)MapiType.UnicodeString && !toAnsi);
        ushort outElem = unicodeOut ? (ushort)MapiType.UnicodeString : (ushort)MapiType.String8;
        var outTag = new MapiTag(tag.Id, multi ? (ushort)(outElem | MapiTypes.MultiValuedFlag) : outElem);
        bool recode = toUnicode || toAnsi;

        if (!multi && !recode)
        {
            // Nothing changes: the value is not read here, only its size is checked, so a large body stays in the file it came from.
            long len = p.GetByteLength(0);
            if (len > _o.MaxValueBytes)
            {
                Fail(MsgWriteDefectKind.ValueTooLarge, where, $"a value of {len} bytes is larger than the limit of {_o.MaxValueBytes}");
            }

            if (len == 0 && !_o.KeepEmptyStrings)
            {
                Warn(MsgWriteDefectKind.EmptyStringDropped, where, "a string with no characters cannot be stored (MS-OXMSG 2.4.2.2); the property was left out");
                return null;
            }

            if (unicodeOut && (len & 1) != 0)
            {
                Warn(MsgWriteDefectKind.OddUnicodeLength, where, $"the Unicode string has {len} bytes, which is odd");
            }

            return new Prep { Tag = outTag, Source = p };
        }

        var values = new byte[count][];
        bool modified = false;
        bool anyIssue = false;
        for (int i = 0; i < count; i++)
        {
            long len = p.GetByteLength(i);
            if (len > _o.MaxValueBytes)
            {
                Fail(MsgWriteDefectKind.ValueTooLarge, where, $"a value of {len} bytes is larger than the limit of {_o.MaxValueBytes}");
            }

            byte[] raw;
            try
            {
                raw = p.GetBytes(i).ToArray();
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or InvalidDataException)
            {
                Fail(MsgWriteDefectKind.ValueUnreadable, where, e.Message);
                return null;
            }

            byte[] value = raw;
            if (recode)
            {
                string text = p.AsString(objectEncoding, null, i) ?? string.Empty;
                if (unicodeOut)
                {
                    value = Encoding.Unicode.GetBytes(text);
                }
                else
                {
                    var issues = new List<TextIssue>();
                    value = _ansi!.GetBytes(text, issues);
                    anyIssue |= issues.Count > 0;
                }
            }

            if (multi)
            {
                if (_o.Strings != MsgStringMode.AsGiven)
                {
                    value = WithTerminator(value, unicodeOut);
                }
            }
            else if (value.Length == 0 && !_o.KeepEmptyStrings)
            {
                Warn(MsgWriteDefectKind.EmptyStringDropped, where, "a string with no characters cannot be stored (MS-OXMSG 2.4.2.2); the property was left out");
                return null;
            }

            if (unicodeOut && (value.Length & 1) != 0)
            {
                Warn(MsgWriteDefectKind.OddUnicodeLength, where, $"the Unicode string value {i} has {value.Length} bytes, which is odd");
            }

            modified |= !value.AsSpan().SequenceEqual(raw);
            values[i] = value;
        }

        if (recode)
        {
            Warn(MsgWriteDefectKind.StringConverted, where, unicodeOut ? "an 8-bit string was written as Unicode" : $"a string was written as {_ansi!.Name} text");
        }

        if (anyIssue)
        {
            Warn(MsgWriteDefectKind.StringNotRepresentable, where, $"characters the code page {_ansi!.Name} cannot hold were replaced by '?'");
        }

        // Converted: made from the twin of another type, which loses to a property that really has this type.
        return new Prep { Tag = outTag, Source = p, Values = modified ? values : null, Converted = outElem != elem };
    }

    // MS-OXMSG 2.1.4.2.2: every value of a multiple-valued string property ends with the NULL terminating character.
    private static byte[] WithTerminator(byte[] value, bool unicode)
    {
        int t = unicode ? 2 : 1;
        if (value.Length >= t && value.AsSpan(value.Length - t).IndexOfAnyExcept((byte)0) < 0)
        {
            return value;
        }

        byte[] longer = new byte[value.Length + t];
        value.CopyTo(longer, 0);
        return longer;
    }

    // ---- derived properties the options and the attachment shape require

    private void Adjust(List<Prep> list, Dictionary<uint, int> byTag, string dir, Kind kind, MapiObject obj)
    {
        bool isMessage = kind is Kind.Message or Kind.Embedded;
        bool ansi = _o.Strings == MsgStringMode.Ansi;
        bool unicode = _o.Strings == MsgStringMode.Unicode;
        if (isMessage)
        {
            if (unicode || ansi)
            {
                int? mask = IntOf(list, byTag, StoreSupportMaskTag);
                if (unicode && (kind == Kind.Message ? ((mask ?? 0) & StoreUnicodeOk) == 0 : mask is { } um && (um & StoreUnicodeOk) == 0))
                {
                    SetInt(list, byTag, StoreSupportMaskTag, (mask ?? 0) | StoreUnicodeOk, dir, "PidTagStoreSupportMask got STORE_UNICODE_OK for Unicode strings");
                }
                else if (ansi && mask is { } m && (m & StoreUnicodeOk) != 0)
                {
                    SetInt(list, byTag, StoreSupportMaskTag, m & ~StoreUnicodeOk, dir, "PidTagStoreSupportMask lost STORE_UNICODE_OK for 8-bit strings");
                }

                if (ansi && IntOf(list, byTag, MessageCodepageTag) != _ansi!.CodePage)
                {
                    SetInt(list, byTag, MessageCodepageTag, _ansi.CodePage, dir, $"PidTagMessageCodepage was set to {_ansi.CodePage}");
                }
            }

            if (_o.Template && kind == Kind.Message && ((IntOf(list, byTag, MessageFlagsTag) ?? 0) & MsgFlagUnsent) == 0)
            {
                SetInt(list, byTag, MessageFlagsTag, (IntOf(list, byTag, MessageFlagsTag) ?? 0) | MsgFlagUnsent, dir, "PidTagMessageFlags got MSGFLAG_UNSENT for a template");
            }
        }
        else if (ansi && byTag.ContainsKey(MessageCodepageTag) && IntOf(list, byTag, MessageCodepageTag) != _ansi!.CodePage)
        {
            SetInt(list, byTag, MessageCodepageTag, _ansi.CodePage, dir, $"PidTagMessageCodepage was set to {_ansi.CodePage}");
        }
    }

    private void AdjustAttachMethod(List<Prep> list, string dir, bool hasMessage, bool hasStorage)
    {
        var byTag = new Dictionary<uint, int>();
        for (int i = 0; i < list.Count; i++)
        {
            byTag[list[i].Tag.Value] = i;
        }

        int? method = IntOf(list, byTag, AttachMethodTag);
        if (hasMessage && method != 5)
        {
            SetInt(list, byTag, AttachMethodTag, 5, dir, "PidTagAttachMethod was set to ATTACH_EMBEDDED_MSG");
        }
        else if (hasStorage && method is null)
        {
            SetInt(list, byTag, AttachMethodTag, 6, dir, "PidTagAttachMethod was set to ATTACH_OLE");
        }
    }

    private static int? IntOf(List<Prep> list, Dictionary<uint, int> byTag, uint tag)
    {
        if (!byTag.TryGetValue(tag, out int at))
        {
            return null;
        }

        ReadOnlyMemory<byte> b = list[at].Bytes(0);
        return b.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(b.Span) : null;
    }

    private void SetInt(List<Prep> list, Dictionary<uint, int> byTag, uint tag, int value, string dir, string what)
    {
        byte[] v = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(v, value);
        var prep = new Prep { Tag = new MapiTag(tag), Values = [v] };
        if (byTag.TryGetValue(tag, out int at))
        {
            list[at] = prep;
        }
        else
        {
            byTag.Add(tag, list.Count);
            list.Add(prep);
        }

        Warn(MsgWriteDefectKind.PropertyAdjusted, dir, what);
    }

    // ---- the property stream and the value streams (MS-OXMSG 2.4, 2.1)

    private void EmitProperties(string dir, byte[] header, List<Prep> props, uint? embeddedMarker)
    {
        using var ps = new MemoryStream(header.Length + (16 * (props.Count + 1)));
        ps.Write(header);
        Span<byte> entry = stackalloc byte[16];
        foreach (Prep p in props)
        {
            entry.Clear();
            MapiTag tag = p.Tag;
            ushort type = tag.Type;
            ushort elem = MapiTypes.ElementType(type);
            bool multi = MapiTypes.IsMultiValued(type);
            string stream = Join(dir, MsgObjectReader.ValuePrefix + tag.ToHex());
            BinaryPrimitives.WriteUInt32LittleEndian(entry, tag.Value);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], DefaultFlags);
            int fixedSize = MapiTypes.FixedSize(elem);
            if (!multi && elem is (ushort)MapiType.Null or (ushort)MapiType.Unspecified)
            {
                // No value: the entry's 8 value bytes are zero.
            }
            else if (!multi && fixedSize is >= 1 and <= 8)
            {
                // MS-OXMSG 2.4.2.1: the value, then reserved bytes that are zero when writing.
                ReadOnlySpan<byte> v = p.Count > 0 ? p.Bytes(0).Span : default;
                v[..Math.Min(v.Length, fixedSize)].CopyTo(entry[8..]);
            }
            else if (!multi)
            {
                // MS-OXMSG 2.4.2.2: the size of the stream, plus the terminator that is not stored for a string.
                long len = p.Count > 0 ? p.Length(0) : 0;
                if (p.Count > 0)
                {
                    AddValue(stream, p, 0);
                }
                else
                {
                    AddBytes(stream, []);
                }

                long terminator = elem == (ushort)MapiType.UnicodeString ? 2 : elem == (ushort)MapiType.String8 ? 1 : 0;
                BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)(len + terminator));
            }
            else if (fixedSize > 0 && elem != (ushort)MapiType.Boolean)
            {
                // MS-OXMSG 2.1.4.1: all elements back to back in one stream named for the tag.
                var joined = new MemoryStream();
                for (int i = 0; i < p.Count; i++)
                {
                    joined.Write(p.Bytes(i).Span);
                }

                AddJoinedBytes(stream, joined.ToArray());
                BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)joined.Length);
            }
            else if (elem is (ushort)MapiType.Binary or (ushort)MapiType.UnicodeString or (ushort)MapiType.String8)
            {
                // MS-OXMSG 2.1.4.2: a length stream (8 bytes per binary value, 4 per string value) and one stream per value, named <tag>-<index>.
                bool binary = elem == (ushort)MapiType.Binary;
                byte[] lengths = new byte[p.Count * (binary ? 8 : 4)];
                for (int i = 0; i < p.Count; i++)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(lengths.AsSpan(i * (binary ? 8 : 4)), (uint)p.Length(i));
                    AddValue(stream + "-" + Hex8(i), p, i);
                }

                AddBytes(stream, lengths);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)lengths.Length);
            }
            else
            {
                // A multi-valued type MS-OXMSG gives no layout for: the values' bytes as one stream, as the reader keeps them.
                var joined = new MemoryStream();
                for (int i = 0; i < p.Count; i++)
                {
                    joined.Write(p.Bytes(i).Span);
                }

                byte[] all = joined.ToArray();
                AddJoinedBytes(stream, all);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)all.Length);
            }

            ps.Write(entry);
            _plan.Properties++;
        }

        if (embeddedMarker is { } marker)
        {
            // MS-OXMSG 2.4.2.2: for an embedded message or storage attachment the size is 0xFFFFFFFF and the reserved field is 0x01 or 0x04.
            entry.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(entry, AttachObjectTag);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], 2);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], 0xFFFFFFFF);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], marker);
            ps.Write(entry);
            _plan.Properties++;
        }

        AddBytes(Join(dir, MsgObjectReader.PropertyStreamName), ps.ToArray());
    }

    // ---- the named property mapping (MS-OXMSG 2.2.3)

    private void WriteNameMap()
    {
        const string dir = MsgObjectReader.NameIdStorageName;
        var guids = new List<Guid>();
        var entries = new MemoryStream();
        var strings = new MemoryStream();
        var hashed = new SortedDictionary<int, MemoryStream>();
        Span<byte> e8 = stackalloc byte[8];
        for (int i = 0; i < _names.Count; i++)
        {
            MapiNamedProperty n = _names[i];
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

            // MS-OXMSG 2.2.3.1.2.1: property index in the high 16 bits, GUID index in bits 1 to 15, the kind in bit 0.
            uint kindInfo = ((uint)i << 16) | (uint)(guidIndex << 1) | (n.IsString ? 1u : 0u);
            uint idOrOffset;
            uint hashKey;
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

                // MS-OXMSG 2.2.3.2.2.1 footnote 1: Outlook lower-cases the name of a PS_INTERNET_HEADERS property before the checksum.
                hashKey = n.PropertySet == MapiPropertySets.InternetHeaders ? Crc32(Encoding.Unicode.GetBytes(n.Name!.ToLowerInvariant())) : Crc32(name);
            }
            else
            {
                idOrOffset = n.Lid;
                hashKey = n.Lid;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(e8, idOrOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(e8[4..], kindInfo);
            entries.Write(e8);

            // MS-OXMSG 2.2.3.2.2.1: stream ID = 0x1000 + ((ID XOR (GUID index << 1 | kind)) MOD 0x1F)
            int streamId = 0x1000 + (int)((hashKey ^ (uint)((guidIndex << 1) | (n.IsString ? 1 : 0))) % 0x1F);
            if (!hashed.TryGetValue(streamId, out MemoryStream? h))
            {
                hashed[streamId] = h = new MemoryStream();
            }

            BinaryPrimitives.WriteUInt32LittleEndian(e8, hashKey);
            BinaryPrimitives.WriteUInt32LittleEndian(e8[4..], kindInfo);
            h.Write(e8);
        }

        _plan.Parts.Add(new MsgPart { Path = dir, IsStorage = true });
        byte[] guidBytes = new byte[guids.Count * 16];
        for (int i = 0; i < guids.Count; i++)
        {
            guids[i].TryWriteBytes(guidBytes.AsSpan(i * 16));
        }

        AddBytes(dir + "/__substg1.0_00020102", guidBytes);
        AddBytes(dir + "/__substg1.0_00030102", entries.ToArray());
        AddBytes(dir + "/__substg1.0_00040102", strings.ToArray());
        foreach ((int id, MemoryStream h) in hashed)
        {
            AddBytes(dir + "/" + MsgObjectReader.ValuePrefix + (((uint)id << 16) | 0x102).ToString("X8", CultureInfo.InvariantCulture), h.ToArray());
        }
    }

    /// <summary>The root storage's class ID: the one Outlook gives message files (00020D0B-0000-0000-C000-000000000046).</summary>
    public static Guid RootClassId => CompoundFileClassId;

    private static readonly uint[] s_crcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320 : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    // MS-OXMSG 2.2.3.2.2.1 names "CRC-32" without saying which. Files Outlook wrote settle it: the reflected polynomial 0xEDB88320 (the one of zlib's CRC-32)
    // with a start value of 0 and no final complement, taken over the UTF-16LE name. The checksum is only a lookup key, so a reader that hashed differently
    // would not find the entry, which is why this has to match exactly.
    internal static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0;
        foreach (byte b in data)
        {
            crc = s_crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }
}
