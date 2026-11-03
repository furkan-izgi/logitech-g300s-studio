"""Watch the device state while LGS runs; log any change to F0/F1/F3..F5."""
import hid, time, sys
for d in hid.enumerate(0x046D, 0xC246):
    if d["usage_page"] == 0xFF80:
        dev = hid.device(); dev.open_path(d["path"]); break

def get(rid, n):
    return bytes(dev.get_feature_report(rid, n + 1)).ljust(n, b"\0")

secs = float(sys.argv[1]) if len(sys.argv) > 1 else 60
last = None
end = time.time() + secs
print("watching...", flush=True)
while time.time() < end:
    snap = (get(0xF0, 4).hex(), get(0xF1, 2).hex(),
            get(0xF3, 35).hex(), get(0xF4, 35).hex(), get(0xF5, 35).hex())
    if snap != last:
        if last is not None:
            print(f"[{time.strftime('%H:%M:%S')}] CHANGE", flush=True)
            for i, name in enumerate(["F0", "F1", "F3", "F4", "F5"]):
                if snap[i] != last[i]:
                    print(f"   {name}: {last[i]} -> {snap[i]}", flush=True)
        else:
            print(f"[{time.strftime('%H:%M:%S')}] baseline {snap[0]} {snap[1]}", flush=True)
        last = snap
    time.sleep(1.5)
print("done", flush=True)
