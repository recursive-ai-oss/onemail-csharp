using System.Security.Cryptography;
using System.Text.Json;
using MimeKit;
using MimeKit.Tnef;

// MimeKitTnef DIR: one JSON line per .eml file in DIR with
//   attachments: the entries MimeMessage.Attachments lists (what a caller sees without unpacking TNEF)
//   tnef:        what TnefPart.ExtractAttachments returns for each TNEF part (names, sizes, SHA-256)
// MimeKit's documentation asks .NET (Core) callers to register the code page provider before reading legacy charsets, which its TNEF reader needs.
System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
string dir = args.Length > 0 ? args[0] : ".";
foreach (string path in Directory.EnumerateFiles(dir, "*.eml").Order(StringComparer.Ordinal))
{
    var record = new Dictionary<string, object?> { ["file"] = Path.GetFileName(path) };
    try
    {
        MimeMessage message = MimeMessage.Load(path);
        record["attachments"] = message.Attachments.Select(Describe).ToList();
        var unpacked = new List<object>();
        foreach (MimeEntity entity in message.BodyParts)
        {
            if (entity is TnefPart tnef)
            {
                foreach (MimeEntity inner in tnef.ExtractAttachments())
                {
                    unpacked.Add(Describe(inner));
                }
            }
        }

        record["tnef"] = unpacked;
    }
    catch (Exception e)
    {
        record["error"] = e.GetType().Name + ": " + e.Message;
    }

    Console.WriteLine(JsonSerializer.Serialize(record));
}

static object Describe(MimeEntity entity)
{
    string name = entity is MimePart p ? p.FileName ?? string.Empty : string.Empty;
    using var ms = new MemoryStream();
    if (entity is MimePart part && part.Content is not null)
    {
        part.Content.DecodeTo(ms);
    }
    else if (entity is MessagePart mp && mp.Message is not null)
    {
        mp.Message.WriteTo(ms);
    }

    return new { name, type = entity.ContentType.MimeType, size = ms.Length, sha256 = Convert.ToHexString(SHA256.HashData(ms.ToArray())).ToLowerInvariant() };
}
