#!/usr/bin/env python3
"""Generate the ICS / vCard fixtures under testdata/calendar/generated (seeded, so the output is reproducible).

  make_fixtures.py [outdir]

Hand-written files cover what real clients produce and what breaks parsers (vCard 2.1 quoted-printable and base64,
bare line ends, bad folds, unbalanced components, NUL bytes, huge parameter lists). Seeded mutations of the RFC example
blocks in testdata/calendar/rfc add truncation, line-end changes, folds in odd places and dropped or duplicated lines.
"""
import base64, glob, os, random, shutil, sys

SEED = 5545
HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
CRLF = "\r\n"


def crlf(s):
    return s.replace("\r\n", "\n").replace("\n", CRLF)


PNG = base64.b64encode(bytes(range(256)) * 3).decode()


def fold(line, n=75):
    b = line.encode("utf-8")
    out, first = [], True
    while len(b) > (n if first else n - 1):
        k = n if first else n - 1
        while k > 0 and (b[k] & 0xC0) == 0x80:
            k -= 1
        out.append(b[:k])
        b = b[k:]
        first = False
    out.append(b)
    return (CRLF + " ").join(x.decode("utf-8") for x in out)


HAND = {}

HAND["outlook-meeting.ics"] = crlf("""BEGIN:VCALENDAR
PRODID:-//Microsoft Corporation//Outlook 16.0 MIMEDIR//EN
VERSION:2.0
METHOD:REQUEST
X-MS-OLK-FORCEINSPECTOROPEN:TRUE
BEGIN:VTIMEZONE
TZID:W. Europe Standard Time
BEGIN:STANDARD
DTSTART:16011028T030000
RRULE:FREQ=YEARLY;BYDAY=-1SU;BYMONTH=10
TZOFFSETFROM:+0200
TZOFFSETTO:+0100
END:STANDARD
BEGIN:DAYLIGHT
DTSTART:16010325T020000
RRULE:FREQ=YEARLY;BYDAY=-1SU;BYMONTH=3
TZOFFSETFROM:+0100
TZOFFSETTO:+0200
END:DAYLIGHT
END:VTIMEZONE
BEGIN:VEVENT
ATTENDEE;CN="Mueller, Anna";ROLE=REQ-PARTICIPANT;PARTSTAT=NEEDS-ACTION;RSVP=TRUE:mailto:anna@example.com
ORGANIZER;CN=Boss:mailto:boss@example.com
CLASS:PUBLIC
CREATED:20260105T101500Z
DESCRIPTION:Agenda:\\n1. Status\\n2. Plans\\, risks\\; budget\\n
DTEND;TZID="W. Europe Standard Time":20260112T110000
DTSTAMP:20260105T101500Z
DTSTART;TZID="W. Europe Standard Time":20260112T100000
LAST-MODIFIED:20260105T101500Z
LOCATION:Room 4\\, Building B
PRIORITY:5
SEQUENCE:0
SUMMARY;LANGUAGE=de:Besprechung \u00fcber Pl\u00e4ne
TRANSP:OPAQUE
UID:040000008200E00074C5B7101A82E00800000000
X-MICROSOFT-CDO-BUSYSTATUS:TENTATIVE
BEGIN:VALARM
TRIGGER:-PT15M
ACTION:DISPLAY
DESCRIPTION:Reminder
END:VALARM
END:VEVENT
END:VCALENDAR
""")

