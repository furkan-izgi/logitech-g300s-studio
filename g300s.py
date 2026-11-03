#!/usr/bin/env python3
"""Logitech G300s (046d:c246) onboard-config tool.

Speaks the vendor HID collection (usage page 0xFF80) using feature reports:
  0xF0  4B   active profile / dpi level  (+ set commands)
  0xF1  2B   live LED colour (RAM only, instant, not throttled)
  0xF2  2B   status (bit6 toggles)
  0xF3..0xF5 35B  onboard profile 0..2
  0x80  input notification

Profile layout (35 bytes):
  [0]    report id (F3/F4/F5)
  [1]    LED bits: 1=R 2=G 4=B (1 bit per channel), bits 3..7 reserved
  [2]    poll rate: 0=1000 1=125 2=250 3=500 Hz
  [3..6] DPI level 0..3: bit7 = default, bits0..6 = dpi/250
  [7]    unknown (0x40 / 0x02 in stock profiles) - preserved
  [8..]  9 buttons x {code, modifier, key}

Button code: 1..9 = mouse button, 0x0A..0x0F = special, 0 = keystroke
(modifier bitmask + single HID keyboard usage in key).
"""
import hid, json, sys, time

VID, PID, USAGE_PAGE = 0x046D, 0xC246, 0xFF80
PROFILE_IDS = (0xF3, 0xF4, 0xF5)
RATES = {0: 1000, 1: 125, 2: 250, 3: 500}
RATE_RAW = {v: k for k, v in RATES.items()}
SPECIAL = {0x0A: "dpi-up", 0x0B: "dpi-down", 0x0C: "dpi-cycle",
           0x0D: "profile-cycle", 0x0E: "dpi-alternate", 0x0F: "dpi-default"}
SPECIAL_RAW = {v: k for k, v in SPECIAL.items()}
LED_CYCLE = [(1, 0, 0), (1, 1, 0), (0, 1, 0), (0, 1, 1),
             (0, 0, 1), (1, 0, 1), (1, 1, 1), (0, 0, 0)]
MODS = {"ctrl": 0x01, "shift": 0x02, "alt": 0x04, "win": 0x08,
        "rctrl": 0x10, "rshift": 0x20, "ralt": 0x40, "rwin": 0x80}
KEYS = {"enter": 0x28, "esc": 0x29, "backspace": 0x2A, "tab": 0x2B, "space": 0x2C,
        "caps": 0x39, "up": 0x52, "down": 0x51, "left": 0x50, "right": 0x4F,
        "home": 0x4A, "end": 0x4D, "pgup": 0x4B, "pgdn": 0x4E, "ins": 0x49,
        "del": 0x4C, "menu": 0x65, "prtsc": 0x46, "scroll": 0x47, "pause": 0x48}
KEYS.update({f"f{i}": 0x39 + i for i in range(1, 13)})  # f1=0x3A .. f12=0x45
KEYS.update({chr(c): c - 0x5D for c in range(ord("a"), ord("z") + 1)})  # a=0x04
KEYS.update({str(d): 0x1E + (d - 1) % 10 for d in range(1, 10)})  # 1=0x1E .. 9=0x26
KEYS["0"] = 0x27
KEY_NAMES = {v: k for k, v in KEYS.items()}


