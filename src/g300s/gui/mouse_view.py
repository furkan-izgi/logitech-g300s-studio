"""Interactive visual canvas rendering the G300s mouse with live LED glow and button pins."""

import os
import tkinter as tk
from tkinter import ttk
from typing import Dict, Tuple, Optional, Callable
from PIL import Image, ImageTk

# Base unscaled coordinates for button hotspots on G300sDevice_logitech.png
BUTTON_HOTSPOTS = {
    0: {"name": "G1 (Left Click)", "x": 268, "y": 139, "side": "left"},
    1: {"name": "G2 (Right Click)", "x": 318, "y": 39, "side": "right"},
    2: {"name": "G3 (Middle Click)", "x": 324, "y": 94, "side": "top"},
    3: {"name": "G4 (Index Up)", "x": 203, "y": 178, "side": "left"},
    4: {"name": "G5 (Index Down)", "x": 114, "y": 151, "side": "left"},
    5: {"name": "G6 (Ring Down)", "x": 488, "y": 64, "side": "right"},
    6: {"name": "G7 (Ring Up)", "x": 398, "y": 44, "side": "right"},
    7: {"name": "G8 (Center Up)", "x": 448, "y": 135, "side": "bottom"},
    8: {"name": "G9 (Center Down)", "x": 378, "y": 112, "side": "bottom"},
}

RGB_PALETTE_COLORS = {
    (0, 0, 0): (20, 20, 20),
    (1, 0, 0): (255, 40, 40),
    (0, 1, 0): (40, 255, 60),
    (0, 0, 1): (40, 120, 255),
    (1, 1, 0): (255, 230, 40),
    (0, 1, 1): (40, 230, 255),
    (1, 0, 1): (255, 40, 230),
    (1, 1, 1): (255, 255, 255)
}


class MouseCanvas(tk.Frame):
    """Canvas widget presenting the mouse graphic, live LED glow, and interactive button overlays."""

    def __init__(
        self,
        parent,
        assets_dir: str,
        scale: float = 0.65,
        on_button_click: Optional[Callable[[int], None]] = None,
        **kwargs
    ):
        super().__init__(parent, bg="#1e1e1e", **kwargs)
        self.assets_dir = assets_dir
        self.scale = scale
        self.on_button_click = on_button_click

        self._base_img_path = os.path.join(assets_dir, "G300sDevice_logitech.png")
        self._swoosh_path = os.path.join(assets_dir, "G300sDevice_swoosh.png")

        self._raw_base = Image.open(self._base_img_path)
        self._raw_swoosh = Image.open(self._swoosh_path)

        self.canvas_w = int(self._raw_base.width * scale)
        self.canvas_h = int(self._raw_base.height * scale)

        self.canvas = tk.Canvas(
            self,
            width=self.canvas_w,
            height=self.canvas_h,
            bg="#1e1e1e",
            highlightthickness=0
        )
        self.canvas.pack(fill=tk.BOTH, expand=True)

        self._current_rgb: Tuple[int, int, int] = (0, 1, 1)  # Default cyan
        self._button_actions: Dict[int, str] = {}
        self._selected_button: Optional[int] = None
        self._tk_image: Optional[ImageTk.PhotoImage] = None

        self._render_composited_mouse()
        self._draw_overlay_elements()

    def set_led_color(self, r: int, g: int, b: int):
        """Update live LED swoosh lighting color on mouse image."""
        self._current_rgb = (1 if r else 0, 1 if g else 0, 1 if b else 0)
        self._render_composited_mouse()
        self._draw_overlay_elements()

    def set_button_actions(self, actions):
        """Update displayed action names on button callout pins."""
        if isinstance(actions, list):
            self._button_actions = {i: actions[i] for i in range(len(actions))}
        elif isinstance(actions, dict):
            self._button_actions = actions.copy()
        self._draw_overlay_elements()

    def set_selected_button(self, btn_idx: Optional[int]):
        """Highlight active selected button."""
        self._selected_button = btn_idx
        self._draw_overlay_elements()

    def _render_composited_mouse(self):
        """Tint the swoosh graphic with active RGB and composite onto base mouse image."""
        base = self._raw_base.copy()
        swoosh = self._raw_swoosh.copy()

        r_bit, g_bit, b_bit = self._current_rgb
        target_rgb = RGB_PALETTE_COLORS.get((r_bit, g_bit, b_bit), (40, 230, 255))

        r_ch, g_ch, b_ch, alpha = swoosh.split()
        r_tinted = Image.eval(r_ch, lambda v: int(v * (target_rgb[0] / 255.0)))
        g_tinted = Image.eval(g_ch, lambda v: int(v * (target_rgb[1] / 255.0)))
        b_tinted = Image.eval(b_ch, lambda v: int(v * (target_rgb[2] / 255.0)))
        tinted_swoosh = Image.merge("RGBA", (r_tinted, g_tinted, b_tinted, alpha))

        # Paste swoosh at manifest position (268, 250)
        base.paste(tinted_swoosh, (268, 250), tinted_swoosh)

        scaled = base.resize((self.canvas_w, self.canvas_h), Image.Resampling.LANCZOS)
        self._tk_image = ImageTk.PhotoImage(scaled)

        self.canvas.delete("mouse_base")
        self.canvas.create_image(0, 0, anchor=tk.NW, image=self._tk_image, tags="mouse_base")

    def _draw_overlay_elements(self):
        """Draw interactive button pins, badges, and connection lines."""
        self.canvas.delete("pin")

        for idx, info in BUTTON_HOTSPOTS.items():
            cx = int(info["x"] * self.scale)
            cy = int(info["y"] * self.scale)

            is_sel = (self._selected_button == idx)
            pin_color = "#00d2ff" if is_sel else "#3a3a3a"
            text_color = "#ffffff"
            outline_color = "#ffffff" if is_sel else "#606060"
            radius = 12 if is_sel else 10

            tag = f"btn_pin_{idx}"

            # Glow circle if selected
            if is_sel:
                self.canvas.create_oval(
                    cx - radius - 4, cy - radius - 4,
                    cx + radius + 4, cy + radius + 4,
                    fill="#0077b6", outline="", tags=("pin", tag)
                )

            # Center button pin circle
            self.canvas.create_oval(
                cx - radius, cy - radius,
                cx + radius, cy + radius,
                fill=pin_color, outline=outline_color, width=2 if is_sel else 1,
                tags=("pin", tag)
            )

            # Button number text (1..9)
            self.canvas.create_text(
                cx, cy,
                text=str(idx + 1),
                fill=text_color,
                font=("Segoe UI", 9, "bold"),
                tags=("pin", tag)
            )

            # Click handler
            self.canvas.tag_bind(tag, "<Button-1>", lambda e, b=idx: self._handle_click(b))

    def _handle_click(self, btn_idx: int):
        self.set_selected_button(btn_idx)
        if self.on_button_click:
            self.on_button_click(btn_idx)
