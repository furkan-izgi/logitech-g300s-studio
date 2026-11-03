import hid, time
for d in hid.enumerate(0x046D, 0xC246):
    if d["usage_page"] == 0xFF80:
        dev = hid.device(); dev.open_path(d["path"]); break

def get(rid, n):
    return bytes(dev.get_feature_report(rid, n + 1)).ljust(n, b"\0")

def put(buf):
    return dev.send_feature_report(bytes(buf))

base = bytearray(get(0xF3, 35))
print("orig   :", base.hex(" "))

for val in (0x06, 0x07, 0x01, 0x02):
    buf = bytearray(base); buf[1] = val
    r = put(buf)
    time.sleep(0.3)
    got = get(0xF3, 35)
    print(f"write led={val:#04x} send_ret={r} -> read led={got[1]:#04x} match={got == buf}")
    time.sleep(0.2)

# restore original
buf = bytearray(base)
put(buf); time.sleep(0.3)
print("restored to", get(0xF3, 35).hex(" "))
dev.close()
