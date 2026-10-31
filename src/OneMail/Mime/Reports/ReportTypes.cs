using System.Globalization;
using System.Net;
using System.Text;
using RecursiveAI.OneMail.Mime.Reading;

namespace RecursiveAI.OneMail.Mime.Reports;

/// <summary>A value of the form <c>type; value</c> used by the report fields: <c>rfc822; user@example.com</c>, <c>dns; mx.example.com</c>, <c>smtp; 550 mailbox unavailable</c>.</summary>
/// <param name="Type">The type before the semicolon, lower case, trimmed (RFC 3464 §2.1.2: address-type, mta-type and diagnostic-type are case-insensitive).</param>
/// <param name="Value">The text after the semicolon, trimmed.</param>
public readonly record struct DsnTypedValue(string Type, string Value)
{
    /// <summary>Splits <c>type; value</c> at the first semicolon.</summary>
    /// <param name="text">The unfolded field value, or null.</param>
    /// <param name="value">The result. When there is no semicolon the type is empty and the value is the whole text.</param>
    /// <returns>True when the text had a non-empty type and a semicolon.</returns>
    public static bool TryParse(string? text, out DsnTypedValue value)
    {
        value = default;
        if (text is null)
        {
            return false;
        }

        int semi = text.IndexOf(';', StringComparison.Ordinal);
        if (semi < 0)
        {
            value = new DsnTypedValue(string.Empty, text.Trim());
            return false;
        }

        string type = text[..semi].Trim().ToLowerInvariant();
        value = new DsnTypedValue(type, text[(semi + 1)..].Trim());
        return type.Length > 0;
    }

    /// <summary>
    /// Makes the typed value for an address: <c>rfc822</c> with the address in xtext (RFC 3461 §4) when it is printable ASCII,
    /// else <c>utf-8</c> with the address as written (RFC 6533 §3), which is only valid in a <c>message/global-delivery-status</c>.
    /// </summary>
    /// <param name="address">The mailbox, for example <c>user@example.com</c>.</param>
    /// <returns>The typed value.</returns>
    public static DsnTypedValue ForAddress(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!Ascii.IsValid(address))
        {
            return new DsnTypedValue("utf-8", address);
        }

        var sb = new StringBuilder();
        foreach (char c in address)
        {
            // RFC 3461 §4: "+" and "=" and anything outside printable ASCII are written +HEX.
            if (c is < '!' or > '~' or '+' or '=')
            {
                sb.Append('+').Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append(c);
            }
        }

        return new DsnTypedValue("rfc822", sb.ToString());
    }

    /// <summary>
    /// The address with its transport encoding removed: xtext <c>+2B</c> escapes for type <c>rfc822</c> (RFC 3461 §4) and
    /// <c>\x{2B}</c> escapes for type <c>utf-8</c> (RFC 6533 §3). Other types are returned as written. Bad escapes are left alone.
    /// </summary>
    /// <returns>The address text.</returns>
    public string DecodeAddress()
    {
        if (Type == "rfc822")
        {
            var sb = new StringBuilder();
            for (int i = 0; i < Value.Length; i++)
            {
                if (Value[i] == '+' && i + 2 < Value.Length && IsHex(Value[i + 1]) && IsHex(Value[i + 2]))
                {
                    sb.Append((char)int.Parse(Value.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 2;
                }
                else
                {
                    sb.Append(Value[i]);
                }
            }

            return sb.ToString();
        }

        if (Type == "utf-8" && Value.Contains("\\x{", StringComparison.Ordinal))
        {
            var sb = new StringBuilder();
            for (int i = 0; i < Value.Length; i++)
            {
                if (Value[i] == '\\' && i + 3 < Value.Length && Value[i + 1] == 'x' && Value[i + 2] == '{')
                {
                    int close = Value.IndexOf('}', i + 3);
                    if (close > i + 3 && close - i - 3 <= 6 && int.TryParse(Value.AsSpan(i + 3, close - i - 3), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int cp)
                        && cp is >= 0 and <= 0x10FFFF && cp is not (>= 0xD800 and <= 0xDFFF))
                    {
                        sb.Append(char.ConvertFromUtf32(cp));
                        i = close;
                        continue;
                    }
                }

                sb.Append(Value[i]);
            }

            return sb.ToString();
        }

        return Value;
    }

    private static bool IsHex(char c) => char.IsAsciiHexDigit(c);

    /// <summary>The value as written in a field: <c>type; value</c>.</summary>
    /// <returns>The field text.</returns>
    public override string ToString() => Type + "; " + Value;
}

/// <summary>An enhanced status code of RFC 3463: <c>class.subject.detail</c>, for example <c>5.1.1</c>.</summary>
/// <param name="Class">2 (success), 4 (persistent transient failure) or 5 (permanent failure).</param>
/// <param name="Subject">The subject, 0 to 999.</param>
/// <param name="Detail">The detail, 0 to 999.</param>
public readonly record struct EnhancedStatusCode(int Class, int Subject, int Detail)
{
    /// <summary>Reads a code at the start of a value. Text after it (a comment, an explanation) is ignored.</summary>
    /// <param name="text">The text, for example <c>5.1.1 (bad destination mailbox)</c>.</param>
    /// <param name="code">The code.</param>
    /// <returns>True when three dot-separated numbers of up to three digits were found.</returns>
    public static bool TryParse(ReadOnlySpan<char> text, out EnhancedStatusCode code)
    {
        code = default;
        text = text.TrimStart(" \t");
        int[] parts = new int[3];
        int i = 0;
        for (int p = 0; p < 3; p++)
        {
            int s = i;
            while (i < text.Length && char.IsAsciiDigit(text[i]) && i - s < 3)
            {
                i++;
            }

            if (i == s)
            {
                return false;
            }

            parts[p] = int.Parse(text[s..i], NumberStyles.None, CultureInfo.InvariantCulture);
            if (p < 2)
            {
                if (i >= text.Length || text[i] != '.')
                {
                    return false;
                }

                i++;
            }
        }

        // The code must end here: a fourth digit or another dot means it is not a status code.
        if (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '.'))
        {
            return false;
        }

        code = new EnhancedStatusCode(parts[0], parts[1], parts[2]);
        return true;
    }

    /// <summary>Whether the class is one RFC 3463 defines (2, 4 or 5).</summary>
    public bool IsDefinedClass => Class is 2 or 4 or 5;

    /// <summary>The code as written, <c>5.1.1</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Class}.{Subject}.{Detail}");
}

