#!/usr/bin/env python3
"""Compares mbox message boundaries: OneMail against Python's mailbox module and MimeKit's mbox parser.

usage: compare_mbox.py --omr "<cmd for OneMailRanges>" --mk "<cmd for MailboxDump>" --py "<python> py_ranges.py" <file>...

Three comparisons per file:
  1. OneMail in oracle-compatible mode (every "From " line delimits, Content-Length ignored) against Python:
     message count, envelope offset and message end must match. One explained difference: Python keeps the CR LF of a
     CRLF separator line inside the message, OneMail does not (ambiguities.md, mailbox section, S3).
  2. The same OneMail run against MimeKit: message count, envelope offset and record end. MimeKit raising on a file
     is reported as such and the file is skipped for this comparison (S4).
  3. OneMail's defaults against its own oracle-compatible run: a difference must be explained by a defect the default
     run reported (AmbiguousFromLine -> S1, Content-Length used -> S2).
Prints a summary and every unexplained difference; exit status 1 if there is any.
"""
import argparse
import shlex
import subprocess
import sys


def run(cmd, files, extra=()):
    out = {}
    # Batches keep the command line short.
    for i in range(0, len(files), 200):
        batch = files[i:i + 200]
        p = subprocess.run(shlex.split(cmd) + list(extra) + batch, capture_output=True, text=True)
        cur = None
        for line in p.stdout.splitlines():
            f = line.split("\t")
            if len(f) >= 2 and f[0] in batch and (len(f) == 2 or f[1] == "error"):
                cur = f[0]
                out[cur] = "error" if f[1] == "error" else []
            elif f and f[0] == "error" and cur is not None:
                out[cur] = "error"
            elif cur is not None and isinstance(out[cur], list):
                out[cur].append(tuple(f))
    return out


def has_bare_from(path):
    # A line that is only "From ": every other reader splits there, MimeKit does not (S5).
    with open(path, "rb") as fh:
        data = fh.read()
    return data.startswith((b"From \n", b"From \r\n")) or b"\nFrom \n" in data or b"\nFrom \r\n" in data


def ints(rows, *cols):
    return [tuple(int(r[c]) for c in cols) for r in rows]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--omr", required=True)
    ap.add_argument("--mk", required=True)
    ap.add_argument("--py", required=True)
    ap.add_argument("files", nargs="+")
    a = ap.parse_args()
    files = a.files

    omr_any = run(a.omr, files, ["mbox", "--any", "--variant", "mboxrd"])
    omr_def = run(a.omr, files, ["mbox"])
    py = run(a.py, files)
    mk = run(a.mk, files)

    counts = {"files": len(files), "py_same": 0, "py_S3": 0, "mk_same": 0, "mk_skipped_S4": 0, "mk_S5": 0, "def_same": 0, "def_S1": 0, "def_S2": 0}
    unexplained = []
    messages = 0
    for f in files:
        oa = omr_any.get(f)
        od = omr_def.get(f)
        if not isinstance(oa, list) or not isinstance(od, list):
            unexplained.append((f, "OneMail failed"))
            continue
        messages += len(oa)
        crlf = False
        # 1. against Python
        p = py.get(f)
        if isinstance(p, list):
            a_pairs = ints(oa, 0, 1)
            p_pairs = ints(p, 0, 1)
            if a_pairs == p_pairs:
                counts["py_same"] += 1
            elif len(a_pairs) == len(p_pairs) and all(x[0] == y[0] and (x[1] == y[1] or x[1] == y[1] - 2) for x, y in zip(a_pairs, p_pairs)):
                counts["py_S3"] += 1
            else:
                unexplained.append((f, "vs Python: %d vs %d messages or ranges" % (len(a_pairs), len(p_pairs))))
        else:
            unexplained.append((f, "Python failed: %r" % (p,)))
        # 2. against MimeKit
        m = mk.get(f)
        if isinstance(m, list):
            a_rec = [(int(r[0]), int(r[2])) for r in oa]
            m_rec = [(int(r[0]), int(r[2])) for r in m]
            if a_rec == m_rec:
                counts["mk_same"] += 1
            elif has_bare_from(f):
                counts["mk_S5"] += 1
            else:
                unexplained.append((f, "vs MimeKit: %d vs %d messages or ranges" % (len(a_rec), len(m_rec))))
        else:
            counts["mk_skipped_S4"] += 1
        # 3. defaults against compatible mode
        if [r[:3] for r in od] == [r[:3] for r in oa]:
            counts["def_same"] += 1
        else:
            kinds = " ".join(r[3] for r in od)
            if "AmbiguousFromLine" in kinds or not od:
                counts["def_S1"] += 1
            elif any(r[4] != "-1" for r in od):
                counts["def_S2"] += 1
            else:
                unexplained.append((f, "defaults differ with no defect to explain it"))
    print("messages (OneMail, compatible mode): %d" % messages)
    for k, v in counts.items():
        print("%-14s %d" % (k, v))
    for f, why in unexplained:
        print("UNEXPLAINED", f, why)
    return 1 if unexplained else 0


if __name__ == "__main__":
    sys.exit(main())
