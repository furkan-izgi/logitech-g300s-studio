"""Native Windows system tray icon using shell32.Shell_NotifyIconW."""

import os
import ctypes
import ctypes.wintypes as wintypes
import threading
from typing import Callable, Optional, Dict

user32 = ctypes.windll.user32
shell32 = ctypes.windll.shell32
kernel32 = ctypes.windll.kernel32

NIM_ADD = 0x00000000
NIM_MODIFY = 0x00000001
NIM_DELETE = 0x00000002

NIF_MESSAGE = 0x00000001
NIF_ICON = 0x00000002
NIF_TIP = 0x00000004

WM_USER = 0x0400
WM_TRAYICON = WM_USER + 20

WM_LBUTTONUP = 0x0202
WM_RBUTTONUP = 0x0205
WM_DESTROY = 0x0002

MF_STRING = 0x00000000
MF_SEPARATOR = 0x00000800
TPM_RIGHTALIGN = 0x0008
TPM_BOTTOMALIGN = 0x0020
TPM_RETURNCMD = 0x0100

WNDPROC = ctypes.WINFUNCTYPE(ctypes.c_longlong, wintypes.HWND, ctypes.c_uint, wintypes.WPARAM, wintypes.LPARAM)

user32.DefWindowProcW.restype = ctypes.c_longlong
user32.DefWindowProcW.argtypes = [wintypes.HWND, ctypes.c_uint, wintypes.WPARAM, wintypes.LPARAM]
user32.PostQuitMessage.argtypes = [ctypes.c_int]


class NOTIFYICONDATAW(ctypes.Structure):
    _fields_ = [
        ("cbSize", wintypes.DWORD),
        ("hWnd", wintypes.HWND),
        ("uID", ctypes.c_uint),
        ("uFlags", ctypes.c_uint),
        ("uCallbackMessage", ctypes.c_uint),
        ("hIcon", wintypes.HICON),
        ("szTip", wintypes.WCHAR * 128),
        ("dwState", wintypes.DWORD),
        ("dwStateMask", wintypes.DWORD),
        ("szInfo", wintypes.WCHAR * 256),
        ("uTimeoutOrVersion", ctypes.c_uint),
        ("szInfoTitle", wintypes.WCHAR * 64),
        ("dwInfoFlags", wintypes.DWORD),
        ("guidItem", ctypes.c_byte * 16),
        ("hBalloonIcon", wintypes.HICON)
    ]


class WNDCLASSEXW(ctypes.Structure):
    _fields_ = [
        ("cbSize", ctypes.c_uint),
        ("style", ctypes.c_uint),
        ("lpfnWndProc", WNDPROC),
        ("cbClsExtra", ctypes.c_int),
        ("cbWndExtra", ctypes.c_int),
        ("hInstance", wintypes.HINSTANCE),
        ("hIcon", wintypes.HICON),
        ("hCursor", wintypes.HICON),
        ("hbrBackground", wintypes.HBRUSH),
        ("lpszMenuName", wintypes.LPCWSTR),
        ("lpszClassName", wintypes.LPCWSTR),
        ("hIconSm", wintypes.HICON)
    ]