/// <summary>The <c>Action</c> of a delivery status recipient (RFC 3464 §2.3.3).</summary>
public enum DsnAction
{
    /// <summary>Absent or not one of the five keywords.</summary>
    Unknown = 0,

    /// <summary>The message could not be delivered.</summary>
    Failed,

    /// <summary>Delivery has been delayed; the server will keep trying.</summary>
    Delayed,

    /// <summary>The message was delivered to the recipient's mailbox.</summary>
    Delivered,

    /// <summary>The message was passed to a system that does not report delivery status.</summary>
    Relayed,

    /// <summary>The message was delivered to a list or alias and expanded to more recipients.</summary>
    Expanded,
}

internal static class ReportValues
{
    /// <summary>Removes RFC 5322 comments (nested, with escapes) and trims.</summary>
    public static string StripComments(string s)
    {
        if (!s.Contains('(', StringComparison.Ordinal))
        {
            return s.Trim();
        }

        var sb = new StringBuilder();
        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' && depth > 0 && i + 1 < s.Length)
            {
                i++;
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && depth > 0)
            {
                depth--;
            }
            else if (depth == 0)
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Trim();
    }

    /// <summary>The first white-space-delimited word after comments are removed.</summary>
    public static string FirstWord(string s)
    {
        string t = StripComments(s);
        int e = t.AsSpan().IndexOfAny(' ', '\t');
        return e < 0 ? t : t[..e];
    }

    public static MimeDate? Date(string? value, List<MimeDefect>? defects, ReportField? field, int depth)
    {
        if (value is null)
        {
            return null;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(value);
        var local = defects is null ? null : new List<MimeDefect>();
        if (DateParser.TryParse(bytes, out MimeDate d, local, 0, depth))
        {
            return d;
        }

        defects?.Add(new MimeDefect(MimeDefectKind.ReportInvalidFieldValue, field?.Offset ?? 0, field?.RawValue.Length ?? 0, depth));
        return null;
    }

    public static ReportField? FirstField(ReportFieldBlock b, string name)
    {
        foreach (ReportField f in b)
        {
            if (f.IsNamed(name))
            {
                return f;
            }
        }

        return null;
    }

    public static void Duplicates(ReportFieldBlock b, ReadOnlySpan<string> singleValued, List<MimeDefect> defects, int depth)
    {
        foreach (string name in singleValued)
        {
            bool seen = false;
            foreach (ReportField f in b)
            {
                if (!f.IsNamed(name))
                {
                    continue;
                }

                if (seen)
                {
                    defects.Add(new MimeDefect(MimeDefectKind.AmbiguousReportDuplicateField, f.Offset, f.RawValue.Length, depth));
                }

                seen = true;
            }
        }
    }

    public static void Require(ReportFieldBlock b, string name, List<MimeDefect> defects, long fallbackOffset, int depth)
    {
        if (!b.Contains(name))
        {
            defects.Add(new MimeDefect(MimeDefectKind.ReportMissingField, b.Offset >= 0 ? b.Offset : fallbackOffset, 0, depth));
        }
    }

    public static void Bad(List<MimeDefect> defects, ReportField f, int depth) =>
        defects.Add(new MimeDefect(MimeDefectKind.ReportInvalidFieldValue, f.Offset, f.RawValue.Length, depth));

    /// <summary>Reads a typed value field into <paramref name="defects"/> terms: reports when present but not <c>type; value</c>.</summary>
    public static DsnTypedValue? Typed(ReportFieldBlock b, string name, List<MimeDefect>? defects, int depth)
    {
        ReportField? f = FirstField(b, name);
        if (f is null)
        {
            return null;
        }

        if (!DsnTypedValue.TryParse(f.Value, out DsnTypedValue v))
        {
            if (defects is not null)
            {
                Bad(defects, f, depth);
            }

            return v.Value.Length == 0 ? null : v;
        }

        return v;
    }

    public static ReadOnlyMemory<byte> ReadBody(MimePart part, List<MimeDefect>? defects, int depth)
    {
        using Stream s = part.OpenDecodedContent();
        using var ms = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];
        while (ms.Length < ReportFieldParser.MaxBodyBytes)
        {
            int n = s.Read(chunk, 0, (int)Math.Min(chunk.Length, ReportFieldParser.MaxBodyBytes - ms.Length));
            if (n <= 0)
            {
                return ms.ToArray();
            }

            ms.Write(chunk, 0, n);
        }

        if (s.ReadByte() >= 0)
        {
            defects?.Add(new MimeDefect(MimeDefectKind.ReportLimitExceeded, ms.Length, 0, depth));
        }

        return ms.ToArray();
    }
}

/// <summary>
/// The recipient block of a <c>message/delivery-status</c> body (RFC 3464 §2.3): one per recipient the report is about.
/// Typed values are read from the fields on demand; a value that cannot be read is null (the defect was reported when the report was parsed).
/// </summary>
public sealed class RecipientStatus
{
    /// <summary>Wraps a block of fields.</summary>
    /// <param name="fields">The recipient's fields.</param>
    public RecipientStatus(ReportFieldBlock fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        Fields = fields;
    }

