"""LED lighting configuration panel for hardware palette and live effects."""

import tkinter as tk
from tkinter import ttk
from typing import Tuple, Callable, Optional

# Logitech G300s 8-color palette definition
PALETTE_ITEMS = [
    ("Off", (0, 0, 0), "#181818", "#555555"),
    ("Red", (1, 0, 0), "#ff2a2a", "#ffffff"),
    ("Yellow", (1, 1, 0), "#ffd000", "#000000"),
    ("Green", (0, 1, 0), "#2aff4a", "#000000"),
    ("Cyan", (0, 1, 1), "#00e5ff", "#000000"),
    ("Blue", (0, 0, 1), "#2a62ff", "#ffffff"),
    ("Magenta", (1, 0, 1), "#ff2ae5", "#ffffff"),
    ("White", (1, 1, 1), "#f0f0f0", "#000000")
]


class LedPanel(tk.LabelFrame):
    """Configuration panel for 3-bit RGB LED palette and live preview animations."""

    def __init__(
        self,
        parent,
        on_color_selected: Optional[Callable[[int, int, int], None]] = None,
        on_live_preview: Optional[Callable[[int, int, int], None]] = None,
        **kwargs
    ):
        super().__init__(
            parent,
            text=" Lighting & LED (Onboard & RAM) ",
            bg="#242424",
            fg="#00d2ff",
            font=("Segoe UI", 10, "bold"),
            padx=12,
            pady=12,
            **kwargs
        )
        self.on_color_selected = on_color_selected
        self.on_live_preview = on_live_preview

        self._current_rgb: Tuple[int, int, int] = (0, 1, 1)
        self._live_preview_var = tk.BooleanVar(value=True)
        self._swatches = []

        self._build_ui()

    def _build_ui(self):
        # Description
        lbl = tk.Label(
            self,
            text="Select persistent profile side LED color (3-bit onboard palette):",
            bg="#242424",
            fg="#aaaaaa",
            font=("Segoe UI", 9)
        )
        lbl.pack(anchor=tk.W, pady=(0, 8))

        # Swatch container grid
        swatch_frame = tk.Frame(self, bg="#242424")
        swatch_frame.pack(anchor=tk.W, fill=tk.X, pady=(0, 10))

        for idx, (name, rgb, hex_color, text_color) in enumerate(PALETTE_ITEMS):
            frame = tk.Frame(swatch_frame, bg="#242424", padx=2, pady=2)
            frame.grid(row=0, column=idx, padx=4)

            btn = tk.Button(
                frame,
                text=name,
                bg=hex_color,
                fg=text_color,
                activebackground=hex_color,
                activeforeground=text_color,
                font=("Segoe UI", 8, "bold"),
                width=6,
                height=2,
                relief=tk.FLAT,
                bd=0,
                command=lambda r=rgb[0], g=rgb[1], b=rgb[2]: self._select_color(r, g, b)
            )
            btn.pack()
            self._swatches.append((rgb, frame, btn))

        # Live RAM preview toggle
        chk = tk.Checkbutton(
            self,
            text="Live RAM Preview (Instantly lights up physical mouse without flash wear)",
            variable=self._live_preview_var,
            bg="#242424",
            fg="#cccccc",
            selectcolor="#181818",
            activebackground="#242424",
            activeforeground="#ffffff",
            font=("Segoe UI", 9)
        )
        chk.pack(anchor=tk.W, pady=(4, 0))

        self._update_swatch_highlights()

    def set_color(self, r: int, g: int, b: int):
        """Update selected color in UI."""
        self._current_rgb = (1 if r else 0, 1 if g else 0, 1 if b else 0)
        self._update_swatch_highlights()

    def get_color(self) -> Tuple[int, int, int]:
        return self._current_rgb

    def _select_color(self, r: int, g: int, b: int):
        self.set_color(r, g, b)
        if self._live_preview_var.get() and self.on_live_preview:
            self.on_live_preview(r, g, b)
        if self.on_color_selected:
            self.on_color_selected(r, g, b)

    def _update_swatch_highlights(self):
        for rgb, frame, btn in self._swatches:
            if rgb == self._current_rgb:
                frame.configure(bg="#ffffff")
                btn.configure(relief=tk.SOLID, bd=2)
            else:
                frame.configure(bg="#242424")
                btn.configure(relief=tk.FLAT, bd=0)
