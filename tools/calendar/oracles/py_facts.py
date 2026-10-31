#!/usr/bin/env python3
"""Dump the normalised facts for every .ics / .vcf under a root, using a released Python library as a black box.

  py_facts.py icalendar|vobject <root> <out.jsonl>

The fact schema is the one CalendarDump writes (see tools/calendar/README.md): [path, name, params, value] where
value is a canonical string: t:<text>, d:<date>, dt:<date-time>[Z], dur:<seconds>, off:<seconds>, rr:<rule>,
per:<start>/<end|dur>, s:<structured>, l:<list>, b:<byte count>.
"""
import datetime as dt, json, os, sys


EXTS = (".ics", ".vcf", ".ifb")


def files(root):
    for d, _, fs in os.walk(root):
        for f in fs:
            if f.lower().endswith(EXTS):
                yield os.path.join(d, f)


def fmt_dt(v, zoned, utc_hint=None):
    if isinstance(v, dt.datetime):
        s = v.strftime("%Y-%m-%dT%H:%M:%S")
        is_utc = utc_hint if utc_hint is not None else (v.tzinfo is not None and v.utcoffset() == dt.timedelta(0))
        return "dt:" + s + ("Z" if is_utc and not zoned else "")
    if isinstance(v, dt.date):
        return "d:" + v.strftime("%Y-%m-%d")
    return "t:" + str(v)


def secs(td):
    return int(td.total_seconds())


def fmt_rule(r):
    parts = []
    for k, vals in r.items():
        k = k.upper()
        if not isinstance(vals, (list, tuple)):
            vals = [vals]
        out = []
        for x in vals:
            if k == "UNTIL":
                out.append(fmt_dt(x, False)[0:].split(":", 1)[0] + ":" + fmt_dt(x, False).split(":", 1)[1])
            else:
                out.append(str(x))
        if k == "INTERVAL" and out == ["1"]:
            continue
        if k == "UNTIL":
            parts.append("UNTIL=" + out[0])
        else:
            parts.append(k + "=" + ",".join(out))
    return ";".join(sorted(parts))


def norm_params(items):
    m = {}
    for k, vs in items:
        k = k.upper()
        if k in ("ENCODING", "CHARSET", "VALUE") or k.startswith("X-VOBJ-"):
            continue
        if not isinstance(vs, (list, tuple)):
            vs = [vs]
        s = m.setdefault(k, set())
        for v in vs:
            v = str(v)
            if k == "TYPE":
                for t in v.split(","):
                    if t:
                        s.add(t.lower())
            else:
                s.add(v)
    return [[k, sorted(v)] for k, v in sorted(m.items())]


# ---------------------------------------------------------------- icalendar
def ical_value(name, v):
    if isinstance(v, dt.timedelta):
        return ("off:" if name.startswith("TZOFFSET") else "dur:") + str(secs(v))
    zoned = "TZID" in getattr(v, "params", {})
    if hasattr(v, "dts"):  # EXDATE / RDATE lists
        out = []
        for x in v.dts:
            d = x.dt
            if isinstance(d, dt.timedelta):
                out.append("dur:" + str(secs(d)))
            elif isinstance(d, tuple):
                out.append("per:" + fmt_dt(d[0], zoned) + "/" + (fmt_dt(d[1], zoned) if isinstance(d[1], (dt.date,)) else "dur:%d" % secs(d[1])))
            else:
                out.append(fmt_dt(d, zoned))
        return ",".join(out)
    if hasattr(v, "dt") and not isinstance(v, (str,)):
        d = v.dt
        if isinstance(d, dt.timedelta):
            return "dur:" + str(secs(d))
        if isinstance(d, tuple):
            return "per:" + fmt_dt(d[0], zoned) + "/" + (fmt_dt(d[1], zoned) if isinstance(d[1], dt.date) else "dur:%d" % secs(d[1]))
        return fmt_dt(d, zoned)
    if hasattr(v, "td"):
        return ("off:" if name.startswith("TZOFFSET") else "dur:") + str(secs(v.td))
    if name in ("RRULE", "EXRULE") and hasattr(v, "items"):
        return "rr:" + fmt_rule(v)
    if hasattr(v, "cats"):
        return "l:" + ",".join(str(x) for x in v.cats)
    return "t:" + str(v)


