using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using G300sController.Models;

namespace G300sController.Services;

public enum RecommendationCategory
{
    HardwareHealth,
    AppAutomation,
    UnusedKey,
    ErgonomicsDpi
}

public sealed class SmartRecommendation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public RecommendationCategory Category { get; set; }
    public string CategoryTitle => Category switch
    {
        RecommendationCategory.HardwareHealth => "Donanım Sağlığı",
        RecommendationCategory.AppAutomation => "Otomatik Profil",
        RecommendationCategory.UnusedKey => "Atıl Tuş Keşfi",
        RecommendationCategory.ErgonomicsDpi => "DPI & Ergonomi",
        _ => "Tavsiye"
    };

    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string ActionButtonText { get; set; } = "Uygula";
    public string ActionKey { get; set; } = "";
    public Dictionary<string, string> ActionParams { get; set; } = [];
    public bool IsApplied { get; set; }
    public bool IsDismissed { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

public sealed class SmartAdvisorService
{
    private readonly SwitchHealthService _switchHealth;
    private readonly DebounceFilterService _debounce;
    private readonly ClickHeatmapService _heatmap;
    private readonly HostAutomationService _automation;
    private readonly List<SmartRecommendation> _activeRecommendations = [];
    private readonly HashSet<string> _dismissedIds = new(StringComparer.OrdinalIgnoreCase);

    public event Action? RecommendationsUpdated;

    public IReadOnlyList<SmartRecommendation> Recommendations
    {
        get
        {
            lock (_activeRecommendations)
            {
                return _activeRecommendations.Where(r => !r.IsDismissed && !r.IsApplied).ToList();
            }
        }
    }

    public SmartAdvisorService(
        SwitchHealthService switchHealth,
        DebounceFilterService debounce,
        ClickHeatmapService heatmap,
        HostAutomationService automation)
    {
        _switchHealth = switchHealth;
        _debounce = debounce;
        _heatmap = heatmap;
        _automation = automation;
    }

    /// <summary>
    /// Evaluates all live telemetries and generates actionable recommendations.
    /// Runs lightweight heuristics without impacting system performance.
    /// </summary>
    public void EvaluateTelemetry(G300sProfile[] profiles, int activeProfileIndex)
    {
        var discovered = new List<SmartRecommendation>();

        // 1. Hardware Health & Mechanical Chatter Analysis
        EvaluateHardwareHealth(discovered);

        // 2. Active Application & Auto-Switch Rule Suggestions
        EvaluateActiveProcess(discovered, profiles);

        // 3. Unused Buttons / Productivity Key Discovery
        EvaluateButtonUsage(discovered, profiles, activeProfileIndex);

        // 4. Cursor Dwell & DPI Ergonomics Suggestion
        EvaluateDpiErgonomics(discovered, profiles, activeProfileIndex);

        // 5. Proactive Productivity & Web Browsing Tips
        EvaluateGeneralProductivity(discovered, profiles, activeProfileIndex);

        // Merge discovered recommendations
        lock (_activeRecommendations)
        {
            foreach (var rec in discovered)
            {
                if (_dismissedIds.Contains(rec.Id)) continue;
                if (!_activeRecommendations.Any(r => r.Id == rec.Id))
                {
                    _activeRecommendations.Add(rec);
                }
            }
        }

        RecommendationsUpdated?.Invoke();
    }

    private void EvaluateHardwareHealth(List<SmartRecommendation> list)
    {
        if (_debounce.ChatterClicks > 0 || _debounce.ChatterRiskPercentage >= 1.0)
        {
            string id = "rec_chatter_protection";
            list.Add(new SmartRecommendation
            {
                Id = id,
                Category = RecommendationCategory.HardwareHealth,
                Title = "Akıllı Switch Koruma & Debounce Ayarı",
                Description = $"Switch anahtarında {_debounce.ChatterClicks} adet temas sıçraması (chatter) saptandı (Risk: %{_debounce.ChatterRiskPercentage:0.#}). Çift tıklama arızalarını önlemek için uyarlamalı korumayı etkinleştirin.",
                ActionButtonText = "Korumayı Aç",
                ActionKey = "enable_adaptive_debounce"
            });
        }

        // Check for switch lifespan limits (Omron D2FC 10M / 3M)
        foreach (var (btnIdx, stats) in _switchHealth.Switches)
        {
            if (stats.WearPercentage >= 40.0)
            {
                string id = $"rec_wear_{btnIdx}";
                list.Add(new SmartRecommendation
                {
                    Id = id,
                    Category = RecommendationCategory.HardwareHealth,
                    Title = $"{stats.Name} Aşınma Düzeyi (%{stats.WearPercentage:0.#})",
                    Description = $"{stats.Name} anahtarı {stats.TotalClicks:N0} tıklamaya ulaştı. Switch sağlığını optimize etmek için debounce filtresini 10 ms yapabilirsiniz.",
                    ActionButtonText = "10 ms Yap",
                    ActionKey = "set_debounce_threshold",
                    ActionParams = { ["threshold"] = "10" }
                });
                break;
            }
        }
    }

    private void EvaluateActiveProcess(List<SmartRecommendation> list, G300sProfile[] profiles)
    {
        string currentProc = _automation.ActiveForegroundProcess.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(currentProc) || currentProc == "masaüstü" || currentProc == "explorer")
            return;

        // Check if an app rule already exists
        bool alreadyHasRule = _automation.AppRules.Any(r =>
            r.ExecutableName.Contains(currentProc, StringComparison.OrdinalIgnoreCase) ||
            currentProc.Contains(r.ExecutableName, StringComparison.OrdinalIgnoreCase));

        if (!alreadyHasRule)
        {
            // 1. Dynamic match against modular file-based Presets (Minecraft, LoL, CS2, etc.)
            var matchingPreset = PresetTemplateService.Instance.Presets
                .FirstOrDefault(p => p.ProcessNames.Any(proc => currentProc.Contains(proc, StringComparison.OrdinalIgnoreCase)));

            string templateName;
            int targetSlot;
            string title;
            string desc;

            if (matchingPreset is not null)
            {
                templateName = matchingPreset.Id;
                targetSlot = matchingPreset.Category.Equals("Oyun", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                title = $"{matchingPreset.Name} Kuralı Önerisi";
                desc = $"{matchingPreset.Name} tespit edildi. Tuş, LED ve DPI ayarlarını içeren otomatik profil geçiş kuralını bağlayın.";
            }
            else
            {
                (templateName, targetSlot, title, desc) = currentProc switch
                {
                    var p when p.Contains("blender") || p.Contains("maya") || p.Contains("3dsmax") =>
                        ("blender", 2, "Blender 3D Modelleme Kuralı Önerisi", "3D modelleme aracı açık. Extrude, Loop Cut ve Render kısayolları için Profil 3 otomatik geçişini bağlayın."),
                    var p when p.Contains("figma") || p.Contains("photoshop") || p.Contains("illustrator") =>
                        ("design", 2, "Figma & Grafik Tasarım Kuralı Önerisi", "Tasarım aracı çalışıyor. Yakınlaşma ve seçim kısayollarına sahip profil otomatik geçiş kuralını ekleyin."),
                    _ => ("", -1, "", "")
                };
            }

            if (!string.IsNullOrEmpty(templateName))
            {
                string id = $"rec_rule_{currentProc}";
                list.Add(new SmartRecommendation
                {
                    Id = id,
                    Category = RecommendationCategory.AppAutomation,
                    Title = title,
                    Description = desc,
                    ActionButtonText = "Kuralı Ekle",
                    ActionKey = "add_app_rule",
                    ActionParams = { ["process"] = currentProc, ["slot"] = targetSlot.ToString(), ["template"] = templateName }
                });
            }
        }
    }

    private void EvaluateButtonUsage(List<SmartRecommendation> list, G300sProfile[] profiles, int activeProfileIndex)
    {
        var prof = profiles[activeProfileIndex];
        // Check buttons G8 and G9
        string g8Action = prof.Buttons[7];
        string g9Action = prof.Buttons[8];

        if (g8Action == "none" || g8Action == "profile-cycle")
        {
            string id = "rec_unused_g8";
            list.Add(new SmartRecommendation
            {
                Id = id,
                Category = RecommendationCategory.UnusedKey,
                Title = "G8 Tuşuna Makro Atayın (F14 Mikrofon Sustur)",
                Description = "G8 tuşunuz standart profil döngüsünde kalmış. Bu tuşa Discord veya toplantılarda anında mikrofon susturma (F14) atayabilirsiniz.",
                ActionButtonText = "F14 Ata",
                ActionKey = "assign_button_action",
                ActionParams = { ["buttonIndex"] = "7", ["action"] = "key:f14" }
            });
        }
    }

    private void EvaluateDpiErgonomics(List<SmartRecommendation> list, G300sProfile[] profiles, int activeProfileIndex)
    {
        var (totalClicks, totalDwellSec, topZone) = _heatmap.GetScreenSummary();
        double dwellMinutes = totalDwellSec / 60.0;
        if (dwellMinutes >= 5 && topZone.Contains("Sol", StringComparison.OrdinalIgnoreCase))
        {
            string id = "rec_ergonomics_dpi";
            list.Add(new SmartRecommendation
            {
                Id = id,
                Category = RecommendationCategory.ErgonomicsDpi,
                Title = "DPI Hassasiyet Önerisi (1500 DPI)",
                Description = $"İmleç durma analizi, ekranın {topZone} bölgesinde yoğunlaştığını gösteriyor. Hassasiyetinizi optimize ederek bilek seyahat mesafesini azaltabilirsiniz.",
                ActionButtonText = "Optimize Et",
                ActionKey = "set_dpi_preset",
                ActionParams = { ["stageIndex"] = "2" }
            });
        }
    }

    public void DismissRecommendation(string id)
    {
        lock (_activeRecommendations)
        {
            var rec = _activeRecommendations.FirstOrDefault(r => r.Id == id);
            if (rec is not null)
            {
                rec.IsDismissed = true;
                _dismissedIds.Add(id);
            }
        }
        RecommendationsUpdated?.Invoke();
    }

    public void MarkApplied(string id)
    {
        lock (_activeRecommendations)
        {
            var rec = _activeRecommendations.FirstOrDefault(r => r.Id == id);
            if (rec is not null)
            {
                rec.IsApplied = true;
                _dismissedIds.Add(id);
            }
        }
        RecommendationsUpdated?.Invoke();
    }

    private void EvaluateGeneralProductivity(List<SmartRecommendation> list, G300sProfile[] profiles, int activeProfileIndex)
    {
        var prof = profiles[activeProfileIndex];

        // 1. Web Browsing Template Recommendation
        if (prof.Buttons[4] != "mouse:4" || prof.Buttons[5] != "key:f5")
        {
            list.Add(new SmartRecommendation
            {
                Id = "rec_web_template",
                Category = RecommendationCategory.AppAutomation,
                Title = "Web & Tarayıcı Gezinme Şablonu",
                Description = "Geri (G5), İleri (G4), Sayfa Yenileme F5 (G6), Yeni Sekme Ctrl+T (G7) ve Sekme Kapatma Ctrl+W (G8) atamaları ile webde gezinmeyi hızlandırın.",
                ActionButtonText = "Web Şablonunu Yükle",
                ActionKey = "apply_template",
                ActionParams = { ["template"] = "web" }
            });
        }

        // 2. 1000 Hz Polling Rate Recommendation
        if (prof.PollingRateHz < 1000)
        {
            list.Add(new SmartRecommendation
            {
                Id = "rec_polling_rate_1000",
                Category = RecommendationCategory.ErgonomicsDpi,
                Title = "1000 Hz / 1 ms Tepki Süresi Optimizasyonu",
                Description = $"Mevcut profiliniz {prof.PollingRateHz} Hz ile çalışıyor. 1000 Hz seviyesine çıkararak gecikmeyi 1 ms'ye düşürebilir ve imleç akıcılığını artırabilirsiniz.",
                ActionButtonText = "1000 Hz Yap",
                ActionKey = "set_polling_rate",
                ActionParams = { ["rate"] = "1000" }
            });
        }

        // 3. G-Shift Secondary Layer Discovery
        if (!prof.Buttons.Any(b => b.Contains("f18", StringComparison.OrdinalIgnoreCase)))
        {
            list.Add(new SmartRecommendation
            {
                Id = "rec_gshift_discovery",
                Category = RecommendationCategory.UnusedKey,
                Title = "G-Shift İkincil Tuş Katmanını Kullanın",
                Description = "G-Shift tuşu sayesinde farenizin 6 ekstra makro tuşuna ikinci bir işlev katmanı bağlayarak toplam 12 farklı eylem tanımlayabilirsiniz.",
                ActionButtonText = "Otomasyona Git",
                ActionKey = "open_automation"
            });
        }
    }

}
