"""Brute force the write path: report sizes, handles, active profile."""
import hid, time

def opencol(page=0xFF80):
    for d in hid.enumerate(0x046D, 0xC246):
        if d["usage_page"] == page:
            h = hid.device(); h.open_path(d["path"]); return h
    return None

def get(dev, rid, n):
    return bytes(dev.get_feature_report(rid, n + 1)).ljust(n, b"\0")

def log(m):
    print(f"[{time.strftime('%H:%M:%S')}] {m}", flush=True)

dev = opencol()
base = bytearray(get(dev, 0xF3, 35))

def attempt(label, payload, dev=dev):
    dev.send_feature_report(bytes(payload)); time.sleep(0.25)
    got = get(dev, 0xF3, 35)
    diff = [i for i in range(min(35, len(payload))) if got[i] != payload[i]]
    log(f"{label:34s} -> changed={got[2]:#04x} diff_idx={diff} got={got[:8].hex(' ')}")

log(f"base rate={base[2]:#04x} led={base[1]:#04x} id={base[0]:#04x}")
v = bytearray(base); v[2] = 0x01
attempt("35B incl id", v)
attempt("34B without id", v[1:])
attempt("36B with trailing 0", bytes(v) + b"\0")
attempt("35B via mouse collection MI_00", v, opencol(0x0001) or dev)

log("switch to profile 1, then write F4")
dev.send_feature_report(bytes([0xF0, 0x80 | 0x10, 0, 0])); time.sleep(0.3)
b4 = bytearray(get(dev, 0xF4, 35)); b4[2] = 0x01 if b4[2] != 1 else 0x03
attempt("F4 while profile1 active", b4)
dev.send_feature_report(bytes([0xF0, 0x80, 0, 0])); time.sleep(0.3)

log("write F3 with only the target byte differing from a fresh read")
r = bytearray(get(dev, 0xF3, 35)); r[2] = 0x03 if r[2] != 3 else 0x00
dev.send_feature_report(bytes(r)); time.sleep(0.4)
log(f"  now rate={get(dev,0xF3,35)[2]:#04x}")
dev.close()
