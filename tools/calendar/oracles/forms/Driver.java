// Black-box driver for the released biweekly and ez-vcard jars: reads a manifest of jobs and runs each through the library's
// public parse and write calls. One job per line: <job> <input> <output>. A library failure is written as the output file
// "ERROR <exception class>: <message>" so the comparison can count it.
//   text2json / text2xml     iCalendar or vCard text -> jCal/jCard or xCal/xCard
//   json2json / xml2xml      read a JSON or XML form, write it again
// The first token of the job says which library: ical-* uses biweekly, card-* uses ez-vcard.
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;
import biweekly.Biweekly;
import biweekly.ICalendar;
import ezvcard.Ezvcard;
import ezvcard.VCard;

public class Driver {
    public static void main(String[] a) throws Exception {
        for (String line : Files.readAllLines(Paths.get(a[0]), StandardCharsets.UTF_8)) {
            String[] p = line.split("\t");
            if (p.length < 3) continue;
            String out;
            try {
                out = run(p[0], new String(Files.readAllBytes(Paths.get(p[1])), StandardCharsets.UTF_8));
            } catch (Throwable t) {
                out = "ERROR " + t.getClass().getName() + ": " + String.valueOf(t.getMessage()).replace('\n', ' ');
            }
            Files.write(Paths.get(p[2]), out.getBytes(StandardCharsets.UTF_8));
        }
    }

    static String run(String job, String in) throws Exception {
        switch (job) {
            case "ical-text2json": return Biweekly.writeJson(Biweekly.parse(in).all()).go();
            case "ical-text2xml": return Biweekly.writeXml(Biweekly.parse(in).all()).go();
            case "ical-json2json": return Biweekly.writeJson(Biweekly.parseJson(in).all()).go();
            case "ical-xml2xml": return Biweekly.writeXml(Biweekly.parseXml(in).all()).go();
            case "card-text2json": return Ezvcard.writeJson(Ezvcard.parse(in).all()).go();
            case "card-text2xml": return Ezvcard.writeXml(Ezvcard.parse(in).all()).go();
            case "card-json2json": return Ezvcard.writeJson(Ezvcard.parseJson(in).all()).go();
            case "card-xml2xml": return Ezvcard.writeXml(Ezvcard.parseXml(in).all()).go();
            default: throw new IllegalArgumentException(job);
        }
    }
}
