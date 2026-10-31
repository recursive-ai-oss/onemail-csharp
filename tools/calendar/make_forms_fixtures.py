#!/usr/bin/env python3
"""Cut the jCal / jCard / xCal / xCard examples out of the RFC texts into testdata/calendar/forms/rfc.

  make_forms_fixtures.py <dir with rfc7265.txt rfc7095.txt rfc6321.txt rfc6351.txt> [outdir]

Each fixture pair is the text form and the JSON or XML form exactly as the RFC prints them, except where the RFC's own
example is wrong. Those corrections are listed in ERRATA below and written beside the fixtures in ERRATA.txt, so the test
that compares our output with "the RFC's example" is comparing with the RFC after its published inconsistencies are fixed.
The RFC texts are IETF documents (see THIRD-PARTY-NOTICES.md).
"""
import hashlib, os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
PINS = {
    "rfc7265.txt": "e75afb119160ae2ce4cbd3d1cbbd36eb4f482ab4c358a800d5ab6693b65a03ac",
    "rfc7095.txt": "4aa8dc8a1673edb4104fae4a71f3721e73e15defd1dec7b79d9ea0067c22bfd2",
    "rfc6321.txt": "dd6f14e4898e1a94659a3ed72076f60809ed46aad68a782da487960bb2b3c262",
    "rfc6351.txt": "3ac0e03cccda6663ed32052749cc5c607d88e381f9cfcb795317bc39a57909e3",
}
FURNITURE = re.compile(r"^(RFC \d+ .*\d{4}$|.*\[Page \d+\]$|.*Standards Track.*|^Daboo, et al\..*|^Perreault.*|^Kewisch, et al\..*)")


def lines_of(path):
    return open(path, encoding="utf-8").read().replace("\f", "").split("\n")


ERRATA = []


def fix(name, what):
    ERRATA.append("%s: %s" % (name, what))


def write(outdir, name, text):
    with open(os.path.join(outdir, name), "w", encoding="utf-8", newline="\n") as f:
        f.write(text if text.endswith("\n") else text + "\n")


