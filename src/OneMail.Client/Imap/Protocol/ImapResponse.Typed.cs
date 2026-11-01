using System.Globalization;
using RecursiveAI.OneMail.Client.Protocol;
using RecursiveAI.OneMail.Text;

namespace RecursiveAI.OneMail.Client.Imap.Protocol;

public sealed partial class ImapResponse
{
    /// <summary>Reads the capability list from an untagged <c>CAPABILITY</c> response, or from a <c>[CAPABILITY ...]</c> response code.</summary>
    /// <param name="capabilities">The atoms as written (<c>IMAP4rev1</c>, <c>AUTH=PLAIN</c>, <c>LITERAL+</c>).</param>
    /// <returns>True when this response carries a capability list.</returns>
    public bool TryGetCapabilities(out IReadOnlyList<string> capabilities)
    {
        IReadOnlyList<ImapValue>? source = null;
        if (Kind == ImapResponseKind.Untagged && Is("CAPABILITY") && Number is null)
        {
            source = Data;
        }
        else if (Code is { } code && code.Is("CAPABILITY"))
        {
            source = code.Arguments;
        }

        if (source is null)
        {
            capabilities = [];
            return false;
        }

        var list = new List<string>(source.Count);
        foreach (ImapValue v in source)
        {
            if (v.Text is { Length: > 0 } t)
            {
                list.Add(t);
            }
        }

        capabilities = list;
        return true;
    }

    /// <summary>Decodes an untagged <c>n FETCH (...)</c> response.</summary>
    /// <param name="fetch">The decoded response; unknown items are in <see cref="ImapFetchData.Other"/>.</param>
    /// <returns>True when this is a FETCH response with a list.</returns>
    public bool TryGetFetch(out ImapFetchData fetch)
    {
        fetch = null!;
        if (Kind != ImapResponseKind.Untagged || !Is("FETCH") || Number is not { } n || Data.Count == 0 || !Data[0].IsList)
        {
            return false;
        }

        fetch = ImapDecoders.Fetch(n, Data[0].Items);
        return true;
    }

    /// <summary>Decodes an untagged <c>LIST</c> or <c>LSUB</c> response.</summary>
    /// <param name="entry">The decoded entry.</param>
    /// <returns>True when this is such a response with the expected shape.</returns>
    public bool TryGetList(out ImapListEntry entry)
    {
        entry = null!;
        if (Kind != ImapResponseKind.Untagged || !(Is("LIST") || Is("LSUB")) || Data.Count < 3 || !Data[0].IsList)
        {
            return false;
        }

        var e = new ImapListEntry
        {
            Attributes = [.. Data[0].Items.Select(a => a.Text ?? "")],
            Delimiter = Data[1].Text,
            RawName = Data[2].Bytes,
            Name = ImapUtf7.Decode(Data[2].Bytes.Span),
            Extended = Data.Count > 3 ? [.. Data.Skip(3)] : [],
        };
        entry = e;
        return true;
    }

    /// <summary>Decodes an untagged <c>STATUS</c> response.</summary>
    /// <param name="status">The decoded response.</param>
    /// <returns>True when this is a STATUS response with the expected shape.</returns>
    public bool TryGetStatus(out ImapStatusData status)
    {
        status = null!;
        if (Kind != ImapResponseKind.Untagged || !Is("STATUS") || Data.Count < 2 || !Data[1].IsList)
        {
            return false;
        }

        var items = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<ImapValue> l = Data[1].Items;
        for (int i = 0; i + 1 < l.Count; i += 2)
        {
            if (l[i].Text is { } name && l[i + 1].TryGetNumber(out long v))
            {
                items[name.ToUpperInvariant()] = v;
            }
        }

        status = new ImapStatusData { Mailbox = ImapUtf7.Decode(Data[0].Bytes.Span), Items = items };
        return true;
    }

    /// <summary>Decodes an untagged <c>SEARCH</c> (or <c>SORT</c>) response: message numbers or UIDs.</summary>
    /// <param name="numbers">The numbers in order. A trailing <c>(MODSEQ n)</c> is ignored here and stays in <see cref="Data"/>.</param>
    /// <returns>True when this is a SEARCH or SORT response.</returns>
    public bool TryGetSearch(out IReadOnlyList<long> numbers)
    {
        numbers = [];
        if (Kind != ImapResponseKind.Untagged || !(Is("SEARCH") || Is("SORT")))
        {
            return false;
        }

        var list = new List<long>(Data.Count);
        foreach (ImapValue v in Data)
        {
            if (v.TryGetNumber(out long n))
            {
                list.Add(n);
            }
        }

        numbers = list;
        return true;
    }

