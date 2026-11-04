using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace G300sController.Services;

public enum WindowBackdropMode
{
    Solid = 0,
    Mica = 1,
    Acrylic = 2,
    MicaAlt = 3
}

public static class WindowBackdropService
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWA_MICA_EFFECT = 1029;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    public static bool IsWindows11OrGreater => Environment.OSVersion.Version.Major >= 10 && Environment.OSVersion.Version.Build >= 22000;
    public static bool SupportsSystemBackdrop => Environment.OSVersion.Version.Major >= 10 && Environment.OSVersion.Version.Build >= 22621;

    public static bool ApplyBackdrop(Window window, WindowBackdropMode mode)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return false;

            // Enforce dark mode title bar
            int darkMode = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

            if (!IsWindows11OrGreater)
            {
                return false;
            }

            if (SupportsSystemBackdrop)
            {
                int backdropType = mode switch
                {
                    WindowBackdropMode.Mica => 2,
                    WindowBackdropMode.Acrylic => 3,
                    WindowBackdropMode.MicaAlt => 4,
                    _ => 1 // Solid (DWMSBT_NONE / DWMSBT_MAINWINDOW)
                };

                int hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
                return hr == 0;
            }
            else
            {
                // Fallback for Windows 11 Build 22000
                int micaVal = mode == WindowBackdropMode.Mica ? 1 : 0;
                int hr = DwmSetWindowAttribute(hwnd, DWMWA_MICA_EFFECT, ref micaVal, sizeof(int));
                return hr == 0;
            }
        }
        catch
        {
            return false;
        }
    }
}
