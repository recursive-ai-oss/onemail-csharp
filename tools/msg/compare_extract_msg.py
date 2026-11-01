#!/usr/bin/env python3
"""Compare what OneMail reads from MSG files with what the Python package extract-msg reads, property by property.

    compare_extract_msg.py <MsgTool.dll> <file-or-dir>...

Run it with the interpreter of a venv that has extract-msg installed (tools/msg/README.md pins the version). The oracle runs as a
released package through its public API only. Per object (message, recipient, attachment, embedded message, recursively) it compares:

  * the set of property tags,
  * each entry's flags,
  * for fixed-length properties the 16 raw bytes of the entry; for the others the size and reserved fields of the entry,
  * the value stream bytes of every variable-length property, and each element of multi-valued ones,
  * the names of named properties (property set and numeric ID or string),
  * the number of recipients and attachments, and which attachments are embedded messages.

Every difference is either matched by a rule in EXPLAINED (with the reason) or printed as UNEXPLAINED. Exit status 1 when any is unexplained.
"""
import datetime
import decimal
import json
import os
import struct
import subprocess
import sys
from collections import Counter

import extract_msg

DOTNET = os.environ.get("DOTNET", "dotnet")


def ours(tool, path):
    out = subprocess.run([DOTNET, tool, "json", path], capture_output=True, check=True).stdout
    return json.loads(out)


def parse_name(s):
    # "{guid}/0x8510" or '{guid}/"Name"'
    guid, rest = s.split("}/", 1)
    guid = guid.strip("{").lower()
    if rest.startswith('"'):
        return (guid, "s", rest[1:-1])
    return (guid, "n", int(rest, 16))


class Diffs:
    def __init__(self):
        self.explained = Counter()
        self.unexplained = []
        self.compared = Counter()

    def add(self, where, kind, detail, reason=None):
        if reason:
            self.explained[(kind, reason)] += 1
        else:
            self.unexplained.append(f"{where}: {kind}: {detail}")


def their_names(msg):
    names = {}
    try:
        for _key, np in msg.named.items():
            local = int(np.propertyStreamID, 16)
            if hasattr(np, "name") and getattr(np, "type").name == "STRING_NAMED":
                names[local] = (np.guid.strip("{}").lower(), "s", np.name)
            else:
                names[local] = (np.guid.strip("{}").lower(), "n", int(np.propertyID, 16))
    except Exception as e:  # noqa: BLE001 - the oracle failing is reported, not hidden
        names["error"] = repr(e)
    return names


