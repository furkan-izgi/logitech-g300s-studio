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
using G300sController.Models;
using Microsoft.Win32;

namespace G300sController.Services;

public sealed class TimedMacroStep
{
    public ushort VkCode { get; set; }
    public string KeyLabel { get; set; } = "";
    public bool IsKeyUp { get; set; }
    public int DelayMs { get; set; }
}

public sealed class MacroBindingItem
{
    public string TriggerKey { get; set; } = "f13";
    // "text", "rapid_fire", "shortcut", "recoil", "timed", "gshift"
    public string Mode { get; set; } = "text";
    public string TextPayload { get; set; } = "";
    public string MouseButton { get; set; } = "left";
    public int Cps { get; set; } = 12;
    public string ShortcutKeys { get; set; } = "ctrl+c";
    public int RecoilVerticalPx { get; set; } = 4;
    public int RecoilHorizontalPx { get; set; } = 0;
    public int GShiftProfileIndex { get; set; } = 2;
    public List<TimedMacroStep> TimedSteps { get; set; } = [];
}

public sealed class AppProfileRule
{
    public string ExecutableName { get; set; } = "";
    public int ProfileIndex { get; set; }
}

/// <summary>
/// Provides low-level Win32 keyboard hook (WH_KEYBOARD_LL) for F13..F24 macros,
/// foreground window process watcher, flicker-free continuous-anode hardware lighting,
/// multi-zone screen Ambilight, and multi-signal game state telemetry.
/// </summary>
public sealed class HostAutomationService : IDisposable
{
    private readonly G300sHidDevice _device;
    private readonly string _configPath;

    private IntPtr _hookHandle = IntPtr.Zero;
    private Win32.LowLevelKeyboardProc? _hookDelegate;

    private CancellationTokenSource? _watcherCts;
    private CancellationTokenSource? _rgbCts;
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _rapidFireTasks = new();

    private string _lastForegroundExe = "";
    private int _preGShiftProfileIndex = -1;
    private bool _isGShiftActive;

    public bool AutoProfileSwitchEnabled { get; set; }
    public bool OsdNotificationsEnabled { get; set; } = true;
    public int DefaultProfileIndex { get; set; }
    public List<AppProfileRule> AppRules { get; } = [];
    public Dictionary<string, MacroBindingItem> Macros { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string ActiveRgbEffect { get; private set; } = "none";
    public int RgbSpeedMs { get; private set; } = 2800;
    public (int R, int G, int B) RgbBaseColor { get; private set; } = (0, 1, 1);
    public (int R, int G, int B) RgbSecondaryColor { get; private set; } = (0, 0, 1);

    // Real-time 24-bit color & telemetry exposed for 30 FPS UI preview
    public (byte R, byte G, byte B) LivePreviewColor { get; private set; } = (0, 212, 255);
    public double LiveTelemetryLevel { get; private set; } = 1.0;
    public string LiveTelemetryText { get; private set; } = "Donanım Sabit Rengi";
    public string ActiveForegroundProcess { get; private set; } = "Masaüstü";

    public event Action<int, string>? AutoProfileSwitched;
    public event Action<string>? MacroKeyTriggered;
    public event Action<bool, int>? GShiftStateChanged;

    public HostAutomationService(G300sHidDevice device)
    {
        _device = device;
        _configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".g300s_daemon.json");

        InitializeDefaults();
        LoadConfig();
    }

