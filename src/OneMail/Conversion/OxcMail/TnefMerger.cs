using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Text;
using RecursiveAI.OneMail.Tnef;

namespace RecursiveAI.OneMail.Conversion.OxcMail;

/// <summary>
/// Merges a TNEF part into the message built from the MIME headers and body, as MS-OXCMAIL 2.2 and 2.2.3.8 say. What the MIME side already
/// set wins ("the version found in MIME format SHOULD be preferred"), except that a richer body in the TNEF is kept next to the plain text
/// ("the plain text rendering found in MIME messages SHOULD NOT be used in preference to a richer rendering found in TNEF messages").
/// Every property of the TNEF that is set aside is in the loss report; every other TNEF property is on the message exactly as the TNEF reader gave it.
/// </summary>
internal static class TnefMerger
{
    // 2.2.3.8 step 2.1 speaks of attributes that "would result in setting a property ... already set by the processing of the headers". The
    // properties below come from one header field each (or one address), so setting some of them from the TNEF and the rest from the headers would
    // mix two senders or two subjects. A group is therefore taken whole from the headers when any of its properties is there.
    private static readonly ushort[][] Groups =
    [
        [MapiPid.Subject, MapiPid.SubjectPrefix, MapiPid.NormalizedSubject],
        [MapiPid.SentRepresentingName, MapiPid.SentRepresentingAddressType, MapiPid.SentRepresentingEmailAddress, MapiPid.SentRepresentingSmtpAddress,
            MapiPid.SentRepresentingEntryId, MapiPid.SentRepresentingSearchKey],
        [MapiPid.SenderName, MapiPid.SenderAddressType, MapiPid.SenderEmailAddress, MapiPid.SenderSmtpAddress, MapiPid.SenderEntryId, MapiPid.SenderSearchKey],
        [MapiPid.ReplyRecipientEntries, MapiPid.ReplyRecipientNames],
    ];

    /// <summary>Merges one TNEF message into <paramref name="m"/>; the TNEF's attachments are added by the caller where the part stood.</summary>
    /// <param name="m">The message built from the MIME side.</param>
    /// <param name="t">The TNEF message of the part.</param>
    /// <param name="report">The conversion report.</param>
    /// <param name="idFor">Gives the local property ID of a named property of <paramref name="m"/>.</param>
    /// <param name="path">The report path of <paramref name="m"/>.</param>
    public static void Merge(MapiMessage m, TnefMessage t, LossReport report, Func<MapiNamedProperty, ushort> idFor, string path)
    {
        MapiPropertyBag bag = m.Properties;

        // 8-bit strings of the TNEF keep the code page the TNEF reader read them with.
        MailEncoding tnefEncoding = t.String8Encoding;
        bool sameEncoding = true;
        if (m.DeclaredString8Encoding is null)
        {
            m.DeclaredString8Encoding = tnefEncoding;
        }
        else
        {
            sameEncoding = m.DeclaredString8Encoding.CodePage == tnefEncoding.CodePage;
        }

        var groupSetByHeaders = new bool[Groups.Length];
        for (int g = 0; g < Groups.Length; g++)
        {
            foreach (ushort id in Groups[g])
            {
                if (bag.Get(id) is not null)
                {
                    groupSetByHeaders[g] = true;
                }
            }
        }

        bool mimeHadHtml = bag.Get(MapiPid.Html) is not null;
        foreach (MapiProperty p in t.Properties)
        {
            string label = Label(p);
            if (p.Name is null && p.Id >= 0x8000)
            {
                report.Add("mapi", label, LossKind.Dropped, "TnefPropertyWithoutName", "A named property of the TNEF has no name in its mapping, so it cannot be told apart from the message's own named properties.", path);
                continue;
            }

            if (p.Id is MapiPid.InternetCodepage or MapiPid.NativeBody)
            {
                // Derived from the bodies the merged message ends up with; set below.
                continue;
            }

            MapiProperty? existing = p.Name is { } named ? bag.Get(named) : bag.Get(p.Id);

            if (p.Id == MapiPid.MessageClass && existing is not null)
            {
                MergeMessageClass(m, p, existing, report, path);
                continue;
            }

            int group = GroupOf(p.Id);
            if (existing is null && group >= 0 && groupSetByHeaders[group])
            {
                // The group is the headers'; this member has no counterpart there but would mix the two sources.
                report.Add("mapi", label, LossKind.Overridden, "TnefPropertyIgnored", "The MIME headers set other properties of the same group (subject, sender or reply-to); the group is taken from the headers whole (MS-OXCMAIL 2.2.3.8).", path);
                continue;
            }

            if (existing is not null)
            {
                if (SameValue(existing, p))
                {
                    continue;
                }

                report.Add("mapi", label, LossKind.Overridden, "TnefPropertyIgnored", "The MIME side already set this property and MS-OXCMAIL 2.2.3.8 ignores the TNEF's value.", path);
                continue;
            }

            MapiProperty toAdd = p;
            if (p.Name is { } np)
            {
                ushort id = idFor(np);
                if (id != p.Id)
                {
                    toAdd = Retag(p, id);
                }
            }

            if (!sameEncoding && p.Type == MapiType.String8 && p.ValueCount == 1)
            {
                string text = p.AsString(tnefEncoding) ?? string.Empty;
                toAdd = p.Name is { } n2 ? MapiProps.NamedStr(toAdd.Id, n2, text) : MapiProps.Str(p.Id, text);
                report.Add("mapi", label, LossKind.Approximated, "TnefCodePageDiffers", "An earlier TNEF part declared another code page; this 8-bit string is stored as Unicode.", path);
            }
            else if (!sameEncoding && p.Type is MapiType.String8 or MapiType.MultipleString8)
            {
                report.Add("mapi", label, LossKind.Approximated, "TnefCodePageDiffers", "An earlier TNEF part declared another code page; this 8-bit string is read with that one.", path);
            }

            bag.Add(toAdd);
        }

        // The merged message's body bookkeeping follows the bodies it has: the code page names how HTML bytes are read, so the TNEF's applies
        // unless the HTML is the MIME side's.
        if (!mimeHadHtml && t.Properties.Get(MapiPid.InternetCodepage, MapiType.Integer32) is { } cp)
        {
            bag.Set(cp);
        }

        // PidTagNativeBody says which body the message was written in. The TNEF carries the author's value (the MIME text part is only a
        // rendering), so it stands unless the HTML is the MIME side's; then, as without a TNEF, it is the richest body the message has.
        if (!mimeHadHtml && t.Properties.Get(MapiPid.NativeBody, MapiType.Integer32) is { } native)
        {
            bag.Set(native);
        }
        else if (bag.Get(MapiPid.Html) is not null || bag.Get(MapiPid.RtfCompressed) is not null || bag.Get(MapiPid.Body) is not null)
        {
            bag.Set(MapiProps.Int(MapiPid.NativeBody, bag.Get(MapiPid.Html) is not null ? 3 : bag.Get(MapiPid.RtfCompressed) is not null ? 2 : 1));
        }
    }

