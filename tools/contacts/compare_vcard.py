#!/usr/bin/env python3
"""Compare the typed view's facts (ContactTool vfacts) with an oracle's facts for the same .vcf files.

  compare_vcard.py ours.jsonl oracle.jsonl [--oracle vobject|folker] [--show N]

Both files use the fact schema of tools/calendar/oracles/compare.py: [path, name, params, value, raw]. The oracle's facts are
first brought to the typed view's form: only VCARD facts, only the TYPE parameter, dates as ISO text, media as a byte count.
What an oracle does not model (it returns the value as plain text, or fails on the file) is counted apart, never as agreement.
Prints per property name: agreements, differences, oracle limits.
"""
import argparse, collections, json, os, re

ap = argparse.ArgumentParser()
ap.add_argument("ours")
ap.add_argument("oracle")
ap.add_argument("--show", type=int, default=3)
ap.add_argument("--no-types", action="store_true", help="the oracle does not return TYPE parameters")
ap.add_argument("--skip", default="", help="property names the oracle driver does not model")
ap.add_argument("--root", default="", help="directory of the .vcf files, to classify differences by the raw bytes")
ap.add_argument("--list", action="store_true", help="print every difference")
a = ap.parse_args()

NAMES = {"FN", "N", "NICKNAME", "ADR", "TEL", "EMAIL", "URL", "ORG", "TITLE", "ROLE", "NOTE", "CATEGORIES", "UID", "KIND", "MEMBER", "BDAY", "ANNIVERSARY",
         "PHOTO", "LOGO", "SOUND", "KEY", "GENDER", "GEO", "TZ", "LANG", "IMPP", "RELATED"}
TYPED = {"ADR", "TEL", "EMAIL", "URL", "IMPP", "RELATED"}


def load(p):
    d = {}
    for line in open(p, encoding="utf-8"):
        r = json.loads(line)
        d[r["file"]] = r
    return d


def date_iso(raw):
    """The typed view's date text for a raw vCard date-and-or-time, or None when it is free text."""
    m = re.fullmatch(r"(?:(\d{4})-?(\d{2})?-?(\d{2})?|--(\d{2})-?(\d{2})?|---(\d{2}))(?:[Tt](\d{2}):?(\d{2})?:?(\d{2})?([Zz]|[+-]\d{2}:?\d{2})?)?", raw.strip())
    if not m:
        return None
    y, mo, d, mo2, d2, d3, h, mi, s, off = m.groups()
    if y:
        date = y + ("-" + mo if mo else "") + ("-" + d if d else "")
    elif mo2 or d2:
        date = "--" + (mo2 or "-") + ("-" + d2 if d2 else "")
    else:
        date = ""
    if h is not None:
        date += "T" + h + ":" + (mi or "00") + ":" + (s or "00") + (off or "")
    return date


def norm_oracle(f):
    path, name, params, value, raw = f[:5]
    name = name.upper().rsplit(".", 1)[-1]
    if path != "VCARD" or name not in NAMES:
        return None
    types = []
    for k, vs in params:
        if k == "TYPE":
            types = sorted(set(x.lower() for x in vs))
    if name not in TYPED:
        types = []
    if name in ("BDAY", "ANNIVERSARY"):
        d = date_iso(value[2:])
        return None if d is None else (name, (), "d:" + d)
    if name == "GENDER":
        v = value[2:]
        sex, _, ident = v.partition(";")
        return (name, (), "s:" + sex.upper() + ";" + ident)
    if name in ("PHOTO", "LOGO", "SOUND", "KEY"):
        return (name, (), value) if value.startswith("b:") else None
    if name == "GEO" and value.startswith("g:"):
        return (name, (), value)
    if name == "GEO":
        m = re.fullmatch(r"t:(?:geo:)?(-?[\d.]+)[,;] ?(-?[\d.]+)(?:;.*)?", value.replace(chr(92), ""))
        return None if not m else (name, (), "g:" + m.group(1).rstrip("0").rstrip(".") + ";" + m.group(2).rstrip("0").rstrip("."))
    return (name, tuple(types), value)


