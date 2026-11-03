"""Is the block time-based, or re-armed by other SetFeature writes?

1. idle 240s with zero writes -> F3 write (expect OK if it is a timeout)
2. immediately F0 write (same profile) -> F3 write (expect blocked if F0 re-arms)
Also records cursor movement so we can rule out user activity.
"""
import hid, time, ctypes
from ctypes import wintypes

for d in hid.enumerate(0x046D, 0xC246):
    if d["usage_page"] == 0xFF80:
        dev = hid.device(); dev.open_path(d["path"]); break

def get(rid, n):
    return bytes(dev.get_feature_report(rid, n + 1)).ljust(n, b"\0")

def log(m):
    print(f"[{time.strftime('%H:%M:%S')}] {m}", flush=True)

def cursor():
    p = wintypes.POINT()
    ctypes.windll.user32.GetCursorPos(ctypes.byref(p))
    return (p.x, p.y)

def trywrite(rid, idx, val, label):
    base = bytearray(get(rid, 35))
    buf = bytearray(base); buf[idx] = val
    dev.send_feature_report(bytes(buf)); time.sleep(0.35)
    got = get(rid, 35)
    ok = got[idx] == val
    log(f"{label:32s} [{base[idx]:#04x}->{val:#04x}] stuck={ok}")
    return ok

log("idling 240s (no writes), watching cursor")
last, moves = cursor(), 0
for _ in range(240):
    time.sleep(1)
    c = cursor()
    if c != last:
        moves += 1
        last = c
log(f"cursor moved {moves} times during idle")

trywrite(0xF3, 2, 1, "after 240s idle")
log("now F0 write (same profile), then F3 again")
dev.send_feature_report(bytes([0xF0, 0x80, 0, 0])); time.sleep(0.2)
trywrite(0xF3, 2, 3, "right after F0 write")
time.sleep(2)
trywrite(0xF3, 2, 0, "2s after F0 write")
trywrite(0xF3, 2, 3, "restore 500Hz")
dev.close()
log("done")
