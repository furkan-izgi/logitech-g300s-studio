using System.Collections.Generic;

namespace G300sController.Presets.Definitions;

public sealed class LeagueOfLegendsPreset : BaseGamePreset
{
    public override string Id => "lol";
    public override string Name => "League of Legends (LoL / MOBA)";
    public override string Icon => "⚔️";
    public override string Description => "MOBA & LoL: Totem yerleştirme, Attack-Move kite, hızlı üsse dönüş, market ve akıllı uyarı ping çarkı.";
    public override IReadOnlyList<string> ProcessNames => ["league", "lol", "riotclient"];

    public override (int R, int G, int B) LedColor => (0, 1, 1); // Hextech Camgöbeği
    public override IReadOnlyList<int> DpiStages => [1000, 1500, 2000, 2500];

    public override IReadOnlyDictionary<int, string> Buttons => new Dictionary<int, string>
    {
        [0] = "mouse:1", // G1: Sol Tık (Seçim & UI)
        [1] = "mouse:2", // G2: Sağ Tık (Hareket & Normal Saldırı)
        [2] = "mouse:3", // G3: Kamera Odakla (Şampiyona Dön)
        [3] = "key:4",   // G4: Totem / Arayıcı Mercek (Ward 4)
        [4] = "key:a",   // G5: Saldırı Hareketi Tıklaması (Attack Move Kite)
        [5] = "key:b",   // G6: Üsse Dönüş (Recall B)
        [6] = "key:p",   // G7: Dükkan / Market Aç (Shop P)
        [7] = "key:g",   // G8: Akıllı İkaz / Hızlı Ping Çarkı
        [8] = "key:d"    // G9: Sihirdar Büyüsü (Sıçra / Flash D)
    };
}
