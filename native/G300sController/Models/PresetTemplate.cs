using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace G300sController.Models;

/// <summary>
/// Model representing a modular, file-based profile preset template.
/// Stored as JSON files in the Presets/ directory.
/// </summary>
public sealed class PresetTemplate
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = "Oyun";

    [JsonPropertyName("icon")]
    public string Icon { get; set; } = "🎮";

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("processNames")]
    public List<string> ProcessNames { get; set; } = [];

    [JsonPropertyName("pollingRateHz")]
    public int PollingRateHz { get; set; } = 1000;

    [JsonPropertyName("ledR")]
    public int LedR { get; set; } = 0;

    [JsonPropertyName("ledG")]
    public int LedG { get; set; } = 1;

    [JsonPropertyName("ledB")]
    public int LedB { get; set; } = 1;

    [JsonPropertyName("defaultDpiStage")]
    public int DefaultDpiStage { get; set; } = 1;

    [JsonPropertyName("dpiStages")]
    public int[] DpiStages { get; set; } = [500, 1000, 1500, 2000];

    [JsonPropertyName("buttons")]
    public Dictionary<string, string> Buttons { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public string FilePath { get; set; } = string.Empty;

    [JsonIgnore]
    public string DisplayTitle => string.IsNullOrWhiteSpace(Icon) ? Name : $"{Icon} {Name}";

    public string GetButtonAction(int index)
    {
        string gKey = $"G{index + 1}";
        if (Buttons.TryGetValue(gKey, out var act) && !string.IsNullOrWhiteSpace(act))
            return act;

        if (Buttons.TryGetValue(index.ToString(), out act) && !string.IsNullOrWhiteSpace(act))
            return act;

        return index switch
        {
            0 => "mouse:1",
            1 => "mouse:2",
            2 => "mouse:3",
            3 => "mouse:5",
            4 => "mouse:4",
            5 => "dpi-down",
            6 => "dpi-up",
            7 => "profile-cycle",
            8 => "dpi-cycle",
            _ => "none"
        };
    }

    public static PresetTemplate FromProfile(G300sProfile profile, string id, string name, string category, string icon, string description)
    {
        var preset = new PresetTemplate
        {
            Id = id,
            Name = name,
            Category = category,
            Icon = icon,
            Description = description,
            PollingRateHz = profile.PollingRateHz,
            LedR = profile.LedR,
            LedG = profile.LedG,
            LedB = profile.LedB,
            DefaultDpiStage = profile.DefaultDpiStage,
            DpiStages = (int[])profile.DpiStages.Clone(),
            Buttons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };

        for (int i = 0; i < 9; i++)
        {
            preset.Buttons[$"G{i + 1}"] = profile.Buttons[i];
        }

        return preset;
    }
}
