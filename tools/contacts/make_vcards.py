#!/usr/bin/env python3
"""Writes seeded vCard fixtures (2.1, 3.0 and 4.0) for the typed-view and conversion checks.

    make_vcards.py OUT_DIR [SEED [COUNT]]

Each card mixes the shapes that real exporters produce: type words as bare parameters (2.1), quoted-printable and CHARSET lines, groups,
partial dates, structured values with escapes and empty parts, multiple values of one property, inline and URI photos, KIND:group cards.
The seed fixes the output, so the files can be regenerated and are committed.
"""
import base64
import os
import random
import sys

NAMES = ["Anna", "Müller", "O'Brien", "李雷", "Zoë", "Smith", "Åsa", "Dan", "Fennell", "Ünal"]
ORGS = ["Contoso, Ltd.", "R&D;Labs", "Fabrikam", "Acme\\Corp"]


def esc(s, v21=False):
    if v21:
        return s.replace("\\", "\\\\").replace(";", "\\;")
    return s.replace("\\", "\\\\").replace(";", "\\;").replace(",", "\\,").replace("\n", "\\n")


def qp(s):
    return "".join(f"={b:02X}" if b > 126 or b < 32 or b == 61 else chr(b) for b in s.encode("utf-8"))


def fold(line):
    out, data = [], line.encode("utf-8")
    while len(data) > 75:
        cut = 75
        while (data[cut] & 0xC0) == 0x80:
            cut -= 1
        out.append(data[:cut].decode("utf-8"))
        data = b" " + data[cut:]
    out.append(data.decode("utf-8"))
    return "\r\n".join(out)


