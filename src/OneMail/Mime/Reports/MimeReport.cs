using System.Text;
using RecursiveAI.OneMail.Mime.Reading;
using RecursiveAI.OneMail.Mime.Transfer;

namespace RecursiveAI.OneMail.Mime.Reports;

/// <summary>The kind of report a <c>multipart/report</c> carries, from its <c>report-type</c> (RFC 6522 §3).</summary>
public enum ReportKind
{
    /// <summary>The report type is absent or not one this library reads. The parts are still reachable.</summary>
    Unknown = 0,

    /// <summary>A delivery status notification, <c>report-type=delivery-status</c> (RFC 3464).</summary>
    DeliveryStatus,

    /// <summary>A message disposition notification, <c>report-type=disposition-notification</c> (RFC 8098).</summary>
    DispositionNotification,

    /// <summary>An abuse feedback report, <c>report-type=feedback-report</c> (RFC 5965).</summary>
    Feedback,
}

/// <summary>
/// A view of a <c>multipart/report</c> (RFC 6522, earlier RFC 3462): a human-readable part, a machine-readable part named by the
/// <c>report-type</c> parameter, and optionally the original message or its headers. It reads from the tree on demand and changes nothing.
/// </summary>
/// <remarks>
/// The machine-readable part is not looked up by position alone: when <c>report-type</c> is missing or disagrees with the second
/// part, the part's own media type decides the kind, and <see cref="MimeDefectKind.ReportStructureInvalid"/> is reported.
/// </remarks>
public sealed class MimeReport
{
    private readonly List<MimeDefect> _defects = [];
    private MimeEntity? _headersOnly;

    private MimeReport(MimeMultipart multipart)
    {
        Multipart = multipart;
        ReportType = multipart.GetContentTypeParameter("report-type")?.Trim().ToLowerInvariant();
        IReadOnlyList<MimeEntity> parts = multipart.Children;
        int depth = multipart.Depth;
        long at = Math.Max(0, multipart.SourceStart);
        HumanReadable = parts.Count > 0 ? parts[0] : null;
        MachinePart = parts.Count > 1 ? parts[1] as MimePart : null;
        Original = parts.Count > 2 ? parts[2] : null;

        // RFC 6522 §3: the second part is the machine-readable report named by report-type; two parts at least.
        Kind = KindOfType(ReportType);
        ReportKind byPart = MachinePart is null ? ReportKind.Unknown : KindOfMedia(MachinePart.MediaType);
        if (MachinePart is null || ReportType is null || (Kind != ReportKind.Unknown && byPart != Kind))
        {
            _defects.Add(new MimeDefect(MimeDefectKind.ReportStructureInvalid, at, 0, depth));
        }

        if (byPart != ReportKind.Unknown)
        {
            Kind = byPart;
        }
    }

    /// <summary>The <c>multipart/report</c> entity.</summary>
    public MimeMultipart Multipart { get; }

    /// <summary>The <c>report-type</c> parameter in lower case (<c>delivery-status</c>, <c>disposition-notification</c>, <c>feedback-report</c> or another), or null.</summary>
    public string? ReportType { get; }

    /// <summary>The kind of report.</summary>
    public ReportKind Kind { get; private set; }

    /// <summary>The first part: the explanation for people, usually <c>text/plain</c>. Null when the multipart is empty.</summary>
    public MimeEntity? HumanReadable { get; }

    /// <summary>The second part: <c>message/delivery-status</c>, <c>message/disposition-notification</c>, <c>message/feedback-report</c> (or the <c>global</c> forms of RFC 6533). Null when absent or not a leaf.</summary>
    public MimePart? MachinePart { get; }

    /// <summary>The third part, if any: <c>message/rfc822</c>, <c>message/global</c>, <c>text/rfc822-headers</c> or <c>message/global-headers</c>.</summary>
    public MimeEntity? Original { get; }

    /// <summary>Structure problems found when the view was made. Offsets are in the parsed input.</summary>
    public IReadOnlyList<MimeDefect> Defects => _defects;

