#!/usr/bin/env python3
"""A token introspection endpoint (RFC 7662) for the Dovecot test server: "test-token" is the one valid bearer token.

Usage: oauth-introspect.py PORT   (listens on 127.0.0.1). POST /introspect with token=... answers {"active": true, "username": "test"}
for the valid token and {"active": false} for anything else.
"""
import json
import sys
from http.server import BaseHTTPRequestHandler, HTTPServer
from urllib.parse import parse_qs


class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        length = int(self.headers.get("Content-Length", "0"))
        form = parse_qs(self.rfile.read(length).decode())
        token = form.get("token", [""])[0]
        body = {"active": True, "username": "test"} if token == "test-token" else {"active": False}
        data = json.dumps(body).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, *args):
        pass


HTTPServer(("127.0.0.1", int(sys.argv[1])), Handler).serve_forever()