def card(rng, version, i):
    pick = lambda: rng.choice(NAMES)
    given, family = pick(), pick()
    L = ["BEGIN:VCARD", f"VERSION:{version}"]
    if version == "4.0" and i % 9 == 0:
        L += [f"KIND:group", f"FN:Team {i}", f"MEMBER:mailto:{given.lower()}{i}@example.com", f"MEMBER:urn:uuid:{rng.getrandbits(32):08x}-0000-4000-8000-000000000000"]
        L.append("END:VCARD")
        return "\r\n".join(fold(x) for x in L) + "\r\n"
    if version == "2.1":
        L.append(f"N;CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE:{qp(family)};{qp(given)};;;" if rng.random() < .5 else f"N:{esc(family, True)};{esc(given, True)};;;")
        L.append(f"FN:{given} {family}")
        L.append(f"TEL;WORK;VOICE:+1-555-{rng.randrange(1000, 9999)}")
        L.append(f"TEL;CELL;PREF:+1-555-{rng.randrange(1000, 9999)}")
        L.append(f"ADR;HOME:;;{rng.randrange(1, 999)} Elm St;Boulder;CO;33041;USA")
        L.append(f"EMAIL;INTERNET:{given.lower()}{i}@example.com")
        if rng.random() < .5:
            L.append(f"NOTE;ENCODING=QUOTED-PRINTABLE;CHARSET=UTF-8:{qp('Zoë: line one' + chr(10) + 'line two')}")
        if rng.random() < .4:
            L += ["PHOTO;ENCODING=BASE64;JPEG:" + base64.b64encode(bytes(rng.randrange(256) for _ in range(30))).decode(), ""]
    else:
        pre = "item1." if rng.random() < .2 else ""
        L.append(f"N:{esc(family)};{esc(given)};{esc(pick())};{'Dr.' if rng.random() < .3 else ''};")
        L.append(f"FN:{esc(given)} {esc(family)}")
        if rng.random() < .5:
            L.append(f"NICKNAME:{esc(pick())},{esc(pick())}")
        L.append(f"ORG:{esc(rng.choice(ORGS))};{esc('Dept ' + str(i))}")
        L.append(f"TITLE:{esc(rng.choice(['Engineer', 'R;D Lead, EMEA', 'Dr']))}")
        for t in rng.sample(["WORK", "HOME", "CELL", "FAX", "VOICE", "PAGER"], rng.randrange(1, 4)):
            tp = f"TYPE={t.lower()}"
            L.append(f"TEL;{tp}:+1-555-{rng.randrange(1000, 9999)}" if version == "3.0" else f"TEL;{tp};VALUE=uri:tel:+1-555-{rng.randrange(1000, 9999)}")
        for t in rng.sample(["WORK", "HOME", "POSTAL"], rng.randrange(0, 3)):
            L.append(f"ADR;TYPE={t.lower()}:;;{rng.randrange(1, 999)} Main St\\nSuite {i};Town\\, Hills;ST;{rng.randrange(10000, 99999)};Country")
        for j in range(rng.randrange(1, 4)):
            pref = ";TYPE=pref" if version == "3.0" and j == 0 else (";PREF=1" if j == 0 else "")
            L.append(f"{pre}EMAIL;TYPE=internet{pref}:{given.lower()}{i}.{j}@example.com")
        bd = rng.choice(["1980-03-22", "19800322", "--0322", "1980-03", "1980-03-22T10:20:30Z"] if version == "4.0" else ["1980-03-22", "1980-03-22T10:20:30Z"])
        L.append(f"BDAY:{bd}")
        if version == "4.0":
            L.append(f"ANNIVERSARY:{rng.choice(['20100605', '--0605'])}")
            L.append(f"GENDER:{rng.choice(['M', 'F', 'O', 'F;she', ';custom'])}")
            L.append(f"LANG;PREF=1:{rng.choice(['en', 'fr', 'de'])}")
            L.append(f"GEO:geo:{rng.uniform(-80, 80):.5f},{rng.uniform(-170, 170):.5f}")
            L.append(f"IMPP:xmpp:{given.lower()}@example.com")
        else:
            L.append(f"X-MS-ANNIVERSARY:2010-06-05")
            L.append(f"X-MS-TEXT:user field {i}")
            L.append(f"X-MS-IMADDRESS:{given.lower()}@im.example")
        L.append(f"CATEGORIES:{esc(pick())},{esc(pick())}")
        L.append(f"NOTE:{esc('Line one' + chr(10) + 'with; punctuation, and \\ slash')}")
        L.append(f"URL;TYPE=work:https://example.com/{i}")
        if rng.random() < .4:
            raw = base64.b64encode(bytes(rng.randrange(256) for _ in range(60))).decode()
            L.append("PHOTO;ENCODING=b;TYPE=JPEG:" + raw if version == "3.0" else "PHOTO:data:image/jpeg;base64," + raw)
        elif rng.random() < .3:
            L.append("PHOTO;VALUE=uri:https://example.com/p.jpg" if version == "3.0" else "PHOTO:https://example.com/p.jpg")
        L.append(f"REV:2024{rng.randrange(1, 13):02d}{rng.randrange(1, 28):02d}T101010Z")
        L.append(f"UID:urn:uuid:{rng.getrandbits(32):08x}-1111-4222-8333-444444444444")
        L.append(f"X-CUSTOM-{i}:value")
    L.append("END:VCARD")
    return "\r\n".join(fold(x) for x in L) + "\r\n"


def main():
    out = sys.argv[1]
    seed = int(sys.argv[2]) if len(sys.argv) > 2 else 20261031
    count = int(sys.argv[3]) if len(sys.argv) > 3 else 60
    rng = random.Random(seed)
    os.makedirs(out, exist_ok=True)
    for i in range(count):
        v = ["2.1", "3.0", "4.0"][i % 3]
        with open(os.path.join(out, f"gen-{v.replace('.', '')}-{i:03d}.vcf"), "w", encoding="utf-8", newline="") as f:
            f.write(card(rng, v, i))
    print(f"wrote {count} vCards to {out} (seed {seed})")


if __name__ == "__main__":
    main()
