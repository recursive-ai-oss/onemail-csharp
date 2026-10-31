#!/usr/bin/env python3
"""Generates mbox files for the mailbox boundary checks.

Every file is written by an independent writer (Python code here, or Python's own `mailbox` module) and comes with a
sidecar `<name>.expected` that says what a correct reader must find: the variant, the message count and the SHA-256 of
each message as the writer was given it. Seeds are fixed, so the output is reproducible.

usage: gen_mboxes.py <outdir> [--messages N]
"""
import hashlib
import mailbox
import os
import random
import re
import sys

ENV = b"From sender@example.com Mon Jan  1 00:00:00 2024"
ENV_LOOKALIKE = b"From other@example.org Tue Feb  2 10:11:12 2024"

TRICKY = [
    b"plain line",
    b"another plain line with some words",
    b"",
    b"From the desk of someone",          # starts with From but is not an envelope line
    b"From",
    b"Fromage is cheese",
    b" From indented",
    b">From once quoted",
    b">>From twice quoted",
    b">>>From thrice quoted",
    b"x From mid line",
    b"From x@y.z Wed Mar  3 03:04:05 2024",   # reads as an envelope line when unquoted
    b"From   spaces@y.z   Thu Apr  4 04:05:06 2024",
    b"> quoted reply text",
    b"A" * 300,
]


def make_message(rng, i, nbody, tricky, crlf, labels=False):
    headers = [b"Subject: test message %d" % i, b"Date: Mon, 01 Jan 2024 00:00:%02d +0000" % (i % 60),
               b"Message-ID: <gen-%d@example.com>" % i, b"From: a@example.com", b"To: b@example.com"]
    if labels:
        headers.append(b"X-Gmail-Labels: Inbox,Important,Category Personal")
        headers.append(b"X-GM-THRID: %d" % (1700000000000000000 + i))
    body = []
    for _ in range(nbody):
        if tricky and rng.random() < 0.35:
            body.append(rng.choice(tricky))
        else:
            body.append(rng.choice(TRICKY[:2]) + b" %d" % rng.randrange(1000))
    if rng.random() < 0.3:
        body.append(b"")
    eol = b"\r\n" if crlf else b"\n"
    msg = eol.join(headers) + eol + eol + eol.join(body) + eol
    return msg


def quote_o(msg, eol_sensitive=False):
    out = []
    for line in msg.split(b"\n"):
        out.append(b">" + line if line.startswith(b"From ") else line)
    return b"\n".join(out)


def quote_rd(msg):
    out = []
    for line in msg.split(b"\n"):
        out.append(b">" + line if re.match(rb"^>*From ", line) else line)
    return b"\n".join(out)


def add_content_length(msg, quote, eol):
    """Inserts Content-Length (counting the stored body) before the blank line that ends the headers."""
    sep = eol + eol
    i = msg.index(sep)
    head, body = msg[:i], msg[i + len(sep):]
    stored = quote(body) if quote else body
    return head + eol + b"Content-Length: %d" % len(stored) + sep + stored, head + eol + b"Content-Length: %d" % len(stored) + sep + body


def write_mbox(path, variant, messages, crlf=False, final_blank=True, preamble=b"", wrong_cl=False, no_blank=False):
    """messages: list of original message bytes. Returns the list of expected originals."""
    eol = b"\r\n" if crlf else b"\n"
    out = [preamble]
    expected = []
    for n, msg in enumerate(messages):
        if variant in ("mboxcl", "mboxcl2"):
            stored_msg, original = add_content_length(msg, quote_o if variant == "mboxcl" else None, eol)
            if wrong_cl and n % 3 == 1:
                stored_msg = re.sub(rb"Content-Length: (\d+)", lambda m: b"Content-Length: %d" % (int(m.group(1)) + 7), stored_msg, count=1)
            if variant == "mboxcl":
                # headers carry no From_ lines, so quoting the whole message equals quoting the body
                stored_msg = quote_o(stored_msg)
            expected.append(original if not (wrong_cl and n % 3 == 1) else None)
        elif variant == "mboxo":
            stored_msg = quote_o(msg)
            expected.append(msg)
        elif variant == "mboxrd":
            stored_msg = quote_rd(msg)
            expected.append(msg)
        elif variant == "plain":  # a writer that does not quote at all
            stored_msg = msg
            expected.append(msg)
        else:
            raise ValueError(variant)
        out.append(ENV + eol + stored_msg)
        if not no_blank:
            out.append(eol)
    data = b"".join(out)
    if not final_blank and data.endswith(eol) and not no_blank:
        data = data[: -len(eol)]
    with open(path, "wb") as f:
        f.write(data)
    return expected


