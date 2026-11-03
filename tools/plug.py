"""After replug: does a profile write land, and how many in a row?"""
import hid, time
for d in hid.enumerate(0x046D, 0xC246):
    if d["usage_page"] == 0xFF80:
        dev = hid.device(); dev.open_path(d["path"]); break

def get(rid, n):
    return bytes(dev.get_feature_report(rid, n + 1)).ljust(n, b"\0")

def log(m):
    print(f"[{time.strftime('%H:%M:%S')}] {m}", flush=True)

def w(idx, val, label):
    base = bytearray(get(0xF3, 35))
    buf = bytearray(base); buf[idx] = val
    dev.send_feature_report(bytes(buf)); time.sleep(0.3)
    got = get(0xF3, 35)
    log(f"{label:20s} [{base[idx]:#04x}->{val:#04x}] stuck={got[idx] == val}")
    return got[idx] == val

a = get(0xF0, 4)
log(f"active profile={(a[1] >> 4) & 0xF} dpi={(a[1] >> 1) & 7}")
log(f"F3 led={get(0xF3,35)[1]:#04x} rate={get(0xF3,35)[2]:#04x}  F1={get(0xF1,2)[1]:#04x}")
w(2, 3, "rate->500Hz")
w(1, 0x06, "led->cyan")
w(2, 1, "rate->125Hz")
w(2, 3, "rate->500Hz")
dev.close()