def norm_ours(f):
    path, name, params, value, raw = f[:5]
    if path != "VCARD" or name not in NAMES:
        return None
    types = []
    for k, vs in params:
        if k == "TYPE":
            types = sorted(set(x.lower() for x in vs))
    if name == "GEO":
        m = re.fullmatch(r"g:(-?[\d.]+);(-?[\d.]+)", value)
        return (name, (), "g:" + m.group(1).rstrip("0").rstrip(".") + ";" + m.group(2).rstrip("0").rstrip("."))
    return (name, tuple(types), value)


SKIP = set(x for x in a.skip.split(",") if x)
ours, orc = load(a.ours), load(a.oracle)
files = [f for f in ours if f in orc]
bad = [f for f in files if "error" in orc[f]]
# the garbage fixtures are there to show nothing crashes; their facts are not meaningful to compare
cmp = [f for f in files if "error" not in orc[f] and "error" not in ours[f] and "garbage" not in f]
agree, differ = collections.Counter(), collections.Counter()
explained = collections.Counter()
limit = collections.Counter()
examples = collections.defaultdict(list)
def expand(c):
    out = collections.Counter()
    for k, n in c.items():
        if k[2].startswith("l:"):
            for item in k[2][2:].split(","):
                out[(k[0], k[1], "l:" + item)] += n
        elif k[0] in ("BDAY", "ANNIVERSARY"):
            out[(k[0], k[1], k[2].split("T")[0])] += n
        else:
            out[k] += n
    return out


def related(x, y, raw):
    """Why an ours-only fact x and an oracle-only fact y of the same property are the same value read differently, or None."""
    if x[0] != y[0]:
        return None
    ov, rv = x[2], y[2]
    if "\ufffd" in rv or "\ufffd" in ov:
        return "OL-ENC the driver decodes the file as UTF-8, the card is Latin-1"
    if ov.replace("\r\n", "\n") == rv.replace("\r\n", "\n"):
        return "OL-NEWLINE the oracle turns CR LF in a value into LF"
    if ov.startswith(rv) and len(ov) > len(rv) and ov[len(rv)] == ",":
        return "OL-COMMA the oracle cuts a text value at its first unescaped comma"
    if ov.replace("\\", "") == rv.replace("\\", ""):
        return "ESC the oracles disagree on a backslash before an unlisted character; ours drops it"
    if x[0] in ("ADR", "N") and ov.replace(",", "") == rv.replace(",", ""):
        return "V21COMMA a comma in a 2.1 component is text, the oracle makes lists of it" if b"VERSION:2.1" in raw else "COMMA-LIST an empty list element: oracles disagree"
    if x[0] in ("ADR", "TEL", "EMAIL") and ov == rv and x[1] != y[1]:
        return "OL-BARETYPE the oracle drops vCard 2.1 style bare types"
    if x[0] in ("BDAY", "ANNIVERSARY") and ov.startswith("d:--") and rv.startswith("d:0004"):
        return "OL-PARTIAL the oracle gives a year to a date without one"
    if x[0] == "PHOTO" and rv == "b:0":
        return "OL-EMPTY the oracle returns an empty array for data it cannot decode (a surplus = at the end)"
    if x[0] == "PHOTO" and (b"\r\r\n" in raw):
        return "K1 CR CR LF ends the line twice, which breaks the folding of the photo"
    if x[0] in ("ORG", "CATEGORIES", "NOTE") and (b"=\r\n\r\n" in raw or b"=\n\n" in raw):
        return "OL-QP a quoted-printable soft break before a blank line: the readers disagree on where the value ends"
    return None


