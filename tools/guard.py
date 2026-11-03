"""Characterise the write guard + whether F0 commands are instant."""
import hid, time
for d in hid.enumerate(0x046D, 0xC246):
    if d["usage_page"] == 0xFF80:
        dev = hid.device(); dev.open_path(d["path"]); break

def get(rid, n):
    return bytes(dev.get_feature_report(rid, n + 1)).ljust(n, b"\0")

def log(m):
    print(f"[{time.strftime('%H:%M:%S')}] {m}", flush=True)

def w(rid, idx, val, label):
    base = bytearray(get(rid, 35))
    buf = bytearray(base); buf[idx] = val
    dev.send_feature_report(bytes(buf)); time.sleep(0.35)
    got = get(rid, 35)
    ok = got[idx] == val
    log(f"{label:32s} rid={rid:#x} [{base[idx]:#04x}->{val:#04x}] stuck={ok} now={got[idx]:#04x}")
    return ok

log("=== E: F1 live LED register?  (F3[1] should mirror it)")
for v in (0x01, 0x02, 0x04, 0x00, 0x06):
    dev.send_feature_report(bytes([0xF1, v])); time.sleep(0.15)
    log(f"  set F1={v:#04x} -> F1={get(0xF1, 2)[1]:#04x}  F3[1]={get(0xF3, 35)[1]:#04x}")

log("=== C: F0 commands instant?")
for p in (1, 2, 0):
    dev.send_feature_report(bytes([0xF0, 0x80 | (p << 4), 0, 0])); time.sleep(0.2)
    a = get(0xF0, 4)
    log(f"  set profile {p} -> readback profile={(a[1]>>4)&0xF} dpi={(a[1]>>1)&7}")
for lv in (0, 2, 3):
    dev.send_feature_report(bytes([0xF0, 0x40 | (lv << 1), 0, 0])); time.sleep(0.2)
    a = get(0xF0, 4)
    log(f"  set dpi {lv} -> readback profile={(a[1]>>4)&0xF} dpi={(a[1]>>1)&7}")

log("=== A: F4 write then F3 write immediately")
time.sleep(50)
w(0xF4, 2, 1, "A1 F4 rate->1")
w(0xF3, 2, 0, "A2 F3 rate->0 right after")

log("=== B: back-to-back same report")
time.sleep(50)
w(0xF3, 2, 1, "B1 F3 rate->1")
w(0xF3, 2, 3, "B2 F3 rate->3 right away")

log("=== D: gap sweep on F3")
for gap in (2, 5, 10):
    time.sleep(50)
    w(0xF3, 2, 1, f"D prime (gap {gap})")
    time.sleep(gap)
    w(0xF3, 2, 3, f"D after {gap}s")

# leave profile 0 poll rate at stock 500Hz
time.sleep(50)
w(0xF3, 2, 3, "final restore 500Hz")
dev.close()
log("done")
