"""Read-only probe of the G300s vendor collection (usage page 0xFF80)."""
import hid, time

VENDOR = None
for d in hid.enumerate(0x046D, 0xC246):
    if d["usage_page"] == 0xFF80:
        VENDOR = d

dev = hid.device()
dev.open_path(VENDOR["path"])
print("opened vendor collection")

SIZES = {0xF0: 3, 0xF1: 1, 0xF2: 1, 0xF3: 34, 0xF4: 34, 0xF5: 34}

for rid, size in SIZES.items():
    try:
        data = dev.get_feature_report(rid, size + 1)
        print(f"  feature 0x{rid:02x} ({size}B) -> {bytes(data).hex(' ')}")
    except Exception as e:
        print(f"  feature 0x{rid:02x} failed: {e}")

print("\nreading input reports for 2s (click/move the mouse now)...")
t0 = time.time()
while time.time() - t0 < 2.0:
    d = dev.read(64, 100)
    if d:
        print("  in:", bytes(d).hex(" "))
dev.close()
