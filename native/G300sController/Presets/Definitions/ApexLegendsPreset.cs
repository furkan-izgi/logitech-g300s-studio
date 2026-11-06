using System.Collections.Generic;

namespace G300sController.Presets.Definitions;

public sealed class ApexLegendsPreset : BaseGamePreset
{
    public override string Id => "apex";
    public override string Name => "Apex Legends (Battle Royale)";
    public override string Icon => "⚡";
    public override string Description => "Apex Legends: Taktiksel yetenek (Q), Ultimate (Z), sağlık/kalkan çarkı (4), el bombası (G), hızlı depar ve yakın dövüş.";
    public override IReadOnlyList<string> ProcessNames => ["r5apex", "apex"];

    public override (int R, int G, int B) LedColor => (1, 0, 1); // Apex Moru
    public override IReadOnlyList<int> DpiStages => [750, 1000, 1250, 1750];

    public override IReadOnlyDictionary<int, string> Buttons => new Dictionary<int, string>
    {
        [0] = "mouse:1", // G1: Ateş Et
        [1] = "mouse:2", // G2: Nişan Al
        [2] = "mouse:3", // G3: Düşman İşaretle (Ping)
        [3] = "key:q",   // G4: Taktiksel Yetenek (Q)
        [4] = "key:z",   // G5: Ultimate Yetenek (Z)
        [5] = "key:4",   // G6: Sağlık & Kalkan Çarkı (4)
        [6] = "key:g",   // G7: El Bombası Seç (G)
        [7] = "key:3",   // G8: Silahı Kına Sok / Hızlı Depar (3)
        [8] = "key:v"    // G9: Yakın Dövüş Tekmesi (V)
    };
}
