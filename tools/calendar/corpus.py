#!/usr/bin/env python3
"""Fetch the public ICS / vCard corpus used by the calendar tests, pinned by URL and SHA-256.

  corpus.py fetch [dest]     download every file in corpus.tsv and verify its SHA-256 (default dest: $ONEMAIL_CORPUS/calendar)
  corpus.py pin              regenerate corpus.tsv from the pinned commits below (network; use when moving a pin)
  corpus.py pin-forms        add (or refresh) the jCal / jCard / xCal / xCard test files of the same repositories to corpus.tsv
  corpus.py rfc [outdir]     cut the BEGIN:VCALENDAR / BEGIN:VCARD example blocks out of the RFC texts into outdir
                             (default testdata/calendar/rfc; the RFC texts are verified against RFC_SHA256)

Nothing is fetched at test time and the downloaded files are never committed; only the RFC example blocks
(testdata/calendar/rfc) and generated fixtures live in the repository.
"""
import hashlib, json, os, re, sys, urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
MANIFEST = os.path.join(HERE, "corpus.tsv")

# GitHub sources: (id, repo, commit, path prefixes, extensions)
GITHUB = [
    ("libical", "libical/libical", "319d98a5da7822d75cd55909f0286ce33b125422",
     ["test-data/", "src/test/", "src/Net-ICal-Libical/test-data/", "zoneinfo/"], (".ics", ".vcf"), ["test-data/zoneinfo"]),
    ("icaljs", "mozilla-comm/ical.js", "33ba266282fac7c721d792e04124513df73fe140", ["test/parser/", "samples/"], (".ics", ".vcf"), []),
    ("ezvcard", "mangstadt/ez-vcard", "55bb5ded1df8b4989cb8d2f344e7d6209850d2df", ["src/test/resources/"], (".vcf",), []),
    ("biweekly", "mangstadt/biweekly", "2e38b50161b0ebb870f7f1dc831436eab4e8ec40", ["src/test/resources/"], (".ics",), []),
    ("sabrevobject", "sabre-io/vobject", "5e2f338811916c03927398bb24e527989de63194", ["tests/"], (".ics", ".vcf"), []),
]
# The JSON and XML forms: (id, repo, commit, path prefixes, extensions). Same repositories and commits as above.
FORMS_GITHUB = [
    ("icaljs-forms", "kewisch/ical.js", "33ba266282fac7c721d792e04124513df73fe140", ["test/parser/"], (".json",)),
    ("biweekly-forms", "mangstadt/biweekly", "2e38b50161b0ebb870f7f1dc831436eab4e8ec40", ["src/test/resources/"], (".json", ".xml")),
    ("ezvcard-forms", "mangstadt/ez-vcard", "55bb5ded1df8b4989cb8d2f344e7d6209850d2df", ["src/test/resources/"], (".json", ".xml")),
]
THUNDERBIRD_PAGE = "https://www.thunderbird.net/en-US/calendar/holidays/"
THUNDERBIRD_BASE = "https://www.thunderbird.net"

RFCS = {  # rfc number -> sha256 of https://www.rfc-editor.org/rfc/rfcN.txt
    5545: "", 5546: "", 6350: "", 2426: "", 7986: "", 6047: "", 7529: "", 2445: "", 2446: "", 2425: "",
}
RFC_SHA256 = os.path.join(HERE, "rfc.sha256")


def get(url):
    with urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent": "onemail-corpus/1"}), timeout=60) as r:
        return r.read()


def sha(b):
    return hashlib.sha256(b).hexdigest()


def pin():
    rows = []
    for sid, repo, commit, prefixes, exts, skip in GITHUB:
        tree = json.loads(get("https://api.github.com/repos/%s/git/trees/%s?recursive=1" % (repo, commit)))
        assert not tree.get("truncated"), sid
        for e in tree["tree"]:
            p = e["path"]
            if e["type"] != "blob" or not p.lower().endswith(exts) or not any(p.startswith(x) for x in prefixes):
                continue
            if any(p.startswith(x) for x in skip):
                continue
            url = "https://raw.githubusercontent.com/%s/%s/%s" % (repo, commit, p)
            data = get(url)
            rows.append((sid, p, url, sha(data), len(data)))
            print(sid, p, len(data), file=sys.stderr)
    page = get(THUNDERBIRD_PAGE).decode("utf-8")
    for path in sorted(set(re.findall(r'href="(/media/caldata/autogen/[^"]+\.ics)"', page))):
        url = THUNDERBIRD_BASE + path
        data = get(url)
        rows.append(("thunderbird", os.path.basename(path), url, sha(data), len(data)))
    with open(MANIFEST, "w", newline="\n") as f:
        f.write("# source\tpath\turl\tsha256\tbytes\n")
        for r in rows:
            f.write("\t".join(map(str, r)) + "\n")
    print(len(rows), "files pinned")


