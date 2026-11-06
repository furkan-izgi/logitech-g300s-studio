using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using G300sController.Models;

namespace G300sController;

public partial class MainWindow
{
    private void LoadVisualAssets(string assetsDir)
    {
        var baseBmp = LoadPng(System.IO.Path.Combine(assetsDir, "G300sDevice_logitech.png"));
        MouseBaseImage.Source = baseBmp;
        LightingPreviewBaseImage.Source = baseBmp;

        _rawSwooshBitmap = LoadPng(System.IO.Path.Combine(assetsDir, "G300sDevice_swoosh.png"));

        var texBmp = LoadPng(System.IO.Path.Combine(assetsDir, "G300sDevice_swoosh_texture.png"));
        MouseSwooshTextureImage.Source = texBmp;
        LightingPreviewTextureImage.Source = texBmp;

        MouseAmbientGlowImage.Source = LoadPng(System.IO.Path.Combine(assetsDir, "pulseglow_G300s_home_buttons.png"));

        for (int i = 0; i < 9; i++)
        {
            int btnNum = i + 1;
            _pulseGlowBitmaps[i] = LoadPng(System.IO.Path.Combine(assetsDir, $"pulseglow_G300s_btn{btnNum}.png"));
            _highlightBitmaps[i] = LoadPng(System.IO.Path.Combine(assetsDir, $"highlight_G300s_btn{btnNum}.png"));

            var outlineBmp = LoadPng(System.IO.Path.Combine(assetsDir, $"outline_G300s_btn{btnNum}.png"));
            if (outlineBmp is not null)
            {
                _hitMasks[i] = ExtractHitMask(outlineBmp);
            }
        }
    }

    private static BitmapSource? LoadPng(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.UriSource = new Uri(path, UriKind.Absolute);
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch { }
        }

        try
        {
            string fileName = System.IO.Path.GetFileName(path);
            var packUri = new Uri($"pack://application:,,,/Assets/{fileName}", UriKind.Absolute);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.UriSource = packUri;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { }

        return null;
    }

