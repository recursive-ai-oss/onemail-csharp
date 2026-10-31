using System.Buffers.Binary;
using System.Globalization;
using RecursiveAI.OneArchive.Cfb;
using RecursiveAI.OneMail.Mapi;

namespace RecursiveAI.OneMail.Msg;

/// <summary>Reads one MS-OXMSG object storage (a message, recipient or attachment) into a property bag. Never throws on damaged content.</summary>
internal static class MsgObjectReader
{
    public const string PropertyStreamName = "__properties_version1.0";
    public const string NameIdStorageName = "__nameid_version1.0";
    public const string RecipientPrefix = "__recip_version1.0_#";
    public const string AttachmentPrefix = "__attach_version1.0_#";
    public const string ValuePrefix = "__substg1.0_";
    public const string EmbeddedObjectName = "__substg1.0_3701000D";

    internal sealed class Result
    {
        public MsgObjectLayout Layout { get; set; } = null!;

        public List<(int Index, CfbEntry Entry)> Recipients { get; } = [];

        public List<(int Index, CfbEntry Entry)> Attachments { get; } = [];

        /// <summary>The <c>__substg1.0_3701000D</c> storage of an attachment, if there is one.</summary>
        public CfbEntry? ObjectStorage { get; set; }
    }

    public static int HeaderSize(MsgObjectKind kind) => kind switch
    {
        MsgObjectKind.Message => 32,
        MsgObjectKind.EmbeddedMessage => 24,
        _ => 8,
    };