def pin_forms():
    old = [r for r in rows() if not r[0].endswith("-forms")]
    new = []
    for sid, repo, commit, prefixes, exts in FORMS_GITHUB:
        tree = json.loads(get("https://api.github.com/repos/%s/git/trees/%s?recursive=1" % (repo, commit)))
        assert not tree.get("truncated"), sid
        for e in tree["tree"]:
            p = e["path"]
            if e["type"] != "blob" or not p.lower().endswith(exts) or not any(p.startswith(x) for x in prefixes):
                continue
            if p.endswith("pom.xml"):
                continue
            url = "https://raw.githubusercontent.com/%s/%s/%s" % (repo, commit, p)
            data = get(url)
            new.append((sid, p, url, sha(data), len(data)))
    with open(MANIFEST, "w", newline="\n") as f:
        f.write("# source\tpath\turl\tsha256\tbytes\n")
        for r in old:
            f.write("\t".join(r) + "\n")
        for r in new:
            f.write("\t".join(map(str, r)) + "\n")
    print(len(new), "forms files pinned")


def rows():
    out = []
    for line in open(MANIFEST, encoding="utf-8"):
        if line.startswith("#") or not line.strip():
            continue
        out.append(line.rstrip("\n").split("\t"))
    return out


def fetch(dest):
    bad = 0
    for sid, path, url, digest, size in rows():
        target = os.path.join(dest, sid, path)
        if os.path.exists(target) and sha(open(target, "rb").read()) == digest:
            continue
        data = get(url)
        if sha(data) != digest:
            print("HASH MISMATCH", url, file=sys.stderr)
            bad += 1
            continue
        os.makedirs(os.path.dirname(target), exist_ok=True)
        open(target, "wb").write(data)
    print("fetched into", dest, "mismatches:", bad)
    return 1 if bad else 0


def rfc_blocks(text):
    """Yield (start_line, block_lines) for every BEGIN:VCALENDAR / BEGIN:VCARD ... END block in an RFC text."""
    lines = text.replace("\f", "").split("\n")
    # drop RFC page furniture: "Name & Name   Standards Track   [Page n]" and "RFC nnnn   Title   Month Year"
    furniture = re.compile(r"^(RFC \d+ .*\d{4}$|.*\[Page \d+\]$|.*Standards Track.*|.*Experimental.*|.*Informational.*)")
    i = 0
    while i < len(lines):
        m = re.match(r"^(\s+)BEGIN:(VCALENDAR|VCARD)\s*$", lines[i], re.I)
        if not m:
            i += 1
            continue
        indent, kind = len(m.group(1)), m.group(2).upper()
        block, j = [], i
        while j < len(lines):
            ln = lines[j]
            if furniture.match(ln.strip()) and not ln.strip().startswith(("BEGIN", "END")):
                j += 1
                continue
            if ln.strip() == "":
                # a blank line inside an example is a page break; a blank line followed by a non-indented line ends it
                nxt = lines[j + 1] if j + 1 < len(lines) else ""
                if nxt.startswith(" " * indent) or furniture.match(nxt.strip()):
                    j += 1
                    continue
                break
            if not ln.startswith(" " * indent):
                break
            block.append(ln[indent:].rstrip())
            if ln.strip().upper() == "END:" + kind:
                break
            j += 1
        if block and block[-1].upper() == "END:" + kind:
            yield i + 1, kind, block
        i = j + 1


def rfc(outdir):
    cache = os.environ.get("ONEMAIL_CORPUS", os.path.expanduser("~/onemail/corpus"))
    os.makedirs(outdir, exist_ok=True)
    pins = dict(l.split() for l in open(RFC_SHA256) if l.strip()) if os.path.exists(RFC_SHA256) else {}
    count = 0
    for n in RFCS:
        data = get("https://www.rfc-editor.org/rfc/rfc%d.txt" % n)
        if str(n) in pins and pins[str(n)] != sha(data):
            print("RFC", n, "changed", file=sys.stderr)
            return 1
        pins[str(n)] = sha(data)
        for start, kind, block in rfc_blocks(data.decode("utf-8", "replace")):
            ext = "ics" if kind == "VCALENDAR" else "vcf"
            name = "rfc%d-L%d.%s" % (n, start, ext)
            with open(os.path.join(outdir, name), "w", newline="") as f:
                f.write("\r\n".join(block) + "\r\n")
            count += 1
    with open(RFC_SHA256, "w", newline="\n") as f:
        for k, v in sorted(pins.items()):
            f.write("%s %s\n" % (k, v))
    print(count, "example blocks written to", outdir)
    return 0


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else ""
    root = os.path.abspath(os.path.join(HERE, "..", ".."))
    if cmd == "pin":
        pin()
    elif cmd == "pin-forms":
        pin_forms()
    elif cmd == "fetch":
        dest = sys.argv[2] if len(sys.argv) > 2 else os.path.join(os.environ.get("ONEMAIL_CORPUS", os.path.expanduser("~/onemail/corpus")), "calendar")
        sys.exit(fetch(dest))
    elif cmd == "rfc":
        sys.exit(rfc(sys.argv[2] if len(sys.argv) > 2 else os.path.join(root, "testdata", "calendar", "rfc")))
    else:
        print(__doc__)
        sys.exit(2)
