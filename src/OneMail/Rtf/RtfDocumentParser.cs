using System.Globalization;
using System.Text;

namespace RecursiveAI.OneMail.Rtf;

// Reads an RTF document into an RtfDocument (RTF 1.9.1). Group state (character and paragraph properties, the current
// destination) is saved on "{" and restored on "}", as the specification says.
internal sealed class RtfDocumentParser
{
    private enum Dest : byte
    {
        Normal,
        Skip,
        FontTable,
        ColorTable,
        StyleSheet,
        ListTable,
        ListOverrideTable,
        FieldInst,
        Pict,
        PicProp,
        SpName,
        SpValue,
        Pn,
    }

    private struct GState
    {
        public Dest Dest;
        public bool Star;
        public bool Fresh;
        public bool PictRoot;
        public bool SpRoot;
        public bool Bold, Italic, Strike, Caps, SmallCaps, Hidden, DoubleByte;
        public byte Underline, Script;
        public int Font, AssocFont, HalfPoints, Color, Highlight, CharBack, Uc;
        public string? Href;
        public Align Align;
        public int Left, Right, FirstLine, SpaceBefore, SpaceAfter, LineSpacing, ListKey, ListLevel, Style, Itap;
        public bool LineMultiple, InTable;
    }

    private enum W
    {
        Unknown, B, I, Ul, UlNone, Strike, Super, Sub, NoSuperSub, Caps, SmallCaps, V, F, Af, Fs, Cf, Highlight, Cb, ChCbPat, Plain,
        Loch, Hich, Dbch, Uc, U, Pard, Par, Line, Tab, Qc, Ql, Qr, Qj, Li, Ri, Fi, Sb, Sa, Sl, SlMult, S, Ls, Ilvl, InTbl, Itap,
        TrOwd, CellX, Cell, Row, NestCell, NestRow, ClMgf, ClMrg, ClVmgf, ClVmrg, ClVertAlt, ClVertAlc, ClVertAlb, ClCbPat,
        ClBrdrT, ClBrdrL, ClBrdrB, ClBrdrR, TrBrdrT, TrBrdrL, TrBrdrB, TrBrdrR, TrBrdrH, TrBrdrV, BrdrNone, BrdrLine, BrdrW, BrdrCf, ParaBorder,
        TrHdr, TrLeft, Char, ObjAttPh, Bin,
        AnsiCpg, Ansi, Mac, Pc, Pca, Deff, Red, Green, Blue,
        FNil, FRoman, FSwiss, FModern, FScript, FDecor, FTech, FCharset, Cpg,
        Fonttbl, Colortbl, Stylesheet, Listtable, ListOverrideTable, FldInst, FldRslt, Field, Pict, PicProp, Sp, Sn, Sv, Pn, ShpPict,
        PngBlip, JpegBlip, EmfBlip, WMetafile, DibBitmap, WBitmap, MacPict, PmMetafile, PicW, PicH, PicWGoal, PicHGoal, PicScaleX, PicScaleY,
        List, ListId, ListLevel, LevelNfc, LevelStartAt, ListOverride, LfoLevel, ListOverrideStartAt, Shp, ShpInst, ShpRslt, PnLvl, PnLvlBlt, PnLvlBody, PnDec, PnUcLtr, PnLcLtr, PnUcRm, PnLcRm, PnStart,
    }

    private static readonly Dictionary<string, W> Words = BuildWords();
    private static readonly Dictionary<string, W>.AlternateLookup<ReadOnlySpan<char>> WordLookup = Words.GetAlternateLookup<ReadOnlySpan<char>>();

    private static readonly Dictionary<string, char> CharWords = new()
    {
        ["bullet"] = '\u2022', ["endash"] = '\u2013', ["emdash"] = '\u2014', ["lquote"] = '\u2018', ["rquote"] = '\u2019',
        ["ldblquote"] = '\u201C', ["rdblquote"] = '\u201D', ["enspace"] = '\u2002', ["emspace"] = '\u2003', ["qmspace"] = '\u2005',
        ["zwj"] = '\u200D', ["zwnj"] = '\u200C', ["ltrmark"] = '\u200E', ["rtlmark"] = '\u200F',
    };

    private static readonly Dictionary<string, char>.AlternateLookup<ReadOnlySpan<char>> CharLookup = CharWords.GetAlternateLookup<ReadOnlySpan<char>>();

    // Destinations whose content is not body text (RTF 1.9.1 and what Outlook and Word write). A group that starts with one of these is skipped,
    // and so is every other group that starts with \* and is not in the table of destinations this reader uses.
    private static readonly HashSet<string> SkipDestinations = new(StringComparer.Ordinal)
    {
        "info", "header", "headerl", "headerr", "headerf", "footer", "footerl", "footerr", "footerf", "footnote", "private", "xmlnstbl",
        "mmathPr", "pgdsctbl", "listtext", "pntext", "pntxta", "pntxtb", "leveltext", "levelnumbers", "listname", "nonshppict", "bkmkstart",
        "bkmkend", "annotation", "ftnsep", "ftnsepc", "aftnsep", "aftnsepc", "ftncn", "aftncn", "fchars", "lchars", "background", "revtbl",
        "listpicture", "pgptbl", "upr", "title", "subject", "author", "manager", "company", "operator", "keywords", "comment", "doccomm",
        "category", "hlinkbase", "creatim", "revtim", "printim", "buptim", "stylerestrictions", "listoverridetable_",
        "objdata", "falt", "panose", "fontemb", "fontfile", "datafield", "fldtype", "nesttableprops", "nonesttables", "defchp", "defpap",
        "generator", "filetbl", "rsidtbl", "themedata", "colorschememapping", "latentstyles", "datastore", "wgrffmtfilter", "template",
        "xmlopen", "xmlattrname", "xmlattrvalue", "xmlclose", "xmlname", "txe", "tc", "tcn", "xe", "rxe", "ixe", "pxe", "bxe", "ixe_",
        "protusertbl", "userprops", "propname", "staticval", "linkval", "objclass", "objname", "result_", "ffdeftext", "ffl", "ffname",
        "ffformat", "ffhelptext", "ffstattext", "ffentrymcr", "ffexitmcr", "formfield",
    };

    private readonly RtfHtmlOptions _options;
    private readonly RtfDefectList _defects;
    private readonly RtfDocument _doc = new();
    private readonly List<GState> _stack = [];
    private GState _cur;

    private readonly Dictionary<RunStyle, int> _styleIndex = [];
    private List<Seg> _segs = [];
    private readonly char[] _nameBuffer = new char[32];
    private readonly Dictionary<int, Encoding?> _encodings = [];
    private readonly List<byte> _bytes = [];
    private int _bytesCodePage;
    private char _pendingHigh;
    private int _skip;
    private int _nodes;
    private bool _stopped;
    private bool _rootClosed;
    private int _offset;

    private int _explicitCodePage;
    private int _impliedCodePage;

    // Font table.
    private FontInfo? _curFont;
    private readonly StringBuilder _fontName = new();
    private bool _fontNameDone;

    // Colour table.
    private readonly List<int> _colors = [];
    private int _colorR, _colorG, _colorB;
    private bool _colorAny;

    // Style sheet.
    private readonly Dictionary<int, string> _styleNames = [];
    private int _curStyleNumber = -1;
    private readonly StringBuilder _styleName = new();
    private bool _styleNameDone;

    // Lists.
    private readonly Dictionary<int, ListDef> _lists = [];
    private ListDef? _curList;
    private ListLevelInfo? _curLevel;
    private readonly Dictionary<int, (int ListId, Dictionary<int, int> Starts)> _overrides = [];
    private int _ovListId;
    private Dictionary<int, int>? _ovStarts;
    private int _ovLevelIndex = -1;
    private bool _reportedUndefinedList;

    // Old-style paragraph numbering (\pn).
    private bool _pnActive;
    private bool _pnBullet;
    private ListKind _pnKind;
    private int _pnLevel;
    private int _pnStart = 1;

    // Fields.
    private readonly StringBuilder _fldInst = new();

    // Pictures.
    private PictState? _pict;
    private readonly StringBuilder _spName = new();
    private readonly StringBuilder _spValue = new();
    private int _totalPictureBytes;

