#!/usr/bin/env python3
"""Offline stress suite for skua-gamehost: runs each case against the release host over the Bridge frames
and judges it against stress/baseline.txt. Run through check.sh, which builds the host and the SWFs.

usage: check.py --host <skua-gamehost> --swfs <dir with Stress2.swf ...> --skua-swf <skua.swf>
                [--baseline baseline.txt] [--out <dir>] [--only case,case] [--record]

--record prints the measured values in baseline.txt's format instead of failing on the thresholds.
Memory is judged by `footprint` (phys_footprint), never RSS: RSS hides compressed pages and most GPU memory.
"""
import argparse, math, os, re, struct, subprocess, sys, threading, time

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))


class Host:
    """One skua-gamehost process, spoken to over its stdin/stdout Bridge frames."""

    def __init__(self, exe, swf, out_dir, name, flags=(), env=None):
        self.stderr = open(os.path.join(out_dir, f"{name}.stderr.txt"), "wb")
        self.p = subprocess.Popen([exe, *flags, swf], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                  stderr=self.stderr, env={**os.environ, **(env or {})})
        self.cv = threading.Condition()
        self.replies, self.events, self.traces, self.logs, self.callbacks = {}, [], [], [], []
        self.on_event = None
        self.nid = 0
        self.wlock = threading.Lock()
        threading.Thread(target=self._read, daemon=True).start()

    def _read(self):
        out = self.p.stdout
        while True:
            head = out.read(4)
            if len(head) < 4:
                break
            body = out.read(struct.unpack("<I", head)[0])
            kind, payload = body[:1], body[1:]
            with self.cv:
                if kind in (b"R", b"I", b"P", b"Q"):
                    self.replies[struct.unpack("<I", payload[:4])[0]] = payload[4:]
                elif kind == b"E":
                    self.events.append(payload.decode("utf-8", "replace"))
                elif kind == b"F":
                    self.traces.append(payload.decode("utf-8", "replace"))
                elif kind == b"L":
                    self.logs.append(payload[1:].decode("utf-8", "replace"))
                elif kind == b"X":
                    self.callbacks.append(payload.decode())
                self.cv.notify_all()
            if kind == b"E" and self.on_event:
                self.on_event(payload.decode("utf-8", "replace"))
        with self.cv:
            self.cv.notify_all()

    def send(self, kind, extra=b""):
        with self.wlock:
            self.nid += 1
            i = self.nid
            body = kind + struct.pack("<I", i) + extra
            self.p.stdin.write(struct.pack("<I", len(body)) + body)
            self.p.stdin.flush()
        return i

    def ask(self, kind, extra=b"", timeout=60):
        i = self.send(kind, extra)
        with self.cv:
            self.cv.wait_for(lambda: i in self.replies or self.p.poll() is not None, timeout=timeout)
            return self.replies.pop(i, None)

    def stats(self):
        import json
        q = self.ask(b"Q")
        return json.loads(q) if q else {}

    def wait_for(self, pred, timeout):
        with self.cv:
            return self.cv.wait_for(lambda: pred() or self.p.poll() is not None, timeout=timeout) and pred()

    def footprint_mb(self):
        o = subprocess.run(["footprint", str(self.p.pid)], capture_output=True, text=True).stdout
        m = re.search(r"Footprint: ([\d.]+) (\w+)", o)
        return float(m.group(1)) * {"B": 1e-6, "KB": 1e-3, "MB": 1, "GB": 1e3}[m.group(2)] if m else float("nan")

    def bad_logs(self):
        return [l[:200] for l in self.logs if any(k in l for k in ("panic", "lost", "Validation", "OutOfMemory"))]

    def close(self, timeout=5):
        try:
            self.p.stdin.close()
        except OSError:
            pass
        try:
            self.p.wait(timeout)
        except subprocess.TimeoutExpired:
            self.p.kill()
            self.p.wait()
        self.stderr.close()


