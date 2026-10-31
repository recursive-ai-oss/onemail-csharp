#!/usr/bin/env python3
"""Writes the CR CR LF probe files: one iCalendar and one vCard, each with the line ends spelled four ways.

  make_probes.py <out dir>

crlf   every line ends CR LF (the control)
all    every line ends CR CR LF, as a CRLF file looks after a text-mode copy that doubles the CR
fold   only the line end before a fold (CR CR LF, space) is doubled
plain  only line ends that are not followed by a fold are doubled
"""
import os, sys

ICS = [("BEGIN:VCALENDAR", 0), ("VERSION:2.0", 0), ("PRODID:-//probe//EN", 0), ("BEGIN:VEVENT", 0), ("UID:probe-1", 0),
       ("DTSTAMP:20240101T000000Z", 0), ("DTSTART:20240101T090000Z", 0), ("SUMMARY:Hello", 1), (" World", 0),
       ("DESCRIPTION:After", 0), ("LOCATION:Room", 0), ("END:VEVENT", 0), ("END:VCALENDAR", 0)]
VCF = [("BEGIN:VCARD", 0), ("VERSION:3.0", 0), ("FN:Ann", 1), (" Lee", 0), ("N:Lee;Ann;;;", 0), ("NOTE:After", 0),
       ("EMAIL:ann@example.com", 0), ("END:VCARD", 0)]


def build(lines, mode):
    out = b""
    for i, (text, _) in enumerate(lines):
        nxt = lines[i + 1][0] if i + 1 < len(lines) else ""
        before_fold = nxt.startswith(" ")
        dbl = mode == "all" or (mode == "fold" and before_fold) or (mode == "plain" and not before_fold and i % 2 == 0)
        out += text.encode() + (b"\r\r\n" if dbl else b"\r\n")
    return out


out = sys.argv[1]
os.makedirs(out, exist_ok=True)
for ext, lines in (("ics", ICS), ("vcf", VCF)):
    for mode in ("crlf", "all", "fold", "plain"):
        with open(os.path.join(out, f"{mode}.{ext}"), "wb") as f:
            f.write(build(lines, mode))