    // The converter gives every message IPM.Note unless the MIME structure says otherwise (a report, S/MIME). A class the TNEF carries
    // (a meeting request, a task) is what the sender's client wrote, so it replaces the default; it never replaces a class the structure gave.
    private static void MergeMessageClass(MapiMessage m, MapiProperty tnefClass, MapiProperty existing, LossReport report, string path)
    {
        string? mime = existing.AsString(m.String8Encoding);
        string? fromTnef = tnefClass.AsString(m.String8Encoding);
        if (string.Equals(mime, fromTnef, StringComparison.Ordinal) || fromTnef is null)
        {
            return;
        }

        if (mime == "IPM.Note" && fromTnef.StartsWith("IPM.", StringComparison.OrdinalIgnoreCase))
        {
            m.Properties.Set(MapiProps.Str(MapiPid.MessageClass, fromTnef));
            report.Add("mime", "message class", LossKind.Overridden, "TnefMessageClassUsed", "The MIME structure gave only the default class IPM.Note; the class the TNEF carries (" + fromTnef + ") replaces it.", path);
            return;
        }

        report.Add("mapi", "PidTagMessageClass", LossKind.Overridden, "TnefPropertyIgnored", "The MIME structure gave the class " + mime + "; the TNEF's class " + fromTnef + " is ignored.", path);
    }

    /// <summary>
    /// Adds the TNEF's recipients when the MIME headers gave none. When they gave some, the TNEF's table is ignored (2.2.3.8 step 2.1) and,
    /// if it names addresses the headers do not, that is reported.
    /// </summary>
    public static void MergeRecipients(MapiMessage m, TnefMessage t, LossReport report, string path)
    {
        IReadOnlyList<MapiRecipient> tnefRecipients = t.Recipients;
        if (tnefRecipients.Count == 0)
        {
            return;
        }

        if (m.Recipients.Count == 0)
        {
            foreach (MapiRecipient r in tnefRecipients)
            {
                m.AddRecipient(r);
            }

            return;
        }

        var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (MapiRecipient r in m.Recipients)
        {
            have.Add(r.SmtpAddress ?? r.EmailAddress ?? r.DisplayName ?? string.Empty);
        }

        foreach (MapiRecipient r in tnefRecipients)
        {
            string key = r.SmtpAddress ?? r.EmailAddress ?? r.DisplayName ?? string.Empty;
            if (!have.Contains(key))
            {
                report.Add("mapi", "recipient table", LossKind.Overridden, "TnefRecipientIgnored", "The MIME headers gave the recipients; the TNEF recipient " + key + " is not among them and is ignored (MS-OXCMAIL 2.2.3.8).", path);
            }
        }
    }

    private static int GroupOf(ushort id)
    {
        for (int g = 0; g < Groups.Length; g++)
        {
            if (Array.IndexOf(Groups[g], id) >= 0)
            {
                return g;
            }
        }

        return -1;
    }

    // Same type and bytes; a 8-bit string and the same text in Unicode count as the same value.
    private static bool SameValue(MapiProperty a, MapiProperty b)
    {
        if (a.Type == b.Type && a.ValueCount == b.ValueCount)
        {
            for (int i = 0; i < a.ValueCount; i++)
            {
                if (!a.GetBytes(i).Span.SequenceEqual(b.GetBytes(i).Span))
                {
                    return false;
                }
            }

            return true;
        }

        return a.Type is MapiType.UnicodeString or MapiType.String8 && b.Type is MapiType.UnicodeString or MapiType.String8
            && string.Equals(a.AsString(), b.AsString(), StringComparison.Ordinal);
    }

    private static string Label(MapiProperty p) => p.Name is { } n ? n.ToString() : MapiPropertyNames.GetName(p.Id) ?? p.Tag.ToString();

    // The same property under another local ID: named properties are numbered per message, and the TNEF's numbers can mean other names here.
    private static MapiProperty Retag(MapiProperty p, ushort id) =>
        MapiProperty.CreateLazy(new MapiTag(id, p.Type), new Forward(p), p.Name);

    private sealed class Forward(MapiProperty inner) : MapiValueSource
    {
        public override int Count => inner.ValueCount;

        public override long GetLength(int index) => inner.GetByteLength(index);

        public override byte[] Read(int index) => inner.GetBytes(index).ToArray();

        public override Stream OpenRead(int index) => inner.OpenRead(index);
    }
}
