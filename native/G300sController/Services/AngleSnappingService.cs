using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace G300sController.Services;

public sealed class AngleSnappingService : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEMOVE = 0x0200;

    private IntPtr _hookId = IntPtr.Zero;
    private LowLevelMouseProc? _proc;
    private bool _isSnappingCorrection;

    private Point _lastPos;
    private int _lockedX = -1;
    private int _lockedY = -1;
    private int _consecutiveSnappedFrames = 0;

    public bool IsEnabled { get; set; } = false;
    public double AngleThresholdDegrees { get; set; } = 8.0; // Default: Moderate (8°)

    public event Action<int, int, bool>? MovementProcessed;

    public void Start()
    {
        if (_hookId != IntPtr.Zero) return;

        _proc = HookCallback;
        _hookId = SetWindowsHookEx(WH_MOUSE_LL, _proc, IntPtr.Zero, 0);
    }

    public void Stop()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WM_MOUSEMOVE)
        {
            if (_isSnappingCorrection)
            {
                return CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var currentPos = new Point(hookStruct.pt.x, hookStruct.pt.y);

            if (_lastPos.IsEmpty)
            {
                _lastPos = currentPos;
                return CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            int dx = currentPos.X - _lastPos.X;
            int dy = currentPos.Y - _lastPos.Y;

            if (dx == 0 && dy == 0)
            {
                return CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            if (IsEnabled && AngleThresholdDegrees > 0)
            {
                double absDx = Math.Abs(dx);
                double absDy = Math.Abs(dy);

                // Angle from horizontal in degrees (0..90)
                double angleDeg = Math.Atan2(absDy, absDx) * (180.0 / Math.PI);

                // Near horizontal (angle <= threshold)
                if (angleDeg <= AngleThresholdDegrees && absDx >= 3)
                {
                    if (_lockedY == -1)
                    {
                        _lockedY = _lastPos.Y;
                    }

                    _consecutiveSnappedFrames++;
                    _isSnappingCorrection = true;
                    SetCursorPos(currentPos.X, _lockedY);
                    _isSnappingCorrection = false;

                    _lastPos = new Point(currentPos.X, _lockedY);
                    MovementProcessed?.Invoke(dx, 0, true);
                    return (IntPtr)1; // Handled
                }
                // Near vertical (90 - angle <= threshold)
                else if ((90.0 - angleDeg) <= AngleThresholdDegrees && absDy >= 3)
                {
                    if (_lockedX == -1)
                    {
                        _lockedX = _lastPos.X;
                    }

                    _consecutiveSnappedFrames++;
                    _isSnappingCorrection = true;
                    SetCursorPos(_lockedX, currentPos.Y);
                    _isSnappingCorrection = false;

                    _lastPos = new Point(_lockedX, currentPos.Y);
                    MovementProcessed?.Invoke(0, dy, true);
                    return (IntPtr)1; // Handled
                }
                else
                {
                    // Diagonal / Free movement breaks lock
                    _lockedX = -1;
                    _lockedY = -1;
                    _consecutiveSnappedFrames = 0;
                    MovementProcessed?.Invoke(dx, dy, false);
                }
            }
            else
            {
                _lockedX = -1;
                _lockedY = -1;
            }

            _lastPos = currentPos;
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        Stop();
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);
}
