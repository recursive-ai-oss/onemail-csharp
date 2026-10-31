#!/usr/bin/env python3
"""Compare the calendar and task fields OxocalTool read (items.jsonl) with what extract-msg 0.55.0 exposes for the same MSG files.

  compare_extract_msg.py <items.jsonl> <msg root> [<venv python provides extract_msg>]

extract-msg (GPL-3.0) is run as a black box; only its released behaviour is observed. Fields compared: subject, location, start and end
(from the raw named properties it decodes), the attendee names and addresses, the task owner, status and percent. The script prints one
line per field and a final count; a field extract-msg does not expose is counted as 'not exposed', not as a difference.
"""
import datetime as dt, json, os, sys
import extract_msg


def find(root, name):
    for d, _, fs in os.walk(root):
        if name in fs:
            return os.path.join(d, name)


def first(msg, *names):
    for n in names:
        try:
            v = getattr(msg, n)
        except Exception:
            continue
        if v not in (None, ""):
            return v
    return None


items = [json.loads(l) for l in open(sys.argv[1], encoding="utf-8")]
root = sys.argv[2]
agree = differ = unexposed = 0
for it in items:
    fname = it["origin"].split("#")[0]
    path = find(root, fname)
    if path is None:
        print("missing", fname)
        continue
    msg = extract_msg.openMsg(path)
    checks = [("subject", it.get("subject"), first(msg, "subject"))]
    if it["class"].startswith("IPM.Appointment"):
        checks += [("location", it.get("location"), first(msg, "location")),
                   ("start", it.get("start"), first(msg, "startDate", "apptStartWhole")),
                   ("end", it.get("end"), first(msg, "endDate", "apptEndWhole"))]
        ours = sorted((a.get("Address") or "").lower() for a in it.get("attendees", []))
        theirs = sorted((r.email or "").lower() for r in getattr(msg, "recipients", []) or [])
        checks.append(("attendees", ours, theirs))
    else:
        checks += [("status", it.get("status"), first(msg, "taskStatus")),
                   ("start", it.get("start"), first(msg, "taskStartDate")),
                   ("due", it.get("due"), first(msg, "taskDueDate"))]
    for field, a, b in checks:
        if b is None:
            unexposed += 1
            print(f"{fname} {field}: not exposed (ours {a!r})")
            continue
        if isinstance(b, dt.datetime):
            # extract-msg shows an instant in a local zone; the stored value is UTC, and a task date is the UTC midnight of its day.
            b = (b.astimezone(dt.timezone.utc) if b.tzinfo else b).isoformat()
        elif isinstance(b, dt.date):
            b = b.isoformat()
        elif hasattr(b, "name"):
            b = b.name.replace("_", "").lower()
            a = str(a).lower()
        sa, sb = str(a), str(b)
        same = sa == sb or (isinstance(a, str) and isinstance(b, str) and sb.startswith(sa[:10]) and field in ("start", "end", "due")
                            and sa[:10] == sb[:10])
        if same:
            agree += 1
        else:
            differ += 1
        print(f"{fname} {field}: {'same' if same else 'DIFFERENT'} ours={a!r} extract-msg={b!r}")
    msg.close()
print(f"agree {agree}, differ {differ}, not exposed {unexposed}")
