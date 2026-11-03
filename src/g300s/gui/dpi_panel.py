"""DPI levels and USB polling rate configuration panel."""

import tkinter as tk
from tkinter import ttk
from typing import List, Callable, Optional


class DpiPanel(tk.LabelFrame):
    """Configuration panel for 4 onboard DPI levels and USB report rate."""

    def __init__(
        self,
        parent,
        on_rate_changed: Optional[Callable[[int], None]] = None,
        on_dpis_changed: Optional[Callable[[List[int], int], None]] = None,
        **kwargs
    ):
        super().__init__(
            parent,
            text=" Performance & DPI Levels ",
            bg="#242424",
            fg="#00d2ff",
            font=("Segoe UI", 10, "bold"),
            padx=12,
            pady=12,
            **kwargs
        )
        self.on_rate_changed = on_rate_changed
        self.on_dpis_changed = on_dpis_changed

        self._rate_var = tk.IntVar(value=500)
        self._default_dpi_var = tk.IntVar(value=1)
        self._dpi_vars = [tk.IntVar(value=500), tk.IntVar(value=1000), tk.IntVar(value=1500), tk.IntVar(value=2500)]
        self._dpi_labels: List[tk.Label] = []
        self._sliders: List[tk.Scale] = []

        self._build_ui()

    def _build_ui(self):
        # 1. Report Rate
        rate_frame = tk.Frame(self, bg="#242424")
        rate_frame.pack(fill=tk.X, pady=(0, 12))

        lbl_rate = tk.Label(
            rate_frame,
            text="USB Report Rate (Polling Rate):",
            bg="#242424",
            fg="#cccccc",
            font=("Segoe UI", 9, "bold")
        )
        lbl_rate.pack(side=tk.LEFT, padx=(0, 10))

        for rate in [125, 250, 500, 1000]:
            rb = tk.Radiobutton(
                rate_frame,
                text=f"{rate} Hz",
                variable=self._rate_var,
                value=rate,
                bg="#242424",
                fg="#ffffff",
                selectcolor="#181818",
                activebackground="#242424",
                activeforeground="#00d2ff",
                font=("Segoe UI", 9),
                command=self._handle_rate_change
            )
            rb.pack(side=tk.LEFT, padx=6)

        # Separator line
        sep = tk.Frame(self, height=1, bg="#383838")
        sep.pack(fill=tk.X, pady=(0, 10))

        # 2. DPI Levels
        lbl_dpi_head = tk.Label(
            self,
            text="DPI Sensitivity Levels (250 to 2500 DPI, Step 250):",
            bg="#242424",
            fg="#cccccc",
            font=("Segoe UI", 9, "bold")
        )
        lbl_dpi_head.pack(anchor=tk.W, pady=(0, 8))

        dpi_grid = tk.Frame(self, bg="#242424")
        dpi_grid.pack(fill=tk.X)

        for i in range(4):
            # Row header
            row_frame = tk.Frame(dpi_grid, bg="#242424")
            row_frame.pack(fill=tk.X, pady=3)

            rb_def = tk.Radiobutton(
                row_frame,
                text=f"Level {i + 1}",
                variable=self._default_dpi_var,
                value=i,
                bg="#242424",
                fg="#00d2ff",
                selectcolor="#181818",
                activebackground="#242424",
                activeforeground="#00d2ff",
                font=("Segoe UI", 9, "bold"),
                width=8,
                anchor=tk.W,
                command=self._handle_dpis_change
            )
            rb_def.pack(side=tk.LEFT, padx=(0, 8))

            val_label = tk.Label(
                row_frame,
                text=f"{self._dpi_vars[i].get()} DPI",
                bg="#242424",
                fg="#ffffff",
                font=("Segoe UI", 9),
                width=8,
                anchor=tk.E
            )
            val_label.pack(side=tk.RIGHT, padx=(8, 0))
            self._dpi_labels.append(val_label)

            slider = tk.Scale(
                row_frame,
                from_=250,
                to=2500,
                resolution=250,
                orient=tk.HORIZONTAL,
                variable=self._dpi_vars[i],
                bg="#242424",
                fg="#242424",
                troughcolor="#383838",
                activebackground="#00d2ff",
                highlightthickness=0,
                showvalue=False,
                bd=0,
                command=lambda val, idx=i: self._on_slider_moved(idx, val)
            )
            slider.pack(side=tk.LEFT, fill=tk.X, expand=True)
            self._sliders.append(slider)

        hint = tk.Label(
            self,
            text="* Radio button sets default boot DPI for this profile.",
            bg="#242424",
            fg="#777777",
            font=("Segoe UI", 8)
        )
        hint.pack(anchor=tk.W, pady=(6, 0))

    def _on_slider_moved(self, idx: int, val_str: str):
        val = int(val_str)
        self._dpi_labels[idx].configure(text=f"{val} DPI")
        self._handle_dpis_change()

    def _handle_rate_change(self):
        if self.on_rate_changed:
            self.on_rate_changed(self._rate_var.get())

    def _handle_dpis_change(self):
        if self.on_dpis_changed:
            dpis = [v.get() for v in self._dpi_vars]
            self.on_dpis_changed(dpis, self._default_dpi_var.get())

    def set_values(self, rate: int, dpis: List[int], default_dpi_idx: int):
        """Update UI fields from profile state."""
        self._rate_var.set(rate)
        self._default_dpi_var.set(default_dpi_idx)
        for i in range(min(4, len(dpis))):
            self._dpi_vars[i].set(dpis[i])
            self._dpi_labels[i].configure(text=f"{dpis[i]} DPI")
