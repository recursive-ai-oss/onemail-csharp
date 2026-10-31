namespace RecursiveAI.OneMail.Mapi;

/// <summary>Where the code page of an object's 8-bit strings came from, strongest first.</summary>
public enum MapiCodePageSource
{
    /// <summary>The object's own PidTagMessageCodepage.</summary>
    MessageCodepage,

    /// <summary>The Windows ANSI code page of the language in the object's PidTagMessageLocaleId.</summary>
    LocaleId,

    /// <summary>The object's own PidTagInternetCodepage, when it is a single- or double-byte code page (not a Unicode one).</summary>
    InternetCodepage,

    /// <summary>The enclosing object's code page.</summary>
    Enclosing,

    /// <summary>Nothing says: windows-1252.</summary>
    Default,
}

/// <summary>Code page facts for Outlook items.</summary>
public static class MapiCodePages
{
    /// <summary>
    /// The Windows ANSI code page that text in a locale is written in, which is what an 8-bit string without a stated code page means
    /// on a machine set to that locale.
    /// </summary>
    /// <param name="lcid">A Windows locale ID such as 0x0407 (German, Germany).</param>
    /// <returns>The code page number, or 0 for a locale with no ANSI code page (Unicode-only scripts) or one not in the table.</returns>
    public static int AnsiCodePageForLocale(int lcid)
    {
        int primary = lcid & 0x3FF;
        int sub = (lcid >> 10) & 0x3F;
        switch (primary)
        {
            case 0x04: // Chinese: simplified for China and Singapore, traditional for Taiwan, Hong Kong and Macao
                return sub is 2 or 4 ? 936 : sub is 1 or 3 or 5 ? 950 : 0;
            case 0x1A: // Croatian, Serbian, Bosnian: Latin script is 1250, Cyrillic 1251
                return lcid is 0x0C1A or 0x1C1A or 0x281A or 0x301A or 0x201A ? 1251 : 1250;
            case 0x2C: // Azeri
                return lcid == 0x082C ? 1251 : 1254;
            case 0x43: // Uzbek
                return lcid == 0x0843 ? 1251 : 1254;
            case 0x01 or 0x20 or 0x29: // Arabic, Urdu, Persian
                return 1256;
            case 0x02 or 0x19 or 0x22 or 0x23 or 0x2F or 0x3F or 0x40 or 0x44 or 0x50: // Bulgarian, Russian, Ukrainian, Belarusian, Macedonian, Kazakh, Kyrgyz, Tatar, Mongolian
                return 1251;
            case 0x05 or 0x0E or 0x15 or 0x18 or 0x1B or 0x1C or 0x24 or 0x42: // Czech, Hungarian, Polish, Romanian, Slovak, Albanian, Slovenian, Turkmen
                return 1250;
            case 0x08:
                return 1253;
            case 0x1F:
                return 1254;
            case 0x0D:
                return 1255;
            case 0x25 or 0x26 or 0x27:
                return 1257;
            case 0x2A:
                return 1258;
            case 0x1E:
                return 874;
            case 0x11:
                return 932;
            case 0x12:
                return 949;
            case 0x03 or 0x06 or 0x07 or 0x09 or 0x0A or 0x0B or 0x0C or 0x0F or 0x10 or 0x13 or 0x14 or 0x16 or 0x1D or 0x21 or 0x2D or 0x36 or 0x38 or 0x3E or 0x41 or 0x56:
                return 1252;
            default:
                return 0;
        }
    }

    /// <summary>Whether a code page number is one of the Unicode transformation formats, which cannot be the code page of 8-bit strings.</summary>
    /// <param name="codePage">A Windows code page number.</param>
    /// <returns>True for UTF-7, UTF-8, UTF-16 and UTF-32.</returns>
    public static bool IsUnicodeTransformation(int codePage) => codePage is 65000 or 65001 or 1200 or 1201 or 12000 or 12001;
}
