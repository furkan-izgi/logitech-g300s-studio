using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace G300sController.Services;

/// <summary>
/// Thread-safe low-level Windows mouse hook (WH_MOUSE_LL) filter designed to eliminate
/// mechanical microswitch contact chatter and false double-clicks on aging mouse switches
/// (e.g., Omron D2FC-F-7N microswitches commonly used in Logitech G300s).
/// </summary>
public sealed class DebounceFilterService : IDisposable
{
    private const int DefaultDebounceThresholdMs = 18;
    private const int MinDebounceThresholdMs = 5;
    private const int MaxDebounceThresholdMs = 50;

    private readonly object _stateLock = new();
    private readonly ManualResetEventSlim _readyEvent = new(false);

    private readonly ButtonDebounceState _leftButton = new("Left");
    private readonly ButtonDebounceState _rightButton = new("Right");
    private readonly ButtonDebounceState _middleButton = new("Middle");

    private volatile bool _isEnabled = true;
    private int _debounceThresholdMs = DefaultDebounceThresholdMs;
    private volatile bool _filterInjectedClicks;
    private volatile bool _debounceMiddleButton = true;
    private volatile bool _debounceReleaseEvents;

    private long _filteredClicksCount;
    private long _leftFilteredClicksCount;
    private long _rightFilteredClicksCount;

    private long _lastFilteredTimestamp;
    private volatile string? _lastFilteredButton;
    private int _lastFilteredIntervalMs;

    private Thread? _hookThread;
    private uint _hookThreadId;
    private IntPtr _hookHandle = IntPtr.Zero;
    private Win32.LowLevelMouseProc? _hookDelegate;
    private bool _isRunning;
    private bool _disposed;

    /// <summary>
    /// Occurs when an illegitimate switch-chatter double-click is detected and suppressed.
    /// Provides the button name ("Left", "Right", "Middle") and the suppressed interval in milliseconds.
    /// </summary>
    public event Action<string, int>? DoubleClickFiltered;

    /// <summary>
    /// Occurs when a legitimate, valid mouse button press passes through the debounce filter.
    /// Used for telemetry, heatmaps, and click analytics.
    /// </summary>
    public event Action<string>? ValidClickOccurred;

