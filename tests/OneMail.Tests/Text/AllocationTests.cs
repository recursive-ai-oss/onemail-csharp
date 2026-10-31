using System.Text;
using RecursiveAI.OneMail.Text;

namespace RecursiveAI.OneMail.Tests.Text;

// Allocation per call on the common paths. The bounds are loose guards against regressions; the measured numbers go to the test output.
public class AllocationTests
{
    private static long PerCall(Func<object?> body)
    {
        for (int i = 0; i < 200; i++)
        {
            body();
        }

        const int N = 2000;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < N; i++)
        {
            body();
        }

        return (GC.GetAllocatedBytesForCurrentThread() - before) / N;
    }

    private static void Report(string what, long bytes) =>
        TestContext.Current.SendDiagnosticMessage($"allocation: {what} = {bytes} B/call");

    [Fact]
    public void CommonPaths()
    {
        byte[] ascii = Encoding.ASCII.GetBytes("Quarterly report for the Houston office");
        byte[] oneWord = Encoding.ASCII.GetBytes("=?utf-8?B?0J/RgNC40LLQtdGCINC80LjRgCDQv9GA0LjQstC10YI=?=");
        byte[] twoWords = Encoding.ASCII.GetBytes("Re: =?windows-1252?Q?r=E9sum=E9_of_the?= =?windows-1252?Q?_caf=E9_menu?=");
        byte[] utf8 = Encoding.UTF8.GetBytes("Gr\u00F6\u00DFe und Gew\u00FCrze, f\u00FCr alle");
        byte[] latin = Encoding.Latin1.GetBytes("Gr\u00F6\u00DFe und Gew\u00FCrze, f\u00FCr alle");
        byte[] sjis = [0x93, 0xFA, 0x96, 0x7B, 0x8C, 0xEA, 0x93, 0xFA, 0x96, 0x7B, 0x8C, 0xEA];
        byte[] utf7 = Encoding.ASCII.GetBytes("Hi Mom -+Jjo--! +ZeVnLIqe-");
        MailEncoding w1252 = MailEncoding.Windows1252;
        MailEncoding shiftJis = MailEncoding.ForLabel("shift_jis")!;
        byte[] n0 = Encoding.ASCII.GetBytes("filename*0*");
        byte[] v0 = Encoding.ASCII.GetBytes("utf-8''caf%C3%A9%20");
        byte[] n1 = Encoding.ASCII.GetBytes("filename*1*");
        byte[] v1 = Encoding.ASCII.GetBytes("menu.pdf");

        long a1 = PerCall(() => Rfc2047.DecodeText(ascii));
        long a2 = PerCall(() => Rfc2047.DecodeText(oneWord));
        long a3 = PerCall(() => Rfc2047.DecodeText(twoWords));
        long a4 = PerCall(() => MailEncoding.Utf8.GetString(utf8));
        long a5 = PerCall(() => w1252.GetString(latin));
        long a6 = PerCall(() => shiftJis.GetString(sjis));
        long a7 = PerCall(() => Utf7.Decode(utf7));
        long a8 = PerCall(() =>
        {
            var c = new ParameterCollector();
            c.Add(n0, v0);
            c.Add(n1, v1);
            return c.Resolve();
        });
        Report("Rfc2047.DecodeText, 39 ASCII bytes, no words (result string 120 B)", a1);
        Report("Rfc2047.DecodeText, one UTF-8 B word (result string 56 B)", a2);
        Report("Rfc2047.DecodeText, two windows-1252 Q words", a3);
        Report("MailEncoding.Utf8.GetString, 42 bytes", a4);
        Report("windows-1252 GetString, 42 bytes with accents (via OneHtml)", a5);
        Report("shift_jis GetString, 12 bytes (via OneHtml)", a6);
        Report("Utf7.Decode, 26 bytes", a7);
        Report("ParameterCollector, 2 segments, Resolve", a8);

        Assert.InRange(a1, 0, 400);      // the string plus a small constant
        Assert.InRange(a4, 0, 400);
        Assert.InRange(a2, 0, 2000);
        Assert.InRange(a3, 0, 2500);
    }
}
