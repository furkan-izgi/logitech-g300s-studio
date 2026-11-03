"""Untested hypothesis: F0 flag bit0 = config/unlock mode. Also F2 and repeat writes."""
import hid, time
for d in hid.enumerate(0x046D, 0xC246):
    if d["usage_page"] == 0xFF80:
        dev = hid.device(); dev.open_path(d["path"]); break

def get(rid, n):
    return bytes(dev.get_feature_report(rid, n + 1)).ljust(n, b"\0")

def log(m):
    print(f"[{time.strftime('%H:%M:%S')}] {m}", flush=True)

def wr(rid, data):
    dev.send_feature_report(bytes([rid] + list(data))); time.sleep(0.15)

def try_profile(label, val):
    base = bytearray(get(0xF3, 35))
    buf = bytearray(base); buf[2] = val
    dev.send_feature_report(bytes(buf)); time.sleep(0.3)
    got = get(0xF3, 35)[2]
    log(f"{label:42s} rate [{base[2]:#04x}->{val:#04x}] stuck={got == val}")
    return got == val

log(f"F0={get(0xF0,4).hex(' ')}  F2={get(0xF2,2).hex(' ')}  F3rate={get(0xF3,35)[2]:#04x}")

log("--- F0 bit0 variants before write")
for flags, name in [(0x01, "F0 01 (bit0)"), (0x81, "F0 81 (bit0+profile0)"),
                    (0x47, "F0 47 (bit0+dpi3)"), (0x00, "F0 00 (clear)")]:
    wr(0xF0, [flags, 0, 0])
    try_profile(f"  after {name}", 1)

log("--- F2 write variants")
for v in (0x01, 0x80, 0x00):
    wr(0xF2, [v])
    try_profile(f"  after F2={v:#04x}", 3)

log("--- double write, identical payload")
base = bytearray(get(0xF3, 35))
for i in range(2):
    dev.send_feature_report(bytes(base)); time.sleep(0.2)
log(f"  noop x2 ok, rate now {get(0xF3,35)[2]:#04x}")

log("--- F4 (profile 1) while profile 0 active")
b4 = bytearray(get(0xF4, 35)); b4[2] = 1 if b4[2] != 1 else 3
dev.send_feature_report(bytes(b4)); time.sleep(0.3)
log(f"  F4 rate->{b4[2]:#04x} stuck={get(0xF4,35)[2] == b4[2]}")

dev.send_feature_report(bytes([0xF0, 0x80, 0, 0])); time.sleep(0.2)
wr(0xF0, [0x40 | (3 << 1), 0, 0])
log(f"final F0={get(0xF0,4).hex(' ')}")
dev.close()