    /// <summary>Makes a recipient block from the usual fields.</summary>
    /// <param name="finalRecipient">The mailbox the message was finally addressed to, for example <c>user@example.com</c>; see <see cref="DsnTypedValue.ForAddress(string)"/>.</param>
    /// <param name="action">What happened.</param>
    /// <param name="status">The RFC 3463 code.</param>
    /// <param name="diagnosticCode">The remote server's reply, written as <c>smtp; text</c>; null for none.</param>
    /// <param name="remoteMta">The remote server, written as <c>dns; host</c>; null for none.</param>
    /// <param name="originalRecipient">The recipient as first requested, if different; null for none.</param>
    /// <returns>The recipient block.</returns>
    public static RecipientStatus Create(string finalRecipient, DsnAction action, EnhancedStatusCode status, string? diagnosticCode = null, string? remoteMta = null, string? originalRecipient = null)
    {
        ArgumentNullException.ThrowIfNull(finalRecipient);
        var b = new ReportFieldBlock();
        if (originalRecipient is not null)
        {
            b.Add("Original-Recipient", DsnTypedValue.ForAddress(originalRecipient).ToString());
        }

        b.Add("Final-Recipient", DsnTypedValue.ForAddress(finalRecipient).ToString());
        b.Add("Action", action.ToString().ToLowerInvariant());
        b.Add("Status", status.ToString());
        if (remoteMta is not null)
        {
            b.Add("Remote-MTA", remoteMta);
        }

        if (diagnosticCode is not null)
        {
            b.Add("Diagnostic-Code", diagnosticCode);
        }

        return new RecipientStatus(b);
    }

    /// <summary>All the fields of the block, including extension fields.</summary>
    public ReportFieldBlock Fields { get; }

    /// <summary><c>Final-Recipient</c>: the address-type and address (RFC 3464 §2.3.2), or null.</summary>
    public DsnTypedValue? FinalRecipient => ReportValues.Typed(Fields, "Final-Recipient", null, 0);

    /// <summary><c>Original-Recipient</c> (RFC 3464 §2.3.1), or null.</summary>
    public DsnTypedValue? OriginalRecipient => ReportValues.Typed(Fields, "Original-Recipient", null, 0);

    /// <summary>The <c>Action</c> keyword, or <see cref="DsnAction.Unknown"/> when absent or not recognised.</summary>
    public DsnAction Action => ParseAction(Fields.Get("Action"));

    /// <summary>The <c>Status</c> code (RFC 3463), or null when absent or unreadable.</summary>
    public EnhancedStatusCode? Status
    {
        get
        {
            string? s = Fields.Get("Status");
            return s is not null && EnhancedStatusCode.TryParse(ReportValues.StripComments(s), out EnhancedStatusCode c) ? c : null;
        }
    }

    /// <summary><c>Remote-MTA</c>: the mta-type and name of the server that reported, or null.</summary>
    public DsnTypedValue? RemoteMta => ReportValues.Typed(Fields, "Remote-MTA", null, 0);

    /// <summary><c>Diagnostic-Code</c>: the diagnostic-type and the server's reply text, or null.</summary>
    public DsnTypedValue? DiagnosticCode => ReportValues.Typed(Fields, "Diagnostic-Code", null, 0);

    /// <summary><c>Last-Attempt-Date</c>, or null.</summary>
    public MimeDate? LastAttemptDate => ReportValues.Date(Fields.Get("Last-Attempt-Date"), null, null, 0);

    /// <summary><c>Will-Retry-Until</c> (only with <see cref="DsnAction.Delayed"/>), or null.</summary>
    public MimeDate? WillRetryUntil => ReportValues.Date(Fields.Get("Will-Retry-Until"), null, null, 0);

    /// <summary><c>Final-Log-ID</c>, or null.</summary>
    public string? FinalLogId => Fields.Get("Final-Log-ID");

    internal static DsnAction ParseAction(string? value)
    {
        if (value is null)
        {
            return DsnAction.Unknown;
        }

        return ReportValues.FirstWord(value).ToLowerInvariant() switch
        {
            "failed" => DsnAction.Failed,
            "delayed" => DsnAction.Delayed,
            "delivered" => DsnAction.Delivered,
            "relayed" => DsnAction.Relayed,
            "expanded" => DsnAction.Expanded,
            _ => DsnAction.Unknown,
        };
    }
}

/// <summary>
/// A delivery status notification body, <c>message/delivery-status</c> or <c>message/global-delivery-status</c> (RFC 3464, RFC 6533):
/// one per-message block followed by one block per recipient.
/// </summary>
public sealed class DeliveryStatusReport
{
    private static readonly string[] SingleMessage = ["Reporting-MTA", "DSN-Gateway", "Received-From-MTA", "Original-Envelope-ID", "Arrival-Date"];
    private static readonly string[] SingleRecipient = ["Original-Recipient", "Final-Recipient", "Action", "Status", "Remote-MTA", "Diagnostic-Code", "Last-Attempt-Date", "Final-Log-ID", "Will-Retry-Until"];

    private readonly List<RecipientStatus> _recipients;
    private readonly List<MimeDefect> _defects = [];

    /// <summary>Builds a report from blocks.</summary>
    /// <param name="perMessage">The per-message fields (<c>Reporting-MTA</c> and the others).</param>
    /// <param name="recipients">The recipient blocks.</param>
    public DeliveryStatusReport(ReportFieldBlock perMessage, IEnumerable<RecipientStatus> recipients)
    {
        ArgumentNullException.ThrowIfNull(perMessage);
        ArgumentNullException.ThrowIfNull(recipients);
        PerMessage = perMessage;
        _recipients = [.. recipients];
    }

    /// <summary>The per-message fields.</summary>
    public ReportFieldBlock PerMessage { get; }

    /// <summary>The recipient blocks, in order.</summary>
    public IReadOnlyList<RecipientStatus> Recipients => _recipients;

    /// <summary>What was repaired or found wrong when the report was read; empty for a report built in code. Offsets are in the decoded body.</summary>
    public IReadOnlyList<MimeDefect> Defects => _defects;

