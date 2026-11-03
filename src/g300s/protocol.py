"""Protocol parsing and serialization for Logitech G300s HID reports."""

from typing import Dict, List, Tuple, Any, Optional
from .constants import (
    REPORT_ACTIVE_STATE,
    REPORT_LIVE_LED,
    REPORT_LIVE_RATE,
    REPORT_INPUT_EVENT,
    PROFILE_REPORT_IDS,
    RATES,
    RATE_TO_CODE,
    SPECIAL_ACTIONS,
    ACTION_TO_CODE,
    BUTTON_COUNT,
    DPI_LEVEL_COUNT
)
from .keycodes import format_keystroke, parse_keystroke
from .exceptions import G300sValidationError


def parse_f0_state(buf: bytes) -> Dict[str, Any]:
    """Parse Report 0xF0 into active profile, DPI level, busy state, and raw flags."""
    if len(buf) < 4 or buf[0] != REPORT_ACTIVE_STATE:
        raise G300sValidationError(f"Invalid F0 buffer: {buf.hex(' ')}")
    flags = buf[1]
    return {
        "profile": (flags >> 4) & 0x0F,
        "dpi_level": (flags >> 1) & 0x03,
        "is_busy": bool(flags & 0x08),
        "config_mode": bool(flags & 0x01),
        "raw_flags": flags,
        "raw_scratch": (buf[2], buf[3])
    }


def make_set_active_profile_cmd(profile_index: int) -> bytes:
    """Create 0xF0 payload to switch active profile."""
    if not 0 <= profile_index <= 2:
        raise G300sValidationError(f"Profile index must be 0..2, got {profile_index}")
    return bytes([REPORT_ACTIVE_STATE, 0x80 | ((profile_index & 0x0F) << 4), 0x00, 0x00])


def make_set_dpi_level_cmd(dpi_level_index: int) -> bytes:
    """Create 0xF0 payload to switch active DPI level."""
    if not 0 <= dpi_level_index <= 3:
        raise G300sValidationError(f"DPI level index must be 0..3, got {dpi_level_index}")
    return bytes([REPORT_ACTIVE_STATE, 0x40 | ((dpi_level_index & 0x03) << 1), 0x00, 0x00])


def make_config_mode_cmd(enable: bool) -> bytes:
    """Create 0xF0 payload to enter or exit unlock/config mode."""
    return bytes([REPORT_ACTIVE_STATE, 0x01 if enable else 0x00, 0x00, 0x00])


def parse_f1_led(buf: bytes) -> Tuple[int, int, int]:
    """Parse Report 0xF1 live LED color: (R, G, B)."""
    if len(buf) < 2 or buf[0] != REPORT_LIVE_LED:
        raise G300sValidationError(f"Invalid F1 buffer: {buf.hex(' ')}")
    val = buf[1]
    return (val & 1, (val >> 1) & 1, (val >> 2) & 1)


def make_f1_led_cmd(r: int, g: int, b: int) -> bytes:
    """Create Report 0xF1 live LED payload."""
    val = (r & 1) | ((g & 1) << 1) | ((b & 1) << 2)
    return bytes([REPORT_LIVE_LED, val])


def parse_f2_rate(buf: bytes) -> int:
    """Parse Report 0xF2 live polling rate into Hz."""
    if len(buf) < 2 or buf[0] != REPORT_LIVE_RATE:
        raise G300sValidationError(f"Invalid F2 buffer: {buf.hex(' ')}")
    rate_code = buf[1] & 0x03
    return RATES.get(rate_code, 500)


def make_f2_rate_cmd(hz: int) -> bytes:
    """Create Report 0xF2 live polling rate payload."""
    if hz not in RATE_TO_CODE:
        raise G300sValidationError(f"Invalid rate {hz}Hz. Must be one of {list(RATE_TO_CODE.keys())}")
    code = RATE_TO_CODE[hz]
    return bytes([REPORT_LIVE_RATE, 0x04 | (code & 0x03)])


def parse_input_event(buf: bytes) -> Optional[Dict[str, Any]]:
    """Parse Report 0x80 asynchronous hardware button notification."""
    if not buf or len(buf) < 5 or buf[0] != REPORT_INPUT_EVENT:
        return None
    is_pressed = bool(buf[1] & 0x01)
    action_code = buf[4]
    action_name = SPECIAL_ACTIONS.get(action_code, f"special:0x{action_code:02x}")
    return {
        "event": "button",
        "action_code": action_code,
        "action": action_name,
        "pressed": is_pressed
    }