def slope_per_min(samples):
    """Least-squares slope of (seconds, value) samples, per minute."""
    n = len(samples)
    mx, my = sum(t for t, _ in samples) / n, sum(v for _, v in samples) / n
    den = sum((t - mx) ** 2 for t, _ in samples)
    return 60 * sum((t - mx) * (v - my) for t, v in samples) / den if den else 0.0


class Suite:
    def __init__(self, args, baseline):
        self.a, self.base = args, baseline
        self.results, self.measured = [], {}

    def swf(self, name):
        return os.path.join(self.a.swfs, f"{name}.swf")

    def host(self, swf, name, flags=(), env=None):
        return Host(self.a.host, swf, self.a.out, name, flags, env)

    def record(self, key, value):
        self.measured[key] = value

    def check(self, case, ok, detail):
        ok = ok or self.a.record
        self.results.append((case, ok, detail))
        print(f"{'PASS' if ok else 'FAIL'} {case:10s} {detail}", flush=True)

    def limit(self, key):
        return self.base[key] * (1 + self.base["tolerance_pct"] / 100)

    # Stress2 (offscreen BitmapData.draw every frame) and Stress3 (one heavy stage): footprint flat and
    # under baseline + tolerance.
    def footprint_case(self, case):
        h = self.host(self.swf(case.capitalize()), case)
        secs, warm, every = self.base["footprint_secs"], self.base["footprint_warm_secs"], 5
        t0, samples = time.time(), []
        while time.time() - t0 < secs and h.p.poll() is None:
            time.sleep(every)
            samples.append((time.time() - t0, h.footprint_mb()))
        bad, alive = h.bad_logs(), h.p.poll() is None
        h.close()
        steady = [s for s in samples if s[0] >= warm]
        if len(steady) < 3:
            return self.check(case, False, f"host exited after {len(samples)} samples; {bad}")
        peak, slope = max(v for _, v in steady), slope_per_min(steady)
        self.record(f"{case}.footprint_mb", round(peak))
        limit = self.limit(f"{case}.footprint_mb")
        max_slope = self.base[f"{case}.footprint_mb"] * self.base["flat_pct_per_min"] / 100
        self.check(case, alive and not bad and peak <= limit and slope <= max_slope,
                   f"footprint peak {peak:.0f} MB (limit {limit:.0f}), slope {slope:+.0f} MB/min "
                   f"(limit {max_slope:+.0f}); samples {[round(v) for _, v in steady]}{'; ' + str(bad) if bad else ''}")

    # Stress4 (6,000 masked, filtered, layered children) on the render thread: average submit_frame time
    # under baseline + tolerance.
    def stress4(self):
        h = self.host(self.swf("Stress4"), "stress4")
        time.sleep(self.base["stress4_warm_secs"])
        h.stats()  # resets the render counters
        time.sleep(self.base["stress4_secs"])
        q = h.stats()
        bad, alive = h.bad_logs(), h.p.poll() is None
        h.close()
        n, avg = q.get("submitN", 0), q.get("submitMsSum", 0) / max(1, q.get("submitN", 0))
        self.record("stress4.render_ms", round(avg))
        limit = self.limit("stress4.render_ms")
        self.check("stress4", alive and not bad and q.get("threaded") and n >= 3 and avg <= limit,
                   f"render avg {avg:.0f} ms over {n} frames (limit {limit:.0f}), threaded={q.get('threaded')}"
                   f"{'; ' + str(bad) if bad else ''}")

    # Sounds: AQW's SoundFX keeps every channel until SOUND_COMPLETE; the count must not grow (the last half
    # never exceeds the first) and stays under baseline + tolerance.
    def sounds(self):
        h = self.host(self.swf("Sounds"), "sounds")
        time.sleep(self.base["short_secs"])
        h.close()
        counts = [int(m.group(1)) for t in h.traces if (m := re.match(r"active (\d+)", t))]
        steady = counts[len(counts) // 4:]
        if len(steady) < 3:
            return self.check("sounds", False, f"only {len(counts)} 'active' traces")
        half = len(steady) // 2
        grows = max(steady[half:]) > max(steady[:half])
        self.record("sounds.max_active", max(steady))
        limit = math.ceil(self.limit("sounds.max_active"))
        self.check("sounds", not grows and max(steady) <= limit,
                   f"active channels {steady} (limit {limit}){'; growing' if grows else ''}")

    # WeakDict: AQW's Game._colorCache. Ruffle's System.gc() doesn't force a collection, so dead keys go at the
    # end of a GC cycle: the count must fall back to a fresh cycle's size (<= 62: one trace of keys plus the
    # two live ones) and stay under baseline + tolerance. Without the weak-key fix it grows by 60 every trace.
    def weakdict(self):
        h = self.host(self.swf("WeakDict"), "weakdict")
        time.sleep(self.base["weakdict_secs"])
        h.close()
        rows = [m.groups() for t in h.traces if (m := re.match(r"weak (\d+) strong (\d+) kept (\w+) str (\S+)", t))]
        if len(rows) < 3:
            return self.check("weakdict", False, f"only {len(rows)} traces")
        weak = [int(r[0]) for r in rows]
        drops = sum(b < a for a, b in zip(weak, weak[1:]))
        low = min((b for a, b in zip(weak, weak[1:]) if b < a), default=None)
        last = rows[-1]
        self.record("weakdict.max_keys", max(weak))
        limit = math.ceil(self.limit("weakdict.max_keys"))
        self.check("weakdict", low is not None and low <= 62 and max(weak) <= limit and last[1:] == ("50", "true", "1"),
                   f"weak keys {weak}: {drops} drops to as low as {low}, max {max(weak)} (limit {limit}); "
                   f"last strong={last[1]} kept={last[2]} str={last[3]}")

    # Events: 30,000 numbered ExternalInterface calls in three interleaved streams arrive complete, in order.
    def events(self):
        total = 30000
        h = self.host(self.swf("Events"), "events")
        t0 = time.time()
        h.wait_for(lambda: len(h.events) >= total, timeout=60)
        secs = time.time() - t0
        time.sleep(1)
        h.close()
        names = ("pext", "packetFromServer", "packet")
        seqs, wrong = [], 0
        for e in h.events:
            m = re.search(r'<invoke name="(\w+)".*?&quot;seq&quot;:(\d+)', e)
            if not m:
                wrong += 1
                continue
            seq = int(m.group(2))
            seqs.append(seq)
            wrong += m.group(1) != names[seq % 3]
        in_order = seqs == list(range(total))
        self.check("events", in_order and not wrong,
                   f"{len(h.events)} events in {secs:.1f} s, in order: {in_order}, wrong names: {wrong}")

    # smoke: skua.swf loads the game to the login screen; 73 callbacks register; a 958x550 screenshot.
    def smoke(self):
        if not self.a.skua_swf or not os.path.exists(self.a.skua_swf):
            return self.check("smoke", False, f"no skua.swf at {self.a.skua_swf!r}")
        src = open(os.path.join(REPO, "Skua.AS3/skua/src/skua/Externalizer.as")).read()
        expected = set(re.findall(r'addCallback\(\s*"([^"]+)"', src))
        h = self.host(self.a.skua_swf, "smoke")
        loaded = threading.Event()

        def on_event(xml):
            m = re.match(r'<invoke name="([^"]+)"', xml)
            name = m.group(1) if m else None
            if name == "requestLoadGame":  # SkuaStartupHandler.LoadGame
                h.send(b"C", b'<invoke name="loadClient" returntype="xml"></invoke>')
            elif name == "loaded":
                loaded.set()

        h.on_event = on_event
        t0 = time.time()
        if not loaded.wait(60):
            h.close()
            return self.check("smoke", False, f"'loaded' never arrived; {len(h.callbacks)} callbacks; {h.bad_logs()}")
        load_ms = (time.time() - t0) * 1000
        h.wait_for(lambda: set(h.callbacks) >= expected, timeout=10)
        time.sleep(self.base["smoke_settle_secs"])
        shot = h.ask(b"S", struct.pack("<I", 0))
        bad = h.bad_logs()
        h.close()
        got = set(h.callbacks)
        w, hh, frames = struct.unpack("<IIQ", shot[:16]) if shot and len(shot) >= 16 else (0, 0, 0)
        png = shot[16:] if shot else b""
        path = os.path.join(self.a.out, "smoke-login-screen.png")
        open(path, "wb").write(png)
        min_png = self.base["smoke.min_png_bytes"]
        self.check("smoke", got == expected and len(expected) == self.base["smoke.callbacks"] and (w, hh) == (958, 550)
                   and len(png) >= min_png and png[:8] == b"\x89PNG\r\n\x1a\n" and not bad,
                   f"'loaded' after {load_ms:.0f} ms; callbacks {len(got)}/{len(expected)} missing {sorted(expected - got)}; "
                   f"screenshot {w}x{hh} frame~{frames} {len(png)} B (min {min_png}) -> {path}{'; ' + str(bad) if bad else ''}")

    # lifecycle: closing stdin ends the host within 1 s; a panic inside Ruffle aborts it.
    def lifecycle(self):
        h = self.host(self.swf("Events"), "lifecycle-eof")
        h.wait_for(lambda: len(h.events) > 0, timeout=10)
        t0 = time.time()
        h.p.stdin.close()
        try:
            code = h.p.wait(5)
        except subprocess.TimeoutExpired:
            code = None
        exit_ms = (time.time() - t0) * 1000
        h.close()
        limit = self.base["lifecycle.eof_exit_ms"]
        self.check("eof", code == 0 and exit_ms <= limit, f"exit {code} {exit_ms:.0f} ms after stdin EOF (limit {limit})")

        h = self.host(self.swf("Events"), "lifecycle-panic", env={"SKUA_GAMEHOST_PANIC_ON": "pext"})
        try:
            code = h.p.wait(20)
        except subprocess.TimeoutExpired:
            code = None
        time.sleep(0.2)
        panic = [l for l in h.logs if "[panic]" in l and "injected panic" in l]
        h.close()
        self.check("panic", code == -6 and bool(panic),
                   f"exit {code} (SIGABRT = -6); panic 'L' frame: {panic[0].splitlines()[0] if panic else None}")

    def run(self, only):
        cases = {"stress2": lambda: self.footprint_case("stress2"), "stress3": lambda: self.footprint_case("stress3"),
                 "stress4": self.stress4, "sounds": self.sounds, "weakdict": self.weakdict, "events": self.events,
                 "smoke": self.smoke, "lifecycle": self.lifecycle}
        for name, case in cases.items():
            if not only or name in only:
                case()


def read_baseline(path):
    base = {}
    for line in open(path):
        line = line.split("#", 1)[0].strip()
        if line:
            k, v = line.split()
            base[k] = float(v) if "." in v else int(v)
    return base


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", required=True)
    ap.add_argument("--swfs", required=True)
    ap.add_argument("--skua-swf")
    ap.add_argument("--baseline", default=os.path.join(HERE, "baseline.txt"))
    ap.add_argument("--out", default=os.path.join(HERE, "out"))
    ap.add_argument("--only", default="")
    ap.add_argument("--record", action="store_true")
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    suite = Suite(a, read_baseline(a.baseline))
    suite.run([c for c in a.only.split(",") if c])
    if a.record:
        print("\n# measured (baseline.txt format)")
        for k, v in suite.measured.items():
            print(f"{k} {v}")
        return 0
    failed = [c for c, ok, _ in suite.results if not ok]
    print(f"\n{len(suite.results) - len(failed)}/{len(suite.results)} passed" + (f"; failed: {', '.join(failed)}" if failed else ""))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
