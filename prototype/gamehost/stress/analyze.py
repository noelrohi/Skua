# PROTOTYPE (#13): summarize a gate run's MEM/SAMPLE lines. usage: analyze.py console.txt
import re, sys, json
mem, rss = [], []
for line in open(sys.argv[1], errors='replace'):
    m = re.search(r' MEM script t=([\d.]+)min fp=(-?\d+) gfx=(\d+) unmappedGfx=(\d+) malloc=(\d+) (\{.*\})', line)
    if m:
        j = json.loads(m.group(6))
        mem.append((float(m.group(1)), int(m.group(2)), int(m.group(3)), int(m.group(4)), int(m.group(5)), j))
    m = re.search(r' SAMPLE script t=([\d.]+)min rss=(\d+) MB .*?tickBusyMs=(\d+) .*?maxTickGapMs=(\d+) .*?calls=(\d+) .*?map=(\S+) kills=(\d+) deaths=(\d+)', line)
    if m:
        rss.append((float(m.group(1)), int(m.group(2)), int(m.group(3)), int(m.group(4)), int(m.group(5)), m.group(6), int(m.group(7)), int(m.group(8))))
def slope(pts):
    n = len(pts)
    if n < 2: return float('nan')
    mx = sum(p[0] for p in pts) / n; my = sum(p[1] for p in pts) / n
    return sum((x - mx) * (y - my) for x, y in pts) / sum((x - mx) ** 2 for x, _ in pts)
T = max((r[0] for r in rss), default=0)
last = lambda arr, lo: [a for a in arr if a[0] >= lo]
print(f"samples: rss={len(rss)} mem={len(mem)} duration={T:.0f} min")
for name, pts in [('rss', [(r[0], r[1]) for r in rss]), ('footprint', [(m[0], m[1]) for m in mem]),
                  ('ioaccel', [(m[0], m[2]) for m in mem]), ('unmappedGfx', [(m[0], m[3]) for m in mem]), ('malloc', [(m[0], m[4]) for m in mem]),
                  ('movies', [(m[0], m[5].get('movies', 0)) for m in mem]), ('gcObjects/1000', [(m[0], m[5].get('gcObjects', 0) / 1000) for m in mem])]:
    if not pts: continue
    ys = [y for _, y in pts]; lh = [p for p in pts if p[0] >= T - 60]
    s = slope(lh)
    print(f"{name:15s} start={pts[0][1]:8.0f} min={min(ys):8.0f} max={max(ys):8.0f} end={pts[-1][1]:8.0f} last-hour slope={s:7.2f}/min ({s*60:7.0f}/h)")
if rss:
    busy = [(b[2] - a[2]) / ((b[0] - a[0]) * 600) for a, b in zip(rss, rss[1:]) if b[0] > a[0]]
    print(f"main thread busy: mean {sum(busy)/len(busy):.1f}% max {max(busy):.1f}%; max tick gap {max(r[3] for r in rss)} ms; calls {rss[-1][4]} ({rss[-1][4]/max(T,1)/60:.0f}/s); kills {rss[-1][6]} deaths {rss[-1][7]}; maps {sorted(set(r[5] for r in rss))}")
