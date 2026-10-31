#!/usr/bin/env python3
"""Checks the shared read view against the OneMail oracle driver's dump and against Python's `email`.

    viewcheck.py --cli "dotnet /path/RecursiveAI.OneMail.Cli.dll" --driver /path/onemail-driver-dir/OneMailDump \
                 --root CORPUS_DIR --work WORKDIR [--limit N]

What it does:
  1. Runs `onemail inspect --json` over CORPUS_DIR (a directory of messages, or an mbox file / Maildir). Every item comes back
     with the file and byte range it was read from.
  2. Writes each item's raw bytes under WORKDIR/items (a message file is used in place) and runs the OneMail oracle driver
     (the dump form of tools/oracles/README.md, built from the MIME tree directly, not through the view) over them.
  3. Compares per item, view against dump: subject, from/to/cc/bcc/reply-to, date, Message-ID, In-Reply-To, References and
     the attachment list (names and count; the dump's tree is run through an independent Python version of the attachment
     rule documented in docs/shared-view.md).
  4. Compares attachment names and count with Python 3.13 `email` (policy.default, EmailMessage.iter_attachments, nested
     multiparts flattened, message/rfc822 as one attachment).
Differences are grouped by a short signature; the report prints each group with a count and one example. Exit status is the
number of groups that no rule in EXPLAINED covers, capped at 100.
"""
import argparse
import collections
import email
import email.policy
import json
import os
import re
import subprocess
import sys

# Differences with a documented reason. Keys are signatures printed by the report; values name the ambiguity entry.
EXPLAINED = {
    "py-att:inline-parts-in-related": "V1: parts addressed by Content-ID are inline attachments in the view; Python's iter_attachments skips the whole multipart/related",
    "py-att:root-leaf": "V1: a message that is one non-body part (or one message/rfc822) is its own attachment in the view; Python lists none for a message that is not multipart",
    "py-att:name-resolution": "N1, N2, N6, N8: file names resolved by the documented parameter rules (extended form wins, encoded words decoded, unquoted value to the next ';', blanks kept)",
    "py-att:nested-body": "V1: the view looks for the body inside nested multipart/signed and multipart/mixed parts; Python lists the nested multipart's text part as an attachment",
    "py-att:ours-superset": "V1 with B19: a second text part of a multipart/mixed whose structure is ambiguous is a file in the view",
}

ADDR_KEYS = [("from", "from"), ("to", "to"), ("cc", "cc"), ("bcc", "bcc"), ("replyTo", "reply-to")]


def run_lines(cmd, stdin=None, cwd=None):
    p = subprocess.run(cmd, input=stdin, stdout=subprocess.PIPE, stderr=subprocess.PIPE, cwd=cwd, check=False)
    return p


# ---- the attachment rule, written again from docs/shared-view.md over the dump tree ----

def media(e):
    return e.get("contentType") or "text/plain"


def find_body(e, sub):
    ct = media(e)
    if ct.startswith("multipart/"):
        s = ct.split("/", 1)[1]
        kids = e.get("children") or []
        if s == "alternative":
            for c in reversed(kids):
                r = find_body(c, sub)
                if r is not None:
                    return r
            return None
        if s in ("related", "signed"):
            return find_body(kids[0], sub) if kids else None
        for c in kids:
            r = find_body(c, sub)
            if r is not None:
                return r
        return None
    if e.get("message") is not None or ct.startswith("message/"):
        return None
    if ct == "text/" + sub and e.get("disposition") != "attachment":
        return e
    return None


def content_id(e):
    for h in e.get("headers") or []:
        if h["name"].lower() == "content-id":
            m = re.search(r"<([^<>]*)>", h["raw"])
            if m and m.group(1).strip():
                return m.group(1).strip()
            if not m and h["raw"].strip():
                return h["raw"].strip()
    return None


def tree_attachments(root):
    text, html = find_body(root, "plain"), find_body(root, "html")
    out = []

    def rec(e, in_alt=False):
        ct = media(e)
        if ct.startswith("multipart/") and e.get("body") is None and e.get("message") is None:
            for c in e["children"]:
                rec(c, ct == "multipart/alternative")
            return
        if e is text or e is html:
            return
        if in_alt and ct.startswith("text/") and e.get("disposition") != "attachment":
            return
        out.append((e.get("filename") or "", e.get("disposition") != "attachment" and content_id(e) is not None))

    rec(root)
    return out


# ---- Python's own notion ----

def py_attachments(data):
    msg = email.message_from_bytes(data, policy=email.policy.default)
    out = []

    def leaves(p):
        if p.get_content_maintype() == "multipart":
            payload = p.get_payload()
            if isinstance(payload, list):
                for c in payload:
                    yield from leaves(c)
        else:
            yield p

    try:
        for a in msg.iter_attachments():
            for p in leaves(a):
                out.append(p.get_filename() or "")
    except Exception:
        return None
    return out


def ids(dump, name):
    for h in dump["root"]["headers"]:
        if h["name"].lower() == name:
            return re.findall(r"<([^<>]*)>", h["raw"])
    return []


def addr_list(dump, key):
    return [(a["name"] or "", a["addr"]) for a in (dump.get("addresses") or {}).get(key, [])]


