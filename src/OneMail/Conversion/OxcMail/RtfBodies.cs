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
