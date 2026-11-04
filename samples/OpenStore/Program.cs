using System;
using System.IO;
using RecursiveAI.OneMail;

// Task 2, open any store and enumerate its items: `OpenStore [path]`, where path is a PST, OST, mbox, Maildir, OLM, DBX, MSG or EML file or a folder
// of them; the content decides, not the extension. Without an argument it makes a two-message mbox in the temp folder.
string path = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "onemail-sample-" + Guid.NewGuid().ToString("N") + ".mbox");
bool made = args.Length == 0;
if (made)
{
    File.WriteAllText(path, "From a@example.com Mon Jan  1 00:00:00 2024\nSubject: First\n\nhello\n\nFrom b@example.com Tue Jan  2 00:00:00 2024\nSubject: Second\n\nworld\n");
}

try
{
    // 1. Open: one call for every format. Items are read one at a time, so a 50 GB store needs no more memory than a small one.
    await using MailContainer store = Mail.Open(path);

    // 2. Enumerate with await foreach. Provenance says where each item sits in the file (folder, byte offset).
    await foreach (IMailItem item in store) Console.WriteLine($"{store.Format} {item.Provenance.Folder} #{item.Provenance.Index}: {item.Subject}");
}
finally
{
    if (made) File.Delete(path);
}
