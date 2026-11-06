using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using G300sController.Models;
using G300sController.Presets;

namespace G300sController.Services;

/// <summary>
/// Service coordinating modular profile presets across code-defined registries and disk JSON files.
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

        if (key == "fps") key = "cs2";
        if (key == "moba") key = "lol";

        lock (_lock)
        {
            var match = _presets.FirstOrDefault(p =>
                string.Equals(p.Id, key, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));

            if (match is not null) return match;
        }

        // Check code registry directly as secondary lookup
        var codePreset = PresetRegistry.FindById(key);
        return codePreset?.ToTemplate();
    }

    public bool ApplyPreset(string idOrTag, G300sProfile profile, out string appliedTitle)
    {
        // First check strongly-typed code registry
        var codePreset = PresetRegistry.FindById(idOrTag);
        if (codePreset is not null)
        {
            appliedTitle = $"{codePreset.Icon} {codePreset.Name}";
            codePreset.ApplyTo(profile);
            return true;
        }

        // Secondary: check loaded templates from disk
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
        return PresetRegistry.BuiltInPresets.Select(p => p.ToTemplate()).ToList();
    }
}