    // Tables.
    private readonly List<TableCell> _defs = [];
    private TableCell _pendDef = new();
    private int _trLeft;
    private bool _trHeader;
    private int _borderTarget = -1;
    private readonly Border[] _rowBorders = new Border[6]; // top, left, bottom, right, horizontal, vertical
    private List<Block> _cellBlocks = [];
    private readonly List<TableCell> _rowCells = [];
    private Table? _table;
    private bool _reportedNested;

    private sealed class PictState
    {
        public string Format = "";
        public int W, H, WGoal, HGoal, ScaleX = 100, ScaleY = 100;
        public readonly List<byte> Data = [];
        public int Nibble = -1;
        public bool Overflow, Bad, BadHex;
        public bool HorizontalRule;
        public string? Description;
    }

    private RtfDocumentParser(RtfHtmlOptions options, RtfDefectList defects)
    {
        _options = options;
        _defects = defects;
    }

    private static Dictionary<string, W> BuildWords()
    {
        var d = new Dictionary<string, W>(StringComparer.Ordinal);
        void Add(W w, params string[] names)
        {
            foreach (string n in names)
            {
                d[n] = w;
            }
        }

        Add(W.B, "b"); Add(W.I, "i"); Add(W.Strike, "strike", "striked");
        Add(W.Ul, "ul", "uld", "uldash", "uldashd", "uldashdd", "uldb", "ulhwave", "ulldash", "ulth", "ulthd", "ulthdash", "ulthdashd", "ulthdashdd", "ulthldash", "ululdbwave", "ulw", "ulwave");
        Add(W.UlNone, "ulnone"); Add(W.Super, "super"); Add(W.Sub, "sub"); Add(W.NoSuperSub, "nosupersub");
        Add(W.Caps, "caps"); Add(W.SmallCaps, "scaps"); Add(W.V, "v"); Add(W.F, "f"); Add(W.Af, "af"); Add(W.Fs, "fs");
        Add(W.Cf, "cf"); Add(W.Highlight, "highlight"); Add(W.Cb, "cb"); Add(W.ChCbPat, "chcbpat"); Add(W.Plain, "plain");
        Add(W.Loch, "loch", "hich"); Add(W.Dbch, "dbch"); Add(W.Uc, "uc"); Add(W.U, "u");
        Add(W.Pard, "pard"); Add(W.Par, "par"); Add(W.Line, "line"); Add(W.Tab, "tab");
        Add(W.Qc, "qc"); Add(W.Ql, "ql"); Add(W.Qr, "qr"); Add(W.Qj, "qj", "qd");
        Add(W.Li, "li", "lin"); Add(W.Ri, "ri", "rin"); Add(W.Fi, "fi"); Add(W.Sb, "sb"); Add(W.Sa, "sa"); Add(W.Sl, "sl"); Add(W.SlMult, "slmult");
        Add(W.S, "s"); Add(W.Ls, "ls"); Add(W.Ilvl, "ilvl"); Add(W.InTbl, "intbl"); Add(W.Itap, "itap");
        Add(W.TrOwd, "trowd"); Add(W.CellX, "cellx"); Add(W.Cell, "cell"); Add(W.Row, "row"); Add(W.NestCell, "nestcell"); Add(W.NestRow, "nestrow");
        Add(W.ClMgf, "clmgf"); Add(W.ClMrg, "clmrg"); Add(W.ClVmgf, "clvmgf"); Add(W.ClVmrg, "clvmrg");
        Add(W.ClVertAlt, "clvertalt"); Add(W.ClVertAlc, "clvertalc"); Add(W.ClVertAlb, "clvertalb"); Add(W.ClCbPat, "clcbpat");
        Add(W.ClBrdrT, "clbrdrt"); Add(W.ClBrdrL, "clbrdrl"); Add(W.ClBrdrB, "clbrdrb"); Add(W.ClBrdrR, "clbrdrr");
        Add(W.TrBrdrT, "trbrdrt"); Add(W.TrBrdrL, "trbrdrl"); Add(W.TrBrdrB, "trbrdrb"); Add(W.TrBrdrR, "trbrdrr"); Add(W.TrBrdrH, "trbrdrh"); Add(W.TrBrdrV, "trbrdrv");
        Add(W.BrdrNone, "brdrnone", "brdrnil");
        Add(W.BrdrLine, "brdrs", "brdrth", "brdrsh", "brdrdb", "brdrdot", "brdrdash", "brdrhair", "brdrdashsm", "brdrdashd", "brdrdashdd", "brdrtriple", "brdrtnthsg", "brdrthtnsg", "brdrtnthtnsg", "brdrtnthmg", "brdrthtnmg", "brdrtnthtnmg", "brdrtnthlg", "brdrthtnlg", "brdrtnthtnlg", "brdrwavy", "brdrwavydb", "brdrdashdotstr", "brdremboss", "brdrengrave", "brdroutset", "brdrinset");
        Add(W.BrdrW, "brdrw"); Add(W.BrdrCf, "brdrcf");
        Add(W.ParaBorder, "brdrt", "brdrb", "brdrl", "brdrr", "box", "brdrbar");
        Add(W.TrHdr, "trhdr"); Add(W.TrLeft, "trleft");
        Add(W.ObjAttPh, "objattph"); Add(W.Bin, "bin");
        Add(W.AnsiCpg, "ansicpg"); Add(W.Ansi, "ansi"); Add(W.Mac, "mac"); Add(W.Pc, "pc"); Add(W.Pca, "pca"); Add(W.Deff, "deff");
        Add(W.Red, "red"); Add(W.Green, "green"); Add(W.Blue, "blue");
        Add(W.FNil, "fnil"); Add(W.FRoman, "froman"); Add(W.FSwiss, "fswiss"); Add(W.FModern, "fmodern"); Add(W.FScript, "fscript"); Add(W.FDecor, "fdecor"); Add(W.FTech, "ftech");
        Add(W.FCharset, "fcharset"); Add(W.Cpg, "cpg");
        Add(W.Fonttbl, "fonttbl"); Add(W.Colortbl, "colortbl"); Add(W.Stylesheet, "stylesheet"); Add(W.Listtable, "listtable"); Add(W.ListOverrideTable, "listoverridetable");
        Add(W.FldInst, "fldinst"); Add(W.FldRslt, "fldrslt"); Add(W.Field, "field"); Add(W.Pict, "pict"); Add(W.PicProp, "picprop"); Add(W.Sp, "sp");
        Add(W.Sn, "sn"); Add(W.Sv, "sv"); Add(W.Pn, "pn"); Add(W.ShpPict, "shppict"); Add(W.Shp, "shp"); Add(W.ShpInst, "shpinst"); Add(W.ShpRslt, "shprslt");
        Add(W.PngBlip, "pngblip"); Add(W.JpegBlip, "jpegblip"); Add(W.EmfBlip, "emfblip"); Add(W.WMetafile, "wmetafile"); Add(W.DibBitmap, "dibitmap");
        Add(W.WBitmap, "wbitmap"); Add(W.MacPict, "macpict"); Add(W.PmMetafile, "pmmetafile");
        Add(W.PicW, "picw"); Add(W.PicH, "pich"); Add(W.PicWGoal, "picwgoal"); Add(W.PicHGoal, "pichgoal"); Add(W.PicScaleX, "picscalex"); Add(W.PicScaleY, "picscaley");
        Add(W.List, "list"); Add(W.ListId, "listid"); Add(W.ListLevel, "listlevel"); Add(W.LevelNfc, "levelnfc", "levelnfcn"); Add(W.LevelStartAt, "levelstartat");
        Add(W.ListOverride, "listoverride"); Add(W.LfoLevel, "lfolevel"); Add(W.ListOverrideStartAt, "listoverridestartat");
        Add(W.PnLvl, "pnlvl"); Add(W.PnLvlBlt, "pnlvlblt"); Add(W.PnLvlBody, "pnlvlbody"); Add(W.PnDec, "pndec", "pncard", "pnord", "pnordt"); Add(W.PnUcLtr, "pnucltr");
        Add(W.PnLcLtr, "pnlcltr"); Add(W.PnUcRm, "pnucrm"); Add(W.PnLcRm, "pnlcrm"); Add(W.PnStart, "pnstart");
        return d;
    }

    internal static RtfDocument Parse(ReadOnlySpan<byte> data, RtfHtmlOptions options, RtfDefectList defects)
    {
        var p = new RtfDocumentParser(options, defects);
        p.Run(data);
        return p._doc;
    }

