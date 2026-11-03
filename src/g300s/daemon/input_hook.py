"""Low-level Windows keyboard hook for intercepting macro triggers (e.g. F13..F24)."""

import logging
import threading
import ctypes
import ctypes.wintypes as wintypes
from typing import Callable, Optional, Set

logger = logging.getLogger("g300s.daemon.input_hook")

user32 = ctypes.windll.user32
kernel32 = ctypes.windll.kernel32

WH_KEYBOARD_LL = 13
WM_KEYDOWN = 0x0100
WM_KEYUP = 0x0101
WM_SYSKEYDOWN = 0x0104
WM_SYSKEYUP = 0x0105

# F13..F24 Virtual-Key codes in Windows: 0x7C .. 0x87
VK_F13 = 0x7C
VK_F24 = 0x87

HOOKPROC = ctypes.WINFUNCTYPE(ctypes.c_longlong, ctypes.c_int, wintypes.WPARAM, wintypes.LPARAM)

user32.SetWindowsHookExW.restype = wintypes.HHOOK
user32.SetWindowsHookExW.argtypes = [ctypes.c_int, HOOKPROC, wintypes.HINSTANCE, wintypes.DWORD]

user32.UnhookWindowsHookEx.restype = wintypes.BOOL
user32.UnhookWindowsHookEx.argtypes = [wintypes.HHOOK]

user32.CallNextHookEx.restype = ctypes.c_longlong
user32.CallNextHookEx.argtypes = [wintypes.HHOOK, ctypes.c_int, wintypes.WPARAM, wintypes.LPARAM]


class KBDLLHOOKSTRUCT(ctypes.Structure):
    _fields_ = [
        ("vkCode", wintypes.DWORD),
        ("scanCode", wintypes.DWORD),
        ("flags", wintypes.DWORD),
        ("time", wintypes.DWORD),
        ("dwExtraInfo", ctypes.c_ulonglong)
    ]


class LowLevelKeyHook:
    """Hooks global keyboard events to intercept high function keys (F13-F24) for macro execution."""

    def __init__(
        self,
        on_key_down: Optional[Callable[[int, str], bool]] = None,
        on_key_up: Optional[Callable[[int, str], None]] = None,
        intercept_keys: Optional[Set[int]] = None
    ):
        """
        on_key_down: func(vk_code, key_name) -> bool (return True to consume/suppress key)
        on_key_up: func(vk_code, key_name)
        intercept_keys: set of virtual key codes to intercept. Default is F13..F24.
        """
        self.on_key_down = on_key_down
        self.on_key_up = on_key_up
        self.intercept_keys = intercept_keys or set(range(VK_F13, VK_F24 + 1))
        self._hook_id: Optional[int] = None
        self._thread: Optional[threading.Thread] = None
        self._thread_id = 0
        self._hook_proc_ref = None  # prevent garbage collection of ctypes callback

    def _get_key_name(self, vk_code: int) -> str:
        if VK_F13 <= vk_code <= VK_F24:
            return f"f{13 + (vk_code - VK_F13)}"
        return f"vk_{hex(vk_code)}"

    def _hook_callback(self, nCode: int, wParam: wintypes.WPARAM, lParam: wintypes.LPARAM) -> int:
        if nCode >= 0:
            kbd = KBDLLHOOKSTRUCT.from_address(lParam)
            vk = kbd.vkCode
            if vk in self.intercept_keys:
                key_name = self._get_key_name(vk)
                if wParam in (WM_KEYDOWN, WM_SYSKEYDOWN):
                    if self.on_key_down:
                        consume = self.on_key_down(vk, key_name)
                        if consume:
                            return 1  # Suppress key from other apps
                elif wParam in (WM_KEYUP, WM_SYSKEYUP):
                    if self.on_key_up:
                        self.on_key_up(vk, key_name)
                    return 1

        return user32.CallNextHookEx(self._hook_id, nCode, wParam, lParam)

    def _pump_messages(self):
        self._thread_id = kernel32.GetCurrentThreadId()
        self._hook_proc_ref = HOOKPROC(self._hook_callback)
        self._hook_id = user32.SetWindowsHookExW(
            WH_KEYBOARD_LL,
            self._hook_proc_ref,
            None,
            0
        )

        if not self._hook_id:
            logger.error("Failed to install LowLevelKeyHook: %d", kernel32.GetLastError())
            return

        logger.info("LowLevelKeyHook installed successfully for keys: %s", [f"F{i}" for i in range(13, 25)])

        msg = wintypes.MSG()
        while user32.GetMessageW(ctypes.byref(msg), None, 0, 0) > 0:
            user32.TranslateMessage(ctypes.byref(msg))
            user32.DispatchMessageW(ctypes.byref(msg))

        if self._hook_id:
            user32.UnhookWindowsHookEx(self._hook_id)
            self._hook_id = None
        logger.info("LowLevelKeyHook uninstalled")

    def start(self):
        """Start key hook thread and message pump."""
        if self._thread and self._thread.is_alive():
            return
        self._thread = threading.Thread(target=self._pump_messages, daemon=True, name="G300s_KeyHook")
        self._thread.start()

    def stop(self):
        """Post WM_QUIT to hook message pump."""
        if self._thread_id:
            WM_QUIT = 0x0012
            user32.PostThreadMessageW(self._thread_id, WM_QUIT, 0, 0)
        if self._thread and self._thread.is_alive():
            self._thread.join(timeout=1.0)
        self._thread = None
        self._thread_id = 0
