using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using G300sController.Models;

namespace G300sController.Services;

/// <summary>
/// Service coordinating file-based, modular profile preset templates.
/// Allows users to view, add, modify, and delete game profiles as JSON files on disk.
/// </summary>
public sealed class PresetTemplateService
{
    private static readonly Lazy<PresetTemplateService> _instance = new(() => new PresetTemplateService());
    public static PresetTemplateService Instance => _instance.Value;

    private readonly List<PresetTemplate> _presets = [];
    private readonly object _lock = new();

    public event Action? PresetsReloaded;

    public IReadOnlyList<PresetTemplate> Presets
    {
        get
        {
            lock (_lock) return _presets.ToList();
        }
    }

    public PresetTemplateService()
    {
        ReloadPresets();
    }

    public string GetPresetsDirectory()
    {
        // 1. Primary: next to executable (portable friendly)
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        string primary = Path.Combine(appDir, "Presets");

        try
        {
            if (!Directory.Exists(primary))
            {
                Directory.CreateDirectory(primary);
            }
            return primary;
        }
        catch
        {
            // Fallback to LocalAppData if appDir is write-restricted
            string localApp = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LogitechG300sStudio",
                "Presets"
            );
            if (!Directory.Exists(localApp))
            {
                Directory.CreateDirectory(localApp);
            }
            return localApp;
        }
    }

    public void ReloadPresets()
    {
        string dir = GetPresetsDirectory();
        EnsureDefaultPresetFiles(dir);

        lock (_lock)
        {
            _presets.Clear();

            try
            {
                var files = Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly);
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                };

                foreach (var file in files)
                {
                    try
                    {
                        string json = File.ReadAllText(file);
                        var preset = JsonSerializer.Deserialize<PresetTemplate>(json, options);
                        if (preset is not null && !string.IsNullOrWhiteSpace(preset.Id))
                        {
                            preset.FilePath = file;
                            _presets.Add(preset);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[PresetService] Could not parse '{file}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PresetService] Error scanning presets directory '{dir}': {ex.Message}");
            }

            // Ensure built-in fallbacks exist if directory read was somehow empty
            if (_presets.Count == 0)
            {
                _presets.AddRange(GetBuiltInFallbackPresets());
            }

            // Sort presets: Games first, then alphabetically
            _presets.Sort((a, b) =>
            {
                bool aGame = a.Category.Equals("Oyun", StringComparison.OrdinalIgnoreCase);
                bool bGame = b.Category.Equals("Oyun", StringComparison.OrdinalIgnoreCase);
                if (aGame != bGame) return aGame ? -1 : 1;
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
        }

        PresetsReloaded?.Invoke();
    }

    public PresetTemplate? GetPreset(string idOrTag)
    {
        if (string.IsNullOrWhiteSpace(idOrTag)) return null;
        string key = idOrTag.Trim().ToLowerInvariant();

        // Handle legacy alias mappings
        if (key == "fps") key = "cs2";
        if (key == "moba") key = "lol";

        lock (_lock)
        {
            return _presets.FirstOrDefault(p =>
                string.Equals(p.Id, key, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));
        }
    }

    public bool ApplyPreset(string idOrTag, G300sProfile profile, out string appliedTitle)
    {
        var preset = GetPreset(idOrTag);
        if (preset is null)
        {
            appliedTitle = idOrTag;
            return false;
        }

        appliedTitle = preset.DisplayTitle;
        profile.PollingRateHz = preset.PollingRateHz;
        profile.DefaultDpiStage = Math.Clamp(preset.DefaultDpiStage, 0, 3);

        if (preset.DpiStages is { Length: >= 4 })
        {
            for (int i = 0; i < 4; i++)
            {
                profile.SetDpiStage(i, preset.DpiStages[i]);
            }
        }

        profile.SetLed(preset.LedR, preset.LedG, preset.LedB);

        for (int i = 0; i < 9; i++)
        {
            profile.SetButtonAction(i, preset.GetButtonAction(i));
        }

        return true;
    }

    public string SaveCustomPreset(PresetTemplate preset, string? fileName = null)
    {
        string dir = GetPresetsDirectory();
        string safeName = string.IsNullOrWhiteSpace(fileName)
            ? $"{preset.Id.ToLowerInvariant().Replace(' ', '_')}.json"
            : fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? fileName : $"{fileName}.json";

        string filePath = Path.Combine(dir, safeName);
        var options = new JsonSerializerOptions { WriteIndented = true };
        string json = JsonSerializer.Serialize(preset, options);
        File.WriteAllText(filePath, json);

        ReloadPresets();
        return filePath;
    }

    public void OpenFolderInExplorer()
    {
        string dir = GetPresetsDirectory();
        if (Directory.Exists(dir))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
        }
    }

    private void EnsureDefaultPresetFiles(string dir)
    {
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var defaults = GetBuiltInFallbackPresets();
        var options = new JsonSerializerOptions { WriteIndented = true };

        foreach (var def in defaults)
        {
            string path = Path.Combine(dir, $"{def.Id}.json");
            if (!File.Exists(path))
            {
                try
                {
                    string json = JsonSerializer.Serialize(def, options);
                    File.WriteAllText(path, json);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[PresetService] Could not write default preset '{path}': {ex.Message}");
                }
            }
        }
    }

    private static List<PresetTemplate> GetBuiltInFallbackPresets()
    {
        return
        [
            new PresetTemplate
            {
                Id = "minecraft",
                Name = "Minecraft (PvP / F5 / Zoom / Sol El)",
                Category = "Oyun",
                Icon = "⛏️",
                Description = "Minecraft PvP ve Hayatta Kalma: Sol el kalkan/meşale değişimi, F5 perspektif, OptiFine zoom, envanter ve hızlı koşma tuşları.",
                ProcessNames = ["javaw", "minecraft", "bedrock"],
                PollingRateHz = 1000,
                LedR = 0, LedG = 1, LedB = 0,
                DefaultDpiStage = 1,
                DpiStages = [500, 1000, 1500, 2000],
                Buttons = new()
                {
                    ["G1"] = "mouse:1", ["G2"] = "mouse:2", ["G3"] = "mouse:3",
                    ["G4"] = "key:f", ["G5"] = "key:e", ["G6"] = "key:f5",
                    ["G7"] = "key:c", ["G8"] = "key:lctrl", ["G9"] = "key:q"
                }
            },
            new PresetTemplate
            {
                Id = "lol",
                Name = "League of Legends (LoL / MOBA)",
                Category = "Oyun",
                Icon = "⚔️",
                Description = "MOBA & LoL: Totem yerleştirme, Attack-Move kite, hızlı üsse dönüş, market ve akıllı uyarı ping çarkı.",
                ProcessNames = ["league", "lol", "riotclient"],
                PollingRateHz = 1000,
                LedR = 0, LedG = 1, LedB = 1,
                DefaultDpiStage = 1,
                DpiStages = [1000, 1500, 2000, 2500],
                Buttons = new()
                {
                    ["G1"] = "mouse:1", ["G2"] = "mouse:2", ["G3"] = "mouse:3",
                    ["G4"] = "key:4", ["G5"] = "key:a", ["G6"] = "key:b",
                    ["G7"] = "key:p", ["G8"] = "key:g", ["G9"] = "key:d"
                }
            },
            new PresetTemplate
            {
                Id = "cs2",
                Name = "Counter-Strike 2 (CS2 Rekabetçi)",
                Category = "Oyun",
                Icon = "🎯",
                Description = "CS2 & FPS: Turnuva standardı 750 DPI hassas kafa nişanı, bomba çevrimi, AWP hızlı silah geçişi, telsiz ve sniper DPI shift.",
                ProcessNames = ["cs2", "csgo"],
                PollingRateHz = 1000,
                LedR = 1, LedG = 1, LedB = 0,
                DefaultDpiStage = 1,
                DpiStages = [500, 750, 1000, 1250],
                Buttons = new()
                {
                    ["G1"] = "mouse:1", ["G2"] = "mouse:2", ["G3"] = "mouse:3",
                    ["G4"] = "key:4", ["G5"] = "key:q", ["G6"] = "key:v",
                    ["G7"] = "key:g", ["G8"] = "key:space", ["G9"] = "dpi-alternate"
                }
            },
            new PresetTemplate
            {
                Id = "valorant",
                Name = "Valorant (Taktiksel Nişancı)",
                Category = "Oyun",
                Icon = "🔴",
                Description = "Valorant: İmza ve temel ajan yetenekleri (E, C, Q), Ulti (X), takım telsizi bas-konuş ve keskin nişancı hassasiyeti.",
                ProcessNames = ["valorant"],
                PollingRateHz = 1000,
                LedR = 1, LedG = 0, LedB = 0,
                DefaultDpiStage = 1,
                DpiStages = [500, 750, 1000, 1250],
                Buttons = new()
                {
                    ["G1"] = "mouse:1", ["G2"] = "mouse:2", ["G3"] = "mouse:3",
                    ["G4"] = "key:e", ["G5"] = "key:c", ["G6"] = "key:q",
                    ["G7"] = "key:x", ["G8"] = "key:v", ["G9"] = "dpi-alternate"
                }
            },
            new PresetTemplate
            {
                Id = "apex",
                Name = "Apex Legends (Battle Royale)",
                Category = "Oyun",
                Icon = "⚡",
                Description = "Apex Legends: Taktiksel yetenek (Q), Ultimate (Z), sağlık/kalkan çarkı (4), el bombası (G), hızlı depar ve yakın dövüş.",
                ProcessNames = ["r5apex", "apex"],
                PollingRateHz = 1000,
                LedR = 1, LedG = 0, LedB = 1,
                DefaultDpiStage = 1,
                DpiStages = [750, 1000, 1250, 1750],
                Buttons = new()
                {
                    ["G1"] = "mouse:1", ["G2"] = "mouse:2", ["G3"] = "mouse:3",
                    ["G4"] = "key:q", ["G5"] = "key:z", ["G6"] = "key:4",
                    ["G7"] = "key:g", ["G8"] = "key:3", ["G9"] = "key:v"
                }
            },
            new PresetTemplate
            {
                Id = "fortnite",
                Name = "Fortnite (İnşa & Savaş)",
                Category = "Oyun",
                Icon = "🧱",
                Description = "Fortnite Hızlı İnşa: Duvar (Q), merdiven (C), zemin (F) ve düzenleme (G) kısayolları ile anlık inşa.",
                ProcessNames = ["fortnite"],
                PollingRateHz = 1000,
                LedR = 0, LedG = 1, LedB = 1,
                DefaultDpiStage = 1,
                DpiStages = [750, 1000, 1250, 1750],
                Buttons = new()
                {
                    ["G1"] = "mouse:1", ["G2"] = "mouse:2", ["G3"] = "mouse:3",
                    ["G4"] = "key:q", ["G5"] = "key:c", ["G6"] = "key:f",
                    ["G7"] = "key:g", ["G8"] = "key:e", ["G9"] = "key:1"
                }
            },
            new PresetTemplate
            {
                Id = "gta",
                Name = "GTA V / FiveM & RP",
                Category = "Oyun",
                Icon = "🚗",
                Description = "GTA V & FiveM Rol Yapma: Silah çarkı (Tab), siper alma (Q), hızlı şarjör (R), akıllı telefon (Yukarı Ok) ve araç etkileşim tuşları.",
                ProcessNames = ["gta", "fivem", "gta5"],
                PollingRateHz = 1000,
                LedR = 0, LedG = 1, LedB = 0,
                DefaultDpiStage = 1,
                DpiStages = [750, 1250, 1750, 2250],
                Buttons = new()
                {
                    ["G1"] = "mouse:1", ["G2"] = "mouse:2", ["G3"] = "mouse:3",
                    ["G4"] = "key:tab", ["G5"] = "key:q", ["G6"] = "key:r",
                    ["G7"] = "key:up", ["G8"] = "key:e", ["G9"] = "dpi-cycle"
                }
            },
            new PresetTemplate
            {
                Id = "mmo",
                Name = "MMORPG / World of Warcraft",
                Category = "Oyun",
                Icon = "🐉",
                Description = "MMORPG & WoW: Aksiyon çubuğu 1-4 temel yetenekleri, en yakın düşmanı otomatik hedefleme (Tab) ve otomatik koşma (NumLock).",
                ProcessNames = ["wow", "worldofwarcraft", "eso", "ffxiv"],
                PollingRateHz = 1000,
                LedR = 1, LedG = 0, LedB = 1,
                DefaultDpiStage = 1,
                DpiStages = [1000, 1500, 2000, 2500],
                Buttons = new()
                {
                    ["G1"] = "mouse:1", ["G2"] = "mouse:2", ["G3"] = "mouse:3",
                    ["G4"] = "key:1", ["G5"] = "key:2", ["G6"] = "key:3",
                    ["G7"] = "key:4", ["G8"] = "key:tab", ["G9"] = "key:numlock"
                }
            },
            new PresetTemplate
            {
                Id = "web",
                Name = "Web & Tarayıcı Gezinme",
                Category = "Genel",
                Icon = "🌐",
                Description = "Web tarayıcılarında ultra hızlı gezinme: Geri, İleri, F5 Sayfa Yenileme, Yeni Sekme (Ctrl+T), Sekme Kapatma (Ctrl+W) ve Geri Açma (Ctrl+Shift+T).",
                ProcessNames = ["chrome", "msedge", "firefox", "brave", "opera"],
                PollingRateHz = 1000,
                LedR = 0, LedG = 1, LedB = 1,
                DefaultDpiStage = 1,
                DpiStages = [750, 1250, 1750, 2250],
                Buttons = new()
                {
                    ["G1"] = "mouse:1", ["G2"] = "mouse:2", ["G3"] = "mouse:3",
                    ["G4"] = "mouse:5", ["G5"] = "mouse:4", ["G6"] = "key:f5",
                    ["G7"] = "key:lctrl+t", ["G8"] = "key:lctrl+w", ["G9"] = "key:lctrl+lshift+t"
                }
            },
            new PresetTemplate
            {
                Id = "dev",
                Name = "Yazılım Geliştirme (VS Code / Git)",
                Category = "Üretkenlik",
                Icon = "💻",
                Description = "IDE ve Kodlama: Kopyala (Ctrl+C), Yapıştır (Ctrl+V), Geri Al (Ctrl+Z), İleri Al (Ctrl+Y), Hızlı Kaydet (Ctrl+S) ve Komut Paleti (Ctrl+P).",
                ProcessNames = ["code", "devenv", "idea", "pycharm", "rider"],
                PollingRateHz = 1000,
                LedR = 0, LedG = 0, LedB = 1,
                DefaultDpiStage = 1,
                DpiStages = [800, 1250, 1750, 2500],
                Buttons = new()
                {
                    ["G1"] = "mouse:1", ["G2"] = "mouse:2", ["G3"] = "mouse:3",
                    ["G4"] = "key:lctrl+c", ["G5"] = "key:lctrl+v", ["G6"] = "key:lctrl+z",
                    ["G7"] = "key:lctrl+y", ["G8"] = "key:lctrl+s", ["G9"] = "key:lctrl+p"
                }
            },
            new PresetTemplate
            {
                Id = "office",
                Name = "Ofis & Standart Masaüstü",
                Category = "Genel",
                Icon = "📄",
                Description = "Günlük ofis ve masaüstü kullanımı: Geri, İleri, Kopyala, Yapıştır, Geri Al ve DPI döngüsü.",
                ProcessNames = ["excel", "winword", "powerpnt"],
                PollingRateHz = 500,
                LedR = 0, LedG = 1, LedB = 0,
                DefaultDpiStage = 1,
                DpiStages = [750, 1250, 1750, 2250],
                Buttons = new()
                {
                    ["G1"] = "mouse:1", ["G2"] = "mouse:2", ["G3"] = "mouse:3",
                    ["G4"] = "mouse:4", ["G5"] = "mouse:5", ["G6"] = "key:lctrl+c",
                    ["G7"] = "key:lctrl+v", ["G8"] = "key:lctrl+z", ["G9"] = "dpi-cycle"
                }
            }
        ];
    }
}
