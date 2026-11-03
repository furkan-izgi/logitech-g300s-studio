"""Main application window for Logitech G300s Controller Desktop GUI."""

import os
import sys
import json
import logging
import tkinter as tk
from tkinter import ttk, messagebox, filedialog
from typing import Optional, Dict, List
from PIL import Image, ImageTk

from ..device import G300sDevice
from ..profiles import Profile
from ..exceptions import G300sError, G300sDeviceNotFoundError, G300sConflictWarning
from .mouse_view import MouseCanvas
from .led_panel import LedPanel
from .dpi_panel import DpiPanel
from .button_panel import ButtonPanel
from .tray import NativeTrayIcon

logger = logging.getLogger("g300s.gui")


class G300sMainWindow:
    """Master desktop application window for Logitech G300s configuration."""

    def __init__(self, root: tk.Tk):
        self.root = root
        self.root.title("Logitech G300s Gaming Mouse Controller")
        self.root.geometry("1060x720")
        self.root.minsize(980, 680)
        self.root.configure(bg="#181818")

        self.assets_dir = os.path.join(os.path.dirname(__file__), "assets")

        # Set window icon
        icon_path = os.path.join(self.assets_dir, "G300s_thumbnail_normal.png")
        if os.path.exists(icon_path):
            try:
                self._icon_img = ImageTk.PhotoImage(Image.open(icon_path))
                self.root.iconphoto(False, self._icon_img)
            except Exception as e:
                logger.debug("Could not load icon: %s", e)

        # State
        self.device: Optional[G300sDevice] = None
        self.profiles: List[Profile] = []
        self.active_profile_idx: int = 0
        self.active_dpi_level: int = 1
        self.is_connected = False
        self.has_unsaved_changes = False

        # Native Windows tray
        self.tray = NativeTrayIcon(
            tooltip="Logitech G300s Controller",
            on_left_click=self._toggle_window_visibility,
            on_profile_select=self._on_tray_profile_select,
            on_exit=self._on_app_exit
        )
        self.tray.start()

        self._build_layout()
        self._connect_and_load_device()

        # Handle window close (minimize to tray or confirm)
        self.root.protocol("WM_DELETE_WINDOW", self._on_window_close)

    def _build_layout(self):
        # 1. Header Bar
        header = tk.Frame(self.root, bg="#202020", height=60, padx=16, pady=8)
        header.pack(fill=tk.X, side=tk.TOP)

        title_frame = tk.Frame(header, bg="#202020")
        title_frame.pack(side=tk.LEFT)

        lbl_title = tk.Label(
            title_frame,
            text="LOGITECH G300s",
            bg="#202020",
            fg="#00d2ff",
            font=("Segoe UI", 14, "bold")
        )
        lbl_title.pack(anchor=tk.W)

        lbl_sub = tk.Label(
            title_frame,
            text="Onboard Flash Memory & Realtime Controller (046D:C246)",
            bg="#202020",
            fg="#888888",
            font=("Segoe UI", 8)
        )
        lbl_sub.pack(anchor=tk.W)

        # Hardware connection badge
        self.lbl_status = tk.Label(
            header,
            text="● Connecting...",
            bg="#202020",
            fg="#e0a800",
            font=("Segoe UI", 9, "bold"),
            padx=10
        )
        self.lbl_status.pack(side=tk.RIGHT)

        # Profile Selector Bar
        prof_bar = tk.Frame(header, bg="#202020")
        prof_bar.pack(side=tk.RIGHT, padx=20)

        self.btn_profiles = []
        for i in range(3):
            btn = tk.Button(
                prof_bar,
                text=f"Profile {i + 1}",
                bg="#2e2e2e",
                fg="#ffffff",
                activebackground="#00d2ff",
                activeforeground="#000000",
                font=("Segoe UI", 9, "bold"),
                padx=12,
                pady=4,
                relief=tk.FLAT,
                bd=0,
                command=lambda idx=i: self._select_profile(idx)
            )
            btn.pack(side=tk.LEFT, padx=4)
            self.btn_profiles.append(btn)

        # 2. Main Body (Panels)
        body = tk.Frame(self.root, bg="#181818", padx=16, pady=12)
        body.pack(fill=tk.BOTH, expand=True)

        # Left Column: Mouse Visual View
        left_col = tk.Frame(body, bg="#181818")
        left_col.pack(side=tk.LEFT, fill=tk.BOTH, expand=True, padx=(0, 10))

        self.mouse_view = MouseCanvas(
            left_col,
            assets_dir=self.assets_dir,
            scale=0.62,
            on_button_click=self._on_mouse_button_clicked
        )
        self.mouse_view.pack(fill=tk.BOTH, expand=True)

        # Right Column: Settings Notebook (Tabs)
        right_col = tk.Frame(body, bg="#181818", width=460)
        right_col.pack(side=tk.RIGHT, fill=tk.BOTH, expand=False, padx=(10, 0))
        right_col.pack_propagate(False)

        # Notebook tabs
        self.btn_panel = ButtonPanel(
            right_col,
            on_action_changed=self._on_action_changed,
            on_button_focused=self._on_button_focused
        )
        self.btn_panel.pack(fill=tk.X, pady=(0, 8))

        self.led_panel = LedPanel(
            right_col,
            on_color_selected=self._on_led_color_selected,
            on_live_preview=self._on_led_live_preview
        )
        self.led_panel.pack(fill=tk.X, pady=(0, 8))

        self.dpi_panel = DpiPanel(
            right_col,
            on_rate_changed=self._on_rate_changed,
            on_dpis_changed=self._on_dpis_changed
        )
        self.dpi_panel.pack(fill=tk.X)

        # 3. Footer Bar (Actions & Save)
        footer = tk.Frame(self.root, bg="#202020", height=50, padx=16, pady=10)
        footer.pack(fill=tk.X, side=tk.BOTTOM)

        btn_save = tk.Button(
            footer,
            text="💾 Save to Mouse Memory",
            bg="#00d2ff",
            fg="#000000",
            activebackground="#00a8cc",
            activeforeground="#ffffff",
            font=("Segoe UI", 9, "bold"),
            padx=14,
            pady=4,
            relief=tk.FLAT,
            bd=0,
            command=self._save_to_hardware
        )
        btn_save.pack(side=tk.RIGHT, padx=4)

        btn_reload = tk.Button(
            footer,
            text="🔄 Reload from Mouse",
            bg="#333333",
            fg="#ffffff",
            font=("Segoe UI", 9),
            padx=10,
            pady=4,
            relief=tk.FLAT,
            bd=0,
            command=self._connect_and_load_device
        )
        btn_reload.pack(side=tk.RIGHT, padx=4)

        btn_export = tk.Button(
            footer,
            text="Export JSON",
            bg="#333333",
            fg="#cccccc",
            font=("Segoe UI", 8),
            padx=8,
            pady=4,
            relief=tk.FLAT,
            bd=0,
            command=self._export_json
        )
        btn_export.pack(side=tk.LEFT, padx=4)

        btn_import = tk.Button(
            footer,
            text="Import JSON",
            bg="#333333",
            fg="#cccccc",
            font=("Segoe UI", 8),
            padx=8,
            pady=4,
            relief=tk.FLAT,
            bd=0,
            command=self._import_json
        )
        btn_import.pack(side=tk.LEFT, padx=4)

    def _connect_and_load_device(self):
        """Connect to G300s hardware and read active state and all 3 profiles."""
        try:
            if not self.device:
                self.device = G300sDevice()
                self.device.open()

            state = self.device.get_active_state()
            self.active_profile_idx = state["profile"]
            self.active_dpi_level = state["dpi_level"]

            self.profiles = []
            for i in range(3):
                raw = self.device.get_profile_raw(i)
                self.profiles.append(Profile.from_raw(raw, i))

            self.is_connected = True
            conflict_msg = " (LGS Conflict Active)" if self.device.is_lgs_running() else ""
            self.lbl_status.configure(
                text=f"● G300s Connected{conflict_msg}",
                fg="#ffaa00" if conflict_msg else "#2aff4a"
            )

            self._select_profile(self.active_profile_idx, notify_device=False)

        except G300sDeviceNotFoundError:
            self.is_connected = False
            self.lbl_status.configure(text="● Mouse Not Found", fg="#ff4444")
            # Load default empty profiles for offline preview
            if not self.profiles:
                self.profiles = [Profile(i, 500, (1, 0, 0), [500, 1000, 1500, 2500], 1, {}) for i in range(3)]
                self._select_profile(0, notify_device=False)
        except Exception as e:
            logger.error("Failed to read device: %s", e)
            self.lbl_status.configure(text=f"● Error: {e}", fg="#ff4444")

    def _select_profile(self, profile_idx: int, notify_device: bool = True):
        """Switch active profile in UI and on hardware."""
        self.active_profile_idx = profile_idx

        # Update profile buttons in header
        for idx, btn in enumerate(self.btn_profiles):
            if idx == profile_idx:
                btn.configure(bg="#00d2ff", fg="#000000")
            else:
                btn.configure(bg="#2e2e2e", fg="#ffffff")

        # Load active profile data into panels
        if profile_idx < len(self.profiles):
            p = self.profiles[profile_idx]

            # 1. LED panel & Canvas glow
            self.led_panel.set_color(*p.led)
            self.mouse_view.set_led_color(*p.led)

            # 2. DPI & Polling rate panel
            self.dpi_panel.set_values(p.rate, p.dpis, p.default_dpi)

            # 3. Button assignments
            self.btn_panel.set_actions(p.buttons)
            self.mouse_view.set_button_actions(p.buttons)

        # Notify hardware if requested
        if notify_device and self.is_connected and self.device:
            try:
                self.device.set_active_profile(profile_idx)
            except Exception as e:
                logger.debug("Failed to set active profile on mouse: %s", e)

    def _on_mouse_button_clicked(self, btn_idx: int):
        self.btn_panel.focus_button(btn_idx)

    def _on_button_focused(self, btn_idx: int):
        self.mouse_view.set_selected_button(btn_idx)

    def _on_action_changed(self, btn_idx: int, action_code: str):
        if self.active_profile_idx < len(self.profiles):
            self.profiles[self.active_profile_idx].buttons[btn_idx] = action_code
            self.mouse_view.set_button_actions(self.profiles[self.active_profile_idx].buttons)
            self.has_unsaved_changes = True

    def _on_led_color_selected(self, r: int, g: int, b: int):
        if self.active_profile_idx < len(self.profiles):
            self.profiles[self.active_profile_idx].led = (r, g, b)
            self.mouse_view.set_led_color(r, g, b)
            self.has_unsaved_changes = True

    def _on_led_live_preview(self, r: int, g: int, b: int):
        if self.is_connected and self.device:
            try:
                self.device.set_live_led(r, g, b)
            except Exception as e:
                logger.debug("Live LED preview failed: %s", e)

    def _on_rate_changed(self, hz: int):
        if self.active_profile_idx < len(self.profiles):
            self.profiles[self.active_profile_idx].rate = hz
            self.has_unsaved_changes = True
        if self.is_connected and self.device:
            try:
                self.device.set_live_rate(hz)
            except Exception as e:
                logger.debug("Live rate preview failed: %s", e)

    def _on_dpis_changed(self, dpis: List[int], default_dpi_idx: int):
        if self.active_profile_idx < len(self.profiles):
            self.profiles[self.active_profile_idx].dpis = dpis.copy()
            self.profiles[self.active_profile_idx].default_dpi = default_dpi_idx
            self.has_unsaved_changes = True

    def _save_to_hardware(self):
        """Write all 3 profile configurations to onboard flash."""
        if not self.is_connected or not self.device:
            messagebox.showwarning("Device Not Connected", "Cannot save to hardware: G300s is not connected.")
            return

        try:
            # Check flash busy
            if not self.device.wait_not_busy(timeout_sec=2.0):
                messagebox.showerror("Flash Busy", "Mouse onboard flash controller is busy. Please try again.")
                return

            for i in range(min(3, len(self.profiles))):
                raw = self.profiles[i].to_raw()
                self.device.set_profile_raw(i, raw)

            self.has_unsaved_changes = False
            messagebox.showinfo("Saved", "All 3 profiles saved to Logitech G300s onboard memory successfully!")

        except Exception as e:
            logger.error("Save failed: %s", e)
            messagebox.showerror("Save Error", f"Failed to write profiles to hardware:\n{e}")

    def _export_json(self):
        path = filedialog.asksaveasfilename(
            defaultextension=".json",
            filetypes=[("JSON Files", "*.json")],
            title="Export G300s Profiles"
        )
        if not path:
            return
        try:
            data = {
                "active_profile": self.active_profile_idx,
                "profiles": [p.to_dict() for p in self.profiles]
            }
            with open(path, "w", encoding="utf-8") as f:
                json.dump(data, f, indent=2)
            messagebox.showinfo("Export Successful", f"Profiles exported to:\n{path}")
        except Exception as e:
            messagebox.showerror("Export Failed", str(e))

    def _import_json(self):
        path = filedialog.askopenfilename(
            filetypes=[("JSON Files", "*.json")],
            title="Import G300s Profiles"
        )
        if not path or not os.path.exists(path):
            return
        try:
            with open(path, "r", encoding="utf-8") as f:
                data = json.load(f)
            self.profiles = [Profile.from_dict(d) for d in data.get("profiles", [])]
            self._select_profile(self.active_profile_idx, notify_device=False)
            self.has_unsaved_changes = True
            messagebox.showinfo("Import Successful", "Profiles imported into editor. Click 'Save to Mouse Memory' to flash.")
        except Exception as e:
            messagebox.showerror("Import Failed", str(e))

    def _toggle_window_visibility(self):
        if self.root.state() == "withdrawn":
            self.root.deiconify()
            self.root.lift()
        else:
            self.root.withdraw()

    def _on_tray_profile_select(self, profile_idx: int):
        self._select_profile(profile_idx, notify_device=True)

    def _on_window_close(self):
        """Minimize to tray instead of abruptly closing."""
        self.root.withdraw()

    def _on_app_exit(self):
        self.tray.stop()
        if self.device:
            try:
                self.device.close()
            except Exception:
                pass
        self.root.quit()
        sys.exit(0)
