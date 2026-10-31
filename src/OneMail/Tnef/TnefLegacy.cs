using System.Buffers.Binary;
using System.Text;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Text;
using Context = RecursiveAI.OneMail.Tnef.TnefMessageBuilder.Context;

namespace RecursiveAI.OneMail.Tnef;

/// <summary>Maps the legacy message-level attributes of MS-OXTNEF 2.1.3.3 onto properties the encapsulated list does not carry.</summary>
internal static class MessageLegacy
{
    // MS-OXTNEF 2.3.3.4: legacy class names and the class each stands for.
    private static readonly (string Legacy, string Modern)[] ClassNames =
    [
        ("IPM.Microsoft Mail.Note", "IPM.Note"),
        ("IPM.Microsoft Mail.Read Receipt", "Report.IPM.Note.IPNRN"),
        ("IPM.Microsoft Mail.Non-Delivery", "Report.IPM.Note.NDR"),
        ("IPM.Microsoft Schedule.MtgRespP", "IPM.Schedule.Meeting.Resp.Pos"),
        ("IPM.Microsoft Schedule.MtgRespN", "IPM.Schedule.Meeting.Resp.Neg"),
        ("IPM.Microsoft Schedule.MtgRespA", "IPM.Schedule.Meeting.Resp.Tent"),
        ("IPM.Microsoft Schedule.MtgReq", "IPM.Schedule.Meeting.Request"),
        ("IPM.Microsoft Schedule.MtgCncl", "IPM.Schedule.Meeting.Canceled"),
    ];

    internal static string MapClass(string value, out bool changed)
    {
        string v = value;
        const string Prefix = "Microsoft Mail v3.0 ";
        if (v.StartsWith(Prefix, StringComparison.Ordinal))
        {
            v = v[Prefix.Length..];
        }

        // The specification's own example for the prefix spells the legacy name with dots; both spellings are accepted.
        string probe = v.StartsWith("IPM.Microsoft.Mail.", StringComparison.OrdinalIgnoreCase) ? "IPM.Microsoft Mail." + v["IPM.Microsoft.Mail.".Length..] : v;
        foreach ((string legacy, string modern) in ClassNames)
        {
            if (probe.Equals(legacy, StringComparison.OrdinalIgnoreCase))
            {
                changed = true;
                return modern;
            }
        }

        changed = !ReferenceEquals(v, value);
        return v;
    }

    internal static string ReverseClass(string modern)
    {
        foreach ((string legacy, string m) in ClassNames)
        {
            if (modern.Equals(m, StringComparison.Ordinal))
            {
                return legacy;
            }
        }

        return modern;
    }

    public static void Apply(Context cx, Dictionary<ushort, TnefAttributeRecord> legacy, MailEncoding? oemEncoding)
    {
        var l = new Legacy(cx.Message, cx, oemEncoding ?? MailEncoding.Windows1252);

        if (legacy.TryGetValue(0x8008, out TnefAttributeRecord? cls))
        {
            string mapped = MapClass(l.Text(cls), out bool changed);
            if (changed)
            {
                cx.Add(TnefDefectKind.AmbiguousMessageClass, cls.Offset, "legacy class '" + l.Text(cls) + "' read as '" + mapped + "'");
            }

            l.SetText(MapiPid.MessageClass, mapped, cls);
        }

        if (legacy.TryGetValue(0x0600, out TnefAttributeRecord? ocls))
        {
            string mapped = MapClass(l.Text(ocls), out bool changed);
            if (changed)
            {
                cx.Add(TnefDefectKind.AmbiguousMessageClass, ocls.Offset, "legacy original class '" + l.Text(ocls) + "' read as '" + mapped + "'");
            }

            l.SetText(MapiPid.OriginalMessageClass, mapped, ocls);
        }

        foreach ((ushort id, TnefAttributeRecord a) in legacy)
        {
            switch (id)
            {
                case 0x8004:
                    l.SetText(MapiPid.Subject, l.Text(a), a);
                    break;
                case 0x800C:
                    l.SetText(MapiPid.Body, l.Text(a), a);
                    break;
                case 0x8005:
                    l.SetDate(MapiPid.ClientSubmitTime, a);
                    break;
                case 0x8006:
                    l.SetDate(MapiPid.MessageDeliveryTime, a);
                    break;
                case 0x8020:
                    l.SetDate(MapiPid.LastModificationTime, a);
                    break;
                case 0x0006:
                    l.SetDate(MapiPid.StartDate, a);
                    break;
                case 0x0007:
                    l.SetDate(MapiPid.EndDate, a);
                    break;
                case 0x8007:
                    l.SetStatus(a);
                    break;
                case 0x8009:
                    l.SetMessageId(a);
                    break;
                case 0x800D:
                    l.SetPriority(a);
                    break;
                case 0x8000:
                    l.SetFrom(a);
                    break;
                case 0x0000:
                    l.SetOwner(a, owner: true);
                    break;
                case 0x0001:
                    l.SetOwner(a, owner: false);
                    break;
                case 0x0002:
                    if (l.Missing(MapiPid.ReceivedRepresentingEntryId))
                    {
                        l.AddBinary(MapiPid.ReceivedRepresentingEntryId, a.ReadData());
                    }

                    break;
                case 0x0008:
                    if (l.Missing(MapiPid.OwnerAppointmentId) && l.Fixed(a, 4) is { } aid)
                    {
                        l.AddInt32(MapiPid.OwnerAppointmentId, BinaryPrimitives.ReadInt32LittleEndian(aid));
                    }

                    break;
                case 0x0009:
                    // MS-OXTNEF 2.1.3.3.20: an INT16 whose low byte is the PtypBoolean value.
                    if (l.Missing(MapiPid.ResponseRequested) && l.Fixed(a, 2) is { } rr)
                    {
                        l.Add(new MapiTag(MapiPid.ResponseRequested, MapiType.Boolean), [(byte)(rr[0] != 0 ? 1 : 0)]);
                    }

                    break;
            }
        }
    }

