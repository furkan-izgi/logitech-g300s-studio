using System.Collections.Generic;

namespace G300sController.Presets.Definitions;

public sealed class Gta5Preset : BaseGamePreset
{
    public override string Id => "gta";
    public override string Name => "GTA V / FiveM & RP";
    public override string Icon => "🚗";
    public override string Description => "GTA V & FiveM Rol Yapma: Silah çarkı (Tab), siper alma (Q), hızlı şarjör (R), akıllı telefon (Yukarı Ok) ve araç etkileşim tuşları.";
    public override IReadOnlyList<string> ProcessNames => ["gta", "fivem", "gta5"];

    public override (int R, int G, int B) LedColor => (0, 1, 0); // Zümrüt Yeşili
    public override IReadOnlyList<int> DpiStages => [750, 1250, 1750, 2250];

    public override IReadOnlyDictionary<int, string> Buttons => new Dictionary<int, string>
    {
        [0] = "mouse:1",   // G1: Ateş / Vur
        [1] = "mouse:2",   // G2: Nişan Al
        [2] = "mouse:3",   // G3: Yakın Dövüş / El Freni
        [3] = "key:tab",   // G4: Silah Çarkı (Tab)
        [4] = "key:q",     // G5: Siper Al (Take Cover Q)
        [5] = "key:r",     // G6: Şarjör Değiştir (Reload R)
        [6] = "key:up",    // G7: Akıllı Telefonu Aç (Yukarı Ok)
        [7] = "key:e",     // G8: Korna / Far / Etkileşim (E)
        [8] = "dpi-cycle" // G9: Hassasiyet Kademesi Değiştir
    };
}
