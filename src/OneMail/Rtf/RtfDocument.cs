using System.Text;

namespace RecursiveAI.OneMail.Rtf;

// The model between the RTF parser and the HTML and text writers: blocks of paragraphs and tables, paragraphs as
// segments of one shared text buffer, each segment pointing at an interned character style.

internal enum SegKind : byte
{
    Text,
    Tab,
    LineBreak,
    Image,          // Start = index into RtfDocument.Images (a placeholder in the HTML when the picture is not displayable)
    Attachment,     // Start = ordinal of the \objattph position
    HorizontalRule,
}

internal readonly record struct Seg(SegKind Kind, int Start, int Length, int Style);

// Underline: 0 none, 1 single (any line style), 2 double. Script: 0 none, 1 superscript, 2 subscript.
// Color and Background are 0xRRGGBB, or -1 for the document's automatic colour.
internal readonly record struct RunStyle(
    bool Bold, bool Italic, byte Underline, bool Strike, byte Script, bool Caps, bool SmallCaps,
    int Font, int HalfPoints, int Color, int Background, string? Href);

internal enum Align : byte
{
    Left,
    Center,
    Right,
    Justify,
}

internal enum ListKind : byte
{
    None,
    Bullet,
    Decimal,
    UpperRoman,
    LowerRoman,
    UpperAlpha,
    LowerAlpha,
}

internal abstract class Block
{
}

internal sealed class Paragraph : Block
{
    public List<Seg> Segs { get; init; } = [];

    public Align Align { get; init; }

    public int Left { get; init; }

    public int Right { get; init; }

    public int FirstLine { get; init; }

    public int SpaceBefore { get; init; }

    public int SpaceAfter { get; init; }

    public int LineSpacing { get; init; }

    public bool LineSpacingMultiple { get; init; }

    public int Heading { get; init; }

    // The list this paragraph belongs to: Ls from \ls, or a negative key made from an old-style \pn group. -1 = not a list item.
    public int ListKey { get; init; } = -1;

    public int ListLevel { get; init; }

    public bool PnBullet { get; init; }

    public ListKind PnKind { get; init; }

    public int PnStart { get; init; } = 1;

    // Filled by the list pass.
    public ListKind Kind { get; set; }

    public int Number { get; set; }

    public bool IsEmpty => Segs.Count == 0;
}

internal struct Border
{
    public byte Width;     // twips (\brdrw); 0 = none
    public int Color;      // 0xRRGGBB or -1
}

internal sealed class TableCell
{
    public List<Block> Blocks { get; } = [];

    public int Right { get; set; } = int.MinValue; // right boundary in twips (\cellx); MinValue = not defined
    public int Width { get; set; }
    public bool MergeFirst { get; set; }
    public bool MergeContinue { get; set; }
    public bool VMergeFirst { get; set; }
    public bool VMergeContinue { get; set; }
    public int ColSpan { get; set; } = 1;
    public int RowSpan { get; set; } = 1;
    public bool Hidden { get; set; }
    public TableCell? Owner { get; set; }
    public byte VAlign { get; set; } // 0 top (default), 1 center, 2 bottom
    public int Shading { get; set; } = -1;
    public Border Top;
    public Border Left;
    public Border Bottom;
    public Border Right_;
}

internal sealed class TableRow
{
    public List<TableCell> Cells { get; } = [];

    public bool Header { get; set; }
}

internal sealed class Table : Block
{
    public List<TableRow> Rows { get; } = [];
}

internal sealed class FontInfo
{
    public string Name = "";
    public int CodePage; // 0 = the document default; -2 = symbol font
    public byte Family;  // 0 none, 1 roman, 2 swiss, 3 modern, 4 script, 5 decor, 6 tech
}

internal sealed class ListLevelInfo
{
    public int Nfc;
    public int Start = 1;
}

internal sealed class ListDef
{
    public int ListId;
    public List<ListLevelInfo> Levels { get; } = [];
}

internal sealed class RtfDocument
{
    public List<Block> Blocks { get; } = [];

    public StringBuilder Text { get; } = new();

    public List<RunStyle> Styles { get; } = [];

    public List<RtfImage> Images { get; } = [];

    public Dictionary<int, FontInfo> Fonts { get; } = [];

    public int DefaultFont { get; set; }

    public int AttachmentPlaceholders { get; set; }

    public int DefaultCodePage { get; set; } = 1252;
}
