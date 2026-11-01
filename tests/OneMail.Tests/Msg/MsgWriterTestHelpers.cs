using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Msg;

namespace RecursiveAI.OneMail.Tests.Msg;

internal static class MsgWriterTestHelpers
{
    /// <summary>Writes exactly what is in the model: no conversions and no additions.</summary>
    public static readonly MsgWriterOptions Exact = new() { Strings = MsgStringMode.AsGiven, KeepEmptyStrings = true, MaxRecipients = 65_536, MaxAttachments = 65_536 };

    public static List<string> Differences(MapiObject a, MapiObject b, string path = "message", Func<string, bool>? ignore = null) =>
        MsgModelComparer.Differences(a, b, path, ignore);

    public static MsgFile OpenAll(byte[] bytes)
    {
        MsgFile f = MsgTestHelpers.Open(bytes);
        f.ReadEverything();
        return f;
    }
}
