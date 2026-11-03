"""Low-level HID communication driver for the Logitech G300s with flash write guard."""

import time
import warnings
import hid
from typing import Dict, Any, Tuple, Optional, Callable

from .constants import (
    VID,
    PID,
    USAGE_PAGE,
    PROFILE_REPORT_IDS,
    REPORT_ACTIVE_STATE,
    REPORT_LIVE_LED,
    REPORT_LIVE_RATE,
    REPORT_INPUT_EVENT,
    FLASH_BUSY_TIMEOUT_SEC,
    FLASH_SETTLE_POLL_INTERVAL_SEC,
    FLASH_SETTLE_MAX_ATTEMPTS,
    DEBOUNCE_RETRY_INTERVAL_SEC
)
from .protocol import (
    parse_f0_state,
    make_set_active_profile_cmd,
    make_set_dpi_level_cmd,
    make_config_mode_cmd,
    parse_f1_led,
    make_f1_led_cmd,
    parse_f2_rate,
    make_f2_rate_cmd,
    parse_input_event,
    parse_profile,
    serialize_profile
)
from .exceptions import (
    G300sDeviceNotFoundError,
    G300sWriteTimeoutError,
    G300sBusyError,
    G300sConflictWarning
)


def is_lgs_running() -> bool:
    """Check if Logitech Gaming Software (LCore.exe) is currently active."""
    try:
        import subprocess
        out = subprocess.check_output(
            ["tasklist", "/FI", "IMAGENAME eq LCore.exe"],
            stderr=subprocess.DEVNULL,
            creationflags=0x08000000  # CREATE_NO_WINDOW
        ).decode("latin-1", "ignore")
        return "LCore.exe" in out
    except Exception:
        return False


