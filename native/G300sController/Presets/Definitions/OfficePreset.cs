using System.Collections.Generic;

namespace G300sController.Presets.Definitions;

public sealed class OfficePreset : BaseGamePreset
{
    public override string Id => "office";
    public override string Name => "Ofis & Standart Masaüstü";
    public override string Category => "Genel";
    public override string Icon => "📄";
    public override string Description => "Günlük ofis ve masaüstü kullanımı: Geri, İleri, Kopyala, Yapıştır, Geri Al ve DPI döngüsü.";
    public override IReadOnlyList<string> ProcessNames => ["excel", "winword", "powerpnt"];

    public override (int R, int G, int B) LedColor => (0, 1, 0); // Zümrüt Yeşili
    public override IReadOnlyList<int> DpiStages => [750, 1250, 1750, 2250];

    public override IReadOnlyDictionary<int, string> Buttons => new Dictionary<int, string>
    {
        [0] = "mouse:1",     // G1: Sol Tık
        [1] = "mouse:2",     // G2: Sağ Tık
        [2] = "mouse:3",     // G3: Orta Tık
        [3] = "mouse:4",     // G4: Geri
        [4] = "mouse:5",     // G5: İleri
        [5] = "key:lctrl+c", // G6: Kopyala
        [6] = "key:lctrl+v", // G7: Yapıştır
        [7] = "key:lctrl+z", // G8: Geri Al
        [8] = "dpi-cycle"   // G9: DPI Döngüsü
    };
}
