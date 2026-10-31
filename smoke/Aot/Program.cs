using RecursiveAI.OneMail;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Client;
using RecursiveAI.OneMail.Cryptography;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Render;

namespace RecursiveAI.OneMail.AotSmoke;

/// <summary>Touches every package under NativeAOT with trimming; the publish must produce no trim or AOT warning.</summary>
internal static class Program
{
    private static int Main()
    {
        string[] lines =
        [
            CalendarLibrary.Name,
            OneMailLibrary.Describe(),
            ClientLibrary.Describe(),
            CryptographyLibrary.Describe(),
            RenderLibrary.Describe(),
        ];
        foreach (string line in lines)
        {
            Console.WriteLine(line);
        }

        // The MIME layer under NativeAOT: parse a small multipart, read a decoded body, write it back unchanged.
        byte[] message = "Content-Type: multipart/mixed; boundary=b\r\n\r\n--b\r\nContent-Transfer-Encoding: base64\r\n\r\nSGk=\r\n--b--\r\n"u8.ToArray();
        var root = (MimeMultipart)MimeEntity.Parse(message);
        var part = (MimePart)root.Children[0];
        bool mimeOk = System.Text.Encoding.ASCII.GetString(part.GetDecodedContent()) == "Hi" && root.ToArray().AsSpan().SequenceEqual(message);
        Console.WriteLine(mimeOk ? "mime ok" : "mime FAILED");

        return mimeOk && OneMailLibrary.Describe() == "RecursiveAI.OneMail (on RecursiveAI.OneMail.Calendar)" ? 0 : 1;
    }
}
