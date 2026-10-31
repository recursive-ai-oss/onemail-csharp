using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Rtf;
using RecursiveAI.OneMail.Rtf.Compression;

namespace RecursiveAI.OneMail.Conversion.OxcMail;

/// <summary>
/// The RTF body of a message and what can be taken out of it: the decompressed RTF (MS-OXRTFCP) and, when it encapsulates HTML or
/// plain text, that text (MS-OXRTFEX). Read once; the work is done when the body is first asked for.
/// </summary>
internal sealed class RtfBodies
{
    public byte[]? Data { get; private init; }

    public string? Html { get; private init; }

    public string? Text { get; private init; }

    public RtfEncapsulation Kind { get; private init; }

    private RtfHtmlResult? _native;
    private bool _nativeDone;

    /// <summary>The RTF converted to HTML and text when it is written as RTF (it encapsulates neither HTML nor text); null when there is no RTF body or it is an encapsulation.</summary>
    public RtfHtmlResult? Native()
    {
        if (!_nativeDone)
        {
            _nativeDone = true;
            if (Data is not null && Kind == RtfEncapsulation.None)
            {
                _native = RtfHtmlConverter.Convert(Data);
            }
        }

        return _native;
    }

    public static RtfBodies Read(MapiMessage m)
    {
        if (m.Properties.Get(MapiPid.RtfCompressed, MapiType.Binary) is not { } p)
        {
            return new RtfBodies();
        }

        RtfDecompressionResult d = RtfDecompressor.Decompress(p.GetBytes().Span);
        if (d.Data.Length == 0)
        {
            return new RtfBodies();
        }

        RtfDeencapsulationResult r = RtfDeencapsulator.Deencapsulate(d.Data);
        return new RtfBodies
        {
            Data = d.Data,
            Kind = r.Kind,
            Html = r.Kind == RtfEncapsulation.Html ? r.Text : null,
            Text = r.Kind == RtfEncapsulation.PlainText ? r.Text : null,
        };
    }
}