    private static bool[,] ExtractHitMask(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int w = converted.PixelWidth;
        int h = converted.PixelHeight;
        byte[] pixels = new byte[w * h * 4];
        converted.CopyPixels(pixels, w * 4, 0);

        bool[,] mask = new bool[w, h];
        for (int y = 0; y < h; y++)
        {
            int rowOffset = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                byte a = pixels[rowOffset + x * 4 + 3];
                if (a > 80) mask[x, y] = true;
            }
        }
        return mask;
    }

    private void BuildOverlayCallouts()
    {
        OverlayCalloutsCanvas.Children.Clear();

        foreach (var meta in ButtonMetaList)
        {
            int idx = meta.Index;
            double anchorX = meta.CenterX * MouseScale + MouseStageX;
            double anchorY = meta.CenterY * MouseScale + MouseStageY;

            double cardW = 226.0;
            double cardH = 46.0;
            double attachX = meta.IsLeftSide ? meta.CardX + cardW : meta.CardX;
            double attachY = meta.CardY + cardH / 2.0;
            double elbowX = meta.IsLeftSide ? attachX + 24.0 : attachX - 24.0;

            var line = new Polyline
            {
                Points =
                [
                    new Point(anchorX, anchorY),
                    new Point(elbowX, attachY),
                    new Point(attachX, attachY)
                ],
                Stroke = new SolidColorBrush(Color.FromArgb(90, 161, 161, 170)),
                StrokeThickness = 1.6,
                IsHitTestVisible = false
            };
            _leaderLines[idx] = line;
            OverlayCalloutsCanvas.Children.Add(line);

            var dot = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = new SolidColorBrush(Color.FromRgb(212, 212, 216)),
                Stroke = new SolidColorBrush(Color.FromRgb(9, 9, 11)),
                StrokeThickness = 1.8,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(dot, anchorX - 4.0);
            Canvas.SetTop(dot, anchorY - 4.0);
            _anchorDots[idx] = dot;
            OverlayCalloutsCanvas.Children.Add(dot);

            // Modern Frameless Callout Tag with enhanced readability
            var card = new Border
            {
                Width = cardW,
                Height = cardH,
                CornerRadius = new CornerRadius(8),
                Background = Brushes.Transparent,
                Padding = new Thickness(10, 4, 12, 4),
                Cursor = Cursors.Hand,
                Tag = idx
            };
            Canvas.SetLeft(card, meta.CardX);
            Canvas.SetTop(card, meta.CardY);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var accentBar = new Border
            {
                Width = 3.5,
                Height = 24,
                CornerRadius = new CornerRadius(1.75),
                Background = new SolidColorBrush(Color.FromArgb(90, 161, 161, 170)),
                Margin = new Thickness(0, 0, 11, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            _calloutAccentBars[idx] = accentBar;
            Grid.SetColumn(accentBar, 0);
            grid.Children.Add(accentBar);

            var codeLbl = new TextBlock
            {
                Text = meta.CodeLabel,
                FontFamily = (FontFamily)FindResource("FontDisplay"),
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("BrushTextSecondary"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 11, 0)
            };
            Grid.SetColumn(codeLbl, 1);
            grid.Children.Add(codeLbl);

            var actionLbl = new TextBlock
            {
                Text = "Sol Tık",
                FontFamily = (FontFamily)FindResource("FontSans"),
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("BrushTextPrimary"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _calloutActionTexts[idx] = actionLbl;
            Grid.SetColumn(actionLbl, 2);
            grid.Children.Add(actionLbl);

            card.Child = grid;
            card.MouseEnter += (_, _) => SetHoveredButton(idx);
            card.MouseLeave += (_, _) => SetHoveredButton(-1);
            card.MouseLeftButtonDown += (_, ev) =>
            {
                ev.Handled = true;
                SelectButtonForEditing(idx);
            };

            _calloutBorders[idx] = card;
            OverlayCalloutsCanvas.Children.Add(card);
        }
    }

    private void UpdateCalloutHeatmapVisuals()
    {
        if (OverlayCalloutsCanvas is null) return;
        var prof = _profiles[_activeProfileIndex];

        for (int i = 0; i < 9; i++)
        {
            if (_heatmapModeEnabled)
            {
                Color col = _heatmap.GetHeatmapColor(i);
                var brush = new SolidColorBrush(col);
                long cnt = _heatmap.GetClickCount(i);
                double pct = _heatmap.GetNormalizedIntensity(i) * 100.0;

                _calloutAccentBars[i].Background = brush;
                _anchorDots[i].Fill = brush;
                _leaderLines[i].Stroke = brush;
                _calloutActionTexts[i].Text = $"{cnt:N0} Tık ({pct:0}%)";
                _calloutBorders[i].Background = new SolidColorBrush(Color.FromArgb(32, col.R, col.G, col.B));
            }
            else
            {
                string spec = prof.Buttons[i];
                _calloutActionTexts[i].Text = G300sProtocol.GetFriendlyActionTitle(spec);
                _calloutBorders[i].Background = Brushes.Transparent;
            }
        }

        if (!_heatmapModeEnabled)
        {
            UpdateCalloutHighlightVisuals();
        }
    }

    private void DeselectButton()
    {
        _selectedButtonIndex = -1;
        StopKeyRecording();
        SelectedButtonGlowImage.Visibility = Visibility.Collapsed;
        SelectedButtonHighlightImage.Visibility = Visibility.Collapsed;
        InspectorDrawerBorder.Visibility = Visibility.Collapsed;
        UpdateCalloutHighlightVisuals();
    }

    private void CloseInspector_Click(object sender, RoutedEventArgs e) => DeselectButton();

    private void SelectButtonForEditing(int btnIndex)
    {
        _selectedButtonIndex = Math.Clamp(btnIndex, 0, 8);
        StopKeyRecording();

        var meta = ButtonMetaList[_selectedButtonIndex];
        var glowBmp = _pulseGlowBitmaps[_selectedButtonIndex];
        var hiBmp = _highlightBitmaps[_selectedButtonIndex];

        if (glowBmp is not null)
        {
            Canvas.SetLeft(SelectedButtonGlowImage, meta.RelX);
            Canvas.SetTop(SelectedButtonGlowImage, meta.RelY);
            SelectedButtonGlowImage.Width = glowBmp.PixelWidth;
            SelectedButtonGlowImage.Height = glowBmp.PixelHeight;
            SelectedButtonGlowImage.Source = glowBmp;
            SelectedButtonGlowImage.Visibility = Visibility.Visible;
        }

        if (hiBmp is not null)
        {
            Canvas.SetLeft(SelectedButtonHighlightImage, meta.RelX);
            Canvas.SetTop(SelectedButtonHighlightImage, meta.RelY);
            SelectedButtonHighlightImage.Width = hiBmp.PixelWidth;
            SelectedButtonHighlightImage.Height = hiBmp.PixelHeight;
            SelectedButtonHighlightImage.Source = hiBmp;
            SelectedButtonHighlightImage.Visibility = Visibility.Visible;
        }

        InspectorDrawerBorder.Visibility = Visibility.Visible;
        UpdateCalloutHighlightVisuals();
        RefreshSelectedButtonInspectorUi();
    }

    private void SetHoveredButton(int btnIndex)
    {
        if (_hoveredButtonIndex == btnIndex) return;
        _hoveredButtonIndex = btnIndex;

        if (_hoveredButtonIndex >= 0 && _hoveredButtonIndex != _selectedButtonIndex)
        {
            var meta = ButtonMetaList[_hoveredButtonIndex];
            var glowBmp = _pulseGlowBitmaps[_hoveredButtonIndex];
            if (glowBmp is not null)
            {
                Canvas.SetLeft(HoveredButtonGlowImage, meta.RelX);
                Canvas.SetTop(HoveredButtonGlowImage, meta.RelY);
                HoveredButtonGlowImage.Width = glowBmp.PixelWidth;
                HoveredButtonGlowImage.Height = glowBmp.PixelHeight;
                HoveredButtonGlowImage.Source = glowBmp;
                HoveredButtonGlowImage.Opacity = 0.65;
                HoveredButtonGlowImage.Visibility = Visibility.Visible;
            }
        }
        else
        {
            HoveredButtonGlowImage.Visibility = Visibility.Collapsed;
        }

        UpdateCalloutHighlightVisuals();
    }

    private void UpdateCalloutHighlightVisuals()
    {
        if (_heatmapModeEnabled)
        {
            UpdateCalloutHeatmapVisuals();
            return;
        }

        for (int i = 0; i < 9; i++)
        {
            bool isSelected = i == _selectedButtonIndex;
            bool isHovered = i == _hoveredButtonIndex;

            if (isSelected)
            {
                _calloutBorders[i].Background = (Brush)FindResource("BrushBgSurface2");
                _calloutAccentBars[i].Background = (Brush)FindResource("BrushActiveWhite");
                _leaderLines[i].Stroke = (Brush)FindResource("BrushActiveWhite");
                _leaderLines[i].StrokeThickness = 2.4;
                _anchorDots[i].Fill = (Brush)FindResource("BrushActiveWhite");
            }
            else if (isHovered)
            {
                _calloutBorders[i].Background = (Brush)FindResource("BrushBgSurface1");
                _calloutAccentBars[i].Background = (Brush)FindResource("BrushTextSecondary");
                _leaderLines[i].Stroke = new SolidColorBrush(Color.FromArgb(180, 212, 212, 216));
                _leaderLines[i].StrokeThickness = 2.0;
                _anchorDots[i].Fill = (Brush)FindResource("BrushActiveWhite");
            }
            else
            {
                _calloutBorders[i].Background = Brushes.Transparent;
                _calloutAccentBars[i].Background = new SolidColorBrush(Color.FromArgb(90, 161, 161, 170));
                _leaderLines[i].Stroke = new SolidColorBrush(Color.FromArgb(90, 161, 161, 170));
                _leaderLines[i].StrokeThickness = 1.6;
                _anchorDots[i].Fill = new SolidColorBrush(Color.FromRgb(161, 161, 170));
            }
        }
    }

    private void RefreshSelectedButtonInspectorUi()
    {
        if (_selectedButtonIndex < 0) return;
        var meta = ButtonMetaList[_selectedButtonIndex];
        var prof = _profiles[_activeProfileIndex];
        string spec = prof.Buttons[_selectedButtonIndex];

        InspectorBtnNumberText.Text = meta.CodeLabel;
        InspectorBtnTitleText.Text = meta.FullTitle;
        InspectorBtnSubText.Text = $"Profil {_activeProfileIndex + 1} Ataması";
        InspectorCurrentActionText.Text = G300sProtocol.GetFriendlyActionTitle(spec);
    }

    private void MouseStageCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        Point pt = e.GetPosition(MouseStageCanvas);
        double mouseX = (pt.X - MouseStageX) / MouseScale;
        double mouseY = (pt.Y - MouseStageY) / MouseScale;
        int hit = HitTestMouseButton(mouseX, mouseY);
        MouseStageCanvas.Cursor = hit >= 0 ? Cursors.Hand : Cursors.Arrow;
        SetHoveredButton(hit);
    }

    private void MouseStageCanvas_MouseLeave(object sender, MouseEventArgs e)
    {
        MouseStageCanvas.Cursor = Cursors.Arrow;
        SetHoveredButton(-1);
    }

    private void MouseStageCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Point pt = e.GetPosition(MouseStageCanvas);
        double mouseX = (pt.X - MouseStageX) / MouseScale;
        double mouseY = (pt.Y - MouseStageY) / MouseScale;
        int hit = HitTestMouseButton(mouseX, mouseY);
        if (hit >= 0)
        {
            SelectButtonForEditing(hit);
        }
        else
        {
            DeselectButton();
        }
    }

    private int HitTestMouseButton(double mouseX, double mouseY)
    {
        int[] checkOrder = [8, 7, 6, 5, 4, 3, 2, 1, 0];
        foreach (int idx in checkOrder)
        {
            var meta = ButtonMetaList[idx];
            var mask = _hitMasks[idx];
            if (mask is null) continue;

            int lx = (int)Math.Round(mouseX) - meta.RelX;
            int ly = (int)Math.Round(mouseY) - meta.RelY;

            if (lx >= 0 && lx < mask.GetLength(0) && ly >= 0 && ly < mask.GetLength(1))
            {
                if (mask[lx, ly]) return idx;
            }
        }
        return -1;
    }

    private void AssignActionToSelectedButton(string actionSpec)
    {
        if (_selectedButtonIndex < 0) return;
        var prof = _profiles[_activeProfileIndex];
        prof.SetButtonAction(_selectedButtonIndex, actionSpec);
        MarkModified($"{ButtonMetaList[_selectedButtonIndex].CodeLabel} tuşuna '{G300sProtocol.GetFriendlyActionTitle(actionSpec)}' atandı.");
        RefreshCurrentProfileToUi();
    }

    private void QuickActionPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string spec })
        {
            AssignActionToSelectedButton(spec);
        }
    }

    private void HostMacroPresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents) return;
        if (HostMacroPresetCombo.SelectedItem is ComboBoxItem { Tag: string spec })
        {
            AssignActionToSelectedButton(spec);
            HostMacroPresetCombo.SelectedIndex = 0;
        }
    }

    private void RecordKeyBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isRecordingKey)
        {
            StopKeyRecording();
            return;
        }

        _isRecordingKey = true;
        RecordKeyBtnText.Text = "Dinleniyor... Bir tuşa basın (Esc: İptal)";
        RecordKeyBtn.Background = (Brush)FindResource("BrushBgElevated");
        RecordKeyBtn.Focus();
    }

    private void RecordKeyBtn_LostFocus(object sender, RoutedEventArgs e) => StopKeyRecording();

    private void StopKeyRecording()
    {
        if (!_isRecordingKey) return;
        _isRecordingKey = false;
        RecordKeyBtnText.Text = "Tuş Kaydetmek İçin Tıklayın";
        RecordKeyBtn.Background = (Brush)FindResource("BrushBgSurface2");
    }

    private void RecordKeyBtn_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isRecordingKey) return;
        e.Handled = true;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            return;
        }

        if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            StopKeyRecording();
            return;
        }

        string? hidName = G300sProtocol.MapWpfKeyToHidName(key);
        if (hidName is null)
        {
            if (StatusFooterText is not null) StatusFooterText.Text = $"Desteklenmeyen tuş '{key}'. Standart harf/rakam veya F1-F24 deneyin.";
            StopKeyRecording();
            return;
        }

        var parts = new List<string>();
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("ctrl");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("shift");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("alt");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("win");
        parts.Add(hidName);

        string spec = $"key:{string.Join("+", parts)}";
        StopKeyRecording();
        AssignActionToSelectedButton(spec);
    }

    private void PulseCalloutCard(int buttonIndex)
    {
        if (buttonIndex < 0 || buttonIndex >= _calloutBorders.Length) return;
        var border = _calloutBorders[buttonIndex];
        if (border is null || !_microInteractionsEnabled) return;

        var animGlow = new ColorAnimation
        {
            From = Color.FromRgb(0, 240, 255),
            To = Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF),
            Duration = TimeSpan.FromMilliseconds(420),
            DecelerationRatio = 0.8
        };
        var brush = new SolidColorBrush(Color.FromRgb(0, 240, 255));
        border.BorderBrush = brush;
        brush.BeginAnimation(SolidColorBrush.ColorProperty, animGlow);
    }
}
