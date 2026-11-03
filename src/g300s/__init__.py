"""Logitech G300s Onboard Memory & Realtime Hardware Control Tool.

Exposes high-level driver and protocol abstractions for the Logitech G300s
Optical Gaming Mouse (VID 0x046D, PID 0xC246).
"""

__version__ = "1.0.0"

from .constants import (
    VID,
    PID,
    USAGE_PAGE,
    RATES,
    SPECIAL_ACTIONS,
    LED_PALETTE
)
from .exceptions import (
    G300sError,
    G300sDeviceNotFoundError,
    G300sWriteTimeoutError,
    G300sBusyError,
    G300sValidationError,
    G300sConflictWarning
)
from .device import G300sDevice
from .profiles import Profile, DeviceSnapshot

__all__ = [
    "G300sDevice",
    "Profile",
    "DeviceSnapshot",
    "G300sError",
    "G300sDeviceNotFoundError",
    "G300sWriteTimeoutError",
    "G300sBusyError",
    "G300sValidationError",
    "G300sConflictWarning",
    "VID",
    "PID",
    "USAGE_PAGE",
    "RATES",
    "SPECIAL_ACTIONS",
    "LED_PALETTE"
]
