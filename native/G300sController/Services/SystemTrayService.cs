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
    private const int WM_POWERBROADCAST = 0x0218;
    private const int WM_DEVICECHANGE = 0x0219;

    private const int DBT_DEVNODES_CHANGED = 0x0007;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    private const uint MF_STRING = 0x00000000;
    private const uint MF_CHECKED = 0x00000008;
    private const uint MF_POPUP = 0x00000010;
    private const uint MF_SEPARATOR = 0x00000800;

    private const uint TPM_RIGHTALIGN = 0x0008;
    private const uint TPM_BOTTOMALIGN = 0x0020;
    private const uint TPM_RETURNCMD = 0x0100;

    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private NOTIFYICONDATAW _nid;
    private bool _added;

    private int _activeProfile;
    private int _activeDpiStage = 1;
    private int[] _dpiStages = [500, 1000, 1500, 2500];
    private string _activeLightingMode = "none";
    private bool _osdEnabled = true;
    private bool _crosshairEnabled;

    public event Action? RestoreRequested;
    public event Action<int>? ProfileSwitchRequested;
    public event Action<int>? DpiStageSwitchRequested;
    public event Action<string>? LightingModeSwitchRequested;
    public event Action? OsdToggleRequested;
    public event Action? CrosshairToggleRequested;
    public event Action? DeviceChangeDetected;
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
            szTip = "Logitech G300s Studio · Profil 1"
        };

        _added = Shell_NotifyIconW(NIM_ADD, ref _nid);
    }

    public void UpdateTrayState(
        int profileIndex,
        int activeDpiStage,
        int[] dpiStages,
        int pollingRateHz,
        string lightingMode,
        bool osdEnabled,
        bool crosshairEnabled = false)
    {
        _activeProfile = Math.Clamp(profileIndex, 0, 2);
        _activeDpiStage = Math.Clamp(activeDpiStage, 0, 3);
        if (dpiStages is { Length: >= 4 })
        {
            _dpiStages = [dpiStages[0], dpiStages[1], dpiStages[2], dpiStages[3]];
        }
        _activeLightingMode = lightingMode;
        _osdEnabled = osdEnabled;
        _crosshairEnabled = crosshairEnabled;

        if (!_added) return;
        int currentDpi = _dpiStages[_activeDpiStage];
        _nid.szTip = $"Logitech G300s · Profil {_activeProfile + 1} ({currentDpi} DPI · {pollingRateHz} Hz)";
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
        else if (msg == WM_DEVICECHANGE)
        {
            int evt = (int)(wParam.ToInt64() & 0xFFFF);
            if (evt is DBT_DEVNODES_CHANGED or DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE)
            {
                DeviceChangeDetected?.Invoke();
            }
        }
        else if (msg == WM_POWERBROADCAST)
        {
            int pbt = (int)(wParam.ToInt64() & 0xFFFF);
            if (pbt == PBT_APMRESUMEAUTOMATIC)
            {
                DeviceChangeDetected?.Invoke();
            }
        }

        return IntPtr.Zero;
    }

    private void ShowContextMenu()
    {
        IntPtr hMenu = CreatePopupMenu();
        IntPtr hDpiMenu = CreatePopupMenu();
        IntPtr hLightMenu = CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return;

        try
        {
            AppendMenuW(hMenu, MF_STRING, 100, "Logitech G300s Studio'yu Aç");
            AppendMenuW(hMenu, MF_SEPARATOR, 0, null);

            AppendMenuW(hMenu, MF_STRING | (_activeProfile == 0 ? MF_CHECKED : 0), 200, "Profil 1");
            AppendMenuW(hMenu, MF_STRING | (_activeProfile == 1 ? MF_CHECKED : 0), 201, "Profil 2");
            AppendMenuW(hMenu, MF_STRING | (_activeProfile == 2 ? MF_CHECKED : 0), 202, "Profil 3");
            AppendMenuW(hMenu, MF_SEPARATOR, 0, null);

            for (int i = 0; i < 4; i++)
            {
                uint flags = MF_STRING | (_activeDpiStage == i ? MF_CHECKED : 0);
                AppendMenuW(hDpiMenu, flags, (uint)(300 + i), $"Kademe {i + 1} · {_dpiStages[i]} DPI");
            }
            AppendMenuW(hMenu, MF_POPUP, (uint)hDpiMenu.ToInt64(), "DPI Kademesi Seç");

            AppendMenuW(hLightMenu, MF_STRING | (_activeLightingMode == "none" ? MF_CHECKED : 0), 400, "Sabit Donanım Rengi");
            AppendMenuW(hLightMenu, MF_STRING | (_activeLightingMode == "spectrum" ? MF_CHECKED : 0), 401, "Gökkuşağı Döngüsü (Rainbow)");
            AppendMenuW(hLightMenu, MF_STRING | (_activeLightingMode == "color_transition" ? MF_CHECKED : 0), 402, "Renk Geçişi (İki Renk)");
            AppendMenuW(hMenu, MF_POPUP, (uint)hLightMenu.ToInt64(), "Aydınlatma Modu");

            AppendMenuW(hMenu, MF_STRING | (_osdEnabled ? MF_CHECKED : 0), 500, "Ekran Üstü Bildirim (OSD)");
            AppendMenuW(hMenu, MF_STRING | (_crosshairEnabled ? MF_CHECKED : 0), 501, "Ekran Nişangahı (Crosshair)");
            AppendMenuW(hMenu, MF_SEPARATOR, 0, null);
            AppendMenuW(hMenu, MF_STRING, 999, "Çıkış");

            GetCursorPos(out POINT pt);
            SetForegroundWindow(_hwnd);

            int cmd = TrackPopupMenuEx(
                hMenu,
                TPM_RIGHTALIGN | TPM_BOTTOMALIGN | TPM_RETURNCMD,
                pt.X,
                pt.Y,
                _hwnd,
                IntPtr.Zero);

            switch (cmd)
            {
                case 100:
                    RestoreRequested?.Invoke();
                    break;
                case >= 200 and <= 202:
                    ProfileSwitchRequested?.Invoke(cmd - 200);
                    break;
                case >= 300 and <= 303:
                    DpiStageSwitchRequested?.Invoke(cmd - 300);
                    break;
                case 400:
                    LightingModeSwitchRequested?.Invoke("none");
                    break;
                case 401:
                    LightingModeSwitchRequested?.Invoke("spectrum");
                    break;
                case 402:
                    LightingModeSwitchRequested?.Invoke("color_transition");
                    break;
                case 500:
                    OsdToggleRequested?.Invoke();
                    break;
                case 501:
                    CrosshairToggleRequested?.Invoke();
                    break;
                case 999:
                    ExitRequested?.Invoke();
                    break;
            }
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
