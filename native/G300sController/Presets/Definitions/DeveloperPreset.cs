using System.Collections.Generic;

namespace G300sController.Presets.Definitions;

public sealed class DeveloperPreset : BaseGamePreset
{
    public override string Id => "dev";
    public override string Name => "Yazılım Geliştirme (VS Code / Git)";
    public override string Category => "Üretkenlik";
    public override string Icon => "💻";
    public override string Description => "IDE ve Kodlama: Kopyala (Ctrl+C), Yapıştır (Ctrl+V), Geri Al (Ctrl+Z), İleri Al (Ctrl+Y), Hızlı Kaydet (Ctrl+S) ve Komut Paleti (Ctrl+P).";
    public override IReadOnlyList<string> ProcessNames => ["code", "devenv", "idea", "pycharm", "rider"];

    public override (int R, int G, int B) LedColor => (0, 0, 1); // Saf Mavi
    public override IReadOnlyList<int> DpiStages => [800, 1250, 1750, 2500];

    public override IReadOnlyDictionary<int, string> Buttons => new Dictionary<int, string>
    {
        [0] = "mouse:1",     // G1: Sol Tık
        [1] = "mouse:2",     // G2: Sağ Tık
        [2] = "mouse:3",     // G3: Orta Tık
        [3] = "key:lctrl+c", // G4: Kopyala
        [4] = "key:lctrl+v", // G5: Yapıştır
        [5] = "key:lctrl+z", // G6: Geri Al
        [6] = "key:lctrl+y", // G7: İleri Al
        [7] = "key:lctrl+s", // G8: Hızlı Kaydet
        [8] = "key:lctrl+p"  // G9: Dosya / Komut Gezgini
    };
}
