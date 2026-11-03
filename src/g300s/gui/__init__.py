"""Logitech G300s Graphical User Interface launcher."""

from pathlib import Path
import subprocess
import sys


def _find_or_build_native_exe() -> Path | None:
    """Locate the native .NET 10 G300sController.exe on Windows, building it if necessary."""
    repo_root = Path(__file__).resolve().parents[3]
    csproj = repo_root / "native" / "G300sController" / "G300sController.csproj"
    release_exe = repo_root / "native" / "G300sController" / "bin" / "Release" / "net10.0-windows" / "G300sController.exe"
    debug_exe = repo_root / "native" / "G300sController" / "bin" / "Debug" / "net10.0-windows" / "G300sController.exe"

    if release_exe.exists():
        return release_exe
    if debug_exe.exists():
        return debug_exe

    if csproj.exists():
        subprocess.run(
            ["dotnet", "build", str(csproj), "-c", "Release"],
            cwd=str(repo_root),
            check=True,
        )
        if release_exe.exists():
            return release_exe
    return None


def run_gui() -> None:
    """Launch the native Windows G300s Studio GUI (with Tkinter fallback on non-Windows)."""
    if sys.platform == "win32":
        exe_path = _find_or_build_native_exe()
        if exe_path is not None:
            subprocess.Popen([str(exe_path)], cwd=str(exe_path.parent))
            return

    import tkinter as tk
    from .main_window import G300sMainWindow

    root = tk.Tk()
    G300sMainWindow(root)
    root.mainloop()


__all__ = ["run_gui"]
