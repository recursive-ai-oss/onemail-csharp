#!/usr/bin/env python3
"""Generates testdata/calendar/card: one iTIP/iMIP file per method (and variant) and a file of events covering every RRULE shape
the card describes or falls back on. Deterministic: no clock, no random numbers.

  make_fixtures.py <outdir>
"""
import itertools, os, sys

out = sys.argv[1]
os.makedirs(out, exist_ok=True)

TZ = """BEGIN:VTIMEZONE
TZID:Europe/Berlin
BEGIN:STANDARD
DTSTART:19701025T030000
TZOFFSETFROM:+0200
TZOFFSETTO:+0100
RRULE:FREQ=YEARLY;BYMONTH=10;BYDAY=-1SU
END:STANDARD
BEGIN:DAYLIGHT
DTSTART:19700329T020000
TZOFFSETFROM:+0100
TZOFFSETTO:+0200
RRULE:FREQ=YEARLY;BYMONTH=3;BYDAY=-1SU
END:DAYLIGHT
END:VTIMEZONE
"""


def cal(method, body, tz=True):
    lines = ["BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//OneMail//card fixtures//EN"]
    if method:
        lines.append("METHOD:" + method)
    text = "\n".join(lines) + "\n" + (TZ if tz else "") + body + "END:VCALENDAR\n"
    return text.replace("\n", "\r\n")


def event(uid, extra, start="DTSTART;TZID=Europe/Berlin:20261130T140000", end="DTEND;TZID=Europe/Berlin:20261130T150000", summary="Quarterly planning"):
    return "\n".join(["BEGIN:VEVENT", "UID:" + uid, "DTSTAMP:20261101T120000Z", start, end, "SUMMARY:" + summary] + extra + ["END:VEVENT"]) + "\n"


ORG = "ORGANIZER;CN=Olivia Organizer:mailto:olivia@example.com"
ATT = ["ATTENDEE;CN=Alice;ROLE=REQ-PARTICIPANT;PARTSTAT=NEEDS-ACTION;RSVP=TRUE:mailto:alice@example.com",
       "ATTENDEE;CN=Bob;ROLE=OPT-PARTICIPANT;PARTSTAT=NEEDS-ACTION:mailto:bob@example.com",
       "ATTENDEE;CUTYPE=ROOM;CN=Room 4:mailto:room4@example.com"]

files = {}
files["request.ics"] = cal("REQUEST", event("req@example.com", [ORG, "SEQUENCE:0", "LOCATION:Room 4", "URL:https://example.com/agenda", "DESCRIPTION:Agenda:\\n1. Plans\\n2. Budget"] + ATT))
files["request-update.ics"] = cal("REQUEST", event("req@example.com", [ORG, "SEQUENCE:2", "LOCATION:Room 5"] + ATT))
files["request-conference.ics"] = cal("REQUEST", event("conf@example.com", [ORG, "CONFERENCE;VALUE=URI;FEATURE=VIDEO:https://meet.example.com/abc", "X-MICROSOFT-ONLINEMEETINGCONFLINK:https://teams.example.com/l/meetup-join/xyz"] + ATT[:1]))
files["request-recurring.ics"] = cal("REQUEST", event("rec@example.com", [ORG, "RRULE:FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;UNTIL=20271130T000000Z", "EXDATE;TZID=Europe/Berlin:20261202T140000"] + ATT[:1]))
files["request-allday.ics"] = cal("REQUEST", event("allday@example.com", [ORG], start="DTSTART;VALUE=DATE:20261224", end="DTEND;VALUE=DATE:20261228", summary="Holiday"))
files["request-utc.ics"] = cal("REQUEST", event("utc@example.com", [ORG], start="DTSTART:20261130T130000Z", end="DTEND:20261130T140000Z"), tz=False)
files["request-floating.ics"] = cal("REQUEST", event("float@example.com", [ORG], start="DTSTART:20261130T130000", end="DTEND:20261130T140000"), tz=False)
files["request-tentative.ics"] = cal("REQUEST", event("tent@example.com", [ORG, "STATUS:TENTATIVE", "TRANSP:TRANSPARENT"] + ATT[:2]))
for ps, name in (("ACCEPTED", "accept"), ("DECLINED", "decline"), ("TENTATIVE", "tentative")):
    files["reply-%s.ics" % name] = cal("REPLY", event("req@example.com", [ORG, "ATTENDEE;CN=Alice;PARTSTAT=%s:mailto:alice@example.com" % ps, "COMMENT:See you there"]))
