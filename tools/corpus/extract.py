#!/usr/bin/env python3
"""Corpus helpers used by fetch.sh (standard library only, portable to macOS).

    extract.py mime4j  TARBALL OUTDIR      message files (*.msg) from the two test-message folders, nothing else
    extract.py sample  TARBALL OUTDIR [N]  every file whose name hashes into 1/N (default 100) from a .tar.gz of messages
    extract.py mbox    MBOX OUTDIR         split an mbox at "From " lines into one file per message

The corpora hold other people's mail or third-party test files: nothing here is committed or printed.
"""
import hashlib
import os
import sys
import tarfile


def mime4j(tarball, out):
    wanted = ("core/src/test/resources/testmsgs/", "core/src/test/resources/mimetools-testmsgs/")
    n = 0
    with tarfile.open(tarball, "r:gz") as t:
        for m in t:
            if not m.isfile() or not m.name.endswith(".msg"):
                continue
            rel = m.name.split("/", 1)[1] if "/" in m.name else m.name
            for w in wanted:
                if rel.startswith(w):
                    dest = os.path.join(out, w.split("/")[-2], rel[len(w):])
                    os.makedirs(os.path.dirname(dest), exist_ok=True)
                    with open(dest, "wb") as f:
                        f.write(t.extractfile(m).read())
                    n += 1
    print("mime4j: %d message files" % n)


def sample(tarball, out, one_in):
    n = 0
    with tarfile.open(tarball, "r|gz") as t:
        for m in t:
            if not m.isfile():
                continue
            if int.from_bytes(hashlib.sha1(m.name.encode()).digest()[:4], "big") % one_in != 0:
                continue
            dest = os.path.join(out, m.name)
            os.makedirs(os.path.dirname(dest), exist_ok=True)
            with open(dest, "wb") as f:
                f.write(t.extractfile(m).read())
            n += 1
    print("sample: %d files" % n)


def mbox(path, out):
    """A line starting with "From " that follows a blank line (or starts the file) opens a message.
    This is a means of getting messages out for the differential run, not the mbox reader."""
    os.makedirs(out, exist_ok=True)
    n = 0
    cur = None
    prev_blank = True
    with open(path, "rb") as f:
        for line in f:
            if prev_blank and line.startswith(b"From "):
                if cur:
                    cur.close()
                n += 1
                cur = open(os.path.join(out, "%06d.eml" % n), "wb")
                prev_blank = False
                continue
            if cur:
                cur.write(line)
            prev_blank = line in (b"\n", b"\r\n")
    if cur:
        cur.close()
    print("mbox: %d messages" % n)


if __name__ == "__main__":
    a = sys.argv[1:]
    if len(a) >= 3 and a[0] == "mime4j":
        mime4j(a[1], a[2])
    elif len(a) >= 3 and a[0] == "sample":
        sample(a[1], a[2], int(a[3]) if len(a) > 3 else 100)
    elif len(a) == 3 and a[0] == "mbox":
        mbox(a[1], a[2])
    else:
        sys.exit(__doc__)
