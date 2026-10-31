using System.Buffers.Binary;
using System.Text;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Text;

namespace RecursiveAI.OneMail.Tnef;

/// <summary>
/// Writes a <see cref="MapiMessage"/> as a TNEF stream (MS-OXTNEF 2.2): attTnefVersion, attOemCodepage, the legacy attributes that old readers look for,
/// the recipient table, every property in attMsgProps, and for each attachment attAttachRendData, attAttachData, attAttachTitle and the rest of its
/// properties in attAttachment. An embedded message is written as a nested TNEF stream inside a PtypObject property. Reading the result back gives the
/// same properties, recipients and attachments.
/// </summary>
/// <remarks>
/// Properties the format cannot carry are left out and named in the returned list: a type MS-OXTNEF does not define, a named property whose name is unknown,
/// an OLE object attachment (its storage is not a property value), an object property with no data. Large attachment data is copied from its source piece by
/// piece; embedded messages are built in memory.
/// </remarks>
public static class TnefMessageWriter
{
    private const ushort Unicode = 0x1F;
    private const ushort String8 = 0x1E;
    private const ushort Binary = 0x102;
    private const ushort Object = 0x0D;
    private const ushort Boolean = 0x0B;

    /// <summary>Writes the message.</summary>
    /// <param name="message">The message; properties are read through their sources, so lazy values are copied across.</param>
    /// <param name="output">Where the bytes go; not closed.</param>
    /// <param name="options">Writer choices, or null.</param>
    /// <returns>A description of each property or attachment part that could not be carried; empty when nothing was lost.</returns>
    public static IReadOnlyList<string> Write(MapiMessage message, Stream output, TnefWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(output);
        var warnings = new List<string>();
        WriteMessage(message, output, options ?? new TnefWriterOptions(), warnings, 0, "");
        return warnings;
    }

    private readonly record struct WireProp(MapiTag Tag, MapiNamedProperty? Name, MapiProperty? Prop, byte[]? ObjectBytes);

    private static void WriteMessage(MapiMessage m, Stream output, TnefWriterOptions o, List<string> warnings, int depth, string where)
    {
        var w = new TnefWriter(output, o.LegacyKey);
        MailEncoding enc = m.String8Encoding;
        if (enc.CodePage == 0 || !enc.CanEncode)
        {
            enc = MailEncoding.Windows1252;
        }

        w.WriteAttribute(1, TnefAttributeIds.TnefVersion, [0x00, 0x00, 0x01, 0x00]);
        Span<byte> cp = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(cp, (uint)enc.CodePage);
        w.WriteAttribute(1, TnefAttributeIds.OemCodepage, cp);

        // Legacy attributes: text that the code page cannot hold is left to the Unicode property in attMsgProps.
        if (m.MessageClass is { } cls && Encode(enc, MessageLegacy.ReverseClass(cls)) is { } clsBytes)
        {
            w.WriteAttribute(1, TnefAttributeIds.MessageClass, clsBytes);
        }

        WriteFrom(w, m, enc);
        if (m.Subject is { } subject && Encode(enc, subject) is { } sb)
        {
            w.WriteAttribute(1, TnefAttributeIds.Subject, sb);
        }

        WriteDate(w, TnefAttributeIds.DateSent, m, MapiPid.ClientSubmitTime);
        WriteDate(w, TnefAttributeIds.DateRecd, m, MapiPid.MessageDeliveryTime);
        WriteDate(w, TnefAttributeIds.DateModified, m, MapiPid.LastModificationTime);
        if (m.Properties.Get(MapiPid.Importance, MapiType.Integer32)?.AsInt32() is int imp and >= 0 and <= 2)
        {
            Span<byte> p = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(p, (ushort)(3 - imp));
            w.WriteAttribute(1, TnefAttributeIds.Priority, p);
        }

        if (m.Properties.Get(MapiPid.SearchKey, MapiType.Binary) is { } key && key.GetByteLength() is > 0 and < 4096)
        {
            // MS-OXTNEF 2.2.3.7: the search key is written as hexadecimal text.
            w.WriteAttribute(1, TnefAttributeIds.MessageId, NulTerminated(Encoding.ASCII.GetBytes(Convert.ToHexString(key.GetBytes().Span))));
        }

        if (o.IncludeRecipients && m.Recipients.Count > 0)
        {
            var rows = new List<List<WireProp>>();
            for (int i = 0; i < m.Recipients.Count; i++)
            {
                rows.Add(Collect(m.Recipients[i].Properties, warnings, where + "recipient " + i, skipObject: true, dropId: null));
            }

            long size = 4;
            foreach (List<WireProp> row in rows)
            {
                size += ListSize(row);
            }

            w.BeginAttribute(1, TnefAttributeIds.RecipTable, size);
            WriteUInt32(w, (uint)rows.Count);
            foreach (List<WireProp> row in rows)
            {
                WriteList(w, row);
            }

            w.EndAttribute();
        }

        List<WireProp> props = Collect(m.Properties, warnings, where + "message", skipObject: true, dropId: null);
        w.BeginAttribute(1, TnefAttributeIds.MsgProps, ListSize(props));
        WriteList(w, props);
        w.EndAttribute();

        for (int i = 0; i < m.Attachments.Count; i++)
        {
            WriteAttachment(w, m.Attachments[i], enc, o, warnings, depth, where + "attachment " + i);
        }
    }

