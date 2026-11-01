#!/usr/bin/env python3
"""Collects syslog datagrams on a Unix socket into a file (Cyrus and Postfix log only to syslog).

Usage: syslog-sink.py SOCKET LOGFILE
"""
import os
import socket
import sys

path, out = sys.argv[1], sys.argv[2]
if os.path.exists(path):
    os.unlink(path)
s = socket.socket(socket.AF_UNIX, socket.SOCK_DGRAM)
s.bind(path)
os.chmod(path, 0o666)
with open(out, "ab", buffering=0) as f:
    while True:
        f.write(s.recv(65536).rstrip(b"\0") + b"\n")
