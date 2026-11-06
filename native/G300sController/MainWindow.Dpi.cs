using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace G300sController;

public partial class MainWindow
{
    private void BuildDpiStagesUi()
    {
        DpiStagesContainer.Children.Clear();

        for (int i = 0; i < 4; i++)
        {
            int stageIdx = i;
            var card = new Border
            {
                Style = (Style)FindResource("StudioCardBorder"),
                Padding = new Thickness(18, 14, 18, 14),
                Margin = new Thickness(i == 0 ? 0 : 6, 0, i == 3 ? 0 : 6, 0)
            };

            var stack = new StackPanel();

            var topGrid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            var actBtn = new Button
            {
                Content = $"Kademe {stageIdx + 1}",
                Style = (Style)FindResource("ToolbarActionButton"),
                Height = 26,
                Padding = new Thickness(11, 0, 11, 0),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = "Bu DPI kademesini şimdi etkinleştir"
            };
            actBtn.Click += (_, _) => ActivateDpiStage(stageIdx);
            _dpiActivateButtons[stageIdx] = actBtn;
            topGrid.Children.Add(actBtn);

            var defBtn = new Button
            {
                Content = "Varsayılan",
                Style = (Style)FindResource("ToolbarActionButton"),
                Height = 26,
                Padding = new Thickness(10, 0, 10, 0),
                FontSize = 11.5,
                HorizontalAlignment = HorizontalAlignment.Right,
                ToolTip = "Başlangıç DPI kademesi olarak ayarla"
            };
            defBtn.Click += (_, _) => SetDefaultDpiStage(stageIdx);
            _dpiDefaultButtons[stageIdx] = defBtn;
            topGrid.Children.Add(defBtn);
            stack.Children.Add(topGrid);

            var valText = new TextBlock
            {
                Text = "1000 DPI",
                FontFamily = (FontFamily)FindResource("FontDisplay"),
                FontSize = 21,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("BrushTextPrimary"),
                Margin = new Thickness(0, 2, 0, 12)
            };
            _dpiValueLabels[stageIdx] = valText;
            stack.Children.Add(valText);

            var ctrlGrid = new Grid();
            ctrlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ctrlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ctrlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var minusBtn = new Button
            {
                Content = "−",
                Style = (Style)FindResource("StepIconButton"),
                Margin = new Thickness(0, 0, 10, 0)
            };
            minusBtn.Click += (_, _) => StepDpiStage(stageIdx, -250);
            Grid.SetColumn(minusBtn, 0);
            ctrlGrid.Children.Add(minusBtn);

            var slider = new Slider
            {
                Minimum = 250,
                Maximum = 2500,
                SmallChange = 250,
                LargeChange = 250,
                TickFrequency = 250,
                IsSnapToTickEnabled = true,
                VerticalAlignment = VerticalAlignment.Center
            };
            slider.ValueChanged += (_, ev) => OnDpiSliderValueChanged(stageIdx, (int)ev.NewValue);
            _dpiSliders[stageIdx] = slider;
            Grid.SetColumn(slider, 1);
            ctrlGrid.Children.Add(slider);

            var plusBtn = new Button
            {
                Content = "+",
                Style = (Style)FindResource("StepIconButton"),
                Margin = new Thickness(10, 0, 0, 0)
            };
            plusBtn.Click += (_, _) => StepDpiStage(stageIdx, 250);
            Grid.SetColumn(plusBtn, 2);
            ctrlGrid.Children.Add(plusBtn);

            stack.Children.Add(ctrlGrid);
            card.Child = stack;
            card.Cursor = Cursors.Hand;
            card.MouseLeftButtonDown += (s, ev) =>
            {
                if (ev.OriginalSource is not Slider && (ev.OriginalSource as FrameworkElement)?.Parent is not Slider)
                {
                    ActivateDpiStage(stageIdx);
                }
            };
            _dpiStageCards[stageIdx] = card;
            DpiStagesContainer.Children.Add(card);
        }
    }

