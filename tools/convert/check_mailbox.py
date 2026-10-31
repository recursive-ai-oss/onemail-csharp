#!/usr/bin/env python3
"""Reads a converted destination with Python's mailbox/email modules and prints "folder<TAB>count" lines, sorted.

  check_mailbox.py DEST eml|mbox|maildir

The same lines come from `ConvertTool store ... WORK DUMP`; the run script diffs the two. Every message is also parsed by
email.message_from_bytes and must have a header block (a Subject or From field)."""
import email
import mailbox
import os
import sys
from collections import Counter

dest, fmt = sys.argv[1], sys.argv[2]
counts = Counter()
bad = 0


def check(raw):
    global bad
    m = email.message_from_bytes(raw)
    if m.get("Subject") is None and m.get("From") is None and m.get("Message-ID") is None:
        bad += 1


if fmt == "eml":
    for root, dirs, files in os.walk(dest):
        dirs[:] = [d for d in dirs if d != "_items"]
        for f in files:
            if f.endswith(".eml"):
                rel = os.path.relpath(root, dest)
                folder = "" if rel == "." else rel
                with open(os.path.join(root, f), "rb") as fh:
                    check(fh.read())
                counts[folder] += 1
elif fmt == "mbox":
    for root, dirs, files in os.walk(dest):
        dirs[:] = [d for d in dirs if d != "_items"]
        for f in files:
            if f.endswith(".mbox"):
                path = os.path.join(root, f)
                rel = os.path.relpath(path, dest)
                folder = "" if rel.endswith("_root.mbox") else rel[:-5]
                box = mailbox.mbox(path)
                for msg in box:
                    check(msg.as_bytes())
                    counts[folder] += 1
elif fmt == "maildir":
    top = mailbox.Maildir(dest, factory=None, create=False)
    for name in [""] + top.list_folders():
        box = top if name == "" else top.get_folder(name)
        for key in box.iterkeys():
            check(box.get_bytes(key))
            counts[name.replace(".", "/")] += 1
for k in sorted(counts):
    print(f"{k}\t{counts[k]}")
print(f"# unparseable {bad}", file=sys.stderr)
