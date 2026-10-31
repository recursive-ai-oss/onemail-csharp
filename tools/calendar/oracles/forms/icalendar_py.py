"""Black-box driver for the released icalendar package (to_jcal, from_jcal). Manifest lines: job<TAB>input<TAB>output.

Jobs: text2json (Calendar.from_ical(text).to_jcal()), json2json (Calendar.from_jcal(json).to_jcal()).
A failure is written as 'ERROR <class>: <message>'.
"""
import json
import sys

from icalendar import Calendar

for line in open(sys.argv[1], encoding="utf-8").read().split("\n"):
    parts = line.split("\t")
    if len(parts) < 3:
        continue
    job, src, dst = parts[:3]
    try:
        if job == "text2json":
            out = json.dumps(Calendar.from_ical(open(src, "rb").read()).to_jcal())
        elif job == "json2json":
            out = json.dumps(Calendar.from_jcal(json.load(open(src, encoding="utf-8"))).to_jcal())
        else:
            raise ValueError(job)
    except Exception as e:  # noqa: BLE001 - the oracle's failure is the result
        out = "ERROR %s: %s" % (type(e).__name__, str(e).replace("\n", " "))
    open(dst, "w", encoding="utf-8").write(out)