    /// <summary><c>Reporting-MTA</c> (RFC 3464 §2.2.2): the server that produced the report, as mta-type and name.</summary>
    public DsnTypedValue? ReportingMta => ReportValues.Typed(PerMessage, "Reporting-MTA", null, 0);

    /// <summary><c>DSN-Gateway</c> (§2.2.3), or null.</summary>
    public DsnTypedValue? DsnGateway => ReportValues.Typed(PerMessage, "DSN-Gateway", null, 0);

    /// <summary><c>Received-From-MTA</c> (§2.2.4), or null.</summary>
    public DsnTypedValue? ReceivedFromMta => ReportValues.Typed(PerMessage, "Received-From-MTA", null, 0);

    /// <summary><c>Original-Envelope-ID</c> (§2.2.1), or null. Not xtext-decoded; the value is the identifier the sender gave in <c>ENVID</c>, in xtext.</summary>
    public string? OriginalEnvelopeId => PerMessage.Get("Original-Envelope-ID");

    /// <summary><c>Arrival-Date</c> (§2.2.5), or null.</summary>
    public MimeDate? ArrivalDate => ReportValues.Date(PerMessage.Get("Arrival-Date"), null, null, 0);

    /// <summary>Whether any field holds non-ASCII text, which needs <c>message/global-delivery-status</c> (RFC 6533).</summary>
    public bool RequiresGlobal => PerMessage.HasNonAscii || _recipients.Any(r => r.Fields.HasNonAscii);

    /// <summary>
    /// Reads a delivery status body. The first block is the per-message block and each later one a recipient block. A body with
    /// only one block is read as a per-message block with no recipients (<see cref="MimeDefectKind.ReportMissingGroup"/>).
    /// </summary>
    /// <param name="body">The decoded body.</param>
    /// <param name="baseOffset">Added to defect offsets.</param>
    /// <param name="depth">The entity depth recorded in defects.</param>
    /// <returns>The report; never null.</returns>
    public static DeliveryStatusReport Parse(ReadOnlyMemory<byte> body, long baseOffset = 0, int depth = 0)
    {
        var defects = new List<MimeDefect>();
        List<ReportFieldBlock> blocks = ReportFieldParser.Parse(body, defects, baseOffset, depth);
        ReportFieldBlock perMessage = blocks.Count > 0 ? blocks[0] : new ReportFieldBlock();
        var recipients = new List<RecipientStatus>();
        for (int i = 1; i < blocks.Count; i++)
        {
            recipients.Add(new RecipientStatus(blocks[i]));
        }

        var r = new DeliveryStatusReport(perMessage, recipients);
        r._defects.AddRange(defects);
        r.Validate(baseOffset, depth);
        return r;
    }

    /// <summary>Reads the body of a <c>message/delivery-status</c> or <c>message/global-delivery-status</c> part.</summary>
    /// <param name="part">The part.</param>
    /// <param name="defects">Receives the report's defects (offsets in the decoded body); may be null.</param>
    /// <returns>The report, or null when the part is another media type.</returns>
    public static DeliveryStatusReport? From(MimePart part, List<MimeDefect>? defects = null)
    {
        ArgumentNullException.ThrowIfNull(part);
        if (part.MediaType is not ("message/delivery-status" or "message/global-delivery-status"))
        {
            return null;
        }

        var local = new List<MimeDefect>();
        DeliveryStatusReport r = Parse(ReportValues.ReadBody(part, local, part.Depth), 0, part.Depth);
        r._defects.AddRange(local);
        defects?.AddRange(r._defects);
        return r;
    }

    private void Validate(long baseOffset, int depth)
    {
        if (PerMessage.Count == 0)
        {
            _defects.Add(new MimeDefect(MimeDefectKind.ReportMissingGroup, baseOffset, 0, depth));
            return;
        }

        ReportValues.Duplicates(PerMessage, SingleMessage, _defects, depth);
        ReportValues.Require(PerMessage, "Reporting-MTA", _defects, baseOffset, depth);
        _ = ReportValues.Typed(PerMessage, "Reporting-MTA", _defects, depth);
        _ = ReportValues.Typed(PerMessage, "DSN-Gateway", _defects, depth);
        _ = ReportValues.Typed(PerMessage, "Received-From-MTA", _defects, depth);
        if (ReportValues.FirstField(PerMessage, "Arrival-Date") is { } ad)
        {
            _ = ReportValues.Date(ad.Value, _defects, ad, depth);
        }

        if (_recipients.Count == 0)
        {
            _defects.Add(new MimeDefect(MimeDefectKind.ReportMissingGroup, baseOffset, 0, depth));
        }

        foreach (RecipientStatus r in _recipients)
        {
            ReportFieldBlock b = r.Fields;
            ReportValues.Duplicates(b, SingleRecipient, _defects, depth);
            ReportValues.Require(b, "Final-Recipient", _defects, baseOffset, depth);
            ReportValues.Require(b, "Action", _defects, baseOffset, depth);
            ReportValues.Require(b, "Status", _defects, baseOffset, depth);
            foreach (string n in new[] { "Original-Recipient", "Final-Recipient", "Remote-MTA", "Diagnostic-Code" })
            {
                _ = ReportValues.Typed(b, n, _defects, depth);
            }

            if (ReportValues.FirstField(b, "Action") is { } af && r.Action == DsnAction.Unknown)
            {
                ReportValues.Bad(_defects, af, depth);
            }

            if (ReportValues.FirstField(b, "Status") is { } sf && !(EnhancedStatusCode.TryParse(ReportValues.StripComments(sf.Value), out EnhancedStatusCode code) && code.IsDefinedClass))
            {
                ReportValues.Bad(_defects, sf, depth);
            }

            foreach (string n in new[] { "Last-Attempt-Date", "Will-Retry-Until" })
            {
                if (ReportValues.FirstField(b, n) is { } df)
                {
                    _ = ReportValues.Date(df.Value, _defects, df, depth);
                }
            }
        }
    }

