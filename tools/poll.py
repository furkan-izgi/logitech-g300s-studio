import hid, time, threading
dev = None
for d in hid.enumerate(0x046D, 0xC246):
    if d["usage_page"] == 0xFF80:
        dev = hid.device(); dev.open_path(d["path"]); break

stop = False
def reader():
    while not stop:
        r = dev.read(64, 100)
        if r:
            print(f"   [{time.time()-t0:5.2f}] IN {bytes(r).hex(' ')}")

t0 = time.time()
th = threading.Thread(target=reader, daemon=True); th.start()
for i in range(12):
    vals = []
    for rid in (0xF0, 0xF1, 0xF2):
        vals.append(f"{rid:02x}={bytes(dev.get_feature_report(rid, 8)).hex()}")
    print(f"[{time.time()-t0:5.2f}] " + "  ".join(vals))
    time.sleep(0.4)
stop = True
time.sleep(0.3)
dev.close()
print("done")
