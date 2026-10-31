using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using RecursiveAI.OneMail.Text;

// Prints one JSON line per case (format as in py_oracle.py), plus "i": the issue kinds raised, so compare.py can
// attribute a difference to a documented resolution.
//   ew:    Rfc2047.DecodeText
//   param: ParameterCollector.Resolve
using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
foreach (string line in File.ReadLines(args[0]))
{
    using JsonDocument doc = JsonDocument.Parse(line);
    JsonElement c = doc.RootElement;
    int id = c.GetProperty("id").GetInt32();
    var issues = new List<TextIssue>();
    string? text = null;
    string error = "";
    try
    {
        if (c.GetProperty("kind").GetString() == "cs")
        {
            byte[] raw = Convert.FromHexString(c.GetProperty("hex").GetString()!);
            string codec = c.GetProperty("codec").GetString()!;
            if (codec == "imap")
            {
                text = ImapUtf7.Decode(raw, issues);
            }
            else if (codec == "imap-encode")
            {
                text = ImapUtf7.Encode(Encoding.UTF8.GetString(raw));
                output.WriteLine($"{{\"id\":{id},\"r\":\"{Convert.ToHexString(Encoding.ASCII.GetBytes(text)).ToLowerInvariant()}\",\"i\":\"\"}}");
                continue;
            }
            else
            {
                text = MailEncoding.ForLabel(c.GetProperty("label").GetString()!)!.GetString(raw, issues);
            }
        }
        else if (c.GetProperty("kind").GetString() == "ew")
        {
            text = Rfc2047.DecodeText(Convert.FromHexString(c.GetProperty("hex").GetString()!), issues);
        }
        else
        {
            var collector = new ParameterCollector();
            int n = 0;
            foreach (JsonElement p in c.GetProperty("params").EnumerateArray())
            {
                collector.Add(Encoding.ASCII.GetBytes(p[0].GetString()!), Convert.FromHexString(p[1].GetString()!), n++);
            }

            string want = c.GetProperty("want").GetString()!;
            foreach (DecodedParameter d in collector.Resolve(issues))
            {
                if (string.Equals(d.Name, want, StringComparison.OrdinalIgnoreCase))
                {
                    text = d.Value;
                }
            }
        }
    }
    catch (Exception e)
    {
        error = e.GetType().Name;
    }

    string kinds = string.Join(",", issues.Select(i => i.Kind.ToString()).Distinct());
    string body = error.Length > 0 ? $"\"e\":\"{error}\"" : text is null ? "\"n\":1" : $"\"r\":\"{Convert.ToHexString(Encoding.UTF8.GetBytes(text)).ToLowerInvariant()}\"";
    output.WriteLine($"{{\"id\":{id},{body},\"i\":\"{kinds}\"}}");
}
