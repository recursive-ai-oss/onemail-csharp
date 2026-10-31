namespace RecursiveAI.OneMail;

/// <summary>
/// Guesses an attachment's media type from its file name's extension, for attachments that carry no usable stored type
/// (an absent or <c>application/octet-stream</c> type). A fixed table: it never asks the operating system, so the answer
/// is the same on every platform and in the browser.
/// </summary>
internal static class AttachmentMediaTypes
{
    private const string Octet = "application/octet-stream";

    /// <summary>True when <paramref name="stored"/> is missing or the generic <c>application/octet-stream</c>, so a better type may be guessed.</summary>
    internal static bool IsGeneric(string? stored)
        => string.IsNullOrWhiteSpace(stored) || stored.Trim().Equals(Octet, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The type for <paramref name="fileName"/> if its extension is in the table, else null. Only the last extension counts,
    /// case-insensitively; a name with no extension, or an unknown one, gives null.
    /// </summary>
    internal static string? Infer(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return null;
        }

        // A path separator may precede the name when a producer stored a full path.
        ReadOnlySpan<char> name = fileName;
        int slash = name.LastIndexOfAny('/', '\\');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        int dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1)
        {
            return null;
        }

        ReadOnlySpan<char> ext = name[(dot + 1)..];
        if (ext.Length > 8)
        {
            return null;
        }

        Span<char> lower = stackalloc char[8];
        ext.ToLowerInvariant(lower);
        return Table.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(lower[..ext.Length], out string? type) ? type : null;
    }

    private static readonly Dictionary<string, string> Table = Build();

    private static Dictionary<string, string> Build()
    {
        Dictionary<string, string> t = new(StringComparer.Ordinal);
        void Add(string type, string extensions)
        {
            foreach (string e in extensions.Split(' '))
            {
                t[e] = type;
            }
        }

        // Documents
        Add("application/pdf", "pdf");
        Add("application/msword", "doc dot");
        Add("application/vnd.openxmlformats-officedocument.wordprocessingml.document", "docx");
        Add("application/vnd.openxmlformats-officedocument.wordprocessingml.template", "dotx");
        Add("application/vnd.ms-word.document.macroenabled.12", "docm");
        Add("application/vnd.ms-excel", "xls xlt");
        Add("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "xlsx");
        Add("application/vnd.openxmlformats-officedocument.spreadsheetml.template", "xltx");
        Add("application/vnd.ms-excel.sheet.macroenabled.12", "xlsm");
        Add("application/vnd.ms-excel.sheet.binary.macroenabled.12", "xlsb");
        Add("application/vnd.ms-powerpoint", "ppt pot pps");
        Add("application/vnd.openxmlformats-officedocument.presentationml.presentation", "pptx");
        Add("application/vnd.openxmlformats-officedocument.presentationml.slideshow", "ppsx");
        Add("application/vnd.ms-powerpoint.presentation.macroenabled.12", "pptm");
        Add("application/vnd.ms-outlook", "msg");
        Add("application/vnd.ms-project", "mpp");
        Add("application/vnd.visio", "vsd vsdx");
        Add("application/vnd.oasis.opendocument.text", "odt");
        Add("application/vnd.oasis.opendocument.spreadsheet", "ods");
        Add("application/vnd.oasis.opendocument.presentation", "odp");
        Add("application/rtf", "rtf");
        Add("application/ms-tnef", "tnef");
        Add("application/postscript", "ps eps ai");
        Add("application/epub+zip", "epub");

        // Text
        Add("text/plain", "txt text log");
        Add("text/csv", "csv");
        Add("text/tab-separated-values", "tsv");
        Add("text/html", "html htm xhtml");
        Add("text/css", "css");
        Add("text/markdown", "md markdown");
        Add("text/xml", "xml");
        Add("application/json", "json");
        Add("application/javascript", "js");
        Add("application/yaml", "yaml yml");

        // Calendar, contacts, messages
        Add("text/calendar", "ics ical ifb");
        Add("text/vcard", "vcf vcard");
        Add("message/rfc822", "eml");
        Add("application/mbox", "mbox");

        // Images
        Add("image/jpeg", "jpg jpeg jpe jfif");
        Add("image/png", "png");
        Add("image/gif", "gif");
        Add("image/bmp", "bmp");
        Add("image/tiff", "tif tiff");
        Add("image/webp", "webp");
        Add("image/svg+xml", "svg");
        Add("image/x-icon", "ico");
        Add("image/heic", "heic");
        Add("image/avif", "avif");

        // Audio and video
        Add("audio/mpeg", "mp3");
        Add("audio/wav", "wav");
        Add("audio/mp4", "m4a");
        Add("audio/ogg", "ogg oga");
        Add("audio/flac", "flac");
        Add("audio/aac", "aac");
        Add("video/mp4", "mp4 m4v");
        Add("video/quicktime", "mov");
        Add("video/x-msvideo", "avi");
        Add("video/mpeg", "mpg mpeg");
        Add("video/webm", "webm");
        Add("video/x-ms-wmv", "wmv");

        // Archives
        Add("application/zip", "zip");
        Add("application/gzip", "gz");
        Add("application/x-tar", "tar");
        Add("application/x-7z-compressed", "7z");
        Add("application/vnd.rar", "rar");
        Add("application/x-bzip2", "bz2");

        return t;
    }
}