    private void Run(ReadOnlySpan<byte> data)
    {
        if (!StartsWithRtf(data))
        {
            _defects.Add(RtfDefectKind.NotRtf, 0, "the data does not start with {\\rtf");
            return;
        }

        var reader = new RtfReader(data, new RtfReaderOptions { MaxGroupDepth = _options.MaxGroupDepth });
        _cur = new GState { Font = -1, AssocFont = -1, Uc = 1, ListKey = -1, Style = -1, Color = 0 };
        _stack.Add(_cur);
        _doc.DefaultFont = 0;

        while (!_stopped && !_rootClosed && reader.Read())
        {
            _offset = reader.Offset;
            switch (reader.Kind)
            {
                case RtfTokenKind.GroupStart:
                    FlushBytes();
                    _skip = 0;
                    _stack.Add(_cur);
                    _cur.Fresh = true;
                    _cur.Star = false;
                    _cur.PictRoot = false;
                    _cur.SpRoot = false;
                    break;
                case RtfTokenKind.GroupEnd:
                    FlushBytes();
                    _skip = 0;
                    PopGroup();
                    if (_stack.Count == 1)
                    {
                        _rootClosed = true; // the root group is closed: anything after it (padding, stray bytes) is not part of the document
                    }

                    break;
                case RtfTokenKind.ControlWord:
                    ControlWord(ref reader);
                    break;
                case RtfTokenKind.ControlSymbol:
                    ControlSymbol(reader.Name.Length > 0 ? reader.Name[0] : (byte)0);
                    break;
                case RtfTokenKind.HexEscape:
                    _cur.Fresh = false;
                    if (_skip > 0)
                    {
                        _skip--;
                        break;
                    }

                    AddByte(reader.HexValue);
                    break;
                case RtfTokenKind.Text:
                    _cur.Fresh = false;
                    Text(reader.Text);
                    break;
                case RtfTokenKind.Binary:
                    if (_cur.Dest == Dest.Pict && _pict is not null)
                    {
                        foreach (byte b in reader.Text)
                        {
                            PictByte(b);
                        }
                    }

                    break;
            }
        }

        FlushBytes();
        while (_stack.Count > 1)
        {
            PopGroup();
        }

        FinishAtEnd();
        foreach (RtfDefect d in reader.Defects)
        {
            _defects.Add(d.Kind, d.Offset, d.Message);
        }

        _doc.DefaultCodePage = DefaultCodePage;
    }

    private static bool StartsWithRtf(ReadOnlySpan<byte> data)
    {
        int i = 0;
        while (i < data.Length && (data[i] == '\r' || data[i] == '\n' || data[i] == ' '))
        {
            i++;
        }

        return data.Length - i >= 5 && data[i] == '{' && data[i + 1] == '\\' && data[i + 2] == 'r' && data[i + 3] == 't' && data[i + 4] == 'f';
    }

    private int DefaultCodePage => _explicitCodePage != 0 ? _explicitCodePage : _impliedCodePage != 0 ? _impliedCodePage : 1252;

    private bool Budget()
    {
        if (++_nodes > _options.MaxNodes)
        {
            if (!_stopped)
            {
                _defects.Add(RtfDefectKind.ContentLimitExceeded, _offset, "the document has more items than the limit; the rest is dropped");
            }

            _stopped = true;
            return false;
        }

        return true;
    }

    private void PopGroup()
    {
        if (_stack.Count <= 1)
        {
            return;
        }

        GState popped = _cur;
        _cur = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        if (popped.PictRoot)
        {
            FinishPicture(popped);
        }

        if (popped.SpRoot)
        {
            ApplyShapeProperty();
        }

        if (popped.Dest == Dest.FontTable && _cur.Dest != Dest.FontTable)
        {
            _curFont = null;
        }
    }

    // ---- tokens ----

    private void ControlSymbol(byte symbol)
    {
        if (symbol == '*')
        {
            _cur.Star = true; // the group is an ignorable destination; Fresh stays set until its first control word
            return;
        }

        bool first = _cur.Fresh;
        _cur.Fresh = false;
        if (first && _cur.Star)
        {
            _cur.Dest = Dest.Skip; // \* followed by a symbol: nothing this reader knows
        }

        if (_skip > 0)
        {
            _skip--;
            return;
        }

        FlushBytes();
        switch (symbol)
        {
            case (byte)'\\':
            case (byte)'{':
            case (byte)'}':
                EmitChar((char)symbol);
                break;
            case (byte)'~':
                EmitChar('\u00A0');
                break;
            case (byte)'_':
                EmitChar('\u2011');
                break;
            case (byte)'\r':
            case (byte)'\n':
                EndParagraph(); // a backslash before a line break is \par (RTF 1.9.1, control symbols)
                break;
        }
    }

    private void ControlWord(ref RtfReader reader)
    {
        bool first = _cur.Fresh;
        bool star = _cur.Star;
        _cur.Fresh = false;

        ReadOnlySpan<byte> raw = reader.Name;
        int n = Math.Min(raw.Length, _nameBuffer.Length);
        for (int i = 0; i < n; i++)
        {
            _nameBuffer[i] = (char)raw[i];
        }

        ReadOnlySpan<char> name = _nameBuffer.AsSpan(0, n);
        bool hasParam = reader.HasParameter;
        int param = reader.Parameter;
        WordLookup.TryGetValue(name, out W w);

        if (first && _cur.Dest != Dest.Skip)
        {
            if (DestinationWord(w, name, star, hasParam, param))
            {
                return;
            }
        }

        if (_cur.Dest == Dest.Skip)
        {
            return;
        }

        if (_skip > 0)
        {
            _skip--; // a control word stands in for one character of the \u fallback
            return;
        }

        _pendingPlaceholderSpace = false;
        FlushBytes();
        if (CharLookup.TryGetValue(name, out char special))
        {
            EmitChar(special);
            return;
        }

        switch (_cur.Dest)
        {
            case Dest.FontTable:
                FontTableWord(w, hasParam, param);
                return;
            case Dest.ColorTable:
                ColorTableWord(w, param);
                return;
            case Dest.StyleSheet:
                if (w == W.S && hasParam)
                {
                    _curStyleNumber = param;
                    _styleName.Clear();
                    _styleNameDone = false;
                }

                return;
            case Dest.ListTable:
                ListTableWord(w, hasParam, param);
                return;
            case Dest.ListOverrideTable:
                ListOverrideWord(w, hasParam, param);
                return;
            case Dest.Pict:
            case Dest.PicProp:
            case Dest.SpName:
            case Dest.SpValue:
                PictWord(w, hasParam, param);
                return;
            case Dest.Pn:
                PnWord(w, hasParam, param);
                return;
            case Dest.FieldInst:
                return;
        }

        BodyWord(w, hasParam, param);
    }

    // Decides whether a control word that opens a group makes it a destination. Returns true when the word was consumed.
    private bool DestinationWord(W w, ReadOnlySpan<char> name, bool star, bool hasParam, int param)
    {
        switch (w)
        {
            case W.Fonttbl:
                _cur.Dest = Dest.FontTable;
                return true;
            case W.Colortbl:
                _cur.Dest = Dest.ColorTable;
                _colorAny = false;
                _colorR = _colorG = _colorB = 0;
                return true;
            case W.Stylesheet:
                _cur.Dest = Dest.StyleSheet;
                _curStyleNumber = -1;
                return true;
            case W.Listtable:
                _cur.Dest = Dest.ListTable;
                return true;
            case W.ListOverrideTable:
                _cur.Dest = Dest.ListOverrideTable;
                return true;
            case W.FldInst:
                _fldInst.Clear();
                _cur.Dest = Dest.FieldInst;
                return true;
            case W.FldRslt:
                _cur.Href = LinkTarget(_fldInst.ToString()) ?? _cur.Href;
                _fldInst.Clear();
                return true;
            case W.Field:
                _fldInst.Clear();
                return true;
            case W.Pict:
                StartPicture();
                return true;
            case W.PicProp:
                _cur.Dest = Dest.PicProp;
                return true;
            case W.Pn:
                _cur.Dest = Dest.Pn;
                _pnActive = true;
                _pnBullet = false;
                _pnKind = ListKind.Decimal;
                _pnLevel = 0;
                _pnStart = 1;
                return true;
            case W.ShpPict:
                return true;
            case W.Shp:
                _shapeHasInstance = false;
                return false;
            case W.ShpInst:
                // The shape's own description: its properties are read, and its text box (\shptxt) is body text. The fallback in \shprslt is the same shape again.
                _shapeHasInstance = true;
                return true;
            case W.ShpRslt:
                if (_shapeHasInstance)
                {
                    _cur.Dest = Dest.Skip;
                }

                return true;
            case W.Sp:
                return true;
            case W.Sn:
                _spName.Clear();
                _cur.Dest = Dest.SpName;
                return true;
            case W.Sv:
                _spValue.Clear();
                _cur.Dest = Dest.SpValue;
                _cur.SpRoot = true;
                return true;
        }

        if (_cur.Dest == Dest.PicProp || _cur.Dest == Dest.SpName || _cur.Dest == Dest.SpValue)
        {
            return false;
        }

        if (name.Length > 0 && SkipDestinations.Contains(new string(name)))
        {
            _cur.Dest = Dest.Skip;
            return true;
        }

        if (star)
        {
            // An ignorable destination the reader does not know (RTF 1.9.1: "a reader that does not recognise it skips the group").
            _cur.Dest = Dest.Skip;
            return true;
        }

        return false;
    }

