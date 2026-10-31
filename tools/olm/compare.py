#!/usr/bin/env python3
"""Compares OneMail's view of an OLM archive with the released olmreader jar's.

usage: compare.py <oracle.txt> <ours.jsonl>

oracle.txt  output of tools/olm/OlmDump.java (flat "kind#N.path = value" lines)
ours.jsonl  output of `OlmTool dump`

Items are matched by kind and by a key made of fields both sides carry; the comparison is of multisets, so the order the two
readers visit records in does not matter. Prints counts per kind and field, then every difference. Exit status 1 when any
difference is not listed in EXPLAINED (documented oracle limits).
"""
import collections
import hashlib
import json
import re
import sys

# Oracle limits, each documented in docs/olm.md.
EXPLAINED = {
    # The jar keys attachment bytes by file name in a map, so two attachments with one name collapse into one.
    "attachment-name-collision",
}


def unesc(s):
    out, i = [], 0
    while i < len(s):
        if s[i] == "\\" and i + 1 < len(s):
            n = s[i + 1]
            out.append({"n": "\n", "r": "\r", "\\": "\\"}.get(n, n))
            i += 2
        else:
            out.append(s[i])
            i += 1
    return "".join(out)


def load_oracle(path):
    items = collections.OrderedDict()
    for line in open(path, encoding="utf-8"):
        line = line.rstrip("\n")
        m = re.match(r"^([a-z]+)#(\d+)\.(.*?) = (.*)$", line)
        if not m:
            m = re.match(r"^([a-z]+)#(\d+)\.(.*?) =$", line)
            if not m:
                continue
            kind, n, p, v = m.group(1), m.group(2), m.group(3), ""
        else:
            kind, n, p, v = m.group(1), m.group(2), m.group(3), m.group(4)
        items.setdefault((kind, n), {})[p] = unesc(v)
    return items


def nz(s):
    return None if s in (None, "") else s


def addrs(d, listpath):
    """Address entries of a list: [(name or None, address or None)], skipping entries with neither."""
    out = []
    i = 0
    while True:
        base = f"{listpath}.EmailAddress[{i}]"
        a = d.get(base + ".OPFContactEmailAddressAddress")
        n = d.get(base + ".OPFContactEmailAddressName")
        if a is None and n is None and (base + ".OPFContactEmailAddressType") not in d:
            break
        if a or n:
            out.append((nz(n), nz(a)))
        i += 1
    return out


def oracle_email(d):
    atts = []
    for k, v in d.items():
        m = re.match(r"^attachmentFile\[(.*)\]$", k)
        if m:
            size, sha = v.split(" ")
            atts.append((m.group(1), int(size), sha))
    return {
        "subject": d.get("OPFMessageCopySubject.Value"),
        "from": addrs(d, "OPFMessageCopyFromAddresses"),
        "to": addrs(d, "OPFMessageCopyToAddresses"),
        "cc": addrs(d, "OPFMessageCopyCCAddresses"),
        "bcc": addrs(d, "OPFMessageCopyBCCAddresses"),
        "sender": addrs(d, "OPFMessageCopySenderAddress")[:1],
        "sent": nz(d.get("OPFMessageCopySentTime.Value")),
        "received": nz(d.get("OPFMessageCopyReceivedTime.Value")),
        "messageId": nz(d.get("OPFMessageCopyMessageID.Value")),
        "body": nz(d.get("OPFMessageCopyBody.Value")) or nz(d.get("OPFMessageCopyHTMLBody.Value")),
        "attachments": sorted(set(atts)),
    }


def ours_email(o):
    atts = sorted(set((a["name"], a["size"], a["sha256"]) for a in o["attachments"]))
    return {
        "subject": o["subject"],
        "from": [(nz(a[0]), nz(a[1])) for a in o["from"]],
        "to": [(nz(a[0]), nz(a[1])) for a in o["to"]],
        "cc": [(nz(a[0]), nz(a[1])) for a in o["cc"]],
        "bcc": [(nz(a[0]), nz(a[1])) for a in o["bcc"]],
        "sender": [(nz(a[0]), nz(a[1])) for a in o["sender"]][:1],
        "sent": o["sent"],
        "received": o["received"],
        "messageId": nz(o["messageId"]),
        "body": nz(o["text"]) or nz(o["html"]),
        "attachments": atts,
    }


