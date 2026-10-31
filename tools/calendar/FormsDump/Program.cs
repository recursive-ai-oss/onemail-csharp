using System.Text;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.Forms;

// FormsDump to json|xml <file>     convert an ICS or vCard file; the text goes to stdout, the losses to stderr
// FormsDump from json|xml <file>   convert a jCal/jCard/xCal/xCard file to ICS or vCard text
return args.Length >= 3 ? Run(args) : Usage();

static int Usage()
{
    Console.Error.WriteLine("usage: FormsDump to json|xml <file> | from json|xml <file>");
    return 2;
}

static int Run(string[] args)
{
    byte[] data = File.ReadAllBytes(args[2]);
    using Stream o = Console.OpenStandardOutput();
    switch (args[0], args[1])
    {
        case ("to", "json"):
        {
            FormWriteResult w = JsonForms.ToJson(ContentDocument.Parse(data), new JsonFormsOptions { Indented = true });
            o.Write(w.Bytes);
            Losses(w);
            return 0;
        }

        case ("to", "xml"):
        {
            ContentDocument d = ContentDocument.Parse(data);
            bool card = d.Components.Any(c => c.IsNamed("VCARD"));
            FormWriteResult w = card ? XmlForms.ToXCard(d) : XmlForms.ToXCal(d);
            o.Write(w.Bytes);
            Losses(w);
            return 0;
        }

        case ("from", "json"):
        {
            FormReadResult r = JsonForms.FromJson(data);
            o.Write(r.Document.ToArray());
            foreach (FormDefect x in r.Defects)
            {
                Console.Error.WriteLine(x);
            }

            return 0;
        }

        case ("from", "xml"):
        {
            FormReadResult r = XmlForms.FromXml(data);
            o.Write(r.Document.ToArray());
            foreach (FormDefect x in r.Defects)
            {
                Console.Error.WriteLine(x);
            }

            return 0;
        }

        default:
            return Usage();
    }
}

static void Losses(FormWriteResult w)
{
    foreach (FormLoss l in w.Losses)
    {
        Console.Error.WriteLine(l);
    }
}
