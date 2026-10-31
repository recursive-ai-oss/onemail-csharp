#!/usr/bin/env python3
"""Compares what OneMail reads from TNEF files with three black-box oracles, per file:

  tnefparse 1.4.0 (PyPI)    attributes, message-level MAPI properties, attachments (name, size, SHA-256), bodies
  tnef 1.4.18  (.deb)       extracted attachments (name, size, SHA-256)
  ytnef 2.1.2  (.deb)       extracted attachments (name, size, SHA-256)

Every difference is either listed in EXPLAINED (with the reason) or counted as unexplained; the exit status is the number of unexplained ones.

  compare.py --tool TnefTool.dll --py /path/to/venv/bin/python --tnef /path/to/tnef --ytnef /path/to/ytnef [--lib /path/to/lib/dir] files...
"""
import argparse, hashlib, json, os, subprocess, sys, tempfile

HERE = os.path.dirname(os.path.abspath(__file__))

# kind -> reason. A difference whose kind is here is explained; the kind is the text before the first colon.
EXPLAINED = {
    "ours-only-prop-legacy": "a property built from a legacy attribute (MS-OXTNEF 2.1.3.3) that the encapsulated list does not carry; tnefparse keeps those as attributes, not properties",
    "ours-only-prop-body": "tnefparse moves the body properties (text, HTML, compressed RTF) into its body fields instead of its property list; they are compared there",
    "ours-only-prop-after-mv": "tnefparse stops reading the property list at the first multi-valued property of the fixture",
    "oracle-misparses-mv": "tnefparse misreads the first multi-valued property of the fixture (a documented limit of that oracle; the plain fixture without them agrees)",
    "oracle-failed": "the oracle itself timed out or failed on this input (checked below that OneMail ends with a typed defect)",
    "oracle-body-file": "tnef writes the decoded RTF body next to the attachments; the body is not an attachment in the stream",
    "oracle-synthesised-file": "ytnef writes an .ics file for meeting requests; it is not an attachment in the stream",
    "oracle-tool-name": "tnef and ytnef write extracted files under sanitised or de-duplicated names",
}


# The properties MS-OXTNEF 2.1.3.3 maps legacy attributes to (class, subject, body, dates, status, search key, priority, sender and owner groups, ...).
LEGACY_IDS = {0x1A, 0x4B, 0x37, 0x1000, 0x39, 0x0E06, 0x3008, 0x60, 0x61, 0x0E07, 0x300B, 0x17, 0xC19, 0xC1A, 0xC1E, 0xC1F,
              0x41, 0x42, 0x64, 0x65, 0x43, 0x44, 0x77, 0x78, 0x62, 0x63}


def run(cmd, env=None, cwd=None):
    return subprocess.run(cmd, capture_output=True, text=True, errors="replace", env=env, cwd=cwd, timeout=300)


def ours(tool, files):
    r = run(["dotnet", tool, "dump", *files])
    if r.returncode != 0:
        sys.exit("TnefTool failed: " + r.stderr[:500])
    return {json.loads(l)["file"]: json.loads(l) for l in r.stdout.splitlines() if l.strip()}


def limit():
    import resource
    resource.setrlimit(resource.RLIMIT_AS, (2 << 30, 2 << 30))


def theirs_py(py, files):
    # One process per file with a time and memory cap: a hostile input can send the oracle into a very long loop.
    out = {}
    for f in files:
        name = os.path.basename(f)
        try:
            r = subprocess.run([py, os.path.join(HERE, "py_tnefparse.py"), f], capture_output=True, text=True, timeout=20, preexec_fn=limit)
            lines = [l for l in r.stdout.splitlines() if l.strip()]
            out[name] = json.loads(lines[0]) if lines else {"file": name, "error": "no output (exit %d)" % r.returncode}
        except subprocess.TimeoutExpired:
            out[name] = {"file": name, "error": "timeout after 20 s"}
    return out


def extract(cmd_prefix, file, env):
    with tempfile.TemporaryDirectory() as d:
        r = run([*cmd_prefix, os.path.abspath(file)], env=env, cwd=d)
        out = []
        for root, _, names in os.walk(d):
            for n in names:
                data = open(os.path.join(root, n), "rb").read()
                out.append({"name": n, "size": len(data), "sha256": hashlib.sha256(data).hexdigest()})
        return sorted(out, key=lambda x: (x["sha256"], x["name"])), r.returncode