    public static Result Populate(MsgContainer c, MapiObject target, CfbEntry storage, string path, MsgObjectKind kind)
    {
        MsgReaderOptions opt = c.Options;
        IReadOnlyList<CfbEntry> kids = c.Children(storage);
        var index = new Dictionary<string, int>(kids.Count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < kids.Count; i++)
        {
            index.TryAdd(kids[i].RawName, i);
        }

        Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> lookup = index.GetAlternateLookup<ReadOnlySpan<char>>();
        var claimed = new bool[kids.Count];
        string Where(string name) => path.Length == 0 ? "/" + name : path + "/" + name;

        // ---- the property stream and its header (MS-OXMSG 2.4)
        int headerSize = HeaderSize(kind);
        byte[] ps = [];
        string psPath = Where(PropertyStreamName);
        if (index.TryGetValue(PropertyStreamName, out int psIndex) && kids[psIndex].ObjectType == CfbObjectType.Stream)
        {
            claimed[psIndex] = true;
            ps = c.ReadAll(kids[psIndex], psPath, headerSize + (16L * opt.MaxProperties)) ?? [];
        }
        else
        {
            c.Report(target, MapiDefectKind.PropertyStreamMissing, psPath, 0, "the storage has no property stream; it is read as having no properties");
        }

        byte[] rawHeader = ps.AsSpan(0, Math.Min(headerSize, ps.Length)).ToArray();
        if (ps.Length > 0 && ps.Length < headerSize)
        {
            c.Report(target, MapiDefectKind.PropertyStreamTruncated, psPath, ps.Length, $"{ps.Length} bytes is shorter than the {headerSize}-byte header");
        }

        int dataLength = Math.Max(0, ps.Length - headerSize);
        int count = dataLength / 16;
        if (dataLength % 16 != 0)
        {
            c.Report(target, MapiDefectKind.PropertyStreamTruncated, psPath, headerSize + (count * 16L), $"{dataLength % 16} bytes after the last whole 16-byte entry are ignored");
        }

        if (count > opt.MaxProperties)
        {
            c.Report(target, MapiDefectKind.LimitReached, psPath, headerSize, $"{count} entries; only the first {opt.MaxProperties} are read");
            count = opt.MaxProperties;
        }

        // ---- the entries
        var metas = new List<MsgEntryMeta>(count);
        var seen = new HashSet<uint>(count);
        var nameBuffer = new char[ValuePrefix.Length + 8 + 1 + 8];
        ValuePrefix.CopyTo(nameBuffer);
        MsgNameMap map = c.NameMap;
        int? storeSupportMask = null;
        CfbEntry? objectStorage = null;

        for (int n = 0; n < count; n++)
        {
            long entryOffset = headerSize + (n * 16L);
            ReadOnlySpan<byte> e = ps.AsSpan((int)entryOffset, 16);
            uint tagValue = BinaryPrimitives.ReadUInt32LittleEndian(e);
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
            ulong tail = BinaryPrimitives.ReadUInt64LittleEndian(e[8..]);
            uint size = (uint)tail;
            var tag = new MapiTag(tagValue);
            ushort elem = MapiTypes.ElementType(tag.Type);
            bool multi = tag.IsMultiValued;

            MapiNamedProperty? name = null;
            if (tag.IsNamed)
            {
                name = map.Get(tag.Id);
                if (name is null)
                {
                    c.Report(target, MapiDefectKind.NamedPropertyUnmapped, psPath, entryOffset, $"property {tag} has no entry in the named property mapping");
                }
            }

            if (!seen.Add(tagValue))
            {
                c.Report(target, MapiDefectKind.AmbiguousDuplicateProperty, psPath, entryOffset, $"property {tag} appears more than once; the last one wins lookups");
            }

            tag.Value.TryFormat(nameBuffer.AsSpan(ValuePrefix.Length), out _, "X8", CultureInfo.InvariantCulture);
            ReadOnlySpan<char> baseName = nameBuffer.AsSpan(0, ValuePrefix.Length + 8);

            MapiProperty prop;
            MsgValueStorage st;
            if (!multi)
            {
                int fixedSize = MapiTypes.FixedSize(elem);
                if (fixedSize is >= 1 and <= 8)
                {
                    var v = new byte[fixedSize];
                    e.Slice(8, fixedSize).CopyTo(v);
                    prop = MapiProperty.CreateOwned(tag, v, name);
                    st = MsgValueStorage.Inline;
                    if (tagValue == 0x340D0003)
                    {
                        storeSupportMask = BinaryPrimitives.ReadInt32LittleEndian(v);
                    }
                }
                else if (elem is (ushort)MapiType.Null or (ushort)MapiType.Unspecified)
                {
                    prop = MapiProperty.CreateOwned(tag, [], name);
                    st = MsgValueStorage.Inline;
                }
                else if (elem == (ushort)MapiType.EmbeddedObject && lookup.TryGetValue(baseName, out int oi) && kids[oi].ObjectType != CfbObjectType.Stream)
                {
                    claimed[oi] = true;
                    objectStorage = kids[oi];
                    prop = MapiProperty.CreateOwned(tag, [], name);
                    st = MsgValueStorage.ObjectStorage;
                }
                else if (!MapiTypes.IsKnown(tag.Type) && !lookup.TryGetValue(baseName, out _))
                {
                    // An undefined type with no value stream: the 8 bytes of the entry are all there is.
                    c.Report(target, MapiDefectKind.UnknownPropertyType, psPath, entryOffset, $"property {tag} has a type that MS-OXCDATA does not define");
                    prop = MapiProperty.CreateOwned(tag, e.Slice(8, 8).ToArray(), name);
                    st = MsgValueStorage.Inline;
                }
                else
                {
                    if (!MapiTypes.IsKnown(tag.Type))
                    {
                        c.Report(target, MapiDefectKind.UnknownPropertyType, psPath, entryOffset, $"property {tag} has a type that MS-OXCDATA does not define; its value stream is kept as bytes");
                    }

                    st = MsgValueStorage.Stream;
                    if (lookup.TryGetValue(baseName, out int vi) && kids[vi].ObjectType == CfbObjectType.Stream)
                    {
                        claimed[vi] = true;
                        CfbEntry s = kids[vi];
                        string sp = Where(kids[vi].RawName);
                        // MS-OXMSG 2.4.2.2: the size is the stream size plus 2 for PtypString and 1 for PtypString8, the terminator that is not stored.
                        // Files written with the terminator inside the stream (common in 8-bit files) give the stream size itself; both are read.
                        long terminator = elem == (ushort)MapiType.UnicodeString ? 2 : elem == (ushort)MapiType.String8 ? 1 : 0;
                        if (size != 0xFFFFFFFF && size != s.Size + terminator && size != s.Size)
                        {
                            c.Report(target, MapiDefectKind.ValueSizeMismatch, psPath, entryOffset, $"{tag}: the entry says {size} bytes but the stream holds {s.Size} (expected {s.Size + terminator})");
                        }

                        if (elem == (ushort)MapiType.UnicodeString && (s.Size & 1) != 0)
                        {
                            c.Report(target, MapiDefectKind.OddStringLength, sp, s.Size - 1, $"{tag}: a Unicode string stream of {s.Size} bytes has an odd length");
                        }

                        prop = MapiProperty.CreateLazy(tag, new MsgStreamValue(c, s, sp), name);
                    }
                    else
                    {
                        c.Report(target, MapiDefectKind.ValueStreamMissing, Where(ValuePrefix + tag.ToHex()), 0, $"property {tag} has an entry but no value stream; its value is empty");
                        prop = MapiProperty.CreateOwned(tag, [], name);
                        st = MsgValueStorage.Missing;
                    }
                }
            }
            else
            {
                int elemFixed = MapiTypes.FixedSize(elem);
                bool hasStream = lookup.TryGetValue(baseName, out int mi) && kids[mi].ObjectType == CfbObjectType.Stream;
                string mp = Where(ValuePrefix + tag.ToHex());
                if (elemFixed > 0 && elem != (ushort)MapiType.Boolean)
                {
                    st = MsgValueStorage.MultiFixedStream;
                    if (hasStream)
                    {
                        claimed[mi] = true;
                        CfbEntry s = kids[mi];
                        if (size != s.Size)
                        {
                            c.Report(target, MapiDefectKind.ValueSizeMismatch, psPath, entryOffset, $"{tag}: the entry says {size} bytes but the stream holds {s.Size}");
                        }

                        if (s.Size % elemFixed != 0)
                        {
                            c.Report(target, MapiDefectKind.MultiValueTrailingBytes, mp, s.Size - (s.Size % elemFixed), $"{tag}: {s.Size} bytes is not a whole number of {elemFixed}-byte elements");
                        }

                        prop = MapiProperty.CreateLazy(tag, new MsgFixedMultiValue(c, s, mp, elemFixed), name);
                    }
                    else
                    {
                        c.Report(target, MapiDefectKind.ValueStreamMissing, mp, 0, $"property {tag} has an entry but no value stream; it has no values");
                        prop = MapiProperty.CreateMulti(tag, [], name);
                        st = MsgValueStorage.Missing;
                    }
                }
                else if (elem is (ushort)MapiType.Binary or (ushort)MapiType.UnicodeString or (ushort)MapiType.String8)
                {
                    st = MsgValueStorage.MultiVariableStreams;
                    if (hasStream)
                    {
                        claimed[mi] = true;
                        prop = ReadVariableMulti(c, target, tag, name, kids[mi], kids, lookup, claimed, nameBuffer, elem, size, mp, psPath, entryOffset, opt);
                    }
                    else
                    {
                        c.Report(target, MapiDefectKind.ValueStreamMissing, mp, 0, $"property {tag} has an entry but no length stream; it has no values");
                        prop = MapiProperty.CreateMulti(tag, [], name);
                        st = MsgValueStorage.Missing;
                    }
                }
                else
                {
                    // A multi-valued type with no layout in MS-OXMSG (for example multiple objects): keep the stream's bytes as one value.
                    c.Report(target, MapiDefectKind.UnknownPropertyType, psPath, entryOffset, $"property {tag} is multi-valued with a type MS-OXMSG gives no layout for; its stream is kept as one value");
                    st = MsgValueStorage.Stream;
                    if (hasStream)
                    {
                        claimed[mi] = true;
                        prop = MapiProperty.CreateLazy(tag, new MsgStreamValue(c, kids[mi], mp), name);
                    }
                    else
                    {
                        prop = MapiProperty.CreateOwned(tag, [], name);
                        st = MsgValueStorage.Missing;
                    }
                }
            }

            target.Properties.Add(prop);
            metas.Add(new MsgEntryMeta(flags, tail, st, true));
        }

        // ---- everything else in the storage
        var result = new Result();
        var recipients = result.Recipients;
        var attachments = result.Attachments;
        bool isMessage = kind is MsgObjectKind.Message or MsgObjectKind.EmbeddedMessage;
        for (int k = 0; k < kids.Count; k++)
        {
            if (claimed[k])
            {
                continue;
            }

            CfbEntry kid = kids[k];
            string kidName = kid.RawName;
            if (isMessage && TryChildIndex(kidName, RecipientPrefix, out int ri, out bool badRecipient) && kid.ObjectType != CfbObjectType.Stream)
            {
                if (badRecipient)
                {
                    c.Report(target, MapiDefectKind.StorageNameInvalid, Where(kidName), 0, "recipient storage name does not end in eight hexadecimal digits; it is ignored");
                }
                else
                {
                    recipients.Add((ri, kid));
                }

                continue;
            }

            if (isMessage && TryChildIndex(kidName, AttachmentPrefix, out int ai, out bool badAttachment) && kid.ObjectType != CfbObjectType.Stream)
            {
                if (badAttachment)
                {
                    c.Report(target, MapiDefectKind.StorageNameInvalid, Where(kidName), 0, "attachment storage name does not end in eight hexadecimal digits; it is ignored");
                }
                else
                {
                    attachments.Add((ai, kid));
                }

                continue;
            }

            if (kind == MsgObjectKind.Attachment && kidName.Equals(EmbeddedObjectName, StringComparison.OrdinalIgnoreCase) && kid.ObjectType != CfbObjectType.Stream)
            {
                objectStorage = kid;
                continue;
            }

            if (kidName.Equals(NameIdStorageName, StringComparison.OrdinalIgnoreCase))
            {
                // The map of the whole file is read once, from the top level (MsgFile). Nowhere else is one expected (MS-OXMSG 2.2.2.1).
                if (kind != MsgObjectKind.Message)
                {
                    c.Report(target, MapiDefectKind.NestedNamedPropertyMap, Where(kidName), 0, "a named property mapping storage here is ignored; the top level's map applies");
                }

                continue;
            }

            if (kid.ObjectType == CfbObjectType.Stream && kidName.StartsWith(ValuePrefix, StringComparison.OrdinalIgnoreCase)
                && kidName.Length == ValuePrefix.Length + 8
                && uint.TryParse(kidName.AsSpan(ValuePrefix.Length), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint orphanTag))
            {
                // A value stream whose property has no entry. The data is there, so it is read as a property; there is no entry to keep flags from.
                var otag = new MapiTag(orphanTag);
                MapiNamedProperty? oname = otag.IsNamed ? map.Get(otag.Id) : null;
                c.Report(target, MapiDefectKind.AmbiguousOrphanValueStream, Where(kidName), 0, $"stream for property {otag} has no entry in the property stream; it is read as a property");
                target.Properties.Add(MapiProperty.CreateLazy(otag, new MsgStreamValue(c, kid, Where(kidName)), oname));
                metas.Add(new MsgEntryMeta(0, 0, MsgValueStorage.Stream, false));
                continue;
            }

            c.Report(target, MapiDefectKind.UnreferencedStream, Where(kidName), 0, $"{(kid.ObjectType == CfbObjectType.Stream ? "stream" : "storage")} is not part of the object and was not read");
        }

        recipients.Sort((a, b) => a.Index.CompareTo(b.Index));
        attachments.Sort((a, b) => a.Index.CompareTo(b.Index));

        var layout = new MsgObjectLayout(kind, path, rawHeader, metas.ToArray());
        if (isMessage)
        {
            if (layout.DeclaredRecipientCount is int dr && dr != recipients.Count)
            {
                c.Report(target, MapiDefectKind.RecipientCountMismatch, psPath, 16, $"the header declares {dr} recipients but {recipients.Count} storages exist");
            }

            if (layout.DeclaredAttachmentCount is int da && da != attachments.Count)
            {
                c.Report(target, MapiDefectKind.AttachmentCountMismatch, psPath, 20, $"the header declares {da} attachments but {attachments.Count} storages exist");
            }

            CheckStrings(c, target, psPath, storeSupportMask, kind);
        }

        if (kind == MsgObjectKind.Attachment && objectStorage is null
            && target.Properties.Get(MapiPid.AttachMethod, MapiType.Integer32)?.AsInt32() is 5 or 6)
        {
            c.Report(target, MapiDefectKind.EmbeddedObjectMissing, Where(EmbeddedObjectName), 0, "the attachment method says an embedded message or object, but the storage is missing");
        }

        result.Layout = layout;
        result.ObjectStorage = objectStorage;
        return result;
    }

