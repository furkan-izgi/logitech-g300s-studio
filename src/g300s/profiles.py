"""High-level Profile representation, validation, and JSON snapshot handling."""

import json
from dataclasses import dataclass, field
from typing import List, Tuple, Dict, Any, Optional

from .constants import (
    PROFILE_COUNT,
    BUTTON_COUNT,
    DPI_LEVEL_COUNT,
    RATES,
    RATE_TO_CODE
)
from .keycodes import format_keystroke, parse_keystroke
from .protocol import parse_profile, serialize_profile
from .exceptions import G300sValidationError


@dataclass
class Profile:
    """High-level G300s profile model."""
    index: int
    led: Tuple[int, int, int] = (1, 1, 1)  # R, G, B (0 or 1)
    rate: int = 500                       # 125, 250, 500, 1000 Hz
    dpis: List[int] = field(default_factory=lambda: [500, 1000, 1500, 2000])
    default_dpi: int = 1
    buttons: List[str] = field(default_factory=lambda: ["none"] * BUTTON_COUNT)
    raw7: int = 0x40

    def validate(self):
        """Validate all parameters against hardware limits."""
        if not 0 <= self.index <= 2:
            raise G300sValidationError(f"Invalid profile index: {self.index}")

        for val in self.led:
            if val not in (0, 1):
                raise G300sValidationError(f"LED channels must be 0 or 1, got {self.led}")

        if self.rate not in RATE_TO_CODE:
            raise G300sValidationError(f"Invalid polling rate {self.rate}Hz. Must be one of {list(RATE_TO_CODE.keys())}")

        if len(self.dpis) != DPI_LEVEL_COUNT:
            raise G300sValidationError(f"Profile must have exactly {DPI_LEVEL_COUNT} DPI levels")

        for val in self.dpis:
            if val % 250 != 0 or not (250 <= val <= 2500):
                raise G300sValidationError(f"DPI must be between 250 and 2500 in steps of 250, got {val}")

        if not 0 <= self.default_dpi < DPI_LEVEL_COUNT:
            raise G300sValidationError(f"Default DPI index must be between 0 and 3, got {self.default_dpi}")

        if len(self.buttons) != BUTTON_COUNT:
            raise G300sValidationError(f"Profile must configure exactly {BUTTON_COUNT} buttons")

    def to_dict(self) -> Dict[str, Any]:
        """Convert profile to serializable dictionary."""
        return {
            "index": self.index,
            "led": list(self.led),
            "rate": self.rate,
            "dpis": list(self.dpis),
            "default_dpi": self.default_dpi,
            "buttons": list(self.buttons),
            "raw7": self.raw7
        }

    @classmethod
    def from_dict(cls, data: Dict[str, Any]) -> "Profile":
        """Construct a Profile instance from dictionary."""
        prof = cls(
            index=data["index"],
            led=tuple(data.get("led", (1, 1, 1))),
            rate=data.get("rate", 500),
            dpis=list(data.get("dpis", [500, 1000, 1500, 2000])),
            default_dpi=data.get("default_dpi", 0),
            buttons=list(data.get("buttons", ["none"] * BUTTON_COUNT)),
            raw7=data.get("raw7", 0x40)
        )
        prof.validate()
        return prof

    @classmethod
    def from_raw(cls, raw: bytearray, index: int) -> "Profile":
        """Construct a Profile instance from 35-byte raw report buffer."""
        parsed = parse_profile(raw, index)
        return cls(
            index=index,
            led=parsed["led"],
            rate=parsed["rate"],
            dpis=parsed["dpis"],
            default_dpi=parsed["default_dpi"],
            buttons=parsed["buttons"],
            raw7=parsed["raw7"]
        )

    def to_raw(self) -> bytearray:
        """Serialize profile into 35-byte raw report buffer."""
        self.validate()
        return serialize_profile(self.to_dict(), self.index)

    def diff(self, other: "Profile") -> List[str]:
        """Compute human-readable differences against another profile."""
        diffs = []
        if self.led != other.led:
            diffs.append(f"LED: {self.led} -> {other.led}")
        if self.rate != other.rate:
            diffs.append(f"Rate: {self.rate}Hz -> {other.rate}Hz")
        if self.dpis != other.dpis:
            diffs.append(f"DPIs: {self.dpis} -> {other.dpis}")
        if self.default_dpi != other.default_dpi:
            diffs.append(f"Default DPI: {self.default_dpi} -> {other.default_dpi}")
        if self.buttons != other.buttons:
            for i, (b1, b2) in enumerate(zip(self.buttons, other.buttons)):
                if b1 != b2:
                    diffs.append(f"Button {i}: {b1} -> {b2}")
        if self.raw7 != other.raw7:
            diffs.append(f"Raw7: {self.raw7:#04x} -> {other.raw7:#04x}")
        return diffs


@dataclass
class DeviceSnapshot:
    """Full snapshot of active state and all onboard profiles."""
    active_profile: int
    active_dpi_level: int
    profiles: List[Profile]

    def to_dict(self) -> Dict[str, Any]:
        return {
            "active": {
                "profile": self.active_profile,
                "dpi_level": self.active_dpi_level
            },
            "profiles": [p.to_dict() for p in self.profiles],
            "raw_hex": [p.to_raw().hex() for p in self.profiles]
        }

    def save_json(self, filepath: str):
        with open(filepath, "w", encoding="utf-8") as f:
            json.dump(self.to_dict(), f, indent=2)

    @classmethod
    def load_json(cls, filepath: str) -> "DeviceSnapshot":
        with open(filepath, "r", encoding="utf-8") as f:
            data = json.load(f)

        profiles = []
        # Support both new structured format and legacy backup.json format
        if "profiles" in data and isinstance(data["profiles"][0], str):
            # Legacy hex string list
            for idx, hx in enumerate(data["profiles"]):
                raw = bytearray.fromhex(hx)
                profiles.append(Profile.from_raw(raw, idx))
        else:
            for p in data["profiles"]:
                profiles.append(Profile.from_dict(p))

        active = data.get("active", {})
        return cls(
            active_profile=active.get("profile", 0),
            active_dpi_level=active.get("dpi_level", 0),
            profiles=profiles
        )
