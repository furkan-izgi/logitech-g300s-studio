using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace G300sController.Services;

public sealed class MacroBindingItem
{
    public string TriggerKey { get; set; } = "f13";
    public string Mode { get; set; } = "text"; // "text", "rapid_fire", "shortcut"
    public string TextPayload { get; set; } = "gg wp!";
    public int Cps { get; set; } = 15;
    public string MouseButton { get; set; } = "left";
}

public sealed class AppProfileRule
{
    public string ExecutableName { get; set; } = string.Empty;
    public int ProfileIndex { get; set; }
}

public sealed class HostAutomationService : IDisposable
{
    private readonly G300sHidDevice _device;
    private IntPtr _hookHandle = IntPtr.Zero;
    private Win32.LowLevelKeyboardProc? _hookDelegate;

    private CancellationTokenSource? _watcherCts;
    private CancellationTokenSource? _rgbCts;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _rapidFireTasks = new();

    private ulong _lastIdleTime;
    private ulong _lastKernelTime;
    private ulong _lastUserTime;
    private string? _lastForegroundExe;

    public string ConfigFilePath { get; }
    public bool AutoProfileSwitchEnabled { get; set; }
    public int DefaultProfileIndex { get; set; }
    public List<AppProfileRule> AppRules { get; } = [];
    public Dictionary<string, MacroBindingItem> Macros { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string ActiveRgbEffect { get; private set; } = "none";
    public int RgbSpeedMs { get; private set; } = 350;
    public (int R, int G, int B) RgbBaseColor { get; private set; } = (0, 1, 1);

    public event Action<int, string>? AutoProfileSwitched;
    public event Action<string, bool>? MacroKeyTriggered;

    public HostAutomationService(G300sHidDevice device)
    {
        _device = device;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        ConfigFilePath = Path.Combine(home, ".g300s_daemon.json");

        InitDefaultMacros();
        LoadConfig();
    }

    private void InitDefaultMacros()
    {
        Macros["f13"] = new MacroBindingItem { TriggerKey = "f13", Mode = "shortcut", TextPayload = "ctrl+c" };
        Macros["f14"] = new MacroBindingItem { TriggerKey = "f14", Mode = "shortcut", TextPayload = "ctrl+v" };
        Macros["f15"] = new MacroBindingItem { TriggerKey = "f15", Mode = "text", TextPayload = "gg wp!" };
        Macros["f16"] = new MacroBindingItem { TriggerKey = "f16", Mode = "rapid_fire", Cps = 16, MouseButton = "left" };
        Macros["f17"] = new MacroBindingItem { TriggerKey = "f17", Mode = "rapid_fire", Cps = 10, MouseButton = "right" };
        Macros["f18"] = new MacroBindingItem { TriggerKey = "f18", Mode = "text", TextPayload = "gl hf!" };
    }

    public void LoadConfig()
    {
        if (!File.Exists(ConfigFilePath))
        {
            AppRules.Clear();
            AppRules.Add(new AppProfileRule { ExecutableName = "cs2.exe", ProfileIndex = 1 });
            AppRules.Add(new AppProfileRule { ExecutableName = "valorant-win64-shipping.exe", ProfileIndex = 1 });
            AppRules.Add(new AppProfileRule { ExecutableName = "blender.exe", ProfileIndex = 2 });
            AppRules.Add(new AppProfileRule { ExecutableName = "code.exe", ProfileIndex = 0 });
            SaveConfig();
            return;
        }

        try
        {
            string json = File.ReadAllText(ConfigFilePath, Encoding.UTF8);
            var root = JsonNode.Parse(json)?.AsObject();
            if (root is null) return;

            DefaultProfileIndex = root["default_profile"]?.GetValue<int>() ?? 0;

            AppRules.Clear();
            if (root["app_rules"] is JsonObject rulesObj)
            {
                foreach (var kv in rulesObj)
                {
                    if (kv.Value is not null)
                    {
                        AppRules.Add(new AppProfileRule
                        {
                            ExecutableName = kv.Key.ToLowerInvariant(),
                            ProfileIndex = Math.Clamp(kv.Value.GetValue<int>(), 0, 2)
                        });
                    }
                }
            }

            if (root["macros"] is JsonObject macrosObj)
            {
                foreach (var kv in macrosObj)
                {
                    string key = kv.Key.ToLowerInvariant();
                    if (kv.Value is not JsonObject mObj) continue;

                    string mType = mObj["type"]?.GetValue<string>() ?? "text";
                    if (mType == "rapid_fire")
                    {
                        Macros[key] = new MacroBindingItem
                        {
                            TriggerKey = key,
                            Mode = "rapid_fire",
                            Cps = Math.Clamp(mObj["cps"]?.GetValue<int>() ?? 15, 1, 50),
                            MouseButton = mObj["button"]?.GetValue<string>() ?? "left"
                        };
                    }
                    else if (mType == "text")
                    {
                        Macros[key] = new MacroBindingItem
                        {
                            TriggerKey = key,
                            Mode = "text",
                            TextPayload = mObj["text"]?.GetValue<string>() ?? string.Empty
                        };
                    }
                    else if (mType == "shortcut")
                    {
                        Macros[key] = new MacroBindingItem
                        {
                            TriggerKey = key,
                            Mode = "shortcut",
                            TextPayload = mObj["shortcut"]?.GetValue<string>() ?? "ctrl+c"
                        };
                    }
                }
            }
        }
        catch
        {
            // Keep initialized defaults on corrupt JSON
        }
    }

    public void SaveConfig()
    {
        try
        {
            var root = new JsonObject
            {
                ["default_profile"] = DefaultProfileIndex,
                ["app_watcher_interval_sec"] = 0.5
            };

            var rulesObj = new JsonObject();
            foreach (var r in AppRules)
            {
                if (!string.IsNullOrWhiteSpace(r.ExecutableName))
                    rulesObj[r.ExecutableName.Trim().ToLowerInvariant()] = r.ProfileIndex;
            }
            root["app_rules"] = rulesObj;

            var rgbObj = new JsonObject
            {
                ["enabled"] = ActiveRgbEffect != "none",
                ["effect"] = ActiveRgbEffect == "none" ? "cycle" : ActiveRgbEffect,
                ["speed_ms"] = RgbSpeedMs,
                ["color"] = new JsonArray(RgbBaseColor.R, RgbBaseColor.G, RgbBaseColor.B)
            };
            root["rgb_effect"] = rgbObj;

            var macrosObj = new JsonObject();
            foreach (var (key, item) in Macros)
            {
                if (item.Mode == "rapid_fire")
                {
                    macrosObj[key] = new JsonObject
                    {
                        ["type"] = "rapid_fire",
                        ["cps"] = item.Cps,
                        ["button"] = item.MouseButton
                    };
                }
                else if (item.Mode == "shortcut")
                {
                    macrosObj[key] = new JsonObject
                    {
                        ["type"] = "shortcut",
                        ["shortcut"] = item.TextPayload
                    };
                }
                else
                {
                    macrosObj[key] = new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = item.TextPayload
                    };
                }
            }
            root["macros"] = macrosObj;

            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(ConfigFilePath, root.ToJsonString(opts), Encoding.UTF8);
        }
        catch
        {
            // Ignore write errors
        }
    }