    private void RenderDpiGraph()
    {
        DpiGraphCanvas.Children.Clear();
        double w = DpiGraphCanvas.ActualWidth;
        double h = DpiGraphCanvas.ActualHeight;
        if (w < 100 || h < 80) return;

        double padLeft = 56;
        double padRight = 32;
        double padTop = 24;
        double padBottom = 34;
        double plotW = w - padLeft - padRight;
        double plotH = h - padTop - padBottom;

        int[] gridDpis = [250, 750, 1250, 1750, 2250, 2500];
        foreach (int gd in gridDpis)
        {
            double gy = padTop + plotH * (1.0 - (gd - 250.0) / 2250.0);
            var line = new Line
            {
                X1 = padLeft,
                Y1 = gy,
                X2 = w - padRight,
                Y2 = gy,
                Stroke = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)),
                StrokeThickness = 1
            };
            DpiGraphCanvas.Children.Add(line);

            var lbl = new TextBlock
            {
                Text = $"{gd}",
                FontSize = 11,
                Foreground = (Brush)FindResource("BrushTextMuted")
            };
            Canvas.SetLeft(lbl, 6);
            Canvas.SetTop(lbl, gy - 8);
            DpiGraphCanvas.Children.Add(lbl);
        }

        var prof = _profiles[_activeProfileIndex];
        Point[] pts = new Point[4];

        for (int i = 0; i < 4; i++)
        {
            double x = padLeft + plotW * ((i + 0.5) / 4.0);
            double norm = (prof.DpiStages[i] - 250.0) / 2250.0;
            double y = padTop + plotH * (1.0 - norm);
            pts[i] = new Point(x, y);

            bool isActive = i == _activeDpiStageIndex;
            var bar = new Border
            {
                Width = 40,
                Height = Math.Max(6, (padTop + plotH) - y),
                CornerRadius = new CornerRadius(6, 6, 2, 2),
                Background = new LinearGradientBrush(
                    isActive ? Color.FromArgb(90, 250, 250, 250) : Color.FromArgb(35, 161, 161, 170),
                    Color.FromArgb(4, 161, 161, 170),
                    90.0),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(bar, x - 20);
            Canvas.SetTop(bar, y);
            DpiGraphCanvas.Children.Add(bar);

            var xLbl = new TextBlock
            {
                Text = $"Kademe {i + 1}" + (prof.DefaultDpiStage == i ? " (Varsayılan)" : ""),
                FontSize = 12,
                FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = isActive ? (Brush)FindResource("BrushTextPrimary") : (Brush)FindResource("BrushTextMuted")
            };
            Canvas.SetLeft(xLbl, x - (prof.DefaultDpiStage == i ? 54 : 26));
            Canvas.SetTop(xLbl, padTop + plotH + 10);
            DpiGraphCanvas.Children.Add(xLbl);
        }

        var curve = new Polyline
        {
            Points = new PointCollection(pts),
            Stroke = new SolidColorBrush(Color.FromArgb(110, 244, 244, 246)),
            StrokeThickness = 2,
            IsHitTestVisible = false
        };
        DpiGraphCanvas.Children.Add(curve);

        for (int i = 0; i < 4; i++)
        {
            bool isActive = i == _activeDpiStageIndex;
            var node = new Ellipse
            {
                Width = 14,
                Height = 14,
                Fill = isActive ? (Brush)FindResource("BrushActiveWhite") : (Brush)FindResource("BrushTextSecondary"),
                Stroke = new SolidColorBrush(Color.FromRgb(9, 9, 11)),
                StrokeThickness = 2.5,
                Cursor = Cursors.SizeNS
            };
            Canvas.SetLeft(node, pts[i].X - 7);
            Canvas.SetTop(node, pts[i].Y - 7);
            DpiGraphCanvas.Children.Add(node);

            var topTag = new Border
            {
                Background = (Brush)FindResource("BrushBgSurface2"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 3, 8, 3),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = $"{prof.DpiStages[i]} DPI",
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("BrushTextPrimary")
                }
            };
            Canvas.SetLeft(topTag, pts[i].X - 30);
            Canvas.SetTop(topTag, Math.Max(2, pts[i].Y - 32));
            DpiGraphCanvas.Children.Add(topTag);
        }
    }

    private void DpiGraphCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RenderDpiGraph();

    private void DpiGraphCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Point pt = e.GetPosition(DpiGraphCanvas);
        double w = DpiGraphCanvas.ActualWidth;
        double padLeft = 56, padRight = 32;
        double plotW = w - padLeft - padRight;
        if (plotW <= 0) return;

        int closest = (int)Math.Clamp(Math.Floor((pt.X - padLeft) / (plotW / 4.0)), 0, 3);
        _draggingDpiStageIndex = closest;
        DpiGraphCanvas.CaptureMouse();
        UpdateDraggedDpiFromPoint(pt);
    }

    private void DpiGraphCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingDpiStageIndex < 0) return;
        UpdateDraggedDpiFromPoint(e.GetPosition(DpiGraphCanvas));
    }

    private void DpiGraphCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingDpiStageIndex >= 0)
        {
            _draggingDpiStageIndex = -1;
            DpiGraphCanvas.ReleaseMouseCapture();
        }
    }

    private void UpdateDraggedDpiFromPoint(Point pt)
    {
        double h = DpiGraphCanvas.ActualHeight;
        double padTop = 24, padBottom = 34;
        double plotH = h - padTop - padBottom;
        if (plotH <= 10) return;

        double norm = Math.Clamp(1.0 - (pt.Y - padTop) / plotH, 0.0, 1.0);
        int rawDpi = (int)Math.Round((250.0 + norm * 2250.0) / 250.0) * 250;
        rawDpi = Math.Clamp(rawDpi, 250, 2500);

        var prof = _profiles[_activeProfileIndex];
        if (prof.DpiStages[_draggingDpiStageIndex] != rawDpi)
        {
            prof.SetDpiStage(_draggingDpiStageIndex, rawDpi);
            MarkModified($"Kademe {_draggingDpiStageIndex + 1} hassasiyeti {rawDpi} DPI olarak ayarlandı.");
            RefreshCurrentProfileToUi();
        }
    }

    private void PollingRateBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !int.TryParse(tag, out int hz))
            return;

        var prof = _profiles[_activeProfileIndex];
        prof.PollingRateHz = hz;

        if (_device.IsConnected)
        {
            try { _device.SetLiveRate(hz); } catch { /* ignore */ }
        }

        MarkModified($"Profil {_activeProfileIndex + 1} bildirim hızı {hz} Hz olarak ayarlandı.");
        RefreshCurrentProfileToUi();
    }

    private void OnDpiSliderValueChanged(int stageIdx, int newValue)
    {
        if (_suppressUiEvents) return;
        var prof = _profiles[_activeProfileIndex];
        prof.SetDpiStage(stageIdx, newValue);
        MarkModified($"Kademe {stageIdx + 1} hassasiyeti {prof.DpiStages[stageIdx]} DPI yapıldı.");
        RefreshCurrentProfileToUi();

        if (stageIdx == _activeDpiStageIndex)
        {
            TelemetryDpiText.Text = $"{newValue} DPI";
            ShowToast($"Kademe {stageIdx + 1}: {newValue} DPI (Kalıcı yapmak için Belleğe Kaydet)");
        }
    }

    private void StepDpiStage(int stageIdx, int delta)
    {
        var prof = _profiles[_activeProfileIndex];
        int next = Math.Clamp(prof.DpiStages[stageIdx] + delta, 250, 2500);
        prof.SetDpiStage(stageIdx, next);
        MarkModified($"Kademe {stageIdx + 1} hassasiyeti {next} DPI yapıldı.");
        RefreshCurrentProfileToUi();
    }

    private void ActivateDpiStage(int stageIdx)
    {
        _activeDpiStageIndex = Math.Clamp(stageIdx, 0, 3);
        var prof = _profiles[_activeProfileIndex];
        int activeDpi = prof.DpiStages[_activeDpiStageIndex];

        if (_device.IsConnected)
        {
            try
            {
                _device.SetActiveDpiStage(_activeDpiStageIndex);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetActiveDpiStage: {ex.Message}");
            }
        }

        RefreshCurrentProfileToUi();
        TelemetryDpiText.Text = $"{activeDpi} DPI";
        ShowToast($"Hassasiyet Değiştirildi: Kademe {_activeDpiStageIndex + 1} ({activeDpi} DPI)");
        _osd.ShowNotification(
            $"Kademe {_activeDpiStageIndex + 1} Aktif",
            $"{activeDpi} DPI · {prof.PollingRateHz} Hz",
            prof.ColorHex);

        if (StatusFooterText is not null)
            StatusFooterText.Text = $"Aktif hassasiyet: Kademe {_activeDpiStageIndex + 1} ({activeDpi} DPI)";
    }

    private void SetDefaultDpiStage(int stageIdx)
    {
        var prof = _profiles[_activeProfileIndex];
        prof.DefaultDpiStage = stageIdx;
        MarkModified($"Kademe {stageIdx + 1} ({prof.DpiStages[stageIdx]} DPI) başlangıç varsayılanı yapıldı.");
        RefreshCurrentProfileToUi();
    }

    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    private void ToggleRateTestBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_rateTestCts is not null)
        {
            _rateTestCts.Cancel();
            _rateTestCts.Dispose();
            _rateTestCts = null;
            ToggleRateTestBtn.Content = "Hz Testi Başlat";
            LiveRateReadoutText.Text = "Ölçüm: Durduruldu";
            return;
        }

        _rateTestCts = new CancellationTokenSource();
        CancellationToken token = _rateTestCts.Token;
        ToggleRateTestBtn.Content = "Testi Durdur";
        LiveRateReadoutText.Text = "Fareyi daireler çizerek hareket ettirin...";

        Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            GetCursorPos(out POINT lastPt);
            long lastMoveTick = sw.ElapsedTicks;
            var intervalsMs = new List<double>(128);
            int peakHz = 0;
            long lastUiUpdateMs = 0;
            double tickToMs = 1000.0 / Stopwatch.Frequency;

            while (!token.IsCancellationRequested)
            {
                if (GetCursorPos(out POINT pt) && (pt.X != lastPt.X || pt.Y != lastPt.Y))
                {
                    long nowTick = sw.ElapsedTicks;
                    double dtMs = (nowTick - lastMoveTick) * tickToMs;
                    lastMoveTick = nowTick;
                    lastPt = pt;

                    if (dtMs is >= 0.45 and <= 25.0)
                    {
                        intervalsMs.Add(dtMs);
                        if (intervalsMs.Count > 80) intervalsMs.RemoveAt(0);
                    }
                }

                long nowMs = sw.ElapsedMilliseconds;
                if (nowMs - lastUiUpdateMs >= 160)
                {
                    lastUiUpdateMs = nowMs;
                    if (intervalsMs.Count >= 6)
                    {
                        double avgMs = intervalsMs.Average();
                        int hz = (int)Math.Round(1000.0 / Math.Max(0.5, avgMs));
                        if (hz > peakHz && hz <= 1050) peakHz = hz;

                        double variance = intervalsMs.Average(v => (v - avgMs) * (v - avgMs));
                        double jitterMs = Math.Sqrt(variance);

                        Dispatcher.BeginInvoke(() =>
                        {
                            LiveRateReadoutText.Text = $"{hz} Hz · Tepe: {peakHz} Hz · Jitter: ±{jitterMs:0.00} ms";
                        });
                        intervalsMs.Clear();
                    }
                }

                Thread.SpinWait(400);
            }
        }, token);
    }
}