    private void InitializeDefaults()
    {
        AppRules.Clear();
        AppRules.Add(new AppProfileRule { ExecutableName = "cs2.exe", ProfileIndex = 1 });
        AppRules.Add(new AppProfileRule { ExecutableName = "valorant-win64-shipping.exe", ProfileIndex = 1 });
        AppRules.Add(new AppProfileRule { ExecutableName = "blender.exe", ProfileIndex = 2 });
        AppRules.Add(new AppProfileRule { ExecutableName = "code.exe", ProfileIndex = 0 });

        Macros["f13"] = new MacroBindingItem { TriggerKey = "f13", Mode = "shortcut", ShortcutKeys = "ctrl+c" };
        Macros["f14"] = new MacroBindingItem { TriggerKey = "f14", Mode = "shortcut", ShortcutKeys = "ctrl+v" };
        Macros["f15"] = new MacroBindingItem { TriggerKey = "f15", Mode = "timed", TimedSteps =
        [
            new TimedMacroStep { VkCode = 0x51, KeyLabel = "Q", IsKeyUp = false, DelayMs = 0 },
            new TimedMacroStep { VkCode = 0x51, KeyLabel = "Q", IsKeyUp = true, DelayMs = 45 },
            new TimedMacroStep { VkCode = 0x52, KeyLabel = "R", IsKeyUp = false, DelayMs = 90 },
            new TimedMacroStep { VkCode = 0x52, KeyLabel = "R", IsKeyUp = true, DelayMs = 45 }
        ]};
        Macros["f16"] = new MacroBindingItem { TriggerKey = "f16", Mode = "rapid_fire", MouseButton = "left", Cps = 15 };
        Macros["f17"] = new MacroBindingItem { TriggerKey = "f17", Mode = "recoil", Cps = 12, RecoilVerticalPx = 4, RecoilHorizontalPx = 0 };
        Macros["f18"] = new MacroBindingItem { TriggerKey = "f18", Mode = "gshift", GShiftProfileIndex = 2 };
    }

