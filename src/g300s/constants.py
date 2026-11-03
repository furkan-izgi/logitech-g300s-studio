"""Constants and hardware mappings for the Logitech G300s optical gaming mouse.
Device: USB\\VID_046D&PID_C246
"""

VID = 0x046D
PID = 0xC246
USAGE_PAGE = 0xFF80
USAGE = 0x0080

# Report IDs
REPORT_INPUT_EVENT = 0x80     # 5 bytes: async hardware button event notifications
REPORT_ACTIVE_STATE = 0xF0    # 4 bytes: active profile, dpi level, flash busy flag, config mode
REPORT_LIVE_LED = 0xF1        # 2 bytes: instant RAM RGB LED
REPORT_LIVE_RATE = 0xF2       # 2 bytes: instant RAM USB polling rate (0x04 | rate_code)
REPORT_PROFILE_0 = 0xF3       # 35 bytes: onboard flash profile 0
REPORT_PROFILE_1 = 0xF4       # 35 bytes: onboard flash profile 1
REPORT_PROFILE_2 = 0xF5       # 35 bytes: onboard flash profile 2

PROFILE_REPORT_IDS = (REPORT_PROFILE_0, REPORT_PROFILE_1, REPORT_PROFILE_2)
PROFILE_COUNT = 3
BUTTON_COUNT = 9
DPI_LEVEL_COUNT = 4

# Polling rate mappings: code -> Hz
RATES = {
    0: 1000,
    1: 125,
    2: 250,
    3: 500
}
RATE_TO_CODE = {v: k for k, v in RATES.items()}

# Special button action codes
SPECIAL_ACTIONS = {
    0x0A: "dpi-up",
    0x0B: "dpi-down",
    0x0C: "dpi-cycle",
    0x0D: "profile-cycle",
    0x0E: "dpi-alternate",    # sniper button (hold)
    0x0F: "dpi-default"
}
ACTION_TO_CODE = {v: k for k, v in SPECIAL_ACTIONS.items()}

# 3-bit RGB LED cycle (1 bit per channel: R, G, B)
LED_PALETTE = {
    "off": (0, 0, 0),
    "red": (1, 0, 0),
    "green": (0, 1, 0),
    "blue": (0, 0, 1),
    "yellow": (1, 1, 0),
    "cyan": (0, 1, 1),
    "magenta": (1, 0, 1),
    "white": (1, 1, 1)
}

LED_CYCLE = [
    (1, 0, 0),  # Red
    (1, 1, 0),  # Yellow
    (0, 1, 0),  # Green
    (0, 1, 1),  # Cyan
    (0, 0, 1),  # Blue
    (1, 0, 1),  # Magenta
    (1, 1, 1),  # White
    (0, 0, 0),  # Off
]

# Flash write guard timing (measured against LGS LCore.exe)
FLASH_BUSY_TIMEOUT_SEC = 2.5
FLASH_SETTLE_POLL_INTERVAL_SEC = 0.05
FLASH_SETTLE_MAX_ATTEMPTS = 40
DEBOUNCE_RETRY_INTERVAL_SEC = 0.5