class G300sDevice:
    """Hardware driver for Logitech G300s."""

    def __init__(self, check_conflict: bool = True):
        self.dev = None
        self.path = None

        if check_conflict and is_lgs_running():
            warnings.warn(
                "Logitech Gaming Software ('LCore.exe') is running. "
                "Simultaneous HID access may cause profile writes or state updates to revert.",
                G300sConflictWarning,
                stacklevel=2
            )

        self.open()

    def open(self):
        """Open HID connection to device on usage page 0xFF80."""
        if self.dev:
            return
        devices = hid.enumerate(VID, PID)
        path = next((d["path"] for d in devices if d.get("usage_page") == USAGE_PAGE), None)
        if not path:
            raise G300sDeviceNotFoundError(
                f"Logitech G300s (VID={VID:#06x}, PID={PID:#06x}, UsagePage={USAGE_PAGE:#06x}) not found. "
                "Ensure the mouse is securely plugged in."
            )
        self.dev = hid.device()
        self.dev.open_path(path)
        self.path = path

    def is_lgs_running(self) -> bool:
        """Check if Logitech Gaming Software is currently running."""
        return is_lgs_running()

    def close(self):
        if self.dev:
            try:
                self.dev.close()
            except Exception:
                pass
            self.dev = None

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc_val, exc_tb):
        self.close()

    # --- Raw Feature Report Access ---

    def get_feature(self, report_id: int, expected_size: int) -> bytes:
        """Read a feature report from the device."""
        if not self.dev:
            raise G300sDeviceNotFoundError("Device is not opened")
        # Request buffer size + 1 to account for report id prefix
        buf = bytes(self.dev.get_feature_report(report_id, expected_size + 1))
        if not buf or buf[0] != report_id:
            raise RuntimeError(f"Bad report ID received {buf[:1].hex() if buf else 'empty'} for {report_id:#x}")
        return buf[:expected_size].ljust(expected_size, b"\x00")

    def send_feature(self, payload: bytes):
        """Send a feature report to the device."""
        if not self.dev:
            raise G300sDeviceNotFoundError("Device is not opened")
        if not payload:
            raise ValueError("Payload cannot be empty")
        self.dev.send_feature_report(payload)

    # --- Active State & Flash Guard (Report 0xF0) ---

    def get_active_state(self) -> Dict[str, Any]:
        """Read active profile, DPI level, and flash busy flag."""
        buf = self.get_feature(REPORT_ACTIVE_STATE, 4)
        return parse_f0_state(buf)

    def is_flash_busy(self) -> bool:
        """Check if mouse hardware flash controller is currently busy."""
        return self.get_active_state()["is_busy"]

    def wait_not_busy(self, timeout_sec: float = FLASH_BUSY_TIMEOUT_SEC) -> bool:
        """Poll until flash controller is ready to receive writes."""
        start = time.time()
        while time.time() - start < timeout_sec:
            if not self.is_flash_busy():
                return True
            time.sleep(FLASH_SETTLE_POLL_INTERVAL_SEC)
        return False

    def set_active_profile(self, profile_index: int):
        """Switch active onboard profile (0..2)."""
        cmd = make_set_active_profile_cmd(profile_index)
        self.send_feature(cmd)

    def set_active_dpi_level(self, dpi_level: int):
        """Switch active DPI level index (0..3)."""
        cmd = make_set_dpi_level_cmd(dpi_level)
        self.send_feature(cmd)

    # --- Live RAM Registers (Zero Flash Wear) ---

    def get_live_led(self) -> Tuple[int, int, int]:
        """Read active live LED color (R, G, B) from RAM."""
        buf = self.get_feature(REPORT_LIVE_LED, 2)
        return parse_f1_led(buf)

    def set_live_led(self, r: int, g: int, b: int):
        """Set live LED color in RAM instantly (0xF1). Does not touch flash."""
        cmd = make_f1_led_cmd(r, g, b)
        self.send_feature(cmd)

    def get_live_rate(self) -> int:
        """Read live USB polling rate in Hz (0xF2)."""
        buf = self.get_feature(REPORT_LIVE_RATE, 2)
        return parse_f2_rate(buf)

    def set_live_rate(self, hz: int):
        """Set live USB polling rate in RAM instantly (0xF2). Does not touch flash."""
        cmd = make_f2_rate_cmd(hz)
        self.send_feature(cmd)

    # --- Flash Profiles (0xF3..0xF5) ---

    def get_profile_raw(self, profile_index: int) -> bytearray:
        """Read raw 35-byte profile report."""
        if not 0 <= profile_index <= 2:
            raise ValueError(f"Profile index must be 0..2, got {profile_index}")
        rid = PROFILE_REPORT_IDS[profile_index]
        return bytearray(self.get_feature(rid, 35))

    def get_profile(self, profile_index: int) -> Dict[str, Any]:
        """Read and parse profile into a structured dictionary."""
        raw = self.get_profile_raw(profile_index)
        return parse_profile(raw, profile_index)

    def set_profile_raw(self, profile_index: int, raw_payload: bytearray):
        """Write raw 35-byte profile to flash with busy-wait and verification polling."""
        if not 0 <= profile_index <= 2:
            raise ValueError(f"Profile index must be 0..2, got {profile_index}")
        if len(raw_payload) != 35 or raw_payload[0] != PROFILE_REPORT_IDS[profile_index]:
            raise ValueError(f"Invalid profile payload for profile {profile_index}")

        # Retry loop for flash write
        max_attempts = 3
        for attempt in range(max_attempts):
            # 1. Wait for flash controller ready
            if not self.wait_not_busy():
                if attempt == max_attempts - 1:
                    raise G300sBusyError("Mouse flash controller is busy; write rejected.")
                time.sleep(DEBOUNCE_RETRY_INTERVAL_SEC)
                continue

            # 2. Enter config / unlock mode
            self.send_feature(make_config_mode_cmd(True))
            time.sleep(0.01)

            # 3. Send profile payload
            self.send_feature(bytes(raw_payload))

            # 4. Exit config mode
            self.send_feature(make_config_mode_cmd(False))

            # 5. Verification poll loop (up to 40 polls of 50ms)
            settled = False
            for _ in range(FLASH_SETTLE_MAX_ATTEMPTS):
                time.sleep(FLASH_SETTLE_POLL_INTERVAL_SEC)
                check = self.get_profile_raw(profile_index)
                if check == raw_payload and not self.is_flash_busy():
                    settled = True
                    break

            if settled:
                return

            time.sleep(DEBOUNCE_RETRY_INTERVAL_SEC)

        raise G300sWriteTimeoutError(f"Profile {profile_index} write did not verify in flash after {max_attempts} attempts.")

    def set_profile(self, profile_index: int, profile_data: Dict[str, Any]):
        """Serialize and write structured profile dictionary to flash."""
        raw = serialize_profile(profile_data, profile_index)
        self.set_profile_raw(profile_index, raw)

    # --- Hardware Event Notification (Report 0x80) ---

    def read_input_event(self, timeout_ms: int = 50) -> Optional[Dict[str, Any]]:
        """Read a pending asynchronous hardware button notification from report 0x80."""
        if not self.dev:
            return None
        data = self.dev.read(64, timeout_ms)
        if data:
            return parse_input_event(bytes(data))
        return None

    def listen_events(self, duration_sec: float, callback: Callable[[Dict[str, Any]], None]):
        """Listen for hardware button events on 0xFF80 for a given duration."""
        end_time = time.time() + duration_sec
        while time.time() < end_time:
            evt = self.read_input_event(timeout_ms=50)
            if evt:
                callback(evt)
