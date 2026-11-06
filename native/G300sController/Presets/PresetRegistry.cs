using System;
using System.Collections.Generic;
using System.Linq;
using G300sController.Presets.Definitions;

namespace G300sController.Presets;

/// <summary>
/// Registry coordinating strongly-typed code presets.
/// </summary>
public static class PresetRegistry
{
    private static readonly IReadOnlyList<IPreset> _builtInPresets =
    [
        new MinecraftPreset(),
        new LeagueOfLegendsPreset(),
        new CounterStrike2Preset(),
        new ValorantPreset(),
        new ApexLegendsPreset(),
        new FortnitePreset(),
        new Gta5Preset(),
        new MmoPreset(),
        new WebBrowsingPreset(),
        new DeveloperPreset(),
        new OfficePreset()
    ];

    public static IReadOnlyList<IPreset> BuiltInPresets => _builtInPresets;

    public static IPreset? FindById(string idOrAlias)
    {
        if (string.IsNullOrWhiteSpace(idOrAlias)) return null;

        string key = idOrAlias.Trim().ToLowerInvariant();
        if (key == "fps") key = "cs2";
        if (key == "moba") key = "lol";

        return _builtInPresets.FirstOrDefault(p =>
            string.Equals(p.Id, key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));
    }
}
