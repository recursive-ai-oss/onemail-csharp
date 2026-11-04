using System.Text;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Mime.Reading;

namespace RecursiveAI.OneMail.Tests.Mime;

/// <summary>
/// Plain address lists are read by a short path. A trailing comma keeps a list off that path and the general path reads it the
/// same way (an empty last element is skipped), so the two must agree on every value, damaged ones included.
/// </summary>
public class AddressParserFastPathTests
{
    private static string Describe(string text, out List<MimeDefectKind> kinds)
    {
        var defects = new List<MimeDefect>();
        List<MimeAddress> list = AddressParser.Parse(Encoding.Latin1.GetBytes(text), defects, 5, 2);
        kinds = defects.Select(d => d.Kind).ToList();
        var sb = new StringBuilder();
        foreach (MimeAddress a in list)
        {
            sb.Append(a is MimeMailbox m ? $"[{m.Name}|{m.Address}]" : "[group]");
        }

        return sb.ToString() + string.Join(',', defects.Select(d => $"{d.Kind}@{d.Offset}+{d.Length}"));
    }

    [Fact]
    public void SimpleListsAgreeWithTheGeneralPath()
    {
        var rnd = new Random(5);
        string[] pieces =
        [
            "a", "b.c", "Phillip", "x-y", "o'brien", "a.b.c", "", ".", "..", "A", "9", "k+tag", "=?utf-8?q?x?=", "\"q\"", "(c)", "\u00e9",
            "a b", "a  b", "a\tb", "a\r\n b", "a\r\n\tb", "x@y", "d.e",
        ];
        string[] seps = [", ", ",", " ,", ",\r\n\t", ", \t", ",,", "; ", " "];
        string Piece() => pieces[rnd.Next(pieces.Length)];
        string Atoms() => Piece() + (rnd.Next(2) == 0 ? "" : "." + Piece());
        int clean = 0;
        for (int round = 0; round < 30000; round++)
        {
            var sb = new StringBuilder();
            int n = rnd.Next(1, 5);
            for (int k = 0; k < n; k++)
            {
                if (k > 0)
                {
                    sb.Append(seps[rnd.Next(seps.Length)]);
                }

                string addr = Atoms() + "@" + Atoms();
                switch (rnd.Next(4))
                {
                    case 0:
                        sb.Append(addr);
                        break;
                    case 1:
                        sb.Append(Piece() + " " + Piece() + " <" + addr + ">");
                        break;
                    case 2:
                        sb.Append('<').Append(addr).Append('>');
                        break;
                    default:
                        sb.Append(Piece()).Append(" <").Append(addr).Append(rnd.Next(5) == 0 ? "" : ">");
                        break;
                }
            }

            string text = sb.ToString();
            string a = Describe(text, out List<MimeDefectKind> ka);
            string b = Describe(text + ",", out List<MimeDefectKind> kb);

            // A defect that reports the length of the rest of the value moves by the comma, so only values one side read cleanly are compared.
            if (ka.Count == 0 || kb.Count == 0)
            {
                Assert.Equal(b, a);
            }

            if (ka.Count == 0)
            {
                clean++;
            }
        }

        Assert.True(clean > 1000, "the generator should produce plenty of clean lists; got " + clean);
    }
}
