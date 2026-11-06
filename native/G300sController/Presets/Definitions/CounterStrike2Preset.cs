using System.Collections.Generic;

namespace G300sController.Presets.Definitions;

public sealed class CounterStrike2Preset : BaseGamePreset
{
    public override string Id => "cs2";
    public override string Name => "Counter-Strike 2 (CS2 Rekabetçi)";
    public override string Icon => "🎯";
    public override string Description => "CS2 & FPS: Turnuva standardı 750 DPI hassas kafa nişanı, bomba çevrimi, AWP hızlı silah geçişi, telsiz ve sniper DPI shift.";
    public override IReadOnlyList<string> ProcessNames => ["cs2", "csgo"];

    public override (int R, int G, int B) LedColor => (1, 1, 0); // Sıcak Amber
    public override IReadOnlyList<int> DpiStages => [500, 750, 1000, 1250];

    public override IReadOnlyDictionary<int, string> Buttons => new Dictionary<int, string>
    {
        [0] = "mouse:1",       // G1: Sol Tık (Ateş Et)
        [1] = "mouse:2",       // G2: Sağ Tık (Dürbün Aç / İkincil)
        [2] = "mouse:3",       // G3: Tehlike / Düşman İşaretle (Ping)
        [3] = "key:4",         // G4: Bomba Seçimi / Flaş Çevir
        [4] = "key:q",         // G5: Hızlı Silah Değiştir (AWP Quick Switch)
        [5] = "key:v",         // G6: Telsiz Bas-Konuş (Push-to-Talk)
        [6] = "key:g",         // G7: Silahı Yere At (Drop Weapon)
        [7] = "key:space",     // G8: Zıplama / Jump-throw Yardımcısı
        [8] = "dpi-alternate" // G9: DPI Shift (Keskin Nişancı 500 DPI)
    };
}
