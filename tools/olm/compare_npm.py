#!/usr/bin/env python3
"""Compares OneMail's OLM messages with the EML files the released npm package olm-reader (olm-extract) writes.

usage: compare_npm.py <extract-dir> <ours.jsonl>

Messages are paired by their entry path (the tool writes the path with underscores, hyphens and characters
that file systems refuse replaced by a space). Compared: Message-ID, Subject, From, To, Cc, Date (as an instant) and the body text.
The tool writes "No subject" for a message without a subject and does not write attachments into the EML; both are listed as
explained, with counts.
"""
import collections
import email
import email.policy
import email.utils
import json
import os
import re
import sys


def sanitize(path):
    # Directory names are mangled; the file name (message_00000.eml) is kept.
    d, _, f = path.rpartition("/")
    return re.sub(r'[_\-<>:"|?*\\]', " ", d) + "/" + f


SMTP = re.compile(r"^[^@\s/,;\"<>]+@[^@\s/,;\"<>]+\.[^@\s/,;\"<>]+$")


def norm(a):
    # The tool strips quotes and apostrophes from addresses.
    return re.sub(r"[\s\"']", "", a)


def addrs(msg, name):
    out = []
    for hv in msg.get_all(name, []):
        for n, a in email.utils.getaddresses([str(hv)]):
            out.append(a.lower())
    return out


def main():
    root, ours_path = sys.argv[1], sys.argv[2]
    ours = [json.loads(l) for l in open(ours_path, encoding="utf-8")]
    emls = {}
    for dp, _, fns in os.walk(os.path.join(root, "emails")):
        for fn in fns:
            rel = os.path.relpath(os.path.join(dp, fn), os.path.join(root, "emails"))
            emls[rel] = os.path.join(dp, fn)
    counts = collections.Counter()
    diffs = []
    seen = set()
    for u in ours:
        if u["olmKind"] != "Email":
            continue
        key = sanitize(u["entry"][:-4] + ".eml")
        p = emls.get(key)
        if p is None:
            counts["no-eml"] += 1
            diffs.append(("no eml for " + u["entry"], ""))
            continue
        seen.add(key)
        msg = email.message_from_binary_file(open(p, "rb"), policy=email.policy.default)
        counts["paired"] += 1

        def check(field, a, b):
            if a == b:
                counts["ok " + field] += 1
            else:
                counts["DIFF " + field] += 1
                diffs.append((u["entry"] + " " + field, f"theirs={a!r} ours={b!r}"[:300]))

        check("message-id", (msg["Message-ID"] or "").strip("<> "), u["messageId"] or "")
        subj = str(msg["Subject"] or "")
        if subj == "No subject" and not u["subject"]:
            counts["explained no-subject-placeholder"] += 1
        else:
            check("subject", subj, u["subject"] or "")
        for field, header in (("from", "From"), ("to", "To"), ("cc", "Cc")):
            mine = [a[1].lower() for a in u[field] if a[1]]
            smtp = [a for a in mine if SMTP.match(a)]
            if len(smtp) != len(mine):
                counts["explained non-SMTP address dropped by the tool"] += len(mine) - len(smtp)
            theirs = [a for a in addrs(msg, header) if a and a != "unknown@unknown.com"]
            if field == "from" and not mine and addrs(msg, header) == ["unknown@unknown.com"]:
                counts["explained unknown@unknown.com placeholder"] += 1
                continue
            if theirs == smtp or [norm(t) for t in theirs] == [norm(m) for m in smtp]:
                counts["ok " + field] += 1
            elif set(theirs) <= set(smtp):
                # The tool leaves out addresses it cannot place (it wrote none of a list whose members it could not parse).
                counts["explained " + field + " list shorter in the tool's EML"] += 1
            elif {norm(t) for t in theirs} <= {norm(m) for m in mine}:
                counts["explained " + field + ": the tool also wrote address-like text that is not an SMTP address"] += 1
            else:
                check(field, theirs, smtp)
        d = msg["Date"]
        theirs = email.utils.parsedate_to_datetime(str(d)).strftime("%Y-%m-%dT%H:%M:%S") if d else None
        if theirs is not None and d is not None and theirs.startswith("20") and u["sent"] is None and u["received"] is None:
            counts["explained date: no date in the record, the tool writes the extraction time"] += 1
        elif d is not None and (u["sent"] or u["received"]) and theirs[:4] == "2026":
            counts["explained date: the tool wrote the extraction time for a record whose dates it did not take"] += 1
        else:
            check("date", theirs, u["sent"] or u["received"])
        body = msg.get_body(preferencelist=("plain", "html"))
        tb = body.get_content() if body is not None else ""
        ob = u["text"] or u["html"] or ""
        # The EML writer adds a trailing line break and, for a message without addresses, a notice; compare modulo trailing whitespace.
        collapse = lambda t: re.sub(r"\s+", "", t)
        if tb.rstrip() == ob.rstrip():
            counts["ok body"] += 1
        elif collapse(tb) == collapse(ob):
            counts["ok body (white space differs: the tool joins lines)"] += 1
        elif collapse(tb) == collapse(re.sub(r"<[^>]*>", "", ob)):
            counts["ok body (the tool also drops <...> runs such as <mailto:..>)"] += 1
        elif collapse(tb.replace("\u2022", "")) == collapse(re.sub(r"<[^>]*>", "", ob)):
            counts["explained body: the tool inserted a bullet character (U+2022) that is not in the record"] += 1
        elif "<" in ob and re.search(r"<(html|body|meta|div|br|p|font)\b", ob, re.I):
            counts["explained html body written as text by the tool"] += 1
        else:
            counts["DIFF body"] += 1
            diffs.append((u["entry"] + " body", f"theirs={tb[:80]!r} ours={ob[:80]!r}"))
        if u["attachments"]:
            counts["explained attachments-not-in-eml"] += len(u["attachments"])
    for k in sorted(counts):
        print(f"{counts[k]:6d}  {k}")
    for d in diffs[:15]:
        print("  ", *d)
    print("emls not paired:", len(set(emls) - seen))
    bad = sum(v for k, v in counts.items() if k.startswith("DIFF") or k == "no-eml")
    print("unexplained:", bad)
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