HAND["google-recurring.ics"] = crlf("""BEGIN:VCALENDAR
PRODID:-//Google Inc//Google Calendar 70.9054//EN
VERSION:2.0
CALSCALE:GREGORIAN
X-WR-CALNAME:Team
X-WR-TIMEZONE:America/New_York
BEGIN:VEVENT
DTSTART;TZID=America/New_York:20260105T090000
DTEND;TZID=America/New_York:20260105T093000
RRULE:FREQ=WEEKLY;UNTIL=20261231T045959Z;BYDAY=MO,WE,FR
EXDATE;TZID=America/New_York:20260107T090000,20260109T090000
DTSTAMP:20260101T000000Z
UID:abc123@google.com
RECURRENCE-ID;TZID=America/New_York:20260112T090000
SUMMARY:Standup
END:VEVENT
BEGIN:VEVENT
DTSTART;VALUE=DATE:20260301
DTEND;VALUE=DATE:20260302
RDATE;VALUE=PERIOD:20260301T000000Z/PT1H,20260302T000000Z/20260302T010000Z
DTSTAMP:20260101T000000Z
UID:allday@google.com
SUMMARY:All day
END:VEVENT
END:VCALENDAR
""")

HAND["folded-utf8.ics"] = ("BEGIN:VCALENDAR" + CRLF + "VERSION:2.0" + CRLF + "BEGIN:VEVENT" + CRLF + "UID:u1" + CRLF
                           + fold("SUMMARY:" + "\u00e4\u00f6\u00fc\u20ac\U0001f600 " * 20) + CRLF
                           + fold("DESCRIPTION:" + "x" * 300) + CRLF + "END:VEVENT" + CRLF + "END:VCALENDAR" + CRLF)

