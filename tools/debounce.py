"""Test whether profile writes are debounced: idle 45s, write, read back."""
import hid, time
for d in hid.enumerate(0x046D, 0xC246):
    if d["usage_page"] == 0xFF80:
        dev = hid.device(); dev.open_path(d["path"]); break

def get(rid, n):
    return bytes(dev.get_feature_report(rid, n + 1)).ljust(n, b"\0")

def log(msg):
    print(f"[{time.strftime('%H:%M:%S')}] {msg}", flush=True)

log(f"start, rate byte now = {get(0xF3,35)[2]:#04x}")
for wait, val in ((45, 1), (45, 3), (90, 0)):
    log(f"idle {wait}s ...")
    time.sleep(wait)
    base = bytearray(get(0xF3, 35))
    buf = bytearray(base); buf[2] = val
    log(f"write rate={val} (was {base[2]})")
    dev.send_feature_report(bytes(buf))
    time.sleep(0.4)
    got = get(0xF3, 35)
    log(f"   after 0.4s: {got[2]:#04x} stuck={got == buf}")
    if got != buf:
        buf2 = bytearray(base); buf2[2] = base[2]
dev.close()
log("done")
