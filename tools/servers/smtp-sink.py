#!/usr/bin/env python3
"""A minimal SMTP sink for the Dovecot submission service and the Postfix guest to relay into.

Usage: smtp-sink.py PORT [DIR]   (listens on 127.0.0.1). Speaks just enough ESMTP: EHLO, MAIL, RCPT, DATA, BDAT, RSET, NOOP, QUIT.
Accepts every message. With DIR, each message is kept there as N.eml (the bytes between DATA and the end dot, dot-unstuffed, or the
BDAT chunks joined) with N.env holding the MAIL FROM and RCPT TO lines, so tests can compare what arrived with what was sent.
"""
import asyncio
import itertools
import os
import sys

OUT = None
COUNTER = itertools.count(1)


def keep(data, envelope):
    if OUT is None:
        return
    n = "%06d" % next(COUNTER)
    with open(os.path.join(OUT, n + ".eml.tmp"), "wb") as f:
        f.write(data)
    with open(os.path.join(OUT, n + ".env"), "w") as f:
        f.write("\n".join(envelope) + "\n")
    os.rename(os.path.join(OUT, n + ".eml.tmp"), os.path.join(OUT, n + ".eml"))


async def handle(reader, writer):
    async def send(line):
        writer.write(line.encode() + b"\r\n")
        await writer.drain()

    await send("220 sink ESMTP ready")
    envelope = []
    chunks = bytearray()
    while True:
        line = await reader.readline()
        if not line:
            break
        cmd = line.decode("latin-1").strip()
        verb = cmd.split(" ", 1)[0].upper()
        if verb == "EHLO":
            await send("250-sink")
            await send("250-PIPELINING")
            await send("250-8BITMIME")
            await send("250-SMTPUTF8")
            await send("250-ENHANCEDSTATUSCODES")
            await send("250 CHUNKING")
        elif verb == "HELO":
            await send("250 sink")
        elif verb in ("MAIL", "RCPT"):
            if verb == "MAIL":
                envelope = []
            envelope.append(cmd)
            await send("250 2.0.0 ok")
        elif verb in ("RSET", "NOOP"):
            if verb == "RSET":
                envelope = []
                chunks = bytearray()
            await send("250 2.0.0 ok")
        elif verb == "DATA":
            await send("354 end with <CRLF>.<CRLF>")
            body = bytearray()
            while True:
                data = await reader.readline()
                if not data or data in (b".\r\n", b".\n"):
                    break
                body += data[1:] if data.startswith(b"..") else data
            keep(bytes(body), envelope)
            await send("250 2.0.0 queued")
        elif verb == "BDAT":
            parts = cmd.split()
            n = int(parts[1])
            chunks += await reader.readexactly(n)
            if len(parts) > 2:
                keep(bytes(chunks), envelope)
                chunks = bytearray()
            await send("250 2.0.0 chunk ok")
        elif verb == "QUIT":
            await send("221 2.0.0 bye")
            break
        else:
            await send("502 5.5.1 not implemented")
    writer.close()


async def main(port):
    server = await asyncio.start_server(handle, "127.0.0.1", port)
    async with server:
        await server.serve_forever()


if len(sys.argv) > 2:
    OUT = sys.argv[2]
    os.makedirs(OUT, exist_ok=True)
asyncio.run(main(int(sys.argv[1])))
