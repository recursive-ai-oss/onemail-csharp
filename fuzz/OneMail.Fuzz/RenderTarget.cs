using System.Text;
using RecursiveAI.OneMail.Render;

namespace RecursiveAI.OneMail.Fuzz;

// A message to PDF and PNG on hostile markup. Byte 0 picks the options (remote content allowed, plain-text body, a small page); the rest is the
// HTML (or text) body. Nothing may throw except a typed MailRenderException, the PDF must start with its header, and no resource outside a
// data: URI may be fetched (the callback records what the layout asked for and supplies nothing).
internal static class RenderTarget
{
    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        byte b = data[0];
        string body = Encoding.UTF8.GetString(data[1..]);
        IMailItem item = Mail.Parse(Encoding.UTF8.GetBytes(
            "Subject: fuzz\r\nContent-Type: " + ((b & 2) != 0 ? "text/plain" : "text/html") + "; charset=utf-8\r\n\r\n" + body));
        var o = new RenderOptions
        {
            Page = (b & 4) != 0 ? new RenderPage(200, 300, 10, 10, 10, 10) : RenderPage.A4,
            MaxDuration = TimeSpan.FromSeconds(3),
            MaxPdfPages = 50,
            MaxPngPages = 2,
            Resources = url => url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? throw new InvalidOperationException("a data: URI reached the callback") : null,
            Display = (b & 1) != 0
                ? new RecursiveAI.OneMail.Display.DisplayOptions { Sanitizer = new RecursiveAI.OneMail.Display.SanitizerOptions { RemoteContent = RecursiveAI.OneMail.Display.RemoteContentAction.Allow } }
                : RecursiveAI.OneMail.Display.DisplayOptions.Default,
        };
        try
        {
            using var pdf = new MemoryStream();
            item.ToPdfAsync(pdf, o).GetAwaiter().GetResult();
            if (pdf.Length < 4 || pdf.GetBuffer()[0] != (byte)'%' || pdf.GetBuffer()[1] != (byte)'P')
            {
                throw new InvalidOperationException("the PDF has no header");
            }

            item.ToPngAsync(_ => Stream.Null, o).GetAwaiter().GetResult();
        }
        catch (MailRenderException)
        {
        }
    }

    // RFC 2397 decoding on arbitrary text: never throws.
    public static void DataUri(ReadOnlySpan<byte> data)
    {
        _ = MessageRenderer.DecodeDataUri("data:" + Encoding.UTF8.GetString(data));
    }
}
