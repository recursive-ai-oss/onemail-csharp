#!/usr/bin/env python3
"""Compares what Outlook's object model shows for a set of PSTs (Check-Stores.ps1) with what our reader shows (PstOracle outlookview).

    compare-views.py <outlook-dir> <ours-dir> [--details <file>]

Both directories hold one JSON per file, named alike (`<relative path with __>.outlook.json` and `.ours.json`). Per file one line is printed:
opened, dialogs, folder and item counts on each side, and differences in two classes: hard (a folder or count missing or different, an item
Outlook shows that we do not, a different subject, class, recipient or attachment count, read state, sender or sent time) and soft (Outlook's
own message size, which it recomputes). Exit status 1 when any file has a hard difference or did not open.
"""
import json, sys, os, collections


def load(p):
    with open(p, encoding="utf-8-sig") as f:
        return json.load(f)


def is_mail(it):
    # Outlook's object model gives sender and sent time only for mail-like items (appointments, contacts, tasks and notes have none there).
    c = it.get("class", "")
    return c.startswith(("IPM.Note", "REPORT.", "IPM.Schedule", "IPM.Post")) or c == ""


def key(it):
    return (it.get("subject", ""), it.get("class", ""), it.get("sentUtc", "") if is_mail(it) else "")


def compare(o, u):
    hard, soft = [], []
    if not o.get("opened"):
        return ["outlook did not open: " + str(o.get("error"))], soft, 0, 0
    ours = {f["path"]: f for f in u.get("folders", [])}
    # Outlook names the root folder it shows after the store; ours names it as the IPM subtree's own folder (MS-PST: "Top of Personal Folders").
    # The subtree is the top-level folder with the most descendants.
    tops = collections.Counter(p.split("/")[0] for p in ours)
    ipm = max(tops, key=lambda t: (tops[t], t)) if tops else ""
    for f in o["folders"]:
        parts = f["path"].split("/")
        f["path"] = "/".join([ipm] + parts[1:])
    seen = set()
    o_items = u_items = 0
    for of in o["folders"]:
        p = of["path"]
        uf = ours.get(p)
        if uf is None:
            # Outlook names its root folder by the store; ours names the IPM subtree the same way, so a miss is a real difference.
            hard.append(f"folder only in Outlook: {p} ({of['count']} items)")
            continue
        seen.add(p)
        of["count"] = of["count"] or 0
        o_items += of["count"]
        u_items += uf["count"]
        if of["count"] != uf["count"]:
            hard.append(f"{p}: item count Outlook {of['count']} ours {uf['count']}")
        pool = collections.defaultdict(list)
        for it in uf["items"]:
            pool[key(it)].append(it)
        for it in of["items"]:
            if "error" in it:
                hard.append(f"{p}: Outlook could not read item {it.get('index')}: {it['error']}")
                continue
            cands = pool.get(key(it))
            if not cands:
                hard.append(f"{p}: Outlook item not in ours: {key(it)}")
                continue
            # prefer a candidate that agrees on everything, else report the closest
            best = None
            for c in cands:
                bad = [f for f in ("recipients", "attachments", "unread", "sender") if it.get(f) != c.get(f) and (f != "sender" or is_mail(it))]
                if best is None or len(bad) < len(best[1]):
                    best = (c, bad)
                if not bad:
                    break
            c, bad = best
            if bad:
                hard.append(f"{p}: {key(it)[0][:40]!r}: " + ", ".join(f"{f} Outlook {it.get(f)!r} ours {c.get(f)!r}" for f in bad))
            else:
                cands.remove(c)
                if it.get("size") != c.get("size"):
                    soft.append(f"{p}: size {it.get('size')} vs {c.get('size')}")
    for p, uf in ours.items():
        if p not in seen and uf["count"] > 0:
            hard.append(f"folder only in ours with items: {p} ({uf['count']} items)")
    return hard, soft, o_items, u_items


def main():
    odir, udir = sys.argv[1], sys.argv[2]
    details = sys.argv[sys.argv.index("--details") + 1] if "--details" in sys.argv else None
    rows, bad, out = 0, 0, []
    for name in sorted(os.listdir(odir)):
        if not name.endswith(".outlook.json"):
            continue
        base = name[: -len(".outlook.json")]
        o = load(os.path.join(odir, name))
        up = os.path.join(udir, base + ".ours.json")
        u = load(up) if os.path.exists(up) else {"opened": False, "folders": []}
        hard, soft, oi, ui = compare(o, u)
        dialogs = len(o.get("dialogs", []))
        rows += 1
        ok = not hard
        bad += 0 if ok else 1
        print(f"{'ok  ' if ok else 'DIFF'} {base}: outlook opened={o.get('opened')} dialogs={dialogs} items Outlook {oi} ours {ui} hard {len(hard)} soft {len(soft)}")
        out.append((base, o.get("dialogs", []), hard, soft))
    print(f"{rows} files, {rows - bad} agree, {bad} differ")
    if details:
        with open(details, "w", encoding="utf-8") as f:
            for base, dlg, hard, soft in out:
                f.write(f"== {base}\n")
                for d in dlg:
                    f.write(f"  dialog: {d}\n")
                for h in hard[:50]:
                    f.write(f"  HARD {h}\n")
                for s in soft[:5]:
                    f.write(f"  soft {s}\n")
    sys.exit(1 if bad else 0)


main()