HAND["bare-lf.ics"] = "BEGIN:VCALENDAR\nVERSION:2.0\nBEGIN:VEVENT\nUID:lf\nSUMMARY:a\n b\nDTSTART:20260101T000000Z\nEND:VEVENT\nEND:VCALENDAR\n"
HAND["bare-cr.ics"] = "BEGIN:VCALENDAR\rVERSION:2.0\rBEGIN:VEVENT\rUID:cr\rSUMMARY:a\r b\rEND:VEVENT\rEND:VCALENDAR\r"
HAND["mixed-endings.ics"] = "BEGIN:VCALENDAR\r\nVERSION:2.0\nBEGIN:VEVENT\rUID:mix\r\nSUMMARY:x\nEND:VEVENT\r\nEND:VCALENDAR"
HAND["no-final-newline.ics"] = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nEND:VCALENDAR"
HAND["bom.ics"] = "\ufeffBEGIN:VCALENDAR\r\nVERSION:2.0\r\nEND:VCALENDAR\r\n"
HAND["unclosed-event.ics"] = crlf("BEGIN:VCALENDAR\nVERSION:2.0\nBEGIN:VEVENT\nUID:x\nSUMMARY:no end\nEND:VCALENDAR\n")
HAND["extra-end.ics"] = crlf("BEGIN:VCALENDAR\nVERSION:2.0\nEND:VEVENT\nEND:VCALENDAR\nEND:VCALENDAR\n")
HAND["garbage-lines.ics"] = crlf("junk before\nBEGIN:VCALENDAR\nVERSION:2.0\nthis line has no colon\n:novalue name\nSUMMARY :spaced name\nX_UNDERSCORE:1\nEND:VCALENDAR\ntrailing junk\n")
HAND["blank-lines.ics"] = crlf("\n\nBEGIN:VCALENDAR\n\nVERSION:2.0\n\n\nEND:VCALENDAR\n\n")
HAND["case-mixed.ics"] = crlf("begin:vcalendar\nversion:2.0\nBegin:VEvent\nuid:ci\ndtstart;tzid=Europe/Paris:20260101T100000\nEnd:vevent\nend:VCALENDAR\n")
HAND["unknown-components.ics"] = crlf("BEGIN:VCALENDAR\nVERSION:2.0\nBEGIN:X-CUSTOM\nX-A:1\nBEGIN:VEVENT\nUID:n\nEND:VEVENT\nEND:X-CUSTOM\nBEGIN:VAVAILABILITY\nUID:av\nBEGIN:AVAILABLE\nUID:a\nEND:AVAILABLE\nEND:VAVAILABILITY\nEND:VCALENDAR\n")
HAND["caret-params.ics"] = crlf("BEGIN:VCALENDAR\nVERSION:2.0\nBEGIN:VEVENT\nUID:c\nATTENDEE;CN=\"Sir ^'Bob^' Smith^nJr ^^ x\":mailto:b@example.com\nX-P;A=1;A=2;B=\"x,y\",z:v\nEND:VEVENT\nEND:VCALENDAR\n")
HAND["quoted-colon-semicolon.ics"] = crlf("BEGIN:VCALENDAR\nVERSION:2.0\nBEGIN:VEVENT\nUID:q\nATTENDEE;DELEGATED-TO=\"mailto:a@x;y\";CN=\"A:B\":mailto:c@d\nEND:VEVENT\nEND:VCALENDAR\n")
HAND["bad-values.ics"] = crlf("BEGIN:VCALENDAR\nVERSION:2.0\nBEGIN:VEVENT\nUID:bv\nDTSTART:2026-01-01 10:00\nDTEND:20261301T000000\nDURATION:15 minutes\nRRULE:FREQ=SOMETIMES\nSUMMARY:bad escapes \\q \\x41 trailing\\\nTZOFFSET:x\nEND:VEVENT\nEND:VCALENDAR\n")
HAND["latin1-text.ics"] = (crlf("BEGIN:VCALENDAR\nVERSION:2.0\nBEGIN:VEVENT\nUID:l1\nSUMMARY:Caf") + "\u00e9" + crlf(" cr\u00e8me\nEND:VEVENT\nEND:VCALENDAR\n")).encode("latin-1")
HAND["nul-bytes.ics"] = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nSUMMARY:a\x00b\r\nEND:VCALENDAR\r\n"
HAND["many-parameters.ics"] = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nX-P" + "".join(";P%d=v%d" % (i, i) for i in range(300)) + ":x\r\nEND:VCALENDAR\r\n"
HAND["deep-nesting.ics"] = "".join("BEGIN:X%d\r\n" % i for i in range(60)) + "".join("END:X%d\r\n" % i for i in reversed(range(60)))
HAND["freebusy.ics"] = crlf("BEGIN:VCALENDAR\nVERSION:2.0\nMETHOD:REPLY\nBEGIN:VFREEBUSY\nUID:fb\nDTSTAMP:20260101T000000Z\nFREEBUSY;FBTYPE=BUSY:20260101T100000Z/20260101T110000Z,20260101T130000Z/PT1H\nEND:VFREEBUSY\nEND:VCALENDAR\n")
HAND["rfc7986.ics"] = crlf("BEGIN:VCALENDAR\nVERSION:2.0\nNAME:My Calendar\nCOLOR:turquoise\nREFRESH-INTERVAL;VALUE=DURATION:PT1H\nSOURCE;VALUE=URI:https://example.com/cal.ics\nBEGIN:VEVENT\nUID:r7986\nCONFERENCE;VALUE=URI;FEATURE=VIDEO;LABEL=Join:https://chat.example.com/x\nIMAGE;VALUE=URI;DISPLAY=BADGE;FMTTYPE=image/png:https://example.com/i.png\nEND:VEVENT\nEND:VCALENDAR\n")
HAND["attach-binary.ics"] = crlf("BEGIN:VCALENDAR\nVERSION:2.0\nBEGIN:VEVENT\nUID:ab\n") + fold("ATTACH;ENCODING=BASE64;VALUE=BINARY;FMTTYPE=image/png:" + PNG) + CRLF + crlf("END:VEVENT\nEND:VCALENDAR\n")