    internal sealed class Legacy
    {
        private readonly MapiObject _o;
        private readonly Context _cx;
        private readonly MailEncoding _enc;

        public Legacy(MapiObject o, Context cx, MailEncoding enc)
        {
            _o = o;
            _cx = cx;
            _enc = enc;
        }

        public bool Missing(ushort id) => _o.Properties.Get(id) is null;

        public void Add(MapiTag tag, byte[] raw) => _o.Properties.Add(MapiProperty.CreateOwned(tag, raw));

        public void AddInt32(ushort id, int v) => Add(new MapiTag(id, MapiType.Integer32), BitConverter.GetBytes(v));

        public void AddBinary(ushort id, byte[] v) => Add(new MapiTag(id, MapiType.Binary), v);

        public void AddUnicode(ushort id, string v) => Add(new MapiTag(id, MapiType.UnicodeString), Encoding.Unicode.GetBytes(v));

        public string Text(TnefAttributeRecord a)
        {
            byte[] d = a.ReadData();
            int n = d.Length;
            if (n > 0 && d[n - 1] == 0)
            {
                n--;
            }

            return _enc.GetString(d.AsSpan(0, n));
        }

        public byte[]? Fixed(TnefAttributeRecord a, int minimum)
        {
            byte[] d = a.ReadData();
            if (d.Length >= minimum)
            {
                return d;
            }

            _cx.Add(TnefDefectKind.AttributeMalformed, a.Offset, "attribute 0x" + a.Id.ToString("X8") + " has " + d.Length + " bytes, expected " + minimum);
            return null;
        }

        public void SetText(ushort id, string text, TnefAttributeRecord a)
        {
            if (_o.Properties.Get(id) is { } existing)
            {
                string? have = existing.AsString(_o.String8Encoding);
                if (have is not null && !string.Equals(have, text, StringComparison.Ordinal))
                {
                    _cx.Add(TnefDefectKind.AmbiguousLegacyConflict, a.Offset, "attribute 0x" + a.Id.ToString("X8") + " differs from the encapsulated property; the property is used");
                }

                return;
            }

            AddUnicode(id, text);
        }

