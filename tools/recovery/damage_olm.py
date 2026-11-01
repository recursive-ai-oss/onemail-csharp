#!/usr/bin/env python3
"""Makes damaged variants of an Outlook for Mac archive (.olm), deterministically from a seed.

usage: damage_olm.py IN.olm OUT_DIR [--seed N]

Writes, next to each other in OUT_DIR:
  truncate-25 / -50 / -90   the file cut after that percentage of its bytes (an interrupted copy or download)
  garbage-between           300 random bytes inserted before a local header in the middle of the archive
  no-directory              the central directory's end record signature destroyed
  bad-directory-offset      the end record points the central directory at the wrong place
  corrupt-late              bytes flipped late in the data of the largest XML record file
  corrupt-entries           bytes flipped inside the data of three entries chosen by the seed
  zero-block                a 4 KiB block of zeros written over the middle of the archive
The input is only read.
"""
import argparse
import os
import random
import struct


def zip64_extra(extra, want_usize, want_csize, want_local):
    """Reads the zip64 extended information extra field (APPNOTE 4.5.3): only the fields the 32-bit header marked 0xFFFFFFFF are present."""
    pos = 0
    while pos + 4 <= len(extra):
        tag, size = struct.unpack_from("<HH", extra, pos)
        if tag == 1:
            vals = []
            p = pos + 4
            out = {}
            for key, wanted in (("usize", want_usize), ("csize", want_csize), ("local", want_local)):
                if wanted:
                    out[key], = struct.unpack_from("<Q", extra, p)
                    p += 8
            return out
        pos += 4 + size
    return {}


def central_entries(data):
    """(name, local header offset, compressed size, header length) for every entry of the central directory, and the end record's offset."""
    eocd = data.rfind(b"PK\x05\x06")
    if eocd < 0:
        raise SystemExit("no end of central directory record")
    total, size, off = struct.unpack_from("<HII", data, eocd + 10)
    if off == 0xFFFFFFFF or total == 0xFFFF:
        loc = data.rfind(b"PK\x06\x07", 0, eocd)
        z64, = struct.unpack_from("<Q", data, loc + 8)
        total, = struct.unpack_from("<Q", data, z64 + 32)
        off, = struct.unpack_from("<Q", data, z64 + 48)
    entries = []
    pos = off
    for _ in range(total):
        if data[pos:pos + 4] != b"PK\x01\x02":
            break
        csize, usize = struct.unpack_from("<II", data, pos + 20)
        nlen, elen, clen = struct.unpack_from("<HHH", data, pos + 28)
        local, = struct.unpack_from("<I", data, pos + 42)
        name = data[pos + 46:pos + 46 + nlen].decode("utf-8", "replace")
        if 0xFFFFFFFF in (csize, usize, local):
            z = zip64_extra(data[pos + 46 + nlen:pos + 46 + nlen + elen], usize == 0xFFFFFFFF, csize == 0xFFFFFFFF, local == 0xFFFFFFFF)
            csize = z.get("csize", csize)
            local = z.get("local", local)
        lnlen, lelen = struct.unpack_from("<HH", data, local + 26)
        entries.append((name, local, csize, 30 + lnlen + lelen))
        pos += 46 + nlen + elen + clen
    return entries, eocd


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("input")
    ap.add_argument("out")
    ap.add_argument("--seed", type=int, default=2026)
    a = ap.parse_args()
    rng = random.Random(a.seed)
    data = open(a.input, "rb").read()
    os.makedirs(a.out, exist_ok=True)
    entries, eocd = central_entries(data)
    big = [e for e in entries if e[2] > 64]
    if len(big) < 3:
        raise SystemExit("too few entries with data to damage")

    def write(name, b):
        with open(os.path.join(a.out, name + ".olm"), "wb") as f:
            f.write(b)

    for pct in (25, 50, 90):
        write(f"truncate-{pct}", data[: len(data) * pct // 100])

    mid = sorted(entries, key=lambda e: e[1])[len(entries) // 2]
    junk = bytearray(rng.randbytes(300))
    for i in range(len(junk) - 1):
        if junk[i] == 0x50 and junk[i + 1] == 0x4B:
            junk[i] = 1
    write("garbage-between", data[: mid[1]] + bytes(junk) + data[mid[1]:])

    b = bytearray(data)
    b[eocd:eocd + 4] = b"\0\0\0\0"
    write("no-directory", bytes(b))

    b = bytearray(data)
    struct.pack_into("<I", b, eocd + 16, 12345)
    loc = data.rfind(b"PK\x06\x07", 0, eocd)
    if loc >= 0:
        z64, = struct.unpack_from("<Q", data, loc + 8)
        struct.pack_into("<Q", b, z64 + 48, 12345)
    write("bad-directory-offset", bytes(b))

    b = bytearray(data)
    for name, local, csize, hlen in rng.sample(big, 3):
        for _ in range(6):
            b[local + hlen + rng.randrange(min(csize, 64) if csize > 64 else csize)] ^= 0xFF
    write("corrupt-entries", bytes(b))

    # The largest XML file that holds many records (a calendar or an address book) damaged late in its data: its earlier records still decode.
    xmls = [e for e in entries if e[0].endswith(".xml") and "__Attachments" not in e[0]]
    many = [e for e in xmls if "message_" not in e[0]] or xmls     # calendars, address books, tasks hold many records per entry
    name, local, csize, hlen = max(many, key=lambda e: e[2])
    b = bytearray(data)
    for _ in range(6):
        b[local + hlen + int(csize * 0.75) + rng.randrange(max(1, csize // 5))] ^= 0xFF
    write("corrupt-late", bytes(b))

    b = bytearray(data)
    start = len(b) // 2
    b[start:start + 4096] = bytes(4096)
    write("zero-block", bytes(b))


if __name__ == "__main__":
    main()
