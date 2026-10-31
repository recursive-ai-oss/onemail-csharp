using RecursiveAI.OneMail.Mime.Reading;
using RecursiveAI.OneMail.Text;

namespace RecursiveAI.OneMail.Mime;

/// <summary>Turns the text layer's issues into MIME defects and resolves header parameters (RFC 2045 §5.1, RFC 2231).</summary>
internal static class HeaderDecoding
{
    public static MimeDefectKind Map(TextIssueKind kind) => kind switch
    {
        TextIssueKind.InvalidBytes => MimeDefectKind.InvalidCharsetBytes,
        TextIssueKind.AmbiguousUnknownCharset => MimeDefectKind.AmbiguousUnknownCharset,
        TextIssueKind.AmbiguousRaw8Bit => MimeDefectKind.AmbiguousEightBitHeader,
        TextIssueKind.MalformedEncodedWord => MimeDefectKind.MalformedEncodedWord,
        TextIssueKind.InvalidBase64 => MimeDefectKind.InvalidEncodedWordBase64,
        TextIssueKind.InvalidQuotedPrintable => MimeDefectKind.InvalidEncodedWordQuotedPrintable,
        TextIssueKind.EncodedWordTooLong => MimeDefectKind.EncodedWordTooLong,
        TextIssueKind.AmbiguousEncodedWordInQuotedString => MimeDefectKind.AmbiguousEncodedWordInQuotedString,
        TextIssueKind.AmbiguousSplitMultibyte => MimeDefectKind.AmbiguousSplitMultibyte,
        TextIssueKind.InvalidPercentEscape => MimeDefectKind.InvalidParameterEscape,
        TextIssueKind.MalformedExtendedValue => MimeDefectKind.MalformedExtendedParameter,
        TextIssueKind.AmbiguousParameterSegments => MimeDefectKind.AmbiguousParameterSegments,
        TextIssueKind.DuplicateParameterSegment => MimeDefectKind.DuplicateParameterSegment,
        TextIssueKind.AmbiguousParameterForms => MimeDefectKind.AmbiguousParameterForms,
        TextIssueKind.DuplicateParameter => MimeDefectKind.DuplicateParameter,
        TextIssueKind.LimitExceeded => MimeDefectKind.TextLimitExceeded,
        _ => MimeDefectKind.InvalidCharsetBytes, // UTF-7, ISO-2022 and unencodable characters cannot arise from a header value
    };

    /// <summary>Appends a defect for each issue; issue offsets are relative to the span that was decoded, which starts at <paramref name="baseOffset"/>.</summary>
    public static void Report(List<TextIssue> issues, List<MimeDefect>? defects, long baseOffset, int depth)
    {
        if (defects is null)
        {
            return;
        }

        foreach (TextIssue i in issues)
        {
            defects.Add(new MimeDefect(Map(i.Kind), baseOffset + i.Offset, i.Length, depth));
        }
    }

    /// <summary>
    /// Resolves the parameters that follow the first token of a structured header value (Content-Type after
    /// <c>type/subtype</c>, Content-Disposition after the disposition). RFC 2231 continuations and extended values are joined and
    /// decoded; names are case-insensitive; comments are dropped; a parameter without <c>=</c> is dropped.
    /// </summary>
    public static List<DecodedParameter> Parameters(MimeHeader? header, int from, List<MimeDefect>? defects, int depth)
    {
        if (header is null)
        {
            return [];
        }

        ReadOnlySpan<byte> v = header.RawValue.Span;
        long baseOffset = Math.Max(0, header.ValueSourceOffset);
        var collector = new ParameterCollector();
        var en = new StructuredHeader.ParameterEnumerator(v, from);
        while (en.MoveNext())
        {
            if (en.Unterminated)
            {
                defects?.Add(new MimeDefect(MimeDefectKind.UnterminatedQuote, baseOffset + en.NameStart, 0, depth));
            }

            if (en.Quoted)
            {
                byte[] tmp = new byte[en.Value.Length];
                int n = StructuredHeader.Unquote(en.Value, tmp);
                collector.Add(en.Name, tmp.AsSpan(0, n), en.NameStart);
            }
            else
            {
                collector.Add(en.Name, en.Value, en.NameStart);
            }
        }

        if (en.FirstInvalid >= 0)
        {
            defects?.Add(new MimeDefect(MimeDefectKind.InvalidParameter, baseOffset + en.FirstInvalid, 0, depth));
        }

        var issues = new List<TextIssue>();
        List<DecodedParameter> result = collector.Resolve(issues);
        Report(issues, defects, baseOffset, depth);
        return result;
    }

    /// <summary>The offset just past <c>type/subtype</c> in a Content-Type value, or -1 when the value is not a media type.</summary>
    public static int AfterMediaType(MimeHeader header)
    {
        return StructuredHeader.TryParseMediaType(header.RawValue.Span, out _, out _, out int next) ? next : -1;
    }

    /// <summary>The offset just past the first token of a Content-Disposition value.</summary>
    public static int AfterToken(ReadOnlySpan<byte> v)
    {
        int i = StructuredHeader.SkipCfws(v, 0);
        while (i < v.Length && v[i] != (byte)';')
        {
            i++;
        }

        return i;
    }
}
