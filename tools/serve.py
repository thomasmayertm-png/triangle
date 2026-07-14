#!/usr/bin/env python3
"""
Tiny LAN web server for testing the Unity WebGL build on a phone.

Usage (from the project root):
    python tools/serve.py                # serves ./WebGLBuild on port 8080
    python tools/serve.py path/to/build  # serve a different folder
    python tools/serve.py WebGLBuild 9000 # custom port

It sets the MIME types and Content-Encoding headers Unity WebGL needs
(application/wasm, and gzip/br for compressed .gz/.br files), so it works
whether you build with compression Disabled or Gzip/Brotli.

On first run Windows may pop a Firewall prompt -> allow it on PRIVATE networks,
or the phone cannot connect. Phone and PC must be on the same Wi-Fi.
"""
import http.server
import socketserver
import os
import sys
import socket
import functools

DIR = os.path.abspath(sys.argv[1]) if len(sys.argv) > 1 else os.path.abspath("WebGLBuild")
PORT = int(sys.argv[2]) if len(sys.argv) > 2 else 8080

MIME = {
    ".wasm": "application/wasm",
    ".js": "application/javascript",
    ".json": "application/json",
    ".data": "application/octet-stream",
    ".html": "text/html",
    ".css": "text/css",
    ".symbols": "application/octet-stream",
}


class Handler(http.server.SimpleHTTPRequestHandler):
    def guess_type(self, path):
        # Unity may emit compressed assets: foo.wasm.gz / foo.js.br etc.
        enc, base = None, path
        if path.endswith(".gz"):
            enc, base = "gzip", path[:-3]
        elif path.endswith(".br"):
            enc, base = "br", path[:-3]
        self._enc = enc
        ext = os.path.splitext(base)[1].lower()
        return MIME.get(ext) or super().guess_type(path)

    def end_headers(self):
        if getattr(self, "_enc", None):
            self.send_header("Content-Encoding", self._enc)
        self.send_header("Cache-Control", "no-store")  # always fetch fresh while testing
        super().end_headers()


def lan_ip():
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.connect(("8.8.8.8", 80))
        ip = s.getsockname()[0]
        s.close()
        return ip
    except OSError:
        return "127.0.0.1"


def main():
    if not os.path.isdir(DIR):
        print(f"[!] Folder not found: {DIR}")
        print("    Build WebGL in Unity first (into a folder named 'WebGLBuild' at the")
        print("    project root), or pass the build folder:  python tools/serve.py <folder>")
        sys.exit(1)

    handler = functools.partial(Handler, directory=DIR)
    socketserver.ThreadingTCPServer.allow_reuse_address = True
    with socketserver.ThreadingTCPServer(("0.0.0.0", PORT), handler) as httpd:
        ip = lan_ip()
        bar = "=" * 56
        print(bar)
        print(f"  Serving : {DIR}")
        print(f"  This PC : http://localhost:{PORT}")
        print(f"  Phone   : http://{ip}:{PORT}     (same Wi-Fi)")
        print("  Ctrl+C to stop")
        print(bar)
        try:
            httpd.serve_forever()
        except KeyboardInterrupt:
            print("\nstopped.")


if __name__ == "__main__":
    main()