def main():
    src = sys.argv[1]
    out = sys.argv[2] if len(sys.argv) > 2 else os.path.join(HERE, "..", "..", "testdata", "calendar", "forms", "rfc")
    os.makedirs(out, exist_ok=True)
    for f, h in PINS.items():
        got = hashlib.sha256(open(os.path.join(src, f), "rb").read()).hexdigest()
        if got != h:
            sys.exit("%s: sha256 %s does not match the pinned %s" % (f, got, h))

    # ---- RFC 7265 (jCal) Appendix B ----
    L = lines_of(os.path.join(src, "rfc7265.txt"))
    def last_block(lines, start, end):
        idx = [k for k, l in enumerate(lines) if start in l][-1] + 1
        o = []
        while end not in lines[idx]:
            l = lines[idx]
            if l.strip() and not FURNITURE.match(l.strip()):
                o.append(l[3:] if l.startswith("   ") else l)
            idx += 1
        return o

    ics1 = last_block(L, "B.1.1.  iCalendar Data", "B.1.2.  jCal Data")
    js1 = last_block(L, "B.1.2.  jCal Data", "B.2.  Example 2")
    write(out, "rfc7265-b1.ics", "\n".join(ics1))
    write(out, "rfc7265-b1.json", "\n".join(js1))

    ics2 = last_block(L, "B.2.1.  iCalendar Data", "B.2.2.  jCal Data")
    js2 = last_block(L, "B.2.2.  jCal Data", "Authors' Addresses")
    j = "\n".join(js2)
    # Remove the explanatory comment and join the concatenated string.
    j = re.sub(r"\s*// Note that comments.*?\n(\s*//.*\n)*", "\n", j)
    desc = ("We are having a meeting all this week at 12 pm for one hour, with an additional meeting on the first day 2 hours long."
            "\\nPlease bring your own lunch for the 12 pm meetings.")
    j = re.sub(r'"We are having a meeting.*?"meetings\."', lambda m: '"' + desc + '"', j, flags=re.S)
    fix("rfc7265-b2.json", "comments removed and the concatenated description joined (JSON has neither)")
    j = re.sub(r'"period",\s*"2006-01-02T15:00:00/PT2H"', lambda m: '"period", ["2006-01-02T15:00:00", "PT2H"]', j)
    fix("rfc7265-b2.json", 'rdate period is the string "2006-01-02T15:00:00/PT2H"; RFC 7265 §3.6.9 makes a period an array of two strings')
    k = j.rfind('"byday": "1SU"')
    j = j[:k] + '"byday": "-1SU"' + j[k + len('"byday": "1SU"'):]
    fix("rfc7265-b2.json", 'STANDARD rrule byday is "1SU"; the iCalendar text of the same example has BYDAY=-1SU')
    k = j.rfind('"summary", {}, "text", "Event #2"')
    j = j[:k] + '"summary", {}, "text", "Event #2 bis"' + j[k + len('"summary", {}, "text", "Event #2"'):]
    fix("rfc7265-b2.json", 'the second VEVENT summary is "Event #2"; the iCalendar text has "Event #2 bis"')
    j = j.replace('"2006-01-02T14:00:00"', '"2006-01-04T14:00:00"')
    fix("rfc7265-b2.json", 'the second VEVENT dtstart is 2006-01-02T14:00:00; the iCalendar text has 20060104T140000')
    write(out, "rfc7265-b2.ics", "\n".join(ics2))
    write(out, "rfc7265-b2.json", j)

    # ---- RFC 6321 (xCal) Appendix B ----
    L = lines_of(os.path.join(src, "rfc6321.txt"))
    write(out, "rfc6321-b1.ics", "\n".join(last_block(L, "B.1.1.  iCalendar Data", "B.1.2.  XML Data")))
    write(out, "rfc6321-b1.xml", "\n".join(last_block(L, "B.1.2.  XML Data", "B.2.  Example 2")))
    ics = last_block(L, "B.2.1.  iCalendar Data", "B.2.2.  XML Data")
    ics.insert(0, "BEGIN:VCALENDAR")
    fix("rfc6321-b2.ics", "the example has no BEGIN:VCALENDAR line")
    write(out, "rfc6321-b2.ics", "\n".join(ics))
    x = "\n".join(last_block(L, "B.2.2.  XML Data", "Authors' Addresses"))
    x = x.replace("<text>-//Example Inc.//Example Client//EN</text>", "<text>-//Example Corp.//Example Client//EN</text>")
    fix("rfc6321-b2.xml", 'PRODID is "-//Example Inc.//Example Client//EN"; the iCalendar text has "Example Corp."')
    x = x.replace("<tzid>US/Eastern</tzid>", "<tzid><text>US/Eastern</text></tzid>")
    fix("rfc6321-b2.xml", "the VTIMEZONE tzid has no <text> element; RFC 6321 Appendix A requires value-text")
    x = x.replace("<recur>\n          <freq>YEARLY</freq>", "<recur><freq>YEARLY</freq>")  # no-op guard
    x = x.replace("We are having a meeting all this week at 12\npm for one hour", "We are having a meeting all this week at 12 pm for one hour")
    x = re.sub(r"(day)\n(\s*)2 hours long\.", r"\1 2 hours long.", x)
    x = re.sub(r"(12 pm)\nmeetings\.", r"\1 meetings.", x)
    x = x.replace("<?xml version=\"1.0\" encoding=\"utf-8\" ?>", "<?xml version=\"1.0\" encoding=\"utf-8\"?>")
    fix("rfc6321-b2.xml", "the description text is wrapped over several lines in the RFC; joined to the single line of the iCalendar text")
    write(out, "rfc6321-b2.xml", x)

    # ---- RFC 7095 (jCard) Appendix B ----
    L = lines_of(os.path.join(src, "rfc7095.txt"))
    vcf = last_block(L, "B.1.1.  vCard Data", "B.1.2.  jCard Data")
    vcf = [l for l in vcf]
    write(out, "rfc7095-b1.vcf", "\n".join(vcf))
    jc = "\n".join(last_block(L, "B.1.2.  jCard Data", "Author's Address"))
    write(out, "rfc7095-b1.json", jc)

    # ---- RFC 6351 (xCard) §4 ----
    L = lines_of(os.path.join(src, "rfc6351.txt"))
    xc = last_block(L, "4.  Example: Author's XML vCard", "5.  Design Considerations")
    write(out, "rfc6351-s4.xml", "\n".join(xc))

    write(out, "ERRATA.txt", "\n".join(ERRATA))
    print("wrote", len(os.listdir(out)), "files to", out)


if __name__ == "__main__":
    main()
