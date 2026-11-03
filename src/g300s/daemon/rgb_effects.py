"""Dynamic software-driven RGB effects using G300s live RAM LED control (0xF1)."""

import time
import logging
import threading
import ctypes
import ctypes.wintypes as wintypes
from typing import Optional, Callable, Tuple
from ..constants import LED_PALETTE, LED_CYCLE

logger = logging.getLogger("g300s.daemon.rgb_effects")

# Windows API for zero-dependency CPU load calculation
kernel32 = ctypes.windll.kernel32


class FILETIME(ctypes.Structure):
    _fields_ = [
        ("dwLowDateTime", wintypes.DWORD),
        ("dwHighDateTime", wintypes.DWORD)
    ]


def _filetime_to_int(ft: FILETIME) -> int:
    return (ft.dwHighDateTime << 32) + ft.dwLowDateTime


class CpuMonitor:
    """Calculates system CPU usage percentage using kernel32.GetSystemTimes."""

    def __init__(self):
        self._last_idle = 0
        self._last_kernel = 0
        self._last_user = 0
        self._initialized = False

    def get_cpu_percent(self) -> float:
        idle = FILETIME()
        kernel = FILETIME()
        user = FILETIME()

        if not kernel32.GetSystemTimes(ctypes.byref(idle), ctypes.byref(kernel), ctypes.byref(user)):
            return 0.0

        i = _filetime_to_int(idle)
        k = _filetime_to_int(kernel)
        u = _filetime_to_int(user)

        if not self._initialized:
            self._last_idle = i
            self._last_kernel = k
            self._last_user = u
            self._initialized = True
            return 0.0

        d_idle = i - self._last_idle
        d_kernel = k - self._last_kernel
        d_user = u - self._last_user

        self._last_idle = i
        self._last_kernel = k
        self._last_user = u

        total_system = d_kernel + d_user
        if total_system <= 0:
            return 0.0

        percent = (1.0 - (d_idle / total_system)) * 100.0
        return max(0.0, min(100.0, percent))


class RgbEffectsEngine:
    """Runs real-time lighting animations by streaming live RAM color updates (0xF1)."""

    def __init__(self, write_color_func: Callable[[int, int, int], None]):
        """
        write_color_func: callable that takes (r, g, b) ints and writes 0xF1 to mouse.
        """
        self.write_color = write_color_func
        self._effect_name = "none"
        self._speed_ms = 400
        self._base_rgb: Tuple[int, int, int] = (1, 0, 0)
        self._thread: Optional[threading.Thread] = None
        self._stop_event = threading.Event()
        self._lock = threading.Lock()
        self._cpu_monitor = CpuMonitor()

    @property
    def current_effect(self) -> str:
        return self._effect_name

    def set_effect(self, name: str, speed_ms: int = 400, base_rgb: Tuple[int, int, int] = (1, 0, 0)):
        """Configure and start or change active lighting effect."""
        name = name.lower()
        valid_effects = {"none", "static", "cycle", "breathing", "police", "cpu"}
        if name not in valid_effects:
            raise ValueError(f"Unknown effect: '{name}'. Valid effects: {sorted(valid_effects)}")

        with self._lock:
            self._effect_name = name
            self._speed_ms = max(50, speed_ms)
            self._base_rgb = (1 if base_rgb[0] else 0, 1 if base_rgb[1] else 0, 1 if base_rgb[2] else 0)

        if not self.is_running():
            self.start()

    def start(self):
        """Start lighting animation thread."""
        if self._thread and self._thread.is_alive():
            return
        self._stop_event.clear()
        self._thread = threading.Thread(target=self._worker, daemon=True, name="G300s_RgbEffects")
        self._thread.start()
        logger.info("RgbEffectsEngine started (effect='%s')", self._effect_name)

    def stop(self):
        """Stop animation thread."""
        self._stop_event.set()
        if self._thread and self._thread.is_alive():
            self._thread.join(timeout=1.0)
        self._thread = None
        logger.info("RgbEffectsEngine stopped")

    def is_running(self) -> bool:
        return self._thread is not None and self._thread.is_alive()

    def _worker(self):
        # 7 distinct non-off colors from LED_CYCLE
        cycle_colors = LED_CYCLE[:7]
        cycle_idx = 0
        breathe_state = False
        police_state = False

        while not self._stop_event.is_set():
            with self._lock:
                effect = self._effect_name
                speed = self._speed_ms
                base_rgb = self._base_rgb

            try:
                if effect == "none":
                    time.sleep(0.1)
                    continue

                elif effect == "static":
                    self.write_color(*base_rgb)
                    time.sleep(0.5)

                elif effect == "cycle":
                    color = cycle_colors[cycle_idx % len(cycle_colors)]
                    cycle_idx += 1
                    self.write_color(*color)
                    time.sleep(speed / 1000.0)

                elif effect == "breathing":
                    # Toggle between base color and off
                    color = base_rgb if breathe_state else (0, 0, 0)
                    breathe_state = not breathe_state
                    self.write_color(*color)
                    time.sleep(speed / 1000.0)

                elif effect == "police":
                    # Flashes red (1, 0, 0) and blue (0, 0, 1)
                    color = (1, 0, 0) if police_state else (0, 0, 1)
                    police_state = not police_state
                    self.write_color(*color)
                    time.sleep(speed / 1000.0)

                elif effect == "cpu":
                    # Green < 50%, Yellow 50-80%, Red > 80%
                    usage = self._cpu_monitor.get_cpu_percent()
                    if usage < 50.0:
                        color = (0, 1, 0)  # green
                    elif usage < 80.0:
                        color = (1, 1, 0)  # yellow
                    else:
                        color = (1, 0, 0)  # red
                    self.write_color(*color)
                    time.sleep(speed / 1000.0)

            except Exception as e:
                logger.debug("Error in RGB effect worker: %s", e)
                time.sleep(0.2)
