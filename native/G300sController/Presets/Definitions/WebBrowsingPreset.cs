using System.Collections.Generic;

namespace G300sController.Presets.Definitions;

public sealed class WebBrowsingPreset : BaseGamePreset
{
    public override string Id => "web";
    public override string Name => "Web & Tarayıcı Gezinme";
    public override string Category => "Genel";
    public override string Icon => "🌐";
    public override string Description => "Web tarayıcılarında ultra hızlı gezinme: Geri, İleri, F5 Sayfa Yenileme, Yeni Sekme (Ctrl+T), Sekme Kapatma (Ctrl+W) ve Geri Açma (Ctrl+Shift+T).";
    public override IReadOnlyList<string> ProcessNames => ["chrome", "msedge", "firefox", "brave", "opera"];

    public override (int R, int G, int B) LedColor => (0, 1, 1); // Turkuaz Camgöbeği
    public override IReadOnlyList<int> DpiStages => [750, 1250, 1750, 2250];

    public override IReadOnlyDictionary<int, string> Buttons => new Dictionary<int, string>
    {
        [0] = "mouse:1",              // G1: Sol Tık
        [1] = "mouse:2",              // G2: Sağ Tık
        [2] = "mouse:3",              // G3: Orta Tık (Yeni sekmede aç)
        [3] = "mouse:5",              // G4: İleri (Forward)
        [4] = "mouse:4",              // G5: Geri (Back)
        [5] = "key:f5",               // G6: Sayfayı Yenile (Refresh F5)
        [6] = "key:lctrl+t",          // G7: Yeni Sekme Aç (Ctrl+T)
        [7] = "key:lctrl+w",          // G8: Sekmeyi Kapat (Ctrl+W)
        [8] = "key:lctrl+lshift+t"    // G9: Kapatılan Sekmeyi Aç (Ctrl+Shift+T)
    };
}
