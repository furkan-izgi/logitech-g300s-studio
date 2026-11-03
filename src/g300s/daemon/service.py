"""G300s Host Daemon coordinating auto profile switching, macro execution, and live RGB."""

import os
import json
import time
import logging
import threading
from typing import Dict, Any, Optional

from ..device import G300sDevice
from ..exceptions import G300sDeviceNotFoundError, G300sError
from .app_watcher import AppWatcher
from .macro_engine import MacroEngine
from .input_hook import LowLevelKeyHook
from .rgb_effects import RgbEffectsEngine

logger = logging.getLogger("g300s.daemon.service")

DEFAULT_CONFIG_PATH = os.path.join(os.path.expanduser("~"), ".g300s_daemon.json")

DEFAULT_CONFIG: Dict[str, Any] = {
    "app_rules": {
        "cs2.exe": 1,
        "valorant-win64-shipping.exe": 1,
        "blender.exe": 2,
        "code.exe": 0,
        "chrome.exe": 0
    },
    "default_profile": 0,
    "app_watcher_interval_sec": 0.5,
    "rgb_effect": {
        "enabled": False,
        "effect": "cycle",
        "speed_ms": 400,
        "color": 1
    },
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
            "type": "sequence",
            "steps": [
                {"type": "press", "key": "ctrl"},
                {"type": "tap", "key": "v"},
                {"type": "release", "key": "ctrl"}
            ]
        },
        "f15": {
            "type": "text",
            "text": "gg wp!"
        },
        "f16": {
            "type": "rapid_fire",
            "cps": 15,
            "button": "left"
        }
    }
}


