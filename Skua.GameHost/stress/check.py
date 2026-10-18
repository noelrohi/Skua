#!/usr/bin/env python3
"""Offline stress suite for skua-gamehost: runs each case against the release host over the Bridge frames
and judges it against stress/baseline.txt. Run through check.sh, which builds the host and the SWFs.

usage: check.py --host <skua-gamehost> --swfs <dir with Stress2.swf ...> --skua-swf <skua.swf>
                [--baseline baseline.txt] [--out <dir>] [--only case,case] [--record]

--record prints the measured values in baseline.txt's format instead of failing on the thresholds.
Memory is judged by `footprint` (phys_footprint), never RSS: RSS hides compressed pages and most GPU memory.
"""
import argparse, json, math, os, re, struct, subprocess, sys, threading, time
import xml.etree.ElementTree as ET

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
        self.trace_times = []
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
                    self.trace_times.append(time.time())
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

    def uncaught(self):
        """Uncaught AS3 errors: the host prefixes their flash-log copy with "[uncaught]" (src/backends.rs)."""
        return [t[:200] for t in self.traces if t.startswith("[uncaught]")]

    def uncaught_cancelled(self):
        """(movie file name, trace) for each uncaught AS3 error an uncaughtError listener cancelled."""
        cancelled = []
        for t in self.traces:
            if m := re.match(r"\[uncaught-cancelled\] movie=(\S*) ", t):
                cancelled.append((m.group(1).rsplit("/", 1)[-1], t))
        return cancelled

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

    # WeakDict: AQW's Game._colorCache. Dead keys go when a collection finishes: the count must fall back to a fresh
    # cycle's size (one trace's worth of dead keys plus the live ones, as the clip reports them), or never leave it
    # when the System.gc() before each trace collects, and stay under baseline + tolerance. Without the weak-key fix
    # it grows by one trace's worth every trace.
    def weakdict(self):
        h = self.host(self.swf("WeakDict"), "weakdict")
        time.sleep(self.base["weakdict_secs"])
        h.close()
        clip = next((m.groups() for t in h.traces if (m := re.match(r"weakdict every (\d+) live (\d+)", t))), None)
        rows = [m.groups() for t in h.traces if (m := re.match(r"weak (\d+) strong (\d+) kept (\w+) str (\S+)", t))]
        if not clip or len(rows) < 3:
            return self.check("weakdict", False, f"clip constants {clip}, only {len(rows)} traces")
        fresh = int(clip[0]) + int(clip[1])
        weak = [int(r[0]) for r in rows]
        drops = sum(b < a for a, b in zip(weak, weak[1:]))
        low = min((b for a, b in zip(weak, weak[1:]) if b < a), default=None)
        last = rows[-1]
        self.record("weakdict.max_keys", max(weak))
        limit = math.ceil(self.limit("weakdict.max_keys"))
        collected = (low is not None and low <= fresh) or max(weak) <= fresh
        self.check("weakdict", collected and max(weak) <= limit and last[1:] == ("50", "true", "1"),
                   f"weak keys {weak}: {drops} drops to as low as {low}, max {max(weak)} (fresh cycle {fresh}, limit {limit}); "
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

    # reads: getGameObject of AQW-shaped objects (#49). Each reply parses as JSON with every entry, and every
    # whole number is written as Flash writes it, "1500000000" and not "1500000000.0": Core's Newtonsoft models
    # reject the latter for an int, and the whole list then reads as empty. The players' weak-key Dictionary is
    # written as "Dictionary", as Flash writes it, so the Engine reads it by key. Each inventory entry that the
    # game enumerated and then gave a new key (#263) lists its 29 keys and the new one, each once: a Ruffle that
    # trusts a stale enumeration position lists one key twice and loses another (live: ItemID).
    INVENTORY_KEYS = set("sIcon bEquip EnhID sFile bUpg iCost iType tPurchase CharID sES sLink iDPS bStaff iHrs sElmt "
                         "sName sType dPurchase iRng iLvl bCoins iQty iStk bBank sDesc iRty bTemp ItemID CharItemID".split())

    def reads(self):
        h = self.host(self.swf("Reads"), "reads")
        h.wait_for(lambda: "getGameObjectKey" in h.callbacks, timeout=20)
        # Each path's entry count, or for the Dictionary the string Flash writes for it.
        expect = {("items",): 17, ("bankinfo.items",): 1000, ("uoTree",): "Dictionary", ("areaUsers",): 5,
                  ("uoTree", "maada"): 14, ("odd",): 8, ("myAvatar.items",): 16}
        problems, sizes = [], []
        for args, count in expect.items():
            path = args[0] + "".join(f"[{a}]" for a in args[1:])
            call = "getGameObject" if len(args) == 1 else "getGameObjectKey"
            reply = h.ask(b"C", f'<invoke name="{call}" returntype="xml"><arguments>'
                                f'{"".join(f"<string>{a}</string>" for a in args)}</arguments></invoke>'.encode(), timeout=20)
            try:
                text = ET.fromstring(reply.decode()).text or ""
                value = json.loads(text)
            except Exception as e:
                problems.append(f"{path}: unreadable reply {reply[:120]!r}: {e}")
                continue
            sizes.append(f"{path} {len(reply)} B")
            got = value if isinstance(count, str) else len(value)
            if got != count:
                problems.append(f"{path}: {got}, expected {count}")
            # Whole numbers below 2^63 (JSON only; strings are skipped).
            tokens = re.findall(r'"(?:[^"\\]|\\.)*"|(-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?)', text)
            bad = sorted({t for t in tokens if t and float(t).is_integer() and abs(float(t)) < 2 ** 63
                          and not re.fullmatch(r"-?\d+", t)})
            if bad:
                problems.append(f"{path}: whole numbers not written as integers: {bad[:4]}")
            if path == "myAvatar.items":
                broken = []
                for n, pairs in enumerate(json.loads(text, object_pairs_hook=lambda p: p)):
                    keys = [k for k, _ in pairs]
                    twice = sorted({k for k in keys if keys.count(k) > 1})
                    lost = sorted(self.INVENTORY_KEYS - set(keys))
                    if twice or lost or len(keys) != len(self.INVENTORY_KEYS) + 1:
                        broken.append(f"[{n}] {len(keys)} keys, twice {twice}, lost {lost}")
                if broken:
                    problems.append(f"{path}: {len(broken)} entries with keys listed twice or lost, first {broken[0]}")
        uncaught = h.uncaught()
        h.close()
        self.check("reads", not problems and not uncaught,
                   f"{'; '.join(sizes)}{'; ' + '; '.join(problems) if problems else ''}"
                   f"{'; uncaught: ' + uncaught[0] if uncaught else ''}")

    # mapswap: AQW's map change (World.cleanupMap: close, unloadAndStop(true), System.gc()), every 12 frames and every
    # fourth map after only 5, with every third map replaced while its load is in flight, every fifth unloaded after its
    # content is in but before its init and followed by a quiet spell with no map, and an image Loader of the parent's
    # unloaded with each change (MapSwap.as). Once a later map has loaded, no map's stage enterFrame listener or Timer
    # may fire, nor its weak sharedEvents listener once a map later still has; a map replaced while loading, or
    # unloaded before its init, never loads, and no map loads during a quiet spell. The parent's stage listener and
    # Timer keep running to the end. The resident movie libraries ("movies" in the Q stats) stay flat and under
    # baseline + tolerance, and in the second half of every quiet spell, once the change's collection has run, they
    # come down to exactly QUIET_MOVIES, MapSwap.swf alone.
    QUIET_MOVIES = 1

    def mapswap(self):
        h = self.host(self.swf("MapSwap"), "mapswap")
        t0, samples = time.time(), []
        while time.time() - t0 < self.base["mapswap_secs"] and h.p.poll() is None:
            time.sleep(0.25)
            samples.append((time.time(), h.stats().get("movies", -1)))
        bad, uncaught, alive = h.bad_logs(), h.uncaught(), h.p.poll() is None
        h.close()
        movies = [n for _, n in samples]
        loaded, replaced, before_init, zombies, parent, rate = [], set(), set(), [], [], None
        quiet, quiet_start, quiet_inits = [], None, []
        for t, at in zip(h.traces, h.trace_times):
            if m := re.match(r"parent traces every (\d+) frames at ([\d.]+) fps", t):
                rate = int(m.group(1)), float(m.group(2))
            elif m := re.match(r"map (\d+) loaded", t):
                loaded.append(int(m.group(1)))
                if quiet_start is not None:
                    quiet_inits.append(t)
            elif m := re.match(r"map (\d+) replaced while loading", t):
                replaced.add(int(m.group(1)))
            elif m := re.match(r"map (\d+) unloaded before init", t):
                before_init.add(int(m.group(1)))
                quiet_start = at
            elif t == "quiet spell over" and quiet_start is not None:
                late = [n for s_at, n in samples if (quiet_start + at) / 2 <= s_at <= at]
                quiet.append(min(late) if late else None)
                quiet_start = None
            elif m := re.match(r"map (\d+) (stage enterFrame|ping|timer)", t):
                later = [n for n in loaded if n > int(m.group(1))]
                if len(later) >= (2 if m.group(2) == "ping" else 1):
                    zombies.append(t)
            elif m := re.match(r"parent frames (\d+) ticks (\d+)", t):
                parent.append((int(m.group(1)), int(m.group(2))))
        maps = len(loaded)
        steady = movies[len(movies) // 4:]
        quiet_measured = [n for n in quiet if n is not None]
        if maps < 10 or len(steady) < 3 or not quiet_measured:
            return self.check("mapswap", False, f"{maps} maps loaded, {len(movies)} stats samples, quiet spells {quiet}; "
                              f"parent {len(parent)} traces, last {parent[-1] if parent else None}; "
                              f"last trace {h.traces[-1] if h.traces else None!r}; {bad}")
        half = len(steady) // 2
        grows = max(steady[half:]) > max(steady[:half])
        loaded_replaced = sorted((replaced | before_init) & set(loaded))
        quiet_ok = all(n == self.QUIET_MOVIES for n in quiet_measured)
        min_parent_traces = self.base["mapswap_secs"] * rate[1] / rate[0] / 2 if rate else math.inf
        parent_alive = len(parent) >= min_parent_traces and all(b[1] > a[1] for a, b in zip(parent, parent[1:]))
        self.record("mapswap.max_movies", max(steady))
        limit = math.ceil(self.limit("mapswap.max_movies"))
        self.check("mapswap", alive and not bad and not uncaught and not zombies and not grows and max(steady) <= limit
                   and replaced and before_init and not loaded_replaced and not quiet_inits and quiet_ok and parent_alive,
                   f"{maps} maps, {len(replaced)} replaced while loading, {len(before_init)} unloaded before init"
                   f"{', loaded anyway: ' + str(loaded_replaced) if loaded_replaced else ''}"
                   f"{', init in a quiet spell: ' + quiet_inits[0] if quiet_inits else ''}; "
                   f"resident in quiet spells {quiet} (exactly {self.QUIET_MOVIES}); "
                   f"resident movies {min(movies)} to {max(movies)} over {len(movies)} samples, steady max "
                   f"{max(steady)} (limit {limit}){'; growing' if grows else ''}; "
                   f"{len(zombies)} traces from unloaded maps{', first: ' + zombies[0] if zombies else ''}; "
                   f"parent {len(parent)} traces (at least {min_parent_traces:.0f}), last {parent[-1] if parent else None}"
                   f"{'; ' + str(bad) if bad else ''}{'; uncaught: ' + uncaught[0] if uncaught else ''}")

    # smoke: skua.swf loads the game to the login screen; its callbacks register; a 958x550 screenshot; no uncaught
    # AS3 error while it sits there (skua.swf's modules run every frame, before login too), and none from skua.swf's
    # own code even where the game's uncaughtError listener cancels it (AQW's Game.registerErrorSuppression).
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
        bad, uncaught, cancelled = h.bad_logs(), h.uncaught(), h.uncaught_cancelled()
        from_skua = [t[:200] for movie, t in cancelled if movie == "skua.swf"]
        h.close()
        got = set(h.callbacks)
        w, hh, frames = struct.unpack("<IIQ", shot[:16]) if shot and len(shot) >= 16 else (0, 0, 0)
        png = shot[16:] if shot else b""
        path = os.path.join(self.a.out, "smoke-login-screen.png")
        open(path, "wb").write(png)
        min_png = self.base["smoke.min_png_bytes"]
        self.check("smoke", got == expected and len(expected) == self.base["smoke.callbacks"] and (w, hh) == (958, 550)
                   and len(png) >= min_png and png[:8] == b"\x89PNG\r\n\x1a\n" and not bad and not uncaught and not from_skua,
                   f"'loaded' after {load_ms:.0f} ms; callbacks {len(got)}/{len(expected)} missing {sorted(expected - got)}; "
                   f"screenshot {w}x{hh} frame~{frames} {len(png)} B (min {min_png}) -> {path}{'; ' + str(bad) if bad else ''}"
                   f"{'; %d uncaught AS3 errors, first: %s' % (len(uncaught), uncaught[0]) if uncaught else ''}"
                   f"; {len(cancelled)} cancelled uncaught errors, {len(from_skua)} from skua.swf"
                   f"{', first: ' + from_skua[0] if from_skua else ''}")

    # cancelled: Cancelled.swf cancels every uncaught error, as AQW does; it and its child CancelledChild.swf each throw
    # one from an anonymous callback. Both, and nothing else, reach the flash log as [uncaught-cancelled], each with the
    # movie whose code threw it, which is how smoke tells skua.swf's errors from the game's.
    def cancelled(self):
        h = self.host(self.swf("Cancelled"), "cancelled")
        h.wait_for(lambda: "done" in h.traces, timeout=20)
        bad, uncaught, cancelled = h.bad_logs(), h.uncaught(), h.uncaught_cancelled()
        h.close()
        expected = [("Cancelled.swf", "thrown by the root"), ("CancelledChild.swf", "thrown by the child")]
        found = sorted((movie, msg) for movie, t in cancelled for _, msg in expected if msg in t)
        self.check("cancelled", sorted(expected) == found and len(cancelled) == 2 and not uncaught and not bad,
                   f"cancelled {[(movie, t.split('] ', 2)[-1][:80]) for movie, t in cancelled]}"
                   f"{'; uncaught: ' + uncaught[0] if uncaught else ''}{'; ' + str(bad) if bad else ''}")

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
                 "reads": self.reads, "mapswap": self.mapswap, "cancelled": self.cancelled, "smoke": self.smoke,
                 "lifecycle": self.lifecycle}
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
