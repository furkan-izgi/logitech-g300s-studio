"""Dump current profiles and diff against backup.json."""
import hid, json

for d in hid.enumerate(0x046D, 0xC246):
    if d["usage_page"] == 0xFF80:
        dev = hid.device(); dev.open_path(d["path"]); break

def g(r, n):
    return bytes(dev.get_feature_report(r, n + 1)).ljust(n, b"\0")

cur = [g(r, 35) for r in (0xF3, 0xF4, 0xF5)]
bak = [bytes.fromhex(x) for x in json.load(open("backup.json"))["profiles"]]
for i, (c, b) in enumerate(zip(cur, bak)):
    same = "SAME" if c == b else "DIFF"
    print(f"--- profile {i}: {same}")
    for k in range(len(c)):
        if c[k] != b[k]:
            print(f"   byte[{k:2d}] now={c[k]:#04x} stock={b[k]:#04x}")
print()
print("F0", g(0xF0, 4).hex(" "), " F1", g(0xF1, 2).hex(" "), " F2", g(0xF2, 2).hex(" "))
print("heap[14]", g(0x14, 1).hex())
dev.close()
