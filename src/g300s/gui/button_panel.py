"""Button assignment and action mapping panel."""

import tkinter as tk
from tkinter import ttk
from typing import Dict, Callable, Optional

ACTION_PRESETS = [
    ("Left Click", "mouse:1"),
    ("Right Click", "mouse:2"),
    ("Middle Click", "mouse:3"),
    ("Back", "mouse:4"),
    ("Forward", "mouse:5"),
    ("DPI Up", "dpi-up"),
    ("DPI Down", "dpi-down"),
    ("DPI Cycle", "dpi-cycle"),
    ("Profile Switch", "profile-cycle"),
    ("DPI Shift / Sniper", "dpi-alternate"),
    ("Default DPI", "dpi-default"),
    ("Copy (Ctrl+C)", "key:ctrl+c"),
    ("Paste (Ctrl+V)", "key:ctrl+v"),
    ("Cut (Ctrl+X)", "key:ctrl+x"),
    ("Undo (Ctrl+Z)", "key:ctrl+z"),
    ("Host Macro 1 (F13)", "key:f13"),
    ("Host Macro 2 (F14)", "key:f14"),
    ("Host Macro 3 (F15)", "key:f15"),
    ("Host Macro 4 (F16)", "key:f16"),
]

ACTION_LABELS = {v: k for k, v in ACTION_PRESETS}

BUTTON_NAMES = [
    "G1 (Left Click)",
    "G2 (Right Click)",
    "G3 (Middle Click)",
    "G4 (Index Top)",
    "G5 (Index Bottom)",
    "G6 (Ring Bottom)",
    "G7 (Ring Top)",
    "G8 (Center Top)",
    "G9 (Center Bottom)"
]


class ButtonPanel(tk.LabelFrame):
    """Panel for configuring button assignments on the active profile."""

    def __init__(
        self,
        parent,
        on_action_changed: Optional[Callable[[int, str], None]] = None,
        on_button_focused: Optional[Callable[[int], None]] = None,
        **kwargs
    ):
        super().__init__(
            parent,
            text=" Button Assignments (G1 - G9) ",
            bg="#242424",
            fg="#00d2ff",
            font=("Segoe UI", 10, "bold"),
            padx=12,
            pady=10,
            **kwargs
        )
        self.on_action_changed = on_action_changed
        self.on_button_focused = on_button_focused

        self._combos: Dict[int, ttk.Combobox] = {}
        self._action_vars: Dict[int, tk.StringVar] = {}
        self._current_actions: Dict[int, str] = {}

        self._build_ui()

    def _build_ui(self):
        style = ttk.Style()
        style.theme_use("clam")
        style.configure("G300s.TCombobox", fieldbackground="#1e1e1e", background="#333333", foreground="#ffffff")

        # Scrollable or compact 2-column layout
        grid_frame = tk.Frame(self, bg="#242424")
        grid_frame.pack(fill=tk.BOTH, expand=True)

        for i in range(9):
            row = i
            col = 0

            row_frame = tk.Frame(grid_frame, bg="#242424")
            row_frame.pack(fill=tk.X, pady=2)

            lbl = tk.Label(
                row_frame,
                text=BUTTON_NAMES[i],
                bg="#242424",
                fg="#cccccc",
                font=("Segoe UI", 9),
                width=18,
                anchor=tk.W
            )
            lbl.pack(side=tk.LEFT)

            var = tk.StringVar()
            self._action_vars[i] = var

            combo = ttk.Combobox(
                row_frame,
                textvariable=var,
                values=[label for label, _ in ACTION_PRESETS],
                state="readonly",
                style="G300s.TCombobox",
                width=22
            )
            combo.pack(side=tk.RIGHT, fill=tk.X, expand=True)
            combo.bind("<<ComboboxSelected>>", lambda e, idx=i: self._on_combo_selected(idx))
            combo.bind("<FocusIn>", lambda e, idx=i: self._on_combo_focus(idx))

            self._combos[i] = combo

    def _on_combo_selected(self, btn_idx: int):
        selected_text = self._action_vars[btn_idx].get()
        # Find raw action code
        action_code = next((code for label, code in ACTION_PRESETS if label == selected_text), selected_text)
        self._current_actions[btn_idx] = action_code
        if self.on_action_changed:
            self.on_action_changed(btn_idx, action_code)

    def _on_combo_focus(self, btn_idx: int):
        if self.on_button_focused:
            self.on_button_focused(btn_idx)

    def focus_button(self, btn_idx: int):
        """Set keyboard focus and highlight on a button combo."""
        if btn_idx in self._combos:
            self._combos[btn_idx].focus_set()

    def set_actions(self, actions):
        """Populate button assignments."""
        if isinstance(actions, list):
            self._current_actions = {i: actions[i] for i in range(len(actions))}
        elif isinstance(actions, dict):
            self._current_actions = actions.copy()
        else:
            self._current_actions = {}

        for i in range(9):
            code = self._current_actions.get(i, "mouse:1" if i == 0 else "mouse:2" if i == 1 else "mouse:3")
            label = ACTION_LABELS.get(code, code)
            self._action_vars[i].set(label)