    /// <summary>
    /// The original message the report is about, when the report carries it: the embedded message of a <c>message/rfc822</c> part, or
    /// the headers of a <c>text/rfc822-headers</c> part parsed as a message with no body (check <see cref="OriginalIsHeadersOnly"/>).
    /// Null when there is no third part, or it is neither.
    /// </summary>
    public MimeEntity? OriginalMessage
    {
        get
        {
            switch (Original)
            {
                case MimeMessagePart mp:
                    return mp.Message;
                case MimePart p when IsHeadersMedia(p.MediaType):
                    return _headersOnly ??= MimeEntity.Parse(p.GetDecodedContent());
                default:
                    return null;
            }
        }
    }

    /// <summary>True when the original is only its headers (<c>text/rfc822-headers</c> or <c>message/global-headers</c>).</summary>
    public bool OriginalIsHeadersOnly => Original is MimePart p && IsHeadersMedia(p.MediaType);

    /// <summary>Makes a view of a <c>multipart/report</c>.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns>The view, or null when the entity is not a <c>multipart/report</c>.</returns>
    public static MimeReport? TryCreate(MimeEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return entity is MimeMultipart m && m.MediaType == "multipart/report" ? new MimeReport(m) : null;
    }

    /// <summary>Finds the first <c>multipart/report</c> in a message, in document order (a bounce is usually the message itself).</summary>
    /// <param name="tree">The message or any entity.</param>
    /// <returns>The view, or null when there is none.</returns>
    public static MimeReport? Find(MimeEntity tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        if (TryCreate(tree) is { } r)
        {
            return r;
        }

        foreach (MimeEntity c in tree.ChildEntities())
        {
            if (Find(c) is { } f)
            {
                return f;
            }
        }

        return null;
    }

    /// <summary>The delivery status of the machine-readable part, or null when the report is of another kind.</summary>
    /// <param name="defects">Receives the report's defects; may be null.</param>
    /// <returns>The status.</returns>
    public DeliveryStatusReport? GetDeliveryStatus(List<MimeDefect>? defects = null) =>
        Kind == ReportKind.DeliveryStatus && MachinePart is not null ? DeliveryStatusReport.From(MachinePart, defects) : null;

    /// <summary>The disposition notification of the machine-readable part, or null when the report is of another kind.</summary>
    /// <param name="defects">Receives the notification's defects; may be null.</param>
    /// <returns>The notification.</returns>
    public DispositionNotification? GetDispositionNotification(List<MimeDefect>? defects = null) =>
        Kind == ReportKind.DispositionNotification && MachinePart is not null ? DispositionNotification.From(MachinePart, defects) : null;

    /// <summary>The feedback report of the machine-readable part, or null when the report is of another kind.</summary>
    /// <param name="defects">Receives the report's defects; may be null.</param>
    /// <returns>The report.</returns>
    public FeedbackReport? GetFeedbackReport(List<MimeDefect>? defects = null) =>
        Kind == ReportKind.Feedback && MachinePart is not null ? FeedbackReport.From(MachinePart, defects) : null;

    private static bool IsHeadersMedia(string media) => media is "text/rfc822-headers" or "message/global-headers";

    private static ReportKind KindOfType(string? type) => type switch
    {
        "delivery-status" => ReportKind.DeliveryStatus,
        "disposition-notification" => ReportKind.DispositionNotification,
        "feedback-report" => ReportKind.Feedback,
        _ => ReportKind.Unknown,
    };

    private static ReportKind KindOfMedia(string media) => media switch
    {
        "message/delivery-status" or "message/global-delivery-status" => ReportKind.DeliveryStatus,
        "message/disposition-notification" or "message/global-disposition-notification" => ReportKind.DispositionNotification,
        "message/feedback-report" => ReportKind.Feedback,
        _ => ReportKind.Unknown,
    };

    // ---- writing ----

    /// <summary>Builds a delivery status notification (RFC 3464, RFC 6522).</summary>
    /// <param name="humanReadable">The explanation for people; the first part, <c>text/plain; charset=utf-8</c>.</param>
    /// <param name="status">The machine-readable status. Non-ASCII fields make the part <c>message/global-delivery-status</c> (RFC 6533).</param>
    /// <param name="originalMessage">The message the report is about, as bytes; empty for none.</param>
    /// <param name="originalHeadersOnly">When true the third part holds only the original's headers (<c>text/rfc822-headers</c>) instead of the whole message.</param>
    /// <param name="boundary">The multipart boundary; null generates a random one.</param>
    /// <returns>A new <c>multipart/report</c> entity.</returns>
    public static MimeMultipart CreateDeliveryStatus(string humanReadable, DeliveryStatusReport status, ReadOnlyMemory<byte> originalMessage = default, bool originalHeadersOnly = false, string? boundary = null)
    {
        ArgumentNullException.ThrowIfNull(status);
        return Build("delivery-status", status.RequiresGlobal ? "message/global-delivery-status" : "message/delivery-status", humanReadable, status.ToBytes(), originalMessage, originalHeadersOnly, boundary);
    }

