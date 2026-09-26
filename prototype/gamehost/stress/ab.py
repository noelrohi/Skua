# PROTOTYPE (#14): A/B the pass budget in one Game Host process with interval rendering on.
# usage: python3 ab.py <swf> <seconds-per-leg> <budget>... (budget 1000000000 = no mid-frame flush)
# env: SKUA_GAMEHOST, AB_INTERVAL_MS (default 250)
import json, os, struct, subprocess, sys, threading, time

H = os.environ.get("SKUA_GAMEHOST", os.path.join(os.path.dirname(__file__), "../target/release/skua-gamehost"))
swf, secs, budgets = sys.argv[1], float(sys.argv[2]), [int(b) for b in sys.argv[3:]]
p = subprocess.Popen([H, f"--render-interval-ms={os.environ.get('AB_INTERVAL_MS', '250')}", swf],
                     stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
replies, logs, nid = {}, [], [0]

def reader():
    while True:
        h = p.stdout.read(4)
        if len(h) < 4:
            return
        b = p.stdout.read(struct.unpack("<I", h)[0])
        if b[:1] == b"L":
            logs.append(b[2:].decode(errors="replace")[:160])
        elif b[:1] in (b"Q", b"P"):
            replies[struct.unpack("<I", b[1:5])[0]] = json.loads(b[5:]) if b[:1] == b"Q" else True

threading.Thread(target=reader, daemon=True).start()

def ask(kind, extra=b""):
    nid[0] += 1
    body = kind + struct.pack("<I", nid[0]) + extra
    t = time.time()
    p.stdin.write(struct.pack("<I", len(body)) + body); p.stdin.flush()
    while nid[0] not in replies and p.poll() is None:
        time.sleep(0.01)
    return replies.get(nid[0]), time.time() - t

time.sleep(3)
ask(b"Q")
for b in budgets:
    ask(b"K", struct.pack("<I", b))
    ask(b"Q")
    lat = []
    t = time.time()
    while time.time() - t < secs:  # an Engine polling the Bridge 4x/s, like Core's waits
        lat.append(ask(b"P")[1] * 1000)
        time.sleep(0.25)
    q, _ = ask(b"Q")
    lat.sort()
    print(json.dumps({"budget": b, "renderN": q["renderN"], "renderMsAvg": q["renderMsAvg"], "renderMsMax": q["renderMsMax"],
                      "ticks": q["ticks"], "maxTickGapMs": q["maxTickGapMs"], "peak": q["maxOutstandingCmdBufs"],
                      "pingP50Ms": round(lat[len(lat) // 2], 1), "pingMaxMs": round(lat[-1], 1)}), flush=True)
print("logs:", [l for l in logs if "slow render" in l or "outstanding" in l][:5])
p.stdin.close(); p.wait()