def header_value(dump, name):
    for h in dump["root"]["headers"]:
        if h["name"].lower() == name:
            return h["value"]
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cli", required=True)
    ap.add_argument("--driver", required=True)
    ap.add_argument("--root", required=True)
    ap.add_argument("--work", required=True)
    ap.add_argument("--limit", type=int, default=0)
    args = ap.parse_args()

    os.makedirs(args.work, exist_ok=True)
    jsonl = os.path.join(args.work, "inspect.jsonl")
    with open(jsonl, "wb") as f:
        p = subprocess.run(args.cli.split() + ["inspect", "--json", args.root], stdout=f, stderr=subprocess.PIPE, check=False)
    sys.stderr.write(p.stderr.decode("utf-8", "replace"))
    if p.returncode != 0:
        print("inspect failed", p.returncode)
        return 100

    items = []
    with open(jsonl, "rb") as f:
        for line in f:
            r = json.loads(line)
            if r["type"] == "item":
                items.append(r)
    if args.limit:
        items = items[: args.limit]

    # Raw bytes per item.
    items_dir = os.path.join(args.work, "items")
    os.makedirs(items_dir, exist_ok=True)
    names, blobs = [], []
    for i, r in enumerate(items):
        pv = r["provenance"]
        with open(pv["location"], "rb") as fh:
            fh.seek(pv["offset"])
            data = fh.read(pv["length"])
        name = "%07d.eml" % i
        with open(os.path.join(items_dir, name), "wb") as out:
            out.write(data)
        names.append(name)
        blobs.append(data)

    dp = run_lines(args.driver.split() + ["--root", items_dir], stdin=("\n".join(names) + "\n").encode())
    dumps = [json.loads(l) for l in dp.stdout.decode("utf-8", "replace").splitlines() if l.strip()]
    if len(dumps) != len(items):
        print("driver returned %d records for %d items" % (len(dumps), len(items)))
        return 100

    groups = collections.OrderedDict()
    totals = collections.Counter()

    where = [""]

    def note(sig, example):
        g = groups.setdefault(sig, [0, []])
        g[0] += 1
        if len(g[1]) < 4:
            g[1].append(example + "  @" + where[0])

    for r, d, data in zip(items, dumps, blobs):
        totals["items"] += 1
        where[0] = "%s+%d (item %d)" % (r["provenance"]["location"], r["provenance"]["offset"], totals["items"] - 1)
        if d.get("error"):
            note("driver-error", d["error"][:80])
            continue
        # -- view versus the driver's dump
        if (r["subject"] or None) != (header_value(d, "subject") or None):
            note("dump:subject", "%r vs %r" % (r["subject"], header_value(d, "subject")))
        for jk, dk in ADDR_KEYS:
            mine = [(a["name"] or "", a["addr"]) for a in r[jk]]
            if mine != addr_list(d, dk):
                note("dump:" + jk, "%r vs %r" % (mine[:2], addr_list(d, dk)[:2]))
        dd = d.get("date")
        rd = r["date"]
        if (dd is None) != (rd is None) or (dd and (dd["utc"] != rd["utc"] or dd.get("offsetMinutes") != rd.get("offsetMinutes"))):
            note("dump:date", "%r vs %r" % (rd, dd))
        di = d["ids"]
        if (r["messageId"] or None) != (di["messageId"] or None):
            note("dump:message-id", "%r vs %r" % (r["messageId"], di["messageId"]))
        if r["inReplyTo"] != di["inReplyTo"]:
            note("dump:in-reply-to", "%r vs %r" % (r["inReplyTo"][:2], di["inReplyTo"][:2]))
        if r["references"] != di["references"]:
            note("dump:references", "%r vs %r" % (r["references"][:2], di["references"][:2]))
        mine_att = [(a["name"] or "", a["inline"]) for a in r["attachments"]]
        tree_att = tree_attachments(d["root"])
        if mine_att != tree_att:
            note("dump:attachments", "%r vs %r" % (mine_att[:3], tree_att[:3]))
        # -- attachments versus Python
        py = py_attachments(data)
        if py is None:
            totals["py-failed"] += 1
            continue
        mine_names = sorted(n for n, _ in mine_att)
        if mine_names != sorted(py):
            ct = d["root"]["contentType"]
            non_inline = sorted(n for n, inl in mine_att if not inl)
            ct = d["root"]["contentType"]
            root_leaf = (not ct.startswith("multipart/")) or d["root"].get("body") is not None
            if non_inline == sorted(py):
                note("py-att:inline-parts-in-related", "%d vs %d" % (len(mine_names), len(py)))
            elif root_leaf and not py:
                note("py-att:root-leaf", ct)
            elif len(mine_names) == len(py):
                note("py-att:name-resolution", "%r vs %r" % (sorted(set(mine_names) - set(py)), sorted(set(py) - set(mine_names))))
            elif not (collections.Counter(mine_names) - collections.Counter(py)):
                note("py-att:nested-body", "%r vs %r  [%s]" % (mine_names[:4], py[:4], ct))
            elif not (collections.Counter(py) - collections.Counter(mine_names)):
                note("py-att:ours-superset", "%r vs %r  [%s]" % (mine_names[:4], py[:4], ct))
            else:
                note("py-att:other", "%r vs %r  [%s]" % (mine_names[:4], py[:4], ct))
        else:
            totals["py-agree"] += 1

    unexplained = 0
    print("items: %d, python attachment lists agreeing: %d" % (totals["items"], totals["py-agree"]))
    for sig, (n, ex) in sorted(groups.items()):
        status = "EXPLAINED " + EXPLAINED[sig] if sig in EXPLAINED else "UNEXPLAINED"
        if sig not in EXPLAINED:
            unexplained += 1
        print("%-34s %7d  %s" % (sig, n, status))
        for e in ex:
            print("    e.g. " + e[:330])
    print("unexplained groups: %d" % unexplained)
    return min(unexplained, 100)


if __name__ == "__main__":
    sys.exit(main())
