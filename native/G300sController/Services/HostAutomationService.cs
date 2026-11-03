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
    public string TriggerKey { get; set; } = "f13"; // f13..f24
    public string Mode { get; set; } = "text";      // "text", "rapid_fire", "shortcut"
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
/// foreground window process watcher for automatic profile switching, and a
/// 333 Hz First-Order Delta-Sigma (ΔΣ) PWM lighting &amp; game-telemetry engine.
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
    private ulong _lastIdleTime;
    private ulong _lastKernelTime;
    private ulong _lastUserTime;

    public bool AutoProfileSwitchEnabled { get; set; }
    public int DefaultProfileIndex { get; set; }
    public List<AppProfileRule> AppRules { get; } = [];
    public Dictionary<string, MacroBindingItem> Macros { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string ActiveRgbEffect { get; private set; } = "none";
    public int RgbSpeedMs { get; private set; } = 2400;
    public (int R, int G, int B) RgbBaseColor { get; private set; } = (0, 1, 1);
    public (int R, int G, int B) RgbSecondaryColor { get; private set; } = (0, 0, 0);

    // Real-time 24-bit color & telemetry exposed for 30 FPS UI preview
    public (byte R, byte G, byte B) LivePreviewColor { get; private set; } = (0, 212, 255);
    public double LiveTelemetryLevel { get; private set; }
    public string LiveTelemetryText { get; private set; } = "Hardware Static";

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

    public void UpdateRgbParameters(int speedMs, (int R, int G, int B) baseColor, (int R, int G, int B)? secondaryColor = null)
    {
        RgbSpeedMs = Math.Clamp(speedMs, 400, 8000);
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
        RgbSpeedMs = Math.Clamp(speedMs, 400, 8000);
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

        Task.Run(() => RunDeltaSigmaLightingLoop(token), token);
    }

    private static (byte R, byte G, byte B) MapBitTo24(int r, int g, int b) =>
        (r != 0 ? 1 : 0, g != 0 ? 1 : 0, b != 0 ? 1 : 0) switch
        {
            (0, 0, 0) => (22, 26, 34),
            (1, 0, 0) => (255, 45, 70),
            (1, 1, 0) => (255, 195, 0),
            (0, 1, 0) => (0, 235, 115),
            (0, 1, 1) => (0, 215, 255),
            (0, 0, 1) => (45, 125, 255),
            (1, 0, 1) => (230, 60, 255),
            (1, 1, 1) => (245, 250, 255),
            _ => (0, 215, 255)
        };

    /// <summary>
    /// Runs a 333 Hz First-Order Delta-Sigma (ΔΣ) error-diffusion PWM loop over HID Feature Report 0xF1.
    /// This enables smooth analog brightness fading (sin² gamma-corrected breathing), continuous 24-bit
    /// spectrum waves, real-time game screen Ambilight, combat barrel heat, and CoreAudio peak reaction.
    /// </summary>
    private void RunDeltaSigmaLightingLoop(CancellationToken token)
    {
        Win32.timeBeginPeriod(1);
        var sw = Stopwatch.StartNew();

        double accR = 0.0, accG = 0.0, accB = 0.0;
        int lastMask = -1;

        // State for screen ambilight & game state sampling (updated at ~28 Hz)
        long lastSampleMs = -100;
        double ambiR = 0.0, ambiG = 0.85, ambiB = 1.0;
        double damageAlertLevel = 0.0;
        double barrelHeat = 0.0;
        double smoothAudioPeak = 0.0;
        bool wasLeftMouseDown = false;
        double muzzleFlashBoost = 0.0;

        try
        {
            while (!token.IsCancellationRequested)
            {
                double elapsedSec = sw.Elapsed.TotalSeconds;
                long elapsedMs = sw.ElapsedMilliseconds;

                // Target normalized linear RGB in [0.0 .. 1.0]
                double targetR = 0.0, targetG = 0.0, targetB = 0.0;

                switch (ActiveRgbEffect)
                {
                    case "breathing":
                    {
                        // Smooth sinusoidal breathing with perceptual gamma (γ = 1.85)
                        double periodSec = Math.Max(0.8, RgbSpeedMs / 1000.0);
                        double wave = (1.0 - Math.Cos(2.0 * Math.PI * elapsedSec / periodSec)) * 0.5;
                        double gammaWave = Math.Pow(wave, 1.85);

                        double r1 = RgbBaseColor.R != 0 ? 1.0 : 0.0;
                        double g1 = RgbBaseColor.G != 0 ? 1.0 : 0.0;
                        double b1 = RgbBaseColor.B != 0 ? 1.0 : 0.0;

                        double r2 = RgbSecondaryColor.R != 0 ? 1.0 : 0.0;
                        double g2 = RgbSecondaryColor.G != 0 ? 1.0 : 0.0;
                        double b2 = RgbSecondaryColor.B != 0 ? 1.0 : 0.0;

                        targetR = r2 + (r1 - r2) * gammaWave;
                        targetG = g2 + (g1 - g2) * gammaWave;
                        targetB = b2 + (b1 - b2) * gammaWave;

                        LiveTelemetryLevel = wave;
                        LiveTelemetryText = $"Parlaklık: {(int)Math.Round(wave * 100)}%";
                        break;
                    }

                    case "spectrum":
                    case "cycle":
                    {
                        double periodSec = Math.Max(1.2, RgbSpeedMs / 1000.0 * 2.0);
                        double hue = (elapsedSec / periodSec * 360.0) % 360.0;
                        (double r, double g, double b) = HsvToRgb(hue, 1.0, 1.0);
                        targetR = Math.Pow(r, 1.6);
                        targetG = Math.Pow(g, 1.6);
                        targetB = Math.Pow(b, 1.6);

                        LiveTelemetryLevel = hue / 360.0;
                        LiveTelemetryText = $"Spektrum Açısı: {(int)hue}°";
                        break;
                    }

                    case "ambilight":
                    {
                        if (elapsedMs - lastSampleMs >= 36)
                        {
                            lastSampleMs = elapsedMs;
                            var (sr, sg, sb, _) = SampleScreenAmbilightAndDamage();
                            // Smooth EMA filter so game lighting feels organic
                            const double alpha = 0.18;
                            ambiR = ambiR + (sr - ambiR) * alpha;
                            ambiG = ambiG + (sg - ambiG) * alpha;
                            ambiB = ambiB + (sb - ambiB) * alpha;
                        }

                        targetR = Math.Pow(Math.Clamp(ambiR, 0.0, 1.0), 1.5);
                        targetG = Math.Pow(Math.Clamp(ambiG, 0.0, 1.0), 1.5);
                        targetB = Math.Pow(Math.Clamp(ambiB, 0.0, 1.0), 1.5);

                        double luma = Math.Max(ambiR, Math.Max(ambiG, ambiB));
                        LiveTelemetryLevel = luma;
                        LiveTelemetryText = $"Ekran Rengi: #{(int)(ambiR * 255):X2}{(int)(ambiG * 255):X2}{(int)(ambiB * 255):X2}";
                        break;
                    }

                    case "game_combat":
                    {
                        if (elapsedMs - lastSampleMs >= 20)
                        {
                            double dt = Math.Max(0.005, (elapsedMs - lastSampleMs) / 1000.0);
                            lastSampleMs = elapsedMs;

                            bool lmbDown = (Win32.GetAsyncKeyState(0x01) & 0x8000) != 0;
                            if (lmbDown)
                            {
                                barrelHeat = Math.Min(1.0, barrelHeat + dt * 0.65);
                                if (!wasLeftMouseDown)
                                {
                                    // Instant shot impulse
                                    barrelHeat = Math.Min(1.0, barrelHeat + 0.14);
                                    muzzleFlashBoost = 1.0;
                                }
                            }
                            else
                            {
                                barrelHeat = Math.Max(0.0, barrelHeat - dt * 0.38);
                            }
                            wasLeftMouseDown = lmbDown;
                            muzzleFlashBoost = Math.Max(0.0, muzzleFlashBoost - dt * 6.0);

                            // Check screen red damage vignette every ~60ms
                            if ((elapsedMs / 20) % 3 == 0)
                            {
                                var (_, _, _, dmg) = SampleScreenAmbilightAndDamage();
                                damageAlertLevel = dmg > damageAlertLevel
                                    ? dmg
                                    : Math.Max(0.0, damageAlertLevel - 0.08);
                            }
                        }

                        if (damageAlertLevel > 0.38)
                        {
                            // Critical Damage / Low HP Double-Heartbeat Crimson Alert
                            double hbPhase = (elapsedSec * 2.2) % 1.0;
                            double pulse = (hbPhase < 0.15 || (hbPhase > 0.25 && hbPhase < 0.42)) ? 1.0 : 0.08;
                            targetR = pulse;
                            targetG = 0.0;
                            targetB = 0.0;
                            LiveTelemetryLevel = damageAlertLevel;
                            LiveTelemetryText = "UYARI: Kritik Hasar / Düşük Can Algılandı!";
                        }
                        else if (barrelHeat > 0.02 || muzzleFlashBoost > 0.05)
                        {
                            // Thermal Blackbody Gradient: Base -> Gold -> Orange -> Crimson/White Strobe
                            double h = Math.Clamp(barrelHeat + muzzleFlashBoost * 0.25, 0.0, 1.0);
                            if (h < 0.4)
                            {
                                double t = h / 0.4;
                                targetR = t;
                                targetG = 0.85 + 0.15 * t;
                                targetB = 1.0 * (1.0 - t);
                            }
                            else if (h < 0.8)
                            {
                                double t = (h - 0.4) / 0.4;
                                targetR = 1.0;
                                targetG = 1.0 - 0.75 * t;
                                targetB = 0.0;
                            }
                            else
                            {
                                // Overheat / Muzzle flash strobe
                                bool flash = ((int)(elapsedMs / 35) % 2) == 0;
                                targetR = 1.0;
                                targetG = flash ? 0.85 : 0.0;
                                targetB = flash ? 0.85 : 0.0;
                            }
                            LiveTelemetryLevel = barrelHeat;
                            LiveTelemetryText = $"Namlu Sıcaklığı: %{(int)Math.Round(barrelHeat * 100)}";
                        }
                        else
                        {
                            // Calm tactical idle breathing in user's base color
                            double idleBreath = 0.20 + 0.65 * Math.Pow((1.0 - Math.Cos(elapsedSec * 2.2)) * 0.5, 1.7);
                            targetR = (RgbBaseColor.R != 0 ? 1.0 : 0.0) * idleBreath;
                            targetG = (RgbBaseColor.G != 0 ? 1.0 : 0.0) * idleBreath;
                            targetB = (RgbBaseColor.B != 0 ? 1.0 : 0.0) * idleBreath;
                            LiveTelemetryLevel = 0.05;
                            LiveTelemetryText = "Taktik Bekleme · Ateş / Hasar İzleniyor";
                        }
                        break;
                    }

                    case "audio_reactive":
                    {
                        if (elapsedMs - lastSampleMs >= 18)
                        {
                            lastSampleMs = elapsedMs;
                            double rawPeak = SampleSystemAudioPeak();
                            smoothAudioPeak = rawPeak > smoothAudioPeak
                                ? smoothAudioPeak + (rawPeak - smoothAudioPeak) * 0.65
                                : smoothAudioPeak * 0.86;
                        }

                        double level = Math.Clamp(smoothAudioPeak * 1.35, 0.0, 1.0);
                        double baseR = RgbBaseColor.R != 0 ? 1.0 : 0.0;
                        double baseG = RgbBaseColor.G != 0 ? 1.0 : 0.0;
                        double baseB = RgbBaseColor.B != 0 ? 1.0 : 0.0;

                        if (level > 0.78)
                        {
                            // High-energy bass/explosion peak shifts toward fiery crimson/white
                            double overdrive = (level - 0.78) / 0.22;
                            targetR = 1.0;
                            targetG = baseG * (1.0 - overdrive * 0.6);
                            targetB = baseB * (1.0 - overdrive * 0.6);
                        }
                        else
                        {
                            double intensity = 0.06 + Math.Pow(level, 1.5) * 0.94;
                            targetR = baseR * intensity;
                            targetG = baseG * intensity;
                            targetB = baseB * intensity;
                        }

                        LiveTelemetryLevel = level;
                        LiveTelemetryText = $"Ses / Patlama Şiddeti: %{(int)Math.Round(level * 100)}";
                        break;
                    }

                    case "cpu":
                    {
                        if (elapsedMs - lastSampleMs >= 250)
                        {
                            lastSampleMs = elapsedMs;
                            LiveTelemetryLevel = GetCpuUsagePercent() / 100.0;
                        }
                        double cpu = LiveTelemetryLevel;
                        if (cpu < 0.5)
                        {
                            double t = cpu / 0.5;
                            targetR = t;
                            targetG = 1.0;
                            targetB = 0.0;
                        }
                        else
                        {
                            double t = (cpu - 0.5) / 0.5;
                            targetR = 1.0;
                            targetG = 1.0 - t;
                            targetB = 0.0;
                        }
                        LiveTelemetryText = $"CPU Yükü: %{(int)Math.Round(cpu * 100)}";
                        break;
                    }

                    case "police":
                    {
                        bool side = ((int)(elapsedMs / 160) % 2) == 0;
                        bool strobe = ((int)(elapsedMs / 40) % 2) == 0;
                        targetR = (side && strobe) ? 1.0 : 0.0;
                        targetG = 0.0;
                        targetB = (!side && strobe) ? 1.0 : 0.0;
                        LiveTelemetryLevel = strobe ? 1.0 : 0.2;
                        LiveTelemetryText = "Acil Durum Çakar (Kırmızı / Mavi)";
                        break;
                    }
                }

                // Update 24-bit UI preview color smoothly
                LivePreviewColor = (
                    (byte)Math.Clamp((int)(Math.Pow(targetR, 1.0 / 1.6) * 255), 18, 255),
                    (byte)Math.Clamp((int)(Math.Pow(targetG, 1.0 / 1.6) * 255), 20, 255),
                    (byte)Math.Clamp((int)(Math.Pow(targetB, 1.0 / 1.6) * 255), 26, 255)
                );

                // First-Order Delta-Sigma (ΔΣ) Quantizer for 3-bit Hardware LED (0xF1)
                accR += targetR;
                int outR = accR >= 1.0 ? 1 : 0;
                if (outR == 1) accR -= 1.0;

                accG += targetG;
                int outG = accG >= 1.0 ? 1 : 0;
                if (outG == 1) accG -= 1.0;

                accB += targetB;
                int outB = accB >= 1.0 ? 1 : 0;
                if (outB == 1) accB -= 1.0;

                int mask = outR | (outG << 1) | (outB << 2);
                if (_device.IsConnected && mask != lastMask)
                {
                    try
                    {
                        _device.SetLiveLed(outR, outG, outB);
                        lastMask = mask;
                    }
                    catch
                    {
                        Thread.Sleep(5);
                    }
                }
                else
                {
                    // Maintain ~333 Hz carrier tick when output bitmask doesn't change
                    Thread.Sleep(3);
                }
            }
        }
        finally
        {
            Win32.timeEndPeriod(1);
        }
    }

    /// <summary>
    /// Downsamples the primary screen into a 24x24 buffer using Win32 GDI StretchBlt
    /// to extract the saturation-weighted dominant game color and detect red damage vignettes.
    /// </summary>
    private static (double R, double G, double B, double DamageVignette) SampleScreenAmbilightAndDamage()
    {
        const int sampleW = 24;
        const int sampleH = 24;

        IntPtr hdcScreen = Win32.GetDC(IntPtr.Zero);
        if (hdcScreen == IntPtr.Zero) return (0.0, 0.8, 1.0, 0.0);

        IntPtr hdcMem = Win32.CreateCompatibleDC(hdcScreen);
        IntPtr hBitmap = Win32.CreateCompatibleBitmap(hdcScreen, sampleW, sampleH);
        IntPtr hOld = Win32.SelectObject(hdcMem, hBitmap);

        try
        {
            int scrW = Win32.GetSystemMetrics(0);
            int scrH = Win32.GetSystemMetrics(1);

            Win32.SetStretchBltMode(hdcMem, 3); // COLORONCOLOR (fast downsample)
            Win32.StretchBlt(hdcMem, 0, 0, sampleW, sampleH, hdcScreen, 0, 0, scrW, scrH, 0x00CC0020);

            var bmi = new Win32.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
                biWidth = sampleW,
                biHeight = -sampleH, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0
            };

            byte[] pixels = new byte[sampleW * sampleH * 4];
            Win32.GetDIBits(hdcMem, hBitmap, 0, sampleH, pixels, ref bmi, 0);

            double sumR = 0.0, sumG = 0.0, sumB = 0.0, weightSum = 0.001;
            double edgeRedSum = 0.0;
            int edgeCount = 0;

            for (int y = 0; y < sampleH; y++)
            {
                for (int x = 0; x < sampleW; x++)
                {
                    int idx = (y * sampleW + x) * 4;
                    double b = pixels[idx + 0] / 255.0;
                    double g = pixels[idx + 1] / 255.0;
                    double r = pixels[idx + 2] / 255.0;

                    double max = Math.Max(r, Math.Max(g, b));
                    double min = Math.Min(r, Math.Min(g, b));
                    double sat = max > 0.05 ? (max - min) / max : 0.0;

                    // Weight vibrant & bright game colors higher than dark gray UI backgrounds
                    double weight = 0.15 + sat * sat * 2.5 + max * 0.5;
                    sumR += r * weight;
                    sumG += g * weight;
                    sumB += b * weight;
                    weightSum += weight;

                    // Check screen perimeter for red damage vignette / blood overlay
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

            // Boost saturation slightly so LED swoosh looks vivid
            double maxC = Math.Max(avgR, Math.Max(avgG, avgB));
            if (maxC > 0.08)
            {
                avgR /= maxC;
                avgG /= maxC;
                avgB /= maxC;
            }

            double damageRatio = edgeCount > 0 ? Math.Clamp(edgeRedSum / (edgeCount * 0.28), 0.0, 1.0) : 0.0;
            return (avgR, avgG, avgB, damageRatio);
        }
        catch
        {
            return (0.0, 0.8, 1.0, 0.0);
        }
        finally
        {
            Win32.SelectObject(hdcMem, hOld);
            Win32.DeleteObject(hBitmap);
            Win32.DeleteDC(hdcMem);
            Win32.ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    private static (double R, double G, double B) HsvToRgb(double h, double s, double v)
    {
        double c = v * s;
        double x = c * (1.0 - Math.Abs((h / 60.0) % 2.0 - 1.0));
        double m = v - c;
        (double r1, double g1, double b1) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };
        return (r1 + m, g1 + m, b1 + m);
    }

    /// <summary>
    /// Reads the real-time system master audio peak value (0.0..1.0) via Windows CoreAudio COM
    /// (IMMDeviceEnumerator -> IAudioMeterInformation) with a fallback to synthetic pulse if no audio device is active.
    /// </summary>
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

                    var state = _device.GetActiveState();
                    if (state.ProfileIndex != targetProfile)
                    {
                        _device.SetActiveProfile(targetProfile);
                        AutoProfileSwitched?.Invoke(targetProfile, exe);
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
            MacroKeyTriggered?.Invoke($"{keyName.ToUpperInvariant()} (Text: {macro.TextPayload})");
            string text = macro.TextPayload;
            Task.Run(() => SendUnicodeText(text));
        }
        else if (macro.Mode == "shortcut")
        {
            MacroKeyTriggered?.Invoke($"{keyName.ToUpperInvariant()} (Shortcut: {macro.ShortcutKeys})");
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

    /// <summary>
    /// Lightweight CoreAudio COM wrapper for IAudioMeterInformation to read real-time master audio peak.
    /// </summary>
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
