"""Active foreground application monitor for automatic profile switching."""

import os
import time
import logging
import ctypes
import ctypes.wintypes as wintypes
from typing import Dict, Optional, Callable

logger = logging.getLogger("g300s.daemon.app_watcher")

user32 = ctypes.windll.user32
kernel32 = ctypes.windll.kernel32

PROCESS_QUERY_LIMITED_INFORMATION = 0x1000


def get_foreground_process_name() -> Optional[str]:
    """Retrieve the executable name (e.g. 'cs2.exe') of the active foreground window."""
    hwnd = user32.GetForegroundWindow()
    if not hwnd:
        return None

    pid = wintypes.DWORD()
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    if not pid.value:
        return None

    h_proc = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid.value)
    if not h_proc:
        return None

    try:
        buf = ctypes.create_unicode_buffer(1024)
        size = wintypes.DWORD(1024)
        if kernel32.QueryFullProcessImageNameW(h_proc, 0, buf, ctypes.byref(size)):
            return os.path.basename(buf.value).lower()
        return None
    finally:
        kernel32.CloseHandle(h_proc)


class AppWatcher:
    """Monitors active window process changes and triggers profile switches."""

    def __init__(
        self,
        app_rules: Dict[str, int],
        default_profile: int = 0,
        switch_callback: Optional[Callable[[int, str], None]] = None,
        poll_interval: float = 0.5
    ):
        """
        app_rules: mapping of lowercase exe name to profile index (0..2),
                   e.g. {"cs2.exe": 1, "blender.exe": 2, "code.exe": 0}
        """
        self.app_rules = {k.lower(): v for k, v in app_rules.items()}
        self.default_profile = default_profile
        self.switch_callback = switch_callback
        self.poll_interval = poll_interval
        self._last_exe: Optional[str] = None
        self._current_profile: int = default_profile
        self._running = False

    @property
    def current_profile(self) -> int:
        return self._current_profile

    def poll_once(self) -> Optional[int]:
        """Check the foreground process and trigger switch if rule matches."""
        exe = get_foreground_process_name()
        if not exe or exe == self._last_exe:
            return None

        self._last_exe = exe
        target_profile = self.app_rules.get(exe, self.default_profile)

        if target_profile != self._current_profile:
            logger.info("Active window changed to '%s' -> switching profile to %d", exe, target_profile)
            self._current_profile = target_profile
            if self.switch_callback:
                self.switch_callback(target_profile, exe)
            return target_profile

        return None

    def run_loop(self):
        """Continuous polling loop."""
        self._running = True
        logger.info("AppWatcher started. Rules: %s (Default: %d)", self.app_rules, self.default_profile)
        while self._running:
            try:
                self.poll_once()
            except Exception as e:
                logger.debug("Error in app watcher poll: %s", e)
            time.sleep(self.poll_interval)

    def stop(self):
        """Stop the watcher loop."""
        self._running = False