def parse_profile(raw: bytearray, profile_index: int) -> Dict[str, Any]:
    """Parse a 35-byte raw profile report into a structured dictionary."""
    expected_id = PROFILE_REPORT_IDS[profile_index]
    if len(raw) < 35 or raw[0] != expected_id:
        raise G300sValidationError(
            f"Invalid profile report for index {profile_index}: length {len(raw)}, id 0x{raw[0]:02x} (expected 0x{expected_id:02x})"
        )

    # LED
    led_byte = raw[1]
    led = (led_byte & 1, (led_byte >> 1) & 1, (led_byte >> 2) & 1)

    # Polling Rate
    rate_byte = raw[2]
    rate_hz = RATES.get(rate_byte, 500)

    # DPI levels (4 levels)
    dpis = []
    default_dpi = 0
    for i in range(DPI_LEVEL_COUNT):
        b = raw[3 + i]
        dpis.append((b & 0x7F) * 250)
        if b & 0x80:
            default_dpi = i

    # Unknown hardware/calibration byte (offset 7)
    raw7 = raw[7]

    # 9 Buttons (offset 8 onwards, 3 bytes each)
    buttons = []
    for i in range(BUTTON_COUNT):
        code = raw[8 + 3 * i]
        mod = raw[9 + 3 * i]
        key = raw[10 + 3 * i]

        if code in SPECIAL_ACTIONS:
            buttons.append(SPECIAL_ACTIONS[code])
        elif 1 <= code <= 9:
            buttons.append(f"mouse:{code}")
        elif mod or key:
            buttons.append(f"key:{format_keystroke(mod, key)}")
        else:
            buttons.append("none")

    return {
        "index": profile_index,
        "report_id": expected_id,
        "led": led,
        "rate": rate_hz,
        "rate_code": rate_byte,
        "dpis": dpis,
        "default_dpi": default_dpi,
        "raw7": raw7,
        "buttons": buttons
    }


def serialize_profile(data: Dict[str, Any], profile_index: int) -> bytearray:
    """Serialize a structured profile dictionary into a 35-byte raw report buffer."""
    buf = bytearray(35)
    buf[0] = PROFILE_REPORT_IDS[profile_index]

    # LED
    r, g, b = data.get("led", (1, 1, 1))
    buf[1] = (r & 1) | ((g & 1) << 1) | ((b & 1) << 2)

    # Rate
    rate = data.get("rate", 500)
    if rate not in RATE_TO_CODE:
        raise G300sValidationError(f"Invalid polling rate {rate}Hz")
    buf[2] = RATE_TO_CODE[rate]

    # DPI levels
    dpis = data.get("dpis", [500, 1000, 1500, 2000])
    default_idx = data.get("default_dpi", 0)
    if len(dpis) != DPI_LEVEL_COUNT:
        raise G300sValidationError(f"Exactly {DPI_LEVEL_COUNT} DPI levels required, got {len(dpis)}")

    for i, val in enumerate(dpis):
        if val % 250 != 0 or not (250 <= val <= 2500):
            raise G300sValidationError(f"DPI must be between 250 and 2500 in steps of 250, got {val}")
        step = (val // 250) & 0x7F
        if i == default_idx:
            step |= 0x80
        buf[3 + i] = step

    # Byte 7 (preserved)
    buf[7] = data.get("raw7", 0x40)

    # Buttons
    buttons = data.get("buttons", ["none"] * BUTTON_COUNT)
    if len(buttons) != BUTTON_COUNT:
        raise G300sValidationError(f"Exactly {BUTTON_COUNT} buttons required, got {len(buttons)}")

    for i, spec in enumerate(buttons):
        code, mod, key = 0, 0, 0
        spec = spec.strip()
        if spec == "none":
            pass
        elif spec in ACTION_TO_CODE:
            code = ACTION_TO_CODE[spec]
        elif spec.startswith("mouse:"):
            code = int(spec[6:])
            if not 1 <= code <= 9:
                raise G300sValidationError(f"Mouse button must be 1..9, got {code}")
        elif spec.startswith("key:"):
            mod, key = parse_keystroke(spec[4:])
        else:
            raise G300sValidationError(f"Unknown button action: '{spec}'")

        buf[8 + 3 * i] = code
        buf[9 + 3 * i] = mod
        buf[10 + 3 * i] = key

    return buf
