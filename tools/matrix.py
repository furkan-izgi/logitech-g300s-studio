import hid, time
for d in hid.enumerate(0x046D, 0xC246):
    if d["usage_page"] == 0xFF80:
        dev = hid.device(); dev.open_path(d["path"]); break

def get(rid, n):
    return bytes(dev.get_feature_report(rid, n + 1)).ljust(n, b"\0")

def put(buf):
    return dev.send_feature_report(bytes(buf))

def trial(rid, idx, val, label):
    base = bytearray(get(rid, 35))
    buf = bytearray(base); buf[idx] = val
    put(buf); time.sleep(0.25)
    got = get(rid, 35)
    ok = got == buf
    print(f"{label}: rid={rid:#x} byte[{idx}]: {base[idx]:#04x} -> {val:#04x}  stuck={ok}  now={got[idx]:#04x}")
    if ok:
        put(base); time.sleep(0.25)
        print(f"    reverted, match={get(rid,35) == base}")

print("--- profile 1 (inactive) led byte ---")
for v in (0x01, 0x02, 0x04, 0x07):
    trial(0xF4, 1, v, "p1.led")
print("--- profile 1 (inactive) rate byte ---")
for v in (1, 2, 3):
    trial(0xF4, 2, v, "p1.rate")
print("--- profile 0 (active) rate byte ---")
for v in (0, 1, 2):
    trial(0xF3, 2, v, "p0.rate")
print("--- profile 0 (active) dpi byte ---")
trial(0xF3, 3, 0x05, "p0.dpi0")
dev.close()