    // ---- body words ----

    private void BodyWord(W w, bool hasParam, int param)
    {
        switch (w)
        {
            case W.B: _cur.Bold = !hasParam || param != 0; break;
            case W.I: _cur.Italic = !hasParam || param != 0; break;
            case W.Ul: _cur.Underline = (byte)(!hasParam || param != 0 ? 1 : 0); break;
            case W.UlNone: _cur.Underline = 0; break;
            case W.Strike: _cur.Strike = !hasParam || param != 0; break;
            case W.Super: _cur.Script = 1; break;
            case W.Sub: _cur.Script = 2; break;
            case W.NoSuperSub: _cur.Script = 0; break;
            case W.Caps: _cur.Caps = !hasParam || param != 0; break;
            case W.SmallCaps: _cur.SmallCaps = !hasParam || param != 0; break;
            case W.V: _cur.Hidden = !hasParam || param != 0; break;
            case W.F: _cur.Font = param; break;
            case W.Af: _cur.AssocFont = param; break;
            case W.Fs: _cur.HalfPoints = param; break;
            case W.Cf: _cur.Color = ColorIndex(param); break;
            case W.Highlight: _cur.Highlight = ColorIndex(param); break;
            case W.Cb: _cur.CharBack = ColorIndex(param); break;
            case W.ChCbPat: _cur.CharBack = ColorIndex(param); break;
            case W.Plain:
                _cur.Bold = _cur.Italic = _cur.Strike = _cur.Caps = _cur.SmallCaps = _cur.Hidden = _cur.DoubleByte = false;
                _cur.Underline = _cur.Script = 0;
                _cur.Font = _cur.AssocFont = -1;
                _cur.HalfPoints = 0;
                _cur.Color = _cur.Highlight = _cur.CharBack = 0;
                break;
            case W.Loch: _cur.DoubleByte = false; break;
            case W.Dbch: _cur.DoubleByte = true; break;
            case W.Uc: _cur.Uc = Math.Clamp(hasParam ? param : 1, 0, 16); break;
            case W.U: Unicode(param); break;
            case W.Pard:
                _cur.Align = Align.Left;
                _cur.Left = _cur.Right = _cur.FirstLine = _cur.SpaceBefore = _cur.SpaceAfter = _cur.LineSpacing = 0;
                _cur.LineMultiple = false;
                _cur.ListKey = -1;
                _cur.ListLevel = 0;
                _cur.Style = -1;
                _cur.InTable = false;
                _cur.Itap = 0;
                _pnActive = false;
                break;
            case W.Par: EndParagraph(); break;
            case W.Line: AddSeg(SegKind.LineBreak, 0, 0); break;
            case W.Tab: AddSeg(SegKind.Tab, 0, 0); break;
            case W.Qc: _cur.Align = Align.Center; break;
            case W.Ql: _cur.Align = Align.Left; break;
            case W.Qr: _cur.Align = Align.Right; break;
            case W.Qj: _cur.Align = Align.Justify; break;
            case W.Li: _cur.Left = param; break;
            case W.Ri: _cur.Right = param; break;
            case W.Fi: _cur.FirstLine = param; break;
            case W.Sb: _cur.SpaceBefore = Math.Max(param, 0); break;
            case W.Sa: _cur.SpaceAfter = Math.Max(param, 0); break;
            case W.Sl: _cur.LineSpacing = param; break;
            case W.SlMult: _cur.LineMultiple = !hasParam || param != 0; break;
            case W.S: _cur.Style = param; break;
            case W.Ls: _cur.ListKey = param; break;
            case W.Ilvl: _cur.ListLevel = Math.Clamp(param, 0, 8); break;
            case W.InTbl: _cur.InTable = true; break;
            case W.Itap:
                _cur.Itap = param;
                _cur.InTable = param > 0;
                if (param > 1 && !_reportedNested)
                {
                    _reportedNested = true;
                    _defects.Add(RtfDefectKind.NestedTableFlattened, _offset, "nested tables are flattened into the cell that holds them");
                }

                break;
            case W.TrOwd:
                _defs.Clear();
                _pendDef = new TableCell();
                Array.Clear(_rowBorders);
                _trLeft = 0;
                _trHeader = false;
                _borderTarget = -1;
                break;
            case W.TrLeft: _trLeft = param; break;
            case W.TrHdr: _trHeader = true; break;
            case W.CellX:
                _pendDef.Right = param;
                _defs.Add(_pendDef);
                _pendDef = new TableCell();
                _borderTarget = -1;
                break;
            case W.Cell:
            case W.NestCell:
                EndCell(w == W.NestCell);
                break;
            case W.Row: EndRow(); break;
            case W.NestRow: break;
            case W.ClMgf: _pendDef.MergeFirst = true; break;
            case W.ClMrg: _pendDef.MergeContinue = true; break;
            case W.ClVmgf: _pendDef.VMergeFirst = true; break;
            case W.ClVmrg: _pendDef.VMergeContinue = true; break;
            case W.ClVertAlt: _pendDef.VAlign = 0; break;
            case W.ClVertAlc: _pendDef.VAlign = 1; break;
            case W.ClVertAlb: _pendDef.VAlign = 2; break;
            case W.ClCbPat: _pendDef.Shading = ResolveColor(ColorIndex(param)); break;
            case W.ClBrdrT: _borderTarget = 0; break;
            case W.ClBrdrL: _borderTarget = 1; break;
            case W.ClBrdrB: _borderTarget = 2; break;
            case W.ClBrdrR: _borderTarget = 3; break;
            case W.TrBrdrT: _borderTarget = 4; break;
            case W.TrBrdrL: _borderTarget = 5; break;
            case W.TrBrdrB: _borderTarget = 6; break;
            case W.TrBrdrR: _borderTarget = 7; break;
            case W.TrBrdrH: _borderTarget = 8; break;
            case W.TrBrdrV: _borderTarget = 9; break;
            case W.ParaBorder: _borderTarget = -1; break;
            case W.BrdrLine: SetBorder(BorderOp.Line, 0); break;
            case W.BrdrNone: SetBorder(BorderOp.None, 0); break;
            case W.BrdrW: SetBorder(BorderOp.Width, Math.Clamp(param, 1, 255)); break;
            case W.BrdrCf: SetBorder(BorderOp.Color, ResolveColor(ColorIndex(param))); break;
            case W.ObjAttPh:
                AddSeg(SegKind.Attachment, _doc.AttachmentPlaceholders++, 0);
                _skip = 0;
                _pendingPlaceholderSpace = true;
                break;
            case W.AnsiCpg: _explicitCodePage = param; break;
            case W.Ansi: _impliedCodePage = 1252; break;
            case W.Mac: _impliedCodePage = 10000; break;
            case W.Pc: _impliedCodePage = 437; break;
            case W.Pca: _impliedCodePage = 850; break;
            case W.Deff: _doc.DefaultFont = param; break;
        }
    }

    private bool _pendingPlaceholderSpace;

    private int ColorIndex(int index)
    {
        if (index <= 0)
        {
            return 0;
        }

        if (index >= _colors.Count)
        {
            if (!_reportedUndefinedColor)
            {
                _reportedUndefinedColor = true;
                _defects.Add(RtfDefectKind.UndefinedReference, _offset, "a colour index is outside the colour table; the automatic colour is used");
            }

            return 0;
        }

        return index; // the table index, resolved when a run's style is made
    }

    private bool _reportedUndefinedColor;

    private enum BorderOp
    {
        Line,
        None,
        Width,
        Color,
    }

