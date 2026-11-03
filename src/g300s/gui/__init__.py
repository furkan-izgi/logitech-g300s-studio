"""Logitech G300s Graphical User Interface package."""

import tkinter as tk
from .main_window import G300sMainWindow


def run_gui():
    """Launch the Logitech G300s Desktop GUI application."""
    root = tk.Tk()
    app = G300sMainWindow(root)
    root.mainloop()


__all__ = ["run_gui", "G300sMainWindow"]
