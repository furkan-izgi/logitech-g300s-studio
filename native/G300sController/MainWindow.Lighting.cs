using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace G300sController;

public partial class MainWindow
{
    private void UpdateTintedSwoosh24Bit(byte r, byte g, byte b)
    {
        if (_rawSwooshBitmap is null) return;
        if (_lastRenderedSwooshColor.R == r && _lastRenderedSwooshColor.G == g && _lastRenderedSwooshColor.B == b)
            return;

        _lastRenderedSwooshColor = (r, g, b);

        var bgra = new FormatConvertedBitmap(_rawSwooshBitmap, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth;
        int h = bgra.PixelHeight;
        byte[] pixels = new byte[w * h * 4];
        bgra.CopyPixels(pixels, w * 4, 0);

        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte lum = pixels[i + 2];
            pixels[i + 0] = (byte)(lum * b / 255);
            pixels[i + 1] = (byte)(lum * g / 255);
            pixels[i + 2] = (byte)(lum * r / 255);
        }

        var wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, w, h), pixels, w * 4, 0);
        wb.Freeze();

        MouseSwooshImage.Source = wb;
        LightingPreviewSwooshImage.Source = wb;

        SpotlightStop0.Color = Color.FromArgb(26, r, g, b);
        LightingPreviewGlowStop.Color = Color.FromArgb(48, r, g, b);
    }

    private void LivePreviewTimer_Tick(object? sender, EventArgs e)
    {
        if (_fluidRgbEnabled)
        {
            double time = Environment.TickCount / 1200.0;
            byte r = (byte)(Math.Sin(time) * 127 + 128);
            byte g = (byte)(Math.Sin(time + 2.094) * 127 + 128);
            byte b = (byte)(Math.Sin(time + 4.188) * 127 + 128);
            UpdateTintedSwoosh24Bit(r, g, b);

            if (_activeViewIndex == 2 && LiveColorPreviewOrb is not null && LiveTelemetryTitleText is not null)
            {
                var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
                LiveColorPreviewOrb.Background = brush;
                LiveTelemetryProgressFill.Background = brush;
                LiveTelemetryTitleText.Text = "Canlı Sıvı RGB Spektrumu";
                ActiveColorLabel.Text = $"#{r:X2}{g:X2}{b:X2}";
            }
        }
        else if (_automation.ActiveRgbEffect != "none")
        {
            var (r, g, b) = _automation.LivePreviewColor;
            UpdateTintedSwoosh24Bit(r, g, b);

            if (_activeViewIndex == 2)
            {
                var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
                LiveColorPreviewOrb.Background = brush;
                LiveTelemetryProgressFill.Background = brush;
                LiveTelemetryProgressFill.Width = Math.Clamp(_automation.LiveTelemetryLevel * 380.0, 12.0, 380.0);
                LiveTelemetryTitleText.Text = _automation.LiveTelemetryText;
            }
        }
    }

    private void BuildLightingStudioUi()
    {
        LightingModesList.Children.Clear();

        for (int i = 0; i < LightingModes.Length; i++)
        {
            var mode = LightingModes[i];
            int idx = i;

            var row = new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = Brushes.Transparent,
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 0, 0, 4),
                Cursor = Cursors.Hand
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var radioOuter = new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(8),
                BorderBrush = (Brush)FindResource("BrushTextMuted"),
                BorderThickness = new Thickness(1.5),
                Margin = new Thickness(0, 0, 14, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var radioInner = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            radioOuter.Child = radioInner;
            _lightingModeDots[idx] = radioInner;
            Grid.SetColumn(radioOuter, 0);
            grid.Children.Add(radioOuter);

            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            textStack.Children.Add(new TextBlock
            {
                Text = mode.Title,
                FontFamily = (FontFamily)FindResource("FontDisplay"),
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("BrushTextPrimary")
            });
            textStack.Children.Add(new TextBlock
            {
                Text = mode.Description,
                FontSize = 12,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                Margin = new Thickness(0, 2, 0, 0)
            });
            Grid.SetColumn(textStack, 1);
            grid.Children.Add(textStack);

            row.Child = grid;
            row.MouseLeftButtonDown += (_, _) => SelectLightingMode(mode.Key);
            _lightingModeBorders[idx] = row;
            LightingModesList.Children.Add(row);
        }

        if (LedSwatchesGrid is not null)
        {
            LedSwatchesGrid.Children.Clear();
            for (int i = 0; i < PaletteSwatches.Length; i++)
            {
                var sw = PaletteSwatches[i];
                int idx = i;
                var orb = CreateColorOrb(sw.Hex, $"{sw.Name} ({sw.Hex})", () => SelectLedColor(sw.R, sw.G, sw.B));
                _ledSwatchBorders[idx] = orb;
                LedSwatchesGrid.Children.Add(orb);
            }
        }

        UpdateLightingModeSelectionVisuals("none");
    }

    private static Border CreateColorOrb(string hex, string tooltip, Action onClick)
    {
        var outer = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            Padding = new Thickness(2),
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var inner = new Border
        {
            CornerRadius = new CornerRadius(13),
            Background = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!,
            BorderBrush = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)),
            BorderThickness = new Thickness(1)
        };

        outer.Child = inner;
        outer.MouseLeftButtonDown += (_, _) => onClick();
        return outer;
    }

    private void SelectLedColor(int r, int g, int b)
    {
        var prof = _profiles[_activeProfileIndex];
        prof.SetLed(r, g, b);
        _activeCustomColor24Bit = null;

        _automation.UpdateRgbParameters(2400, (r, g, b), _secondaryBreathColor);

        if (_device.IsConnected && _automation.ActiveRgbEffect == "none")
        {
            try { _device.SetLiveLed(r, g, b); } catch { /* ignore */ }
        }

        MarkModified($"Profil {_activeProfileIndex + 1} ana rengi {prof.ColorName} olarak seçildi.");
        RefreshSlotHeaderVisuals();
        RefreshCurrentProfileToUi();
    }

    private void SelectSecondaryLedColor(int r, int g, int b)
    {
        _secondaryBreathColor = (r, g, b);
        var prof = _profiles[_activeProfileIndex];
        _automation.UpdateRgbParameters(2400, (prof.LedR, prof.LedG, prof.LedB), _secondaryBreathColor);
        if (StatusFooterText is not null) StatusFooterText.Text = "İkincil geçiş rengi güncellendi.";
    }

    private void UpdateSecondarySwatchVisuals()
    {
        for (int i = 0; i < PaletteSwatches.Length; i++)
        {
            var sw = PaletteSwatches[i];
            bool selected = _secondaryBreathColor.R == sw.R && _secondaryBreathColor.G == sw.G && _secondaryBreathColor.B == sw.B;
            _secondarySwatchBorders[i].BorderBrush = selected ? (Brush)FindResource("BrushActiveWhite") : Brushes.Transparent;
        }
    }

    private void SelectLightingMode(string effectKey)
    {
        var prof = _profiles[_activeProfileIndex];
        int speedMs = 2400;
        _automation.SetRgbEffect(effectKey, speedMs, (prof.LedR, prof.LedG, prof.LedB), _secondaryBreathColor);

        if (effectKey == "none" && _device.IsConnected)
        {
            try { _device.SetLiveLed(prof.LedR, prof.LedG, prof.LedB); } catch { /* ignore */ }
        }

        UpdateLightingModeSelectionVisuals(effectKey);
        RefreshCurrentProfileToUi();
        if (StatusFooterText is not null) StatusFooterText.Text = $"Aydınlatma modu '{effectKey}' olarak güncellendi.";
    }

    private void UpdateLightingModeSelectionVisuals(string activeKey)
    {
        for (int i = 0; i < LightingModes.Length; i++)
        {
            bool active = string.Equals(LightingModes[i].Key, activeKey, StringComparison.OrdinalIgnoreCase);
            _lightingModeBorders[i].Background = active
                ? (Brush)FindResource("BrushBgSurface2")
                : Brushes.Transparent;
            _lightingModeDots[i].Fill = active
                ? (Brush)FindResource("BrushActiveWhite")
                : Brushes.Transparent;
        }
    }

    private void FluidRgbSettingsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || FluidRgbSettingsToggle is null) return;
        _fluidRgbEnabled = FluidRgbSettingsToggle.IsChecked == true;
        if (FluidRgbToggle is not null && FluidRgbToggle.IsChecked != _fluidRgbEnabled)
        {
            FluidRgbToggle.IsChecked = _fluidRgbEnabled;
        }
        if (!_fluidRgbEnabled)
        {
            var col = (Color)ColorConverter.ConvertFromString(_profiles[_activeProfileIndex].ColorHex);
            UpdateTintedSwoosh24Bit(col.R, col.G, col.B);
        }
        if (StatusFooterText is not null) StatusFooterText.Text = _fluidRgbEnabled ? "Önizlemede canlı sıvı RGB spektrum dalgası başlatıldı." : "Sıvı RGB spektrumu durduruldu.";
    }

    private void FluidRgbToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || FluidRgbToggle is null) return;
        _fluidRgbEnabled = FluidRgbToggle.IsChecked == true;
        if (FluidRgbSettingsToggle is not null && FluidRgbSettingsToggle.IsChecked != _fluidRgbEnabled)
        {
            FluidRgbSettingsToggle.IsChecked = _fluidRgbEnabled;
        }
        if (!_fluidRgbEnabled)
        {
            var col = (Color)ColorConverter.ConvertFromString(_profiles[_activeProfileIndex].ColorHex);
            UpdateTintedSwoosh24Bit(col.R, col.G, col.B);
        }
        if (StatusFooterText is not null) StatusFooterText.Text = _fluidRgbEnabled ? "Önizlemede canlı sıvı RGB spektrum dalgası başlatıldı." : "Sıvı RGB spektrumu durduruldu.";
    }
}
