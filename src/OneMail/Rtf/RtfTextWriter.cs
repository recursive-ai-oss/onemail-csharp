using System.Globalization;
using System.Text;

namespace RecursiveAI.OneMail.Rtf;

// Plain-text rendering of an RtfDocument: one line per paragraph, built from the same model as the HTML.
internal static class RtfTextWriter
{
    internal static string Write(RtfDocument doc)
    {
        var sb = new StringBuilder();
        WriteBlocks(doc, doc.Blocks, sb);
        return sb.ToString();
    }

    private static void WriteBlocks(RtfDocument doc, List<Block> blocks, StringBuilder sb)
    {
        foreach (Block b in blocks)
        {
            switch (b)
            {
                case Paragraph p:
                    sb.Append(Line(doc, p)).Append("\r\n");
                    break;
                case Table t:
                    foreach (TableRow row in t.Rows)
                    {
                        bool firstCell = true;
                        foreach (TableCell c in row.Cells)
                        {
                            if (c.Hidden)
                            {
                                continue;
                            }

                            if (!firstCell)
                            {
                                sb.Append('\t');
                            }

                            firstCell = false;
                            var cell = new StringBuilder();
                            WriteBlocks(doc, c.Blocks, cell);
                            sb.Append(cell.ToString().Trim().Replace("\r\n", " ").Replace('\t', ' '));
                        }

                        sb.Append("\r\n");
                    }

                    break;
            }
        }
    }

    private static string Line(RtfDocument doc, Paragraph p)
    {
        var sb = new StringBuilder();
        if (p.Kind != ListKind.None && p.ListKey != -1)
        {
            sb.Append(' ', 2 * p.ListLevel).Append(Label(p)).Append(' ');
        }

        int i = 0;
        while (i < p.Segs.Count)
        {
            Seg s = p.Segs[i];
            string? href = doc.Styles[s.Style].Href;
            int linkStart = sb.Length;
            // A link's text is followed by its target, unless the text already is the target.
            int j = i;
            do
            {
                Append(doc, p.Segs[j], sb);
                j++;
            }
            while (href is not null && j < p.Segs.Count && doc.Styles[p.Segs[j].Style].Href == href);

            if (href is not null)
            {
                string shown = sb.ToString(linkStart, sb.Length - linkStart).Trim();
                string bare = href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? href[7..] : href;
                if (shown.Length > 0 && !string.Equals(shown, href, StringComparison.OrdinalIgnoreCase) && !string.Equals(shown, bare, StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append(" <").Append(href).Append('>');
                }
            }

            i = j;
        }

        return sb.ToString();
    }

    private static void Append(RtfDocument doc, Seg s, StringBuilder sb)
    {
        switch (s.Kind)
        {
            case SegKind.Text:
                sb.Append(doc.Text.Substring(s.Start, s.Length).Replace('\u00A0', ' '));
                break;
            case SegKind.Tab:
                sb.Append('\t');
                break;
            case SegKind.LineBreak:
                sb.Append("\r\n");
                break;
            case SegKind.HorizontalRule:
                sb.Append("----");
                break;
            case SegKind.Image:
                RtfImage image = doc.Images[s.Start];
                if (!image.IsDisplayable)
                {
                    string format = image.ContentType.StartsWith("image/x-", StringComparison.Ordinal) ? image.ContentType[8..] : "unknown";
                    sb.Append('[').Append(format).Append(" picture]");
                }

                break;
        }
    }

    private static string Label(Paragraph p) => p.Kind switch
    {
        ListKind.Bullet => "\u2022",
        ListKind.Decimal => p.Number.ToString(CultureInfo.InvariantCulture) + ".",
        ListKind.LowerAlpha => Alpha(p.Number, 'a') + ".",
        ListKind.UpperAlpha => Alpha(p.Number, 'A') + ".",
        ListKind.LowerRoman => Roman(p.Number).ToLowerInvariant() + ".",
        ListKind.UpperRoman => Roman(p.Number) + ".",
        _ => "",
    };

    private static string Alpha(int n, char a)
    {
        if (n < 1)
        {
            return n.ToString(CultureInfo.InvariantCulture);
        }

        var sb = new StringBuilder();
        while (n > 0)
        {
            n--;
            sb.Insert(0, (char)(a + n % 26));
            n /= 26;
        }

        return sb.ToString();
    }

    private static string Roman(int n)
    {
        if (n < 1 || n > 3999)
        {
            return n.ToString(CultureInfo.InvariantCulture);
        }

        int[] values = [1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1];
        string[] symbols = ["M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I"];
        var sb = new StringBuilder();
        for (int i = 0; i < values.Length; i++)
        {
            while (n >= values[i])
            {
                sb.Append(symbols[i]);
                n -= values[i];
            }
        }

        return sb.ToString();
    }
}
