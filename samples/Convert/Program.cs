using System;
using System.IO;
using RecursiveAI.OneMail;
using RecursiveAI.OneMail.Conversion;
using RecursiveAI.OneMail.Conversion.Stores;

// Task 3, convert: `Convert [source destination-folder]`. The source can be any store (PST to mbox, MSG to EML, mbox to PST ...).
// Without arguments it converts a two-message mbox made in the temp folder to a Maildir and to a PST.
string dir = Path.Combine(Path.GetTempPath(), "onemail-sample-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
try
{
    string source = args.Length > 0 ? args[0] : Path.Combine(dir, "in.mbox");
    if (args.Length == 0) File.WriteAllText(source, "From a@example.com Mon Jan  1 00:00:00 2024\nSubject: First\n\nhello\n\nFrom b@example.com Tue Jan  2 00:00:00 2024\nSubject: Second\n\nworld\n");
    string target = args.Length > 1 ? args[1] : dir;

    // 1. A whole store to another format, with the loss report: what was read, written, skipped, and what did not carry over.
    StoreConversionReport report = await Mail.ConvertAsync(source, ConvertFormat.Pst, Path.Combine(target, "out.pst"));
    Console.WriteLine($"read {report.ItemsRead}, wrote {report.ItemsWritten}, {report.ItemsWithContentLoss} with content loss");
    foreach (LossTotal loss in report.Losses) Console.WriteLine($"  {loss.Family}/{loss.Code}: {loss.Items} items ({loss.Kind})");

    // 2. One item to the bytes of a format, with its own loss report.
    using MailContainer store = Mail.Open(source);
    foreach (IMailItem item in store.Enumerate())
    {
        ConversionResult<byte[]?> eml = Mail.Convert(item, ConvertFormat.Eml);
        Console.WriteLine($"{item.Subject}: {eml.Value?.Length} bytes, {eml.Report.Count} losses");
    }
}
finally
{
    Directory.Delete(dir, recursive: true);
}