    private static void WriteAttachment(TnefWriter w, MapiAttachment a, MailEncoding enc, TnefWriterOptions o, List<string> warnings, int depth, string where)
    {
        int method = a.AttachMethodValue ?? 0;
        bool macBinary = a.Properties.Get(MapiPid.AttachEncoding, MapiType.Binary) is { } e && e.GetBytes().Span.SequenceEqual(new byte[] { 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x14, 0x03, 0x0B, 0x01 });
        Span<byte> rend = stackalloc byte[14];
        BinaryPrimitives.WriteUInt16LittleEndian(rend, (ushort)(method == 6 ? 2 : 1));
        BinaryPrimitives.WriteInt32LittleEndian(rend[2..], a.GetInt32(MapiPid.RenderingPosition) ?? -1);
        BinaryPrimitives.WriteInt16LittleEndian(rend[6..], -1);
        BinaryPrimitives.WriteInt16LittleEndian(rend[8..], -1);
        BinaryPrimitives.WriteUInt32LittleEndian(rend[10..], macBinary ? 1u : 0u);
        w.WriteAttribute(2, TnefAttributeIds.AttachRendData, rend);

        MapiProperty? data = a.Properties.Get(MapiPid.AttachDataBinary, MapiType.Binary);
        bool dataAsAttribute = data is not null && method is 0 or 1;
        if (dataAsAttribute)
        {
            using Stream s = data!.OpenRead();
            w.WriteAttribute(2, TnefAttributeIds.AttachData, s, data.GetByteLength());
        }

        if ((a.GetString(MapiPid.AttachLongFilename) ?? a.GetString(MapiPid.AttachFilename)) is { } title && Encode(enc, title) is { } tb)
        {
            w.WriteAttribute(2, TnefAttributeIds.AttachTitle, tb);
        }

        List<WireProp> props = Collect(a.Properties, warnings, where, skipObject: false, dropId: dataAsAttribute ? MapiPid.AttachDataBinary : null, embeddedPresent: a.EmbeddedMessage is not null);
        if (a.EmbeddedMessage is { } inner)
        {
            if (depth + 1 > o.MaxEmbeddingDepth)
            {
                warnings.Add(where + ": embedded message nested deeper than " + o.MaxEmbeddingDepth + " was not written");
            }
            else
            {
                using var nested = new MemoryStream();
                nested.Write(TnefMessageBuilder.MessageIid);
                WriteMessage(inner, nested, o, warnings, depth + 1, where + " > ");
                props.RemoveAll(p => p.Tag.Id == MapiPid.AttachDataBinary && p.Tag.PropertyType == MapiType.EmbeddedObject);
                props.Add(new WireProp(new MapiTag(MapiPid.AttachDataBinary, MapiType.EmbeddedObject), null, null, nested.ToArray()));
            }
        }
        else if (method == 6 && a.ObjectStorage is not null)
        {
            warnings.Add(where + ": the OLE object storage cannot be carried as a property value");
        }

        if (props.Count > 0)
        {
            w.BeginAttribute(2, TnefAttributeIds.Attachment, ListSize(props));
            WriteList(w, props);
            w.EndAttribute();
        }
    }

    private static byte[]? Encode(MailEncoding enc, string text)
    {
        var issues = new List<TextIssue>();
        byte[] b = enc.GetBytes(text, issues);
        return issues.Count > 0 ? null : NulTerminated(b);
    }

    private static byte[] NulTerminated(byte[] b)
    {
        byte[] r = new byte[b.Length + 1];
        b.CopyTo(r, 0);
        return r;
    }

