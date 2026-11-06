using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using G300sController.Models;
using G300sController.Services;
using Microsoft.Win32;

namespace G300sController;

public partial class MainWindow
{
    private void SniperToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents) return;
        bool enabled = SniperToggle.IsChecked == true;
        if (StatusFooterText is not null) StatusFooterText.Text = enabled
            ? $"G-Shift Nişan (Sniper) Modu devrede: Hedef {_selectedSniperDpi} DPI."
            : "G-Shift Nişan (Sniper) Modu kapatıldı.";
    }

    private void SniperDpiCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || SniperDpiCombo is null) return;
        if (SniperDpiCombo.SelectedItem is ComboBoxItem { Tag: string t } && int.TryParse(t, out int dpi))
        {
            _selectedSniperDpi = dpi;
            _sniper.TargetSniperDpi = dpi;
            if (SniperStatusText is not null)
            {
                SniperStatusText.Text = $"G-Shift basıldığında hedef hassasiyeti: {_selectedSniperDpi} DPI (Hazır)";
            }
        }
    }

    private void DebounceToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents) return;
        bool enabled = DebounceToggle.IsChecked == true;
        _debounce.IsEnabled = enabled;
        if (StatusFooterText is not null) StatusFooterText.Text = enabled
            ? $"Akıllı çift tıklama filtresi devrede ({_debounce.DebounceThresholdMs} ms)."
            : "Çift tıklama filtresi devre dışı.";
        if (DebounceStatusText is not null)
        {
            DebounceStatusText.Text = enabled
                ? $"Switch sekme koruması aktif · Engellenen: {_debounce.FilteredClicksCount} tık"
                : "Filtre devre dışı";
        }
    }

    private void DebounceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressUiEvents || DebounceSlider is null) return;
        int ms = (int)Math.Round(DebounceSlider.Value);
        _debounce.DebounceThresholdMs = ms;
        if (DebounceValueText is not null)
        {
            DebounceValueText.Text = $"{ms} ms";
        }
    }

    private void ScreenHeatmapBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ScreenHeatmapOverlayCard is null) return;
        ScreenHeatmapOverlayCard.Visibility = ScreenHeatmapOverlayCard.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (ScreenHeatmapOverlayCard.Visibility == Visibility.Visible)
        {
            RefreshScreenHeatmapVisual();
        }
    }

    private void CloseScreenHeatmap_Click(object sender, RoutedEventArgs e)
    {
        if (ScreenHeatmapOverlayCard is not null)
        {
            ScreenHeatmapOverlayCard.Visibility = Visibility.Collapsed;
        }
    }

    private void HeatmapModeRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents) return;
        RefreshScreenHeatmapVisual();
    }

    private void RefreshScreenHeatmap_Click(object sender, RoutedEventArgs e)
    {
        RefreshScreenHeatmapVisual();
    }

    private void ResetScreenHeatmap_Click(object sender, RoutedEventArgs e)
    {
        _heatmap.ResetStats();
        RefreshScreenHeatmapVisual();
        UpdateHeatmapHeaderStats();
        if (StatusFooterText is not null) StatusFooterText.Text = "Ekran tıklama ve gezinme ısı haritası sıfırlandı.";
    }

    private void RefreshScreenHeatmapVisual()
    {
        if (ScreenHeatmapImage is null || ScreenHeatmapOverlayCard is null) return;
        bool isDwell = HeatmapModeDwellRadio?.IsChecked == true;

        var bmp = _heatmap.RenderHeatmapBitmap(960, 540, isDwell);
        ScreenHeatmapImage.Source = bmp;

        var (totalClicks, totalDwell, topZone) = _heatmap.GetScreenSummary();
        int dwellMins = (int)(totalDwell / 60.0);
        int dwellSecs = (int)(totalDwell % 60.0);

        if (ScreenHeatmapSummaryText is not null)
        {
            ScreenHeatmapSummaryText.Text = $"Toplam Tıklama: {totalClicks:N0} · Toplam Durma Süresi: {dwellMins} dk {dwellSecs} sn · En Yoğun Bölge: {topZone}";
        }
    }

    private void SwitchHealthBtn_Click(object sender, RoutedEventArgs e)
    {
        if (SwitchHealthOverlayCard is null) return;
        SwitchHealthOverlayCard.Visibility = SwitchHealthOverlayCard.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (SwitchHealthOverlayCard.Visibility == Visibility.Visible)
        {
            UpdateSwitchHealthUi();
        }
    }

    private void CloseSwitchHealth_Click(object sender, RoutedEventArgs e)
    {
        if (SwitchHealthOverlayCard is not null)
        {
            SwitchHealthOverlayCard.Visibility = Visibility.Collapsed;
        }
    }

    private void ResetSwitchHealth_Click(object sender, RoutedEventArgs e)
    {
        _switchHealth.ResetAll();
        UpdateSwitchHealthUi();
        if (StatusFooterText is not null) StatusFooterText.Text = "Switch tıklama sayaçları sıfırlandı.";
    }

    private void UpdateSwitchHealthUi()
    {
        if (OverallHealthText is null || OverallHealthDot is null) return;

        double totalHealth = 0;
        int count = 0;

        if (SwitchHealthBarsPanel is not null) SwitchHealthBarsPanel.Children.Clear();

        foreach (var (idx, stats) in _switchHealth.Switches)
        {
            totalHealth += stats.HealthPercentage;
            count++;

            if (SwitchHealthBarsPanel is not null)
            {
                SwitchHealthBarsPanel.Children.Add(CreateSwitchHealthRow(stats, 130.0));
            }
        }

        double avgHealth = count > 0 ? (totalHealth / count) : 100.0;
        OverallHealthText.Text = $"Switch: %{avgHealth:0.0}";
        OverallHealthDot.Fill = avgHealth > 85.0
            ? (Brush)FindResource("BrushSuccess")
            : (avgHealth > 60.0 ? (Brush)FindResource("BrushWarning") : (Brush)FindResource("BrushDanger"));
    }

    private FrameworkElement CreateSwitchHealthRow(SwitchStats stats, double labelColWidth)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelColWidth, GridUnitType.Pixel) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55, GridUnitType.Pixel) });

        var nameStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        nameStack.Children.Add(new TextBlock
        {
            Text = stats.Name,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("BrushTextPrimary")
        });
        nameStack.Children.Add(new TextBlock
        {
            Text = $"{stats.TotalClicks:N0} / {stats.RatedLifespanClicks:N0} tık",
            FontSize = 11,
            Foreground = (Brush)FindResource("BrushTextMuted")
        });
        Grid.SetColumn(nameStack, 0);
        grid.Children.Add(nameStack);

        var trackBorder = new Border
        {
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = (Brush)FindResource("BrushBgSurface2"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 0),
            ClipToBounds = true
        };

        var trackGrid = new Grid();
        double healthRatio = Math.Clamp(stats.HealthPercentage, 0.0, 100.0);
        double wearRatio = Math.Max(0.001, 100.0 - healthRatio);

        trackGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(healthRatio, GridUnitType.Star) });
        trackGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(wearRatio, GridUnitType.Star) });

        var healthBrush = (Brush)new BrushConverter().ConvertFromString(stats.HealthColorHex)!;
        var fillBorder = new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = healthBrush
        };
        Grid.SetColumn(fillBorder, 0);
        trackGrid.Children.Add(fillBorder);

        trackBorder.Child = trackGrid;
        Grid.SetColumn(trackBorder, 1);
        grid.Children.Add(trackBorder);

        var percentText = new TextBlock
        {
            Text = $"%{stats.HealthPercentage:0.#}",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = healthBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(percentText, 2);
        grid.Children.Add(percentText);

        return grid;
    }

    private void AngleSnappingToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents) return;
        if (AngleSnappingToggle is null) return;
        _angleSnapping.IsEnabled = AngleSnappingToggle.IsChecked == true;
        if (_angleSnapping.IsEnabled)
        {
            _angleSnapping.Start();
        }
        else
        {
            _angleSnapping.Stop();
        }
        if (AngleSnappingStatusText is not null)
        {
            AngleSnappingStatusText.Text = _angleSnapping.IsEnabled
                ? $"Açı sabitleme aktif ({_angleSnapping.AngleThresholdDegrees}°) · Düz çizgi kılavuzu devrede"
                : "Açı sabitleme devre dışı · Serbest hareket";
        }
        if (StatusFooterText is not null) StatusFooterText.Text = _angleSnapping.IsEnabled ? "Açı sabitleme filtresi etkinleştirildi." : "Açı sabitleme kapatıldı.";
    }

    private void AngleSnappingCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || AngleSnappingCombo?.SelectedItem is not ComboBoxItem item) return;
        if (double.TryParse(item.Tag?.ToString(), out double deg))
        {
            _angleSnapping.AngleThresholdDegrees = deg;
            if (AngleSnappingStatusText is not null && _angleSnapping.IsEnabled)
            {
                AngleSnappingStatusText.Text = $"Açı sabitleme aktif ({deg}°) · Düz çizgi kılavuzu devrede";
            }
        }
    }

    private void RegisterCardInteractions(Border card)
    {
        if (card is null) return;
        var tt = new TranslateTransform();
        card.RenderTransform = tt;
        card.RenderTransformOrigin = new Point(0.5, 0.5);
        var defaultBorder = card.BorderBrush ?? (Brush)FindResource("BrushBorderSubtle");
        var activeGlow = (Brush)FindResource("BrushAccentCyanBorder");

        card.MouseEnter += (s, e) =>
        {
            if (!_microInteractionsEnabled) return;
            var anim = new DoubleAnimation(-3.0, TimeSpan.FromMilliseconds(160)) { DecelerationRatio = 0.8 };
            tt.BeginAnimation(TranslateTransform.YProperty, anim);
            card.BorderBrush = activeGlow;
        };

        card.MouseLeave += (s, e) =>
        {
            var anim = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(200)) { AccelerationRatio = 0.8 };
            tt.BeginAnimation(TranslateTransform.YProperty, anim);
            card.BorderBrush = defaultBorder;
        };
    }

    private void SetupAllCardMicroInteractions()
    {
        AttachCardInteractionsRecursive(this);
    }

    private void AttachCardInteractionsRecursive(DependencyObject parent)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Border b && b.Style == (Style)FindResource("StudioCardBorder"))
            {
                RegisterCardInteractions(b);
            }
            AttachCardInteractionsRecursive(child);
        }
    }

    private void ApplyBackdropVisualStyles(WindowBackdropMode mode)
    {
        bool isTranslucent = mode is WindowBackdropMode.Mica or WindowBackdropMode.Acrylic or WindowBackdropMode.MicaAlt;

        if (isTranslucent)
        {
            this.Background = Brushes.Transparent;
            if (WindowOuterBorder is not null)
            {
                WindowOuterBorder.Background = new SolidColorBrush(Color.FromArgb(180, 0x0E, 0x0E, 0x13));
                WindowOuterBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF));
            }
            Application.Current.Resources["BrushBgCanvas"] = new SolidColorBrush(Color.FromArgb(180, 0x0E, 0x0E, 0x13));
            Application.Current.Resources["BrushBgRail"] = new SolidColorBrush(Color.FromArgb(200, 0x12, 0x12, 0x18));
            Application.Current.Resources["BrushBgHeader"] = new SolidColorBrush(Color.FromArgb(190, 0x12, 0x12, 0x18));
            Application.Current.Resources["BrushBgSurface1"] = new SolidColorBrush(Color.FromArgb(220, 0x18, 0x18, 0x20));
            Application.Current.Resources["BrushBgSurface2"] = new SolidColorBrush(Color.FromArgb(230, 0x20, 0x20, 0x2A));
            Application.Current.Resources["BrushBgElevated"] = new SolidColorBrush(Color.FromArgb(240, 0x28, 0x28, 0x34));
        }
        else
        {
            var canvasBrush = new SolidColorBrush(Color.FromRgb(0x09, 0x09, 0x0B));
            this.Background = canvasBrush;
            if (WindowOuterBorder is not null)
            {
                WindowOuterBorder.Background = canvasBrush;
                WindowOuterBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
            }
            Application.Current.Resources["BrushBgCanvas"] = canvasBrush;
            Application.Current.Resources["BrushBgRail"] = new SolidColorBrush(Color.FromRgb(0x0E, 0x0E, 0x11));
            Application.Current.Resources["BrushBgHeader"] = new SolidColorBrush(Color.FromRgb(0x0E, 0x0E, 0x11));
            Application.Current.Resources["BrushBgSurface1"] = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x18));
            Application.Current.Resources["BrushBgSurface2"] = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x22));
            Application.Current.Resources["BrushBgElevated"] = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x2E));
        }
    }

    private void MicaBackdropToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents) return;
        if (MicaBackdropToggle is null) return;
        _isMicaBackdropEnabled = MicaBackdropToggle.IsChecked == true;
        if (_isMicaBackdropEnabled)
        {
            bool ok = WindowBackdropService.ApplyBackdrop(this, _activeBackdropMode);
            if (ok)
            {
                ApplyBackdropVisualStyles(_activeBackdropMode);
                if (StatusFooterText is not null) StatusFooterText.Text = $"Windows 11 {_activeBackdropMode} yarı saydam akrilik cam uygulandı.";
            }
            else
            {
                WindowBackdropService.ApplyBackdrop(this, WindowBackdropMode.Solid);
                ApplyBackdropVisualStyles(WindowBackdropMode.Solid);
                if (StatusFooterText is not null) StatusFooterText.Text = "Sistem donanım/Windows sürümü bu efekti desteklemediği için mat koyu tema uygulandı.";
            }
        }
        else
        {
            WindowBackdropService.ApplyBackdrop(this, WindowBackdropMode.Solid);
            ApplyBackdropVisualStyles(WindowBackdropMode.Solid);
            if (StatusFooterText is not null) StatusFooterText.Text = "Mat karbon katı arka plan uygulandı.";
        }
    }

    private void MicaModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || MicaModeCombo?.SelectedItem is not ComboBoxItem item) return;
        int modeVal = int.Parse(item.Tag?.ToString() ?? "1");
        _activeBackdropMode = (WindowBackdropMode)modeVal;
        if (MicaBackdropToggle?.IsChecked == true)
        {
            bool ok = WindowBackdropService.ApplyBackdrop(this, _activeBackdropMode);
            if (ok)
            {
                ApplyBackdropVisualStyles(_activeBackdropMode);
            }
            else
            {
                WindowBackdropService.ApplyBackdrop(this, WindowBackdropMode.Solid);
                ApplyBackdropVisualStyles(WindowBackdropMode.Solid);
            }
        }
    }

    private void MicroInteractionsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents) return;
        if (MicroInteractionsToggle is null) return;
        _microInteractionsEnabled = MicroInteractionsToggle.IsChecked == true;
        if (StatusFooterText is not null) StatusFooterText.Text = _microInteractionsEnabled ? "Akıcı hover animasyonları aktif." : "Hover animasyonları devre dışı.";
    }

    private void HandleGShiftFeedback(bool isActive, int slot)
    {
        if (GShiftOverlayBadge is not null && _gShiftVisualFeedbackEnabled)
        {
            GShiftOverlayBadge.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
        }
        if (_gShiftAudioFeedbackEnabled)
        {
            try
            {
                if (isActive)
                    System.Media.SystemSounds.Beep.Play();
                else
                    System.Media.SystemSounds.Asterisk.Play();
            }
            catch { }
        }
    }

    private void GShiftVisualToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || GShiftVisualToggle is null) return;
        _gShiftVisualFeedbackEnabled = GShiftVisualToggle.IsChecked == true;
    }

    private void GShiftAudioToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || GShiftAudioToggle is null) return;
        _gShiftAudioFeedbackEnabled = GShiftAudioToggle.IsChecked == true;
    }

    private void UpdateChatterStatsUi()
    {
        if (ChatterStatusText is null) return;
        ChatterStatusText.Text = $"Engellenen Sıçrama: {_debounce.ChatterClicks:N0} tık · Risk Seviyesi: {_debounce.ChatterRiskStatus}";
    }

    private void AdaptiveChatterToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || AdaptiveChatterToggle is null) return;
        _debounce.AdaptiveDebounce = AdaptiveChatterToggle.IsChecked == true;
        if (StatusFooterText is not null) StatusFooterText.Text = _debounce.AdaptiveDebounce
            ? "Akıllı uyarlamalı chatter koruması aktif."
            : "Uyarlamalı koruma kapatıldı (sabit eşik devrede).";
    }

    private void ResetChatterStats_Click(object sender, RoutedEventArgs e)
    {
        UpdateChatterStatsUi();
        if (StatusFooterText is not null) StatusFooterText.Text = "Chatter ve sıçrama sayaçları sıfırlandı.";
    }

    private void ExportProfiles_Click(object sender, RoutedEventArgs e)
    {
        var sfd = new SaveFileDialog
        {
            Title = "G300s Profil ve Ayar Yedeğini Kaydet",
            Filter = "G300s Profil Yedeği (*.json)|*.json|Tüm Dosyalar (*.*)|*.*",
            FileName = $"G300s_Profiles_Backup_{DateTime.Now:yyyyMMdd_HHmmss}.json"
        };

        if (sfd.ShowDialog() == true)
        {
            try
            {
                var backupObj = new JsonObject
                {
                    ["version"] = "11.0",
                    ["exportTimestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    ["profiles"] = JsonSerializer.SerializeToNode(_profiles),
                    ["appRules"] = JsonSerializer.SerializeToNode(_automation.AppRules)
                };
                File.WriteAllText(sfd.FileName, backupObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                if (StatusFooterText is not null) StatusFooterText.Text = $"Profiller ve makrolar yedeklendi: {Path.GetFileName(sfd.FileName)}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Yedekleme sırasında hata oluştu:\n{ex.Message}", "Yedekleme Hatası", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void ImportProfiles_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog
        {
            Title = "G300s Profil ve Ayar Yedeğini Seç",
            Filter = "G300s Profil Yedeği (*.json)|*.json|Tüm Dosyalar (*.*)|*.*"
        };

        if (ofd.ShowDialog() == true)
        {
            try
            {
                string json = File.ReadAllText(ofd.FileName);
                var node = JsonNode.Parse(json);
                if (node is not null && node["profiles"] is not null)
                {
                    var loaded = JsonSerializer.Deserialize<G300sProfile[]>(node["profiles"]!.ToJsonString());
                    if (loaded is not null && loaded.Length >= 3)
                    {
                        for (int i = 0; i < 3; i++) _profiles[i] = loaded[i];
                        if (_device.IsConnected)
                        {
                            for (int i = 0; i < 3; i++)
                            {
                                try { _device.SaveProfile(_profiles[i], verify: true); } catch { }
                            }
                        }
                        RefreshSlotHeaderVisuals();
                        RefreshCurrentProfileToUi();
                        if (StatusFooterText is not null) StatusFooterText.Text = "Yedek başarıyla geri yüklendi ve donanıma yazıldı.";
                        MessageBox.Show("Profil ve makro yedeği başarıyla geri yüklendi!", "Geri Yükleme Başarılı", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Yedek geri yüklenirken hata oluştu:\n{ex.Message}", "Geri Yükleme Hatası", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    public void ShowToast(string message, string hexColor = "#00F0FF")
    {
        if (ToastBanner is null || ToastMessageText is null || ToastDot is null) return;
        ToastMessageText.Text = message;
        ToastDot.Background = (Brush)new BrushConverter().ConvertFromString(hexColor)!;

        ToastBanner.Visibility = Visibility.Visible;
        var tt = new TranslateTransform(0, 18);
        ToastBanner.RenderTransform = tt;
        ToastBanner.Opacity = 0.0;

        var animIn = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(180)) { DecelerationRatio = 0.8 };
        var animSlide = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(180)) { DecelerationRatio = 0.8 };
        ToastBanner.BeginAnimation(UIElement.OpacityProperty, animIn);
        tt.BeginAnimation(TranslateTransform.YProperty, animSlide);

        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2600) };
        _toastTimer.Tick += (s, e) =>
        {
            _toastTimer.Stop();
            var animOut = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(220)) { AccelerationRatio = 0.8 };
            animOut.Completed += (_, _) => ToastBanner.Visibility = Visibility.Collapsed;
            ToastBanner.BeginAnimation(UIElement.OpacityProperty, animOut);
        };
        _toastTimer.Start();
    }

    private void SmartAdvisorHeaderBtn_Click(object sender, RoutedEventArgs e)
    {
        if (SmartAdvisorOverlayCard is null) return;
        SmartAdvisorOverlayCard.Visibility = SmartAdvisorOverlayCard.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (SmartAdvisorOverlayCard.Visibility == Visibility.Visible)
        {
            _advisor.EvaluateTelemetry(_profiles, _activeProfileIndex);
            UpdateSmartAdvisorUi();
        }
    }

    private void CloseSmartAdvisor_Click(object sender, RoutedEventArgs e)
    {
        if (SmartAdvisorOverlayCard is not null) SmartAdvisorOverlayCard.Visibility = Visibility.Collapsed;
    }

    private void RefreshSmartAdvisor_Click(object sender, RoutedEventArgs e)
    {
        _advisor.EvaluateTelemetry(_profiles, _activeProfileIndex);
        UpdateSmartAdvisorUi();
        ShowToast("Kullanım telemetrisi yeniden analiz edildi.");
    }

    private void UpdateSmartAdvisorUi()
    {
        if (SmartAdvisorCardsPanel is null || SmartAdvisorCountText is null || SmartAdvisorEmptyText is null) return;
        SmartAdvisorCardsPanel.Children.Clear();

        var recs = _advisor.Recommendations;
        SmartAdvisorCountText.Text = $"{recs.Count} Öneri";
        SmartAdvisorBadgeDot.Visibility = recs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (recs.Count == 0)
        {
            SmartAdvisorEmptyText.Visibility = Visibility.Visible;
            return;
        }

        SmartAdvisorEmptyText.Visibility = Visibility.Collapsed;
        foreach (var rec in recs)
        {
            var card = CreateRecommendationCard(rec);
            SmartAdvisorCardsPanel.Children.Add(card);
        }
    }

    private FrameworkElement CreateRecommendationCard(SmartRecommendation rec)
    {
        var border = new Border
        {
            Background = (Brush)FindResource("BrushBgSurface2"),
            BorderBrush = (Brush)FindResource("BrushBorderSubtle"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(0, 0, 0, 10)
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var headerStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var badge = new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(7, 2, 7, 2),
            Margin = new Thickness(0, 0, 8, 0),
            Background = rec.Category switch
            {
                RecommendationCategory.HardwareHealth => (Brush)FindResource("BrushWarning"),
                RecommendationCategory.AppAutomation => (Brush)FindResource("BrushAccentCyanBorder"),
                RecommendationCategory.UnusedKey => (Brush)FindResource("BrushSuccess"),
                _ => (Brush)FindResource("BrushBgElevated")
            }
        };
        badge.Child = new TextBlock
        {
            Text = rec.CategoryTitle,
            FontSize = 10.5,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(9, 9, 11))
        };
        headerStack.Children.Add(badge);

        headerStack.Children.Add(new TextBlock
        {
            Text = rec.Title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("BrushTextPrimary"),
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetRow(headerStack, 0);
        grid.Children.Add(headerStack);

        var descText = new TextBlock
        {
            Text = rec.Description,
            FontSize = 11.5,
            Foreground = (Brush)FindResource("BrushTextMuted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 10)
        };
        Grid.SetRow(descText, 1);
        grid.Children.Add(descText);

        var actionsPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var applyBtn = new Button
        {
            Content = rec.ActionButtonText,
            Style = (Style)FindResource("PrimaryAccentButton"),
            Height = 26,
            Padding = new Thickness(14, 0, 14, 0),
            Margin = new Thickness(0, 0, 8, 0)
        };
        applyBtn.Click += (_, _) => ApplyRecommendation(rec);
        actionsPanel.Children.Add(applyBtn);

        var dismissBtn = new Button
        {
            Content = "Gizle",
            Style = (Style)FindResource("ToolbarActionButton"),
            Height = 26,
            Padding = new Thickness(10, 0, 10, 0)
        };
        dismissBtn.Click += (_, _) =>
        {
            _advisor.DismissRecommendation(rec.Id);
            UpdateSmartAdvisorUi();
        };
        actionsPanel.Children.Add(dismissBtn);

        Grid.SetRow(actionsPanel, 2);
        grid.Children.Add(actionsPanel);

        border.Child = grid;
        return border;
    }

    private void ApplyRecommendation(SmartRecommendation rec)
    {
        switch (rec.ActionKey)
        {
            case "apply_template":
                if (rec.ActionParams.TryGetValue("template", out string? tmpl))
                {
                    ApplyPresetTemplate(tmpl);
                }
                break;

            case "set_polling_rate":
                if (rec.ActionParams.TryGetValue("rate", out string? rStr) && int.TryParse(rStr, out int rateHz))
                {
                    _profiles[_activeProfileIndex].PollingRateHz = rateHz;
                    if (_device.IsConnected)
                    {
                        try { _device.SetLiveRate(rateHz); } catch { /* ignore */ }
                    }
                    MarkModified($"Yoklama hızı {rateHz} Hz olarak güncellendi.");
                    RefreshCurrentProfileToUi();
                    ShowToast($"Yoklama hızı {rateHz} Hz yapıldı!");
                }
                break;

            case "open_automation":
                SwitchMainView(3);
                CloseSmartAdvisor_Click(this, new RoutedEventArgs());
                break;
            case "enable_adaptive_debounce":
                _debounce.AdaptiveDebounce = true;
                _debounce.DebounceThresholdMs = 12;
                if (AdaptiveChatterToggle is not null) AdaptiveChatterToggle.IsChecked = true;
                ShowToast("Akıllı uyarlamalı chatter koruması açıldı (12 ms).");
                break;

            case "set_debounce_threshold":
                if (rec.ActionParams.TryGetValue("threshold", out string? thStr) && int.TryParse(thStr, out int th))
                {
                    _debounce.DebounceThresholdMs = th;
                    ShowToast($"Debounce süresi {th} ms olarak ayarlandı.");
                }
                break;

            case "add_app_rule":
                if (rec.ActionParams.TryGetValue("process", out string? proc) &&
                    rec.ActionParams.TryGetValue("slot", out string? sStr) && int.TryParse(sStr, out int slot))
                {
                    var existing = _automation.AppRules.FirstOrDefault(r => string.Equals(r.ExecutableName, proc, StringComparison.OrdinalIgnoreCase));
                    if (existing is not null) existing.ProfileIndex = slot;
                    else _automation.AppRules.Add(new AppProfileRule { ExecutableName = proc, ProfileIndex = slot });
                    _automation.SaveConfig();
                    RefreshAppRulesListUi();
                    ShowToast($"Otomatik geçiş kuralı eklendi: {proc} → Profil {slot + 1}");
                }
                break;

            case "assign_button_action":
                if (rec.ActionParams.TryGetValue("buttonIndex", out string? bStr) && int.TryParse(bStr, out int bIdx) &&
                    rec.ActionParams.TryGetValue("action", out string? act))
                {
                    var prof = _profiles[_activeProfileIndex];
                    prof.Buttons[bIdx] = act;
                    MarkModified($"G{bIdx + 1} tuşuna {act} atandı.");
                    RefreshCurrentProfileToUi();
                    ShowToast($"G{bIdx + 1} tuşuna kısayol atandı!");
                }
                break;

            case "set_dpi_preset":
                if (rec.ActionParams.TryGetValue("stageIndex", out string? stStr) && int.TryParse(stStr, out int stIdx))
                {
                    ActivateDpiStage(stIdx);
                    ShowToast($"DPI Kademe {stIdx + 1} olarak optimize edildi.");
                }
                break;
        }

        _advisor.MarkApplied(rec.Id);
        UpdateSmartAdvisorUi();
    }
}
