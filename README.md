# Logitech G300s Controller & Firmware Tool

A modern, lightweight, high-performance open-source controller, background daemon, and desktop GUI for the **Logitech G300s Optical Gaming Mouse** (`VID: 0x046D, PID: 0xC246`).

This project completely replaces the legacy 150MB+ Logitech Gaming Software (LGS) with a clean Python package, instant zero-flash-wear RAM controls, advanced macro expansion, and a native system tray application.

---

## ⚡ Highlights & Key Features

* **Complete Onboard Memory Control**: Read, write, backup, and restore all 3 hardware profiles (`0xF3`, `0xF4`, `0xF5`) directly on the mouse's internal flash memory.
* **Flash Lock & Debounce Protection**: Discovered and implemented the exact hardware busy flag (`0xF0` bit 3) used by Logitech's internal firmware to prevent memory corruption and dropped writes.
* **Instant RAM Controls (Zero Flash Wear)**:
  * **Live LED (`0xF1`)**: Set instant 3-bit RGB lighting in RAM without wearing out the onboard flash memory.
  * **Live Polling Rate (`0xF2`)**: Dynamically switch USB report rates (125, 250, 500, 1000 Hz) in RAM in real time.
* **Interactive Desktop GUI & System Tray**:
  * Clean dark-themed desktop interface built with official Logitech SVG/PNG vector graphics.
  * Side LED strips dynamically glow with the selected color preview.
  * Interactive button callout pins for G1–G9 with visual focus.
  * Zero-dependency native Windows taskbar notification tray icon (`Shell_NotifyIconW`).
* **Host Automation Daemon**:
  * **Auto-Profile Switching (`AppWatcher`)**: Automatically detects active foreground games/apps and switches mouse profiles instantly.
  * **Hardware Limit Bypass via High Function Keys (`F13..F24`)**: Overcomes the mouse's 1-key boot-keyboard limit by mapping physical buttons to unallocated keys `F13..F24` and executing multi-step macros, text sequences, and rapid-fire auto-clickers with zero key collisions.
* **Zero-Latency CLI**: Full command-line interface for scripting, headless environments, and rapid tuning.
* **Cross-Platform**: Full Windows support and Linux support via provided udev rules.

---

## 📋 Reverse-Engineered Hardware Protocol

The mouse communicates over USB HID vendor usage page `0xFF80` with report ID `0x0080`:

| Report ID | Size | Type | Function |
| :--- | :--- | :--- | :--- |
| **`0x80`** | 5 B | Input | Asynchronous hardware button notifications (`[0x80, state, 0, 0, action_code]`) |
| **`0xF0`** | 4 B | Feature | Active profile (`bits 7..4`), DPI level (`bits 2..1`), and Flash Busy Lock (`bit 3: 0x08`) |
| **`0xF1`** | 2 B | Feature | Instant Live RAM RGB LED color (`[0xF1, (r) \| (g << 1) \| (b << 2)]`) |
| **`0xF2`** | 2 B | Feature | Instant Live RAM USB Polling Rate (`[0xF2, 0x04 \| rate_code]`) |
| **`0xF3`** | 35 B | Feature | Onboard Profile 0 Flash Memory dump / write |
| **`0xF4`** | 35 B | Feature | Onboard Profile 1 Flash Memory dump / write |
| **`0xF5`** | 35 B | Feature | Onboard Profile 2 Flash Memory dump / write |

### 35-Byte Onboard Profile Layout
```
Offset  0: Report ID (0xF3, 0xF4, 0xF5)
Offset  1: LED color (bit 0=Red, bit 1=Green, bit 2=Blue)
Offset  2: Report rate code (0=1000Hz, 1=125Hz, 2=250Hz, 3=500Hz)
Offset 3-6: 4 DPI levels (value = (byte & 0x7F) * 250; bit 7 indicates default level)
Offset  7: Hardware calibration byte (0x40)
Offset 8-34: 9 buttons (3 bytes each: [action_code, modifier_mask, hid_key_code])
```

---

## 🚀 Installation

### Prerequisites
* Python 3.10 or higher
* Logitech G300s connected via USB

```bash
# Clone the repository
git clone https://github.com/feaz/g300s-mouse-firmware.git
cd "Mouse Firmware"

# Install package in development mode
pip install -e .
```

### Linux Setup (udev)
To access the mouse without root privileges on Linux, copy the udev rule:
```bash
sudo cp udev/99-logitech-g300s.rules /etc/udev/rules.d/
sudo udevadm control --reload-rules && sudo udevadm trigger
```

---

## 🖥️ Usage

### 1. Native Windows 11 Studio GUI (.NET 10 / Direct Win32 HID)
Launch the standalone native Windows 11 Studio application (`native/G300sController/`), featuring an interactive 3D photorealistic hardware visualizer, live `WriteableBitmap` RGB swoosh & honeycomb mesh rendering, pixel-level button hit-testing, live keypress recorder, 4-stage DPI controls, and integrated `WH_KEYBOARD_LL` macro & auto-profile engine:
```bash
# Build & launch directly via .NET 10
dotnet run --project native/G300sController/G300sController.csproj -c Release

# Or launch via the Python CLI entry point (automatically launches G300sController.exe on Windows)
python g300s.py gui
```

### 2. Command Line Interface (CLI)
```bash
# View current status and active profiles
g300s info

# Dump full device memory snapshot to stdout
g300s dump

# Save backup to a JSON file
g300s save backup.json

# Restore configuration from backup
g300s restore backup.json

# Switch active profile (0..2)
g300s profile 1

# Change live LED color in RAM (R G B: 0 or 1) - Zero flash wear!
g300s led-live 0 1 1   # Cyan
g300s led-live 1 0 0   # Red

# Change live polling rate in RAM (125, 250, 500, 1000)
g300s rate-live 1000

# Configure persistent profile settings (writes to onboard flash)
g300s dpi 0 0 1200 --default  # Set Profile 0, Level 0 to 1200 DPI (Default)
g300s btn 0 3 key:ctrl+c      # Set Profile 0, Button 4 (G4) to Ctrl+C
g300s btn 0 4 key:f13         # Set Profile 0, Button 5 (G5) to F13 for daemon macro

# Live RGB lighting effects
g300s ledfx rainbow 10.0
g300s ledfx police 15.0

# Listen to physical hardware events in realtime
g300s monitor 30.0

# Protocol self-test and hardware roundtrip validation
g300s selftest
```

### 3. Background Daemon (Auto-Profile & Macros)
Start the background daemon:
```bash
g300s daemon
```
The daemon creates a default configuration file at `~/.g300s_daemon.json`:
```json
{
  "app_rules": {
    "cs2.exe": 1,
    "valorant-win64-shipping.exe": 1,
    "blender.exe": 2,
    "code.exe": 0
  },
  "default_profile": 0,
  "macros": {
    "f13": {
      "type": "sequence",
      "steps": [
        {"type": "press", "key": "ctrl"},
        {"type": "tap", "key": "c"},
        {"type": "release", "key": "ctrl"}
      ]
    },
    "f14": {
      "type": "text",
      "text": "gg wp!"
    },
    "f15": {
      "type": "rapid_fire",
      "cps": 16,
      "button": "left"
    }
  }
}
```

---

## 🧪 Testing

Run the automated test suite:
```bash
pytest
```
All 12 protocol serialization, keycode parsing, and profile roundtrip tests execute against simulated and authentic hardware memory dumps.

---

## 📄 License
MIT License. Logitech and G300s are registered trademarks of Logitech Inc. This project is an independent reverse engineering and software compatibility effort.
