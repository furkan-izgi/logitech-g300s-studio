"""Hypothesis: only the ACTIVE profile is writable; switch first, then write."""
import hid, time
for d in hid.enumerate(0x046D, 0xC246):
    if d["usage_page"] == 0xFF80:
        dev = hid.device(); dev.open_path(d["path"]); break

def get(rid, n):
    return bytes(dev.get_feature_report(rid, n + 1)).ljust(n, b"\0")

def log(m):
    print(f"[{time.strftime('%H:%M:%S')}] {m}", flush=True)

def active():
    b = get(0xF0, 4)
    return (b[1] >> 4) & 0xF

def w(rid, idx, val, label):
    base = bytearray(get(rid, 35))
    buf = bytearray(base); buf[idx] = val
    dev.send_feature_report(bytes(buf)); time.sleep(0.3)
    got = get(rid, 35)
    ok = got[idx] == val
    log(f"{label:34s} rid={rid:#x} [{base[idx]:#04x}->{val:#04x}] stuck={ok} now={got[idx]:#04x}")
    return ok

log(f"active={active()}")
log("switch to profile 1 (F0), then write F4")
dev.send_feature_report(bytes([0xF0, 0x80 | (1 << 4), 0, 0])); time.sleep(0.2)
log(f"  active now={active()}")
w(0xF4, 2, 2, "  F4 rate->250Hz while active")
time.sleep(0.5)
w(0xF4, 2, 0, "  F4 rate->1000Hz while active")

log("switch back to profile 0, write F3 (control)")
dev.send_feature_report(bytes([0xF0, 0x80 | (0 << 4), 0, 0])); time.sleep(0.2)
log(f"  active now={active()}")
w(0xF3, 2, 2, "  F3 rate->250Hz while active")
time.sleep(0.5)
w(0xF3, 2, 3, "  F3 rate->500Hz while active")

log("back-to-back same report (no waits)")
w(0xF3, 2, 0, "  F3 rate->1000Hz")
w(0xF3, 2, 3, "  F3 rate->500Hz immediate")

log("write F4 again while profile 0 is active (should fail)")
w(0xF4, 2, 1, "  F4 rate->125Hz inactive")
dev.close()
log("done")
