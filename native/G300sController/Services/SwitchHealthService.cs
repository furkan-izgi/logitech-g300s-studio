using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace G300sController.Services;

public sealed class SwitchStats
{
    public string Name { get; set; } = "";
    public long TotalClicks { get; set; }
    public long RatedLifespanClicks { get; set; } = 10_000_000;
    public double WearPercentage => Math.Min(100.0, Math.Round(((double)TotalClicks / RatedLifespanClicks) * 100.0, 2));
    public double HealthPercentage => Math.Max(0.0, Math.Round(100.0 - WearPercentage, 2));
    public long RemainingClicks => Math.Max(0, RatedLifespanClicks - TotalClicks);

    public string HealthStatus => WearPercentage switch
    {
        < 15.0 => "Mükemmel",
        < 40.0 => "Optimal",
        < 70.0 => "İyi (Hafif Aşınma)",
        < 90.0 => "Yüksek Aşınma",
        _ => "Kritik Değişim Önerilir"
    };

    public string HealthColorHex => WearPercentage switch
    {
        < 40.0 => "#22C55E", // Green
        < 75.0 => "#00D4FF", // Cyan
        < 90.0 => "#F59E0B", // Amber Warning
        _ => "#EF4444"       // Red Critical
    };
}

public sealed class SwitchHealthService
{
    private static readonly string StoragePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "G300sStudio",
        "switch_health.json");

    public Dictionary<int, SwitchStats> Switches { get; } = new();

    public event Action? HealthUpdated;

    public SwitchHealthService()
    {
        InitializeDefaultSwitches();
        LoadStats();
    }

    private void InitializeDefaultSwitches()
    {
        Switches[1] = new SwitchStats { Name = "Sol Tık (G1)", RatedLifespanClicks = 10_000_000 };
        Switches[2] = new SwitchStats { Name = "Sağ Tık (G2)", RatedLifespanClicks = 10_000_000 };
        Switches[3] = new SwitchStats { Name = "Orta Tık (G3)", RatedLifespanClicks = 5_000_000 };
        Switches[4] = new SwitchStats { Name = "G4 (Sol Üst)", RatedLifespanClicks = 3_000_000 };
        Switches[5] = new SwitchStats { Name = "G5 (Sol Alt)", RatedLifespanClicks = 3_000_000 };
        Switches[6] = new SwitchStats { Name = "G6 (Sağ Üst)", RatedLifespanClicks = 3_000_000 };
        Switches[7] = new SwitchStats { Name = "G7 (Sağ Alt)", RatedLifespanClicks = 3_000_000 };
        Switches[8] = new SwitchStats { Name = "G8 (Profil)", RatedLifespanClicks = 3_000_000 };
        Switches[9] = new SwitchStats { Name = "G9 (Mod)", RatedLifespanClicks = 3_000_000 };
    }

    public void RecordClick(int buttonIndex)
    {
        if (buttonIndex < 1 || buttonIndex > 9) return;

        if (Switches.TryGetValue(buttonIndex, out var stats))
        {
            stats.TotalClicks++;
            HealthUpdated?.Invoke();

            // Auto-persist every 25 clicks to minimize disk I/O
            if (stats.TotalClicks % 25 == 0)
            {
                SaveStats();
            }
        }
    }

    public void LoadStats()
    {
        try
        {
            if (!File.Exists(StoragePath)) return;

            string json = File.ReadAllText(StoragePath);
            var loaded = JsonSerializer.Deserialize<Dictionary<int, SwitchStats>>(json);
            if (loaded != null)
            {
                foreach (var (k, v) in loaded)
                {
                    if (Switches.ContainsKey(k))
                    {
                        Switches[k].TotalClicks = v.TotalClicks;
                        if (v.RatedLifespanClicks > 0)
                            Switches[k].RatedLifespanClicks = v.RatedLifespanClicks;
                    }
                }
            }
        }
        catch
        {
            // Ignore load failure
        }
    }

    public void SaveStats()
    {
        try
        {
            var dir = Path.GetDirectoryName(StoragePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(Switches, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(StoragePath, json);
        }
        catch
        {
            // Ignore save failure
        }
    }

    public void ResetAll()
    {
        foreach (var s in Switches.Values)
        {
            s.TotalClicks = 0;
        }
        SaveStats();
        HealthUpdated?.Invoke();
    }
}
