#!/usr/bin/env python3
"""Compare the contact fields OneMail reads from MSG files with the ones the Python package extract-msg reads.

    compare_extract_msg.py <dump.jsonl> <msg-dir>...

`dump.jsonl` is the output of `ContactTool dump` over the same files (its `origin` is `<file name>#<index>`). Run this with the interpreter
of a venv that has extract-msg installed (tools/msg/README.md pins the version). The oracle runs as a released package through its public
API only. A value that is empty on one side and absent on the other is the same value. Every difference is printed; the exit status is 1
when there is one.
"""
import datetime
import json
import os
import sys
from collections import Counter

import extract_msg

SCALARS = {
    "displayName": "displayName", "given": "givenName", "middle": "middleName", "surname": "surname", "prefix": "displayNamePrefix",
    "suffix": "generation", "nickname": "nickname", "company": "companyName", "department": "departmentName", "jobTitle": "jobTitle",
    "profession": "profession", "office": "officeLocation", "manager": "managerName", "assistant": "assistant", "spouse": "spouseName",
    "hobbies": "hobbies", "homePage": "personalHomePage", "businessPage": "businessHomePage", "im": "instantMessagingAddress",
    "freeBusy": "freeBusyLocation", "fileUnder": "fileUnder", "notes": "body",
}
PHONES = {
    "Business": "businessTelephoneNumber", "Business2": "businessTelephone2Number", "Home": "homeTelephoneNumber", "Home2": "homeTelephone2Number",
    "Mobile": "mobileTelephoneNumber", "Car": "carTelephoneNumber", "Pager": "pagerTelephoneNumber", "Radio": "radioTelephoneNumber",
    "Primary": "primaryTelephoneNumber", "Other": "otherTelephoneNumber", "Company": "companyMainTelephoneNumber", "Assistant": "assistantTelephoneNumber",
    "Callback": "callbackTelephoneNumber", "Isdn": "isdnNumber", "Telex": "telexNumber", "TtyTdd": "tddTelephoneNumber",
    "BusinessFax": "businessFaxNumber", "HomeFax": "homeFaxNumber", "PrimaryFax": "primaryFaxNumber",
}
ADDRESSES = {"Business": "workAddress", "Home": "homeAddress", "Other": "otherAddress"}
PARTS = ["Street", "Locality", "StateOrProvince", "PostalCode", "Country", "PostOfficeBox"]  # the order of the dump's address rows after the kind

COMPARED = Counter()
DIFFS = []


def norm(x):
    if x is None:
        return None
    if isinstance(x, list):
        x = "|".join(str(i) for i in x)
    if isinstance(x, bytes):
        x = x.decode("utf-16-le", "replace")
    x = x.rstrip("\x00")
    return x if x != "" else None


def check(where, field, ours, theirs):
    COMPARED[field.split(".")[0]] += 1
    a, b = norm(ours), norm(theirs)
    if a != b:
        DIFFS.append(f"{where}: {field}: ours {a!r:.80} oracle {b!r:.80}")


def date_of(m, local, utc):
    d = getattr(m, local, None) or getattr(m, utc, None)
    if d and d.tzinfo is not None:
        # The package shows the stored UTC time in its host zone (a midnight-UTC date shows as the evening before in the Americas).
        d = d.astimezone(datetime.timezone.utc)
    return d.strftime("%Y-%m-%d") if d else None


def compare(origin, fields, m):
    for k, attr in SCALARS.items():
        theirs = getattr(m, attr, None)
        check(origin, k, fields.get(k), theirs)
    check(origin, "birthDate", fields.get("birthDate"), date_of(m, "birthdayLocal", "birthday"))
    check(origin, "anniversary", fields.get("anniversary"), date_of(m, "weddingAnniversaryLocal", "weddingAnniversary"))
    check(origin, "children", "|".join(fields.get("children") or []) or None, "|".join(getattr(m, "childrensNames", None) or []) or None)
    ours_phones = {k: v for k, v in (fields.get("phones") or [])}
    for kind, attr in PHONES.items():
        check(origin, "phone." + kind, ours_phones.get(kind), getattr(m, attr, None))
    for row in fields.get("addresses") or []:
        kind, street, city, state, postal, country, pobox = row
        prefix = ADDRESSES[kind]
        for part, value in zip(PARTS, [street, city, state, postal, country, pobox]):
            check(origin, f"address.{kind}.{part}", value, getattr(m, prefix + part, None))
    have = {r[0] for r in (fields.get("addresses") or [])}
    for kind, prefix in ADDRESSES.items():
        if kind not in have:
            for part in PARTS:
                check(origin, f"address.{kind}.{part}", None, getattr(m, prefix + part, None))
    emails = {int(r[0]): r for r in (fields.get("emails") or [])}
    for slot in (1, 2, 3):
        r = emails.get(slot)
        check(origin, f"email{slot}.address", r[3] if r else None, getattr(m, f"email{slot}EmailAddress", None))
        check(origin, f"email{slot}.type", r[2] if r else None, getattr(m, f"email{slot}AddressType", None))
        check(origin, f"email{slot}.name", r[1] if r else None, getattr(m, f"email{slot}DisplayName", None))
    uf = fields.get("userFields") or []
    for i in range(4):
        check(origin, f"userField{i + 1}", uf[i] if i < len(uf) else None, getattr(m, f"contactUserField{i + 1}", None))
    check(origin, "hasPicture", str(bool(fields.get("hasPicture"))), str(bool(getattr(m, "hasPicture", False))))


def main():
    dump, dirs = sys.argv[1], sys.argv[2:]
    items = {}
    for line in open(dump, encoding="utf-8"):
        j = json.loads(line)
        items[j["origin"]] = j
    files = {}
    for d in dirs:
        for root, _, names in os.walk(d):
            for n in names:
                if n.lower().endswith((".msg", ".oft")):
                    files[n] = os.path.join(root, n)
    compared = skipped = 0
    for origin, j in sorted(items.items()):
        name = origin.split("#")[0]
        if name not in files or j["kind"] != "Contact":
            skipped += 1
            continue
        try:
            m = extract_msg.openMsg(files[name])
        except Exception as e:  # the oracle failing to open a file is not a difference
            print(f"oracle cannot open {name}: {type(e).__name__}")
            skipped += 1
            continue
        try:
            compare(origin, j["fields"], m)
            compared += 1
        finally:
            m.close()
    print(f"contacts compared {compared}, skipped {skipped}; field checks {sum(COMPARED.values())}; differences {len(DIFFS)}")
    for d in DIFFS[:200]:
        print("DIFF " + d)
    return 1 if DIFFS else 0


if __name__ == "__main__":
    sys.exit(main())
