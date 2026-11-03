"""Hypothesis: profile writes are dropped while the mouse is moving / in use.

Waits for a still cursor, then writes. Also repairs profile 0 back to stock
(rate 500 Hz, LED G+B cyan) one write per idle window.
"""
import hid, time, ctypes
from ctypes import wintypes

for d in hid.enumerate(0x046D, 0xC246):
    if d["usage_page"] == 0xFF80:
        dev = hid.device(); dev.open_path(d["path"]); break

def get(rid, n):
    return bytes(dev.get_feature_report(rid, n + 1)).ljust(n, b"\0")

def cursor():
    p = wintypes.POINT()
    ctypes.windll.user32.GetCursorPos(ctypes.byref(p))
    return (p.x, p.y)

def log(m):
    print(f"[{time.strftime('%H:%M:%S')}] {m}", flush=True)

def wait_still(sec):
    last, t0 = cursor(), time.time()
    moved = 0
    while time.time() - t0 < sec:
        time.sleep(0.03)
        c = cursor()
        if c != last:
            last, t0, moved = c, time.time(), moved + 1
    return moved

def apply(rid, idx, val, label):
    base = bytearray(get(rid, 35))
    if base[idx] == val:
        log(f"{label}: already {val:#04x}")
        return True
    buf = bytearray(base); buf[idx] = val
    moved = wait_still(2.0)
    dev.send_feature_report(bytes(buf))
    time.sleep(0.3)
    got = get(rid, 35)
    ok = got[idx] == val
    log(f"{label:22s} [{base[idx]:#04x}->{val:#04x}] stuck={ok} "
        f"(settled {moved} moves before write)")
    return ok

targets = [(0xF3, 2, 3, "rate->500Hz"), (0xF3, 1, 0x06, "led->cyan")]
deadline = time.time() + 480
for rid, idx, val, label in targets:
    while time.time() < deadline:
        if apply(rid, idx, val, label):
            break
        time.sleep(1)
    else:
        log(f"{label}: gave up")
dev.close()
log("done")
