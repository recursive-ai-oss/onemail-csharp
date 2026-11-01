#!/usr/bin/env python3
"""A minimal SMTP sink for the Dovecot submission service to relay into: accepts every message, keeps none.

Usage: smtp-sink.py PORT   (listens on 127.0.0.1). Speaks just enough ESMTP: EHLO, MAIL, RCPT, DATA, BDAT, RSET, NOOP, QUIT.
"""
import asyncio
import sys


async def handle(reader, writer):
    async def send(line):
        writer.write(line.encode() + b"\r\n")
        await writer.drain()

    await send("220 sink ESMTP ready")
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
        elif verb in ("MAIL", "RCPT", "RSET", "NOOP"):
            await send("250 2.0.0 ok")
        elif verb == "DATA":
            await send("354 end with <CRLF>.<CRLF>")
            while True:
                data = await reader.readline()
                if not data or data in (b".\r\n", b".\n"):
                    break
            await send("250 2.0.0 queued")
        elif verb == "BDAT":
            parts = cmd.split()
            n = int(parts[1])
            await reader.readexactly(n)
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


asyncio.run(main(int(sys.argv[1])))