class G300S:
    def __init__(self):
        path = next((d["path"] for d in hid.enumerate(VID, PID)
                     if d["usage_page"] == USAGE_PAGE), None)
        if not path:
            raise SystemExit("G300s vendor collection not found (is the mouse plugged in?)")
        self.dev = hid.device()
        self.dev.open_path(path)

    def close(self):
        self.dev.close()

    # --- raw feature access (buffers include the report id) -----------------
    def get(self, rid, size):
        buf = bytes(self.dev.get_feature_report(rid, size + 1))
        assert buf and buf[0] == rid, f"bad report id {buf[:1].hex()} for {rid:#x}"
        assert len(buf) <= size, f"{rid:#x}: got {len(buf)} bytes, expected <= {size}"
        return buf.ljust(size, b"\0")

    def set(self, rid, payload):
        assert payload and payload[0] == rid
        self.dev.send_feature_report(bytes(payload))

    # --- active state -------------------------------------------------------
    def active(self):
        b = self.get(0xF0, 4)
        return {"profile": (b[1] >> 4) & 0xF, "dpi_level": (b[1] >> 1) & 0x7}

    def set_active_profile(self, n):
        assert 0 <= n <= 2
        self.set(0xF0, [0xF0, 0x80 | (n << 4), 0, 0])

    def set_dpi_level(self, n):
        assert 0 <= n <= 3
        self.set(0xF0, [0xF0, 0x40 | (n << 1), 0, 0])

    # --- live LED (report 0xF1) --------------------------------------------
    # 0xF1 is a RAM-only register: writes apply instantly, are not throttled by
    # the flash write guard, and do not touch the stored profile. Ideal for
    # effects. The stored value lives in profile byte [1].
    def get_led_live(self):
        v = self.get(0xF1, 2)[1]
        return (v & 1, (v >> 1) & 1, (v >> 2) & 1)

    def set_led_live(self, r, g, b):
        self.set(0xF1, [0xF1, (r & 1) | ((g & 1) << 1) | ((b & 1) << 2)])

    def ledfx(self, name, seconds=10, fps=20):
        keep = self.get_led_live()
        frame = {"rainbow": lambda i: LED_CYCLE[(i // 4) % 8],
                 "blink":   lambda i: LED_CYCLE[6] if (i // 8) % 2 else LED_CYCLE[7],
                 "strobe":  lambda i: LED_CYCLE[6] if (i // 2) % 2 else LED_CYCLE[7],
                 "police":  lambda i: LED_CYCLE[0] if (i // 6) % 2 else LED_CYCLE[4],
                 "pulse":   lambda i: LED_CYCLE[4] if (i // 20) % 2 else LED_CYCLE[7],
                 }[name]
        end, i = time.time() + seconds, 0
        try:
            while time.time() < end:
                self.set_led_live(*frame(i))
                i += 1
                time.sleep(1 / fps)
        finally:
            self.set_led_live(*keep)

    # --- profiles -----------------------------------------------------------
    def get_profile(self, n):
        return bytearray(self.get(PROFILE_IDS[n], 35))

    def set_profile(self, n, raw):
        assert len(raw) == 35 and raw[0] == PROFILE_IDS[n]
        for _ in range(3):
            self.set(PROFILE_IDS[n], raw)
            time.sleep(0.05)
            if bytes(self.get_profile(n)) == bytes(raw):
                return
        raise RuntimeError(f"profile {n} write did not stick")

    def snapshot(self):
        return {"active": self.active(),
                "profiles": [self.get_profile(i).hex() for i in range(3)]}

    def restore(self, snap):
        for i, h in enumerate(snap["profiles"]):
            self.set_profile(i, bytearray.fromhex(h))
        self.set_active_profile(snap["active"]["profile"])
        self.set_dpi_level(snap["active"]["dpi_level"])


# --- profile helpers --------------------------------------------------------
def led(raw):
    return (raw[1] & 1, (raw[1] >> 1) & 1, (raw[1] >> 2) & 1)


def set_led(raw, r, g, b):
    raw[1] = (raw[1] & ~0x07) | (r & 1) | ((g & 1) << 1) | ((b & 1) << 2)


def dpis(raw):
    return [(raw[3 + i] & 0x7F) * 250 for i in range(4)]


def default_dpi(raw):
    for i in range(4):
        if raw[3 + i] & 0x80:
            return i
    return 0


def set_dpi(raw, idx, value):
    assert value % 250 == 0 and 250 <= value <= 2500, "dpi must be 250..2500 step 250"
    raw[3 + idx] = (value // 250) & 0x7F


def set_default_dpi(raw, idx):
    for i in range(4):
        raw[3 + i] &= 0x7F
    raw[3 + idx] |= 0x80


def buttons(raw):
    out = []
    for i in range(9):
        code, mod, key = raw[8 + 3 * i], raw[9 + 3 * i], raw[10 + 3 * i]
        if code:
            out.append(SPECIAL.get(code, f"mouse:{code}"))
        elif mod or key:
            mods = "+".join(n for n, m in MODS.items() if mod & m)
            out.append(f"key:{mods + '+' if mods else ''}{KEY_NAMES.get(key, hex(key))}")
        else:
            out.append("none")
    return out


def set_button(raw, idx, spec):
    code = mod = key = 0
    if spec == "none":
        pass
    elif spec in SPECIAL_RAW:
        code = SPECIAL_RAW[spec]
    elif spec.startswith("mouse:"):
        code = int(spec[6:])
        assert 1 <= code <= 9
    elif spec.startswith("key:"):
        parts = spec[4:].lower().split("+")
        for p in parts[:-1]:
            mod |= MODS[p]
        key = KEYS[parts[-1]]
    else:
        raise SystemExit(f"unknown button action {spec!r}")
    raw[8 + 3 * idx:11 + 3 * idx] = bytes([code, mod, key])


def describe(n, raw):
    r, g, b = led(raw)
    d = dpis(raw)
    return (f"profile {n}: rate={RATES[raw[2]]}Hz  led={''.join(c for c, v in zip('RGB', (r, g, b)) if v) or 'off'}"
            f"  dpi={'/'.join(str(x) for x in d)} (default={default_dpi(raw)})  raw[7]={raw[7]:#04x}",
            "           buttons=" + ", ".join(f"{i}:{a}" for i, a in enumerate(buttons(raw))))


# --- CLI --------------------------------------------------------------------
def main(argv):
    if not argv or argv[0] in ("-h", "--help"):
        print(__doc__)
        return
    m = G300S()
    try:
        cmd = argv[0]
        if cmd == "info":
            a = m.active()
            print(f"active: profile {a['profile']}, dpi level {a['dpi_level']}")
            for i in range(3):
                print("\n".join(describe(i, m.get_profile(i))))
        elif cmd == "dump":
            print(json.dumps(m.snapshot(), indent=2))
        elif cmd == "save":
            open(argv[1], "w").write(json.dumps(m.snapshot(), indent=2))
            print("saved", argv[1])
        elif cmd == "restore":
            m.restore(json.load(open(argv[1])))
            print("restored")
        elif cmd == "profile":
            m.set_active_profile(int(argv[1])); print("active profile ->", argv[1])
        elif cmd == "dpi-level":
            m.set_dpi_level(int(argv[1])); print("dpi level ->", argv[1])
        elif cmd == "led":
            n = int(argv[1]); raw = m.get_profile(n)
            vals = [int(x) for x in argv[2:5]] if len(argv) > 2 else [1, 1, 1]
            set_led(raw, *vals); m.set_profile(n, raw)
            print(f"profile {n} led -> RGB {vals}")
        elif cmd == "led-live":
            vals = [int(x) for x in argv[1:4]] if len(argv) > 1 else [1, 1, 1]
            m.set_led_live(*vals); print(f"live led -> RGB {vals}")
        elif cmd == "ledfx":
            m.ledfx(argv[1], float(argv[2]) if len(argv) > 2 else 10)
            print(f"{argv[1]} finished")
        elif cmd == "dpi":
            n, idx, val = int(argv[1]), int(argv[2]), int(argv[3])
            raw = m.get_profile(n); set_dpi(raw, idx, val)
            if len(argv) > 4 and argv[4] == "default":
                set_default_dpi(raw, idx)
            m.set_profile(n, raw); print(f"profile {n} dpi[{idx}] -> {val}")
        elif cmd == "rate":
            n, hz = int(argv[1]), int(argv[2])
            raw = m.get_profile(n); raw[2] = RATE_RAW[hz]; m.set_profile(n, raw)
            print(f"profile {n} rate -> {hz}Hz")
        elif cmd == "btn":
            n, idx, spec = int(argv[1]), int(argv[2]), argv[3]
            raw = m.get_profile(n); set_button(raw, idx, spec); m.set_profile(n, raw)
            print(f"profile {n} button {idx} -> {spec}")
        elif cmd == "monitor":
            stop = time.time() + float(argv[1] if len(argv) > 1 else 10)
            while time.time() < stop:
                r = m.dev.read(64, 100)
                if r:
                    print(f"{time.strftime('%H:%M:%S')} IN {bytes(r).hex(' ')}")
        elif cmd == "raw-get":
            rid = int(argv[1], 0); size = int(argv[2])
            print(m.get(rid, size).hex(" "))
        elif cmd == "raw-set":
            rid = int(argv[1], 0); payload = bytes.fromhex(argv[2])
            m.set(rid, bytes([rid]) + payload if not payload or payload[0] != rid else payload)
            print("sent")
        elif cmd == "selftest":
            snap = m.snapshot()
            for i in range(3):
                raw = m.get_profile(i)
                assert raw[0] == PROFILE_IDS[i] and len(raw) == 35
                assert buttons(raw) and len(buttons(raw)) == 9
                copy = bytearray(raw)
                set_led(copy, *led(raw))
                for j in range(4):
                    set_dpi(copy, j, dpis(raw)[j])
                set_default_dpi(copy, default_dpi(raw))
                assert copy == raw, f"roundtrip mismatch profile {i}"
                print(f"profile {i} parse/serialize OK:", describe(i, raw)[0])
            assert m.snapshot() == snap, "read is not idempotent"
            print("selftest OK")
        else:
            raise SystemExit(f"unknown command {cmd!r}; see --help")
    finally:
        m.close()


if __name__ == "__main__":
    main(sys.argv[1:])