        public void SetDate(ushort id, TnefAttributeRecord a)
        {
            byte[]? d = Fixed(a, 14);
            if (d is null)
            {
                return;
            }

            // MS-OXTNEF 2.1.3.3.4: wYear wMonth wDay wHour wMinute wSecond wDayOfWeek, each 16 bits. The time zone is not stated; UTC is assumed, as PtypTime is.
            try
            {
                var t = new DateTime(BinaryPrimitives.ReadUInt16LittleEndian(d), BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(2)), BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(4)),
                    BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(6)), BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(8)), BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(10)), DateTimeKind.Utc);
                byte[] ft = BitConverter.GetBytes(t.ToFileTimeUtc());
                if (_o.Properties.Get(id, MapiType.Time) is { } existing)
                {
                    if (!existing.GetBytes().Span.SequenceEqual(ft) && existing.AsTime() is { } have && Math.Abs((have - new DateTimeOffset(t)).TotalSeconds) >= 2)
                    {
                        _cx.Add(TnefDefectKind.AmbiguousLegacyConflict, a.Offset, "date attribute 0x" + a.Id.ToString("X8") + " differs from the encapsulated property; the property is used");
                    }

                    return;
                }

                if (Missing(id))
                {
                    Add(new MapiTag(id, MapiType.Time), ft);
                }
            }
            catch (ArgumentOutOfRangeException)
            {
                _cx.Add(TnefDefectKind.InvalidDate, a.Offset, "date attribute 0x" + a.Id.ToString("X8") + " holds an impossible date");
            }
        }

        public void SetStatus(TnefAttributeRecord a)
        {
            byte[] d = a.ReadData();
            if (d.Length == 0)
            {
                _cx.Add(TnefDefectKind.AttributeMalformed, a.Offset, "empty attMessageStatus");
                return;
            }

            // MS-OXTNEF 2.1.3.3.8: fmsRead 0x20 -> MSGFLAG_READ 0x01, fmsModified 0x01 -> MSGFLAG_UNMODIFIED 0x02 inverted, fmsSubmitted 0x04 -> 0x04,
            // fmsLocal 0x02 -> MSGFLAG_UNSENT 0x08, fmsHasAttach 0x80 -> MSGFLAG_HASATTACH 0x10.
            uint s = d.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(d) : d[0];
            int flags = 0;
            flags |= (s & 0x20) != 0 ? 0x01 : 0;
            flags |= (s & 0x01) == 0 ? 0x02 : 0;
            flags |= (s & 0x04) != 0 ? 0x04 : 0;
            flags |= (s & 0x02) != 0 ? 0x08 : 0;
            flags |= (s & 0x80) != 0 ? 0x10 : 0;
            if (Missing(MapiPid.MessageFlags))
            {
                AddInt32(MapiPid.MessageFlags, flags);
            }
        }

        public void SetMessageId(TnefAttributeRecord a)
        {
            // MS-OXTNEF 2.2.3.7 / 2.3.3.5: the binary search key is written as two hexadecimal characters per byte.
            string t = Text(a);
            if (t.Length % 2 != 0 || t.Length == 0)
            {
                _cx.Add(TnefDefectKind.AttributeMalformed, a.Offset, "attMessageID is not an even number of hexadecimal digits");
                return;
            }

            byte[] bytes = new byte[t.Length / 2];
            if (Convert.FromHexString(t.AsSpan(), bytes, out _, out _) != System.Buffers.OperationStatus.Done)
            {
                _cx.Add(TnefDefectKind.AttributeMalformed, a.Offset, "attMessageID is not hexadecimal");
                return;
            }

            if (Missing(MapiPid.SearchKey))
            {
                AddBinary(MapiPid.SearchKey, bytes);
            }
        }

        public void SetPriority(TnefAttributeRecord a)
        {
            if (Fixed(a, 2) is not { } d)
            {
                return;
            }

            // MS-OXTNEF 2.1.3.3.10: TNEF 3/2/1 (low/normal/high) is PidTagImportance 0/1/2.
            int? importance = BinaryPrimitives.ReadUInt16LittleEndian(d) switch { 3 => 0, 2 => 1, 1 => 2, _ => null };
            if (importance is null)
            {
                _cx.Add(TnefDefectKind.AttributeMalformed, a.Offset, "attPriority value " + BinaryPrimitives.ReadUInt16LittleEndian(d) + " is not 1, 2 or 3");
                return;
            }

            if (_o.Properties.Get(MapiPid.Importance, MapiType.Integer32)?.AsInt32() is int have)
            {
                if (have != importance)
                {
                    _cx.Add(TnefDefectKind.AmbiguousLegacyConflict, a.Offset, "attPriority differs from PidTagImportance; the property is used");
                }

                return;
            }

            if (Missing(MapiPid.Importance))
            {
                AddInt32(MapiPid.Importance, importance.Value);
            }
        }

        // Splits "TYPE:address" at the first colon (MS-OXTNEF 2.1.3.3.3).
        private static (string Type, string Address) SplitAddress(string s)
        {
            int c = s.IndexOf(':', StringComparison.Ordinal);
            return c < 0 ? ("", s) : (s[..c], s[(c + 1)..]);
        }

        private void SetGroup(ushort nameId, ushort typeId, ushort addressId, ushort entryId, string name, string addrType, string address)
        {
            if (Missing(nameId))
            {
                AddUnicode(nameId, name);
            }

            if (Missing(typeId))
            {
                AddUnicode(typeId, addrType);
            }

            if (Missing(addressId))
            {
                AddUnicode(addressId, address);
            }

            if (Missing(entryId))
            {
                AddBinary(entryId, OneOffEntryId(name, addrType, address));
            }
        }

        public void SetFrom(TnefAttributeRecord a)
        {
            byte[] d = a.ReadData();
            // TRP-structure: trpidOneOff (04 00), structure length, name length, address length, the name, "TYPE:address", eight zero bytes.
            if (d.Length < 8 || BinaryPrimitives.ReadUInt16LittleEndian(d) != 4)
            {
                _cx.Add(TnefDefectKind.AttributeMalformed, a.Offset, "attFrom is not a one-off TRP structure");
                return;
            }

            int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(4));
            int addrLen = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(6));
            if (8 + nameLen + addrLen > d.Length)
            {
                _cx.Add(TnefDefectKind.AttributeMalformed, a.Offset, "attFrom string lengths run past the attribute");
                return;
            }

            string name = NulTerminated(d.AsSpan(8, nameLen));
            (string type, string address) = SplitAddress(NulTerminated(d.AsSpan(8 + nameLen, addrLen)));
            SetGroup(MapiPid.SenderName, MapiPid.SenderAddressType, MapiPid.SenderEmailAddress, MapiPid.SenderEntryId, name, type, address);
        }

        public void SetOwner(TnefAttributeRecord a, bool owner)
        {
            byte[] d = a.ReadData();
            // attOwner and attSentFor: display-name length, the name, address length, "TYPE:address" (MS-OXTNEF 2.1.3.3.16), lengths counting the terminators.
            if (d.Length < 2 || (BinaryPrimitives.ReadUInt16LittleEndian(d) + 4) > d.Length)
            {
                _cx.Add(TnefDefectKind.AttributeMalformed, a.Offset, "attribute 0x" + a.Id.ToString("X8") + " is too short for its name");
                return;
            }

            int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(d);
            int addrLen = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(2 + nameLen));
            if (4 + nameLen + addrLen > d.Length)
            {
                _cx.Add(TnefDefectKind.AttributeMalformed, a.Offset, "attribute 0x" + a.Id.ToString("X8") + " address runs past the attribute");
                return;
            }

            string name = NulTerminated(d.AsSpan(2, nameLen));
            (string type, string address) = SplitAddress(NulTerminated(d.AsSpan(4 + nameLen, addrLen)));

            // MS-OXTNEF 2.3.3.10: a meeting request or cancellation puts the owner in PidTagSentRepresenting, a response in PidTagReceivedRepresenting;
            // attSentFor is always SentRepresenting (2.3.3.11).
            bool received = false;
            if (owner)
            {
                string c = _o is MapiMessage m ? m.MessageClass ?? "" : "";
                bool request = c.StartsWith("IPM.Schedule.Meeting.Request", StringComparison.OrdinalIgnoreCase) || c.StartsWith("IPM.Schedule.Meeting.Canceled", StringComparison.OrdinalIgnoreCase);
                received = c.StartsWith("IPM.Schedule.Meeting.Resp", StringComparison.OrdinalIgnoreCase);
                if (!request && !received)
                {
                    _cx.Add(TnefDefectKind.AmbiguousOwner, a.Offset, "attOwner in a message of class '" + c + "'; mapped to PidTagSentRepresenting");
                }
            }

            if (received)
            {
                SetGroup(MapiPid.ReceivedRepresentingName, MapiPid.ReceivedRepresentingAddressType, MapiPid.ReceivedRepresentingEmailAddress, MapiPid.ReceivedRepresentingEntryId, name, type, address);
            }
            else
            {
                SetGroup(MapiPid.SentRepresentingName, MapiPid.SentRepresentingAddressType, MapiPid.SentRepresentingEmailAddress, MapiPid.SentRepresentingEntryId, name, type, address);
            }
        }

        private string NulTerminated(ReadOnlySpan<byte> s)
        {
            int n = s.IndexOf((byte)0);
            return _enc.GetString(n < 0 ? s : s[..n]);
        }

        // MS-OXCDATA 2.2.5.1: a one-off entry ID is four flag bytes, the provider UID, a version, option flags (bit 15: strings are Unicode) and three strings.
        private static byte[] OneOffEntryId(string name, string type, string address)
        {
            using var ms = new MemoryStream();
            ms.Write(new byte[4]);
            ms.Write([0x81, 0x2B, 0x1F, 0xA4, 0xBE, 0xA3, 0x10, 0x19, 0x9D, 0x6E, 0x00, 0xDD, 0x01, 0x0F, 0x54, 0x02]);
            ms.Write([0x00, 0x00, 0x00, 0x80]);
            foreach (string s in new[] { name, type, address })
            {
                ms.Write(Encoding.Unicode.GetBytes(s));
                ms.Write([0, 0]);
            }

            return ms.ToArray();
        }
    }
}