    // \brdrs and the other line styles switch the border on at 0.5 pt unless \brdrw said otherwise; \brdrnone/\brdrnil switch it off.
    private void SetBorder(BorderOp op, int value)
    {
        if (_borderTarget < 0)
        {
            return;
        }

        ref Border b = ref BorderRef(_borderTarget);
        switch (op)
        {
            case BorderOp.None: b = default; break;
            case BorderOp.Line: if (b.Width == 0) { b.Width = 10; } break;
            case BorderOp.Width: b.Width = (byte)value; break;
            case BorderOp.Color: b.Color = value; break;
        }
    }

    private ref Border BorderRef(int target)
    {
        switch (target)
        {
            case 0: return ref _pendDef.Top;
            case 1: return ref _pendDef.Left;
            case 2: return ref _pendDef.Bottom;
            case 3: return ref _pendDef.Right_;
            default: return ref _rowBorders[target - 4];
        }
    }

    // ---- font, colour, style sheet, lists ----

    private void FontTableWord(W w, bool hasParam, int param)
    {
        switch (w)
        {
            case W.F:
                if (hasParam)
                {
                    if (!_doc.Fonts.TryGetValue(param, out FontInfo? f))
                    {
                        f = new FontInfo();
                        _doc.Fonts[param] = f;
                    }

                    _curFont = f;
                    _fontName.Clear();
                    _fontNameDone = false;
                }

                break;
            case W.FCharset:
                if (_curFont is not null)
                {
                    // \fcharset0 is ANSI_CHARSET and 1 is DEFAULT_CHARSET: both mean the document's own ANSI code page (\ansicpg), which is not always 1252.
                    _curFont.CodePage = param == 2 ? -2 : param <= 1 ? 0 : RtfCodePages.FromCharset(param);
                }

                break;
            case W.Cpg:
                if (_curFont is not null && param > 0)
                {
                    _curFont.CodePage = param;
                }

                break;
            case W.FNil: SetFamily(0); break;
            case W.FRoman: SetFamily(1); break;
            case W.FSwiss: SetFamily(2); break;
            case W.FModern: SetFamily(3); break;
            case W.FScript: SetFamily(4); break;
            case W.FDecor: SetFamily(5); break;
            case W.FTech: SetFamily(6); break;
        }
    }

    private void SetFamily(byte f)
    {
        if (_curFont is not null)
        {
            _curFont.Family = f;
        }
    }

    private void ColorTableWord(W w, int param)
    {
        switch (w)
        {
            case W.Red: _colorR = Math.Clamp(param, 0, 255); _colorAny = true; break;
            case W.Green: _colorG = Math.Clamp(param, 0, 255); _colorAny = true; break;
            case W.Blue: _colorB = Math.Clamp(param, 0, 255); _colorAny = true; break;
        }
    }

    private void CommitColor()
    {
        if (_colors.Count < 4096)
        {
            _colors.Add(_colorAny ? (_colorR << 16) | (_colorG << 8) | _colorB : -1);
        }

        _colorAny = false;
        _colorR = _colorG = _colorB = 0;
    }

    private void ListTableWord(W w, bool hasParam, int param)
    {
        switch (w)
        {
            case W.List:
                _curList = new ListDef { ListId = int.MinValue };
                _curLevel = null;
                break;
            case W.ListId when _curList is not null:
                _curList.ListId = param;
                if (!_lists.ContainsKey(param))
                {
                    _lists[param] = _curList;
                }

                break;
            case W.ListLevel when _curList is not null:
                _curLevel = new ListLevelInfo();
                if (_curList.Levels.Count < 9)
                {
                    _curList.Levels.Add(_curLevel);
                }

                break;
            case W.LevelNfc when _curLevel is not null:
                _curLevel.Nfc = param;
                break;
            case W.LevelStartAt when _curLevel is not null:
                _curLevel.Start = param;
                break;
        }
    }

    private void ListOverrideWord(W w, bool hasParam, int param)
    {
        switch (w)
        {
            case W.ListOverride:
                _ovListId = int.MinValue;
                _ovStarts = [];
                _ovLevelIndex = -1;
                break;
            case W.ListId:
                _ovListId = param;
                break;
            case W.LfoLevel:
                _ovLevelIndex++;
                break;
            case W.LevelStartAt when _ovStarts is not null && _ovLevelIndex >= 0:
                _ovStarts[_ovLevelIndex] = param;
                break;
            case W.Ls:
                if (_ovStarts is not null)
                {
                    _overrides[param] = (_ovListId, _ovStarts);
                }

                break;
        }
    }

    private void PnWord(W w, bool hasParam, int param)
    {
        switch (w)
        {
            case W.PnLvlBlt: _pnBullet = true; _pnLevel = 0; break;
            case W.PnLvlBody: _pnLevel = 0; break;
            case W.PnLvl: _pnLevel = Math.Clamp(param - 1, 0, 8); break;
            case W.PnDec: _pnKind = ListKind.Decimal; break;
            case W.PnUcLtr: _pnKind = ListKind.UpperAlpha; break;
            case W.PnLcLtr: _pnKind = ListKind.LowerAlpha; break;
            case W.PnUcRm: _pnKind = ListKind.UpperRoman; break;
            case W.PnLcRm: _pnKind = ListKind.LowerRoman; break;
            case W.PnStart: _pnStart = Math.Max(param, 0); break;
        }
    }

    // ---- text ----

    private void Text(ReadOnlySpan<byte> text)
    {
        if (_cur.Dest == Dest.Skip)
        {
            return;
        }

        if (_cur.Dest == Dest.Pict)
        {
            foreach (byte b in text)
            {
                PictHex(b);
            }

            return;
        }

        if (_cur.Dest == Dest.ColorTable)
        {
            foreach (byte b in text)
            {
                if (b == ';')
                {
                    CommitColor();
                }
            }

            return;
        }

        foreach (byte b in text)
        {
            if (_skip > 0)
            {
                _skip--;
                continue;
            }

            AddByte(b);
        }
    }

    private void AddByte(byte b)
    {
        if (_cur.Dest == Dest.Skip)
        {
            return;
        }

        if (_pendingPlaceholderSpace)
        {
            _pendingPlaceholderSpace = false;
            if (b == ' ')
            {
                return; // the space \objattph inserts to stand for the attachment (RTF 1.9.1)
            }
        }

        if (b < 0x80 && _bytes.Count == 0)
        {
            EmitChar((char)b);
            return;
        }

        FontInfo? font = CurrentFontInfo();
        int cp = font is { CodePage: not 0 } f ? f.CodePage : DefaultCodePage;
        if (_bytes.Count > 0 && cp != _bytesCodePage)
        {
            FlushBytes();
        }

        _bytesCodePage = cp;
        _bytes.Add(b);
    }

    private FontInfo? CurrentFontInfo()
    {
        int index = _cur.DoubleByte && _cur.AssocFont >= 0 ? _cur.AssocFont : _cur.Font >= 0 ? _cur.Font : _doc.DefaultFont;
        return _doc.Fonts.TryGetValue(index, out FontInfo? f) ? f : null;
    }

    private void FlushBytes()
    {
        if (_bytes.Count == 0)
        {
            return;
        }

        byte[] bytes = [.. _bytes];
        _bytes.Clear();
        if (_bytesCodePage == -2)
        {
            if (!_reportedSymbol)
            {
                _reportedSymbol = true;
                _defects.Add(RtfDefectKind.AmbiguousSymbolFont, _offset, "text in a symbol font is kept as private-use Symbol code points");
            }

            foreach (byte b in bytes)
            {
                EmitChar(b == 0xB7 ? '\u2022' : (char)(0xF000 | b));
            }

            return;
        }

        Encoding? enc = GetEncoding(_bytesCodePage);
        if (enc is null)
        {
            _defects.Add(RtfDefectKind.AmbiguousUnknownCodePage, _offset, "no decoder for code page " + _bytesCodePage.ToString(CultureInfo.InvariantCulture) + "; the default was used");
            enc = GetEncoding(DefaultCodePage) ?? Encoding.Latin1;
        }

        string s = enc.GetString(bytes);
        foreach (char c in s)
        {
            EmitChar(c);
        }
    }

    private bool _reportedSymbol;
    private bool _shapeHasInstance;