    // Two things in a message decide how its 8-bit strings read, and both are only ambiguous when 8-bit strings exist.
    private static void CheckStrings(MsgContainer c, MapiObject target, string psPath, int? mask, MsgObjectKind kind)
    {
        bool has8 = false;
        bool has16 = false;
        foreach (MapiProperty p in target.Properties)
        {
            ushort e = MapiTypes.ElementType(p.Tag.Type);
            has8 |= e == (ushort)MapiType.String8;
            has16 |= e == (ushort)MapiType.UnicodeString;
        }

        if (mask is int m)
        {
            // MS-OXMSG 2.1.1.1: STORE_UNICODE_OK in PidTagStoreSupportMask means every string is PtypString; without it, PtypString8.
            bool unicode = (m & 0x00040000) != 0;
            if (kind == MsgObjectKind.Message)
            {
                c.UnicodeStrings = unicode;
            }

            if ((unicode && has8) || (!unicode && has16))
            {
                c.Report(target, MapiDefectKind.AmbiguousStringType, psPath, 0, unicode
                    ? "the store support mask says Unicode strings but 8-bit string properties are present; both are read by their own type"
                    : "the store support mask says 8-bit strings but Unicode string properties are present; both are read by their own type");
            }
        }

        if (has8)
        {
            foreach (ushort id in (ReadOnlySpan<ushort>)[MapiPid.MessageCodepage, MapiPid.InternetCodepage])
            {
                if (target.Properties.Get(id, MapiType.Integer32)?.AsInt32() is > 0 and int v && Text.MailEncoding.ForCodePage(v) is null)
                {
                    c.Report(target, MapiDefectKind.UnsupportedCodePage, psPath, 0, $"code page {v} has no encoding in the library and is skipped");
                }
            }

            // MS-OXMSG says nothing about which code page 8-bit strings use; only PidTagMessageCodepage is explicit about it.
            MapiCodePageSource source = target.String8EncodingSource;
            if (source != MapiCodePageSource.MessageCodepage)
            {
                c.Report(target, MapiDefectKind.AmbiguousCodePage, psPath, 0, $"8-bit strings have no PidTagMessageCodepage; {target.String8Encoding.Name} is used from {source}");
            }
        }
    }

