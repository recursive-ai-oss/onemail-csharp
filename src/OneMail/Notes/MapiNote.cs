using System.Text;
using RecursiveAI.OneMail.Mapi;

namespace RecursiveAI.OneMail.Notes;

/// <summary>The suggested background colour of a note (MS-OXONOTE 2.2.1.1).</summary>
public enum NoteColor
{
    /// <summary>Blue (0).</summary>
    Blue = 0,

    /// <summary>Green (1).</summary>
    Green = 1,

    /// <summary>Pink (2).</summary>
    Pink = 2,

    /// <summary>Yellow (3), Outlook's default.</summary>
    Yellow = 3,

    /// <summary>White (4).</summary>
    White = 4,
}

/// <summary>A typed view over a sticky note item (<c>IPM.StickyNote</c>, MS-OXONOTE) with a factory that writes one.</summary>
public sealed class MapiNote
{
    private static readonly MapiNamedProperty ColorName = new(MapiPropertySets.Note, 0x8B00u);
    private static readonly MapiNamedProperty WidthName = new(MapiPropertySets.Note, 0x8B02u);
    private static readonly MapiNamedProperty HeightName = new(MapiPropertySets.Note, 0x8B03u);
    private static readonly MapiNamedProperty XName = new(MapiPropertySets.Note, 0x8B04u);
    private static readonly MapiNamedProperty YName = new(MapiPropertySets.Note, 0x8B05u);

    private readonly MapiMessage _m;

    /// <summary>Wraps a message.</summary>
    /// <param name="message">A note message.</param>
    public MapiNote(MapiMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _m = message;
    }

    /// <summary>The message behind the view.</summary>
    public MapiMessage Message => _m;

    /// <summary>The note's text (PidTagBody).</summary>
    public string? Text => _m.Body;

    /// <summary>The colour, or null when absent or not one of the five defined values.</summary>
    public NoteColor? Color => Int(ColorName) is >= 0 and <= 4 and int c ? (NoteColor)c : null;

    /// <summary>The width of the note's window in pixels, or null.</summary>
    public int? Width => Int(WidthName);

    /// <summary>The height of the note's window in pixels, or null.</summary>
    public int? Height => Int(HeightName);

    /// <summary>The distance in pixels from the left edge of the screen, or null.</summary>
    public int? Left => Int(XName);

    /// <summary>The distance in pixels from the top edge of the screen, or null.</summary>
    public int? Top => Int(YName);

    private int? Int(MapiNamedProperty n) => _m.Properties.Get(n, MapiType.Integer32)?.AsInt32();

    /// <summary>
    /// Builds a note (MS-OXONOTE 2.2): <c>IPM.StickyNote</c>, the text in PidTagBody, the subject and PidTagNormalizedSubject as the first line (what Outlook
    /// shows as the note's title), the colour, the window size and position, and PidTagIconIndex as 0x300 plus the colour (section 2.2.2.2).
    /// </summary>
    /// <param name="text">The text. A note has no recipients and no attachments.</param>
    /// <param name="color">The colour. Default yellow.</param>
    /// <param name="width">The window width in pixels, above zero. Default 200.</param>
    /// <param name="height">The window height in pixels, above zero. Default 166.</param>
    /// <param name="left">The distance from the left of the screen. Default 110.</param>
    /// <param name="top">The distance from the top of the screen. Default 110.</param>
    /// <returns>The item.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The colour is not one of the five, or a size is not above zero.</exception>
    public static MapiMessage Create(string text, NoteColor color = NoteColor.Yellow, int width = 200, int height = 166, int left = 110, int top = 110)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (color is < NoteColor.Blue or > NoteColor.White)
        {
            throw new ArgumentOutOfRangeException(nameof(color));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        int newline = text.AsSpan().IndexOfAny('\r', '\n');
        string title = newline < 0 ? text : text[..newline];
        var m = new MapiMessage();
        m.Properties.Add(Str(MapiPid.MessageClass, "IPM.StickyNote"));
        m.Properties.Add(Str(MapiPid.Subject, title));
        m.Properties.Add(Str(MapiPid.NormalizedSubject, title));
        m.Properties.Add(Str(MapiPid.Body, text));
        m.Properties.Add(MapiProperty.CreateOwned(new MapiTag(0x1080, MapiType.Integer32), BitConverter.GetBytes(0x300 + (int)color)));
        m.Properties.Add(Named(ColorName, (int)color));
        m.Properties.Add(Named(WidthName, width));
        m.Properties.Add(Named(HeightName, height));
        m.Properties.Add(Named(XName, left));
        m.Properties.Add(Named(YName, top));
        return m;
    }

    private static MapiProperty Str(ushort id, string v) => MapiProperty.CreateOwned(new MapiTag(id, MapiType.UnicodeString), Encoding.Unicode.GetBytes(v));

    private static MapiProperty Named(MapiNamedProperty n, int v) => MapiProperty.CreateOwned(new MapiTag(0x8000, MapiType.Integer32), BitConverter.GetBytes(v), n);
}