    private Encoding? GetEncoding(int codePage)
    {
        if (_encodings.TryGetValue(codePage, out Encoding? known))
        {
            return known;
        }

        Encoding? enc = _options.CodePageDecoder is { } hook ? hook(codePage) : RtfCodePages.Decoder(codePage);
        _encodings[codePage] = enc;
        return enc;
    }

    // \uN: N is a signed 16-bit value; the next \ucN characters are the fallback for readers without Unicode and are skipped.
    private void Unicode(int n)
    {
        char c = (char)(ushort)(n < 0 ? n + 65536 : n);
        EmitChar(c);
        _skip = _cur.Uc;
    }

    // All text reaches the document through here, routed by the current destination. Unpaired surrogates become U+FFFD.
    private void EmitChar(char c)
    {
        if (_pendingHigh != 0)
        {
            char high = _pendingHigh;
            _pendingHigh = '\0';
            if (char.IsLowSurrogate(c))
            {
                Route(high);
                Route(c);
                return;
            }

            _defects.Add(RtfDefectKind.LoneSurrogate, _offset, "unpaired surrogate replaced by U+FFFD");
            Route('\uFFFD');
        }

        if (char.IsHighSurrogate(c))
        {
            _pendingHigh = c;
            return;
        }

        if (char.IsLowSurrogate(c))
        {
            _defects.Add(RtfDefectKind.LoneSurrogate, _offset, "unpaired surrogate replaced by U+FFFD");
            c = '\uFFFD';
        }

        Route(c);
    }

    private void Route(char c)
    {
        switch (_cur.Dest)
        {
            case Dest.Normal:
                if (_cur.Hidden || c == '\0')
                {
                    return;
                }

                AddText(c);
                break;
            case Dest.FieldInst:
                if (_fldInst.Length < 4096)
                {
                    _fldInst.Append(c);
                }

                break;
            case Dest.FontTable:
                if (_curFont is not null && !_fontNameDone)
                {
                    if (c == ';')
                    {
                        _fontNameDone = true;
                        _curFont.Name = _fontName.ToString().Trim();
                    }
                    else if (_fontName.Length < 128)
                    {
                        _fontName.Append(c);
                    }
                }

                break;
            case Dest.StyleSheet:
                if (_curStyleNumber >= 0 && !_styleNameDone)
                {
                    if (c == ';')
                    {
                        _styleNameDone = true;
                        _styleNames[_curStyleNumber] = _styleName.ToString().Trim();
                    }
                    else if (_styleName.Length < 128)
                    {
                        _styleName.Append(c);
                    }
                }

                break;
            case Dest.SpName:
                if (_spName.Length < 256)
                {
                    _spName.Append(c);
                }

                break;
            case Dest.SpValue:
                if (_spValue.Length < 4096)
                {
                    _spValue.Append(c);
                }

                break;
        }
    }

    // ---- runs and paragraphs ----

    private int StyleIndex()
    {
        int font = _cur.DoubleByte && _cur.AssocFont >= 0 ? _cur.AssocFont : _cur.Font >= 0 ? _cur.Font : _doc.DefaultFont;
        int hp = _cur.HalfPoints > 0 ? _cur.HalfPoints : 24;
        int back = _cur.Highlight > 0 ? _cur.Highlight : _cur.CharBack;
        var style = new RunStyle(
            _cur.Bold, _cur.Italic, _cur.Underline, _cur.Strike, _cur.Script, _cur.Caps, _cur.SmallCaps, font, hp,
            ResolveColor(_cur.Color), ResolveColor(back), _cur.Href);
        if (!_styleIndex.TryGetValue(style, out int index))
        {
            index = _doc.Styles.Count;
            _doc.Styles.Add(style);
            _styleIndex[style] = index;
        }

        return index;
    }

    private int ResolveColor(int index) => index > 0 && index < _colors.Count ? _colors[index] : -1;

    private void AddText(char c)
    {
        if (_doc.Text.Length >= _options.MaxOutputChars)
        {
            if (!_stopped)
            {
                _defects.Add(RtfDefectKind.ContentLimitExceeded, _offset, "the text is longer than the limit; the rest is dropped");
            }

            _stopped = true;
            return;
        }

        int style = StyleIndex();
        if (_segs.Count > 0)
        {
            Seg last = _segs[^1];
            if (last.Kind == SegKind.Text && last.Style == style && last.Start + last.Length == _doc.Text.Length)
            {
                _segs[^1] = last with { Length = last.Length + 1 };
                _doc.Text.Append(c);
                return;
            }
        }

        if (!Budget())
        {
            return;
        }

        _segs.Add(new Seg(SegKind.Text, _doc.Text.Length, 1, style));
        _doc.Text.Append(c);
    }

    private void AddSeg(SegKind kind, int start, int length)
    {
        if (_cur.Dest != Dest.Normal || (_cur.Hidden && kind != SegKind.Attachment) || !Budget())
        {
            return;
        }

        _segs.Add(new Seg(kind, start, length, StyleIndex()));
    }

    private Paragraph MakeParagraph()
    {
        int heading = 0;
        if (_cur.Style >= 0 && _styleNames.TryGetValue(_cur.Style, out string? name)
            && name.Length == 9 && name.StartsWith("heading ", StringComparison.OrdinalIgnoreCase) && name[8] is >= '1' and <= '6')
        {
            heading = name[8] - '0';
        }

        int key = _cur.ListKey;
        int level = _cur.ListLevel;
        bool pnBullet = false;
        ListKind pnKind = ListKind.None;
        int pnStart = 1;
        if (key < 0 && _pnActive)
        {
            pnBullet = _pnBullet;
            pnKind = _pnBullet ? ListKind.Bullet : _pnKind;
            pnStart = _pnStart;
            key = -2 - (int)pnKind; // one synthetic list per kind, so numbering continues across interruptions
            level = _pnLevel;
        }

        var p = new Paragraph
        {
            Segs = _segs,
            Align = _cur.Align,
            Left = _cur.Left,
            Right = _cur.Right,
            FirstLine = _cur.FirstLine,
            SpaceBefore = _cur.SpaceBefore,
            SpaceAfter = _cur.SpaceAfter,
            LineSpacing = _cur.LineSpacing,
            LineSpacingMultiple = _cur.LineMultiple,
            Heading = heading,
            ListKey = key,
            ListLevel = level,
            PnBullet = pnBullet,
            PnKind = pnKind,
            PnStart = pnStart,
        };
        _segs = [];
        return p;
    }

    private void EndParagraph()
    {
        if (_cur.Dest != Dest.Normal)
        {
            return;
        }

        FlushBytes();
        if (_cur.Hidden && _segs.Count == 0)
        {
            return; // a hidden paragraph mark with nothing visible
        }

        if (!Budget())
        {
            return;
        }

        Paragraph p = MakeParagraph();
        if (_cur.InTable)
        {
            _cellBlocks.Add(p);
        }
        else
        {
            AddTopBlock(p);
        }
    }

    private void AddTopBlock(Block b)
    {
        FlushTable();
        _doc.Blocks.Add(b);
    }

    private void FlushTable()
    {
        if (_table is not null)
        {
            _doc.Blocks.Add(_table);
            _table = null;
        }
    }

    // ---- tables ----

    private void EndCell(bool nested)
    {
        if (_cur.Dest != Dest.Normal)
        {
            return;
        }

        FlushBytes();
        if (!nested && _defs.Count == 0 && _rowCells.Count == 0 && _table is null)
        {
            // \cell with no \trowd before it: there is no row to put it in, so it only separates text.
            _defects.Add(RtfDefectKind.AmbiguousBareCell, _offset, "\\cell outside a table row definition is treated as a tab");
            AddSeg(SegKind.Tab, 0, 0);
            return;
        }

        if (nested)
        {
            // Nested cells are flattened: the cell's text ends its paragraph and stays in the outer cell.
            if (_segs.Count > 0)
            {
                _cellBlocks.Add(MakeParagraph());
            }

            return;
        }

        if (_segs.Count > 0)
        {
            _cellBlocks.Add(MakeParagraph());
        }

        if (!Budget())
        {
            return;
        }

        var cell = new TableCell();
        cell.Blocks.AddRange(_cellBlocks);
        _cellBlocks = [];
        _rowCells.Add(cell);
    }

