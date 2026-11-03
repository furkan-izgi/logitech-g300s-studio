"""Print printable-ASCII context around every needle hit in a binary."""
import sys, re

path, needle = sys.argv[1], sys.argv[2].encode()
win = int(sys.argv[3]) if len(sys.argv) > 3 else 400
data = open(path, "rb").read()
printable = re.compile(rb"[ -~\t]{6,}")
seen = set()
for m in re.finditer(re.escape(needle), data):
    lo = max(0, m.start() - win)
    hi = min(len(data), m.end() + win)
    for s in printable.findall(data[lo:hi]):
        t = s.decode("ascii", "replace")
        if t not in seen:
            seen.add(t)
            print(f"{m.start():#010x}  {t}")