HAND["v21-outlook.vcf"] = crlf("""BEGIN:VCARD
VERSION:2.1
N;CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE:M=C3=BCller;Hans;;Dr.;
FN;CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE:Dr. Hans M=C3=BCller
ORG:Example AG;Sales
TITLE:Head of =Sales
TEL;WORK;VOICE:+49 30 123456
TEL;CELL;PREF:+49 170 1234567
EMAIL;PREF;INTERNET:hans@example.com
ADR;WORK;ENCODING=QUOTED-PRINTABLE;CHARSET=UTF-8:;;Stra=C3=9Fe 1=0D=0A=
Hinterhaus;Berlin;;10115;Deutschland
NOTE;ENCODING=QUOTED-PRINTABLE:line one=0D=0Aline two =
continues here
REV:20260101T000000Z
END:VCARD
""")
HAND["v21-photo-base64.vcf"] = "BEGIN:VCARD\r\nVERSION:2.1\r\nN:Doe;Jane\r\nFN:Jane Doe\r\nPHOTO;ENCODING=BASE64;JPEG:" + CRLF + CRLF.join(PNG[i:i + 76] for i in range(0, len(PNG), 76)) + CRLF + CRLF + "EMAIL:jane@example.com\r\nEND:VCARD\r\n"
HAND["v21-photo-indented.vcf"] = "BEGIN:VCARD\r\nVERSION:2.1\r\nN:Doe;Jane\r\nPHOTO;ENCODING=BASE64;JPEG:" + CRLF + CRLF.join(" " + PNG[i:i + 76] for i in range(0, len(PNG), 76)) + CRLF + CRLF + "END:VCARD\r\n"
HAND["v21-latin1.vcf"] = (crlf("BEGIN:VCARD\nVERSION:2.1\nN;CHARSET=ISO-8859-1:J") + "\u00fc" + crlf("rgen;Hans\nFN:J") + "\u00fc" + crlf("rgen Hans\nEND:VCARD\n")).encode("latin-1")
HAND["v21-unknown-charset.vcf"] = crlf("BEGIN:VCARD\nVERSION:2.1\nN;CHARSET=X-NOPE:caf\u00e9;A\nFN:x\nEND:VCARD\n").encode("utf-8")
HAND["v30-apple-groups.vcf"] = crlf("""BEGIN:VCARD
VERSION:3.0
PRODID:-//Apple Inc.//iPhone OS 17.0//EN
N:Smith;Ann;Marie;Dr.;Jr.
FN:Dr. Ann Marie Smith Jr.
NICKNAME:Annie,Am
ORG:Acme\\, Inc.;R&D;Lab
TITLE:Engineer
item1.EMAIL;type=INTERNET;type=pref:ann@example.com
item1.X-ABLabel:_$!<Work>!$_
item2.URL;type=pref:https://example.com
item2.X-ABLabel:_$!<HomePage>!$_
TEL;type=CELL;type=VOICE;type=pref:+1 (555) 010-0100
ADR;type=HOME;type=pref:;;1 Infinite Loop;Cupertino;CA;95014;USA
BDAY:1980-02-29
NOTE:Line 1\\nLine 2\\, with comma\\; and semicolon
PHOTO;ENCODING=b;TYPE=JPEG:""" + fold(PNG)[0:0] + PNG[:60] + """
CATEGORIES:friends,work
X-SOCIALPROFILE;type=twitter;x-user=ann:http://twitter.com/ann
END:VCARD
""")
HAND["v30-google.vcf"] = crlf("BEGIN:VCARD\nVERSION:3.0\nFN:Bob Builder\nN:Builder;Bob;;;\nEMAIL;TYPE=INTERNET;TYPE=HOME:bob@example.com\nTEL;TYPE=CELL:555-0100\nEND:VCARD\nBEGIN:VCARD\nVERSION:3.0\nFN:Second Card\nN:Card;Second;;;\nEND:VCARD\n")
HAND["v40-full.vcf"] = crlf("""BEGIN:VCARD
VERSION:4.0
KIND:individual
FN:Ann Smith
N:Smith;Ann;;;
EMAIL;TYPE=work;PREF=1:ann@example.com
TEL;TYPE="voice,work";VALUE=uri;PREF=1:tel:+1-555-0100
ADR;LABEL="1 Main St^nTown":;;1 Main St;Town;;12345;US
BDAY:--0229
ANNIVERSARY:20100101T120000Z
GEO:geo:37.386013,-122.082932
LANG;PREF=1:en
PHOTO:data:image/png;base64,""" + PNG[:80] + """
MEMBER:urn:uuid:03a0e51f-d1aa-4385-8a53-e29025acd8af
RELATED;TYPE=friend:urn:uuid:f81d4fae-7dec-11d0-a765-00a0c91e6bf6
CLIENTPIDMAP:1;urn:uuid:3df403f4-5924-4bb7-b077-3c711d9eb34b
X-CUSTOM;X-PARAM="a,b":custom
END:VCARD
""")
HAND["vcard-garbage.vcf"] = crlf("BEGIN:VCARD\nVERSION:3.0\nFN:A\nthis is not a property\nEND:VCARD\nEND:VCARD\nBEGIN:VCARD\nFN:unterminated\n")
HAND["vcard-no-version.vcf"] = crlf("BEGIN:VCARD\nFN:No Version\nN:Version;No\nEND:VCARD\n")
HAND["vcard-folded-name.vcf"] = "BEGIN:VCARD\r\nVERSION:3.0\r\nFN:" + "a" * 60 + "\r\n " + "b" * 60 + "\r\n \r\n " + "c\r\nEND:VCARD\r\n"
HAND["vcard-bare-lf-qp-lf.vcf"] = "BEGIN:VCARD\nVERSION:2.1\nNOTE;QUOTED-PRINTABLE:a=\nb=\nc\nFN:x\nEND:VCARD\n"


