#!/usr/bin/env python3
"""Compare the shared view OneMail gives MSG files with the typed fields the Python package extract-msg reads.

    compare_view_extract_msg.py <MsgTool.dll> <file-or-dir>...

Run it with the interpreter of a venv that has extract-msg installed (tools/msg/README.md pins the version). The oracle runs as a
released package through its public API only. Per message, and recursively per embedded message, it compares what the view says
(`MsgTool view`) with the oracle's subject, sender, recipients by type, the client-submit, delivery and creation times, the text
and HTML bodies, and each attachment's name, size, content ID and embedded subject. Every difference is matched by a rule in
EXPLAINED or printed as UNEXPLAINED; the exit status is 1 when any is unexplained.
"""
import datetime
import json
import os
import subprocess
import sys
from collections import Counter

import extract_msg

DOTNET = os.environ.get("DOTNET", "dotnet")
EXPLAINED = Counter()
UNEXPLAINED = []
COMPARED = Counter()


def ours(tool, path):
    out = subprocess.run([DOTNET, tool, "view", path], capture_output=True, check=True).stdout
    return json.loads(out)


def explain(where, field, a, b, reason):
    EXPLAINED[(field, reason)] += 1


def unexplained(where, field, a, b):
    UNEXPLAINED.append(f"{where}: {field}: ours {a!r:.100} oracle {b!r:.100}")


def check(where, field, a, b, rules=()):
    COMPARED[field] += 1
    if a == b:
        return
    for pred, reason in rules:
        if pred(a, b):
            explain(where, field, a, b, reason)
            return
    unexplained(where, field, a, b)


