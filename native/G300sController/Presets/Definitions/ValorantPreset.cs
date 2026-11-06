using System.Collections.Generic;

namespace G300sController.Presets.Definitions;

public sealed class ValorantPreset : BaseGamePreset
{
    public override string Id => "valorant";
    public override string Name => "Valorant (Taktiksel Nişancı)";
    public override string Icon => "🔴";
    public override string Description => "Valorant: İmza ve temel ajan yetenekleri (E, C, Q), Ulti (X), takım telsizi bas-konuş ve keskin nişancı hassasiyeti.";
    public override IReadOnlyList<string> ProcessNames => ["valorant"];

    public override (int R, int G, int B) LedColor => (1, 0, 0); // Radyanit Kırmızı
    public override IReadOnlyList<int> DpiStages => [500, 750, 1000, 1250];

    public override IReadOnlyDictionary<int, string> Buttons => new Dictionary<int, string>
    {
        [0] = "mouse:1",       // G1: Ateş Et
        [1] = "mouse:2",       // G2: Nişan Al (ADS)
        [2] = "mouse:3",       // G3: İşaretçi (Ping)
        [3] = "key:e",         // G4: İmza Yetenek (E)
        [4] = "key:c",         // G5: Temel Yetenek (C)
        [5] = "key:q",         // G6: Temel Yetenek (Q)
        [6] = "key:x",         // G7: Ultimate Yeteneği (X)
        [7] = "key:v",         // G8: Takım Telsizi Bas-Konuş (V)
        [8] = "dpi-alternate" // G9: DPI Shift (Keskin Nişancı 500 DPI)
    };
}
