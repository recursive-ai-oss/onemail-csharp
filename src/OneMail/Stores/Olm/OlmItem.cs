using System.Globalization;
using System.Text;
using RecursiveAI.OneMail.Recovery;
using RecursiveAI.OneArchive;

namespace RecursiveAI.OneMail.Stores.Olm;

/// <summary>
/// One record of an OLM archive: a message, appointment, contact, task, note, group or category, read through the shared view
/// (<see cref="IMailItem"/>) with the whole XML record (<see cref="Element"/>) kept beside it, so every element the view does not map is still reachable.
/// </summary>
/// <remarks>
/// <para>The view is built on demand from the record. Dates in the files carry no zone: they are read as UTC and flagged as having no known zone
/// (<see cref="OlmDefectKind.AmbiguousDateZone"/>). Message bodies are the XML's own text (a Unicode string), presented as UTF-8; they are not the original
/// MIME bytes unless the record carries a raw source (<see cref="MimeSource"/>).</para>
/// <para>An item is valid until its <see cref="OlmArchive"/> is disposed.</para>
/// </remarks>
public sealed class OlmItem : IMailItem
{
    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd",
    ];

    private readonly OlmArchive _archive;
    private readonly MailProvenance _provenance;
    private List<OlmDefect>? _defects;
    private List<OlmAttachment>? _attachments;

    internal OlmItem(OlmArchive archive, OlmItemKind kind, OlmElement element, string entry, string folder, bool inMailTree, MailProvenance provenance, int indexInEntry)
    {
        _archive = archive;
        OlmKind = kind;
        Element = element;
        Entry = entry;
        Folder = folder;
        IsMailFolder = inMailTree;
        _provenance = provenance;
        IndexInEntry = indexInEntry;
    }

    /// <summary>What the record is.</summary>
    public OlmItemKind OlmKind { get; }

    /// <summary>The whole XML record (<c>email</c>, <c>appointment</c> …): every element and attribute, in document order. Nothing is dropped.</summary>
    public OlmElement Element { get; }

    /// <summary>The zip entry the record was read from.</summary>
    public string Entry { get; }

    /// <summary>The record's folder, relative to the archive's roots (<c>Outlook PST/Inbox</c>, <c>Calendar</c>, <c>Address Book</c>); empty for the archive root (<c>Categories.xml</c>).</summary>
    public string Folder { get; }

    /// <summary>True when <see cref="Folder"/> is in the message tree.</summary>
    public bool IsMailFolder { get; }

    /// <summary>The record's position among the records of its entry.</summary>
    public int IndexInEntry { get; }

    /// <summary>
    /// Recovery mode only (<see cref="OlmReaderOptions.Recovery"/>): <see cref="RecoveryStatus.Live"/> when the zip entry holding the record, and the
    /// attachment entries it lists, read clean; <see cref="RecoveryStatus.Partial"/> when one is damaged or cut short, or an attachment is missing from a
    /// damaged archive. Null when recovery is off. Evaluated on first use: it reads the record's entry and its attachments once to check them.
    /// </summary>
    public RecoveryProvenance? Recovery
    {
        get
        {
            if (_recovery is null)
            {
                Attached();
                bool missing = _defects?.Exists(d => d.Kind == OlmDefectKind.AttachmentMissing) ?? false;
                _recovery = _archive.RecoveryOf(Entry, _attachments!.Select(a => a.EntryName).OfType<string>(), missing);
            }

            return _recovery;
        }
    }

    private RecoveryProvenance? _recovery;

    /// <summary>The raw MIME source, when the record carries one (<c>OPFMessageCopySource</c>, written by older Outlook for Mac versions), exactly as the XML text decodes; otherwise null.</summary>
    public string? MimeSource => OlmKind == OlmItemKind.Email ? Text("OPFMessageCopySource") : null;

    /// <summary>The record's top-level elements that neither the shared view nor the calendar and vCard conversions read. They stay available through <see cref="Element"/>.</summary>
    /// <returns>The elements, in document order.</returns>
    public IReadOnlyList<OlmElement> UnmappedElements() => OlmMapping.Unmapped(this);

    /// <summary>Categories named by the record's category list (names only; colours are in <see cref="Element"/>).</summary>
    public IReadOnlyList<string> Categories
    {
        get
        {
            var list = new List<string>();
            string listName = OlmMapping.CategoryListName(OlmKind);
            if (Element.Child(listName) is { } l)
            {
                foreach (OlmElement c in l.Children)
                {
                    if (c.Name == "category" && c.Attribute("OPFCategoryCopyName") is { Length: > 0 } n)
                    {
                        list.Add(n);
                    }
                }
            }

            return list;
        }
    }

    /// <summary>Defects found in this item so far: dates and numbers that did not parse, attachments with no entry. Reading the view fills it in.</summary>
    public IReadOnlyList<OlmDefect> OlmDefects
    {
        get
        {
            Analyze();
            return _defects!;
        }
    }

    /// <inheritdoc />
    public MailItemKind Kind => OlmKind switch
    {
        OlmItemKind.Email => MailItemKind.Message,
        OlmItemKind.Appointment => MailItemKind.Appointment,
        OlmItemKind.Contact => MailItemKind.Contact,
        OlmItemKind.Task => MailItemKind.Task,
        OlmItemKind.Note => MailItemKind.Note,
        OlmItemKind.Group => MailItemKind.DistributionList,
        _ => MailItemKind.Other,
    };

    /// <inheritdoc />
    public string? Subject => OlmKind switch
    {
        OlmItemKind.Email => Text("OPFMessageCopySubject"),
        OlmItemKind.Appointment => Text("OPFCalendarEventCopySummary"),
        OlmItemKind.Contact => Text("OPFContactCopyDisplayName") ?? ContactName(),
        OlmItemKind.Task => Text("OPFTaskCopyName"),
        OlmItemKind.Note => Text("OPFNoteCopyTitle"),
        OlmItemKind.Group => Text("OPFGroupCopyDisplayName"),
        _ => Element.Attribute("OPFCategoryCopyName"),
    };

    /// <inheritdoc />
    public MailAddress? Sender => OlmKind == OlmItemKind.Email ? Addresses("OPFMessageCopySenderAddress").FirstOrNull() : null;

    /// <inheritdoc />
    public IReadOnlyList<MailAddress> From => OlmKind == OlmItemKind.Email ? Addresses("OPFMessageCopyFromAddresses") : [];

    /// <inheritdoc />
    public IReadOnlyList<MailAddress> ToRecipients => OlmKind switch
    {
        OlmItemKind.Email => Addresses("OPFMessageCopyToAddresses"),
        OlmItemKind.Appointment => Attendees(),
        _ => [],
    };

    /// <inheritdoc />
    public IReadOnlyList<MailAddress> Cc => OlmKind == OlmItemKind.Email ? Addresses("OPFMessageCopyCCAddresses") : [];

    /// <inheritdoc />
    public IReadOnlyList<MailAddress> Bcc => OlmKind == OlmItemKind.Email ? Addresses("OPFMessageCopyBCCAddresses") : [];

    /// <inheritdoc />
    public IReadOnlyList<MailAddress> ReplyTo => OlmKind == OlmItemKind.Email ? Addresses("OPFMessageCopyReplyToAddresses") : [];

    /// <inheritdoc />
    public MailDate? Sent => OlmKind == OlmItemKind.Email && Date("OPFMessageCopySentTime", MailDateSource.ClientSubmit) is { } d ? d : null;

    /// <inheritdoc />
    public bool TryGetDate(MailDateSource source, out MailDate value)
    {
        MailDate? d = null;
        if (OlmKind == OlmItemKind.Email)
        {
            d = source switch
            {
                MailDateSource.ClientSubmit => Date("OPFMessageCopySentTime", source),
                MailDateSource.Delivery => Date("OPFMessageCopyReceivedTime", source),
                _ => null,
            };
        }
        else if (OlmKind == OlmItemKind.Note && source == MailDateSource.Creation)
        {
            d = Date("OPFNoteCopyCreationDate", source);
        }

        value = d.GetValueOrDefault();
        return d is not null;
    }

    /// <inheritdoc />
    public string? MessageId => OlmKind == OlmItemKind.Email ? Ids(Text("OPFMessageCopyMessageID")).FirstOrDefault() : null;

    /// <inheritdoc />
    public IReadOnlyList<string> InReplyTo => OlmKind == OlmItemKind.Email ? Ids(Text("OPFMessageCopyInReplyTo")) : [];

    /// <inheritdoc />
    public IReadOnlyList<string> References => OlmKind == OlmItemKind.Email ? Ids(Text("OPFMessageCopyReferences")) : [];

    /// <inheritdoc />
    public ReadOnlyMemory<byte> ConversationIndex
    {
        get
        {
            // PidTagConversationIndex travels as base64 in the XML.
            string? t = OlmKind == OlmItemKind.Email ? Text("OPFMessageCopyThreadIndex") : null;
            if (string.IsNullOrWhiteSpace(t))
            {
                return default;
            }

            byte[] buf = new byte[t.Length * 3 / 4 + 3];
            return Convert.TryFromBase64String(t.Trim(), buf, out int n) ? buf.AsMemory(0, n) : default;
        }
    }

    /// <inheritdoc />
    public string? ConversationTopic => OlmKind == OlmItemKind.Email ? Text("OPFMessageCopyThreadTopic") : null;

    /// <inheritdoc />
    public MailBody? TextBody => OlmKind switch
    {
        OlmItemKind.Email => EmailBodies().Text,
        OlmItemKind.Appointment => Body("OPFCalendarEventCopyDescriptionPlain", "text/plain"),
        OlmItemKind.Contact => Body("OPFContactCopyNotesPlain", "text/plain"),
        OlmItemKind.Task => Body("OPFTaskCopyNotePlain", "text/plain"),
        _ => null,
    };

    /// <inheritdoc />
    public MailBody? HtmlBody => OlmKind switch
    {
        OlmItemKind.Email => EmailBodies().Html,
        OlmItemKind.Appointment => Body("OPFCalendarEventCopyDescription", "text/html"),
        OlmItemKind.Contact => Body("OPFContactCopyNotes", "text/html"),
        OlmItemKind.Task => Body("OPFTaskCopyNote", "text/html"),
        OlmItemKind.Note => Body("OPFNoteCopyText", "text/html"),
        _ => null,
    };

    /// <inheritdoc />
    public MailBody? RtfBody => null;

    /// <inheritdoc />
    public IReadOnlyList<IMailAttachment> Attachments
    {
        get
        {
            Attached();
            return _attachments!;
        }
    }

    /// <inheritdoc />
    public IEnumerable<MailDefect> Defects
    {
        get
        {
            long offset = _provenance.Offset;
            foreach (OlmDefect d in OlmDefects)
            {
                yield return new MailDefect("olm", d.Kind.ToString(), offset, d.IsAmbiguity);
            }
        }
    }

    /// <inheritdoc />
    public MailProvenance Provenance => _provenance;

    internal OlmArchive Archive => _archive;

    internal string? Text(string name) => Element.ChildText(name);

    private string? ContactName()
    {
        string?[] parts = [Text("OPFContactCopyFirstName"), Text("OPFContactCopyMiddleName"), Text("OPFContactCopyLastName")];
        string joined = string.Join(' ', parts.Where(p => !string.IsNullOrEmpty(p)));
        return joined.Length == 0 ? null : joined;
    }

    // Dates in the files are 2018-06-20T23:49:52 with no zone; clients write them in UTC, so that is the reading (an ambiguity, noted on the item).
    private MailDate? Date(string name, MailDateSource source)
    {
        string? t = Text(name);
        if (string.IsNullOrWhiteSpace(t))
        {
            return null;
        }

        if (!TryParseDate(t, out DateTimeOffset v))
        {
            Note(OlmDefectKind.BadDate, name);
            return null;
        }

        Note(OlmDefectKind.AmbiguousDateZone, string.Empty);
        return new MailDate(v, source, ZoneKnown: false);
    }

    internal static bool TryParseDate(string text, out DateTimeOffset value) =>
        DateTimeOffset.TryParseExact(text.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);

    // The OLM writes whole numbers and flags as Java-style floats: 1E0, 5.0614E4. A plain integer is read the same way.
    internal static bool TryParseNumber(string? text, out double value)
    {
        value = 0;
        return !string.IsNullOrWhiteSpace(text) && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    internal bool Flag(string name, bool fallback = false)
    {
        string? t = Text(name);
        if (t is null)
        {
            return fallback;
        }

        if (TryParseNumber(t, out double d))
        {
            return d != 0;
        }

        if (bool.TryParse(t.Trim(), out bool b))
        {
            return b;
        }

        Note(OlmDefectKind.BadNumber, name);
        return fallback;
    }

    private static List<string> Ids(string? text)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return list;
        }

        foreach (string part in text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string p = part.Trim('<', '>');
            if (p.Length > 0)
            {
                list.Add(p);
            }
        }

        return list;
    }

    private List<MailAddress> Addresses(string listName)
    {
        var list = new List<MailAddress>();
        if (Element.Child(listName) is not { } l)
        {
            return list;
        }

        foreach (OlmElement a in l.Children)
        {
            if (a.Name != "emailAddress")
            {
                continue;
            }

            string? addr = a.Attribute("OPFContactEmailAddressAddress");
            string? name = a.Attribute("OPFContactEmailAddressName");
            if (string.IsNullOrEmpty(addr))
            {
                Note(OlmDefectKind.AddressMissing, listName);
            }

            if (string.IsNullOrEmpty(addr) && string.IsNullOrEmpty(name))
            {
                continue;
            }

            list.Add(new MailAddress(string.IsNullOrEmpty(name) ? null : name, addr ?? string.Empty));
        }

        return list;
    }

    private List<MailAddress> Attendees()
    {
        var list = new List<MailAddress>();
        if (Element.Child("OPFCalendarEventCopyAttendeeList") is not { } l)
        {
            return list;
        }

        foreach (OlmElement a in l.Children)
        {
            string? addr = a.Attribute("OPFCalendarAttendeeAddress");
            string? name = a.Attribute("OPFCalendarAttendeeName");
            if (!string.IsNullOrEmpty(addr) || !string.IsNullOrEmpty(name))
            {
                list.Add(new MailAddress(string.IsNullOrEmpty(name) ? null : name, addr ?? string.Empty));
            }
        }

        return list;
    }

    private MemoryMailBody? Body(string element, string contentType)
    {
        string? t = Text(element);
        return string.IsNullOrEmpty(t) ? null : new MemoryMailBody(contentType, "utf-8", Encoding.UTF8.GetBytes(t));
    }

    // Outlook for Mac writes the same text into both body elements. Its HTML flag says which kind the message is.
    private (MailBody? Text, MailBody? Html) EmailBodies()
    {
        string? body = Text("OPFMessageCopyBody");
        string? html = Text("OPFMessageCopyHTMLBody");
        bool isHtml;
        if (Element.Child("OPFMessageGetHasHTML") is not null)
        {
            isHtml = Flag("OPFMessageGetHasHTML");
        }
        else
        {
            Note(OlmDefectKind.AmbiguousBodyKind, "OPFMessageGetHasHTML");
            isHtml = !string.IsNullOrEmpty(html) && html != body;
        }

        if (isHtml)
        {
            string? h = string.IsNullOrEmpty(html) ? body : html;
            return (null, string.IsNullOrEmpty(h) ? null : new MemoryMailBody("text/html", "utf-8", Encoding.UTF8.GetBytes(h)));
        }

        string? t = string.IsNullOrEmpty(body) ? html : body;
        return (string.IsNullOrEmpty(t) ? null : new MemoryMailBody("text/plain", "utf-8", Encoding.UTF8.GetBytes(t)), null);
    }

    private void Attached()
    {
        if (_attachments is not null)
        {
            return;
        }

        _attachments = [];
        (string list, string item) = OlmKind switch
        {
            OlmItemKind.Email => ("OPFMessageCopyAttachmentList", "messageAttachment"),
            OlmItemKind.Appointment => ("OPFCalendarEventCopyAttachmentList", "appointmentAttachment"),
            _ => (string.Empty, string.Empty),
        };
        if (list.Length == 0 || Element.Child(list) is not { } l)
        {
            return;
        }

        foreach (OlmElement a in l.Children)
        {
            if (a.Name != item)
            {
                continue;
            }

            string? url = a.Attribute("OPFAttachmentURL");
            ArchiveEntry? entry = null;
            if (url is not null && _archive.TryGetAttachmentEntry(url, out ArchiveEntry? found))
            {
                entry = found;
            }
            else
            {
                Note(OlmDefectKind.AttachmentMissing, url ?? "(no URL)");
            }

            _attachments.Add(new OlmAttachment(this, a, entry));
        }
    }

    private void Note(OlmDefectKind kind, string detail)
    {
        _defects ??= [];
        foreach (OlmDefect d in _defects)
        {
            if (d.Kind == kind && d.Detail == detail)
            {
                return;
            }
        }

        _defects.Add(new OlmDefect(kind, Entry, detail));
    }

    // Touches everything the view reads, so Defects is complete whatever the caller looked at.
    private void Analyze()
    {
        if (_analysed)
        {
            return;
        }

        _analysed = true;
        _defects ??= [];
        _ = Sent;
        _ = TryGetDate(MailDateSource.Delivery, out _);
        _ = TryGetDate(MailDateSource.Creation, out _);
        _ = TextBody;
        _ = From;
        _ = ToRecipients;
        _ = Cc;
        _ = Bcc;
        _ = ReplyTo;
        _ = Sender;
        Attached();
    }

    private bool _analysed;
}

