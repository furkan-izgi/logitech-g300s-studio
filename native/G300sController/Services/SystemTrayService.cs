using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace G300sController.Services;

public sealed class SystemTrayService : IDisposable
{
    private const int WM_USER = 0x0400;
    private const int WM_TRAYICON = WM_USER + 24;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint MF_CHECKED = 0x00000008;
    private const uint TPM_RIGHTALIGN = 0x0008;
    private const uint TPM_BOTTOMALIGN = 0x0020;
    private const uint TPM_RETURNCMD = 0x0100;

    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private NOTIFYICONDATAW _nid;
    private bool _added;
    private int _activeProfile;

    public event Action? RestoreRequested;
    public event Action<int>? ProfileSwitchRequested;
    public event Action? ExitRequested;

    public void Initialize(Window window, string iconPath)
    {
        var helper = new WindowInteropHelper(window);
        _hwnd = helper.EnsureHandle();

        HwndSource? source = HwndSource.FromHwnd(_hwnd);
        source?.AddHook(WndProc);

        if (File.Exists(iconPath))
        {
            _hIcon = LoadImageW(IntPtr.Zero, iconPath, 1, 16, 16, 0x0010 | 0x0040);
        }
        if (_hIcon == IntPtr.Zero)
        {
            _hIcon = LoadIconW(IntPtr.Zero, new IntPtr(32512));
        }

        _nid = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1001,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _hIcon,
            szTip = "Logitech G300s Studio · Profile 1"
        };

        _added = Shell_NotifyIconW(NIM_ADD, ref _nid);
    }

    public void UpdateActiveProfile(int profileIndex, int dpiValue, int pollingRateHz)
    {
        _activeProfile = profileIndex;
        if (!_added) return;
        _nid.szTip = $"Logitech G300s · Slot {profileIndex + 1} ({dpiValue} DPI · {pollingRateHz} Hz)";
        Shell_NotifyIconW(NIM_MODIFY, ref _nid);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAYICON)
        {
            int mouseMsg = (int)(lParam.ToInt64() & 0xFFFF);
            if (mouseMsg is WM_LBUTTONUP or WM_LBUTTONDBLCLK)
            {
                RestoreRequested?.Invoke();
                handled = true;
            }
            else if (mouseMsg == WM_RBUTTONUP)
            {
                ShowContextMenu();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    private void ShowContextMenu()
    {
        IntPtr hMenu = CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return;

        try
        {
            AppendMenuW(hMenu, MF_STRING, 100, "Open G300s Studio");
            AppendMenuW(hMenu, MF_SEPARATOR, 0, null);
            AppendMenuW(hMenu, MF_STRING | (_activeProfile == 0 ? MF_CHECKED : 0), 200, "Slot 1 — Primary Profile");
            AppendMenuW(hMenu, MF_STRING | (_activeProfile == 1 ? MF_CHECKED : 0), 201, "Slot 2 — Secondary Profile");
            AppendMenuW(hMenu, MF_STRING | (_activeProfile == 2 ? MF_CHECKED : 0), 202, "Slot 3 — Auxiliary Profile");
            AppendMenuW(hMenu, MF_SEPARATOR, 0, null);
            AppendMenuW(hMenu, MF_STRING, 999, "Exit G300s Studio");

            GetCursorPos(out POINT pt);
            SetForegroundWindow(_hwnd);

            int cmd = TrackPopupMenuEx(
                hMenu,
                TPM_RIGHTALIGN | TPM_BOTTOMALIGN | TPM_RETURNCMD,
                pt.X,
                pt.Y,
                _hwnd,
                IntPtr.Zero);

            if (cmd == 100) RestoreRequested?.Invoke();
            else if (cmd is >= 200 and <= 202) ProfileSwitchRequested?.Invoke(cmd - 200);
            else if (cmd == 999) ExitRequested?.Invoke();
        }
        finally
        {
            DestroyMenu(hMenu);
        }
    }

    public void Dispose()
    {
        if (_added)
        {
            Shell_NotifyIconW(NIM_DELETE, ref _nid);
            _added = false;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImageW(IntPtr hInst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, uint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);
}
