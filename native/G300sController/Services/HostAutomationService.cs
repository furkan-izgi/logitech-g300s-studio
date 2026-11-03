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

namespace G300sController.Services;

public sealed class MacroBindingItem
{
    public string TriggerKey { get; set; } = "f13";
    public string Mode { get; set; } = "text"; // "text", "rapid_fire", "shortcut"
    public string TextPayload { get; set; } = "";
    public string MouseButton { get; set; } = "left";
    public int Cps { get; set; } = 12;
    public string ShortcutKeys { get; set; } = "ctrl+c";
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

    public bool AutoProfileSwitchEnabled { get; set; }
    public int DefaultProfileIndex { get; set; }
    public List<AppProfileRule> AppRules { get; } = [];
    public Dictionary<string, MacroBindingItem> Macros { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string ActiveRgbEffect { get; private set; } = "none";
    public int RgbSpeedMs { get; private set; } = 2800;
    public (int R, int G, int B) RgbBaseColor { get; private set; } = (0, 1, 1);
    public (int R, int G, int B) RgbSecondaryColor { get; private set; } = (0, 0, 1);

    // "full", "crosshair", "hud", "cinema"
    public string AmbilightZone { get; set; } = "full";
    public bool UseHighFreqBridge { get; set; } = true;

    // Real-time 24-bit color & telemetry exposed for 30 FPS UI preview
    public (byte R, byte G, byte B) LivePreviewColor { get; private set; } = (0, 212, 255);
    public (byte R, byte G, byte B)[] LiveScenePalette { get; private set; } =
    [
        (0, 212, 255),
        (41, 121, 255),
        (224, 64, 251)
    ];
    public double LiveTelemetryLevel { get; private set; } = 1.0;
    public string LiveTelemetryText { get; private set; } = "Donanım Sabit Rengi";
    public string ActiveForegroundProcess { get; private set; } = "Masaüstü";

    public event Action<int, string>? AutoProfileSwitched;
    public event Action<string>? MacroKeyTriggered;

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
        Macros["f15"] = new MacroBindingItem { TriggerKey = "f15", Mode = "text", TextPayload = "gg wp!" };
        Macros["f16"] = new MacroBindingItem { TriggerKey = "f16", Mode = "rapid_fire", MouseButton = "left", Cps = 15 };
        Macros["f17"] = new MacroBindingItem { TriggerKey = "f17", Mode = "rapid_fire", MouseButton = "right", Cps = 10 };
        Macros["f18"] = new MacroBindingItem { TriggerKey = "f18", Mode = "text", TextPayload = "gl hf!" };
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
                    else if (mType == "sequence")
                    {
                        item.Mode = "shortcut";
                        item.ShortcutKeys = mObj["ui_shortcut"]?.GetValue<string>() ?? "ctrl+c";
                    }

                    Macros[key] = item;
                }
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
                else
                {
                    mObj["type"] = "sequence";
                    mObj["ui_shortcut"] = item.ShortcutKeys;
                    var steps = new JsonArray();
                    string[] parts = item.ShortcutKeys.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    for (int i = 0; i < parts.Length - 1; i++)
                    {
                        steps.Add(new JsonObject { ["type"] = "press", ["key"] = parts[i].ToLowerInvariant() });
                    }
                    if (parts.Length > 0)
                    {
                        steps.Add(new JsonObject { ["type"] = "tap", ["key"] = parts[^1].ToLowerInvariant() });
                    }
                    for (int i = parts.Length - 2; i >= 0; i--)
                    {
                        steps.Add(new JsonObject { ["type"] = "release", ["key"] = parts[i].ToLowerInvariant() });
                    }
                    mObj["steps"] = steps;
                }
                macrosObj[kv.Key.ToLowerInvariant()] = mObj;
            }

            var root = new JsonObject
            {
                ["default_profile"] = DefaultProfileIndex,
                ["auto_switch_enabled"] = AutoProfileSwitchEnabled,
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
        Win32.timeBeginPeriod(1);
        var sw = Stopwatch.StartNew();

        int lastSentMask = -1;
        long lastSampleMs = -100;

        // Smoothed Ambilight HSV/RGB state with hysteresis
        double smoothR = 0.0, smoothG = 0.85, smoothB = 1.0;
        (int R, int G, int B) currentAmbiHwColor = (0, 1, 1);
        long lastAmbiSwitchMs = 0;

        // Game state telemetry variables
        double barrelHeat = 0.0;
        double damageAlertLevel = 0.0;
        double flashbangLevel = 0.0;
        double reloadProgress = 0.0;
        double abilityPulse = 0.0;
        double smoothAudioPeak = 0.0;
        bool wasLmbDown = false;

        // Gray-code 6-color wheel where every adjacent pair shares 1 continuously lit LED channel
        (int R, int G, int B)[] grayWheel =
        [
            (1, 0, 0), // Kırmızı
            (1, 1, 0), // Sarı     (R ortak)
            (0, 1, 0), // Yeşil    (G ortak)
            (0, 1, 1), // Turkuaz  (G ortak)
            (0, 0, 1), // Mavi     (B ortak)
            (1, 0, 1)  // Macenta  (B/R ortak)
        ];

        try
        {
            while (!token.IsCancellationRequested)
            {
                double elapsedSec = sw.Elapsed.TotalSeconds;
                long elapsedMs = sw.ElapsedMilliseconds;

                (int R, int G, int B) stateA = RgbBaseColor;
                (int R, int G, int B) stateB = RgbBaseColor;
                bool isBridging = false;

                switch (ActiveRgbEffect)
                {
                    case "breathing":
                    {
                        var ladder = BuildContinuousAnodeLadder(RgbBaseColor, RgbSecondaryColor);
                        double periodSec = Math.Max(1.0, RgbSpeedMs / 1000.0);
                        double wave = (1.0 - Math.Cos(2.0 * Math.PI * elapsedSec / periodSec)) * 0.5;

                        // Map wave [0..1] onto the 4 continuous-anode ladder states [0..3]
                        double scaled = wave * (ladder.Length - 1);
                        int idxA = Math.Clamp((int)Math.Floor(scaled), 0, ladder.Length - 1);
                        int idxB = Math.Clamp(idxA + 1, 0, ladder.Length - 1);
                        double frac = scaled - idxA;

                        stateA = ladder[idxA];
                        stateB = ladder[idxB];

                        // Only use 166.7 Hz 1:1 bridge in the center 30% transition zone when both states share an ON bit
                        bool sharesCommonLed = ((stateA.R & stateB.R) | (stateA.G & stateB.G) | (stateA.B & stateB.B)) != 0;
                        if (UseHighFreqBridge && stateA != stateB && sharesCommonLed && frac is > 0.35 and < 0.65)
                        {
                            isBridging = true;
                        }
                        else if (frac >= 0.5)
                        {
                            stateA = stateB;
                        }

                        var c24A = MapBitTo24(stateA.R, stateA.G, stateA.B);
                        var c24B = MapBitTo24(stateB.R, stateB.G, stateB.B);
                        LivePreviewColor = (
                            (byte)(c24A.R + (c24B.R - c24A.R) * frac),
                            (byte)(c24A.G + (c24B.G - c24A.G) * frac),
                            (byte)(c24A.B + (c24B.B - c24A.B) * frac)
                        );
                        LiveTelemetryLevel = 0.25 + wave * 0.75;
                        LiveTelemetryText = $"Yumuşak Geçiş · Yoğunluk %{(int)Math.Round((0.25 + wave * 0.75) * 100)}";
                        break;
                    }

                    case "spectrum":
                    case "cycle":
                    {
                        double periodSec = Math.Max(1.6, RgbSpeedMs / 1000.0 * 2.2);
                        double pos = ((elapsedSec / periodSec) % 1.0) * grayWheel.Length;
                        int idxA = (int)Math.Floor(pos) % grayWheel.Length;
                        int idxB = (idxA + 1) % grayWheel.Length;
                        double frac = pos - Math.Floor(pos);

                        stateA = grayWheel[idxA];
                        stateB = grayWheel[idxB];

                        if (UseHighFreqBridge && frac is > 0.36 and < 0.64)
                        {
                            isBridging = true;
                        }
                        else if (frac >= 0.5)
                        {
                            stateA = stateB;
                        }

                        var c24A = MapBitTo24(stateA.R, stateA.G, stateA.B);
                        var c24B = MapBitTo24(stateB.R, stateB.G, stateB.B);
                        LivePreviewColor = (
                            (byte)(c24A.R + (c24B.R - c24A.R) * frac),
                            (byte)(c24A.G + (c24B.G - c24A.G) * frac),
                            (byte)(c24A.B + (c24B.B - c24A.B) * frac)
                        );
                        LiveTelemetryLevel = pos / grayWheel.Length;
                        LiveTelemetryText = $"Spektrum Dalgası · {(int)(pos / grayWheel.Length * 360)}°";
                        break;
                    }

                    case "ambilight":
                    {
                        if (elapsedMs - lastSampleMs >= 45)
                        {
                            lastSampleMs = elapsedMs;
                            var sample = SampleScreenZone(AmbilightZone);
                            const double alpha = 0.22;
                            smoothR += (sample.R - smoothR) * alpha;
                            smoothG += (sample.G - smoothG) * alpha;
                            smoothB += (sample.B - smoothB) * alpha;
                            LiveScenePalette = sample.Palette;

                            // Classify to 3-bit hardware color with 180ms minimum hold hysteresis to prevent boundary flicker
                            if (elapsedMs - lastAmbiSwitchMs >= 180)
                            {
                                var candidate = ClassifyRgbToHardwareColor(smoothR, smoothG, smoothB, currentAmbiHwColor);
                                if (candidate != currentAmbiHwColor)
                                {
                                    currentAmbiHwColor = candidate;
                                    lastAmbiSwitchMs = elapsedMs;
                                }
                            }
                        }

                        stateA = currentAmbiHwColor;
                        var hwUi = MapBitTo24(stateA.R, stateA.G, stateA.B);
                        byte rawHexR = (byte)Math.Clamp((int)(smoothR * 255), 0, 255);
                        byte rawHexG = (byte)Math.Clamp((int)(smoothG * 255), 0, 255);
                        byte rawHexB = (byte)Math.Clamp((int)(smoothB * 255), 0, 255);

                        LivePreviewColor = (
                            (byte)Math.Clamp((int)(rawHexR * 0.45 + hwUi.R * 0.55), 28, 255),
                            (byte)Math.Clamp((int)(rawHexG * 0.45 + hwUi.G * 0.55), 28, 255),
                            (byte)Math.Clamp((int)(rawHexB * 0.45 + hwUi.B * 0.55), 36, 255)
                        );
                        LiveTelemetryLevel = Math.Max(0.25, Math.Max(smoothR, Math.Max(smoothG, smoothB)));
                        string zoneTr = AmbilightZone switch
                        {
                            "crosshair" => "Merkez Nişangah",
                            "hud" => "Alt HUD Barı",
                            "cinema" => "Sinematik Kenar",
                            _ => "Tam Ekran"
                        };
                        string hwName = G300sProtocol.GetLedColorName(stateA.R, stateA.G, stateA.B);
                        LiveTelemetryText = $"Ortam ({zoneTr}): #{rawHexR:X2}{rawHexG:X2}{rawHexB:X2} → {hwName}";
                        break;
                    }

                    case "game_combat":
                    {
                        if (elapsedMs - lastSampleMs >= 25)
                        {
                            double dt = Math.Max(0.01, (elapsedMs - lastSampleMs) / 1000.0);
                            lastSampleMs = elapsedMs;

                            bool lmbDown = (Win32.GetAsyncKeyState(0x01) & 0x8000) != 0;
                            bool rmbDown = (Win32.GetAsyncKeyState(0x02) & 0x8000) != 0;
                            bool rKeyDown = (Win32.GetAsyncKeyState('R') & 0x8000) != 0;
                            bool abilityDown = ((Win32.GetAsyncKeyState('Q') & 0x8000) != 0)
                                || ((Win32.GetAsyncKeyState('E') & 0x8000) != 0)
                                || ((Win32.GetAsyncKeyState('G') & 0x8000) != 0);

                            if (lmbDown)
                            {
                                barrelHeat = Math.Min(1.0, barrelHeat + dt * 0.70);
                                if (!wasLmbDown) barrelHeat = Math.Min(1.0, barrelHeat + 0.12);
                            }
                            else
                            {
                                barrelHeat = Math.Max(0.0, barrelHeat - dt * 0.42);
                            }
                            wasLmbDown = lmbDown;

                            if (rKeyDown && reloadProgress <= 0.01)
                            {
                                reloadProgress = 1.0;
                            }
                            if (reloadProgress > 0.0)
                            {
                                reloadProgress = Math.Max(0.0, reloadProgress - dt * 0.65);
                            }

                            if (abilityDown)
                            {
                                abilityPulse = 1.0;
                            }
                            else
                            {
                                abilityPulse = Math.Max(0.0, abilityPulse - dt * 2.5);
                            }

                            // Sample screen for red damage vignette & white flashbang every ~75ms
                            if ((elapsedMs / 25) % 3 == 0)
                            {
                                var sample = SampleScreenZone("full");
                                LiveScenePalette = sample.Palette;
                                damageAlertLevel = sample.DamageVignette > damageAlertLevel
                                    ? sample.DamageVignette
                                    : Math.Max(0.0, damageAlertLevel - 0.10);
                                flashbangLevel = sample.WhiteoutLevel > flashbangLevel
                                    ? sample.WhiteoutLevel
                                    : Math.Max(0.0, flashbangLevel - 0.15);
                            }

                            // Choose solid hardware state based on game priority hierarchy
                            if (flashbangLevel > 0.82)
                            {
                                stateA = (1, 1, 1); // Whiteout / Flashbang
                                LiveTelemetryLevel = 1.0;
                                LiveTelemetryText = $"[{ActiveForegroundProcess}] Flashbang / Yüksek Parlama!";
                            }
                            else if (damageAlertLevel > 0.36)
                            {
                                stateA = (1, 0, 0); // Crimson Red Alert
                                LiveTelemetryLevel = damageAlertLevel;
                                LiveTelemetryText = $"[{ActiveForegroundProcess}] Kritik Hasar / Düşük Can Uyarısı!";
                            }
                            else if (reloadProgress > 0.05)
                            {
                                stateA = reloadProgress > 0.5 ? (1, 1, 0) : (0, 1, 0); // Amber -> Green Ready
                                LiveTelemetryLevel = 1.0 - reloadProgress;
                                LiveTelemetryText = $"[{ActiveForegroundProcess}] Şarjör Yenileniyor (%{(int)((1.0 - reloadProgress) * 100)})";
                            }
                            else if (abilityPulse > 0.2)
                            {
                                stateA = (1, 0, 1); // Magenta Tactical Ability
                                LiveTelemetryLevel = abilityPulse;
                                LiveTelemetryText = $"[{ActiveForegroundProcess}] Taktik Yetenek Tetiklendi";
                            }
                            else if (barrelHeat > 0.08)
                            {
                                stateA = barrelHeat switch
                                {
                                    < 0.35 => (0, 1, 0), // Yeşil
                                    < 0.68 => (1, 1, 0), // Kehribar Sarı
                                    < 0.90 => (1, 0, 0), // Kırmızı
                                    _ => (1, 1, 1)       // Aşırı Isınma Beyaz/Kırmızı
                                };
                                LiveTelemetryLevel = barrelHeat;
                                LiveTelemetryText = $"[{ActiveForegroundProcess}] Çatışma · Namlu Isısı %{(int)(barrelHeat * 100)}";
                            }
                            else if (rmbDown)
                            {
                                stateA = (0, 0, 1); // Deep Cobalt Sniper Focus
                                LiveTelemetryLevel = 0.6;
                                LiveTelemetryText = $"[{ActiveForegroundProcess}] Keskin Nişancı / Dürbün Odak (ADS)";
                            }
                            else
                            {
                                stateA = RgbBaseColor;
                                LiveTelemetryLevel = 0.25;
                                LiveTelemetryText = $"[{ActiveForegroundProcess}] Taktik Bekleme · Durum İzleniyor";
                            }

                            LivePreviewColor = MapBitTo24(stateA.R, stateA.G, stateA.B);
                        }
                        break;
                    }

                    case "audio_reactive":
                    {
                        if (elapsedMs - lastSampleMs >= 25)
                        {
                            lastSampleMs = elapsedMs;
                            double rawPeak = SampleSystemAudioPeak();
                            smoothAudioPeak = rawPeak > smoothAudioPeak
                                ? smoothAudioPeak + (rawPeak - smoothAudioPeak) * 0.65
                                : smoothAudioPeak * 0.85;
                        }

                        var ladder = BuildContinuousAnodeLadder(RgbBaseColor, RgbSecondaryColor);
                        double level = Math.Clamp(smoothAudioPeak * 1.4, 0.0, 1.0);
                        int idx = Math.Clamp((int)Math.Floor(level * ladder.Length), 0, ladder.Length - 1);
                        stateA = level > 0.82 ? (1, 0, 0) : ladder[idx];

                        LivePreviewColor = MapBitTo24(stateA.R, stateA.G, stateA.B);
                        LiveTelemetryLevel = level;
                        LiveTelemetryText = $"Ses & Patlama Şiddeti: %{(int)Math.Round(level * 100)}";
                        break;
                    }
                }

                // Send to physical hardware without flicker
                if (isBridging && _device.IsConnected)
                {
                    // Short 18ms burst of 166.7 Hz 1:1 USB-clocked alternating bridge (where shared LED stays 100% ON)
                    for (int k = 0; k < 6 && !token.IsCancellationRequested; k++)
                    {
                        var c = (k & 1) == 0 ? stateA : stateB;
                        int m = (c.R != 0 ? 1 : 0) | ((c.G != 0 ? 1 : 0) << 1) | ((c.B != 0 ? 1 : 0) << 2);
                        try
                        {
                            _device.SetLiveLed(c.R, c.G, c.B);
                            lastSentMask = m;
                        }
                        catch
                        {
                            break;
                        }
                    }
                }
                else
                {
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
                    Thread.Sleep(18);
                }
            }
        }
        finally
        {
            Win32.timeEndPeriod(1);
        }
    }

    /// <summary>
    /// Maps normalized linear RGB (0..1) to the closest 3-bit hardware color with Schmitt-trigger hysteresis
    /// so the physical LED never flickers when the game scene sits near a color threshold.
    /// </summary>
    private static (int R, int G, int B) ClassifyRgbToHardwareColor(
        double r,
        double g,
        double b,
        (int R, int G, int B) current)
    {
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double sat = max > 0.05 ? (max - min) / max : 0.0;

        if (max < 0.12) return (0, 0, 1); // Very dark scene -> Deep Cobalt ambient
        if (sat < 0.22 && max > 0.72) return (1, 1, 1); // Bright low-saturation scene -> Pure White

        // Normalize by max channel and apply hysteresis threshold around 0.58
        double nr = r / max;
        double ng = g / max;
        double nb = b / max;

        double threshR = current.R != 0 ? 0.48 : 0.62;
        double threshG = current.G != 0 ? 0.48 : 0.62;
        double threshB = current.B != 0 ? 0.48 : 0.62;

        int bitR = nr >= threshR ? 1 : 0;
        int bitG = ng >= threshG ? 1 : 0;
        int bitB = nb >= threshB ? 1 : 0;

        if (bitR == 0 && bitG == 0 && bitB == 0)
        {
            if (nr >= ng && nr >= nb) bitR = 1;
            else if (ng >= nr && ng >= nb) bitG = 1;
            else bitB = 1;
        }

        return (bitR, bitG, bitB);
    }

    private sealed record ScreenZoneSample(
        double R,
        double G,
        double B,
        double DamageVignette,
        double WhiteoutLevel,
        (byte R, byte G, byte B)[] Palette);

    private static ScreenZoneSample SampleScreenZone(string zoneMode)
    {
        const int sampleW = 24;
        const int sampleH = 24;

        IntPtr hdcScreen = Win32.GetDC(IntPtr.Zero);
        if (hdcScreen == IntPtr.Zero)
        {
            return new ScreenZoneSample(0.0, 0.8, 1.0, 0.0, 0.0, [(0, 212, 255), (41, 121, 255), (224, 64, 251)]);
        }

        IntPtr hdcMem = Win32.CreateCompatibleDC(hdcScreen);
        IntPtr hBitmap = Win32.CreateCompatibleBitmap(hdcScreen, sampleW, sampleH);
        IntPtr hOld = Win32.SelectObject(hdcMem, hBitmap);

        try
        {
            int scrW = Win32.GetSystemMetrics(0);
            int scrH = Win32.GetSystemMetrics(1);

            int srcX = 0, srcY = 0, srcW = scrW, srcH = scrH;
            if (zoneMode == "crosshair")
            {
                srcW = (int)(scrW * 0.36);
                srcH = (int)(scrH * 0.36);
                srcX = (scrW - srcW) / 2;
                srcY = (scrH - srcH) / 2;
            }
            else if (zoneMode == "hud")
            {
                srcW = (int)(scrW * 0.70);
                srcH = (int)(scrH * 0.24);
                srcX = (scrW - srcW) / 2;
                srcY = scrH - srcH;
            }

            Win32.SetStretchBltMode(hdcMem, 3);
            Win32.StretchBlt(hdcMem, 0, 0, sampleW, sampleH, hdcScreen, srcX, srcY, srcW, srcH, 0x00CC0020);

            var bmi = new Win32.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
                biWidth = sampleW,
                biHeight = -sampleH,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0
            };

            byte[] pixels = new byte[sampleW * sampleH * 4];
            Win32.GetDIBits(hdcMem, hBitmap, 0, sampleH, pixels, ref bmi, 0);

            double sumR = 0.0, sumG = 0.0, sumB = 0.0, weightSum = 0.001;
            double edgeRedSum = 0.0;
            double whiteSum = 0.0;
            int edgeCount = 0;

            // 3 regional buckets (Left/Top, Center, Right/Bottom) for live palette bar
            double[] bucketR = new double[3];
            double[] bucketG = new double[3];
            double[] bucketB = new double[3];
            double[] bucketW = [0.001, 0.001, 0.001];

            for (int y = 0; y < sampleH; y++)
            {
                for (int x = 0; x < sampleW; x++)
                {
                    if (zoneMode == "cinema" && x > 4 && x < sampleW - 5 && y > 4 && y < sampleH - 5)
                        continue;

                    int idx = (y * sampleW + x) * 4;
                    double b = pixels[idx + 0] / 255.0;
                    double g = pixels[idx + 1] / 255.0;
                    double r = pixels[idx + 2] / 255.0;

                    double max = Math.Max(r, Math.Max(g, b));
                    double min = Math.Min(r, Math.Min(g, b));
                    double sat = max > 0.05 ? (max - min) / max : 0.0;

                    double weight = 0.2 + sat * sat * 2.8 + max * 0.5;
                    sumR += r * weight;
                    sumG += g * weight;
                    sumB += b * weight;
                    weightSum += weight;

                    int bIdx = (x * 3) / sampleW;
                    bucketR[bIdx] += r * weight;
                    bucketG[bIdx] += g * weight;
                    bucketB[bIdx] += b * weight;
                    bucketW[bIdx] += weight;

                    if (min > 0.85) whiteSum += 1.0;

                    if (x <= 2 || x >= sampleW - 3 || y <= 2 || y >= sampleH - 3)
                    {
                        edgeCount++;
                        if (r > 0.45 && r > (g + b) * 1.35)
                        {
                            edgeRedSum += (r - Math.Max(g, b));
                        }
                    }
                }
            }

            double avgR = sumR / weightSum;
            double avgG = sumG / weightSum;
            double avgB = sumB / weightSum;

            double maxC = Math.Max(avgR, Math.Max(avgG, avgB));
            if (maxC > 0.025)
            {
                avgR /= maxC;
                avgG /= maxC;
                avgB /= maxC;
            }

            var palette = new (byte R, byte G, byte B)[3];
            for (int i = 0; i < 3; i++)
            {
                double pr = bucketR[i] / bucketW[i];
                double pg = bucketG[i] / bucketW[i];
                double pb = bucketB[i] / bucketW[i];
                double pm = Math.Max(pr, Math.Max(pg, pb));
                if (pm > 0.025) { pr /= pm; pg /= pm; pb /= pm; }
                palette[i] = (
                    (byte)Math.Clamp((int)(pr * 255), 40, 255),
                    (byte)Math.Clamp((int)(pg * 255), 40, 255),
                    (byte)Math.Clamp((int)(pb * 255), 48, 255)
                );
            }

            double damageRatio = edgeCount > 0 ? Math.Clamp(edgeRedSum / (edgeCount * 0.26), 0.0, 1.0) : 0.0;
            double whiteRatio = Math.Clamp(whiteSum / (sampleW * sampleH), 0.0, 1.0);
            return new ScreenZoneSample(avgR, avgG, avgB, damageRatio, whiteRatio, palette);
        }
        catch
        {
            return new ScreenZoneSample(0.0, 0.8, 1.0, 0.0, 0.0, [(0, 212, 255), (41, 121, 255), (224, 64, 251)]);
        }
        finally
        {
            Win32.SelectObject(hdcMem, hOld);
            Win32.DeleteObject(hBitmap);
            Win32.DeleteDC(hdcMem);
            Win32.ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    private static double SampleSystemAudioPeak()
    {
        try
        {
            return CoreAudioMeter.GetMasterPeak();
        }
        catch
        {
            return 0.0;
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
                MacroKeyTriggered?.Invoke($"{keyName.ToUpperInvariant()} (Rapid-Fire {macro.Cps} CPS)");
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
        if (macro.Mode == "rapid_fire" && _rapidFireTasks.TryRemove(vkCode, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
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

    private static class CoreAudioMeter
    {
        private static IAudioMeterInformation? _meter;
        private static bool _initAttempted;

        public static double GetMasterPeak()
        {
            if (!_initAttempted)
            {
                _initAttempted = true;
                try
                {
                    var enumeratorType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
                    if (enumeratorType is not null)
                    {
                        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType)!;
                        enumerator.GetDefaultAudioEndpoint(0, 1, out IMMDevice device);
                        Guid iidMeter = typeof(IAudioMeterInformation).GUID;
                        device.Activate(ref iidMeter, 23, IntPtr.Zero, out object meterObj);
                        _meter = (IAudioMeterInformation)meterObj;
                    }
                }
                catch
                {
                    _meter = null;
                }
            }

            if (_meter is null) return 0.0;
            _meter.GetPeakValue(out float peak);
            return Math.Clamp(peak, 0f, 1f);
        }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr ppDevices);
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        }

        [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioMeterInformation
        {
            int GetPeakValue(out float pfPeak);
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
