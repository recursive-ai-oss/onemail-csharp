#!/usr/bin/env python3
"""Compare two renderings of the same iCalendar/vCard data in a JSON or XML form, property by property.

    compare_forms.py json|xml <dirA> <dirB> [--ids list.txt] [--report name --rules explained.tsv]

Each directory holds one file per input, the same relative name in both (``<id>.json`` or ``<id>.xml``). A file starting
with ``ERROR`` is an oracle failure and is counted. Documents are reduced to one tuple per property
(component path, name, group, parameters, value type, value) so that the order of properties, the case of names and
the spelling of numbers do not count. Unmatched tuples are paired by component path and name and classified by what
differs: type, parameters, value or presence. The summary lists every class with its count and one example, so each class
can be explained (a documented resolution) or fixed.

With ``--rules`` each unmatched tuple is matched against explained.tsv (id, report regex, class regex, context regex,
text): a matching tuple is counted under the rule's id; the rest are listed as unexplained.
"""
import collections
import json
import os
import re
import sys
import xml.etree.ElementTree as ET

NS_ICAL = "urn:ietf:params:xml:ns:icalendar-2.0"
NS_VCARD = "urn:ietf:params:xml:ns:vcard-4.0"


def norm_scalar(v):
    if isinstance(v, bool):
        return "true" if v else "false"
    if isinstance(v, (int, float)):
        f = float(v)
        return str(int(f)) if f == int(f) else repr(f)
    return str(v)


def norm_value(v):
    if isinstance(v, list):
        return [norm_value(x) for x in v]
    if isinstance(v, dict):
        return {k.lower(): norm_value(x) for k, x in v.items()}
    return norm_scalar(v)


def flat(v):
    if isinstance(v, list):
        return ",".join(flat(x) for x in v)
    if isinstance(v, dict):
        return ";".join(k + "=" + flat(x) for k, x in sorted(v.items()))
    return v


def json_props(node, path, out):
    if not (isinstance(node, list) and node and isinstance(node[0], str)):
        return
    comp = node[0].lower()
    here = path + "/" + comp
    if len(node) > 1 and isinstance(node[1], list):
        for p in node[1]:
            if not (isinstance(p, list) and len(p) >= 4):
                out.append((here, "?malformed", "", "", "", json.dumps(p)))
                continue
            name = str(p[0]).lower()
            params = {}
            group = ""
            for k, val in (p[1].items() if isinstance(p[1], dict) else []):
                k = k.lower()
                vals = val if isinstance(val, list) else [val]
                if k == "group":
                    group = str(vals[0]).lower()
                    continue
                params[k] = [norm_scalar(x) for x in vals]
            vals = [norm_value(x) for x in p[3:]]
            out.append((here, name, group, params, str(p[2]).lower(), flat(vals if len(vals) != 1 else vals[0])))
    if len(node) > 2 and isinstance(node[2], list):
        for c in node[2]:
            json_props(c, here, out)


def load_json(text):
    data = json.loads(text)
    out = []
    if isinstance(data, list) and data and isinstance(data[0], str):
        json_props(data, "", out)
    elif isinstance(data, list):
        for d in data:
            json_props(d, "", out)
    return out


def local(tag):
    return tag.rsplit("}", 1)[-1].lower()


def xml_value_text(e):
    kids = list(e)
    if not kids:
        return (e.text or "")
    return ",".join(xml_value_text(k) if True else "" for k in kids)


def xml_props(root):
    out = []

    def comp(e, path):
        name = local(e.tag)
        here = path + "/" + name
        props = [k for k in e if local(k.tag) == "properties"]
        subs = [k for k in e if local(k.tag) == "components"]
        container = props[0] if props else (e if name == "vcard" else None)
        if container is not None:
            for p in container:
                if local(p.tag) in ("parameters",):
                    continue
                pname = local(p.tag)
                params = {}
                group = (p.get("group") or "").lower()
                for pp in p:
                    if local(pp.tag) == "parameters":
                        for prm in pp:
                            params[local(prm.tag)] = [xml_value_text(v) for v in prm]
                vals = [v for v in p if local(v.tag) != "parameters"]
                if not vals:
                    out.append((here, pname, group, params, "", ""))
                else:
                    types = ",".join(sorted({local(v.tag) for v in vals}))
                    out.append((here, pname, group, params, types, ";".join(xml_value_text(v) for v in vals)))
        for s in subs:
            for c in s:
                comp(c, here)

    r = root
    if local(r.tag) == "icalendar":
        for c in r:
            comp(c, "")
    elif local(r.tag) == "vcards":
        for c in r:
            comp(c, "")
    return out


def load_xml(text):
    return xml_props(ET.fromstring(text))


def key(t):
    return json.dumps(t, sort_keys=True)


def classify(a, b):
    """a: ours, b: oracle, for one component path and name. Returns a class string."""
    if a[2] != b[2]:
        return "group"
    if a[3] != b[3]:
        pa, pb = set(a[3]), set(b[3])
        if pa - pb:
            return "param only ours: " + ",".join(sorted(pa - pb))
        if pb - pa:
            return "param only oracle: " + ",".join(sorted(pb - pa))
        return "param value: " + ",".join(sorted(k for k in a[3] if a[3][k] != b[3][k]))
    if a[4] != b[4]:
        return "type: %s vs %s" % (a[4], b[4])
    return "value"