def single(k, side, raw):
    """Why a fact only one side has is a limit of the oracle or of the driver, or None."""
    name, ty, v = k
    if side == "oracle" and name in ("IMPP", "ANNIVERSARY", "BDAY", "TEL") and not v.startswith("d:--") and b"X-" in raw:
        return "OL-NONSTD the oracle maps nonstandard X- properties (X-AIM, X-ANNIVERSARY) onto this property"
    if side == "ours" and name in ("BDAY", "ANNIVERSARY"):
        return "OL-DATEONLY the driver reads dates only, the card has a date-time"
    if side == "ours" and name in ("NICKNAME", "CATEGORIES") and v.startswith("l:"):
        return "OL-COMMA the oracle cuts a text value at its first unescaped comma"
    if side == "oracle" and v == "b:0":
        return "OL-EMPTY the oracle returns an empty array for data it cannot decode"
    if side == "ours" and name in ("PHOTO", "KEY", "LOGO", "SOUND") and (b"data:" in raw):
        return "OL-DATAURI the oracle does not read data: URIs"
    if side == "oracle" and name == "BDAY" and v == "d:0000":
        return "OL-PARTIAL the oracle reads a malformed year"
    if (b"=\r\n\r\n" in raw or b"=\n\n" in raw) and name in ("ORG", "CATEGORIES", "NOTE", "TITLE", "FN"):
        return "OL-QP a quoted-printable soft break before a blank line: the readers disagree on where the value ends"
    if b"=\n" in raw and name == "ORG":
        return "OL-QP malformed quoted-printable"
    return None


def strip_types(c):
    return collections.Counter({(k[0], (), k[2]): n for k, n in c.items()}) if False else collections.Counter(((k[0], (), k[2]) for k in c.elements()))


for f in cmp:
    A = collections.Counter(k for k in (norm_ours(x) for x in ours[f]["facts"]) if k)
    B = collections.Counter(k for k in (norm_oracle(x) for x in orc[f]["facts"]) if k)
    A, B = expand(A), expand(B)
    A = collections.Counter({k: n for k, n in A.items() if k[0] not in SKIP})
    B = collections.Counter({k: n for k, n in B.items() if k[0] not in SKIP})
    if a.no_types or any(x[1] == "VERSION" and x[3] in ("t:2.1", "t:") for x in ours[f]["facts"]):
        # vobject keeps no bare vCard 2.1 types, so types are not compared on 2.1 cards
        A, B = strip_types(A), strip_types(B)
    oa, ob = A - B, B - A
    raw = open(os.path.join(a.root, f), "rb").read() if a.root else b""
    left_o, left_r = list(oa.elements()), list(ob.elements())
    for x in list(left_o):
        for y in left_r:
            c = related(x, y, raw)
            if c:
                explained[(x[0], c)] += 1
                left_o.remove(x)
                left_r.remove(y)
                break
    for side, items in (("ours", left_o), ("oracle", left_r)):
        for k in items:
            c = single(k, side, raw)
            if c:
                explained[(k[0], c)] += 1
            else:
                differ[k[0]] += 1
                examples[k[0]].append((f, side, k))
    for k in A & B:
        agree[k[0]] += (A & B)[k]
print(f"files in both {len(files)}; oracle failed on {len(bad)}; compared {len(cmp)}")
for n in sorted(NAMES):
    if agree[n] or differ[n]:
        print(f"  {n:12} agree {agree[n]:5}  differ {differ[n]:4}")
        for f, side, k in examples[n][: (10**6 if a.list else a.show)]:
            print(f"      {side:6} {f[-48:]}: {k[1]} {k[2][:100]!r}")
print("explained differences:")
by_class = collections.Counter()
for (name, c), n in sorted(explained.items()):
    by_class[c.split()[0]] += n
    print(f"  {name:12} {n:4}  {c}")
print(f"total agree {sum(agree.values())}, explained {sum(explained.values())}, UNEXPLAINED {sum(differ.values())}")