    /// <summary>Decodes an untagged <c>ESEARCH</c> response.</summary>
    /// <param name="esearch">The decoded response.</param>
    /// <returns>True when this is an ESEARCH response.</returns>
    public bool TryGetEsearch(out ImapEsearchData esearch)
    {
        esearch = null!;
        if (Kind != ImapResponseKind.Untagged || !Is("ESEARCH"))
        {
            return false;
        }

        var e = new ImapEsearchData();
        var items = new List<KeyValuePair<string, ImapValue>>();
        int i = 0;
        if (i < Data.Count && Data[i].IsList)
        {
            // search-correlator: (TAG "A1" [MAILBOX ... UIDVALIDITY n])
            IReadOnlyList<ImapValue> c = Data[i].Items;
            for (int k = 0; k + 1 < c.Count; k += 2)
            {
                if (c[k].IsAtom("TAG"))
                {
                    e.Tag = c[k + 1].Text;
                }
            }

            i++;
        }

        if (i < Data.Count && Data[i].IsAtom("UID"))
        {
            e.IsUid = true;
            i++;
        }

        for (; i < Data.Count; i++)
        {
            if (Data[i].Text is { } name && Data[i].Kind == ImapValueKind.Atom)
            {
                if (i + 1 < Data.Count)
                {
                    items.Add(new KeyValuePair<string, ImapValue>(name.ToUpperInvariant(), Data[i + 1]));
                    i++;
                }
            }
        }

        e.Items = items;
        esearch = e;
        return true;
    }

    /// <summary>Decodes an untagged <c>NAMESPACE</c> response.</summary>
    /// <param name="ns">The decoded response.</param>
    /// <returns>True when this is a NAMESPACE response.</returns>
    public bool TryGetNamespace(out ImapNamespaceData ns)
    {
        ns = null!;
        if (Kind != ImapResponseKind.Untagged || !Is("NAMESPACE") || Data.Count < 3)
        {
            return false;
        }

        static IReadOnlyList<KeyValuePair<string, string?>> Group(ImapValue v)
        {
            var l = new List<KeyValuePair<string, string?>>();
            if (v.IsList)
            {
                foreach (ImapValue entry in v.Items)
                {
                    if (entry.IsList && entry.Items.Count >= 2)
                    {
                        l.Add(new KeyValuePair<string, string?>(ImapUtf7.Decode(entry.Items[0].Bytes.Span), entry.Items[1].Text));
                    }
                }
            }

            return l;
        }

        ns = new ImapNamespaceData { Personal = Group(Data[0]), OtherUsers = Group(Data[1]), Shared = Group(Data[2]) };
        return true;
    }

    /// <summary>Decodes an untagged <c>VANISHED</c> response.</summary>
    /// <param name="vanished">The decoded response.</param>
    /// <returns>True when this is a VANISHED response.</returns>
    public bool TryGetVanished(out ImapVanishedData vanished)
    {
        vanished = null!;
        if (Kind != ImapResponseKind.Untagged || !Is("VANISHED") || Data.Count == 0)
        {
            return false;
        }

        bool earlier = Data[0].IsList;
        int idx = earlier ? 1 : 0;
        vanished = new ImapVanishedData { Earlier = earlier, UidSet = idx < Data.Count ? Data[idx].Text ?? "" : "" };
        return true;
    }
}