def load_rules(path, report):
    rules = []
    for line in open(path, encoding="utf-8"):
        if not line.strip() or line.startswith("#"):
            continue
        rid, rep, cls, ctx, text = (line.rstrip("\n").split("\t") + [""] * 5)[:5]
        if re.fullmatch(rep, report):
            rules.append((rid, re.compile(cls), re.compile(ctx or ".*", re.S)))
    return rules


def main():
    kind, da, db = sys.argv[1:4]
    examples = 1
    ids = None
    rules = []
    if "--rules" in sys.argv:
        rules = load_rules(sys.argv[sys.argv.index("--rules") + 1], sys.argv[sys.argv.index("--report") + 1])
    explained = collections.Counter()
    if "--examples" in sys.argv:
        examples = int(sys.argv[sys.argv.index("--examples") + 1])
    if "--ids" in sys.argv:
        ids = [l.strip() for l in open(sys.argv[sys.argv.index("--ids") + 1]) if l.strip()]
    ext = "." + kind
    if ids is None:
        ids = sorted(os.path.relpath(os.path.join(r, f), da)[: -len(ext)] for r, _, fs in os.walk(da) for f in fs if f.endswith(ext))
    loader = load_json if kind == "json" else load_xml
    files = same = oracle_err = our_err = 0
    props_ours = props_oracle = props_equal = 0
    classes = collections.Counter()
    sample = {}
    class_files = collections.defaultdict(set)
    def count(c, i, ex, ctx):
        for rid, rc, rx in rules:
            if rc.search(c) and rx.search(ctx):
                explained[rid] += 1
                return
        classes[c] += 1
        class_files[c].add(i)
        sample.setdefault(c, ex)

    for i in ids:
        pa, pb = os.path.join(da, i + ext), os.path.join(db, i + ext)
        if not (os.path.exists(pa) and os.path.exists(pb)):
            continue
        ta, tb = open(pa, encoding="utf-8", errors="replace").read(), open(pb, encoding="utf-8", errors="replace").read()
        files += 1
        if tb.startswith("ERROR") or not tb.strip() or not ta.strip():
            oracle_err += 1
            c = "oracle error: " + re.sub(r"[0-9]+", "N", tb[:90]) if tb.strip() else ("oracle wrote nothing" if ta.strip() else "we wrote nothing")
            count(c, i, i, tb[:300])
            continue
        try:
            A = loader(ta)
            B = loader(tb)
        except Exception as e:  # noqa: BLE001
            our_err += 1
            c = "unparsable output: " + type(e).__name__
            count(c, i, i + ": " + str(e)[:100], ta[:200] + " || " + tb[:200])
            continue
        if A and not B:
            oracle_err += 1
            count("oracle wrote no properties", i, i, tb[:200])
            continue
        props_ours += len(A)
        props_oracle += len(B)
        ca = collections.Counter(key(t) for t in A)
        cb = collections.Counter(key(t) for t in B)
        eq = ca & cb
        props_equal += sum(eq.values())
        ra = [json.loads(k) for k, n in (ca - eq).items() for _ in range(n)]
        rb = [json.loads(k) for k, n in (cb - eq).items() for _ in range(n)]
        if not ra and not rb:
            same += 1
            continue
        byname = collections.defaultdict(list)
        for t in rb:
            byname[(t[0], t[1])].append(t)
        for t in ra:
            lst = byname.get((t[0], t[1]))
            o = None
            if lst:
                # pair the tuple with the same value first (case aside), so a parameter difference is not mistaken for a value difference
                j = next((n for n, x in enumerate(lst) if x[5].lower() == t[5].lower()), 0)
                o = lst.pop(j)
                c = classify(t, o) + " [" + t[1] + "]"
                ex = "%s\n      ours  : %s\n      oracle: %s" % (i, json.dumps(t[2:])[:260], json.dumps(o[2:])[:260])
            else:
                c = "only ours [" + t[1] + "]"
                ex = "%s\n      ours  : %s" % (i, json.dumps(t[2:])[:260])
            count(c, i, ex, json.dumps(t[2:]) + " || " + (json.dumps(o[2:]) if o else "") + " @@ " + i + " @@ " + t[0])
        for lst in byname.values():
            for o in lst:
                c = "only oracle [" + o[1] + "]"
                count(c, i, "%s\n      oracle: %s" % (i, json.dumps(o[2:])[:260]), " || " + json.dumps(o[2:]) + " @@ " + i + " @@ " + o[0])
    print("files %d identical %d oracle-errors %d unparsable %d ; properties ours %d oracle %d equal %d" % (files, same, oracle_err, our_err, props_ours, props_oracle, props_equal))
    print("explained %d (%s) ; unexplained classes %d, instances %d" % (sum(explained.values()), ", ".join("%s:%d" % kv for kv in sorted(explained.items())), len(classes), sum(classes.values())))
    for c, n in classes.most_common():
        print("%6d %4d files  %s\n      e.g. %s" % (n, len(class_files[c]), c, sample[c]))


if __name__ == "__main__":
    main()