    /// <summary>Writes the body: the per-message block, then each recipient block, each ended by a blank line.</summary>
    /// <param name="newline">The line end; default CRLF.</param>
    /// <returns>The bytes.</returns>
    public byte[] ToBytes(string newline = "\r\n")
    {
        using var ms = new MemoryStream();
        byte[] nl = Encoding.ASCII.GetBytes(newline);
        PerMessage.WriteFields(ms, newline);
        foreach (RecipientStatus r in _recipients)
        {
            ms.Write(nl);
            r.Fields.WriteFields(ms, newline);
        }

        return ms.ToArray();
    }
}

/// <summary>What the receiving user agent did with the message (RFC 8098 §3.2.6.2).</summary>
public enum MdnDispositionType
{
    /// <summary>Absent or not one of the six keywords.</summary>
    Unknown = 0,

    /// <summary>The message was shown to the user.</summary>
    Displayed,

    /// <summary>The message was sent somewhere without being shown (forwarded, printed).</summary>
    Dispatched,

    /// <summary>The message was processed without being shown, and nothing more is known.</summary>
    Processed,

    /// <summary>The message was deleted unseen.</summary>
    Deleted,

    /// <summary>The recipient declined to say what happened.</summary>
    Denied,

    /// <summary>Something went wrong and no disposition could be determined.</summary>
    Failed,
}

/// <summary>The parsed <c>Disposition</c> field of a message disposition notification: <c>action-mode/sending-mode; type/modifier,modifier</c>.</summary>
/// <param name="ActionMode">The action mode as written, lower case: <c>manual-action</c> or <c>automatic-action</c>.</param>
/// <param name="SendingMode">The sending mode as written, lower case: <c>mdn-sent-manually</c> or <c>mdn-sent-automatically</c>.</param>
/// <param name="Type">The disposition type.</param>
/// <param name="TypeText">The disposition type as written, lower case.</param>
/// <param name="Modifiers">The modifiers as written, lower case (<c>error</c>, <c>warning</c>, <c>superseded</c>, <c>expired</c>, <c>mailbox-terminated</c> or an extension).</param>
public sealed record MdnDisposition(string ActionMode, string SendingMode, MdnDispositionType Type, string TypeText, IReadOnlyList<string> Modifiers)
{
    /// <summary>Reads a <c>Disposition</c> value.</summary>
    /// <param name="text">The unfolded field value.</param>
    /// <param name="disposition">The result when the text has the <c>mode; type</c> shape.</param>
    /// <returns>True when both the modes and the type were found.</returns>
    public static bool TryParse(string? text, out MdnDisposition? disposition)
    {
        disposition = null;
        if (text is null)
        {
            return false;
        }

        string t = ReportValues.StripComments(text);
        int semi = t.IndexOf(';', StringComparison.Ordinal);
        if (semi < 0)
        {
            return false;
        }

        string modes = t[..semi].Trim().ToLowerInvariant();
        string rest = t[(semi + 1)..].Trim().ToLowerInvariant();
        int slash = modes.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0 || rest.Length == 0)
        {
            return false;
        }

        string typeText = rest;
        var mods = new List<string>();
        int ms = rest.IndexOf('/', StringComparison.Ordinal);
        if (ms >= 0)
        {
            typeText = rest[..ms].Trim();
            foreach (string m in rest[(ms + 1)..].Split(','))
            {
                string mm = m.Trim();
                if (mm.Length > 0)
                {
                    mods.Add(mm);
                }
            }
        }

        MdnDispositionType type = typeText switch
        {
            "displayed" => MdnDispositionType.Displayed,
            "dispatched" => MdnDispositionType.Dispatched,
            "processed" => MdnDispositionType.Processed,
            "deleted" => MdnDispositionType.Deleted,
            "denied" => MdnDispositionType.Denied,
            "failed" => MdnDispositionType.Failed,
            _ => MdnDispositionType.Unknown,
        };
        disposition = new MdnDisposition(modes[..slash].Trim(), modes[(slash + 1)..].Trim(), type, typeText, mods);
        return type != MdnDispositionType.Unknown && modes[..slash].Trim().Length > 0 && modes[(slash + 1)..].Trim().Length > 0;
    }

    /// <summary>The value as written in a field.</summary>
    /// <returns>The field text.</returns>
    public override string ToString() => ActionMode + "/" + SendingMode + "; " + TypeText + (Modifiers.Count > 0 ? "/" + string.Join(',', Modifiers) : string.Empty);
}

/// <summary>
/// A message disposition notification body, <c>message/disposition-notification</c> or <c>message/global-disposition-notification</c>
/// (RFC 8098, RFC 3798, RFC 6533): one block of fields.
/// </summary>
public sealed class DispositionNotification
{
    private static readonly string[] Single = ["Reporting-UA", "MDN-Gateway", "Original-Recipient", "Final-Recipient", "Original-Message-ID", "Disposition"];
    private readonly List<ReportFieldBlock> _extra;
    private readonly List<MimeDefect> _defects = [];

    /// <summary>Builds a notification from a block.</summary>
    /// <param name="fields">The fields.</param>
    /// <param name="extraBlocks">Blocks after the first, if any; null for none.</param>
    public DispositionNotification(ReportFieldBlock fields, IEnumerable<ReportFieldBlock>? extraBlocks = null)
    {
        ArgumentNullException.ThrowIfNull(fields);
        Fields = fields;
        _extra = extraBlocks is null ? [] : [.. extraBlocks];
    }