    private void EndRow()
    {
        if (_cur.Dest != Dest.Normal)
        {
            return;
        }

        FlushBytes();
        if (_rowCells.Count == 0 && _segs.Count == 0 && _cellBlocks.Count == 0)
        {
            return;
        }

        if (_segs.Count > 0 || _cellBlocks.Count > 0)
        {
            EndCell(false);
        }

        var row = new TableRow { Header = _trHeader };
        int previousRight = _trLeft;
        for (int i = 0; i < _rowCells.Count; i++)
        {
            TableCell cell = _rowCells[i];
            if (i < _defs.Count)
            {
                TableCell def = _defs[i];
                cell.Right = def.Right;
                cell.Width = Math.Max(def.Right - previousRight, 0);
                previousRight = def.Right;
                cell.MergeFirst = def.MergeFirst;
                cell.MergeContinue = def.MergeContinue;
                cell.VMergeFirst = def.VMergeFirst;
                cell.VMergeContinue = def.VMergeContinue;
                cell.VAlign = def.VAlign;
                cell.Shading = def.Shading;
                cell.Top = def.Top.Width > 0 ? def.Top : _rowBorders[0].Width > 0 ? _rowBorders[0] : _rowBorders[4];
                cell.Bottom = def.Bottom.Width > 0 ? def.Bottom : _rowBorders[2].Width > 0 ? _rowBorders[2] : _rowBorders[4];
                cell.Left = def.Left.Width > 0 ? def.Left : i == 0 ? _rowBorders[1] : _rowBorders[5];
                cell.Right_ = def.Right_.Width > 0 ? def.Right_ : i == _rowCells.Count - 1 ? _rowBorders[3] : _rowBorders[5];
            }
            else if (i == _defs.Count)
            {
                _defects.Add(RtfDefectKind.UndefinedReference, _offset, "a table row has more cells than \\cellx boundaries");
            }

            row.Cells.Add(cell);
        }

        _rowCells.Clear();
        _table ??= new Table();
        _table.Rows.Add(row);
        MergeCells(_table);
    }

    // Horizontal merges (\clmgf/\clmrg) become a column span on the first cell; vertical merges (\clvmgf/\clvmrg) a row span on
    // the cell above that has the same right edge.
    private static void MergeCells(Table table)
    {
        TableRow row = table.Rows[^1];
        for (int i = 0; i < row.Cells.Count; i++)
        {
            TableCell c = row.Cells[i];
            if (!c.MergeFirst)
            {
                continue;
            }

            int j = i + 1;
            while (j < row.Cells.Count && row.Cells[j].MergeContinue)
            {
                c.ColSpan++;
                c.Width += row.Cells[j].Width;
                c.Right = row.Cells[j].Right;
                row.Cells[j].Hidden = true;
                j++;
            }
        }

        if (table.Rows.Count < 2)
        {
            return;
        }

        TableRow above = table.Rows[^2];
        foreach (TableCell c in row.Cells)
        {
            if (!c.VMergeContinue || c.Hidden)
            {
                continue;
            }

            foreach (TableCell a in above.Cells)
            {
                if (a.Right == c.Right && a.Right != int.MinValue && (a.VMergeFirst || a.VMergeContinue || a.RowSpan > 1))
                {
                    TableCell owner = a.Hidden && a.Owner is not null ? a.Owner : a;
                    owner.RowSpan++;
                    c.Hidden = true;
                    c.Owner = owner;
                    break;
                }
            }
        }
    }

    // ---- pictures ----

    private void StartPicture()
    {
        _cur.Dest = Dest.Pict;
        _cur.PictRoot = true;
        _pict = new PictState();
    }

    private void PictWord(W w, bool hasParam, int param)
    {
        PictState? p = _pict;
        if (p is null)
        {
            return;
        }

        switch (w)
        {
            case W.PngBlip: p.Format = "png"; break;
            case W.JpegBlip: p.Format = "jpeg"; break;
            case W.EmfBlip: p.Format = "emf"; break;
            case W.WMetafile: p.Format = "wmf"; break;
            case W.DibBitmap: p.Format = "dib"; break;
            case W.WBitmap: p.Format = "wbitmap"; break;
            case W.MacPict: p.Format = "pict"; break;
            case W.PmMetafile: p.Format = "pmmetafile"; break;
            case W.PicW: p.W = param; break;
            case W.PicH: p.H = param; break;
            case W.PicWGoal: p.WGoal = param; break;
            case W.PicHGoal: p.HGoal = param; break;
            case W.PicScaleX: p.ScaleX = param; break;
            case W.PicScaleY: p.ScaleY = param; break;
        }
    }

    private void PictHex(byte b)
    {
        int v = b switch
        {
            >= (byte)'0' and <= (byte)'9' => b - '0',
            >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
            >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
            _ => -1,
        };
        PictState? p = _pict;
        if (p is null)
        {
            return;
        }

        if (v < 0)
        {
            if (b > ' ')
            {
                p.BadHex = true; // skipped; the digits around it are still read
            }

            return;
        }

        if (p.Nibble < 0)
        {
            p.Nibble = v;
            return;
        }

        PictByte((byte)((p.Nibble << 4) | v));
        p.Nibble = -1;
    }

    private void PictByte(byte b)
    {
        PictState? p = _pict;
        if (p is null || p.Overflow)
        {
            return;
        }

        if (p.Data.Count >= _options.MaxPictureBytes)
        {
            p.Overflow = true;
            return;
        }

        p.Data.Add(b);
    }

    private void ApplyShapeProperty()
    {
        if (_pict is null)
        {
            return;
        }

        string name = _spName.ToString();
        string value = _spValue.ToString();
        if (name == "fHorizRule" && value.Trim() == "1")
        {
            _pict.HorizontalRule = true;
        }
        else if (name == "wzDescription" && value.Length > 0)
        {
            _pict.Description = value;
        }
    }

    private void FinishPicture(GState popped)
    {
        PictState? p = _pict;
        _pict = null;
        if (p is null || popped.Dest != Dest.Pict || (_cur.Dest != Dest.Normal && _cur.Dest != Dest.SpValue) || _cur.Hidden)
        {
            return;
        }

        // A shape's picture sits inside its \sv property group; it is still a picture of the body.
        Dest parent = _cur.Dest;
        _cur.Dest = Dest.Normal;
        try
        {
            AddPicture(p);
        }
        finally
        {
            _cur.Dest = parent;
        }
    }

    private void AddPicture(PictState p)
    {

        if (p.HorizontalRule)
        {
            AddSeg(SegKind.HorizontalRule, 0, 0);
            return;
        }

        if (!Budget())
        {
            return;
        }

        byte[] data = [.. p.Data];
        if (p.Overflow || _totalPictureBytes + data.Length > _options.MaxTotalPictureBytes)
        {
            _defects.Add(RtfDefectKind.PictureLimitExceeded, _offset, "a picture is over the size limit and is replaced by a placeholder");
            data = [];
            p.Bad = true;
        }

        _totalPictureBytes += data.Length;
        string declared = p.Format;
        string sniffed = Sniff(data);
        if (declared == "dib" && sniffed == "")
        {
            data = DibToBmp(data);
            sniffed = data.Length > 0 ? "bmp" : "";
            declared = "bmp";
        }

        string kind = sniffed != "" ? sniffed : declared;
        if (sniffed != "" && declared != "" && declared != sniffed)
        {
            _defects.Add(RtfDefectKind.AmbiguousPictureFormat, _offset, "the picture is declared " + declared + " but its bytes are " + sniffed + "; the bytes decide");
        }
        else if (sniffed == "" && data.Length > 0 && (declared == "png" || declared == "jpeg"))
        {
            _defects.Add(RtfDefectKind.InvalidPictureData, _offset, "the picture data is not a " + declared + " image");
            p.Bad = true;
        }

        if (kind == "")
        {
            kind = declared == "" ? "unknown" : declared;
        }

        (string contentType, bool displayable) = kind switch
        {
            "png" => ("image/png", true),
            "jpeg" => ("image/jpeg", true),
            "bmp" => ("image/bmp", true),
            "emf" => ("image/x-emf", false),
            "wmf" => ("image/x-wmf", false),
            "pict" => ("image/x-pict", false),
            _ => ("application/octet-stream", false),
        };
        if (data.Length == 0 || p.Bad || sniffed == "")
        {
            displayable = false; // only bytes that really are a picture of the shown kind are shown
        }

        int width = p.WGoal > 0 ? (int)Math.Round(p.WGoal * Math.Max(p.ScaleX, 1) / 100.0 / 15.0) : 0;
        int height = p.HGoal > 0 ? (int)Math.Round(p.HGoal * Math.Max(p.ScaleY, 1) / 100.0 / 15.0) : 0;
        if (p.Nibble >= 0)
        {
            _defects.Add(RtfDefectKind.InvalidPictureData, _offset, "the picture's hexadecimal data has an odd number of digits");
        }

        if (p.BadHex)
        {
            _defects.Add(RtfDefectKind.InvalidPictureData, _offset, "the picture data has characters that are not hexadecimal digits; they were skipped");
        }

        var image = new RtfImage(_doc.Images.Count, contentType, data, displayable, width, height, p.Description);
        _doc.Images.Add(image);
        AddSeg(SegKind.Image, image.Index, 0);
    }

