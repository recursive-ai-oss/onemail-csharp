#!/usr/bin/env python3
"""Checks stores written by OneMail with Python's `mailbox` module as the independent reader.

usage: py_check_written.py <dir written by `OneMailRanges write`>
  w-<variant>.mbox   Python's mailbox.mbox must find the messages the sidecar lists. Python does not unquote or read
                     Content-Length, so the check undoes the quoting and drops the Content-Length header itself.
  w-maildir          mailbox.Maildir must list the same folders, sub-directories, flags and contents as maildir.expected.
"""
import glob
import hashlib
import mailbox
import os
import re
import sys

bad = 0
out = sys.argv[1]
for path in sorted(glob.glob(os.path.join(out, "*.mbox"))):
    lines = open(path + ".expected").read().split("\n")
    variant, count = lines[0].split("\t")
    want = [x for x in lines[1:] if x]
    if variant == "mboxcl2":
        # Python's mbox ignores Content-Length and cl2 does not quote, so it would split inside messages (table entry X6).
        print("skip", os.path.basename(path), variant, "(not readable by Python's mailbox by design)")
        continue
    box = mailbox.mbox(path)
    box._generate_toc()
    got = []
    for key in sorted(box._toc):
        raw = box.get_bytes(key)
        if variant == "mboxrd":
            raw = re.sub(rb"(?m)^>(>*From )", rb"\1", raw)
        else:
            raw = re.sub(rb"(?m)^>From ", b"From ", raw)
        if variant in ("mboxcl", "mboxcl2"):
            raw = re.sub(rb"(?m)^Content-Length: \d+\n", b"", raw, count=1)
        got.append(hashlib.sha256(raw).hexdigest())
    ok = got == want
    bad += 0 if ok else 1
    print(("ok  " if ok else "FAIL"), os.path.basename(path), variant, "%d messages" % len(got))

md = os.path.join(out, "w-maildir")
if os.path.isdir(md):
    box = mailbox.Maildir(md, create=False)
    rows = []

    def collect(b, name):
        for key in b.iterkeys():
            m = b.get_message(key)
            raw = b.get_bytes(key)
            rows.append("\t".join([name, m.get_subdir(), key, m.get_flags(), hashlib.sha256(raw).hexdigest(), str(len(raw))]))

    collect(box, "")
    for f in box.list_folders():
        collect(box.get_folder(f), f)
    want = sorted(l for l in open(os.path.join(out, "maildir.expected")).read().split("\n") if l)
    ok = sorted(rows) == want
    bad += 0 if ok else 1
    print(("ok  " if ok else "FAIL"), "w-maildir", "%d messages" % len(rows))
    if not ok:
        print(sorted(rows)); print(want)
sys.exit(1 if bad else 0)