def write(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(data if isinstance(data, bytes) else data.encode("utf-8"))


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, "testdata", "calendar", "generated")
    shutil.rmtree(out, ignore_errors=True)
    for name, data in sorted(HAND.items()):
        write(os.path.join(out, "hand", name), data)
    rnd = random.Random(SEED)
    bases = sorted(glob.glob(os.path.join(ROOT, "testdata", "calendar", "rfc", "*")))
    n = 0
    for p in bases:
        src = open(p, "rb").read()
        if len(src) > 6000:
            continue
        stem, ext = os.path.splitext(os.path.basename(p))
        kinds = rnd.sample(["trunc", "lf", "cr", "fold", "dropline", "dupline", "garbage", "nofinal", "qpmix", "foldutf8"], 3)
        for k in kinds:
            data = bytearray(src)
            lines = src.split(b"\r\n")
            if k == "trunc":
                data = data[:rnd.randrange(1, max(2, len(data)))]
            elif k == "lf":
                data = bytearray(src.replace(b"\r\n", b"\n"))
            elif k == "cr":
                data = bytearray(src.replace(b"\r\n", b"\r"))
            elif k == "fold":
                # fold at random byte positions with random whitespace, including inside names and values
                out_b = bytearray()
                for ch in src:
                    out_b.append(ch)
                    if ch not in (13, 10) and rnd.random() < 0.03:
                        out_b += b"\r\n" + rnd.choice([b" ", b"\t", b"  "])
                data = out_b
            elif k == "dropline" and len(lines) > 3:
                del lines[rnd.randrange(len(lines) - 1)]
                data = bytearray(b"\r\n".join(lines))
            elif k == "dupline" and len(lines) > 3:
                i = rnd.randrange(len(lines) - 1)
                lines.insert(i, lines[i])
                data = bytearray(b"\r\n".join(lines))
            elif k == "garbage":
                i = rnd.randrange(len(lines))
                lines.insert(i, bytes(rnd.randrange(32, 127) for _ in range(rnd.randrange(1, 30))))
                data = bytearray(b"\r\n".join(lines))
            elif k == "nofinal":
                data = bytearray(src.rstrip(b"\r\n"))
            elif k == "qpmix":
                data = bytearray(src.replace(b":", b";ENCODING=QUOTED-PRINTABLE:", 1).replace(b"\r\n", b"=\r\n", 1))
            elif k == "foldutf8":
                data = bytearray(src.replace(b"A", "\u00c4".encode(), 3))
            write(os.path.join(out, "mutated", "%s-%s%s" % (stem, k, ext)), bytes(data))
            n += 1
    print(len(HAND), "hand-written,", n, "mutated fixtures in", out)


main()