    /// <summary>Builds a message disposition notification (RFC 8098, RFC 6522).</summary>
    /// <param name="humanReadable">The explanation for people.</param>
    /// <param name="notification">The machine-readable notification.</param>
    /// <param name="originalMessage">The message the report is about, as bytes; empty for none. RFC 8098 §3 says the original is optional and often headers only.</param>
    /// <param name="originalHeadersOnly">When true the third part holds only the original's headers.</param>
    /// <param name="boundary">The multipart boundary; null generates a random one.</param>
    /// <returns>A new <c>multipart/report</c> entity.</returns>
    public static MimeMultipart CreateDispositionNotification(string humanReadable, DispositionNotification notification, ReadOnlyMemory<byte> originalMessage = default, bool originalHeadersOnly = false, string? boundary = null)
    {
        ArgumentNullException.ThrowIfNull(notification);
        return Build("disposition-notification", notification.Fields.HasNonAscii ? "message/global-disposition-notification" : "message/disposition-notification", humanReadable, notification.ToBytes(), originalMessage, originalHeadersOnly, boundary);
    }

    /// <summary>Builds an abuse feedback report (RFC 5965, RFC 6522).</summary>
    /// <param name="humanReadable">The explanation for people.</param>
    /// <param name="report">The machine-readable report.</param>
    /// <param name="originalMessage">The reported message as bytes; empty for none. RFC 5965 §2 puts it in <c>message/rfc822</c> or <c>text/rfc822-headers</c>.</param>
    /// <param name="originalHeadersOnly">When true the third part holds only the original's headers.</param>
    /// <param name="boundary">The multipart boundary; null generates a random one.</param>
    /// <returns>A new <c>multipart/report</c> entity.</returns>
    public static MimeMultipart CreateFeedbackReport(string humanReadable, FeedbackReport report, ReadOnlyMemory<byte> originalMessage = default, bool originalHeadersOnly = false, string? boundary = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        return Build("feedback-report", "message/feedback-report", humanReadable, report.ToBytes(), originalMessage, originalHeadersOnly, boundary);
    }

    private static MimeMultipart Build(string reportType, string machineMedia, string humanReadable, byte[] machine, ReadOnlyMemory<byte> original, bool headersOnly, string? boundary)
    {
        ArgumentNullException.ThrowIfNull(humanReadable);
        var m = new MimeMultipart("report; report-type=" + reportType, boundary);

        var text = new MimePart("text/plain; charset=utf-8");
        byte[] textBytes = Encoding.UTF8.GetBytes(humanReadable);
        text.SetDecodedContent(textBytes, Ascii.IsValid(textBytes) ? ContentTransferEncoding.SevenBit : ContentTransferEncoding.EightBit);
        m.Add(text);

        var mp = new MimePart(machineMedia);
        mp.SetDecodedContent(machine, Ascii.IsValid(machine) ? ContentTransferEncoding.SevenBit : ContentTransferEncoding.EightBit);
        m.Add(mp);

        if (!original.IsEmpty)
        {
            MimeEntity parsed = MimeEntity.Parse(original);
            if (headersOnly)
            {
                var hp = new MimePart("text/rfc822-headers");
                using var ms = new MemoryStream();
                foreach (MimeHeader h in parsed.Headers)
                {
                    ms.Write(h.Raw.Span);
                }

                byte[] hb = ms.ToArray();
                hp.SetDecodedContent(hb, Ascii.IsValid(hb) ? ContentTransferEncoding.SevenBit : ContentTransferEncoding.EightBit);
                m.Add(hp);
            }
            else
            {
                m.Add(new MimeMessagePart { Message = parsed });
            }
        }

        return m;
    }
}