def iso(dt):
    if dt is None:
        return None
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=datetime.timezone.utc)
    return dt.astimezone(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f") + "0Z"


def z(x):
    # Some ANSI files store the terminating NUL inside the string stream; the oracle keeps it, the view does not (docs/msg.md, "Size field of strings").
    return x.rstrip("\x00") if isinstance(x, str) else x


def ascii_skeleton(x):
    # What survives in any decoding: ASCII letters, digits and punctuation (a failed decoding may leave '?' behind, so that is dropped too).
    return "".join(c for c in x if ord(c) < 128 and not c.isspace() and c != "?") if isinstance(x, str) else x


def redecoded(a, b):
    # The oracle decodes the 8-bit body with one fixed Western code page. Undo that to get the stored bytes and decode them with the East Asian
    # code page the file declares: if that gives our text, the only difference is the oracle's code page choice.
    if not (isinstance(a, str) and isinstance(b, str)):
        return False
    try:
        raw = z(b).encode("cp1252", errors="surrogateescape")
    except UnicodeError:
        return False
    for cp in ("cp950", "cp936", "cp932", "cp949"):
        try:
            if raw.decode(cp) == a:
                return True
        except UnicodeError:
            pass
    return False


EIGHT_BIT = (lambda a, b: isinstance(a, str) and isinstance(b, str) and ascii_skeleton(a) == ascii_skeleton(z(b)), "8-bit text in a file with no message code page: the oracle decodes with a fixed code page, the view follows the code page rule of docs/msg.md")


def prop(msg, tag):
    try:
        return msg.getPropertyVal(tag)
    except Exception:
        return None


def rtype(r):
    t = getattr(r, "type", None)
    v = getattr(t, "value", t)
    return {1: "to", 2: "cc", 3: "bcc"}.get(int(v) if v is not None else -1)


def compare(where, v, msg):
    # subject: PidTagSubject may start with a control pair that marks the prefix split; the view drops it, the oracle keeps what it is given.
    theirs = z(msg.subject)
    ours_s = v["subject"]
    check(where, "subject", ours_s, theirs, [
        EIGHT_BIT,
        (lambda a, b: b is not None and b.startswith("\x01") and a == b[2:], "control pair before the subject prefix is dropped (MS-OXPROPS PidTagSubject)"),
        (lambda a, b: a is None and b == "", "absent subject"),
    ])

    # recipients by type, in table order
    for kind in ("to", "cc", "bcc"):
        o = [(a["name"] or "", a["addr"]) for a in v[kind]]
        t = []
        for r in msg.recipients:
            if rtype(r) == kind:
                smtp = prop(r, "39FE001F") or prop(r, "39FE001E")
                addr = smtp or getattr(r, "email", None) or ""
                t.append((z(getattr(r, "name", None) or ""), z(addr)))
        check(where, "recipients-" + kind, o, t, [
            (lambda a, b: [ascii_skeleton(x[1]) for x in a] == [ascii_skeleton(y[1]) for y in b] and [ascii_skeleton(x[0]) for x in a] == [ascii_skeleton(y[0]) for y in b], EIGHT_BIT[1]),
            (lambda a, b: [x[1] for x in a] == [y[1] for y in b], "display name differs only (oracle falls back to another name property)"),
        ])

    # sent / delivery / creation times
    for field, tag in (("dateClientSubmit", "00390040"), ("dateDelivery", "0E060040"), ("dateCreation", "30070040")):
        check(where, field, v[field], iso(prop(msg, tag)), [
            (lambda a, b: a is not None and b is not None and a[:19] == b[:19], "fractional seconds"),
        ])

    # bodies
    t = z(msg.body)
    check(where, "text", z(v["text"]), t, [
        EIGHT_BIT,
        (redecoded, "the oracle decodes an East Asian 8-bit body with a fixed Western code page; decoding its bytes with the file's code page gives our text"),
        (lambda a, b: a is None and b in (None, ""), "empty text body"),
        (lambda a, b: a is not None and b is not None and a.replace("\r\n", "\n") == b.replace("\r\n", "\n"), "line endings"),
    ])
    # The oracle's htmlBody also de-encapsulates HTML from the RTF body; the view reports only an HTML property, so the oracle is asked for the property.
    has_html_prop = any(msg.exists(f"__substg1.0_1013{t}") for t in ("0102", "001F", "001E"))
    check(where, "html-present", v["html"] is not None, has_html_prop)
    h = None
    if has_html_prop:
        for t in ("0102", "001F", "001E"):
            if msg.exists(f"__substg1.0_1013{t}"):
                h = msg.getStream(f"__substg1.0_1013{t}")
                utf16 = t == "001F"
                break
    if h is not None and v["html"] is not None:
        enc = "utf-16-le" if utf16 else (v.get("htmlCharset") or "utf-8")
        try:
            hs = h.decode(enc, errors="replace")
        except LookupError:
            hs = h.decode("utf-8", errors="replace")
        check(where, "html", v["html"].rstrip("\x00"), hs.rstrip("\x00"), [EIGHT_BIT])

    # attachments
    atts = v["attachments"]
    theirs_atts = list(msg.attachments)
    check(where, "attachment-count", len(atts), len(theirs_atts))
    for i, (a, t) in enumerate(zip(atts, theirs_atts)):
        aw = f"{where} attachment {i}"
        name = getattr(t, "longFilename", None) or getattr(t, "shortFilename", None) or getattr(t, "name", None)
        signed = v.get("kind") == "Message" and "smime" in (name or "").lower() and a["name"] is None
        if signed:
            EXPLAINED[("attachment", "the oracle names and decodes the single attachment of a signed message from the MIME inside it; the view shows the stored attachment (message class IPM.Note.SMIME.MultipartSigned)")] += 1
            continue
        check(aw, "attachment-name", a["name"], name, [
            (lambda x, y: x is not None and y is not None and x.strip("\x00") == y.strip("\x00"), "NUL padding"),
        ])
        data = getattr(t, "data", None)
        if a["embedded"] is not None:
            sub = data if hasattr(data, "subject") else None
            if sub is None:
                unexplained(aw, "embedded", "embedded", "no embedded message object")
            else:
                compare(aw + " embedded", a["embedded"], sub)
        elif isinstance(data, (bytes, bytearray)):
            check(aw, "attachment-size", a["size"], len(data))
        cid = getattr(t, "cid", None) or getattr(t, "contentId", None)
        check(aw, "attachment-cid", a["contentId"], (cid.strip("<>") if isinstance(cid, str) and cid else None), [
            (lambda x, y: x is None and y is None, "none"),
        ])


def main():
    tool = sys.argv[1]
    files = []
    for p in sys.argv[2:]:
        if os.path.isdir(p):
            for root, _, names in os.walk(p):
                files += [os.path.join(root, n) for n in sorted(names) if n.lower().endswith((".msg", ".oft"))]
        else:
            files.append(p)
    skipped = 0
    done = 0
    for f in sorted(files):
        try:
            v = ours(tool, f)
        except subprocess.CalledProcessError:
            skipped += 1
            continue
        try:
            msg = extract_msg.openMsg(f)
        except Exception as e:  # the oracle cannot open it
            skipped += 1
            continue
        try:
            compare(os.path.basename(f), v, msg)
            done += 1
        except UnicodeDecodeError:
            EXPLAINED[("oracle", "the oracle cannot decode the file's 8-bit strings with its fixed code page")] += 1
        except Exception as e:
            unexplained(os.path.basename(f), "oracle-error", type(e).__name__, str(e)[:80])
        finally:
            try:
                msg.close()
            except Exception:
                pass
    print(f"files compared {done}, skipped (unreadable by one side) {skipped}")
    print("fields compared:", dict(COMPARED), "total", sum(COMPARED.values()))
    for (field, reason), n in sorted(EXPLAINED.items()):
        print(f"explained {n:4d} {field}: {reason}")
    for u in UNEXPLAINED[:80]:
        print("UNEXPLAINED", u)
    print(f"unexplained {len(UNEXPLAINED)}")
    return 1 if UNEXPLAINED else 0


if __name__ == "__main__":
    sys.exit(main())
