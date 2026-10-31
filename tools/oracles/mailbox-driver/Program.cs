using MimeKit;

// Usage: MailboxDump <mbox file>...
// For each file prints "<path>\t<count>" then one line per message: marker offset, marker length, position after the
// message (all in bytes), tab separated. Uses only MimeKit's public MimeParser API in its mbox mode.
foreach (string path in args)
{
    var lines = new List<string>();
    try
    {
        using FileStream fs = File.OpenRead(path);
        var parser = new MimeParser(fs, MimeFormat.Mbox);
        while (!parser.IsEndOfStream)
        {
            parser.ParseMessage();
            lines.Add($"{parser.MboxMarkerOffset}\t{parser.MboxMarker?.Length ?? 0}\t{parser.Position}");
        }
    }
    catch (Exception e)
    {
        lines.Add("error\t" + e.GetType().Name + "\t" + e.Message.Replace('\n', ' '));
    }

    Console.WriteLine($"{path}\t{lines.Count}");
    foreach (string l in lines)
    {
        Console.WriteLine(l);
    }
}