internal static class ImapDecoders
{
    public static ImapFetchData Fetch(long number, IReadOnlyList<ImapValue> items)
    {
        var f = new ImapFetchData { SequenceNumber = number };
        var defects = new List<ProtocolDefect>();
        var sections = new List<ImapFetchSection>();
        var other = new List<KeyValuePair<string, ImapValue>>();
        for (int i = 0; i + 1 < items.Count; i += 2)
        {
            string name = items[i].Text ?? "";
            ImapValue v = items[i + 1];
            string upper = name.ToUpperInvariant();
            switch (upper)
            {
                case "UID":
                    if (v.TryGetNumber(out long uid)) { f.Uid = uid; } else { Bad(defects, name); }
                    break;
                case "RFC822.SIZE":
                    if (v.TryGetNumber(out long size)) { f.Rfc822Size = size; } else { Bad(defects, name); }
                    break;
                case "FLAGS":
                    if (v.IsList) { f.Flags = [.. v.Items.Select(x => x.Text ?? "")]; } else { Bad(defects, name); }
                    break;
                case "INTERNALDATE":
                    f.InternalDateText = v.Text;
                    f.InternalDate = ParseInternalDate(v.Text);
                    break;
                case "MODSEQ":
                    if (v.IsList && v.Items.Count == 1 && v.Items[0].TryGetNumber(out long ms)) { f.ModSeq = ms; } else { Bad(defects, name); }
                    break;
                case "EMAILID":
                    f.EmailId = v.IsList && v.Items.Count == 1 ? v.Items[0].Text : null;
                    if (f.EmailId is null) { Bad(defects, name); }
                    break;
                case "THREADID":
                    f.ThreadId = v.IsList && v.Items.Count == 1 ? v.Items[0].Text : null;
                    break;
                case "ENVELOPE":
                    f.Envelope = v.IsList ? Envelope(v.Items) : null;
                    if (f.Envelope is null) { Bad(defects, name); }
                    break;
                case "BODYSTRUCTURE":
                case "BODY" when v.IsList:
                    f.BodyStructure = v.IsList ? BodyStructure(v, "", 0) : null;
                    if (f.BodyStructure is null) { Bad(defects, name); }
                    break;
                case "RFC822":
                case "RFC822.HEADER":
                case "RFC822.TEXT":
                    sections.Add(new ImapFetchSection(upper, v));
                    break;
                default:
                    if (upper.StartsWith("BODY[", StringComparison.Ordinal) || upper.StartsWith("BINARY[", StringComparison.Ordinal)
                        || upper.StartsWith("BINARY.SIZE[", StringComparison.Ordinal))
                    {
                        sections.Add(new ImapFetchSection(name, v));
                    }
                    else
                    {
                        other.Add(new KeyValuePair<string, ImapValue>(name, v));
                    }

                    break;
            }
        }

        f.Sections = sections;
        f.Other = other;
        f.Defects = defects;
        return f;
    }

    private static void Bad(List<ProtocolDefect> defects, string item) =>
        defects.Add(new ProtocolDefect(ProtocolDefectKind.UnexpectedShape, 0, "FETCH item " + item + " has an unexpected value."));

    internal static DateTimeOffset? ParseInternalDate(string? text)
    {
        // date-time = DQUOTE date-day-fixed "-" date-month "-" date-year SP time SP zone DQUOTE (RFC 9051 §9); the day may be space-padded.
        if (text is null)
        {
            return null;
        }

        string t = text.Trim();
        return DateTimeOffset.TryParseExact(t, ["d-MMM-yyyy HH:mm:ss zzz", "d-MMM-yyyy HH:mm:ss zz", "dd-MMM-yyyy HH:mm:ss zzz"], CultureInfo.InvariantCulture,
            DateTimeStyles.None, out DateTimeOffset d) ? d : TryZ(t);
    }

    private static DateTimeOffset? TryZ(string t)
    {
        // The zone is "+hhmm" without a colon, which the "zzz" format wants; rebuild it.
        int sp = t.LastIndexOf(' ');
        if (sp < 0 || t.Length - sp != 6 || (t[sp + 1] != '+' && t[sp + 1] != '-'))
        {
            return null;
        }

        string fixedZone = t[..sp] + " " + t.Substring(sp + 1, 3) + ":" + t.Substring(sp + 4, 2);
        return DateTimeOffset.TryParseExact(fixedZone, ["d-MMM-yyyy HH:mm:ss zzz"], CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset d) ? d : null;
    }

    private static ImapEnvelope Envelope(IReadOnlyList<ImapValue> v)
    {
        ImapValue At(int i) => i < v.Count ? v[i] : ImapValue.Nil;
        return new ImapEnvelope
        {
            Date = At(0).Text,
            Subject = At(1).Text,
            From = Addresses(At(2)),
            Sender = Addresses(At(3)),
            ReplyTo = Addresses(At(4)),
            To = Addresses(At(5)),
            Cc = Addresses(At(6)),
            Bcc = Addresses(At(7)),
            InReplyTo = At(8).Text,
            MessageId = At(9).Text,
        };
    }

