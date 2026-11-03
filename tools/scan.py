"""Find which LGS binary knows about the G300s (VID/PID or product name)."""
import os, sys

root = sys.argv[1] if len(sys.argv) > 1 else r"vendor\LGS_app"
needles = {
    b"C246": "ascii C246", b"c246": "ascii c246",
    "C246".encode("utf-16-le"): "utf16 C246",
    "G300s".encode("utf-16-le"): "utf16 G300s",
    "G300s".encode(): "ascii G300s",
    "G300".encode("utf-16-le"): "utf16 G300",
    b"G300": "ascii G300",
}
hits = {}
for dirpath, _, files in os.walk(root):
    for f in files:
        p = os.path.join(dirpath, f)
        try:
            data = open(p, "rb").read()
        except OSError:
            continue
        found = []
        for n, label in needles.items():
            c = data.count(n)
            if c:
                found.append(f"{label}x{c}")
        if found:
            hits[p] = found
for p, f in sorted(hits.items(), key=lambda kv: os.path.getsize(kv[0]), reverse=True)[:40]:
    print(f"{os.path.getsize(p)//1024:7d} KB  {p.replace(root + os.sep, '')}  -> {', '.join(f)}")
print(f"total files with hits: {len(hits)}")