    private static string Sniff(byte[] d)
    {
        if (d.Length >= 8 && d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G')
        {
            return "png";
        }

        if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF)
        {
            return "jpeg";
        }

        if (d.Length >= 14 && d[0] == 'B' && d[1] == 'M')
        {
            return "bmp";
        }

        if (d.Length >= 44 && d[0] == 1 && d[1] == 0 && d[2] == 0 && d[3] == 0 && d[40] == ' ' && d[41] == 'E' && d[42] == 'M' && d[43] == 'F')
        {
            return "emf";
        }

        if (d.Length >= 4 && ((d[0] == 0xD7 && d[1] == 0xCD && d[2] == 0xC6 && d[3] == 0x9A) || (d[0] is 1 or 2 && d[1] == 0 && d[2] == 9 && d[3] == 0)))
        {
            return "wmf";
        }

        return "";
    }

    // A device-independent bitmap is a BITMAPINFOHEADER and pixels; browsers want the 14-byte BITMAPFILEHEADER in front.
    private static byte[] DibToBmp(byte[] dib)
    {
        if (dib.Length < 40)
        {
            return [];
        }

        int headerSize = BitConverter.ToInt32(dib, 0);
        int bitCount = BitConverter.ToInt16(dib, 14);
        int compression = BitConverter.ToInt32(dib, 16);
        int colorsUsed = BitConverter.ToInt32(dib, 32);
        if (headerSize < 40 || headerSize > dib.Length)
        {
            return [];
        }

        long palette = bitCount <= 8 ? (colorsUsed != 0 ? colorsUsed : 1L << bitCount) * 4 : (compression == 3 && headerSize == 40 ? 12 : 0);
        long offset = 14 + headerSize + palette;
        if (offset > 14L + dib.Length)
        {
            return [];
        }

        byte[] bmp = new byte[14 + dib.Length];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.TryWriteBytes(bmp.AsSpan(2), bmp.Length);
        BitConverter.TryWriteBytes(bmp.AsSpan(10), (int)offset);
        dib.CopyTo(bmp, 14);
        return bmp;
    }

    // ---- fields ----

    // HYPERLINK "url" [\l "anchor"] (RTF 1.9.1, fields; the instruction is the Word field-code syntax). Other fields have no target.
    private static string? LinkTarget(string instruction)
    {
        string s = instruction.Trim();
        if (!s.StartsWith("HYPERLINK", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string url = "";
        string anchor = "";
        int i = 9;
        while (i < s.Length)
        {
            while (i < s.Length && s[i] == ' ')
            {
                i++;
            }

            if (i >= s.Length)
            {
                break;
            }

            if (s[i] == '\\' && i + 1 < s.Length)
            {
                char sw = char.ToLowerInvariant(s[i + 1]);
                i += 2;
                while (i < s.Length && s[i] == ' ')
                {
                    i++;
                }

                string arg = ReadArgument(s, ref i);
                if (sw == 'l')
                {
                    anchor = arg;
                }

                continue;
            }

            string a = ReadArgument(s, ref i);
            if (url.Length == 0)
            {
                url = a;
            }
        }

        string target = anchor.Length > 0 ? url + "#" + anchor : url;
        return target.Length > 0 ? target : null;
    }

    private static string ReadArgument(string s, ref int i)
    {
        if (i >= s.Length)
        {
            return "";
        }

        if (s[i] == '"')
        {
            // Inside quotes a backslash escapes the next character (Word field-code syntax), so "..\\x" is ..\x.
            var sb = new StringBuilder();
            i++;
            while (i < s.Length && s[i] != '"')
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    i++;
                }

                sb.Append(s[i++]);
            }

            if (i < s.Length)
            {
                i++;
            }

            return sb.ToString();
        }

        int start = i;
        while (i < s.Length && s[i] != ' ')
        {
            i++;
        }

        return s[start..i];
    }

    // ---- the end ----

    private void FinishAtEnd()
    {
        if (_pendingHigh != 0)
        {
            _pendingHigh = '\0';
            _defects.Add(RtfDefectKind.LoneSurrogate, _offset, "unpaired surrogate replaced by U+FFFD");
        }

        if (_segs.Count > 0 && !_stopped)
        {
            _cur.Dest = Dest.Normal;
            Paragraph p = MakeParagraph();
            if (_cur.InTable && (_rowCells.Count > 0 || _cellBlocks.Count > 0 || _table is not null))
            {
                _cellBlocks.Add(p);
            }
            else
            {
                AddTopBlock(p);
            }
        }

        if (_rowCells.Count > 0 || _cellBlocks.Count > 0)
        {
            _cur.Dest = Dest.Normal;
            EndRow(); // a table cut off before \row
        }

        FlushTable();

        // Outlook ends every body with an empty paragraph; trailing empty paragraphs say nothing.
        while (_doc.Blocks.Count > 0 && _doc.Blocks[^1] is Paragraph { IsEmpty: true })
        {
            _doc.Blocks.RemoveAt(_doc.Blocks.Count - 1);
        }

        ResolveLists(_doc.Blocks, []);
    }

    // ---- lists ----

    private void ResolveLists(List<Block> blocks, Dictionary<int, int[]> counters)
    {
        foreach (Block b in blocks)
        {
            if (b is Table t)
            {
                foreach (TableRow r in t.Rows)
                {
                    foreach (TableCell c in r.Cells)
                    {
                        ResolveLists(c.Blocks, counters);
                    }
                }

                continue;
            }

            if (b is not Paragraph p || p.ListKey == -1)
            {
                continue;
            }

            ListKind kind;
            int start;
            if (p.ListKey >= 0)
            {
                (kind, start) = LookUp(p.ListKey, p.ListLevel);
            }
            else
            {
                kind = p.PnKind;
                start = p.PnStart;
            }

            p.Kind = kind;
            if (kind == ListKind.None)
            {
                continue;
            }

            if (!counters.TryGetValue(p.ListKey, out int[]? c9))
            {
                c9 = new int[9];
                counters[p.ListKey] = c9;
            }

            c9[p.ListLevel]++;
            for (int l = p.ListLevel + 1; l < 9; l++)
            {
                c9[l] = 0;
            }

            p.Number = start + c9[p.ListLevel] - 1;
        }
    }

    private (ListKind Kind, int Start) LookUp(int ls, int level)
    {
        if (_overrides.TryGetValue(ls, out var ov) && _lists.TryGetValue(ov.ListId, out ListDef? def))
        {
            ListLevelInfo? info = level < def.Levels.Count ? def.Levels[level] : null;
            int start = ov.Starts.TryGetValue(level, out int s) ? s : info?.Start ?? 1;
            return (info is null ? ListKind.Decimal : KindOf(info.Nfc), start);
        }

        if (_lists.TryGetValue(ls, out ListDef? direct))
        {
            ListLevelInfo? info = level < direct.Levels.Count ? direct.Levels[level] : null;
            return (info is null ? ListKind.Decimal : KindOf(info.Nfc), info?.Start ?? 1);
        }

        if (!_reportedUndefinedList)
        {
            _reportedUndefinedList = true;
            _defects.Add(RtfDefectKind.UndefinedReference, 0, "a paragraph uses a list (\\ls) that the list tables do not define; it is shown as a bulleted list");
        }

        return (ListKind.Bullet, 1);
    }

    // \levelnfc values (RTF 1.9.1): 0 decimal, 1 upper roman, 2 lower roman, 3 upper letter, 4 lower letter, 23 bullet, 255 no number.
    private static ListKind KindOf(int nfc) => nfc switch
    {
        23 => ListKind.Bullet,
        255 => ListKind.None,
        1 => ListKind.UpperRoman,
        2 => ListKind.LowerRoman,
        3 => ListKind.UpperAlpha,
        4 => ListKind.LowerAlpha,
        _ => ListKind.Decimal,
    };
}
