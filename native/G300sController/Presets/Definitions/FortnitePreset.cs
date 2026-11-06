using System.Collections.Generic;

namespace G300sController.Presets.Definitions;

public sealed class FortnitePreset : BaseGamePreset
{
    public override string Id => "fortnite";
    public override string Name => "Fortnite (İnşa & Savaş)";
    public override string Icon => "🧱";
    public override string Description => "Fortnite Hızlı İnşa: Duvar (Q), merdiven (C), zemin (F) ve düzenleme (G) kısayolları ile anlık inşa.";
    public override IReadOnlyList<string> ProcessNames => ["fortnite"];

    public override (int R, int G, int B) LedColor => (0, 1, 1); // Gökyüzü Camgöbeği
    public override IReadOnlyList<int> DpiStages => [750, 1000, 1250, 1750];

    public override IReadOnlyDictionary<int, string> Buttons => new Dictionary<int, string>
    {
        [0] = "mouse:1", // G1: Ateş / Yapı Koy
        [1] = "mouse:2", // G2: Nişan Al
        [2] = "mouse:3", // G3: Konum İşaretle
        [3] = "key:q",   // G4: Duvar İnşaatı (Wall Q)
        [4] = "key:c",   // G5: Merdiven İnşaatı (Ramp C)
        [5] = "key:f",   // G6: Zemin İnşaatı (Floor F)
        [6] = "key:g",   // G7: Yapı Düzenleme (Edit G)
        [7] = "key:e",   // G8: Kullan / Etkileşim (E)
        [8] = "key:1"    // G9: Kazma Seç (Pickaxe 1)
    };
}