def run_icalendar(root, out):
    import icalendar
    for f in sorted(files(root)):
        rel = os.path.relpath(f, root)
        try:
            cals = icalendar.Calendar.from_ical(open(f, "rb").read(), multiple=True)
            facts = []

            def walk(c, parent):
                path = (parent + "/" if parent else "") + c.name.upper()
                facts.append([path, "", [], "", ""])
                for k, v in c.property_items(recursive=False):
                    if k.upper() in ("BEGIN", "END"):
                        continue
                    params = getattr(v, "params", {})
                    facts.append([path, k.upper(), norm_params(list(params.items())), ical_value(k.upper(), v), ""])
                for s in c.subcomponents:
                    walk(s, path)

            for c in cals:
                walk(c, "")
            out.write(json.dumps({"file": rel, "facts": facts}) + "\n")
        except Exception as e:  # the library rejected the file
            out.write(json.dumps({"file": rel, "error": type(e).__name__}) + "\n")


# ------------------------------------------------------------------ vobject
def vo_value(name, v, params, is_card):
    zoned = "TZID" in params
    if isinstance(v, dt.datetime):
        return fmt_dt(v, zoned)
    if isinstance(v, dt.date):
        return fmt_dt(v, zoned)
    if isinstance(v, dt.timedelta):
        return ("off:" if name.startswith("TZOFFSET") else "dur:") + str(secs(v))
    if isinstance(v, (bytes, bytearray)):
        return "b:%d" % len(v)
    if is_card and name == "N":
        def j(x):
            return ",".join(x) if isinstance(x, list) else (x or "")
        return "s:" + ";".join(j(x) for x in (v.family, v.given, v.additional, v.prefix, v.suffix))
    if is_card and name == "ADR":
        def j(x):
            return ",".join(x) if isinstance(x, list) else (x or "")
        return "s:" + ";".join(j(x) for x in (v.box, v.extended, v.street, v.city, v.region, v.code, v.country))
    if is_card and name in ("ORG",):
        return "s:" + ";".join(v) if isinstance(v, list) else "s:" + str(v)
    if is_card and name in ("CATEGORIES", "NICKNAME"):
        return "l:" + (",".join(v) if isinstance(v, list) else str(v))
    if name in ("RRULE", "EXRULE"):
        from icalendar import vRecur
        try:
            return "rr:" + fmt_rule(vRecur.from_ical(str(v)))
        except Exception:
            return "t:" + str(v)
    if not is_card and name in ("CATEGORIES", "RESOURCES"):
        return "l:" + (",".join(v) if isinstance(v, (list, tuple)) else str(v))
    if name == "FREEBUSY" and isinstance(v, (list, tuple)):
        out = []
        for x in v:
            a, b = x
            out.append("per:" + fmt_dt(a, zoned) + "/" + (("dur:%d" % secs(b)) if isinstance(b, dt.timedelta) else fmt_dt(b, zoned)))
        return ",".join(out)
    if name == "REQUEST-STATUS" and isinstance(v, (list, tuple)):
        return "t:" + ";".join(str(x) for x in v)
    if isinstance(v, (list, tuple)):
        return ",".join(vo_value(name, x, params, is_card) for x in v)
    return "t:" + str(v)


def run_vobject(root, out):
    import vobject
    for f in sorted(files(root)):
        rel = os.path.relpath(f, root)
        try:
            text = open(f, "rb").read().decode("utf-8", "replace")
            comps = list(vobject.readComponents(text, allowQP=True))
            facts = []

            def walk(c, parent):
                path = (parent + "/" if parent else "") + c.name.upper()
                is_card = c.name.upper() == "VCARD"
                facts.append([path, "", [], "", ""])
                for key, objs in c.contents.items():
                    for o in objs:
                        if isinstance(o, vobject.base.Component):
                            continue
                        name = o.name.upper()
                        if name in ("BEGIN", "END"):
                            continue
                        gname = (o.group.upper() + "." if getattr(o, "group", None) else "") + name
                        params = {k: v for k, v in o.params.items()}
                        facts.append([path, gname, norm_params(list(params.items())), vo_value(name, o.value, params, is_card), ""])
                for key, objs in c.contents.items():
                    for o in objs:
                        if isinstance(o, vobject.base.Component):
                            walk(o, path)

            for c in comps:
                walk(c, "")
            out.write(json.dumps({"file": rel, "facts": facts}) + "\n")
        except Exception as e:
            out.write(json.dumps({"file": rel, "error": type(e).__name__}) + "\n")


if __name__ == "__main__":
    lib, root, outp = sys.argv[1:4]
    if lib == "icalendar":
        EXTS = (".ics", ".ifb")  # icalendar's vCard support is a different model; vCard is compared through vobject
    with open(outp, "w", encoding="utf-8") as out:
        {"icalendar": run_icalendar, "vobject": run_vobject}[lib](root, out)
