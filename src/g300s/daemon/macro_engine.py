"""Host macro and native input injection engine using Windows SendInput API."""

import time
import logging
import threading
import ctypes
import ctypes.wintypes as wintypes
from typing import List, Dict, Any, Optional

logger = logging.getLogger("g300s.daemon.macro_engine")

user32 = ctypes.windll.user32

INPUT_MOUSE = 0
INPUT_KEYBOARD = 1

KEYEVENTF_EXTENDEDKEY = 0x0001
KEYEVENTF_KEYUP = 0x0002
KEYEVENTF_UNICODE = 0x0004
KEYEVENTF_SCANCODE = 0x0008

MOUSEEVENTF_LEFTDOWN = 0x0002
MOUSEEVENTF_LEFTUP = 0x0004
MOUSEEVENTF_RIGHTDOWN = 0x0008
MOUSEEVENTF_RIGHTUP = 0x0010
MOUSEEVENTF_MIDDLEDOWN = 0x0020
MOUSEEVENTF_MIDDLEUP = 0x0040
MOUSEEVENTF_XDOWN = 0x0080
MOUSEEVENTF_XUP = 0x0100

# Virtual Key Codes
VK_CODES = {
    "lbutton": 0x01, "rbutton": 0x02, "mbutton": 0x04,
    "backspace": 0x08, "tab": 0x09, "enter": 0x0D,
    "shift": 0x10, "ctrl": 0x11, "alt": 0x12, "pause": 0x13, "caps": 0x14,
    "esc": 0x1B, "space": 0x20, "pgup": 0x21, "pgdn": 0x22,
    "end": 0x23, "home": 0x24, "left": 0x25, "up": 0x26, "right": 0x27, "down": 0x28,
    "prtsc": 0x2C, "ins": 0x2D, "del": 0x2E,
    # Media keys
    "volume_mute": 0xAD, "volup": 0xAF, "voldown": 0xAE,
    "media_next": 0xB0, "media_prev": 0xB1, "media_stop": 0xB2, "media_play_pause": 0xB3
}
# Add letters
VK_CODES.update({chr(c): c for c in range(ord('A'), ord('Z') + 1)})
# Add digits
VK_CODES.update({str(d): ord(str(d)) for d in range(10)})
# Add F1-F24
VK_CODES.update({f"f{i}": 0x70 + (i - 1) for i in range(1, 25)})


class MOUSEINPUT(ctypes.Structure):
    _fields_ = [
        ("dx", wintypes.LONG),
        ("dy", wintypes.LONG),
        ("mouseData", wintypes.DWORD),
        ("dwFlags", wintypes.DWORD),
        ("time", wintypes.DWORD),
        ("dwExtraInfo", ctypes.c_ulonglong)
    ]


class KEYBDINPUT(ctypes.Structure):
    _fields_ = [
        ("wVk", wintypes.WORD),
        ("wScan", wintypes.WORD),
        ("dwFlags", wintypes.DWORD),
        ("time", wintypes.DWORD),
        ("dwExtraInfo", ctypes.c_ulonglong)
    ]


class HARDWAREINPUT(ctypes.Structure):
    _fields_ = [
        ("uMsg", wintypes.DWORD),
        ("wParamL", wintypes.WORD),
        ("wParamH", wintypes.WORD)
    ]


class _INPUT_UNION(ctypes.Union):
    _fields_ = [
        ("mi", MOUSEINPUT),
        ("ki", KEYBDINPUT),
        ("hi", HARDWAREINPUT)
    ]


class INPUT(ctypes.Structure):
    _fields_ = [
        ("type", wintypes.DWORD),
        ("u", _INPUT_UNION)
    ]


def send_inputs(inputs: List[INPUT]) -> int:
    """Send an array of simulated native input events."""
    if not inputs:
        return 0
    n = len(inputs)
    arr = (INPUT * n)(*inputs)
    return user32.SendInput(n, arr, ctypes.sizeof(INPUT))


