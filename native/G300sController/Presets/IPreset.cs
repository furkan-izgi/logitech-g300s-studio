using System.Collections.Generic;
using G300sController.Models;

namespace G300sController.Presets;

/// <summary>
/// Defines the modular contract for a strongly-typed game or application profile preset.
/// </summary>
public interface IPreset
{
    string Id { get; }
    string Name { get; }
    string Category { get; }
    string Icon { get; }
    string Description { get; }
    IReadOnlyList<string> ProcessNames { get; }

    int PollingRateHz { get; }
    (int R, int G, int B) LedColor { get; }
    int DefaultDpiStage { get; }
    IReadOnlyList<int> DpiStages { get; }
    IReadOnlyDictionary<int, string> Buttons { get; }

    void ApplyTo(G300sProfile profile);
    PresetTemplate ToTemplate();
}