def oracle_simple(kind, d):
    g = d.get
    if kind == "appointment":
        return {"subject": g("OPFCalendarEventCopySummary.Value"), "location": g("OPFCalendarEventCopyLocation.Value"),
                "uuid": g("OPFCalendarEventCopyUUID.Value"), "start": g("OPFCalendarEventCopyStartTime.Value"), "end": g("OPFCalendarEventCopyEndTime.Value"),
                "notes": nz(g("OPFCalendarEventCopyDescriptionPlain.Value"))}
    if kind == "contact":
        return {"subject": g("OPFContactCopyDisplayName.Value"), "first": g("OPFContactCopyFirstName.Value"), "last": g("OPFContactCopyLastName.Value"),
                "notes": nz(g("OPFContactCopyNotesPlain.Value"))}
    if kind == "task":
        return {"subject": g("OPFTaskCopyName.Value"), "notes": nz(g("OPFTaskCopyNotePlain.Value")), "due": g("OPFTaskCopyDueDateTime.Value")}
    if kind == "note":
        return {"subject": g("OPFNoteCopyTitle.Value"), "html": nz(g("OPFNoteCopyText.Value")), "created": g("OPFNoteCopyCreationDate.Value")}
    if kind == "group":
        return {"subject": g("OPFGroupCopyDisplayName.Value"), "members": addrs(d, "OPFGroupCopyMemberList")}
    raise ValueError(kind)


def ours_simple(kind, o, raw):
    if kind == "appointment":
        return {"subject": o["subject"], "location": raw.get("location"), "uuid": raw.get("uuid"), "start": raw.get("start"), "end": raw.get("end"),
                "notes": nz(o["text"])}
    if kind == "contact":
        return {"subject": o["subject"], "first": raw.get("first"), "last": raw.get("last"), "notes": nz(o["text"])}
    if kind == "task":
        return {"subject": o["subject"], "notes": nz(o["text"]), "due": raw.get("due")}
    if kind == "note":
        return {"subject": o["subject"], "html": nz(o["html"]), "created": o["created"]}
    if kind == "group":
        return {"subject": o["subject"], "members": raw.get("members")}
    raise ValueError(kind)


def key(x):
    return json.dumps(x, sort_keys=True, ensure_ascii=False)


def main():
    oracle = load_oracle(sys.argv[1])
    ours = [json.loads(l) for l in open(sys.argv[2], encoding="utf-8")]
    collisions = sum(1 for u in ours if len({(a["name"], a["sha256"]) for a in u["attachments"]}) != len(u["attachments"]))
    if collisions:
        print(f"explained attachment-name-collision: {collisions} item(s) hold attachments that share a name and bytes; the jar's map keeps one")
    kinds = {"email": "Email", "appointment": "Appointment", "contact": "Contact", "task": "Task", "note": "Note", "group": "Group"}
    bad = 0
    for k, olmk in kinds.items():
        o_items = [d for (kk, _), d in oracle.items() if kk == k]
        u_items = [u for u in ours if u["olmKind"] == olmk]
        print(f"{k}: oracle {len(o_items)}, ours {len(u_items)}", end="")
        if k == "email":
            a = [oracle_email(d) for d in o_items]
            b = [ours_email(u) for u in u_items]
        else:
            a = [oracle_simple(k, d) for d in o_items]
            b = []
            for u in u_items:
                raw = u.get("raw", {})
                b.append(ours_simple(k, u, raw))
        ca, cb = collections.Counter(map(key, a)), collections.Counter(map(key, b))
        only_a, only_b = ca - cb, cb - ca
        print(f", matched {sum((ca & cb).values())}, oracle-only {sum(only_a.values())}, ours-only {sum(only_b.values())}")
        for x in list(only_a)[:5]:
            bad += 1
            print("   oracle-only:", x[:300])
        for x in list(only_b)[:5]:
            bad += 1
            print("   ours-only:  ", x[:300])
    # Categories: the jar delivers one bean holding the list.
    names = sorted(v for (kk, _), d in oracle.items() if kk == "categories" for p, v in d.items() if p.endswith("OPFCategoryCopyName"))
    ours_names = sorted(u["subject"] for u in ours if u["olmKind"] == "Category")
    print(f"category: oracle {len(names)}, ours {len(ours_names)}, {'equal' if names == ours_names else 'DIFFERENT'}")
    if names != ours_names:
        bad += 1
    print("unexplained:", bad)
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
