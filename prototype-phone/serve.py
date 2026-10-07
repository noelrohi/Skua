#!/usr/bin/env python3
"""PROTOTYPE, throwaway: what an iPhone remote for Skua's windowless Engines could show.

Serves prototype-phone/index.html and a read-only JSON bridge over the installed `skua` CLI. Actions are stubs.
Run: python3 prototype-phone/serve.py, then open http://localhost:8777/?variant=A
"""

import json
import os
import subprocess
import tempfile
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlparse

HERE = Path(__file__).resolve().parent
SKUA_DIR = Path(os.environ.get("SKUA_DIR") or Path.home() / "Library/Application Support/Skua")
PORT = int(os.environ.get("PORT", "8777"))


def skua(*args, timeout=20):
    """The CLI's --json answer, or {"error": ...}."""
    try:
        out = subprocess.run(["skua", *args, "--json"], capture_output=True, text=True, timeout=timeout)
        return json.loads(out.stdout) if out.stdout.strip() else {"error": out.stderr.strip()}
    except Exception as e:
        return {"error": str(e)}


def fleet():
    """Every Manager account, with its Engine's status where one runs."""
    try:
        manager = json.loads((SKUA_DIR / "Skua.manager.json").read_text())
    except OSError:
        manager = {"accounts": []}
    engines = skua("engine", "list")
    running = {e["engine"]["name"]: e for e in engines} if isinstance(engines, list) else {}
    names = [a["name"] for a in manager.get("accounts", [])]
    names += [n for n in running if n not in names]
    return [{"name": n, **(running.get(n) or {"engine": {"name": n, "state": "offline"}})} for n in names]


def detail(engine):
    return {
        "inventory": skua("inventory", "Inventory", "--engine", engine),
        "quests": skua("quests", "Active", "--engine", engine),
        "gains": skua("logs", "gains", "--engine", engine),
        "events": skua("logs", "events", "--engine", engine, "--tail", "80"),
        "chat": skua("logs", "game", "--engine", engine, "--tail", "20"),
    }


def screenshot(engine):
    with tempfile.TemporaryDirectory() as tmp:
        path = Path(tmp) / "shot.png"
        subprocess.run(["skua", "screenshot", "--engine", engine, "--max-width", "640", "-o", str(path)],
                       capture_output=True, timeout=20)
        return path.read_bytes() if path.exists() else b""


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        url = urlparse(self.path)
        q = {k: v[0] for k, v in parse_qs(url.query).items()}
        if url.path == "/":
            return self.send(200, "text/html", (HERE / "index.html").read_bytes())
        if url.path == "/api/fleet":
            return self.json(fleet())
        if url.path == "/api/detail":
            return self.json(detail(q["engine"]))
        if url.path == "/api/shot":
            return self.send(200, "image/png", screenshot(q["engine"]))
        self.send(404, "text/plain", b"not found")

    def json(self, value):
        self.send(200, "application/json", json.dumps(value).encode())

    def send(self, code, kind, body):
        self.send_response(code)
        self.send_header("Content-Type", kind)
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, fmt, *args):
        print(time.strftime("%H:%M:%S"), fmt % args)


if __name__ == "__main__":
    print(f"PROTOTYPE phone remote on http://localhost:{PORT}/?variant=A  (read-only; actions are stubs)")
    ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()
