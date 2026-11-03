import hid
for d in hid.enumerate(0x046D, 0xC246):
    if d["usage_page"] == 0xFF80:
        dev = hid.device(); dev.open_path(d["path"]); break

for rid in (0xF0, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5):
    for ln in (35, 64):
        try:
            r = dev.get_feature_report(rid, ln)
            print(f"0x{rid:02x} req={ln:2d} got={len(r):2d}: {bytes(r).hex(' ')}")
        except Exception as e:
            print(f"0x{rid:02x} req={ln} ERR {e}")
dev.close()
