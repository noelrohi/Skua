# PROTOTYPE (#17): Bridge responsiveness + footprint while the Game Host renders a heavy stage. No login.
# usage: crowd.py <label> <swf> <secs> [host flags...]
# env: HOST (binary), WARM (s, default 8), KNOBS "k=v,k=v" sent as 'V' frames first, GETTER_SLEEP_MS (default 10)
import json, os, re, struct, subprocess, sys, threading, time
H = os.environ.get("HOST", os.path.join(os.path.dirname(__file__), "../target/release/skua-gamehost"))
label, swf, secs = sys.argv[1], sys.argv[2], float(sys.argv[3])
p = subprocess.Popen([H] + sys.argv[4:] + [swf], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
replies, cv, wl, bad = {}, threading.Condition(), threading.Lock(), []
def reader():
    while True:
        h = p.stdout.read(4)
        if len(h) < 4: return
        b = p.stdout.read(struct.unpack("<I", h)[0])
        if b[:1] in (b"Q", b"P", b"R", b"I"):
            with cv: replies[struct.unpack("<I", b[1:5])[0]] = b[5:]; cv.notify_all()
        elif b[:1] == b"L" and any(k in b for k in (b"panic", b"lost", b"outstanding", b"Validation", b"OutOfMemory")):
            bad.append(b[2:200].decode(errors="replace"))
threading.Thread(target=reader, daemon=True).start()
nid = [0]
def ask(kind, extra=b"", timeout=120):
    with wl:
        nid[0] += 1; i = nid[0]
        body = kind + struct.pack("<I", i) + extra
        p.stdin.write(struct.pack("<I", len(body)) + body); p.stdin.flush()
    t = time.time()
    with cv:
        cv.wait_for(lambda: i in replies, timeout=timeout)
        return replies.pop(i, None), time.time() - t
U = {"B": 1e-6, "KB": 1e-3, "MB": 1, "GB": 1e3}
def footprint():
    o = subprocess.run(["footprint", str(p.pid)], capture_output=True, text=True).stdout
    m = re.search(r"Footprint: ([\d.]+ \w+)", o)
    g = re.search(r"^\s*([\d.]+ \w+)\s+[\d.]+ \w+\s+[\d.]+ \w+\s+\d+\s+IOAccelerator \(graphics\)", o, re.M)
    f = lambda s: float(s.split()[0]) * U[s.split()[1]] if s else -1
    return round(f(m.group(1) if m else None)), round(f(g.group(1) if g else None))
time.sleep(float(os.environ.get("WARM", "8")))
for kv in filter(None, os.environ.get("KNOBS", "").split(",")):
    ask(b"V", kv.encode())
q0 = json.loads(ask(b"Q")[0])
lat, stop = [], [False]
inv = b'<invoke name="getGameObject" returntype="xml"><arguments><string>x</string></arguments></invoke>'
def getter():
    while not stop[0]:
        _, d = ask(b"C", inv)
        lat.append(d * 1000)
        time.sleep(int(os.environ.get("GETTER_SLEEP_MS", "10")) / 1000)
th = threading.Thread(target=getter); th.start()
t0, fps, qs = time.time(), [], []
while time.time() - t0 < secs:
    time.sleep(min(5, secs - (time.time() - t0)) if secs - (time.time() - t0) > 0 else 0)
    fps.append(footprint())
    q, _ = ask(b"Q"); qs.append(json.loads(q))
stop[0] = True; th.join()
lat.sort()
pc = lambda x: round(lat[min(len(lat) - 1, int(len(lat) * x))], 1) if lat else -1
agg = lambda k, f=sum: round(f(q[k] for q in qs), 1)
print(json.dumps({"label": label, "flags": sys.argv[4:], "knobs": os.environ.get("KNOBS", ""), "secs": secs,
    "getterN": len(lat), "p50": pc(.5), "p90": pc(.9), "p99": pc(.99), "max": pc(1),
    "renders": qs[-1]["renders"] - q0["renders"], "renderMsAvg": round(agg("prepMsSum") / max(1, qs[-1]["renders"] - q0["renders"]), 1), "busyPct": round(100 * (agg("prepMsSum") + (agg("submitMsSum") if qs[-1]["threaded"] else 0)) / 1000 / secs, 1), "submitMsSum": agg("submitMsSum"), "submitMsMax": agg("submitMsMax", max),
    "prepMsSum": agg("prepMsSum"), "prepMsMax": agg("prepMsMax", max),
    "mainBlockedMsSum": agg("mainBlockedMsSum"), "mainBlockedMsMax": agg("mainBlockedMsMax", max),
    "maxTickGapMs": agg("maxTickGapMs", max), "maxTickMs": agg("maxTickMs", max), "rc": qs[-1]["rc"], "gapMs": qs[-1]["renderGapMs"],
    "fpMB": [f[0] for f in fps], "gfxMB": [f[1] for f in fps], "bad": bad[:3], "alive": p.poll() is None}), flush=True)
p.stdin.close(); p.wait()
