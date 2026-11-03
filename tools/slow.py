import hid, time, threading
for d in hid.enumerate(0x046D, 0xC246):
    if d["usage_page"] == 0xFF80:
        dev = hid.device(); dev.open_path(d["path"]); break

def get(rid, n):
    return bytes(dev.get_feature_report(rid, n + 1)).ljust(n, b"\0")

running = True
def pump():
    while running:
        r = dev.read(64, 50)
        if r:
            print(f"    [{time.time()-t0:5.2f}] NOTIF {bytes(r).hex(' ')}")

t0 = time.time()
threading.Thread(target=pump, daemon=True).start()

base = bytearray(get(0xF3, 35))
print("base:", base.hex(" "))
buf = bytearray(base); buf[2] = 0x01  # poll rate -> 125Hz
print("writing rate=125Hz ...", dev.send_feature_report(bytes(buf)))
for delay in (0.1, 0.5, 1.0, 2.0, 3.0):
    time.sleep(delay - (time.time() - t0) + 0.1 if delay > 0.1 else 0.1)
    print(f"  t={time.time()-t0:4.1f}s read byte[2]={get(0xF3,35)[2]:#04x}")
running = False
time.sleep(0.2)
dev.close()
