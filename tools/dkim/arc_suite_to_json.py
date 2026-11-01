#!/usr/bin/env python3
"""Turns the ARC test suite's YAML scenarios into one JSON list for `DkimInterop arcsuite`.

  arc_suite_to_json.py <suite dir containing tests/> <out.json>

The suite (MIT licence, https://github.com/ValiMail/arc_test_suite) is fetched by tools/dkim/fetch-arc-suite.sh and never committed.
Each entry: kind ("validate" or "sign"), file, name, spec, description, message, txt (DNS name -> record), and for validation `cv`
(expected chain status), for signing the expected AS, AMS and AAR header values.
"""
import json
import sys
from pathlib import Path

import yaml

root = Path(sys.argv[1])
out = []
for kind, fname in (("validate", "arc-draft-validation-tests.yml"), ("sign", "arc-draft-sign-tests.yml")):
    for scenario in yaml.safe_load_all((root / "tests" / fname).read_text()):
        if not scenario:
            continue
        txt = {k: str(v).strip() for k, v in (scenario.get("txt_records") or scenario.get("txt-records") or {}).items()}
        for name, t in (scenario.get("tests") or {}).items():
            e = {"kind": kind, "file": fname, "name": name, "spec": str(t.get("spec", "")), "description": str(t.get("description", "")).strip(),
                 "message": t["message"], "txt": txt}
            if kind == "validate":
                e["cv"] = str(t.get("cv")).lower()
            else:
                for k in ("AS", "AMS", "AAR", "t", "sig-headers", "srv-id"):
                    if k in t:
                        e[k] = str(t[k])
                e["domain"] = scenario.get("domain")
                e["sel"] = scenario.get("sel")
            out.append(e)
Path(sys.argv[2]).write_text(json.dumps(out, indent=1))
print(len(out), "tests:", sum(1 for e in out if e["kind"] == "validate"), "validate,", sum(1 for e in out if e["kind"] == "sign"), "sign")