    /// <summary>Makes a notification with the required fields.</summary>
    /// <param name="reportingUa">The user agent that made the report, as <c>name; product</c>; null to omit.</param>
    /// <param name="finalRecipient">The mailbox the message was finally delivered to.</param>
    /// <param name="originalMessageId">The <c>Message-ID</c> of the message, with its angle brackets; null to omit.</param>
    /// <param name="disposition">What happened.</param>
    /// <returns>The notification.</returns>
    public static DispositionNotification Create(string? reportingUa, string finalRecipient, string? originalMessageId, MdnDisposition disposition)
    {
        ArgumentNullException.ThrowIfNull(finalRecipient);
        ArgumentNullException.ThrowIfNull(disposition);
        var b = new ReportFieldBlock();
        if (reportingUa is not null)
        {
            b.Add("Reporting-UA", reportingUa);
        }

        b.Add("Final-Recipient", DsnTypedValue.ForAddress(finalRecipient).ToString());
        if (originalMessageId is not null)
        {
            b.Add("Original-Message-ID", originalMessageId);
        }

        b.Add("Disposition", disposition.ToString());
        return new DispositionNotification(b);
    }

    /// <summary>The fields of the notification, including extension fields (<c>Error</c>, <c>Warning</c>, <c>Failure</c> and others).</summary>
    public ReportFieldBlock Fields { get; }

    /// <summary>Blocks after the first. RFC 8098 has one block; anything after it is kept here and reported as <see cref="MimeDefectKind.AmbiguousReportExtraBlocks"/>.</summary>
    public IReadOnlyList<ReportFieldBlock> ExtraBlocks => _extra;

    /// <summary>What was repaired or found wrong when the notification was read. Offsets are in the decoded body.</summary>
    public IReadOnlyList<MimeDefect> Defects => _defects;

    /// <summary><c>Reporting-UA</c>: the user agent as <c>name; product</c>, or null.</summary>
    public string? ReportingUa => Fields.Get("Reporting-UA");

    /// <summary><c>MDN-Gateway</c>: the gateway, as mta-type and name, or null.</summary>
    public DsnTypedValue? MdnGateway => ReportValues.Typed(Fields, "MDN-Gateway", null, 0);

    /// <summary><c>Original-Recipient</c>, or null.</summary>
    public DsnTypedValue? OriginalRecipient => ReportValues.Typed(Fields, "Original-Recipient", null, 0);

    /// <summary><c>Final-Recipient</c>, or null.</summary>
    public DsnTypedValue? FinalRecipient => ReportValues.Typed(Fields, "Final-Recipient", null, 0);

    /// <summary><c>Original-Message-ID</c> without angle brackets, or null.</summary>
    public string? OriginalMessageId
    {
        get
        {
            string? v = Fields.Get("Original-Message-ID");
            if (v is null)
            {
                return null;
            }

            List<string> ids = MessageIdParser.Parse(Encoding.UTF8.GetBytes(v));
            return ids.Count > 0 ? ids[0] : null;
        }
    }

    /// <summary>The <c>Disposition</c> field, or null when absent or unreadable.</summary>
    public MdnDisposition? Disposition => MdnDisposition.TryParse(Fields.Get("Disposition"), out MdnDisposition? d) ? d : null;

    /// <summary>Reads a notification body.</summary>
    /// <param name="body">The decoded body.</param>
    /// <param name="baseOffset">Added to defect offsets.</param>
    /// <param name="depth">The entity depth recorded in defects.</param>
    /// <returns>The notification; never null.</returns>
    public static DispositionNotification Parse(ReadOnlyMemory<byte> body, long baseOffset = 0, int depth = 0)
    {
        var defects = new List<MimeDefect>();
        List<ReportFieldBlock> blocks = ReportFieldParser.Parse(body, defects, baseOffset, depth, singleBlock: true);
        var n = new DispositionNotification(blocks.Count > 0 ? blocks[0] : new ReportFieldBlock(), blocks.Skip(1));
        n._defects.AddRange(defects);
        n.Validate(baseOffset, depth);
        return n;
    }

    /// <summary>Reads the body of a <c>message/disposition-notification</c> or <c>message/global-disposition-notification</c> part.</summary>
    /// <param name="part">The part.</param>
    /// <param name="defects">Receives the notification's defects; may be null.</param>
    /// <returns>The notification, or null when the part is another media type.</returns>
    public static DispositionNotification? From(MimePart part, List<MimeDefect>? defects = null)
    {
        ArgumentNullException.ThrowIfNull(part);
        if (part.MediaType is not ("message/disposition-notification" or "message/global-disposition-notification"))
        {
            return null;
        }

        var local = new List<MimeDefect>();
        DispositionNotification n = Parse(ReportValues.ReadBody(part, local, part.Depth), 0, part.Depth);
        n._defects.AddRange(local);
        defects?.AddRange(n._defects);
        return n;
    }

    private void Validate(long baseOffset, int depth)
    {
        if (_extra.Count > 0)
        {
            _defects.Add(new MimeDefect(MimeDefectKind.AmbiguousReportExtraBlocks, _extra[0].Offset, 0, depth));
        }

        if (Fields.Count == 0)
        {
            _defects.Add(new MimeDefect(MimeDefectKind.ReportMissingGroup, baseOffset, 0, depth));
            return;
        }

        ReportValues.Duplicates(Fields, Single, _defects, depth);
        ReportValues.Require(Fields, "Final-Recipient", _defects, baseOffset, depth);
        ReportValues.Require(Fields, "Disposition", _defects, baseOffset, depth);
        foreach (string n in new[] { "MDN-Gateway", "Original-Recipient", "Final-Recipient" })
        {
            _ = ReportValues.Typed(Fields, n, _defects, depth);
        }

        if (ReportValues.FirstField(Fields, "Disposition") is { } d && Disposition is null)
        {
            ReportValues.Bad(_defects, d, depth);
        }
    }

    /// <summary>Writes the body: the fields and the blank line that ends them.</summary>
    /// <param name="newline">The line end; default CRLF.</param>
    /// <returns>The bytes.</returns>
    public byte[] ToBytes(string newline = "\r\n") => Fields.ToBytes(newline);
}

