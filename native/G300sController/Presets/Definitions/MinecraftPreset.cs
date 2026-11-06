using System.Collections.Generic;

namespace G300sController.Presets.Definitions;

public sealed class MinecraftPreset : BaseGamePreset
{
    public override string Id => "minecraft";
    public override string Name => "Minecraft (PvP / F5 / Zoom / Sol El)";
    public override string Icon => "⛏️";
    public override string Description => "Minecraft PvP ve Hayatta Kalma: Sol el kalkan/meşale değişimi, F5 perspektif, OptiFine zoom, envanter ve hızlı koşma tuşları.";
    public override IReadOnlyList<string> ProcessNames => ["javaw", "minecraft", "bedrock"];

    public override (int R, int G, int B) LedColor => (0, 1, 0); // Creeper Yeşili
    public override IReadOnlyList<int> DpiStages => [500, 1000, 1500, 2000];

    public override IReadOnlyDictionary<int, string> Buttons => new Dictionary<int, string>
    {
        [0] = "mouse:1",  // G1: Sol Tık (Saldırı / Blok Kırma)
        [1] = "mouse:2",  // G2: Sağ Tık (Blok Koyma / Kalkan)
        [2] = "mouse:3",  // G3: Orta Tık (Blok Seç - Pick Block)
        [3] = "key:f",    // G4: Sol El / İkinci Ele Geçir (Kalkan/Meşale)
        [4] = "key:e",    // G5: Envanter Aç / Kapat
        [5] = "key:f5",   // G6: Kamera Görünümü (Perspektif F5)
        [6] = "key:c",    // G7: OptiFine / Zoom Yakınlaştırma
        [7] = "key:lctrl",// G8: Depar / Koşma (Sprint)
        [8] = "key:q"     // G9: Eşya At / Bırak (Drop Q)
    };
}
