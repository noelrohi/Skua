# PROTOTYPE (#13): run the Game Host on a SWF; sample footprint categories and the host's 'M' memory stats.
# usage: fp.py <label> <secs> <every> <swf> [host flags...]   env HOST overrides the binary, GC=1 forces a full GC before each sample
import subprocess, threading, time, sys, os, re, struct, json
H = os.environ.get('HOST', '/Users/rohi/sandbox/skua-mem/prototype/gamehost/target/release/skua-gamehost')
label, secs, every, swf = sys.argv[1], float(sys.argv[2]), float(sys.argv[3]), sys.argv[4]
p = subprocess.Popen([H] + sys.argv[5:] + [swf], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
replies = {}; cv = threading.Condition(); last_trace = {'t': ''}
def reader():
    while True:
        h = p.stdout.read(4)
        if len(h) < 4: return
        b = p.stdout.read(struct.unpack('<I', h)[0])
        if b[:1] in (b'Q', b'P'):
            with cv: replies[struct.unpack('<I', b[1:5])[0]] = b[5:]; cv.notify_all()
        elif b[:1] == b'F': last_trace['t'] = b[1:80].decode(errors='replace')
        elif b[:1] == b'L' and any(k in b for k in (b'panic', b'lost', b'outstanding', b'Validation', b'OutOfMemory')):
            print('  HOST:', b[2:220].decode(errors='replace'), flush=True)
threading.Thread(target=reader, daemon=True).start()
nid = [0]
def ask(kind, extra=b''):
    nid[0] += 1; i = nid[0]
    body = kind + struct.pack('<I', i) + extra
    p.stdin.write(struct.pack('<I', len(body)) + body); p.stdin.flush()
    with cv:
        cv.wait_for(lambda: i in replies, timeout=60)
        return replies.pop(i, b'')
U = {'B': 1e-6, 'KB': 1e-3, 'MB': 1, 'GB': 1e3}
def mb(s):
    n, u = s.split(); return float(n) * U[u]
def footprint():
    o = subprocess.run(['footprint', str(p.pid)], capture_output=True, text=True).stdout
    tot = re.search(r'Footprint: ([\d.]+ \w+)', o)
    cats = {}
    for line in o.splitlines():
        m = re.match(r'\s*([\d.]+ \w+)\s+([\d.]+ \w+)\s+([\d.]+ \w+)\s+(\d+)\s+(.+)$', line)
        if m: cats[m.group(5).strip()] = (mb(m.group(1)), int(m.group(4)))
    g = cats.get('IOAccelerator (graphics)', (0, 0)); u = cats.get('Owned physical footprint (unmapped) (graphics)', (0, 0))
    ms = sum(cats.get(k, (0, 0))[0] for k in ('Malloc Small', 'Malloc Large', 'Malloc Tiny', 'Malloc Nano'))
    return f"fp={mb(tot.group(1)) if tot else -1:.0f} gfx={g[0]:.0f}/{g[1]}r unm={u[0]:.0f} malloc={ms:.0f}"
t0 = time.time()
while time.time() - t0 < secs and p.poll() is None:
    time.sleep(every)
    if os.environ.get('GC'): ask(b'G')
    m = json.loads(ask(b'M') or b'{}')
    keys = os.environ.get('KEYS', 'movies,characters,gcObjects,poolSizes,poolTex,poolMB,offPoolTex,offPoolMB,halTextures,halTexMB,halBuffers,halBufMB,renders').split(',')
    print(f"{label} t={time.time()-t0:4.0f}s {footprint()} " + ' '.join(f"{k}={m.get(k)}" for k in keys), flush=True)
if os.environ.get('TRACE'): print('last trace:', last_trace['t'])
p.stdin.close(); p.wait()