    private static List<ImapAddress> Addresses(ImapValue v)
    {
        var l = new List<ImapAddress>();
        if (v.IsList)
        {
            foreach (ImapValue a in v.Items)
            {
                if (a.IsList && a.Items.Count >= 4)
                {
                    l.Add(new ImapAddress(a.Items[0].Text, a.Items[1].Text, a.Items[2].Text, a.Items[3].Text));
                }
            }
        }

        return l;
    }

    // body = "(" (body-type-1part / body-type-mpart) ")"; a multipart starts with nested bodies (RFC 9051 §9).
    public static ImapBodyStructure BodyStructure(ImapValue value, string section, int depth)
    {
        IReadOnlyList<ImapValue> v = value.Items;
        var b = new ImapBodyStructure { Section = section };
        if (v.Count > 0 && v[0].IsList)
        {
            var parts = new List<ImapBodyStructure>();
            int i = 0;
            while (i < v.Count && v[i].IsList)
            {
                string childSection = section.Length == 0 ? (parts.Count + 1).ToString(CultureInfo.InvariantCulture) : section + "." + (parts.Count + 1).ToString(CultureInfo.InvariantCulture);
                parts.Add(BodyStructure(v[i], childSection, depth + 1));
                i++;
            }

            b.Type = "multipart";
            b.Subtype = (i < v.Count ? v[i].Text : null)?.ToLowerInvariant() ?? "";
            b.Parts = parts;
            i++;
            ReadExtension(b, v, i, multipart: true);
            return b;
        }

        // A single part that is the whole message has the section "1".
        if (section.Length == 0)
        {
            b.Section = "1";
        }

        b.Type = (v.Count > 0 ? v[0].Text : null)?.ToLowerInvariant() ?? "";
        b.Subtype = (v.Count > 1 ? v[1].Text : null)?.ToLowerInvariant() ?? "";
        b.Parameters = Params(v.Count > 2 ? v[2] : ImapValue.Nil);
        b.ContentId = v.Count > 3 ? v[3].Text : null;
        b.Description = v.Count > 4 ? v[4].Text : null;
        b.Encoding = v.Count > 5 ? v[5].Text : null;
        if (v.Count > 6 && v[6].TryGetNumber(out long size))
        {
            b.Size = size;
        }

        int next = 7;
        bool message = b.Type == "message" && (b.Subtype is "rfc822" or "global");
        if (message && v.Count > 9 && v[7].IsList && v[8].IsList)
        {
            b.Envelope = Envelope(v[7].Items);
            b.EmbeddedBody = BodyStructure(v[8], b.Section, depth + 1);
            if (v[9].TryGetNumber(out long ml)) { b.Lines = ml; }
            next = 10;
        }
        else if (b.Type == "text" && v.Count > 7)
        {
            if (v[7].TryGetNumber(out long lines)) { b.Lines = lines; }
            next = 8;
        }

        if (next < v.Count)
        {
            b.Md5 = v[next].Text;
        }

        ReadExtension(b, v, next + 1, multipart: false);
        return b;
    }

    // body-ext-1part after the MD5 / body-ext-mpart after the subtype: parameters (mpart only), disposition, language, location, more.
    private static void ReadExtension(ImapBodyStructure b, IReadOnlyList<ImapValue> v, int i, bool multipart)
    {
        if (multipart)
        {
            if (i < v.Count) { b.Parameters = Params(v[i]); }
            i++;
        }

        if (i < v.Count && v[i].IsList && v[i].Items.Count >= 1)
        {
            b.DispositionType = v[i].Items[0].Text?.ToLowerInvariant();
            b.DispositionParameters = Params(v[i].Items.Count > 1 ? v[i].Items[1] : ImapValue.Nil);
        }

        i++;
        if (i < v.Count)
        {
            b.Languages = v[i].IsList ? [.. v[i].Items.Select(x => x.Text ?? "")] : v[i].Text is { } one ? [one] : [];
        }

        i++;
        if (i < v.Count) { b.Location = v[i].Text; }
        i++;
        if (i < v.Count) { b.Extensions = [.. v.Skip(i)]; }
    }

    private static List<KeyValuePair<string, string>> Params(ImapValue v)
    {
        var l = new List<KeyValuePair<string, string>>();
        if (v.IsList)
        {
            for (int i = 0; i + 1 < v.Items.Count; i += 2)
            {
                l.Add(new KeyValuePair<string, string>((v.Items[i].Text ?? "").ToLowerInvariant(), v.Items[i + 1].Text ?? ""));
            }
        }

        return l;
    }
}
