"""Verify the live LED register (0xF1) still accepts writes after replug."""
import hid, time
for d in hid.enumerate(0x046D, 0xC246):
    if d["usage_page"] == 0xFF80:
        dev = hid.device(); dev.open_path(d["path"]); break

def get():
    return dev.get_feature_report(0xF1, 2)[1]

print("F1 before:", hex(get()))
for name, v in [("red", 0x01), ("green", 0x02), ("blue", 0x04), ("cyan", 0x06),
                ("white", 0x07), ("off", 0x00)]:
    dev.send_feature_report(bytes([0xF1, v]))
    time.sleep(0.08)
    got = get()
    print(f"  {name:6s} write={v:#04x} read={got:#04x} ok={got == v}")
dev.send_feature_report(bytes([0xF1, 0x06]))
print("F1 left at cyan:", hex(get()))
dev.close()
