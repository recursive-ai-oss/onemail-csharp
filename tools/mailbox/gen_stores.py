#!/usr/bin/env python3
"""Generates Maildir trees (with Python's own `mailbox.Maildir` as the independent writer, plus Dovecot- and Courier-style
control files written by hand) and Apple Mail style bundles of .emlx files. Seeds are fixed.

usage: gen_stores.py <outdir>
  <outdir>/maildir/   a Maildir++ tree: inbox, .Archive, .Archive.2024, .Work
  <outdir>/emlx/      Old.mbox/Messages/*.emlx and New.mbox/<GUID>/Data/1/2/Messages/*.emlx (+ .partial.emlx, Info.plist)
"""
import hashlib
import mailbox
import os
import random
import shutil
import sys


def message(i, rng):
    lines = [b"Subject: generated %d" % i, b"Message-ID: <store-%d@example.com>" % i, b"From: a@example.com",
             b"To: b@example.com", b"Date: Mon, 01 Jan 2024 00:00:%02d +0000" % (i % 60), b"", b"body line one",
             b"From the body", b">From quoted", b"body %d" % rng.randrange(10 ** 6)]
    return b"\n".join(lines) + b"\n"


def gen_maildir(root, rng):
    box = mailbox.Maildir(root, create=True)
    folders = {"": box}
    folders["Archive"] = box.add_folder("Archive")
    folders["Archive.2024"] = box.add_folder("Archive.2024")  # Maildir++ names are flat: the dot is the separator
    folders["Work"] = box.add_folder("Work")
    n = 0
    for name, f in folders.items():
        for _ in range(rng.randrange(6, 14)):
            m = mailbox.MaildirMessage(message(n, rng))
            subdir = rng.choice(["cur", "cur", "new"])
            m.set_subdir(subdir)
            if subdir == "cur":
                m.set_flags("".join(sorted(rng.sample("DFPRST", rng.randrange(0, 4)))))
            f.add(m)
            n += 1
    # Dovecot-style extras in the Work folder: keyword letters, size fields, keyword names and a UID list.
    work = os.path.join(root, ".Work")
    with open(os.path.join(work, "cur", "1700000001.M1P1.dovecot,S=40,W=41:2,Sab"), "wb") as f:
        f.write(message(900, rng))
    with open(os.path.join(work, "dovecot-keywords"), "w") as f:
        f.write("0 $Junk\n1 $Label1\n")
    with open(os.path.join(work, "dovecot-uidlist"), "w") as f:
        f.write("3 V1700000000 N7 G0123456789abcdef0123456789abcdef\n6 :1700000001.M1P1.dovecot,S=40,W=41:2,Sab\n")
    # Courier-style keyword list in Archive.
    arch = os.path.join(root, ".Archive")
    os.makedirs(os.path.join(arch, "courierimapkeywords"), exist_ok=True)
    cur = sorted(os.listdir(os.path.join(arch, "cur")))
    if cur:
        unique = cur[0].split(":")[0]
        with open(os.path.join(arch, "courierimapkeywords", ":list"), "w") as f:
            f.write("Work\nPersonal\n\n%s:0 1\n" % unique)


PLIST = """<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
\t<key>conversation-id</key>
\t<integer>%d</integer>
\t<key>date-received</key>
\t<real>%d</real>
\t<key>date-sent</key>
\t<real>%d</real>
\t<key>flags</key>
\t<integer>%d</integer>
\t<key>remote-id</key>
\t<string>%d</string>
</dict>
</plist>
"""


def emlx_bytes(i, rng, crlf=False):
    msg = message(i, rng)
    if crlf:
        msg = msg.replace(b"\n", b"\r\n")
    flags = rng.choice([0, 1, 1, 17, 65, 256 + 1, 8589934593])
    plist = (PLIST % (i, 1700000000 + i, 1699999000 + i, flags, 1000 + i)).encode()
    return b"%d\n" % len(msg) + msg + plist, msg, flags


def gen_emlx(root, rng):
    expected = []
    i = 0
    for base, sub in [("Old.mbox/Messages", None), ("New.mbox/0A1B2C3D-0000-4000-8000-0123456789AB/Data/1/2/Messages", None)]:
        d = os.path.join(root, base)
        os.makedirs(d, exist_ok=True)
        for n in range(1, 8):
            data, msg, flags = emlx_bytes(i, rng, crlf=(n % 4 == 0))
            name = "%d.partial.emlx" % n if n == 3 else "%d.emlx" % n
            with open(os.path.join(d, name), "wb") as f:
                f.write(data)
            expected.append((os.path.join(base, name), hashlib.sha256(msg).hexdigest(), flags))
            i += 1
    os.makedirs(os.path.join(root, "Old.mbox", "Attachments", "3", "1"), exist_ok=True)
    with open(os.path.join(root, "Old.mbox", "Attachments", "3", "1", "a.txt"), "wb") as f:
        f.write(b"attachment bytes")
    with open(os.path.join(root, "Old.mbox", "Info.plist"), "wb") as f:
        f.write(b'<?xml version="1.0" encoding="UTF-8"?>\n<plist version="1.0">\n<dict>\n\t<key>MailboxName</key>\n\t<string>Old</string>\n</dict>\n</plist>\n')
    with open(os.path.join(root, "emlx.expected"), "w") as f:
        for path, sha, flags in sorted(expected):
            f.write("%s\t%s\t%d\n" % (path.replace(os.sep, "/"), sha, flags))


def main():
    out = sys.argv[1]
    rng = random.Random(20261031)
    for sub in ("maildir", "emlx"):
        shutil.rmtree(os.path.join(out, sub), ignore_errors=True)
    os.makedirs(out, exist_ok=True)
    gen_maildir(os.path.join(out, "maildir"), rng)
    gen_emlx(os.path.join(out, "emlx"), rng)
    print("wrote", out)


if __name__ == "__main__":
    main()