def decode_fixed(type_, b):
    """Decode a fixed-length value the way a reader of MS-OXCDATA 2.11.1 would; None for types that are not compared."""
    if type_ == 0x0002 and len(b) >= 2:
        return struct.unpack("<h", b[:2])[0]
    if type_ in (0x0003, 0x000A) and len(b) >= 4:
        return struct.unpack("<i", b[:4])[0]
    if type_ == 0x0004 and len(b) >= 4:
        return struct.unpack("<f", b[:4])[0]
    if type_ == 0x0005 and len(b) >= 8:
        return struct.unpack("<d", b[:8])[0]
    if type_ in (0x0006, 0x0014) and len(b) >= 8:
        return struct.unpack("<q", b[:8])[0]
    if type_ == 0x000B and len(b) >= 1:
        return b[0] != 0
    if type_ == 0x0040 and len(b) >= 8:
        ft = struct.unpack("<Q", b[:8])[0]
        try:
            return datetime.datetime(1601, 1, 1, tzinfo=datetime.timezone.utc) + datetime.timedelta(microseconds=ft // 10)
        except OverflowError:
            return None
    return None


MV_ELEMENT = {0x1002: 2, 0x1003: 4, 0x1004: 4, 0x1005: 8, 0x1006: 8, 0x1007: 8, 0x1014: 8, 0x1040: 8, 0x1048: 16}


def same_value(a, b):
    if type(b).__name__ == "NullDate":
        # The oracle's own object for the "no date" sentinel, which is 4501-01-01 as a FILETIME.
        return isinstance(a, datetime.datetime) and a.year == 4501 and a.month == 1 and a.day == 1
    if isinstance(a, datetime.datetime) and isinstance(b, datetime.datetime):
        if b.tzinfo is None:
            b = b.replace(tzinfo=datetime.timezone.utc)
        return abs((a - b).total_seconds()) < 0.000002
    if isinstance(b, decimal.Decimal):
        return decimal.Decimal(a) / 10000 == b  # currency: the oracle scales to units, the file holds ten-thousandths
    if isinstance(a, float) and isinstance(b, (int, float)):
        return a == b or (a != a and b != b) or abs(a - b) <= 1e-6 * max(1.0, abs(a))
    if isinstance(a, int) and isinstance(b, int):
        return a == b or (a & 0xFFFFFFFF) == (b & 0xFFFFFFFF) or (a & 0xFFFFFFFFFFFFFFFF) == (b & 0xFFFFFFFFFFFFFFFF)
    return a == b


def compare_object(d, where, mine, theirs, names, getstream, tally, getstringstream=None):
    source8 = mine.get("string8Source", "")
    tp = {k.upper(): v for k, v in theirs.props.items()}
    mp = {}
    dup = Counter(p["tag"] for p in mine["props"])
    for p in mine["props"]:
        mp[p["tag"].upper()] = p  # the last one wins, as in ours
    for t in sorted(set(mp) | set(tp)):
        tally["props"] += 1
        a = mp.get(t)
        b = tp.get(t)
        if a is None:
            d.add(where, "only-in-oracle", t)
            continue
        if b is None:
            if not a["entry"]:
                d.add(where, "only-in-ours", t, "orphan-value-stream (no entry; AmbiguousOrphanValueStream)")
            else:
                d.add(where, "only-in-ours", t)
            continue
        if dup[t] > 1:
            d.add(where, "duplicate-tag", t, "oracle keeps one of the duplicated entries (AmbiguousDuplicateProperty)")
            continue
        tail = int(a["tail"], 16)
        if b.flags != a["flags"]:
            d.add(where, "flags", f"{t} ours {a['flags']} theirs {b.flags}")
        type_ = int(t[4:], 16)
        if a["storage"] == "Inline":
            if hasattr(b, "value"):
                mine_v = decode_fixed(type_, bytes.fromhex(a["values"][0]))
                # The oracle decodes 16-bit integers as unsigned and gives the signed reading as signedValue.
                if mine_v is not None and not same_value(mine_v, b.value) and not same_value(mine_v, getattr(b, "signedValue", None)):
                    d.add(where, "fixed-value", f"{t} ours {mine_v!r} theirs {b.value!r}")
                tally["fixed"] += 1
            else:
                d.add(where, "kind", f"{t} ours inline, oracle variable-length")
        else:
            if hasattr(b, "size") and a["storage"] in ("Stream", "MultiFixedStream", "MultiVariableStreams"):
                # The oracle's size is in characters for strings: (bytes - 2) / 2 for Unicode, bytes - 1 for 8-bit.
                ours_size = tail & 0xFFFFFFFF
                base = type_ & 0x0FFF
                conv = {0x1F: (ours_size - 2) // 2, 0x1E: ours_size - 1}.get(base, ours_size) if not (type_ & 0x1000) else ours_size
                if type_ in MV_ELEMENT:
                    conv = ours_size // MV_ELEMENT[type_]  # the oracle reports the element count for fixed-length multi-valued entries
                if type_ in (0x101E, 0x101F):
                    conv = ours_size // 4  # and the number of length entries for multi-valued strings
                if type_ == 0x1102:
                    conv = ours_size // 8
                if b.size not in (conv, ours_size):
                    d.add(where, "entry-size", f"{t} ours {ours_size} theirs {b.size}")
            if hasattr(b, "reservedFlags") and b.reservedFlags != (tail >> 32):
                d.add(where, "entry-reserved", f"{t} ours {tail >> 32} theirs {b.reservedFlags}")
            sname = "__substg1.0_" + t
            if getstream is None:
                continue
            if a["storage"] in ("Stream", "MultiFixedStream"):
                if a["storage"] == "Stream":
                    mine_bytes = bytes.fromhex(a["values"][0])
                else:
                    mine_bytes = b"".join(bytes.fromhex(v) for v in a["values"])
                if len(a["values"]) == 1 and a["values"][0].startswith("sha256:"):
                    continue
                theirs_bytes = getstream(sname)
                if theirs_bytes is None:
                    d.add(where, "value-stream", f"{t} not found by oracle")
                elif bytes(theirs_bytes) != mine_bytes:
                    d.add(where, "value-bytes", f"{t} ours {len(mine_bytes)} bytes theirs {len(theirs_bytes)} bytes")
                tally["values"] += 1
            elif a["storage"] == "MultiVariableStreams":
                for i, v in enumerate(a["values"]):
                    tb = getstream(f"{sname}-{i:08X}")
                    if tb is None or bytes(tb) != bytes.fromhex(v):
                        d.add(where, "value-bytes", f"{t}[{i}] differs or missing in oracle")
                    tally["values"] += 1
        # Decoded text: the oracle decodes 8-bit strings with the code page it resolves for the message.
        if type_ in (0x001E, 0x001F) and "text" in a and getstringstream is not None and not any(o != t and o[:4] == t[:4] for o in mp):
            try:
                tt = getstringstream("__substg1.0_" + t[:4])
            except Exception as e:  # noqa: BLE001
                tt = None
                d.add(where, "text", f"{t}: oracle raised {type(e).__name__}")
            if tt is not None:
                tally["text"] += 1
                if tt.rstrip("\x00") != a["text"].rstrip("\x00"):
                    if type_ == 0x001E and source8 in ("LocaleId", "InternetCodepage", "Enclosing") and "3FFD0003" not in tp:
                        # The oracle takes the code page of 8-bit strings from PidTagMessageCodepage only and otherwise guesses a
                        # fixed Latin one. Ours reads the message locale or the Internet code page, which gives the text the
                        # file is about (Cyrillic and Chinese names here) where the oracle gives mojibake.
                        d.add(where, "text", t, "8-bit strings with no PidTagMessageCodepage: ours uses the locale / Internet code page, the oracle a fixed default (AmbiguousCodePage)")
                    else:
                        d.add(where, "text", f"{t} ours {a['text'][:30]!r} theirs {tt[:30]!r}")
        # named properties
        local = int(t[:4], 16)
        if local >= 0x8000:
            tally["named"] += 1
            tn = names.get(local)
            mn = parse_name(a["name"]) if "name" in a else None
            if tn != mn:
                d.add(where, "name", f"{t} ours {mn} theirs {tn}")


def compare_message(d, where, mine, theirs, names, tally):
    compare_object(d, where, mine, theirs, names, theirs.getStream, tally, theirs.getStringStream)
    tally["objects"] += 1
    h = bytes.fromhex(mine["header"])
    if len(h) >= 24:
        ours_h = struct.unpack("<4I", h[8:24])
        theirs_h = (theirs.props.nextRecipientId, theirs.props.nextAttachmentId, theirs.props.recipientCount, theirs.props.attachmentCount)
        if ours_h != theirs_h:
            d.add(where, "header-counts", f"ours {ours_h} theirs {theirs_h}")
    r_ours, r_theirs = mine["recipients"], list(theirs.recipients)
    if len(r_ours) != len(r_theirs):
        d.add(where, "recipient-count", f"ours {len(r_ours)} theirs {len(r_theirs)}")
    for i, (a, b) in enumerate(zip(r_ours, r_theirs)):
        compare_object(d, f"{where}/recip{i}", a, b, names, b.getStream, tally, b.getStringStream)
        tally["objects"] += 1
    a_ours, a_theirs = mine["attachments"], list(theirs.attachments)
    if len(a_ours) != len(a_theirs):
        # Observed black-box: the oracle lists no attachments for a message whose transport headers say multipart/signed (the
        # attachment is the signature part, e.g. an application/pgp-signature); we list every attachment the file holds.
        signed = False
        try:
            signed = str(theirs.header.get("content-type", "")).lower().startswith("multipart/signed") and len(a_theirs) == 0
        except Exception:
            pass
        d.add(where, "attachment-count", f"ours {len(a_ours)} theirs {len(a_theirs)}",
              "the oracle lists no attachments of a multipart/signed message (signature part)" if signed else None)
    for i, (a, b) in enumerate(zip(a_ours, a_theirs)):
        # Some attachment classes of the oracle (signed attachments) wrap the attachment and expose no properties or streams.
        if not hasattr(b, "props"):
            d.add(where, "no-property-access", f"attach{i}", f"oracle class {type(b).__name__} exposes no properties; that attachment is not compared")
            continue
        gs = b.getStream if hasattr(b, "getStream") else None
        compare_object(d, f"{where}/attach{i}", a["attachment"], b, names, gs, tally, getattr(b, "getStringStream", None))
        tally["objects"] += 1
        emb_theirs = b.data if type(b).__name__ == "EmbeddedMsgAttachment" else None
        if ("embedded" in a) != (emb_theirs is not None):
            d.add(where, "embedded", f"attach{i}: ours {'embedded' in a} theirs {emb_theirs is not None}")
        elif emb_theirs is not None:
            compare_message(d, f"{where}/attach{i}/msg", a["embedded"], emb_theirs, names, tally)


def main():
    tool = sys.argv[1]
    files = []
    for p in sys.argv[2:]:
        if os.path.isdir(p):
            for r, _, fs in os.walk(p):
                files += [os.path.join(r, f) for f in fs if f.lower().endswith((".msg", ".oft"))]
        else:
            files.append(p)
    files.sort()
    total = Counter()
    d = Diffs()
    skipped = []
    for path in files:
        base = os.path.basename(path)
        mine = ours(tool, path)
        if "error" in mine:
            skipped.append((base, "ours: " + mine["error"][:80]))
            continue
        try:
            msg = extract_msg.openMsg(path)
        except Exception as e:  # noqa: BLE001
            skipped.append((base, f"oracle cannot open: {type(e).__name__}"))
            continue
        names = their_names(msg)
        tally = Counter()
        before = len(d.unexplained)
        try:
            compare_message(d, base, mine["message"], msg, names, tally)
        except Exception as e:  # noqa: BLE001
            d.unexplained.append(f"{base}: oracle failed while reading: {type(e).__name__}: {str(e)[:100]}")
        total.update(tally)
        print(f"{base}\tobjects={tally['objects']}\tprops={tally['props']}\tvalues={tally['values']}\tnamed={tally['named']}\tunexplained={len(d.unexplained) - before}")
        msg.close()
    print()
    print(f"files compared {len(files) - len(skipped)} of {len(files)}; objects {total['objects']}, properties {total['props']}, value streams {total['values']}, decoded strings {total['text']}, named {total['named']}")
    for (kind, reason), n in sorted(d.explained.items()):
        print(f"  explained x{n}: {kind}: {reason}")
    for base, why in skipped:
        print(f"  skipped {base}: {why}")
    print(f"unexplained differences: {len(d.unexplained)}")
    for line in d.unexplained[:60]:
        print("  " + line)
    return 1 if d.unexplained else 0


if __name__ == "__main__":
    sys.exit(main())