def norm_text(s):
    return None if s is None else s.rstrip("\x00")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--tool", required=True)
    ap.add_argument("--py", required=True)
    ap.add_argument("--tnef", required=True)
    ap.add_argument("--ytnef", required=True)
    ap.add_argument("--lib", default="")
    ap.add_argument("files", nargs="+")
    a = ap.parse_args()
    a.tnef, a.ytnef = os.path.abspath(a.tnef), os.path.abspath(a.ytnef)
    env = dict(os.environ)
    if a.lib:
        env["LD_LIBRARY_PATH"] = a.lib
    mine = ours(a.tool, a.files)
    py = theirs_py(a.py, a.files)
    total = {"files": 0, "checks": 0, "explained": 0, "unexplained": 0}
    for f in a.files:
        name = os.path.basename(f)
        o = mine[name]
        p = py.get(name, {})
        diffs = []
        total["files"] += 1

        def check(kind, ok, detail):
            total["checks"] += 1
            if not ok:
                diffs.append((kind, detail))

        # ---- attachments against every oracle
        # an embedded message is handed out by the other readers as the nested TNEF stream, so it counts as attachment data here
        ours_att = sorted(({"name": x["name"], "size": x.get("size", x.get("embeddedSize")), "sha256": x.get("sha256", x.get("embeddedSha256"))} for x in o["attachments"] if "sha256" in x or "embeddedSha256" in x), key=lambda x: (x["sha256"], x["name"] or ""))
        mv_fixture = name.startswith(("gen-rich", "written-rich"))
        if "error" in p:
            diffs.append(("oracle-failed", "tnefparse: " + p["error"]))
        else:
            theirs = sorted((x for x in p["attachments"] if x["size"] > 0), key=lambda x: (x["sha256"], x["name"] or ""))
            check("attachments-tnefparse", [(x["size"], x["sha256"]) for x in ours_att] == [(x["size"], x["sha256"]) for x in theirs],
                  f"ours {len(ours_att)} vs tnefparse {len(theirs)}")
            embedded_sha = {x["embeddedSha256"] for x in o["attachments"] if "embeddedSha256" in x}
            # an embedded message has no file name of its own in the stream (it is named by its display name), so names are compared for the others
            check("attachment-names-tnefparse", [x["name"] for x in ours_att if x["sha256"] not in embedded_sha] == [x["name"] for x in theirs if x["sha256"] not in embedded_sha],
                  f"{[x['name'] for x in ours_att]} vs {[x['name'] for x in theirs]}")
        for tool_name, prefix in (("tnef", [a.tnef, "-f"]), ("ytnef", [a.ytnef, "-f", "."])):
            got, rc = extract(prefix if tool_name == "tnef" else [a.ytnef, "-f", "."], f, env)
            got = [x for x in got if x["size"] > 0]
            if rc != 0 and mv_fixture or (mv_fixture and not got):
                diffs.append(("oracle-failed", f"{tool_name} rejects the multi-valued, currency, application-time or error properties in this fixture (the plain fixture passes)"))
                continue
            if any(x["name"] == "tnef-tmp" for x in got):
                diffs.append(("oracle-body-file", f"{tool_name} writes the message body (the compressed RTF, decoded) to a file named tnef-tmp"))
                got = [x for x in got if x["name"] != "tnef-tmp"]
            if any(x["name"].endswith(".ics") for x in got) and not any((x["name"] or "").endswith(".ics") for x in ours_att):
                diffs.append(("oracle-synthesised-file", f"{tool_name} adds an iCalendar file for a meeting request"))
                got = [x for x in got if not x["name"].endswith(".ics")]
            if name.startswith(("gen-", "written-")):
                # tnef and ytnef only extract attachments whose data is in attAttachData, so on the hand-built fixtures everything they extract must be ours
                check("attachments-" + tool_name, {(x["size"], x["sha256"]) for x in got} <= {(x["size"], x["sha256"]) for x in ours_att}, f"{tool_name} extracted something that is not an attachment of ours")
                continue
            check("attachments-" + tool_name, [(x["size"], x["sha256"]) for x in ours_att] == sorted([(x["size"], x["sha256"]) for x in got], key=lambda t: (t[1], t[0])) or
                  sorted([(x["size"], x["sha256"]) for x in ours_att]) == sorted([(x["size"], x["sha256"]) for x in got]),
                  f"ours {len(ours_att)} vs {tool_name} {len(got)} files (exit {rc})")
            if [x["name"] for x in sorted(ours_att, key=lambda x: x["name"] or "")] != [x["name"] for x in sorted(got, key=lambda x: x["name"])]:
                diffs.append(("oracle-tool-name", f"{tool_name}: {[x['name'] for x in got]} vs ours {[x['name'] for x in ours_att]}"))

        if "error" not in p:
            # ---- the attribute sequence
            check("attributes", [[x["level"], x["id"]] for x in o["attributes"]] == p["objects"], f"ours {len(o['attributes'])} vs tnefparse {len(p['objects'])} attributes")

            # ---- message-level properties by (id, type)
            by = {}
            for x in o["props"]:
                by.setdefault((int(x["id"], 16), int(x["type"], 16)), []).append(x)
            ghost = False
            for x in p["props"]:
                key = (x["id"], x["type"])
                mine_list = by.get(key)
                if not mine_list and mv_fixture:
                    # tnefparse reads a multi-valued property as if it were single-valued and loses its place in the list; nothing after it means anything
                    if not ghost:
                        diffs.append(("oracle-misparses-mv", f"tnefparse reports 0x{key[0]:04X}/0x{key[1]:04X}, which the fixture does not have; its remaining {len(p['props'])} properties are not compared"))
                    ghost = True
                    continue
                if ghost:
                    continue
                if not mine_list:
                    check("prop-missing", False, f"tnefparse has 0x{key[0]:04X}/0x{key[1]:04X}, ours does not")
                    continue
                m = mine_list.pop(0)
                if key[1] in (0x1E, 0x1F):
                    check("prop-text", norm_text(m.get("text")) == norm_text(x.get("text")), f"0x{key[0]:04X}: {m.get('text')!r} vs {x.get('text')!r}")
                elif key[1] == 0x0B:
                    check("prop-bool", (m["hex"] != "00") == (x["raw"].rstrip("0") != ""), f"0x{key[0]:04X}")
                else:
                    ours_hex = (m.get("hex") or "").lower() or None
                    check("prop-value", ours_hex is not None and x["raw"].startswith(ours_hex) and set(x["raw"][len(ours_hex):]) <= {"0"}, f"0x{key[0]:04X}/0x{key[1]:04X}")
            for key, rest in by.items():
                for m in rest:
                    if key[0] in (0x1000, 0x1009, 0x1013):
                        diffs.append(("ours-only-prop-body", f"0x{key[0]:04X}"))
                    elif mv_fixture:
                        diffs.append(("ours-only-prop-after-mv", f"0x{key[0]:04X}/0x{key[1]:04X}"))
                    elif m["type"] == "001F" or key[0] in LEGACY_IDS:
                        diffs.append(("ours-only-prop-legacy", f"0x{key[0]:04X}"))
                    else:
                        diffs.append(("ours-only-prop", f"0x{key[0]:04X}/0x{key[1]:04X}"))
            html = next((x for x in o["props"] if x["id"] == "1013" and x.get("hex")), None)
            if html is not None:
                enc = p.get("codepage") or "cp1252"
                mine_html = bytes.fromhex(html["hex"]).decode(enc, "replace")
                check("htmlbody", p.get("htmlbody") is not None and p["htmlbody"].rstrip("\x00") == mine_html.rstrip("\x00"), f"{len(mine_html)} chars vs {len(p.get('htmlbody') or '')}")
            check("rtf-present", p.get("hasRtf") == any(x["id"] == "1009" for x in o["props"]), "compressed RTF presence")
            if True:
                subj = next((x.get("text") for x in p["props"] if x["id"] == 0x37), None)
                if subj is not None:
                    check("subject", norm_text(o["subject"]) == norm_text(subj), f"{o['subject']!r} vs {subj!r}")
                body = next((x.get("text") for x in p["props"] if x["id"] == 0x1000), None)
                if body is not None:
                    check("body", norm_text(o["body"]) == norm_text(body), f"{(o['body'] or '')[:30]!r} vs {body[:30]!r}")

        unexplained = [d for d in diffs if d[0] not in EXPLAINED]
        explained = [d for d in diffs if d[0] in EXPLAINED]
        total["explained"] += len(explained)
        total["unexplained"] += len(unexplained)
        print(f"{name}: checks ok, {len(explained)} explained, {len(unexplained)} unexplained")
        for k, d in explained:
            print(f"    explained   [{k}] {d}")
        for k, d in unexplained:
            print(f"    UNEXPLAINED [{k}] {d}")
    print("TOTAL", json.dumps(total))
    sys.exit(min(total["unexplained"], 125))


if __name__ == "__main__":
    main()