/// <summary>The kind of an abuse report, from <c>Feedback-Type</c> (RFC 5965 §3.1, with the values of RFC 6650 and RFC 6591).</summary>
public enum FeedbackKind
{
    /// <summary>Absent or a keyword not listed here.</summary>
    Unknown = 0,

    /// <summary>Unsolicited bulk mail.</summary>
    Abuse,

    /// <summary>Phishing or other fraud.</summary>
    Fraud,

    /// <summary>The message carried a virus.</summary>
    Virus,

    /// <summary>Anything else.</summary>
    Other,

    /// <summary>The reporter says the message is not spam (RFC 5965 §7.3.1 registry).</summary>
    NotSpam,

    /// <summary>An authentication failure report (RFC 6591).</summary>
    AuthFailure,
}

/// <summary>
/// An abuse feedback report, <c>message/feedback-report</c> (RFC 5965 with RFC 6591 and RFC 6650): one block of fields.
/// </summary>
public sealed class FeedbackReport
{
    private static readonly string[] Single = ["Feedback-Type", "User-Agent", "Version", "Original-Mail-From", "Arrival-Date", "Reporting-MTA", "Source-IP", "Incidents", "Auth-Failure", "Delivery-Result", "DKIM-Domain", "DKIM-Identity", "DKIM-Selector"];
    private readonly List<ReportFieldBlock> _extra;
    private readonly List<MimeDefect> _defects = [];

    /// <summary>Builds a report from a block.</summary>
    /// <param name="fields">The fields.</param>
    /// <param name="extraBlocks">Blocks after the first, if any; null for none.</param>
    public FeedbackReport(ReportFieldBlock fields, IEnumerable<ReportFieldBlock>? extraBlocks = null)
    {
        ArgumentNullException.ThrowIfNull(fields);
        Fields = fields;
        _extra = extraBlocks is null ? [] : [.. extraBlocks];
    }

    /// <summary>Makes a report with the three required fields and the common optional ones.</summary>
    /// <param name="feedbackType">The type keyword: <c>abuse</c>, <c>fraud</c>, <c>virus</c>, <c>other</c>, <c>not-spam</c> or <c>auth-failure</c>.</param>
    /// <param name="userAgent">The reporting agent, <c>product/version</c>.</param>
    /// <param name="sourceIp">The address the message came from, or null.</param>
    /// <param name="arrivalDate">When the message arrived at the reporter, or null.</param>
    /// <returns>The report, with <c>Version: 1</c>.</returns>
    public static FeedbackReport Create(string feedbackType, string userAgent, IPAddress? sourceIp = null, DateTimeOffset? arrivalDate = null)
    {
        ArgumentNullException.ThrowIfNull(feedbackType);
        ArgumentNullException.ThrowIfNull(userAgent);
        var b = new ReportFieldBlock();
        b.Add("Feedback-Type", feedbackType);
        b.Add("User-Agent", userAgent);
        b.Add("Version", "1");
        if (arrivalDate is { } d)
        {
            b.Add("Arrival-Date", d.ToString("ddd, dd MMM yyyy HH:mm:ss ", CultureInfo.InvariantCulture) + (d.Offset < TimeSpan.Zero ? "-" : "+") + d.Offset.ToString("hhmm", CultureInfo.InvariantCulture));
        }

        if (sourceIp is not null)
        {
            b.Add("Source-IP", sourceIp.ToString());
        }

        return new FeedbackReport(b);
    }

    /// <summary>The fields of the report, including extension fields.</summary>
    public ReportFieldBlock Fields { get; }

    /// <summary>Blocks after the first. RFC 5965 has one block; anything after it is kept here and reported as <see cref="MimeDefectKind.AmbiguousReportExtraBlocks"/>.</summary>
    public IReadOnlyList<ReportFieldBlock> ExtraBlocks => _extra;

    /// <summary>What was repaired or found wrong when the report was read. Offsets are in the decoded body.</summary>
    public IReadOnlyList<MimeDefect> Defects => _defects;

    /// <summary><c>Feedback-Type</c> as written, lower case, or null.</summary>
    public string? FeedbackType => Fields.Get("Feedback-Type") is { } t ? ReportValues.FirstWord(t).ToLowerInvariant() : null;

    /// <summary>The <c>Feedback-Type</c> as a keyword.</summary>
    public FeedbackKind Kind => FeedbackType switch
    {
        "abuse" => FeedbackKind.Abuse,
        "fraud" => FeedbackKind.Fraud,
        "virus" => FeedbackKind.Virus,
        "other" => FeedbackKind.Other,
        "not-spam" => FeedbackKind.NotSpam,
        "auth-failure" => FeedbackKind.AuthFailure,
        _ => FeedbackKind.Unknown,
    };

    /// <summary><c>User-Agent</c>, or null.</summary>
    public string? UserAgent => Fields.Get("User-Agent");

