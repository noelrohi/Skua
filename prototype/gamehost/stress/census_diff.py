# PROTOTYPE (#13): diff CENSUS lines between two checkpoints. usage: census_diff.py console.txt t1 t2 [n]
import re, sys
f, t1, t2 = sys.argv[1], sys.argv[2], sys.argv[3]; n = int(sys.argv[4]) if len(sys.argv) > 4 else 25
d = {}
for line in open(f, errors='replace'):
    m = re.search(r'CENSUS \S+ t=([\d.]+)min (\d+) (\d+) (.*)$', line)
    if m: d.setdefault(m.group(1), {})[m.group(4).strip()] = (int(m.group(2)), int(m.group(3)))
a, b = d[t1], d[t2]
rows = [(k, a.get(k, (0, 0)), b.get(k, (0, 0))) for k in set(a) | set(b)]
rows.sort(key=lambda r: -(r[2][0] - r[1][0]))
print(f"{'type/class':70s} {'t'+t1:>9s} {'t'+t2:>9s} {'delta':>8s} {'dMB':>7s}")
for k, x, y in rows[:n]:
    print(f"{k[:70]:70s} {x[0]:9d} {y[0]:9d} {y[0]-x[0]:8d} {(y[1]-x[1])/1e6:7.2f}")
