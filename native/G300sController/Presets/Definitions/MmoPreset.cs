using System.Collections.Generic;

namespace G300sController.Presets.Definitions;

public sealed class MmoPreset : BaseGamePreset
{
    public override string Id => "mmo";
    public override string Name => "MMORPG / World of Warcraft";
    public override string Icon => "🐉";
    public override string Description => "MMORPG & WoW: Aksiyon çubuğu 1-4 temel yetenekleri, en yakın düşmanı otomatik hedefleme (Tab) ve otomatik koşma (NumLock).";
    public override IReadOnlyList<string> ProcessNames => ["wow", "worldofwarcraft", "eso", "ffxiv"];

    public override (int R, int G, int B) LedColor => (1, 0, 1); // Destansı Mor
    public override IReadOnlyList<int> DpiStages => [1000, 1500, 2000, 2500];

    public override IReadOnlyDictionary<int, string> Buttons => new Dictionary<int, string>
    {
        [0] = "mouse:1",     // G1: Sol Tık (Seçim / Hedefle)
        [1] = "mouse:2",     // G2: Sağ Tık (Kamera / Etkileşim)
        [2] = "mouse:3",     // G3: Orta Tık (Yetenek 5)
        [3] = "key:1",       // G4: Yetenek 1
        [4] = "key:2",       // G5: Yetenek 2
        [5] = "key:3",       // G6: Yetenek 3
        [6] = "key:4",       // G7: Yetenek 4
        [7] = "key:tab",     // G8: En Yakın Düşmanı Hedefle (Tab)
        [8] = "key:numlock"  // G9: Otomatik Koşma (Auto-Run)
    };
}