    private static bool TryChildIndex(string name, string prefix, out int index, out bool bad)
    {
        index = 0;
        bad = false;
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ReadOnlySpan<char> hex = name.AsSpan(prefix.Length);
        if (hex.Length != 8 || !uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint v) || v > int.MaxValue)
        {
            bad = true;
            return true;
        }

        index = (int)v;
        return true;
    }

    private static MapiProperty ReadVariableMulti(
        MsgContainer c,
        MapiObject target,
        MapiTag tag,
        MapiNamedProperty? name,
        CfbEntry lengthStream,
        IReadOnlyList<CfbEntry> kids,
        Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> lookup,
        bool[] claimed,
        char[] nameBuffer,
        ushort elem,
        uint entrySize,
        string lengthPath,
        string psPath,
        long entryOffset,
        MsgReaderOptions opt)
    {
        // MS-OXMSG 2.1.4.2.1: binary lengths are 8 bytes (length, reserved), string lengths 4 bytes (including the terminator).
        int unit = elem == (ushort)MapiType.Binary ? 8 : 4;
        if (entrySize != lengthStream.Size)
        {
            c.Report(target, MapiDefectKind.ValueSizeMismatch, psPath, entryOffset, $"{tag}: the entry says {entrySize} bytes but the length stream holds {lengthStream.Size}");
        }

        byte[] lengths = c.ReadAll(lengthStream, lengthPath, (long)opt.MaxMultiValueCount * unit) ?? [];
        if (lengths.Length % unit != 0)
        {
            c.Report(target, MapiDefectKind.MultiValueLengthMismatch, lengthPath, lengths.Length - (lengths.Length % unit), $"{tag}: the length stream is not a whole number of {unit}-byte entries");
        }

        int n = lengths.Length / unit;
        var streams = new CfbEntry?[n];
        for (int i = 0; i < n; i++)
        {
            nameBuffer[ValuePrefix.Length + 8] = '-';
            i.TryFormat(nameBuffer.AsSpan(ValuePrefix.Length + 9), out _, "X8", CultureInfo.InvariantCulture);
            if (lookup.TryGetValue(nameBuffer, out int vi) && kids[vi].ObjectType == CfbObjectType.Stream)
            {
                claimed[vi] = true;
                streams[i] = kids[vi];
                long declared = BinaryPrimitives.ReadUInt32LittleEndian(lengths.AsSpan(i * unit));
                if (declared != kids[vi].Size)
                {
                    c.Report(target, MapiDefectKind.MultiValueLengthMismatch, lengthPath + "-" + i.ToString("X8", CultureInfo.InvariantCulture), 0, $"{tag}: value {i} is declared {declared} bytes but its stream holds {kids[vi].Size}");
                }
            }
            else
            {
                c.Report(target, MapiDefectKind.MultiValueStreamMissing, lengthPath + "-" + i.ToString("X8", CultureInfo.InvariantCulture), 0, $"{tag}: the stream for value {i} is missing; it is empty");
            }
        }

        return MapiProperty.CreateLazy(tag, new MsgVariableMultiValue(c, streams, lengthPath), name);
    }
}