    public static bool GetRunAtStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
            return key?.GetValue("LogitechG300sStudio") is string val && !string.IsNullOrWhiteSpace(val);
        }
        catch
        {
            return false;
        }
    }

    public static void SetRunAtStartup(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key is null) return;

            if (enabled)
            {
                string? exePath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    key.SetValue("LogitechG300sStudio", $"\"{exePath}\" --minimized");
                }
            }
            else
            {
                key.DeleteValue("LogitechG300sStudio", false);
            }
        }
        catch
        {
            // Ignore registry permission errors
        }
    }

    public void LoadConfig()
    {
        if (!File.Exists(_configPath))
        {
            SaveConfig();
            return;
        }

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(_configPath)) as JsonObject;
            if (root is null) return;

            DefaultProfileIndex = root["default_profile"]?.GetValue<int>() ?? 0;
            AutoProfileSwitchEnabled = root["auto_switch_enabled"]?.GetValue<bool>() ?? false;
            OsdNotificationsEnabled = root["osd_enabled"]?.GetValue<bool>() ?? true;

            if (root["app_rules"] is JsonObject rulesObj)
            {
                AppRules.Clear();
                foreach (var kv in rulesObj)
                {
                    AppRules.Add(new AppProfileRule
                    {
                        ExecutableName = kv.Key,
                        ProfileIndex = Math.Clamp(kv.Value?.GetValue<int>() ?? 0, 0, 2)
                    });
                }
            }

            if (root["macros"] is JsonObject macrosObj)
            {
                foreach (var kv in macrosObj)
                {
                    string key = kv.Key.ToLowerInvariant();
                    if (kv.Value is not JsonObject mObj) continue;

                    string mType = mObj["type"]?.GetValue<string>() ?? "text";
                    var item = new MacroBindingItem { TriggerKey = key };

                    if (mType == "text")
                    {
                        item.Mode = "text";
                        item.TextPayload = mObj["text"]?.GetValue<string>() ?? "";
                    }
                    else if (mType == "rapid_fire")
                    {
                        item.Mode = "rapid_fire";
                        item.MouseButton = mObj["button"]?.GetValue<string>() ?? "left";
                        item.Cps = Math.Clamp(mObj["cps"]?.GetValue<int>() ?? 12, 1, 50);
                    }
                    else if (mType == "recoil")
                    {
                        item.Mode = "recoil";
                        item.Cps = Math.Clamp(mObj["cps"]?.GetValue<int>() ?? 12, 1, 50);
                        item.RecoilVerticalPx = Math.Clamp(mObj["dy"]?.GetValue<int>() ?? 4, -30, 40);
                        item.RecoilHorizontalPx = Math.Clamp(mObj["dx"]?.GetValue<int>() ?? 0, -30, 30);
                    }
                    else if (mType == "gshift")
                    {
                        item.Mode = "gshift";
                        item.GShiftProfileIndex = Math.Clamp(mObj["target_profile"]?.GetValue<int>() ?? 2, 0, 2);
                    }
                    else if (mType == "timed")
                    {
                        item.Mode = "timed";
                        if (mObj["timed_steps"] is JsonArray tArr)
                        {
                            foreach (var node in tArr)
                            {
                                if (node is not JsonObject sObj) continue;
                                item.TimedSteps.Add(new TimedMacroStep
                                {
                                    VkCode = (ushort)(sObj["vk"]?.GetValue<int>() ?? 0),
                                    KeyLabel = sObj["label"]?.GetValue<string>() ?? "",
                                    IsKeyUp = sObj["up"]?.GetValue<bool>() ?? false,
                                    DelayMs = Math.Clamp(sObj["delay"]?.GetValue<int>() ?? 30, 0, 5000)
                                });
                            }
                        }
                    }
                    else if (mType == "sequence")
                    {
                        item.Mode = "shortcut";
                        item.ShortcutKeys = mObj["ui_shortcut"]?.GetValue<string>() ?? "ctrl+c";
                    }

                    Macros[key] = item;
                }
            }

            int schemaVer = root["schema_version"]?.GetValue<int>() ?? 1;
            if (schemaVer < 2)
            {
                if (Macros.TryGetValue("f15", out var m15) && m15.Mode == "text" && m15.TextPayload == "gg wp!")
                {
                    Macros["f15"] = new MacroBindingItem
                    {
                        TriggerKey = "f15",
                        Mode = "timed",
                        TimedSteps =
                        [
                            new TimedMacroStep { VkCode = 0x51, KeyLabel = "Q", IsKeyUp = false, DelayMs = 0 },
                            new TimedMacroStep { VkCode = 0x51, KeyLabel = "Q", IsKeyUp = true, DelayMs = 45 },
                            new TimedMacroStep { VkCode = 0x52, KeyLabel = "R", IsKeyUp = false, DelayMs = 90 },
                            new TimedMacroStep { VkCode = 0x52, KeyLabel = "R", IsKeyUp = true, DelayMs = 45 }
                        ]
                    };
                }
                if (Macros.TryGetValue("f17", out var m17) && m17.Mode == "rapid_fire" && m17.Cps == 10)
                {
                    Macros["f17"] = new MacroBindingItem { TriggerKey = "f17", Mode = "recoil", Cps = 12, RecoilVerticalPx = 4, RecoilHorizontalPx = 0 };
                }
                if (Macros.TryGetValue("f18", out var m18) && m18.Mode == "text" && m18.TextPayload == "gl hf!")
                {
                    Macros["f18"] = new MacroBindingItem { TriggerKey = "f18", Mode = "gshift", GShiftProfileIndex = 2 };
                }
                SaveConfig();
            }
        }
        catch
        {
            // Keep default config on parse failure
        }
    }

    public void SaveConfig()
    {
        try
        {
            var rulesObj = new JsonObject();
            foreach (var r in AppRules)
            {
                if (!string.IsNullOrWhiteSpace(r.ExecutableName))
                {
                    rulesObj[r.ExecutableName.Trim().ToLowerInvariant()] = r.ProfileIndex;
                }
            }

            var macrosObj = new JsonObject();
            foreach (var kv in Macros)
            {
                var item = kv.Value;
                var mObj = new JsonObject();
                if (item.Mode == "text")
                {
                    mObj["type"] = "text";
                    mObj["text"] = item.TextPayload;
                }
                else if (item.Mode == "rapid_fire")
                {
                    mObj["type"] = "rapid_fire";
                    mObj["button"] = item.MouseButton;
                    mObj["cps"] = item.Cps;
                }
                else if (item.Mode == "recoil")
                {
                    mObj["type"] = "recoil";
                    mObj["cps"] = item.Cps;
                    mObj["dy"] = item.RecoilVerticalPx;
                    mObj["dx"] = item.RecoilHorizontalPx;
                }
                else if (item.Mode == "gshift")
                {
                    mObj["type"] = "gshift";
                    mObj["target_profile"] = item.GShiftProfileIndex;
                }
                else if (item.Mode == "timed")
                {
                    mObj["type"] = "timed";
                    var tArr = new JsonArray();
                    foreach (var st in item.TimedSteps)
                    {
                        tArr.Add(new JsonObject
                        {
                            ["vk"] = st.VkCode,
                            ["label"] = st.KeyLabel,
                            ["up"] = st.IsKeyUp,
                            ["delay"] = st.DelayMs
                        });
                    }
                    mObj["timed_steps"] = tArr;
                }
                else
                {
                    mObj["type"] = "sequence";
                    mObj["ui_shortcut"] = item.ShortcutKeys;
                }
                macrosObj[kv.Key.ToLowerInvariant()] = mObj;
            }

            var root = new JsonObject
            {
                ["schema_version"] = 2,
                ["default_profile"] = DefaultProfileIndex,
                ["auto_switch_enabled"] = AutoProfileSwitchEnabled,
                ["osd_enabled"] = OsdNotificationsEnabled,
                ["app_rules"] = rulesObj,
                ["macros"] = macrosObj
            };

            File.WriteAllText(_configPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
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
                        PollForegroundWindow();
                    }
                    catch
                    {
                        // Ignore transient process handle errors
                    }
                    await Task.Delay(450, token).ConfigureAwait(false);
                }
            }, token);
        }
    }

    public void UpdateRgbParameters(int speedMs, (int R, int G, int B) baseColor, (int R, int G, int B)? secondaryColor = null)
    {
        RgbSpeedMs = Math.Clamp(speedMs, 800, 8000);
        RgbBaseColor = baseColor;
        if (secondaryColor.HasValue)
        {
            RgbSecondaryColor = secondaryColor.Value;
        }
    }

    public void SetRgbEffect(
        string effectName,
        int speedMs,
        (int R, int G, int B) baseColor,
        (int R, int G, int B)? secondaryColor = null)
    {
        ActiveRgbEffect = effectName.ToLowerInvariant();
        RgbSpeedMs = Math.Clamp(speedMs, 800, 8000);
        RgbBaseColor = baseColor;
        if (secondaryColor.HasValue)
        {
            RgbSecondaryColor = secondaryColor.Value;
        }

        _rgbCts?.Cancel();
        _rgbCts?.Dispose();
        _rgbCts = null;

        if (ActiveRgbEffect == "none")
        {
            LivePreviewColor = MapBitTo24(baseColor.R, baseColor.G, baseColor.B);
            LiveTelemetryLevel = 1.0;
            LiveTelemetryText = "Donanım Sabit Rengi (0% CPU)";
            return;
        }

        _rgbCts = new CancellationTokenSource();
        CancellationToken token = _rgbCts.Token;

        Task.Run(() => RunFlickerFreeLightingLoop(token), token);
    }

    private static (byte R, byte G, byte B) MapBitTo24(int r, int g, int b) =>
        (r != 0 ? 1 : 0, g != 0 ? 1 : 0, b != 0 ? 1 : 0) switch
        {
            (0, 0, 0) => (28, 32, 42),
            (1, 0, 0) => (255, 45, 70),
            (1, 1, 0) => (255, 196, 0),
            (0, 1, 0) => (0, 230, 118),
            (0, 1, 1) => (0, 212, 255),
            (0, 0, 1) => (41, 121, 255),
            (1, 0, 1) => (224, 64, 251),
            (1, 1, 1) => (244, 248, 255),
            _ => (0, 212, 255)
        };

    /// <summary>
    /// Returns the natural 4-step Continuous-Anode Luminance Ladder for a given 3-bit color:
    /// Step 0: Deep/Dim (1 LED channel on)
    /// Step 1: Mid-Low (1 LED channel on or secondary)
    /// Step 2: Vibrant (2 LED channels on)
    /// Step 3: Peak Brilliance (3 LED channels on / White)
    /// Because consecutive steps share at least 1 continuously lit LED channel and never pulse
    /// against (0,0,0), transitions are 100% free of stroboscopic dark-gap flicker.
    /// </summary>
    private static (int R, int G, int B)[] BuildContinuousAnodeLadder((int R, int G, int B) primary, (int R, int G, int B) secondary)
    {
        // If user chose two distinct non-black colors, build a Gray-code path between them
        if (secondary != (0, 0, 0) && secondary != primary)
        {
            (int R, int G, int B) mid = (
                primary.R | secondary.R,
                primary.G | secondary.G,
                primary.B | secondary.B
            );
            return [secondary, mid, primary, (1, 1, 1)];
        }

        return (primary.R != 0 ? 1 : 0, primary.G != 0 ? 1 : 0, primary.B != 0 ? 1 : 0) switch
        {
            (0, 1, 1) => [(0, 0, 1), (0, 1, 1), (0, 1, 1), (1, 1, 1)], // Mavi (1x) -> Turkuaz (2x) -> Beyaz (3x)
            (1, 0, 1) => [(0, 0, 1), (1, 0, 0), (1, 0, 1), (1, 1, 1)], // Mavi (1x) -> Kırmızı (1x) -> Macenta (2x) -> Beyaz (3x)
            (1, 1, 0) => [(1, 0, 0), (0, 1, 0), (1, 1, 0), (1, 1, 1)], // Kırmızı (1x) -> Yeşil (1x) -> Sarı (2x) -> Beyaz (3x)
            (1, 0, 0) => [(1, 0, 0), (1, 0, 1), (1, 1, 0), (1, 1, 1)], // Kırmızı (1x) -> Macenta (2x) -> Sarı (2x) -> Beyaz (3x)
            (0, 1, 0) => [(0, 1, 0), (0, 1, 1), (1, 1, 0), (1, 1, 1)], // Yeşil (1x) -> Turkuaz (2x) -> Sarı (2x) -> Beyaz (3x)
            (0, 0, 1) => [(0, 0, 1), (0, 1, 1), (1, 0, 1), (1, 1, 1)], // Mavi (1x) -> Turkuaz (2x) -> Macenta (2x) -> Beyaz (3x)
            _ => [(0, 0, 1), (0, 1, 1), (1, 0, 1), (1, 1, 1)]
        };
    }

    private void RunFlickerFreeLightingLoop(CancellationToken token)
    {
        var sw = Stopwatch.StartNew();
        int lastSentMask = -1;

        // 6-color hardware spectrum wheel in Gray-code order (each step shares an active channel)
        (int R, int G, int B)[] rainbowWheel =
        [
            (1, 0, 0), // Kırmızı
            (1, 1, 0), // Kehribar Sarı
            (0, 1, 0), // Zümrüt Yeşili
            (0, 1, 1), // Turkuaz (Cyan)
            (0, 0, 1), // Kobalt Mavi
            (1, 0, 1)  // Eflatun (Macenta)
        ];

        while (!token.IsCancellationRequested)
        {
            double elapsedSec = sw.Elapsed.TotalSeconds;
            (int R, int G, int B) stateA = RgbBaseColor;

            switch (ActiveRgbEffect)
            {
                case "spectrum":
                case "cycle":
                {
                    double periodSec = Math.Max(1.6, RgbSpeedMs / 1000.0 * 2.0);
                    double pos = ((elapsedSec / periodSec) % 1.0) * rainbowWheel.Length;
                    int idxA = (int)Math.Floor(pos) % rainbowWheel.Length;
                    int idxB = (idxA + 1) % rainbowWheel.Length;
                    double frac = pos - Math.Floor(pos);

                    stateA = rainbowWheel[idxA];
                    var stateB = rainbowWheel[idxB];

                    var c24A = MapBitTo24(stateA.R, stateA.G, stateA.B);
                    var c24B = MapBitTo24(stateB.R, stateB.G, stateB.B);
                    LivePreviewColor = (
                        (byte)(c24A.R + (c24B.R - c24A.R) * frac),
                        (byte)(c24A.G + (c24B.G - c24A.G) * frac),
                        (byte)(c24A.B + (c24B.B - c24A.B) * frac)
                    );
                    LiveTelemetryLevel = pos / rainbowWheel.Length;
                    string colorName = G300sProtocol.GetLedColorName(stateA.R, stateA.G, stateA.B);
                    LiveTelemetryText = $"Gökkuşağı Döngüsü · {colorName}";
                    break;
                }

                case "color_transition":
                case "breathing":
                {
                    var ladder = BuildContinuousAnodeLadder(RgbBaseColor, RgbSecondaryColor);
                    double periodSec = Math.Max(1.2, RgbSpeedMs / 1000.0 * 1.6);
                    double wave = (1.0 - Math.Cos(2.0 * Math.PI * elapsedSec / periodSec)) * 0.5;

                    double scaled = wave * (ladder.Length - 1);
                    int idxA = Math.Clamp((int)Math.Floor(scaled + 0.5), 0, ladder.Length - 1);
                    int idxPrev = Math.Clamp((int)Math.Floor(scaled), 0, ladder.Length - 1);
                    int idxNext = Math.Clamp(idxPrev + 1, 0, ladder.Length - 1);
                    double frac = scaled - idxPrev;

                    stateA = ladder[idxA];
                    var c24A = MapBitTo24(ladder[idxPrev].R, ladder[idxPrev].G, ladder[idxPrev].B);
                    var c24B = MapBitTo24(ladder[idxNext].R, ladder[idxNext].G, ladder[idxNext].B);
                    LivePreviewColor = (
                        (byte)(c24A.R + (c24B.R - c24A.R) * frac),
                        (byte)(c24A.G + (c24B.G - c24A.G) * frac),
                        (byte)(c24A.B + (c24B.B - c24A.B) * frac)
                    );
                    LiveTelemetryLevel = 0.15 + wave * 0.85;
                    string colorName = G300sProtocol.GetLedColorName(stateA.R, stateA.G, stateA.B);
                    LiveTelemetryText = $"Renk Geçişi · {colorName}";
                    break;
                }
            }

            int mask = (stateA.R != 0 ? 1 : 0) | ((stateA.G != 0 ? 1 : 0) << 1) | ((stateA.B != 0 ? 1 : 0) << 2);
            if (_device.IsConnected && mask != lastSentMask)
            {
                try
                {
                    _device.SetLiveLed(stateA.R, stateA.G, stateA.B);
                    lastSentMask = mask;
                }
                catch
                {
                    // Ignore transient HID error
                }
            }

            Thread.Sleep(25);
        }
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
                if (!string.IsNullOrEmpty(exe))
                {
                    ActiveForegroundProcess = exe;
                    if (exe != _lastForegroundExe)
                    {
                        _lastForegroundExe = exe;
                        if (AutoProfileSwitchEnabled && _device.IsConnected)
                        {
                            int targetProfile = DefaultProfileIndex;
                            foreach (var rule in AppRules)
                            {
                                if (string.Equals(rule.ExecutableName, exe, StringComparison.OrdinalIgnoreCase))
                                {
                                    targetProfile = rule.ProfileIndex;
                                    break;
                                }
                            }

                            var state = _device.GetActiveState();
                            if (state.ProfileIndex != targetProfile)
                            {
                                _device.SetActiveProfile(targetProfile);
                                AutoProfileSwitched?.Invoke(targetProfile, exe);
                            }
                        }
                    }
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
            int msg = wParam.ToInt32();
            int vkCode = Marshal.ReadInt32(lParam);

            if (vkCode >= Win32.VK_F13 && vkCode <= Win32.VK_F24)
            {
                string keyName = $"f{13 + (vkCode - Win32.VK_F13)}";
                if (Macros.TryGetValue(keyName, out var macro))
                {
                    bool isDown = msg == Win32.WM_KEYDOWN || msg == Win32.WM_SYSKEYDOWN;
                    bool isUp = msg == Win32.WM_KEYUP || msg == Win32.WM_SYSKEYUP;

                    if (isDown)
                    {
                        HandleMacroDown(vkCode, keyName, macro);
                        return (IntPtr)1;
                    }
                    if (isUp)
                    {
                        HandleMacroUp(vkCode, macro);
                        return (IntPtr)1;
                    }
                }
            }
        }
        return Win32.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private void HandleMacroDown(int vkCode, string keyName, MacroBindingItem macro)
    {
        if (macro.Mode == "rapid_fire")
        {
            if (_rapidFireTasks.ContainsKey(vkCode))
                return;

            var cts = new CancellationTokenSource();
            if (_rapidFireTasks.TryAdd(vkCode, cts))
            {
                MacroKeyTriggered?.Invoke($"{keyName.ToUpperInvariant()} (Seri Tıklama {macro.Cps} CPS)");
                int delayMs = Math.Max(10, 1000 / Math.Clamp(macro.Cps, 1, 50));
                string btn = macro.MouseButton;
                Task.Run(async () =>
                {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        SendMouseClick(btn);
                        await Task.Delay(delayMs, cts.Token).ConfigureAwait(false);
                    }
                }, cts.Token);
            }
        }
        else if (macro.Mode == "recoil")
        {
            if (_rapidFireTasks.ContainsKey(vkCode))
                return;

            var cts = new CancellationTokenSource();
            if (_rapidFireTasks.TryAdd(vkCode, cts))
            {
                int dy = macro.RecoilVerticalPx;
                int dx = macro.RecoilHorizontalPx;
                int cps = Math.Clamp(macro.Cps, 1, 50);
                MacroKeyTriggered?.Invoke($"{keyName.ToUpperInvariant()} (Recoil ΔY:{dy}px, {cps} CPS)");
                int clickIntervalMs = Math.Max(15, 1000 / cps);
                Task.Run(async () =>
                {
                    var sw = Stopwatch.StartNew();
                    long nextClickMs = 0;
                    while (!cts.Token.IsCancellationRequested)
                    {
                        long now = sw.ElapsedMilliseconds;
                        if (now >= nextClickMs)
                        {
                            SendMouseClick("left");
                            nextClickMs = now + clickIntervalMs;
                        }
                        SendMouseMoveRelative(dx, dy);
                        await Task.Delay(16, cts.Token).ConfigureAwait(false);
                    }
                }, cts.Token);
            }
        }
        else if (macro.Mode == "gshift")
        {
            if (_isGShiftActive) return;
            _isGShiftActive = true;
            int targetSlot = Math.Clamp(macro.GShiftProfileIndex, 0, 2);

            if (_device.IsConnected)
            {
                try
                {
                    var st = _device.GetActiveState();
                    _preGShiftProfileIndex = st.ProfileIndex;
                    if (_preGShiftProfileIndex != targetSlot)
                    {
                        _device.SetActiveProfile(targetSlot);
                    }
                }
                catch
                {
                    _preGShiftProfileIndex = DefaultProfileIndex;
                }
            }
            MacroKeyTriggered?.Invoke($"G-Shift Aktif → Profil {targetSlot + 1}");
            GShiftStateChanged?.Invoke(true, targetSlot);
        }
        else if (macro.Mode == "timed")
        {
            if (macro.TimedSteps.Count == 0) return;
            var stepsCopy = new List<TimedMacroStep>(macro.TimedSteps);
            MacroKeyTriggered?.Invoke($"{keyName.ToUpperInvariant()} (Zamanlamalı: {stepsCopy.Count} adım)");
            Task.Run(async () =>
            {
                foreach (var st in stepsCopy)
                {
                    if (st.DelayMs > 0)
                    {
                        await Task.Delay(st.DelayMs).ConfigureAwait(false);
                    }
                    if (st.VkCode != 0)
                    {
                        SendVkKey(st.VkCode, st.IsKeyUp);
                    }
                }
            });
        }
        else if (macro.Mode == "text")
        {
            MacroKeyTriggered?.Invoke($"{keyName.ToUpperInvariant()} (Metin: {macro.TextPayload})");
            string text = macro.TextPayload;
            Task.Run(() => SendUnicodeText(text));
        }
        else if (macro.Mode == "shortcut")
        {
            MacroKeyTriggered?.Invoke($"{keyName.ToUpperInvariant()} (Kısayol: {macro.ShortcutKeys})");
            string sc = macro.ShortcutKeys;
            Task.Run(() => SendShortcutSequence(sc));
        }
    }

    private void HandleMacroUp(int vkCode, MacroBindingItem macro)
    {
        if ((macro.Mode == "rapid_fire" || macro.Mode == "recoil") && _rapidFireTasks.TryRemove(vkCode, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
        else if (macro.Mode == "gshift" && _isGShiftActive)
        {
            _isGShiftActive = false;
            int restoreSlot = _preGShiftProfileIndex >= 0 ? _preGShiftProfileIndex : DefaultProfileIndex;
            if (_device.IsConnected)
            {
                try
                {
                    _device.SetActiveProfile(restoreSlot);
                }
                catch
                {
                    // Ignore transient error
                }
            }
            MacroKeyTriggered?.Invoke($"G-Shift Bırakıldı → Profil {restoreSlot + 1}");
            GShiftStateChanged?.Invoke(false, restoreSlot);
        }
    }

    private static void SendMouseMoveRelative(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        var move = new Win32.INPUT[1];
        move[0].type = Win32.INPUT_MOUSE;
        move[0].U.mi.dx = dx;
        move[0].U.mi.dy = dy;
        move[0].U.mi.dwFlags = Win32.MOUSEEVENTF_MOVE;
        Win32.SendInput(1, move, Marshal.SizeOf<Win32.INPUT>());
    }

    private static void SendUnicodeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        foreach (char ch in text)
        {
            var inputs = new Win32.INPUT[2];
            inputs[0].type = Win32.INPUT_KEYBOARD;
            inputs[0].U.ki.wScan = ch;
            inputs[0].U.ki.dwFlags = Win32.KEYEVENTF_UNICODE;

            inputs[1].type = Win32.INPUT_KEYBOARD;
            inputs[1].U.ki.wScan = ch;
            inputs[1].U.ki.dwFlags = Win32.KEYEVENTF_UNICODE | Win32.KEYEVENTF_KEYUP;

            Win32.SendInput(2, inputs, Marshal.SizeOf<Win32.INPUT>());
        }
    }

    private static void SendShortcutSequence(string shortcut)
    {
        string[] parts = shortcut.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<ushort> vks = [];
        foreach (string p in parts)
        {
            ushort vk = MapNameToVk(p.ToLowerInvariant());
            if (vk != 0) vks.Add(vk);
        }

        if (vks.Count == 0) return;

        foreach (ushort vk in vks)
        {
            SendVkKey(vk, false);
        }
        Thread.Sleep(15);
        for (int i = vks.Count - 1; i >= 0; i--)
        {
            SendVkKey(vks[i], true);
        }
    }

    private static void SendVkKey(ushort vk, bool keyUp)
    {
        var input = new Win32.INPUT[1];
        input[0].type = Win32.INPUT_KEYBOARD;
        input[0].U.ki.wVk = vk;
        input[0].U.ki.dwFlags = keyUp ? Win32.KEYEVENTF_KEYUP : 0;
        Win32.SendInput(1, input, Marshal.SizeOf<Win32.INPUT>());
    }

    private static ushort MapNameToVk(string name) => name switch
    {
        "ctrl" or "lctrl" => 0xA2,
        "shift" or "lshift" => 0xA0,
        "alt" or "lalt" => 0xA4,
        "win" or "lwin" => 0x5B,
        "enter" => 0x0D,
        "esc" or "escape" => 0x1B,
        "tab" => 0x09,
        "space" => 0x20,
        "backspace" => 0x08,
        "delete" or "del" => 0x2E,
        "up" => 0x26,
        "down" => 0x28,
        "left" => 0x25,
        "right" => 0x27,
        _ when name.Length == 1 && char.IsLetterOrDigit(name[0]) => (ushort)char.ToUpperInvariant(name[0]),
        _ => 0
    };

    private static void SendMouseClick(string button)
    {
        (uint downFlag, uint upFlag) = button.ToLowerInvariant() switch
        {
            "right" => (Win32.MOUSEEVENTF_RIGHTDOWN, Win32.MOUSEEVENTF_RIGHTUP),
            "middle" => (Win32.MOUSEEVENTF_MIDDLEDOWN, Win32.MOUSEEVENTF_MIDDLEUP),
            _ => (Win32.MOUSEEVENTF_LEFTDOWN, Win32.MOUSEEVENTF_LEFTUP)
        };

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

        public const uint MOUSEEVENTF_MOVE = 0x0001;
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

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [DllImport("winmm.dll")]
        public static extern uint timeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll")]
        public static extern uint timeEndPeriod(uint uPeriod);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr ho);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern int SetStretchBltMode(IntPtr hdc, int mode);

        [DllImport("gdi32.dll")]
        public static extern bool StretchBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest, IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, uint rop);

        [DllImport("gdi32.dll")]
        public static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, [Out] byte[] lpvBits, ref BITMAPINFOHEADER lpbmi, uint usage);

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