files["cancel.ics"] = cal("CANCEL", event("req@example.com", [ORG, "SEQUENCE:3", "STATUS:CANCELLED"] + ATT[:1]))
files["counter.ics"] = cal("COUNTER", event("req@example.com", [ORG, "ATTENDEE;CN=Alice;PARTSTAT=TENTATIVE:mailto:alice@example.com", "COMMENT:Can we do 15:00?"], start="DTSTART;TZID=Europe/Berlin:20261130T150000", end="DTEND;TZID=Europe/Berlin:20261130T160000"))
files["declinecounter.ics"] = cal("DECLINECOUNTER", event("req@example.com", [ORG, "ATTENDEE;CN=Alice:mailto:alice@example.com", "COMMENT:No"]))
files["publish.ics"] = cal("PUBLISH", event("pub@example.com", ["RRULE:FREQ=MONTHLY;BYDAY=-1FR", "LOCATION:Town hall"]))
files["add.ics"] = cal("ADD", event("rec@example.com", [ORG], start="DTSTART;TZID=Europe/Berlin:20261215T140000", end="DTEND;TZID=Europe/Berlin:20261215T150000"))
files["refresh.ics"] = cal("REFRESH", event("req@example.com", ["ATTENDEE;CN=Alice:mailto:alice@example.com"]))
files["no-method.ics"] = cal("", event("plain@example.com", ["LOCATION:Somewhere"]))
files["override.ics"] = cal("REQUEST", event("rec@example.com", [ORG, "RRULE:FREQ=DAILY;COUNT=5"]) + event("rec@example.com", [ORG, "RECURRENCE-ID;TZID=Europe/Berlin:20261201T140000"], start="DTSTART;TZID=Europe/Berlin:20261201T160000", end="DTEND;TZID=Europe/Berlin:20261201T170000", summary="Moved"))
files["todo.ics"] = cal("", "BEGIN:VTODO\nUID:todo@example.com\nDTSTAMP:20261101T120000Z\nDUE;VALUE=DATE:20261201\nSUMMARY:File report\nEND:VTODO\n")
files["hostile.ics"] = cal("REQUEST", event("x@example.com", [ORG, "LOCATION:<img src=x onerror=alert(1)>", "DESCRIPTION:<script>alert(1)</script>\\n\\u003cb\\u003e", "URL:javascript:alert(1)",
                                                                    "ATTACH:javascript:alert(2)", "ATTENDEE;CN=\"<script>alert(3)</script>\":mailto:x@example.com"], summary="<script>alert(0)</script>"))

# Every shape of rule: each frequency with intervals, ends and BY parts. A rule that cannot occur is still a rule the card must describe or decline.
freqs = ["HOURLY", "MINUTELY", "DAILY", "WEEKLY", "MONTHLY", "YEARLY"]
intervals = ["", ";INTERVAL=2", ";INTERVAL=5"]
ends = ["", ";COUNT=1", ";COUNT=12", ";UNTIL=20271231", ";UNTIL=20271231T235959Z"]
by = ["", ";BYDAY=MO", ";BYDAY=TU,TH", ";BYDAY=MO,TU,WE,TH,FR", ";BYDAY=SA,SU", ";BYDAY=1MO", ";BYDAY=-1FR", ";BYDAY=2TU,4TU", ";BYDAY=5SU",
      ";BYDAY=MO,TU,WE,TH,FR;BYSETPOS=1", ";BYDAY=MO,TU,WE,TH,FR;BYSETPOS=-2", ";BYDAY=SA,SU;BYSETPOS=-1", ";BYDAY=WE;BYSETPOS=3",
      ";BYMONTHDAY=15", ";BYMONTHDAY=1,15,28", ";BYMONTHDAY=-1", ";BYMONTHDAY=-3", ";BYMONTH=3", ";BYMONTH=11;BYMONTHDAY=5",
      ";BYMONTH=3;BYDAY=1MO", ";BYMONTH=10;BYDAY=-1SU", ";BYMONTH=2;BYMONTHDAY=-1", ";BYMONTH=6;BYMONTHDAY=1,2", ";BYHOUR=9", ";BYYEARDAY=1", ";WKST=SU",
      ";BYWEEKNO=20;BYDAY=MO", ";BYDAY=MO,TU;BYSETPOS=1,2", ";BYMONTHDAY=13;BYDAY=FR", ";BYMONTH=1,6"]
rules = []
for f, i, e, b in itertools.product(freqs, intervals, ends, by):
    rules.append("FREQ=%s%s%s%s" % (f, i, b, e))
chunks = [rules[k:k + 400] for k in range(0, len(rules), 400)]
for n, chunk in enumerate(chunks):
    body = "".join(event("rule-%d-%d@example.com" % (n, k), ["RRULE:" + r], start="DTSTART:20260217T090000Z", end="DTEND:20260217T100000Z", summary=r) for k, r in enumerate(chunk))
    files["rrules-%02d.ics" % n] = cal("", body, tz=False)

for name, text in files.items():
    with open(os.path.join(out, name), "w", newline="") as f:
        f.write(text)
print(len(files), "files,", len(rules), "rules")