/// <summary>An attachment of an OLM record: the element that describes it and the zip entry that holds its bytes (read lazily).</summary>
internal sealed class OlmAttachment(OlmItem owner, OlmElement element, ArchiveEntry? entry) : IMailAttachment
{
    public string? FileName => element.Attribute("OPFAttachmentName");

    public string ContentType => ContentTypeInferred ? AttachmentMediaTypes.Infer(FileName)! : StoredType is { Length: > 0 } t ? t : "application/octet-stream";

    public bool ContentTypeInferred => AttachmentMediaTypes.IsGeneric(StoredType) && AttachmentMediaTypes.Infer(FileName) is not null;

    private string? StoredType => element.Attribute("OPFAttachmentContentType");

    public string? ContentId => element.Attribute("OPFAttachmentContentID") is { Length: > 0 } c ? c.Trim('<', '>') : null;

    public bool IsInline => ContentId is not null;

    public string? EntryName => entry?.Name;

    public long Size => entry?.Size ?? 0;

    public IMailItem? Embedded => null;

    public Stream Open()
    {
        if (entry is null)
        {
            return Stream.Null;
        }

        return owner.Archive.OpenEntry(entry, out _) ?? Stream.Null;
    }
}

internal static class AddressListExtensions
{
    public static MailAddress? FirstOrNull(this List<MailAddress> list) => list.Count > 0 ? list[0] : null;
}
