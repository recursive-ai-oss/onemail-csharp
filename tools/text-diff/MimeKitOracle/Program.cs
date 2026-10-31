using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using MimeKit;
using MimeKit.Utils;

// Prints one JSON line per case (format as in py_oracle.py).
//   ew:    MimeKit.Utils.Rfc2047.DecodeText
//   param: ContentDisposition.TryParse, then the named parameter
// MimeKit documents this registration for .NET Core: without it only the built-in charsets are available.
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
var options = ParserOptions.Default;
using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
foreach (string line in File.ReadLines(args[0]))
{
    using JsonDocument doc = JsonDocument.Parse(line);
    JsonElement c = doc.RootElement;
    int id = c.GetProperty("id").GetInt32();
    string result;
    try
    {
        if (c.GetProperty("kind").GetString() == "cs")
        {
            result = $"{{\"id\":{id},\"s\":1}}";
        }
        else if (c.GetProperty("kind").GetString() == "ew")
        {
            byte[] raw = Convert.FromHexString(c.GetProperty("hex").GetString()!);
            string text = Rfc2047.DecodeText(options, raw);
            result = $"{{\"id\":{id},\"r\":\"{Convert.ToHexString(Encoding.UTF8.GetBytes(text)).ToLowerInvariant()}\"}}";
        }
        else
        {
            byte[] hdr = Encoding.ASCII.GetBytes(c.GetProperty("hdr").GetString()!);
            string want = c.GetProperty("want").GetString()!;
            if (!ContentDisposition.TryParse(options, hdr, out ContentDisposition? cd))
            {
                result = $"{{\"id\":{id},\"e\":\"parse\"}}";
            }
            else if (cd.Parameters.TryGetValue(want, out string? v))
            {
                result = $"{{\"id\":{id},\"r\":\"{Convert.ToHexString(Encoding.UTF8.GetBytes(v)).ToLowerInvariant()}\"}}";
            }
            else
            {
                result = $"{{\"id\":{id},\"n\":1}}";
            }
        }
    }
    catch (Exception e)
    {
        result = $"{{\"id\":{id},\"e\":\"{e.GetType().Name}\"}}";
    }

    output.WriteLine(result);
}