/// <summary>Maps the legacy attachment-level attributes onto properties the attAttachment list does not carry.</summary>
internal static class AttachmentLegacy
{
    public static void Apply(Context cx, TnefAttachment att, Dictionary<ushort, TnefAttributeRecord> legacy, MailEncoding? oemEncoding, bool embedded)
    {
        var l = new MessageLegacy.Legacy(att, cx, oemEncoding ?? MailEncoding.Windows1252);
        int renderType = 1;
        if (legacy.TryGetValue(0x9002, out TnefAttributeRecord? rend))
        {
            byte[]? d = l.Fixed(rend, 14);
            if (d is not null)
            {
                // MS-OXTNEF 2.1.3.3.15: AttachType (2), AttachPosition (4), RenderWidth (2), RenderHeight (2), DataFlags (4).
                renderType = BinaryPrimitives.ReadUInt16LittleEndian(d);
                if (l.Missing(MapiPid.RenderingPosition))
                {
                    l.AddInt32(MapiPid.RenderingPosition, BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(2)));
                }

                // MS-OXTNEF 2.3.3.9: an OLE attachment gets the OLE tag; MacBinary data gets the MacBinary encoding; plain files get neither.
                if (renderType == 2 && l.Missing(MapiPid.AttachTag))
                {
                    l.AddBinary(MapiPid.AttachTag, [0x2A, 0x86, 0x48, 0x86, 0xF7, 0x14, 0x03, 0x0A, 0x03, 0x02, 0x01]);
                }

                if ((BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(10)) & 1) != 0 && l.Missing(MapiPid.AttachEncoding))
                {
                    l.AddBinary(MapiPid.AttachEncoding, [0x2A, 0x86, 0x48, 0x86, 0xF7, 0x14, 0x03, 0x0B, 0x01]);
                }
            }
        }

        int? method = att.Properties.Get(MapiPid.AttachMethod, MapiType.Integer32)?.AsInt32();
        bool hasData = false;
        if (legacy.TryGetValue(0x800F, out TnefAttributeRecord? data))
        {
            if (embedded || method is 5 or 6)
            {
                cx.Add(TnefDefectKind.AmbiguousAttachDataIgnored, data.Offset, "attAttachData ignored because the attachment is an embedded message or OLE object");
            }
            else if (l.Missing(MapiPid.AttachDataBinary))
            {
                att.Properties.Add(MapiProperty.CreateLazy(new MapiTag(MapiPid.AttachDataBinary, MapiType.Binary), new TnefValueSource(data.Source, data.DataOffset, data.AvailableLength)));
                hasData = true;
            }
        }

        if (legacy.TryGetValue(0x8010, out TnefAttributeRecord? title) && l.Missing(MapiPid.AttachLongFilename))
        {
            l.AddUnicode(MapiPid.AttachLongFilename, l.Text(title));
        }

        if (legacy.TryGetValue(0x8011, out TnefAttributeRecord? meta) && l.Missing(MapiPid.AttachRendering))
        {
            att.Properties.Add(MapiProperty.CreateLazy(new MapiTag(MapiPid.AttachRendering, MapiType.Binary), new TnefValueSource(meta.Source, meta.DataOffset, meta.AvailableLength)));
        }

        if (legacy.TryGetValue(0x8012, out TnefAttributeRecord? created))
        {
            l.SetDate(MapiPid.CreationTime, created);
        }

        if (legacy.TryGetValue(0x8013, out TnefAttributeRecord? modified))
        {
            l.SetDate(MapiPid.LastModificationTime, modified);
        }

        if (legacy.TryGetValue(0x9001, out TnefAttributeRecord? transport) && l.Missing(MapiPid.AttachTransportName))
        {
            l.AddUnicode(MapiPid.AttachTransportName, l.Text(transport));
        }

        // A legacy attachment states how its data is held only through which attributes it has; ATTACH_BY_VALUE is what attAttachData means (MS-OXTNEF 2.2.3.8).
        if (method is null)
        {
            int? inferred = embedded ? 5 : renderType == 2 ? 6 : hasData ? 1 : null;
            if (inferred is int m)
            {
                l.AddInt32(MapiPid.AttachMethod, m);
            }
        }
    }
}
