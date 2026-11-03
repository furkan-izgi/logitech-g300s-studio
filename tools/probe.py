"""Enumerate Logitech HID interfaces + dump raw report descriptors."""
import hid

def parse_items(d):
    out, i = [], 0
    while i < len(d):
        b = d[i]
        if b == 0xFE:  # long item
            size, tag = d[i+1], d[i+2]
            out.append(f"LONG(tag=0x{tag:02x},{d[i+3:i+3+size].hex()})")
            i += 3 + size
            continue
        size = (0, 1, 2, 4)[b & 3]
        typ = (b >> 2) & 3
        tag = (b >> 4) & 0xF
        data = d[i+1:i+1+size]
        val = int.from_bytes(data, "little") if data else 0
        names = {
            (0, 8): "Input", (0, 9): "Output", (0, 11): "Feature",
            (0, 10): "Collection", (0, 12): "EndCollection",
            (1, 0): "UsagePage", (1, 7): "ReportSize", (1, 8): "ReportID",
            (1, 9): "ReportCount", (1, 1): "LogicalMin", (1, 2): "LogicalMax",
            (1, 3): "PhysMin", (1, 4): "PhysMax", (1, 5): "UnitExp", (1, 6): "Unit",
            (1, 10): "Push", (1, 11): "Pop",
            (2, 0): "Usage", (2, 1): "UsageMin", (2, 2): "UsageMax",
            (2, 3): "DesigIdx", (2, 4): "DesigMin", (2, 5): "DesigMax",
            (2, 6): "StringIdx", (2, 7): "StringMin", (2, 8): "StringMax", (2, 9): "Delim",
        }
        nm = names.get((typ, tag), f"[t{typ} tag{tag:x}]")
        out.append(f"{nm}({val})" if size else nm)
        i += 1 + size
    return " / ".join(out)


for d in hid.enumerate(0x046D, 0xC246):
    p = d["path"].decode() if isinstance(d["path"], bytes) else d["path"]
    print(f"\n=== {p}")
    print(f"    up={d['usage_page']:04x} usage={d['usage']:04x} iface={d['interface_number']} "
          f"prod={d.get('product_string')!r} rel=0x{d.get('release_number', 0):04x}")
    dev = hid.device()
    try:
        dev.open_path(d["path"])
    except Exception as e:
        print("    open failed:", e)
        continue
    try:
        desc = dev.get_report_descriptor()
        print(f"    descriptor ({len(desc)} B): {' '.join(f'{b:02x}' for b in desc)}")
        print("    " + parse_items(desc))
    except Exception as e:
        print("    descriptor failed:", e)
    dev.close()