    private static void WriteFrom(TnefWriter w, MapiMessage m, MailEncoding enc)
    {
        string? name = m.GetString(MapiPid.SenderName);
        string? type = m.GetString(MapiPid.SenderAddressType);
        string? address = m.GetString(MapiPid.SenderEmailAddress);
        if (name is null || type is null || address is null)
        {
            return;
        }

        byte[]? nb = Encode(enc, name);
        byte[]? eb = Encode(enc, type + ":" + address);
        if (nb is null || eb is null || 8 + nb.Length + eb.Length + 8 > ushort.MaxValue)
        {
            return;
        }

        // MS-OXTNEF 2.1.3.3.3: TRP header (04 00, structure length, name length, address length), the name, "TYPE:address", eight zero bytes.
        byte[] d = new byte[8 + nb.Length + eb.Length + 8];
        BinaryPrimitives.WriteUInt16LittleEndian(d, 4);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(2), (ushort)d.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(4), (ushort)nb.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(6), (ushort)eb.Length);
        nb.CopyTo(d, 8);
        eb.CopyTo(d, 8 + nb.Length);
        w.WriteAttribute(1, TnefAttributeIds.From, d);
    }

    private static void WriteDate(TnefWriter w, uint attribute, MapiObject o, ushort id)
    {
        if (o.Properties.Get(id, MapiType.Time)?.AsTime() is not { } t)
        {
            return;
        }

        DateTime u = t.UtcDateTime;
        if (u.Year is < 1601 or > 9999)
        {
            return;
        }

        Span<byte> d = stackalloc byte[14];
        BinaryPrimitives.WriteUInt16LittleEndian(d, (ushort)u.Year);
        BinaryPrimitives.WriteUInt16LittleEndian(d[2..], (ushort)u.Month);
        BinaryPrimitives.WriteUInt16LittleEndian(d[4..], (ushort)u.Day);
        BinaryPrimitives.WriteUInt16LittleEndian(d[6..], (ushort)u.Hour);
        BinaryPrimitives.WriteUInt16LittleEndian(d[8..], (ushort)u.Minute);
        BinaryPrimitives.WriteUInt16LittleEndian(d[10..], (ushort)u.Second);
        BinaryPrimitives.WriteUInt16LittleEndian(d[12..], (ushort)(int)u.DayOfWeek);
        w.WriteAttribute(1, attribute, d);
    }

    private static int FixedWireSize(ushort element) => element switch
    {
        0x01 => 0,
        0x02 or 0x0B => 2,
        0x03 or 0x04 or 0x0A => 4,
        0x05 or 0x06 or 0x07 or 0x14 or 0x40 => 8,
        0x48 => 16,
        _ => -1,
    };

    private static List<WireProp> Collect(MapiPropertyBag bag, List<string> warnings, string where, bool skipObject, ushort? dropId, bool embeddedPresent = false)
    {
        var list = new List<WireProp>(bag.Count);
        foreach (MapiProperty p in bag)
        {
            ushort type = p.Tag.Type;
            bool multi = MapiTypes.IsMultiValued(type);
            ushort element = MapiTypes.ElementType(type);
            string label = where + ": property " + p.Tag;
            if (dropId is ushort drop && p.Id == drop && element == Binary)
            {
                continue;
            }

            bool writable = type < 0x2000 && (FixedWireSize(element) >= 0 || element is String8 or Unicode or Binary or Object);
            if (multi && element is 0x01 or 0x0A or Boolean or Object)
            {
                writable = false;
            }

            if (!writable)
            {
                warnings.Add(label + " has a type MS-OXTNEF cannot carry");
                continue;
            }

            if (p.Tag.IsNamed && p.Name is null)
            {
                warnings.Add(label + " is a named property whose name is unknown");
                continue;
            }

            if (element == Object)
            {
                if (embeddedPresent && p.Id == MapiPid.AttachDataBinary)
                {
                    continue;
                }

                if (skipObject || p.GetByteLength() < 16)
                {
                    warnings.Add(label + " is an object with no value that can be written");
                    continue;
                }
            }

            list.Add(new WireProp(p.Tag, p.Tag.IsNamed ? p.Name : null, p, null));
        }

        return list;
    }

    private static long Padded(long n) => (n + 3) & ~3L;

    private static long ValueSize(MapiProperty p, int index, ushort element) =>
        element == Unicode ? p.GetByteLength(index) + 2 : element == String8 ? p.GetByteLength(index) + 1 : p.GetByteLength(index);

    private static long ListSize(List<WireProp> list)
    {
        long size = 4;
        foreach (WireProp wp in list)
        {
            size += 4;
            if (wp.Tag.IsNamed)
            {
                size += 16 + 4;
                size += wp.Name!.Value.IsString ? 4 + Padded(2L * wp.Name.Value.Name!.Length + 2) : 4;
            }

            ushort type = wp.Tag.Type;
            ushort element = MapiTypes.ElementType(type);
            bool multi = MapiTypes.IsMultiValued(type);
            int fixedSize = FixedWireSize(element);
            if (wp.ObjectBytes is { } ob)
            {
                size += 4 + 4 + Padded(ob.Length);
            }
            else if (fixedSize >= 0 && !multi)
            {
                size += Padded(fixedSize);
            }
            else if (fixedSize >= 0)
            {
                size += 4 + (long)wp.Prop!.ValueCount * Padded(fixedSize);
            }
            else if (!multi)
            {
                size += 4 + 4 + Padded(ValueSize(wp.Prop!, 0, element));
            }
            else
            {
                size += 4;
                for (int i = 0; i < wp.Prop!.ValueCount; i++)
                {
                    size += 4 + Padded(ValueSize(wp.Prop, i, element));
                }
            }
        }

        return size;
    }

    private static void WriteUInt32(TnefWriter w, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        w.WriteData(b);
    }

    private static void WritePad(TnefWriter w, long written)
    {
        long pad = Padded(written) - written;
        if (pad > 0)
        {
            w.WriteData(new byte[pad]);
        }
    }

    private static void WriteList(TnefWriter w, List<WireProp> list)
    {
        WriteUInt32(w, (uint)list.Count);
        Span<byte> tag = stackalloc byte[4];
        foreach (WireProp wp in list)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(tag, wp.Tag.Type);
            BinaryPrimitives.WriteUInt16LittleEndian(tag[2..], wp.Tag.Id);
            w.WriteData(tag);
            if (wp.Tag.IsNamed)
            {
                WriteName(w, wp.Name!.Value);
            }

            ushort type = wp.Tag.Type;
            ushort element = MapiTypes.ElementType(type);
            bool multi = MapiTypes.IsMultiValued(type);
            int fixedSize = FixedWireSize(element);
            if (wp.ObjectBytes is { } ob)
            {
                WriteUInt32(w, 1);
                WriteUInt32(w, (uint)ob.Length);
                w.WriteData(ob);
                WritePad(w, ob.Length);
            }
            else if (fixedSize >= 0 && !multi)
            {
                WriteFixed(w, wp.Prop!, 0, element, fixedSize);
            }
            else if (fixedSize >= 0)
            {
                WriteUInt32(w, (uint)wp.Prop!.ValueCount);
                for (int i = 0; i < wp.Prop.ValueCount; i++)
                {
                    WriteFixed(w, wp.Prop, i, element, fixedSize);
                }
            }
            else if (!multi)
            {
                WriteUInt32(w, 1);
                WriteVariable(w, wp.Prop!, 0, element);
            }
            else
            {
                WriteUInt32(w, (uint)wp.Prop!.ValueCount);
                for (int i = 0; i < wp.Prop.ValueCount; i++)
                {
                    WriteVariable(w, wp.Prop, i, element);
                }
            }
        }
    }

    private static void WriteName(TnefWriter w, MapiNamedProperty n)
    {
        Span<byte> g = stackalloc byte[16];
        n.PropertySet.TryWriteBytes(g);
        w.WriteData(g);
        if (n.IsString)
        {
            WriteUInt32(w, 1);
            byte[] chars = Encoding.Unicode.GetBytes(n.Name!);
            WriteUInt32(w, (uint)chars.Length + 2);
            w.WriteData(chars);
            w.WriteData(new byte[2]);
            WritePad(w, chars.Length + 2);
        }
        else
        {
            WriteUInt32(w, 0);
            WriteUInt32(w, n.Lid);
        }
    }

    private static void WriteFixed(TnefWriter w, MapiProperty p, int index, ushort element, int size)
    {
        // The bag holds a boolean as one byte; MS-OXTNEF 2.1.3.4 carries it in 16 bits, padded to 4 like every other scalar.
        ReadOnlySpan<byte> raw = p.GetBytes(index).Span;
        Span<byte> wire = stackalloc byte[16];
        wire.Clear();
        raw[..Math.Min(raw.Length, element == Boolean ? 1 : size)].CopyTo(wire);
        w.WriteData(wire[..size]);
        WritePad(w, size);
    }

    private static void WriteVariable(TnefWriter w, MapiProperty p, int index, ushort element)
    {
        long size = ValueSize(p, index, element);
        WriteUInt32(w, (uint)size);
        long raw = p.GetByteLength(index);
        if (raw > 0)
        {
            using Stream s = p.OpenRead(index);
            w.WriteData(s, raw);
        }

        if (element == Unicode)
        {
            w.WriteData(new byte[2]);
        }
        else if (element == String8)
        {
            w.WriteData(new byte[1]);
        }

        WritePad(w, size);
    }
}
