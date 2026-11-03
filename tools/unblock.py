"""Why did writes stop sticking? Correlate with input notifications."""
import hid, time, threading
for d in hid.enumerate(0x046D, 0xC246):
    if d["usage_page"] == 0xFF80:
        dev = hid.device(); dev.open_path(d["path"]); break

def get(rid, n):
    return bytes(dev.get_feature_report(rid, n + 1)).ljust(n, b"\0")

def log(m):
    print(f"[{time.strftime('%H:%M:%S')}] {m}", flush=True)

evt = []
run = True
def pump():
    while run:
        r = dev.read(64, 30)
        if r:
            evt.append((time.time(), bytes(r).hex(" ")))
threading.Thread(target=pump, daemon=True).start()

def trywrite(rid, idx, val, label):
    base = bytearray(get(rid, 35))
    buf = bytearray(base); buf[idx] = val
    n0 = len(evt)
    dev.send_feature_report(bytes(buf)); time.sleep(0.35)
    got = get(rid, 35)
    ok = got[idx] == val
    log(f"{label:30s} [{base[idx]:#04x}->{val:#04x}] stuck={ok} "
        f"notifs_before={n0} after={len(evt)}")
    for t, h in evt[-3:]:
        log(f"      notif {h}")
    return ok

log(f"F1(live)={get(0xF1,2)[1]:#04x}  F3[1]={get(0xF3,35)[1]:#04x}")
trywrite(0xF3, 2, 1, "try1 plain")
time.sleep(1.5)
trywrite(0xF3, 2, 3, "try2 plain")
time.sleep(1.5)

log("set F1 = stored LED")
dev.send_feature_report(bytes([0xF1, get(0xF3, 35)[1]])); time.sleep(0.3)
trywrite(0xF3, 2, 1, "try3 after F1 write")

log("F0 switch to profile 0 (already active)")
dev.send_feature_report(bytes([0xF0, 0x80, 0, 0])); time.sleep(0.3)
trywrite(0xF3, 2, 3, "try4 after F0 write")

# leave at stock 500 Hz if anything lands
time.sleep(1.0)
trywrite(0xF3, 2, 3, "final 500Hz")
run = False
time.sleep(0.1)
dev.close()
log("done")