    /// <summary><c>Version</c> (always 1 so far), or null when absent or not a number.</summary>
    public int? Version => Fields.Get("Version") is { } v && int.TryParse(ReportValues.FirstWord(v), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : null;

    /// <summary><c>Original-Mail-From</c>, or null.</summary>
    public string? OriginalMailFrom => Fields.Get("Original-Mail-From");

    /// <summary>Every <c>Original-Rcpt-To</c>.</summary>
    public IReadOnlyList<string> OriginalRcptTo => Fields.GetAll("Original-Rcpt-To");

    /// <summary>Every <c>Removal-Recipient</c> (RFC 6650).</summary>
    public IReadOnlyList<string> RemovalRecipients => Fields.GetAll("Removal-Recipient");

    /// <summary>Every <c>Reported-Domain</c>.</summary>
    public IReadOnlyList<string> ReportedDomains => Fields.GetAll("Reported-Domain");

    /// <summary>Every <c>Reported-URI</c>.</summary>
    public IReadOnlyList<string> ReportedUris => Fields.GetAll("Reported-URI");

    /// <summary>Every <c>Authentication-Results</c> field of the report, as written; parse one with <see cref="Authentication.AuthenticationResults.Parse(string)"/>.</summary>
    public IReadOnlyList<string> AuthenticationResults => Fields.GetAll("Authentication-Results");

    /// <summary><c>Arrival-Date</c> (or the older <c>Received-Date</c>), or null.</summary>
    public MimeDate? ArrivalDate => ReportValues.Date(Fields.Get("Arrival-Date") ?? Fields.Get("Received-Date"), null, null, 0);

    /// <summary><c>Reporting-MTA</c>, as mta-type and name, or null.</summary>
    public DsnTypedValue? ReportingMta => ReportValues.Typed(Fields, "Reporting-MTA", null, 0);

    /// <summary><c>Source-IP</c>, or null when absent or not an address.</summary>
    public IPAddress? SourceIp => Fields.Get("Source-IP") is { } s && IPAddress.TryParse(ReportValues.FirstWord(s), out IPAddress? ip) ? ip : null;

    /// <summary><c>Incidents</c>, or null.</summary>
    public int? Incidents => Fields.Get("Incidents") is { } s && int.TryParse(ReportValues.FirstWord(s), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : null;

    /// <summary><c>Auth-Failure</c> (RFC 6591): <c>adsp</c>, <c>bodyhash</c>, <c>revoked</c>, <c>signature</c>, <c>spf</c> or <c>dmarc</c>, lower case, or null.</summary>
    public string? AuthFailure => Fields.Get("Auth-Failure") is { } s ? ReportValues.FirstWord(s).ToLowerInvariant() : null;

    /// <summary><c>Delivery-Result</c> (RFC 6591), lower case, or null.</summary>
    public string? DeliveryResult => Fields.Get("Delivery-Result") is { } s ? ReportValues.FirstWord(s).ToLowerInvariant() : null;

    /// <summary><c>DKIM-Domain</c> (RFC 6591), or null.</summary>
    public string? DkimDomain => Fields.Get("DKIM-Domain");

    /// <summary><c>DKIM-Selector</c> (RFC 6591), or null.</summary>
    public string? DkimSelector => Fields.Get("DKIM-Selector");

    /// <summary><c>DKIM-Identity</c> (RFC 6591), or null.</summary>
    public string? DkimIdentity => Fields.Get("DKIM-Identity");

    /// <summary>Reads a feedback report body.</summary>
    /// <param name="body">The decoded body.</param>
    /// <param name="baseOffset">Added to defect offsets.</param>
    /// <param name="depth">The entity depth recorded in defects.</param>
    /// <returns>The report; never null.</returns>
    public static FeedbackReport Parse(ReadOnlyMemory<byte> body, long baseOffset = 0, int depth = 0)
    {
        var defects = new List<MimeDefect>();
        List<ReportFieldBlock> blocks = ReportFieldParser.Parse(body, defects, baseOffset, depth, singleBlock: true);
        var r = new FeedbackReport(blocks.Count > 0 ? blocks[0] : new ReportFieldBlock(), blocks.Skip(1));
        r._defects.AddRange(defects);
        r.Validate(baseOffset, depth);
        return r;
    }

    /// <summary>Reads the body of a <c>message/feedback-report</c> part.</summary>
    /// <param name="part">The part.</param>
    /// <param name="defects">Receives the report's defects; may be null.</param>
    /// <returns>The report, or null when the part is another media type.</returns>
    public static FeedbackReport? From(MimePart part, List<MimeDefect>? defects = null)
    {
        ArgumentNullException.ThrowIfNull(part);
        if (part.MediaType != "message/feedback-report")
        {
            return null;
        }

        var local = new List<MimeDefect>();
        FeedbackReport r = Parse(ReportValues.ReadBody(part, local, part.Depth), 0, part.Depth);
        r._defects.AddRange(local);
        defects?.AddRange(r._defects);
        return r;
    }

    private void Validate(long baseOffset, int depth)
    {
        if (_extra.Count > 0)
        {
            _defects.Add(new MimeDefect(MimeDefectKind.AmbiguousReportExtraBlocks, _extra[0].Offset, 0, depth));
        }

        if (Fields.Count == 0)
        {
            _defects.Add(new MimeDefect(MimeDefectKind.ReportMissingGroup, baseOffset, 0, depth));
            return;
        }

        ReportValues.Duplicates(Fields, Single, _defects, depth);
        foreach (string n in new[] { "Feedback-Type", "User-Agent", "Version" })
        {
            ReportValues.Require(Fields, n, _defects, baseOffset, depth);
        }

        if (ReportValues.FirstField(Fields, "Feedback-Type") is { } ft && Kind == FeedbackKind.Unknown)
        {
            // RFC 5965 §3.1 lets the registry grow, so an unknown keyword is reported but still read.
            ReportValues.Bad(_defects, ft, depth);
        }

        if (ReportValues.FirstField(Fields, "Version") is { } vf && Version is not 1)
        {
            ReportValues.Bad(_defects, vf, depth);
        }

        if (ReportValues.FirstField(Fields, "Source-IP") is { } ip && SourceIp is null)
        {
            ReportValues.Bad(_defects, ip, depth);
        }

        if (ReportValues.FirstField(Fields, "Incidents") is { } inc && Incidents is null)
        {
            ReportValues.Bad(_defects, inc, depth);
        }

        if (ReportValues.FirstField(Fields, "Arrival-Date") is { } ad)
        {
            _ = ReportValues.Date(ad.Value, _defects, ad, depth);
        }

        _ = ReportValues.Typed(Fields, "Reporting-MTA", _defects, depth);
    }

    /// <summary>Writes the body: the fields and the blank line that ends them.</summary>
    /// <param name="newline">The line end; default CRLF.</param>
    /// <returns>The bytes.</returns>
    public byte[] ToBytes(string newline = "\r\n") => Fields.ToBytes(newline);
}