class DaemonService:
    """Master background daemon for Logitech G300s."""

    def __init__(self, config_path: Optional[str] = None):
        self.config_path = config_path or DEFAULT_CONFIG_PATH
        self.config = self._load_config()

        self.device: Optional[G300sDevice] = None
        self.macro_engine = MacroEngine()
        self.app_watcher: Optional[AppWatcher] = None
        self.key_hook: Optional[LowLevelKeyHook] = None
        self.rgb_engine: Optional[RgbEffectsEngine] = None

        self._running = False
        self._device_lock = threading.Lock()
        self._watcher_thread: Optional[threading.Thread] = None
        self._active_rapid_fires = set()

    def _load_config(self) -> Dict[str, Any]:
        if os.path.exists(self.config_path):
            try:
                with open(self.config_path, "r", encoding="utf-8") as f:
                    data = json.load(f)
                    logger.info("Loaded daemon configuration from '%s'", self.config_path)
                    return data
            except Exception as e:
                logger.warning("Failed to load config '%s': %s (using defaults)", self.config_path, e)

        # Write default template if none exists
        try:
            with open(self.config_path, "w", encoding="utf-8") as f:
                json.dump(DEFAULT_CONFIG, f, indent=2)
            logger.info("Created default daemon configuration at '%s'", self.config_path)
        except Exception as e:
            logger.debug("Could not write default config: %s", e)

        return DEFAULT_CONFIG.copy()

    def save_config(self):
        """Save current configuration to disk."""
        try:
            with open(self.config_path, "w", encoding="utf-8") as f:
                json.dump(self.config, f, indent=2)
            logger.info("Saved daemon configuration to '%s'", self.config_path)
        except Exception as e:
            logger.error("Failed to save daemon config: %s", e)

    def _ensure_device_connected(self) -> bool:
        with self._device_lock:
            if self.device is not None:
                return True
            try:
                self.device = G300sDevice()
                self.device.open()
                logger.info("Connected to Logitech G300s hardware.")
                return True
            except G300sDeviceNotFoundError:
                return False
            except Exception as e:
                logger.debug("Device connection attempt failed: %s", e)
                return False

    def _write_live_color_safe(self, r: int, g: int, b: int):
        with self._device_lock:
            if self.device:
                try:
                    self.device.set_live_led(r, g, b)
                except Exception as e:
                    logger.debug("Failed to set live LED: %s", e)

    def _on_app_switch(self, profile_idx: int, exe_name: str):
        """Triggered when active foreground window changes to an app with a profile rule."""
        logger.info("Auto-switch triggered by '%s' -> profile %d", exe_name, profile_idx)
        with self._device_lock:
            if self.device:
                try:
                    self.device.set_active_profile(profile_idx)
                except Exception as e:
                    logger.warning("Failed to switch profile on device: %s", e)

    def _on_key_down(self, vk_code: int, key_name: str) -> bool:
        """Handle macro trigger key down (e.g. F13..F24)."""
        macros = self.config.get("macros", {})
        macro = macros.get(key_name.lower())
        if not macro:
            return False

        mtype = macro.get("type", "").lower()
        if mtype == "sequence":
            steps = macro.get("steps", [])
            threading.Thread(target=self.macro_engine.execute_macro_sequence, args=(steps,), daemon=True).start()
            return True

        elif mtype == "text":
            text = macro.get("text", "")
            threading.Thread(target=self.macro_engine.type_unicode_text, args=(text,), daemon=True).start()
            return True

        elif mtype == "rapid_fire":
            cps = macro.get("cps", 15)
            btn = macro.get("button", "left")
            self._active_rapid_fires.add(key_name.lower())
            self.macro_engine.start_rapid_fire(key_name.lower(), cps=cps, button=btn)
            return True

        return False

    def _on_key_up(self, vk_code: int, key_name: str):
        """Handle macro trigger key up (stop rapid fire if active)."""
        k = key_name.lower()
        if k in self._active_rapid_fires:
            self.macro_engine.stop_rapid_fire(k)
            self._active_rapid_fires.remove(k)

    def start(self):
        """Start all daemon services."""
        if self._running:
            return

        self._running = True
        logger.info("Starting G300s Daemon Service...")

        # 1. Connect to hardware
        self._ensure_device_connected()

        # 2. RGB effects engine
        self.rgb_engine = RgbEffectsEngine(self._write_live_color_safe)
        rgb_cfg = self.config.get("rgb_effect", {})
        if rgb_cfg.get("enabled", False):
            color = rgb_cfg.get("color", (1, 0, 0))
            if isinstance(color, int):
                color = (color & 1, (color >> 1) & 1, (color >> 2) & 1)
            elif isinstance(color, list):
                color = tuple(color[:3])
            self.rgb_engine.set_effect(
                rgb_cfg.get("effect", "cycle"),
                speed_ms=rgb_cfg.get("speed_ms", 400),
                base_rgb=color
            )

        # 3. App Watcher for auto-profile switching
        app_rules = self.config.get("app_rules", {})
        default_prof = self.config.get("default_profile", 0)
        poll_interval = self.config.get("app_watcher_interval_sec", 0.5)

        self.app_watcher = AppWatcher(
            app_rules=app_rules,
            default_profile=default_prof,
            switch_callback=self._on_app_switch,
            poll_interval=poll_interval
        )
        self._watcher_thread = threading.Thread(target=self.app_watcher.run_loop, daemon=True, name="G300s_AppWatcher")
        self._watcher_thread.start()

        # 4. Low-level keyboard hook for F13-F24 macro interception
        self.key_hook = LowLevelKeyHook(
            on_key_down=self._on_key_down,
            on_key_up=self._on_key_up
        )
        self.key_hook.start()

        logger.info("G300s Daemon Service is running. Press Ctrl+C to terminate.")

    def run_forever(self):
        """Run blocking until interrupted."""
        self.start()
        try:
            while self._running:
                # Periodic health check & reconnect
                if not self._ensure_device_connected():
                    time.sleep(2.0)
                else:
                    time.sleep(1.0)
        except KeyboardInterrupt:
            logger.info("Received interrupt signal.")
        finally:
            self.stop()

    def stop(self):
        """Stop all daemon services gracefully."""
        if not self._running:
            return

        logger.info("Stopping G300s Daemon Service...")
        self._running = False

        if self.app_watcher:
            self.app_watcher.stop()
            self.app_watcher = None

        if self.key_hook:
            self.key_hook.stop()
            self.key_hook = None

        if self.rgb_engine:
            self.rgb_engine.stop()
            self.rgb_engine = None

        with self._device_lock:
            if self.device:
                try:
                    self.device.close()
                except Exception:
                    pass
                self.device = None

        logger.info("G300s Daemon Service stopped.")