class NativeTrayIcon:
    """Zero-dependency Windows taskbar notification tray icon."""

    def __init__(
        self,
        tooltip: str = "Logitech G300s Controller",
        on_left_click: Optional[Callable[[], None]] = None,
        on_profile_select: Optional[Callable[[int], None]] = None,
        on_exit: Optional[Callable[[], None]] = None,
        icon_path: Optional[str] = None
    ):
        self.tooltip = tooltip
        self.on_left_click = on_left_click
        self.on_profile_select = on_profile_select
        self.on_exit = on_exit
        self.icon_path = icon_path

        self._hwnd: Optional[int] = None
        self._nid = NOTIFYICONDATAW()
        self._thread: Optional[threading.Thread] = None
        self._class_name = f"G300sTrayClass_{id(self)}"
        self._wndproc_ref = None

    def _create_icon(self) -> int:
        if self.icon_path and os.path.exists(self.icon_path):
            IMAGE_ICON = 1
            LR_LOADFROMFILE = 0x0010
            LR_DEFAULTSIZE = 0x0040
            h = user32.LoadImageW(None, self.icon_path, IMAGE_ICON, 16, 16, LR_LOADFROMFILE | LR_DEFAULTSIZE)
            if h:
                return h
        IDI_APPLICATION = 32512
        return user32.LoadIconW(None, wintypes.LPCWSTR(IDI_APPLICATION))

    def _show_menu(self):
        hmenu = user32.CreatePopupMenu()
        user32.AppendMenuW(hmenu, MF_STRING, 101, "Open G300s Controller")
        user32.AppendMenuW(hmenu, MF_SEPARATOR, 0, None)
        user32.AppendMenuW(hmenu, MF_STRING, 201, "Profile 1")
        user32.AppendMenuW(hmenu, MF_STRING, 202, "Profile 2")
        user32.AppendMenuW(hmenu, MF_STRING, 203, "Profile 3")
        user32.AppendMenuW(hmenu, MF_SEPARATOR, 0, None)
        user32.AppendMenuW(hmenu, MF_STRING, 999, "Exit")

        pt = wintypes.POINT()
        user32.GetCursorPos(ctypes.byref(pt))
        user32.SetForegroundWindow(self._hwnd)

        cmd = user32.TrackPopupMenuEx(
            hmenu,
            TPM_RIGHTALIGN | TPM_BOTTOMALIGN | TPM_RETURNCMD,
            pt.x,
            pt.y,
            self._hwnd,
            None
        )
        user32.DestroyMenu(hmenu)

        if cmd == 101 and self.on_left_click:
            self.on_left_click()
        elif cmd in (201, 202, 203) and self.on_profile_select:
            self.on_profile_select(cmd - 201)
        elif cmd == 999 and self.on_exit:
            self.on_exit()

    def _wnd_proc(self, hwnd: int, msg: int, wparam: int, lparam: int) -> int:
        if msg == WM_TRAYICON:
            if lparam == WM_LBUTTONUP:
                if self.on_left_click:
                    self.on_left_click()
            elif lparam == WM_RBUTTONUP:
                self._show_menu()
            return 0
        elif msg == WM_DESTROY:
            user32.PostQuitMessage(0)
            return 0
        return user32.DefWindowProcW(hwnd, msg, wparam, lparam)

    def _run_window(self):
        self._wndproc_ref = WNDPROC(self._wnd_proc)
        hinst = kernel32.GetModuleHandleW(None)

        wc = WNDCLASSEXW()
        wc.cbSize = ctypes.sizeof(WNDCLASSEXW)
        wc.lpfnWndProc = self._wndproc_ref
        wc.hInstance = hinst
        wc.lpszClassName = self._class_name

        user32.RegisterClassExW(ctypes.byref(wc))

        self._hwnd = user32.CreateWindowExW(
            0,
            self._class_name,
            "G300sTrayWindow",
            0,
            0, 0, 0, 0,
            0, 0, hinst, None
        )

        h_icon = self._create_icon()
        self._nid.cbSize = ctypes.sizeof(NOTIFYICONDATAW)
        self._nid.hWnd = self._hwnd
        self._nid.uID = 1
        self._nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP
        self._nid.uCallbackMessage = WM_TRAYICON
        self._nid.hIcon = h_icon
        self._nid.szTip = self.tooltip[:127]

        shell32.Shell_NotifyIconW(NIM_ADD, ctypes.byref(self._nid))

        msg = wintypes.MSG()
        while user32.GetMessageW(ctypes.byref(msg), None, 0, 0) > 0:
            user32.TranslateMessage(ctypes.byref(msg))
            user32.DispatchMessageW(ctypes.byref(msg))

        shell32.Shell_NotifyIconW(NIM_DELETE, ctypes.byref(self._nid))

    def start(self):
        """Start tray message loop in background thread."""
        if self._thread and self._thread.is_alive():
            return
        self._thread = threading.Thread(target=self._run_window, daemon=True, name="G300s_Tray")
        self._thread.start()

    def update_tooltip(self, text: str):
        """Update taskbar hover text."""
        if self._hwnd:
            self._nid.szTip = text[:127]
            shell32.Shell_NotifyIconW(NIM_MODIFY, ctypes.byref(self._nid))

    def stop(self):
        """Destroy window and remove tray icon."""
        if self._hwnd:
            WM_CLOSE = 0x0010
            user32.PostMessageW(self._hwnd, WM_CLOSE, 0, 0)
        if self._thread and self._thread.is_alive():
            self._thread.join(timeout=1.0)
        self._thread = None
        self._hwnd = None