    public void StartHookAndWatcher()
    {
        if (_hookHandle == IntPtr.Zero)
        {
            _hookDelegate = HookCallback;
            _hookHandle = Win32.SetWindowsHookExW(Win32.WH_KEYBOARD_LL, _hookDelegate, IntPtr.Zero, 0);
        }

        if (_watcherCts is null)
        {
            _watcherCts = new CancellationTokenSource();
            CancellationToken token = _watcherCts.Token;
            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (AutoProfileSwitchEnabled && _device.IsConnected)
                        {
                            PollForegroundWindow();
                        }
                    }
                    catch
                    {
                        // Ignore transient process handle errors
                    }
                    await Task.Delay(500, token).ConfigureAwait(false);
                }
            }, token);
        }
    }

    public void SetRgbEffect(string effectName, int speedMs, (int R, int G, int B) baseColor)
    {
        ActiveRgbEffect = effectName.ToLowerInvariant();
        RgbSpeedMs = Math.Clamp(speedMs, 60, 2000);
        RgbBaseColor = baseColor;

        _rgbCts?.Cancel();
        _rgbCts?.Dispose();
        _rgbCts = null;

        if (ActiveRgbEffect == "none")
            return;

        _rgbCts = new CancellationTokenSource();
        CancellationToken token = _rgbCts.Token;

        Task.Run(async () =>
        {
            (int R, int G, int B)[] cycleColors =
            [
                (1, 0, 0),
                (1, 1, 0),
                (0, 1, 0),
                (0, 1, 1),
                (0, 0, 1),
                (1, 0, 1),
                (1, 1, 1)
            ];
            int idx = 0;
            bool toggle = false;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_device.IsConnected)
                    {
                        switch (ActiveRgbEffect)
                        {
                            case "cycle":
                                var c = cycleColors[idx++ % cycleColors.Length];
                                _device.SetLiveLed(c.R, c.G, c.B);
                                break;
                            case "breathing":
                                toggle = !toggle;
                                (int R, int G, int B) bc = toggle ? RgbBaseColor : (0, 0, 0);
                                _device.SetLiveLed(bc.R, bc.G, bc.B);
                                break;
                            case "police":
                                toggle = !toggle;
                                (int R, int G, int B) pc = toggle ? (1, 0, 0) : (0, 0, 1);
                                _device.SetLiveLed(pc.R, pc.G, pc.B);
                                break;
                            case "cpu":
                                double cpu = GetCpuUsagePercent();
                                (int R, int G, int B) cc = cpu < 45.0 ? (0, 1, 0) : cpu < 80.0 ? (1, 1, 0) : (1, 0, 0);
                                _device.SetLiveLed(cc.R, cc.G, cc.B);
                                break;
                        }
                    }
                }
                catch
                {
                    // Ignore transient HID error
                }

                await Task.Delay(RgbSpeedMs, token).ConfigureAwait(false);
            }
        }, token);
    }

    private void PollForegroundWindow()
    {
        IntPtr hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;

        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return;

        IntPtr hProc = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProc == IntPtr.Zero) return;

        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            if (Win32.QueryFullProcessImageNameW(hProc, 0, sb, ref size))
            {
                string exe = Path.GetFileName(sb.ToString()).ToLowerInvariant();
                if (!string.IsNullOrEmpty(exe) && exe != _lastForegroundExe)
                {
                    _lastForegroundExe = exe;
                    int targetProfile = DefaultProfileIndex;
                    foreach (var rule in AppRules)
                    {
                        if (string.Equals(rule.ExecutableName, exe, StringComparison.OrdinalIgnoreCase))
                        {
                            targetProfile = rule.ProfileIndex;
                            break;
                        }
                    }

                    _device.SetActiveProfile(targetProfile);
                    AutoProfileSwitched?.Invoke(targetProfile, exe);
                }
            }
        }
        finally
        {
            Win32.CloseHandle(hProc);
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int vkCode = Marshal.ReadInt32(lParam);
            if (vkCode is >= Win32.VK_F13 and <= Win32.VK_F24)
            {
                string keyName = $"f{13 + (vkCode - Win32.VK_F13)}";
                int msg = wParam.ToInt32();

                if (msg is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN)
                {
                    MacroKeyTriggered?.Invoke(keyName, true);
                    if (ExecuteMacroDown(keyName))
                        return new IntPtr(1);
                }
                else if (msg is Win32.WM_KEYUP or Win32.WM_SYSKEYUP)
                {
                    MacroKeyTriggered?.Invoke(keyName, false);
                    ExecuteMacroUp(keyName);
                    return new IntPtr(1);
                }
            }
        }
        return Win32.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private bool ExecuteMacroDown(string keyName)
    {
        if (!Macros.TryGetValue(keyName, out var macro))
            return false;

        if (macro.Mode == "text" && !string.IsNullOrEmpty(macro.TextPayload))
        {
            string text = macro.TextPayload;
            Task.Run(() => TypeUnicodeString(text));
            return true;
        }

        if (macro.Mode == "rapid_fire")
        {
            if (_rapidFireTasks.ContainsKey(keyName))
                return true;

            var cts = new CancellationTokenSource();
            if (_rapidFireTasks.TryAdd(keyName, cts))
            {
                int cps = Math.Clamp(macro.Cps, 1, 50);
                string btn = macro.MouseButton;
                CancellationToken token = cts.Token;

                Task.Run(async () =>
                {
                    int intervalMs = Math.Max(10, 1000 / cps);
                    while (!token.IsCancellationRequested)
                    {
                        SendMouseClick(btn);
                        await Task.Delay(intervalMs, token).ConfigureAwait(false);
                    }
                }, token);
            }
            return true;
        }

        return false;
    }

    private void ExecuteMacroUp(string keyName)
    {
        if (_rapidFireTasks.TryRemove(keyName, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    public static void TypeUnicodeString(string text)
    {
        foreach (char c in text)
        {
            var inputs = new Win32.INPUT[2];
            inputs[0].type = Win32.INPUT_KEYBOARD;
            inputs[0].U.ki.wScan = c;
            inputs[0].U.ki.dwFlags = Win32.KEYEVENTF_UNICODE;

            inputs[1].type = Win32.INPUT_KEYBOARD;
            inputs[1].U.ki.wScan = c;
            inputs[1].U.ki.dwFlags = Win32.KEYEVENTF_UNICODE | Win32.KEYEVENTF_KEYUP;

            Win32.SendInput(2, inputs, Marshal.SizeOf<Win32.INPUT>());
            Thread.Sleep(4);
        }
    }

    public static void SendMouseClick(string button)
    {
        uint downFlag = Win32.MOUSEEVENTF_LEFTDOWN;
        uint upFlag = Win32.MOUSEEVENTF_LEFTUP;

        if (string.Equals(button, "right", StringComparison.OrdinalIgnoreCase))
        {
            downFlag = Win32.MOUSEEVENTF_RIGHTDOWN;
            upFlag = Win32.MOUSEEVENTF_RIGHTUP;
        }
        else if (string.Equals(button, "middle", StringComparison.OrdinalIgnoreCase))
        {
            downFlag = Win32.MOUSEEVENTF_MIDDLEDOWN;
            upFlag = Win32.MOUSEEVENTF_MIDDLEUP;
        }

        var down = new Win32.INPUT[1];
        down[0].type = Win32.INPUT_MOUSE;
        down[0].U.mi.dwFlags = downFlag;
        Win32.SendInput(1, down, Marshal.SizeOf<Win32.INPUT>());

        Thread.Sleep(8);

        var up = new Win32.INPUT[1];
        up[0].type = Win32.INPUT_MOUSE;
        up[0].U.mi.dwFlags = upFlag;
        Win32.SendInput(1, up, Marshal.SizeOf<Win32.INPUT>());
    }

    private double GetCpuUsagePercent()
    {
        if (!Win32.GetSystemTimes(out ulong idle, out ulong kernel, out ulong user))
            return 0.0;

        if (_lastKernelTime == 0)
        {
            _lastIdleTime = idle;
            _lastKernelTime = kernel;
            _lastUserTime = user;
            return 0.0;
        }

        ulong dIdle = idle - _lastIdleTime;
        ulong dKernel = kernel - _lastKernelTime;
        ulong dUser = user - _lastUserTime;

        _lastIdleTime = idle;
        _lastKernelTime = kernel;
        _lastUserTime = user;

        ulong total = dKernel + dUser;
        if (total == 0) return 0.0;

        return Math.Clamp((1.0 - (double)dIdle / total) * 100.0, 0.0, 100.0);
    }

    public void Dispose()
    {
        _watcherCts?.Cancel();
        _watcherCts?.Dispose();
        _watcherCts = null;

        _rgbCts?.Cancel();
        _rgbCts?.Dispose();
        _rgbCts = null;

        foreach (var kv in _rapidFireTasks)
        {
            kv.Value.Cancel();
            kv.Value.Dispose();
        }
        _rapidFireTasks.Clear();

        if (_hookHandle != IntPtr.Zero)
        {
            Win32.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }

    private static class Win32
    {
        public const int WH_KEYBOARD_LL = 13;
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_KEYUP = 0x0101;
        public const int WM_SYSKEYDOWN = 0x0104;
        public const int WM_SYSKEYUP = 0x0105;

        public const int VK_F13 = 0x7C;
        public const int VK_F24 = 0x87;

        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        public const uint INPUT_MOUSE = 0;
        public const uint INPUT_KEYBOARD = 1;
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint KEYEVENTF_UNICODE = 0x0004;

        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;
        public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;

        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetSystemTimes(out ulong lpIdleTime, out ulong lpKernelTime, out ulong lpUserTime);
    }
}