def make_key_input(vk_code: int, is_up: bool = False) -> INPUT:
    inp = INPUT(type=INPUT_KEYBOARD)
    inp.u.ki.wVk = vk_code
    inp.u.ki.dwFlags = KEYEVENTF_KEYUP if is_up else 0
    return inp


def make_unicode_input(char: str, is_up: bool = False) -> INPUT:
    inp = INPUT(type=INPUT_KEYBOARD)
    inp.u.ki.wScan = ord(char)
    inp.u.ki.dwFlags = KEYEVENTF_UNICODE | (KEYEVENTF_KEYUP if is_up else 0)
    return inp


def make_mouse_input(flags: int, data: int = 0) -> INPUT:
    inp = INPUT(type=INPUT_MOUSE)
    inp.u.mi.dwFlags = flags
    inp.u.mi.mouseData = data
    return inp


class MacroEngine:
    """Executes multi-key macros, text strings, rapid fire, and media controls."""

    def __init__(self):
        self._rapid_fire_threads: Dict[str, threading.Event] = {}

    def press_key(self, key_name: str):
        vk = VK_CODES.get(key_name.lower())
        if vk:
            send_inputs([make_key_input(vk, is_up=False)])

    def release_key(self, key_name: str):
        vk = VK_CODES.get(key_name.lower())
        if vk:
            send_inputs([make_key_input(vk, is_up=True)])

    def tap_key(self, key_name: str, delay_ms: int = 15):
        self.press_key(key_name)
        time.sleep(delay_ms / 1000.0)
        self.release_key(key_name)

    def mouse_click(self, button: str = "left", delay_ms: int = 15):
        """Click mouse button: 'left', 'right', 'middle'."""
        down_flag, up_flag = {
            "left": (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
            "right": (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
            "middle": (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP)
        }.get(button.lower(), (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP))

        send_inputs([make_mouse_input(down_flag)])
        time.sleep(delay_ms / 1000.0)
        send_inputs([make_mouse_input(up_flag)])

    def type_unicode_text(self, text: str, delay_ms: int = 5):
        """Type any unicode string (including Turkish characters) without layout dependencies."""
        for char in text:
            send_inputs([
                make_unicode_input(char, is_up=False),
                make_unicode_input(char, is_up=True)
            ])
            if delay_ms > 0:
                time.sleep(delay_ms / 1000.0)

    def execute_macro_sequence(self, steps: List[Dict[str, Any]]):
        """
        Execute structured list of macro steps:
        [
            {"type": "press", "key": "ctrl"},
            {"type": "tap", "key": "c"},
            {"type": "release", "key": "ctrl"},
            {"type": "delay", "ms": 50},
            {"type": "text", "text": "Copied!"},
            {"type": "click", "button": "left"}
        ]
        """
        for step in steps:
            stype = step.get("type", "").lower()
            if stype == "press":
                self.press_key(step["key"])
            elif stype == "release":
                self.release_key(step["key"])
            elif stype == "tap":
                self.tap_key(step["key"], step.get("delay_ms", 15))
            elif stype == "click":
                self.mouse_click(step.get("button", "left"), step.get("delay_ms", 15))
            elif stype == "text":
                self.type_unicode_text(step["text"], step.get("delay_ms", 5))
            elif stype == "delay":
                time.sleep(step.get("ms", 10) / 1000.0)

    def start_rapid_fire(self, trigger_id: str, cps: int = 15, button: str = "left"):
        """Start auto-clicker loop in background at requested clicks per second."""
        if trigger_id in self._rapid_fire_threads:
            return

        stop_event = threading.Event()
        self._rapid_fire_threads[trigger_id] = stop_event

        interval = 1.0 / max(1, cps)

        def worker():
            while not stop_event.is_set():
                self.mouse_click(button, delay_ms=10)
                time.sleep(interval)

        thread = threading.Thread(target=worker, daemon=True, name=f"RapidFire_{trigger_id}")
        thread.start()

    def stop_rapid_fire(self, trigger_id: str):
        """Stop rapid fire loop."""
        if trigger_id in self._rapid_fire_threads:
            self._rapid_fire_threads[trigger_id].set()
            del self._rapid_fire_threads[trigger_id]
