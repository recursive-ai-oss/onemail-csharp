using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Mime.Authentication;
using RecursiveAI.OneMail.Mime.Mhtml;
using RecursiveAI.OneMail.Mime.Reports;

namespace RecursiveAI.OneMail.Fuzz;

// Fuzz targets for the report field parsers (DSN, MDN, feedback), Authentication-Results / Received-SPF and MHTML URI
// resolution. Only typed results may escape: an exception, or a parsed report that cannot be written and parsed again, is a finding.
internal static class ReportTargets
{
    internal static void Register(Dictionary<string, Action<ReadOnlySpan<byte>>> targets)
    {
        targets["reports"] = static d => Reports(d);
        targets["authres"] = static d => AuthRes(d);
        targets["mhtml-uri"] = static d => Uri(d);
    }

    private static void Reports(ReadOnlySpan<byte> data)
    {
        byte[] input = data.ToArray();

        DeliveryStatusReport dsn = DeliveryStatusReport.Parse(input);
        _ = dsn.RequiresGlobal;
        foreach (RecipientStatus r in dsn.Recipients)
        {
            _ = (r.FinalRecipient, r.OriginalRecipient, r.Action, r.Status, r.RemoteMta, r.DiagnosticCode, r.LastAttemptDate, r.WillRetryUntil, r.FinalLogId);
        }

        DeliveryStatusReport again = DeliveryStatusReport.Parse(dsn.ToBytes());
        if (again.Recipients.Count != dsn.Recipients.Count)
        {
            throw new InvalidOperationException("a written delivery-status report reads back with another recipient count");
        }

        DispositionNotification mdn = DispositionNotification.Parse(input);
        _ = (mdn.ReportingUa, mdn.MdnGateway, mdn.OriginalRecipient, mdn.FinalRecipient, mdn.Disposition);
        _ = DispositionNotification.Parse(mdn.ToBytes());

        FeedbackReport arf = FeedbackReport.Parse(input);
        _ = (arf.FeedbackType, arf.Version, arf.SourceIp, arf.Incidents);
        _ = FeedbackReport.Parse(arf.ToBytes());

        string text = System.Text.Encoding.Latin1.GetString(input);
        _ = EnhancedStatusCode.TryParse(text, out _);
        _ = DsnTypedValue.TryParse(text, out _);
        _ = MdnDisposition.TryParse(text, out _);
    }

    private static void AuthRes(ReadOnlySpan<byte> data)
    {
        AuthenticationResults ar = AuthenticationResults.Parse(data);
        foreach (AuthenticationMethodResult m in ar.Results)
        {
            _ = (m.Method, m.MethodVersion, m.Result, m.ResultComment, m.Reason);
            foreach (AuthenticationProperty p in m.Properties)
            {
                _ = (p.PropertyType, p.Name, p.Value);
            }
        }

        _ = AuthenticationResults.TryParse(data, out _);
        ReceivedSpf spf = ReceivedSpf.Parse(data);
        _ = (spf.Result, spf.ClientIp, spf.Defects);
    }

    private static void Uri(ReadOnlySpan<byte> data)
    {
        string s = System.Text.Encoding.Latin1.GetString(data);
        int cut = data.IndexOf((byte)'\n');
        string a = cut < 0 ? s : s[..cut];
        string b = cut < 0 ? string.Empty : s[(cut + 1)..];
        string joined = MhtmlUri.Resolve(a, b);
        _ = MhtmlUri.Normalize(joined);
        _ = MhtmlUri.Normalize(a);
        _ = MhtmlUri.PercentDecode(b);
        _ = MhtmlUri.SchemeOf(a);
    }
}