def sidecar(path, variant, expected):
    with open(path + ".expected", "w") as f:
        f.write("%s\t%d\n" % (variant, len(expected)))
        for e in expected:
            f.write((hashlib.sha256(e).hexdigest() if e is not None else "-") + "\n")


def main():
    out = sys.argv[1]
    nmsg = 40
    if "--messages" in sys.argv:
        nmsg = int(sys.argv[sys.argv.index("--messages") + 1])
    os.makedirs(out, exist_ok=True)
    rng = random.Random(20261031)

    def msgs(count, tricky, crlf=False, labels=False, only=None):
        pool = tricky
        if only is not None:
            pool = only
        return [make_message(rng, i, rng.randrange(1, 12), pool, crlf, labels) for i in range(count)]

    # Messages whose bodies never carry ">From" lines: mboxo cannot undo its quoting for those.
    no_gt = [t for t in TRICKY if not t.startswith(b">From") and not t.startswith(b">>")]
    cases = []
    for variant in ("mboxo", "mboxrd", "mboxcl", "mboxcl2", "plain"):
        pool = no_gt if variant in ("mboxo", "mboxcl") else TRICKY
        for crlf in (False, True):
            name = "%s%s" % (variant, "-crlf" if crlf else "")
            m = msgs(nmsg, pool, crlf)
            p = os.path.join(out, name + ".mbox")
            exp = write_mbox(p, variant, m, crlf)
            if variant != "plain":  # a writer that does not quote has no ground truth: unquoted envelope lines split it
                sidecar(p, variant, exp)
            cases.append(name)
    # Edge shapes.
    base = msgs(6, no_gt)
    for name, kw in [("edge-nofinalblank", dict(final_blank=False)),
                     ("edge-preamble", dict(preamble=b"This is junk before the first message\nand more of it\n")),
                     ("edge-noblank", dict(no_blank=True)),
                     ("edge-wrongcl", dict(wrong_cl=True))]:
        variant = "mboxcl" if "wrongcl" in name else "mboxrd"
        p = os.path.join(out, name + ".mbox")
        # Without separator lines a message that itself ends in an empty line cannot be told from a separator.
        use = [b.rstrip(b"\n") + b"\n" for b in base] if kw.get("no_blank") or kw.get("final_blank") is False else base
        exp = write_mbox(p, variant, use, **kw)
        sidecar(p, variant, exp)
    # Gmail Takeout shape: labels and thread ids, envelope with the id and a zone before the year.
    m = msgs(10, no_gt, labels=True)
    p = os.path.join(out, "takeout.mbox")
    data = b"".join(b"From %d@xxx Mon Jan 01 00:00:00 +0000 2024\n" % (1700000000000000000 + i) + quote_rd(x) + b"\n" for i, x in enumerate(m))
    open(p, "wb").write(data)
    sidecar(p, "mboxrd", m)

    # Python's own writer (mboxo-style quoting). Bodies avoid ">From" lines, which it would leave as they are.
    py_msgs = msgs(nmsg, no_gt)
    p = os.path.join(out, "python-mbox.mbox")
    if os.path.exists(p):
        os.remove(p)
    box = mailbox.mbox(p)
    box.lock()
    for x in py_msgs:
        box.add(x)
    box.flush()
    box.unlock()
    box.close()
    sidecar(p, "mboxo", py_msgs)
    print("wrote", len(os.listdir(out)), "files to", out)


if __name__ == "__main__":
    main()
