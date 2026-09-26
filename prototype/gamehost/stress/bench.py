# PROTOTYPE (#14): render-time + device-loss bench for a stress SWF, no login.
# usage: python3 bench.py <swf> [renders=20] [warmup_s=5] [gamehost args...]
# env: BENCH_PNG=<path> saves a screenshot after the bench; SKUA_GAMEHOST (default: ../target/release/skua-gamehost), SKUA_FLUSH_* passed through.
import json, os, struct, subprocess, sys, threading, time

H = os.environ.get("SKUA_GAMEHOST", os.path.join(os.path.dirname(__file__), "../target/release/skua-gamehost"))
swf = sys.argv[1]
n = int(sys.argv[2]) if len(sys.argv) > 2 else 20
warm = float(sys.argv[3]) if len(sys.argv) > 3 else 5
p = subprocess.Popen([H] + sys.argv[4:] + [swf], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
replies, lost, last = {}, [], {"f": ""}

def reader():
    while True:
        h = p.stdout.read(4)
        if len(h) < 4:
            return
        b = p.stdout.read(struct.unpack("<I", h)[0])
        k = b[:1]
        if k == b"F":
            last["f"] = b[1:].decode(errors="replace")
        elif k == b"L":
            t = b[2:].decode(errors="replace")
            if "device lost" in t.lower() or "panic" in t or "outstanding" in t or "Device" in t:
                lost.append(t[:200])
        elif k == b"I":
            replies[struct.unpack("<I", b[1:5])[0]] = b[5 + 16:]
        elif k == b"Q":
            replies[struct.unpack("<I", b[1:5])[0]] = json.loads(b[5:])

threading.Thread(target=reader, daemon=True).start()

def send(kind, id, extra=b""):
    body = kind + struct.pack("<I", id) + extra
    p.stdin.write(struct.pack("<I", len(body)) + body)
    p.stdin.flush()

def ask(kind, id, extra=b"", timeout=600):
    send(kind, id, extra)
    t = time.time()
    while id not in replies and time.time() - t < timeout and p.poll() is None and not lost:
        time.sleep(0.05)
    return replies.get(id)

time.sleep(warm)
bench = ask(b"B", 1, struct.pack("<I", n))
stats = ask(b"Q", 2)
if os.environ.get("BENCH_PNG"):
    png = ask(b"S", 3, struct.pack("<I", 0))
    if png:
        open(os.environ["BENCH_PNG"], "wb").write(png)
print(json.dumps({"swf": os.path.basename(swf), "args": sys.argv[4:],
                  "env": {k: v for k, v in os.environ.items() if k.startswith("SKUA_FLUSH")},
                  "bench": bench, "stats": stats, "lastTrace": last["f"],
                  "deviceLost": lost[:3], "alive": p.poll() is None}))
p.stdin.close()
p.wait()
