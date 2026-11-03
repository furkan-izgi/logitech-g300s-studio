"""HID Keyboard Usage codes and modifier bitmasks for G300s button mapping."""

# Modifier bitmask (USB HID Keyboard specification)
MODIFIERS = {
    "ctrl": 0x01,
    "lctrl": 0x01,
    "shift": 0x02,
    "lshift": 0x02,
    "alt": 0x04,
    "lalt": 0x04,
    "win": 0x08,
    "lwin": 0x08,
    "rctrl": 0x10,
    "rshift": 0x20,
    "ralt": 0x40,
    "rwin": 0x80
}

MODIFIER_NAMES = {
    0x01: "ctrl",
    0x02: "shift",
    0x04: "alt",
    0x08: "win",
    0x10: "rctrl",
    0x20: "rshift",
    0x40: "ralt",
    0x80: "rwin"
}

# Standard HID Keyboard Usage Page (0x07)
KEY_CODES = {
    # Letters
    "a": 0x04, "b": 0x05, "c": 0x06, "d": 0x07, "e": 0x08, "f": 0x09,
    "g": 0x0A, "h": 0x0B, "i": 0x0C, "j": 0x0D, "k": 0x0E, "l": 0x0F,
    "m": 0x10, "n": 0x11, "o": 0x12, "p": 0x13, "q": 0x14, "r": 0x15,
    "s": 0x16, "t": 0x17, "u": 0x18, "v": 0x19, "w": 0x1A, "x": 0x1B,
    "y": 0x1C, "z": 0x1D,

    # Numbers (top row)
    "1": 0x1E, "2": 0x1F, "3": 0x20, "4": 0x21, "5": 0x22,
    "6": 0x23, "7": 0x24, "8": 0x25, "9": 0x26, "0": 0x27,

    # Editing & Control
    "enter": 0x28, "return": 0x28,
    "esc": 0x29, "escape": 0x29,
    "backspace": 0x2A,
    "tab": 0x2B,
    "space": 0x2C,
    "caps": 0x39, "capslock": 0x39,

    # Punctuation & Symbols
    "minus": 0x2D, "-": 0x2D,
    "equal": 0x2E, "=": 0x2E,
    "lbracket": 0x2F, "[": 0x2F,
    "rbracket": 0x30, "]": 0x30,
    "backslash": 0x31, "\\": 0x31,
    "semicolon": 0x33, ";": 0x33,
    "quote": 0x34, "'": 0x34,
    "grave": 0x35, "`": 0x35,
    "comma": 0x36, ",": 0x36,
    "dot": 0x37, "period": 0x37, ".": 0x37,
    "slash": 0x38, "/": 0x38,

    # Function Keys F1-F12
    "f1": 0x3A, "f2": 0x3B, "f3": 0x3C, "f4": 0x3D, "f5": 0x3E, "f6": 0x3F,
    "f7": 0x40, "f8": 0x41, "f9": 0x42, "f10": 0x43, "f11": 0x44, "f12": 0x45,

    # High Function Keys F13-F24 (Ideal for macro triggers with zero conflict)
    "f13": 0x68, "f14": 0x69, "f15": 0x6A, "f16": 0x6B,
    "f17": 0x6C, "f18": 0x6D, "f19": 0x6E, "f20": 0x6F,
    "f21": 0x70, "f22": 0x71, "f23": 0x72, "f24": 0x73,

    # Navigation & System
    "prtsc": 0x46, "printscreen": 0x46,
    "scroll": 0x47, "scrolllock": 0x47,
    "pause": 0x48,
    "ins": 0x49, "insert": 0x49,
    "home": 0x4A,
    "pgup": 0x4B, "pageup": 0x4B,
    "del": 0x4C, "delete": 0x4C,
    "end": 0x4D,
    "pgdn": 0x4E, "pagedown": 0x4E,
    "right": 0x4F,
    "left": 0x50,
    "down": 0x51,
    "up": 0x52,
    "menu": 0x65, "app": 0x65,

    # Numpad / Keypad
    "numlock": 0x53,
    "kp_slash": 0x54,
    "kp_star": 0x55,
    "kp_minus": 0x56,
    "kp_plus": 0x57,
    "kp_enter": 0x58,
    "kp_1": 0x59, "kp_2": 0x5A, "kp_3": 0x5B, "kp_4": 0x5C, "kp_5": 0x5D,
    "kp_6": 0x5E, "kp_7": 0x5F, "kp_8": 0x60, "kp_9": 0x61, "kp_0": 0x62,
    "kp_dot": 0x63,

    # Audio & Consumer shortcuts available in boot keyboard table
    "mute": 0x7F,
    "volup": 0x80, "volume_up": 0x80,
    "voldown": 0x81, "volume_down": 0x81
}

CODE_TO_NAME = {v: k for k, v in KEY_CODES.items() if not k.startswith("kp_") or k.startswith("kp_")}
# Give precedence to common short names
PREFER_NAMES = {
    0x28: "enter",
    0x29: "esc",
    0x39: "caps",
    0x49: "ins",
    0x4B: "pgup",
    0x4C: "del",
    0x4E: "pgdn",
    0x46: "prtsc",
    0x47: "scroll",
    0x2D: "-",
    0x2E: "=",
    0x2F: "[",
    0x30: "]",
    0x31: "\\",
    0x33: ";",
    0x34: "'",
    0x35: "`",
    0x36: ",",
    0x37: ".",
    0x38: "/"
}
for code, name in PREFER_NAMES.items():
    CODE_TO_NAME[code] = name


def format_keystroke(modifier_mask: int, keycode: int) -> str:
    """Format modifier bitmask and HID usage into readable string (e.g. 'ctrl+shift+c')."""
    mods = [name for bit, name in sorted(MODIFIER_NAMES.items()) if modifier_mask & bit]
    key_name = CODE_TO_NAME.get(keycode, f"0x{keycode:02x}" if keycode else "none")
    if not mods and key_name == "none":
        return "none"
    if mods:
        return "+".join(mods) + ("+" + key_name if key_name != "none" else "")
    return key_name


def parse_keystroke(spec: str) -> tuple[int, int]:
    """Parse string representation into (modifier_mask, keycode)."""
    parts = spec.lower().strip().split("+")
    mod_mask = 0
    key_code = 0

    for p in parts[:-1]:
        if p in MODIFIERS:
            mod_mask |= MODIFIERS[p]
        else:
            raise ValueError(f"Unknown modifier: '{p}' in spec '{spec}'")

    last = parts[-1]
    if last in MODIFIERS:
        mod_mask |= MODIFIERS[last]
        key_code = 0
    elif last in KEY_CODES:
        key_code = KEY_CODES[last]
    elif last.startswith("0x"):
        key_code = int(last, 16)
    else:
        raise ValueError(f"Unknown key: '{last}' in spec '{spec}'")

    return mod_mask, key_code
