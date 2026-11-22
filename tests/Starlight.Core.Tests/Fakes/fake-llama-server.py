#!/usr/bin/env python3
"""Stands in for llama-server in tests: answers /health, can be told to fail or exit."""
import http.server, os, sys, time

args = sys.argv[1:]
port = int(args[args.index("--port") + 1])
mode = os.environ.get("FAKE_MODE", "ok")
with open(os.environ["FAKE_ARGS_FILE"], "a") as f:
    f.write(" ".join(args) + "\n")
print("fake server starting", flush=True)
if mode == "die":
    print("error: cannot load model", file=sys.stderr, flush=True)
    sys.exit(3)
if mode == "slow":
    time.sleep(1.0)

class H(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path == "/health":
            self.send_response(200); self.end_headers(); self.wfile.write(b'{"status":"ok"}')
        elif self.path == "/exit":
            self.send_response(200); self.end_headers(); os._exit(1)
        else:
            self.send_response(404); self.end_headers()
    def log_message(self, *a): pass

http.server.HTTPServer(("127.0.0.1", port), H).serve_forever()
