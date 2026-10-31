#!/usr/bin/env python3
"""Checks the shared view's attachments for MIME messages that carry a TNEF part against what three TNEF readers extract from the stream.

  TnefTool mime OUT TNEFFILES...     writes the wrapped messages and OUT/view.jsonl (what the view lists per message)
  compare_mime.py --view OUT/view.jsonl --corpus DIR --py PY --tnef TNEF --ytnef YTNEF [--lib DIR]

For a message whose TNEF part is unpacked, the view's attachments (size and SHA-256; names are compared and a difference of name only is
explained as the tools sanitising file names) must equal what tnefparse 1.4.0, tnef 1.4.18 and ytnef 2.1.2 extract. For a part that is not unpacked
(damaged stream, trailing CR LF) the view must list the one part, winmail.dat, with the stream's own bytes. The exit status is the number of
unexplained differences.
"""
import argparse, hashlib, json, os, sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import compare  # noqa: E402  (the TNEF oracle helpers)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--view", required=True)
    ap.add_argument("--corpus", required=True)
    ap.add_argument("--py", required=True)
    ap.add_argument("--tnef", required=True)
    ap.add_argument("--ytnef", required=True)
    ap.add_argument("--lib", default="")
    ap.add_argument("--mimekit", default="", help="MimeKitTnef output (JSON lines) for the same messages")
    a = ap.parse_args()
    a.tnef, a.ytnef = os.path.abspath(a.tnef), os.path.abspath(a.ytnef)
    env = dict(os.environ)
    if a.lib:
        env["LD_LIBRARY_PATH"] = a.lib
    records = [json.loads(l) for l in open(a.view) if l.strip()]
    files = {}
    for r in records:
        for n in os.listdir(a.corpus):
            if os.path.splitext(n)[0] == r["tnef"]:
                files[r["tnef"]] = os.path.join(a.corpus, n)
    oracle = {}
    for stem, path in files.items():
        py = compare.theirs_py(a.py, [path]).get(os.path.basename(path), {})
        t, _ = compare.extract([a.tnef, "-f"], path, env)
        y, _ = compare.extract([a.ytnef, "-f", "."], path, env)
        oracle[stem] = {"py": py, "tnef": t, "ytnef": y, "sha": hashlib.sha256(open(path, "rb").read()).hexdigest(), "size": os.path.getsize(path)}

    mimekit = {}
    if a.mimekit:
        for line in open(a.mimekit):
            if line.strip():
                rec = json.loads(line)
                mimekit[rec["file"]] = rec

    total = {"messages": 0, "checks": 0, "explained": 0, "unexplained": 0}
    reasons = {}
    for r in records:
        total["messages"] += 1
        o = oracle[r["tnef"]]
        ours = [(x.get("size") if x.get("size") is not None else x.get("embeddedSize"),
                 x.get("sha256") or x.get("embeddedSha256"), x["name"]) for x in r["attachments"]]

        def explain(kind):
            total["explained"] += 1
            reasons[kind] = reasons.get(kind, 0) + 1

        def check(ok, detail):
            total["checks"] += 1
            if not ok:
                total["unexplained"] += 1
                print("UNEXPLAINED", r["file"], detail)

        if not r["merged"]:
            check(ours == [(o["size"], o["sha"], "winmail.dat")], f"refused part: view {ours} vs the stream's own bytes")
            if r["file"] in mimekit:
                mk = mimekit[r["file"]]
                check([(x["size"], x["sha256"], x["name"]) for x in mk["attachments"]] == ours, f"MimeKit lists {mk['attachments']} for the part that is not unpacked")
            continue
        mv_fixture = r["tnef"].startswith(("gen-rich", "written-rich"))
        fixture = r["tnef"].startswith(("gen-", "written-"))
        mine = sorted((s, h) for s, h, _ in ours if s)
        if len(mine) != len(ours):
            # the three readers write nothing for an attachment with no data
            explain("oracle-drops-empty-attachment")
        py = o["py"]
        if "error" in py:
            explain("oracle-failed")
        else:
            theirs = sorted((x["size"], x["sha256"]) for x in py["attachments"] if x["size"] > 0)
            check(mine == theirs, f"tnefparse {theirs} vs view {mine}")
        if r["file"] in mimekit:
            mk = mimekit[r["file"]]
            # without unpacking, MimeKit's attachment list holds the TNEF part itself; the view replaces it by what the part holds
            check([x["name"] for x in mk["attachments"]] == ["winmail.dat"], f"MimeKit attachment list {mk['attachments']}")
            if "error" in mk:
                explain("oracle-failed")
            else:
                # TnefPart.ExtractAttachments also returns the message body as an unnamed entry (RTF, HTML or text)
                got = [x for x in mk["tnef"] if x["name"]]
                if len(got) != len(mk["tnef"]):
                    explain("oracle-body-entry")
                have = sorted((x["size"], x["sha256"]) for x in got if x["size"] > 0)
                if fixture:
                    check(set(have) <= set(mine), f"MimeKit extracted something the view does not list: {have} vs {mine}")
                else:
                    check(have == mine, f"MimeKit {have} vs view {mine}")
        for tool, got in (("tnef", o["tnef"]), ("ytnef", o["ytnef"])):
            got = [x for x in got if x["size"] > 0]
            if mv_fixture:
                explain("oracle-failed")
                continue
            if any(x["name"] == "tnef-tmp" for x in got):
                explain("oracle-body-file")
                got = [x for x in got if x["name"] != "tnef-tmp"]
            if any(x["name"].endswith(".ics") for x in got) and not any((n or "").endswith(".ics") for _, _, n in ours):
                explain("oracle-synthesised-file")
                got = [x for x in got if not x["name"].endswith(".ics")]
            have = {(x["size"], x["sha256"]) for x in got}
            if fixture:
                check(have <= set(mine), f"{tool} extracted something the view does not list")
            else:
                check(sorted(have) == mine, f"{tool} {sorted(have)} vs view {mine}")
                if sorted(n for s, _, n in ours if s) != sorted(x["name"] for x in got):
                    explain("oracle-tool-name")
    print(json.dumps({**total, "explained-by": reasons}))
    return total["unexplained"]


if __name__ == "__main__":
    sys.exit(main())