    /// <summary>
    /// Gets or sets whether the anti-double-click debounce filter is actively suppressing switch chatter.
    /// Default is true.
    /// </summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set => _isEnabled = value;
    }

    /// <summary>
    /// Gets or sets the debounce suppression threshold in milliseconds.
    /// Automatically clamped between 5ms and 50ms. Default is 18ms.
    /// </summary>
    public int DebounceThresholdMs
    {
        get => Volatile.Read(ref _debounceThresholdMs);
        set => Volatile.Write(ref _debounceThresholdMs, Math.Clamp(value, MinDebounceThresholdMs, MaxDebounceThresholdMs));
    }

    /// <summary>
    /// Gets or sets whether synthetic mouse clicks injected via SendInput or software macros
    /// should be subjected to debounce filtering. Default is false (only physical hardware clicks are filtered).
    /// </summary>
    public bool FilterInjectedClicks
    {
        get => _filterInjectedClicks;
        set => _filterInjectedClicks = value;
    }

    /// <summary>
    /// Gets or sets whether the middle mouse button (scroll wheel switch) is protected by the debounce filter.
    /// Default is true.
    /// </summary>
    public bool DebounceMiddleButton
    {
        get => _debounceMiddleButton;
        set => _debounceMiddleButton = value;
    }

    /// <summary>
    /// Gets or sets whether microswitch release chatter (UP events immediately following a DOWN)
    /// should also be debounced. Default is false.
    /// </summary>
    public bool DebounceReleaseEvents
    {
        get => _debounceReleaseEvents;
        set => _debounceReleaseEvents = value;
    }

    /// <summary>
    /// Gets the total number of false double-clicks successfully suppressed since service startup or last reset.
    /// </summary>
    public long FilteredClicksCount => Interlocked.Read(ref _filteredClicksCount);

    /// <summary>
    /// Gets the number of false left button double-clicks suppressed.
    /// </summary>
    public long LeftFilteredClicksCount => Interlocked.Read(ref _leftFilteredClicksCount);

    /// <summary>
    /// Gets the number of false right button double-clicks suppressed.
    /// </summary>
    public long RightFilteredClicksCount => Interlocked.Read(ref _rightFilteredClicksCount);

    /// <summary>
    /// Gets the button name of the most recently suppressed false click.
    /// </summary>
    public string? LastFilteredButton => _lastFilteredButton;

    /// <summary>
    /// Gets the suppressed interval in milliseconds of the most recently suppressed false click.
    /// </summary>
    public int LastFilteredIntervalMs => Volatile.Read(ref _lastFilteredIntervalMs);

    /// <summary>
    /// Gets whether the low-level mouse hook thread and hook handle are currently active.
    /// </summary>
    public bool IsRunning
    {
        get
        {
            lock (_stateLock)
            {
                return _isRunning && _hookHandle != IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DebounceFilterService"/> class.
    /// </summary>
    /// <param name="autoStart">If true, immediately registers the low-level mouse hook on a dedicated thread.</param>
    public DebounceFilterService(bool autoStart = true)
    {
        if (autoStart)
        {
            Start();
        }
    }

    /// <summary>
    /// Starts the low-level mouse hook on a dedicated background message pump thread.
    /// Safe to call multiple times (idempotent).
    /// </summary>
    public void Start()
    {
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_isRunning)
            {
                return;
            }

            _readyEvent.Reset();
            _hookThread = new Thread(HookThreadWorker)
            {
                Name = "G300s_DebounceHook_Thread",
                IsBackground = true
            };
            _hookThread.SetApartmentState(ApartmentState.STA);
            _hookThread.Start();

            if (!_readyEvent.Wait(TimeSpan.FromSeconds(3)))
            {
                Debug.WriteLine("[DebounceFilterService] Warning: Hook initialization timed out waiting for ready signal.");
            }

            _isRunning = _hookHandle != IntPtr.Zero;
        }
    }

    /// <summary>
    /// Stops the mouse hook and terminates the dedicated background hook thread.
    /// Safe to call multiple times (idempotent).
    /// </summary>
    public void Stop()
    {
        lock (_stateLock)
        {
            if (!_isRunning)
            {
                return;
            }

            _isRunning = false;
            uint threadId = _hookThreadId;

            UnhookInternal();

            if (threadId != 0)
            {
                Win32.PostThreadMessageW(threadId, Win32.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }

            if (_hookThread is not null && _hookThread.IsAlive)
            {
                if (!_hookThread.Join(1000))
                {
                    Debug.WriteLine("[DebounceFilterService] Hook thread did not join within 1000ms timeout.");
                }
                _hookThread = null;
            }

            _hookThreadId = 0;
            _leftButton.Reset();
            _rightButton.Reset();
            _middleButton.Reset();
        }
    }

    /// <summary>
    /// Resets all filtered click counters to zero.
    /// </summary>
    public void ResetCounters()
    {
        Interlocked.Exchange(ref _filteredClicksCount, 0);
        Interlocked.Exchange(ref _leftFilteredClicksCount, 0);
        Interlocked.Exchange(ref _rightFilteredClicksCount, 0);
        Volatile.Write(ref _lastFilteredIntervalMs, 0);
        _lastFilteredButton = null;
    }

    /// <summary>
    /// Dedicated background thread message loop ensuring zero latency and immune to UI stalls.
    /// </summary>
    private void HookThreadWorker()
    {
        try
        {
            _hookThreadId = Win32.GetCurrentThreadId();
            _hookDelegate = HookProcedure;

            IntPtr moduleHandle = Win32.GetModuleHandleW(null);
            _hookHandle = Win32.SetWindowsHookExW(Win32.WH_MOUSE_LL, _hookDelegate, moduleHandle, 0);

            if (_hookHandle == IntPtr.Zero)
            {
                // Fallback to IntPtr.Zero if module handle resolution failed
                _hookHandle = Win32.SetWindowsHookExW(Win32.WH_MOUSE_LL, _hookDelegate, IntPtr.Zero, 0);
            }

            if (_hookHandle == IntPtr.Zero)
            {
                int errorCode = Marshal.GetLastWin32Error();
                Debug.WriteLine($"[DebounceFilterService] SetWindowsHookExW failed with Win32 error code: {errorCode}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DebounceFilterService] Exception during hook installation: {ex.Message}");
        }
        finally
        {
            _readyEvent.Set();
        }

        if (_hookHandle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            while (!_disposed && Win32.GetMessageW(out Win32.MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                Win32.TranslateMessage(ref msg);
                Win32.DispatchMessageW(ref msg);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DebounceFilterService] Exception in hook message loop: {ex.Message}");
        }
        finally
        {
            UnhookInternal();
        }
    }

    /// <summary>
    /// Unhooks the low-level Windows hook handle in a thread-safe manner.
    /// </summary>
    private void UnhookInternal()
    {
        IntPtr handle = Interlocked.Exchange(ref _hookHandle, IntPtr.Zero);
        if (handle != IntPtr.Zero)
        {
            Win32.UnhookWindowsHookEx(handle);
        }
    }

    /// <summary>
    /// Low-level mouse hook callback procedure.
    /// Evaluates incoming mouse messages, measures switch timestamps, and suppresses chatter.
    /// </summary>
    private IntPtr HookProcedure(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || !_isEnabled)
        {
            return Win32.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        try
        {
            int msg = (int)wParam;
            if (msg != Win32.WM_LBUTTONDOWN &&
                msg != Win32.WM_LBUTTONUP &&
                msg != Win32.WM_RBUTTONDOWN &&
                msg != Win32.WM_RBUTTONUP &&
                msg != Win32.WM_MBUTTONDOWN &&
                msg != Win32.WM_MBUTTONUP)
            {
                // Rapid pass-through for WM_MOUSEMOVE, WM_MOUSEWHEEL, etc.
                return Win32.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
            }

            bool isInjected = false;
            if (lParam != IntPtr.Zero)
            {
                unsafe
                {
                    Win32.MSLLHOOKSTRUCT* pStruct = (Win32.MSLLHOOKSTRUCT*)lParam;
                    isInjected = (pStruct->flags & (Win32.LLMHF_INJECTED | Win32.LLMHF_LOWER_IL_INJECTED)) != 0;
                }
            }

            long nowTicks = Stopwatch.GetTimestamp();
            if (ShouldSuppressClick(msg, isInjected, nowTicks, out string? buttonName, out int intervalMs))
            {
                Interlocked.Increment(ref _filteredClicksCount);
                if (string.Equals(buttonName, "Left", StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref _leftFilteredClicksCount);
                }
                else if (string.Equals(buttonName, "Right", StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref _rightFilteredClicksCount);
                }

                Volatile.Write(ref _lastFilteredTimestamp, nowTicks);
                _lastFilteredButton = buttonName;
                Volatile.Write(ref _lastFilteredIntervalMs, intervalMs);

                DispatchDoubleClickFiltered(buttonName ?? "Unknown", intervalMs);

                // Suppress message by returning 1 (non-zero prevents Windows from passing to target window)
                return (IntPtr)1;
            }

            if (msg is Win32.WM_LBUTTONDOWN or Win32.WM_RBUTTONDOWN or Win32.WM_MBUTTONDOWN)
            {
                string bName = msg == Win32.WM_LBUTTONDOWN ? "Left" : (msg == Win32.WM_RBUTTONDOWN ? "Right" : "Middle");
                ValidClickOccurred?.Invoke(bName);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DebounceFilterService] Error in HookProcedure: {ex.Message}");
        }

        return Win32.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    /// <summary>
    /// Pure state evaluation logic for mouse clicks.
    /// Compares high-precision timestamps against debounce threshold.
    /// </summary>
    public bool ShouldSuppressClick(
        int msg,
        bool isInjected,
        long timestampTicks,
        out string? buttonName,
        out int intervalMs)
    {
        buttonName = null;
        intervalMs = 0;

        if (!_isEnabled)
        {
            return false;
        }

        bool isDown = (msg == Win32.WM_LBUTTONDOWN || msg == Win32.WM_RBUTTONDOWN || msg == Win32.WM_MBUTTONDOWN);
        bool isUp = (msg == Win32.WM_LBUTTONUP || msg == Win32.WM_RBUTTONUP || msg == Win32.WM_MBUTTONUP);

        if (!isDown && !isUp)
        {
            return false;
        }

        ButtonDebounceState? state = msg switch
        {
            Win32.WM_LBUTTONDOWN or Win32.WM_LBUTTONUP => _leftButton,
            Win32.WM_RBUTTONDOWN or Win32.WM_RBUTTONUP => _rightButton,
            Win32.WM_MBUTTONDOWN or Win32.WM_MBUTTONUP when _debounceMiddleButton => _middleButton,
            _ => null
        };

        if (state is null)
        {
            return false;
        }

        if (isInjected && !_filterInjectedClicks)
        {
            if (isDown)
            {
                state.LastDownTimestamp = timestampTicks;
                state.LastEventTimestamp = timestampTicks;
                state.IsPhysicallyDown = true;
            }
            else if (isUp)
            {
                state.LastUpTimestamp = timestampTicks;
                state.LastEventTimestamp = timestampTicks;
                state.IsPhysicallyDown = false;
            }
            return false;
        }

        if (isUp)
        {
            if (_debounceReleaseEvents && state.IsPhysicallyDown)
            {
                long lastDown = state.LastDownTimestamp;
                if (lastDown > 0)
                {
                    long elapsedTicks = timestampTicks - lastDown;
                    if (elapsedTicks >= 0)
                    {
                        double msSinceDown = (double)elapsedTicks * 1000.0 / Stopwatch.Frequency;
                        if (msSinceDown < Volatile.Read(ref _debounceThresholdMs))
                        {
                            buttonName = state.Name;
                            intervalMs = Math.Max(1, (int)Math.Round(msSinceDown));
                            state.LastEventTimestamp = timestampTicks;
                            return true;
                        }
                    }
                }
            }

            state.LastUpTimestamp = timestampTicks;
            state.LastEventTimestamp = timestampTicks;
            state.IsPhysicallyDown = false;
            return false;
        }

        // It is a button DOWN message
        long lastEvent = state.LastEventTimestamp;
        if (lastEvent > 0)
        {
            long elapsedTicks = timestampTicks - lastEvent;
            if (elapsedTicks >= 0)
            {
                double elapsedMs = (double)elapsedTicks * 1000.0 / Stopwatch.Frequency;
                int threshold = Volatile.Read(ref _debounceThresholdMs);
                if (elapsedMs < threshold)
                {
                    // False double-click / switch chatter detected within lockout window
                    buttonName = state.Name;
                    intervalMs = Math.Max(1, (int)Math.Round(elapsedMs));

                    // Extend debounce lockout window so sustained mechanical vibration bursts are suppressed
                    state.LastEventTimestamp = timestampTicks;
                    return true;
                }
            }
        }

        // Legitimate, human button down
        state.LastDownTimestamp = timestampTicks;
        state.LastEventTimestamp = timestampTicks;
        state.IsPhysicallyDown = true;
        return false;
    }

    /// <summary>
    /// Safely dispatches the DoubleClickFiltered event on a background ThreadPool thread,
    /// ensuring the native mouse hook procedure is never blocked by UI or telemetry handlers.
    /// </summary>
    private void DispatchDoubleClickFiltered(string buttonName, int intervalMs)
    {
        var handler = DoubleClickFiltered;
        if (handler is null)
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                handler.Invoke(buttonName, intervalMs);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DebounceFilterService] DoubleClickFiltered event handler thrown: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Disposes resources and cleanly unhooks the Windows hook.
    /// </summary>
    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }

        Stop();
        _readyEvent.Dispose();
        GC.SuppressFinalize(this);
    }

    ~DebounceFilterService()
    {
        UnhookInternal();
    }

    /// <summary>
    /// Thread-safe tracking of per-button timestamps and physical switch state.
    /// </summary>
    private sealed class ButtonDebounceState
    {
        public string Name { get; }

        private long _lastDownTimestamp;
        private long _lastUpTimestamp;
        private long _lastEventTimestamp;
        private int _isPhysicallyDown;

        public ButtonDebounceState(string name)
        {
            Name = name;
        }

        public long LastDownTimestamp
        {
            get => Interlocked.Read(ref _lastDownTimestamp);
            set => Interlocked.Exchange(ref _lastDownTimestamp, value);
        }

        public long LastUpTimestamp
        {
            get => Interlocked.Read(ref _lastUpTimestamp);
            set => Interlocked.Exchange(ref _lastUpTimestamp, value);
        }

        public long LastEventTimestamp
        {
            get => Interlocked.Read(ref _lastEventTimestamp);
            set => Interlocked.Exchange(ref _lastEventTimestamp, value);
        }

        public bool IsPhysicallyDown
        {
            get => Volatile.Read(ref _isPhysicallyDown) == 1;
            set => Volatile.Write(ref _isPhysicallyDown, value ? 1 : 0);
        }

        public void Reset()
        {
            Interlocked.Exchange(ref _lastDownTimestamp, 0);
            Interlocked.Exchange(ref _lastUpTimestamp, 0);
            Interlocked.Exchange(ref _lastEventTimestamp, 0);
            Volatile.Write(ref _isPhysicallyDown, 0);
        }
    }

    /// <summary>
    /// P/Invoke definitions for low-level Windows mouse hooks and message processing.
    /// </summary>
    private static class Win32
    {
        public const int WH_MOUSE_LL = 14;
        public const uint WM_QUIT = 0x0012;

        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_LBUTTONUP = 0x0202;
        public const int WM_RBUTTONDOWN = 0x0204;
        public const int WM_RBUTTONUP = 0x0205;
        public const int WM_MBUTTONDOWN = 0x0207;
        public const int WM_MBUTTONUP = 0x0208;

        public const uint LLMHF_INJECTED = 0x00000001;
        public const uint LLMHF_LOWER_IL_INJECTED = 0x00000002;

        public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
            public uint lPrivate;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookExW(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr GetModuleHandleW(string? lpModuleName);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        public static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessageW(ref MSG lpMsg);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostThreadMessageW(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);
    }
}
