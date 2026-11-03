using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using G300sController.Models;
using G300sController.Services;
using Microsoft.Win32;
using Path = System.IO.Path;

namespace G300sController;

public partial class MainWindow : Window
{
    private const double MouseOffsetX = 135.0;
    private const double MouseOffsetY = 12.0;

    private sealed record ButtonVisualMeta(
        int Index,
        string CodeLabel,
        string FullTitle,
        int RelX,
        int RelY,
        double CenterX,
        double CenterY,
        double CardX,
        double CardY,
        bool IsLeftSide);

    private static readonly ButtonVisualMeta[] ButtonMetaList =
    [
        new(0, "G1", "Sol Tık (G1)",             59,  33, 323, 141,  18, 242, true),
        new(1, "G2", "Sağ Tık (G2)",            213,   6, 441,  78, 840, 146, false),
        new(2, "G3", "Orta Tekerlek (G3)",      241,  39, 313,  78,  18,  38, true),
        new(3, "G4", "Sol Arka (G4)",           140, 117, 216, 161,  18, 174, true),
        new(4, "G5", "Sol Ön (G5)",              66,  97, 128, 134,  18, 106, true),
        new(5, "G6", "Sağ Arka (G6)",           425,  14, 498,  53, 840,  82, false),
        new(6, "G7", "Sağ Ön (G7)",             344, -10, 405,  30, 840,  18, false),
        new(7, "G8", "Orta Arka / Mod (G8)",    390,  59, 452, 111, 840, 274, false),
        new(8, "G9", "Orta Ön / DPI (G9)",      353,  59, 406,  97, 840, 210, false)
    ];

    private static readonly (string Name, int R, int G, int B, string Hex)[] PaletteSwatches =
    [
        ("Kapalı",          0, 0, 0, "#27272E"),
        ("Kırmızı",         1, 0, 0, "#FF2D46"),
        ("Kehribar Sarı",   1, 1, 0, "#FFC400"),
        ("Zümrüt Yeşil",    0, 1, 0, "#00E676"),
        ("Turkuaz (Cyan)",  0, 1, 1, "#00D4FF"),
        ("Kobalt Mavi",     0, 0, 1, "#2979FF"),
        ("Macenta",         1, 0, 1, "#E040FB"),
        ("Saf Beyaz",       1, 1, 1, "#F4F8FF")
    ];

    private sealed record LightingModeMeta(
        string Key,
        string Title,
        string Description);

    private static readonly LightingModeMeta[] LightingModes =
    [
        new("none",             "Sabit Donanım Rengi",           "Seçili profil rengini sıfır işlemci yüküyle doğrudan fare belleğinde tutar."),
        new("spectrum",         "Gökkuşağı Döngüsü (Rainbow)",   "6 ana donanım tonu (Kırmızı → Sarı → Yeşil → Turkuaz → Mavi → Eflatun) arasında akıcı döngü."),
        new("color_transition", "Renk Geçişi (İki Renk Arası)",  "Seçilen Ana Renk ile İkincil Geçiş Rengi arasında uyumlu ara tonlar üzerinden gidip gelen geçiş.")
    ];

    private readonly G300sHidDevice _device = new();
    private readonly HostAutomationService _automation;
    private readonly SystemTrayService _tray = new();
    private readonly OsdNotificationService _osd = new();
    private readonly DispatcherTimer _livePreviewTimer;
    private CancellationTokenSource? _rateTestCts;

    private readonly G300sProfile[] _profiles =
    [
        new G300sProfile { Index = 0, LedR = 1, LedG = 0, LedB = 1, PollingRateHz = 1000, DefaultDpiStage = 1 },
        new G300sProfile { Index = 1, LedR = 0, LedG = 1, LedB = 0, PollingRateHz = 1000, DefaultDpiStage = 1 },
        new G300sProfile { Index = 2, LedR = 0, LedG = 1, LedB = 1, PollingRateHz = 1000, DefaultDpiStage = 1 }
    ];

    private BitmapSource? _rawSwooshBitmap;
    private readonly BitmapSource?[] _pulseGlowBitmaps = new BitmapSource?[9];
    private readonly BitmapSource?[] _highlightBitmaps = new BitmapSource?[9];
    private readonly bool[,]?[] _hitMasks = new bool[,]?[9];

    private readonly Border[] _calloutBorders = new Border[9];
    private readonly Border[] _calloutAccentBars = new Border[9];
    private readonly TextBlock[] _calloutActionTexts = new TextBlock[9];
    private readonly Polyline[] _leaderLines = new Polyline[9];
    private readonly Ellipse[] _anchorDots = new Ellipse[9];

    private readonly Border[] _ledSwatchBorders = new Border[8];
    private readonly Border[] _secondarySwatchBorders = new Border[8];
    private readonly Border[] _lightingModeBorders = new Border[3];
    private readonly Ellipse[] _lightingModeDots = new Ellipse[3];

    private readonly Slider[] _dpiSliders = new Slider[4];
    private readonly TextBlock[] _dpiValueLabels = new TextBlock[4];
    private readonly Button[] _dpiActivateButtons = new Button[4];
    private readonly Button[] _dpiDefaultButtons = new Button[4];

    private int _activeViewIndex;
    private int _activeDrawerCategory;
    private int _activeProfileIndex;
    private int _activeDpiStageIndex = 1;
    private int _selectedButtonIndex = -1; // Unselected on startup!
    private int _hoveredButtonIndex = -1;
    private int _draggingDpiStageIndex = -1;
    private (int R, int G, int B) _secondaryBreathColor = (0, 0, 1);
    private (byte R, byte G, byte B) _lastRenderedSwooshColor = (0, 0, 0);

    private bool _isRecordingKey;
    private bool _suppressUiEvents;
    private bool _hasUnsavedChanges;
    private bool _isExiting;

    public MainWindow()
    {
        _automation = new HostAutomationService(_device);
        InitializeComponent();

        _device.HardwareEventReceived += OnHardwareEventReceived;
        _automation.AutoProfileSwitched += OnAutoProfileSwitched;
        _automation.MacroKeyTriggered += OnMacroKeyTriggered;
        _automation.GShiftStateChanged += OnGShiftStateChanged;

        _livePreviewTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        _livePreviewTimer.Tick += LivePreviewTimer_Tick;

        SourceInitialized += MainWindow_SourceInitialized;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        int round = 2;
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public void RestoreWindowFromExternalLaunch()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        string assetsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets");
        LoadVisualAssets(assetsDir);
        BuildOverlayCallouts();
        BuildDpiStagesUi();
        BuildLightingStudioUi();
        BuildDaemonWorkspaceUi();

        string iconPath = Path.Combine(assetsDir, "g300s.ico");
        _tray.Initialize(this, iconPath);
        _tray.RestoreRequested += () => Dispatcher.Invoke(RestoreWindowFromExternalLaunch);
        _tray.ProfileSwitchRequested += idx => Dispatcher.Invoke(() => SelectProfileSlot(idx, sendToHardware: true));
        _tray.ExitRequested += () =>
        {
            _isExiting = true;
            Dispatcher.Invoke(Close);
        };

        _automation.StartHookAndWatcher();
        _osd.IsEnabled = _automation.OsdNotificationsEnabled;
        ConnectAndReadHardware();
        DeselectButton(); // Ensure nothing is selected on startup!
        SwitchMainView(0);
        SwitchDrawerCategory(0);
        _livePreviewTimer.Start();

        string[] args = Environment.GetCommandLineArgs();
        if (Array.Exists(args, a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase)))
        {
            Hide();
            StatusFooterText.Text = "Windows başlangıcında sistem tepsisinde başlatıldı.";
            return;
        }

        // Automated multi-view visual verification screenshot mode
        int ssIdx = Array.IndexOf(args, "--verify-screenshot");
        if (ssIdx >= 0 && ssIdx + 1 < args.Length)
        {
            string outPath = args[ssIdx + 1];
            string dir = Path.GetDirectoryName(outPath) ?? "";
            string baseName = Path.GetFileNameWithoutExtension(outPath);
            string ext = Path.GetExtension(outPath);

            await Task.Delay(500);
            CaptureWindowScreenshot(outPath);

            SwitchMainView(1);
            await Task.Delay(250);
            RenderDpiGraph();
            await Task.Delay(150);
            CaptureWindowScreenshot(Path.Combine(dir, $"{baseName}_sensor{ext}"));

            SwitchMainView(2);
            SelectLightingMode("spectrum");
            await Task.Delay(400);
            CaptureWindowScreenshot(Path.Combine(dir, $"{baseName}_lighting{ext}"));

            SwitchMainView(3);
            await Task.Delay(250);
            CaptureWindowScreenshot(Path.Combine(dir, $"{baseName}_automation{ext}"));

            _isExiting = true;
            Application.Current.Shutdown(0);
        }
    }

    private void LoadVisualAssets(string assetsDir)
    {
        var baseBmp = LoadPng(Path.Combine(assetsDir, "G300sDevice_logitech.png"));
        MouseBaseImage.Source = baseBmp;
        LightingPreviewBaseImage.Source = baseBmp;

        _rawSwooshBitmap = LoadPng(Path.Combine(assetsDir, "G300sDevice_swoosh.png"));

        var texBmp = LoadPng(Path.Combine(assetsDir, "G300sDevice_swoosh_texture.png"));
        MouseSwooshTextureImage.Source = texBmp;
        LightingPreviewTextureImage.Source = texBmp;

        MouseAmbientGlowImage.Source = LoadPng(Path.Combine(assetsDir, "pulseglow_G300s_home_buttons.png"));

        for (int i = 0; i < 9; i++)
        {
            int btnNum = i + 1;
            _pulseGlowBitmaps[i] = LoadPng(Path.Combine(assetsDir, $"pulseglow_G300s_btn{btnNum}.png"));
            _highlightBitmaps[i] = LoadPng(Path.Combine(assetsDir, $"highlight_G300s_btn{btnNum}.png"));

            var outlineBmp = LoadPng(Path.Combine(assetsDir, $"outline_G300s_btn{btnNum}.png"));
            if (outlineBmp is not null)
            {
                _hitMasks[i] = ExtractHitMask(outlineBmp);
            }
        }
    }

    private static BitmapSource? LoadPng(string path)
    {
        if (!File.Exists(path)) return null;
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.UriSource = new Uri(path, UriKind.Absolute);
        bi.EndInit();
        bi.Freeze();
        return bi;
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
        if (_automation.ActiveRgbEffect != "none")
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

    private void BuildOverlayCallouts()
    {
        OverlayCalloutsCanvas.Children.Clear();

        foreach (var meta in ButtonMetaList)
        {
            int idx = meta.Index;
            double anchorX = MouseOffsetX + meta.CenterX;
            double anchorY = MouseOffsetY + meta.CenterY;

            double cardW = 182.0;
            double cardH = 38.0;
            double attachX = meta.IsLeftSide ? meta.CardX + cardW : meta.CardX;
            double attachY = meta.CardY + cardH / 2.0;
            double elbowX = meta.IsLeftSide ? attachX + 22.0 : attachX - 22.0;

            var line = new Polyline
            {
                Points =
                [
                    new Point(anchorX, anchorY),
                    new Point(elbowX, attachY),
                    new Point(attachX, attachY)
                ],
                Stroke = new SolidColorBrush(Color.FromArgb(80, 161, 161, 170)),
                StrokeThickness = 1.2,
                IsHitTestVisible = false
            };
            _leaderLines[idx] = line;
            OverlayCalloutsCanvas.Children.Add(line);

            var dot = new Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = new SolidColorBrush(Color.FromRgb(212, 212, 216)),
                Stroke = new SolidColorBrush(Color.FromRgb(9, 9, 11)),
                StrokeThickness = 1.5,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(dot, anchorX - 3.0);
            Canvas.SetTop(dot, anchorY - 3.0);
            _anchorDots[idx] = dot;
            OverlayCalloutsCanvas.Children.Add(dot);

            // Minimalist Frameless Callout Tag (No boxy AI-slop card!)
            var card = new Border
            {
                Width = cardW,
                Height = cardH,
                CornerRadius = new CornerRadius(7),
                Background = Brushes.Transparent,
                Padding = new Thickness(8, 4, 8, 4),
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
                Width = 2.5,
                Height = 18,
                CornerRadius = new CornerRadius(1.25),
                Background = new SolidColorBrush(Color.FromArgb(90, 161, 161, 170)),
                Margin = new Thickness(0, 0, 9, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            _calloutAccentBars[idx] = accentBar;
            Grid.SetColumn(accentBar, 0);
            grid.Children.Add(accentBar);

            var codeLbl = new TextBlock
            {
                Text = meta.CodeLabel,
                FontFamily = (FontFamily)FindResource("FontDisplay"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 9, 0)
            };
            Grid.SetColumn(codeLbl, 1);
            grid.Children.Add(codeLbl);

            var actionLbl = new TextBlock
            {
                Text = "Sol Tık",
                FontFamily = (FontFamily)FindResource("FontSans"),
                FontSize = 13,
                FontWeight = FontWeights.Medium,
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

        LedSwatchesGrid.Children.Clear();
        SecondaryLedSwatchesGrid.Children.Clear();

        for (int i = 0; i < PaletteSwatches.Length; i++)
        {
            var sw = PaletteSwatches[i];
            int idx = i;

            var primaryOrb = CreateColorOrb(sw.Hex, $"{sw.Name} (Ana Renk)", () => SelectLedColor(sw.R, sw.G, sw.B));
            _ledSwatchBorders[idx] = primaryOrb;
            LedSwatchesGrid.Children.Add(primaryOrb);

            var secOrb = CreateColorOrb(sw.Hex, $"{sw.Name} (İkincil Renk)", () => SelectSecondaryLedColor(sw.R, sw.G, sw.B));
            _secondarySwatchBorders[idx] = secOrb;
            SecondaryLedSwatchesGrid.Children.Add(secOrb);
        }

        UpdateLightingModeSelectionVisuals("none");
        UpdateSecondarySwatchVisuals();
    }

    private static Border CreateColorOrb(string hex, string tooltip, Action onClick)
    {
        var outer = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            Padding = new Thickness(2),
            Cursor = Cursors.Hand,
            ToolTip = tooltip
        };

        var inner = new Border
        {
            CornerRadius = new CornerRadius(11),
            Background = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!,
            BorderBrush = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)),
            BorderThickness = new Thickness(1)
        };

        outer.Child = inner;
        outer.MouseLeftButtonDown += (_, _) => onClick();
        return outer;
    }

    private void BuildDaemonWorkspaceUi()
    {
        MacroItemsPanel.Children.Clear();

        string[] keys = ["f13", "f14", "f15", "f16", "f17", "f18"];
        for (int i = 0; i < keys.Length; i++)
        {
            string k = keys[i];
            if (!_automation.Macros.TryGetValue(k, out var item))
            {
                item = new MacroBindingItem { TriggerKey = k };
                _automation.Macros[k] = item;
            }

            var card = new Border
            {
                Background = (Brush)FindResource("BrushBgSurface2"),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 0, 0, 8)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(186) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var badge = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            badge.Children.Add(new TextBlock
            {
                Text = $"Makro {i + 1}",
                FontFamily = (FontFamily)FindResource("FontDisplay"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("BrushTextPrimary")
            });
            badge.Children.Add(new TextBlock
            {
                Text = k.ToUpperInvariant(),
                FontSize = 11.5,
                Foreground = (Brush)FindResource("BrushTextMuted")
            });
            Grid.SetColumn(badge, 0);
            grid.Children.Add(badge);

            var modeCombo = new ComboBox { Height = 32, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            modeCombo.Items.Add(new ComboBoxItem { Content = "Metin Dizisi", Tag = "text" });
            modeCombo.Items.Add(new ComboBoxItem { Content = "Seri Tıklama (CPS)", Tag = "rapid_fire" });
            modeCombo.Items.Add(new ComboBoxItem { Content = "Klavye Kısayolu", Tag = "shortcut" });
            modeCombo.Items.Add(new ComboBoxItem { Content = "Zamanlamalı Kayıt", Tag = "timed" });
            modeCombo.Items.Add(new ComboBoxItem { Content = "Geri Tepme (Recoil)", Tag = "recoil" });
            modeCombo.Items.Add(new ComboBoxItem { Content = "G-Shift Katmanı", Tag = "gshift" });
            modeCombo.SelectedIndex = item.Mode switch
            {
                "rapid_fire" => 1,
                "shortcut" => 2,
                "timed" => 3,
                "recoil" => 4,
                "gshift" => 5,
                _ => 0
            };
            Grid.SetColumn(modeCombo, 1);
            grid.Children.Add(modeCombo);

            var paramBox = new TextBox
            {
                Height = 32,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(8, 0, 8, 0),
                FontSize = 12,
                Text = FormatMacroParamText(item)
            };
            Grid.SetColumn(paramBox, 2);
            grid.Children.Add(paramBox);

            var recBtn = new Button
            {
                Content = "● Kayıt",
                Style = (Style)FindResource("ToolbarActionButton"),
                Height = 32,
                Padding = new Thickness(10, 0, 10, 0),
                Margin = new Thickness(6, 0, 0, 0),
                FontSize = 11.5,
                Visibility = item.Mode == "timed" ? Visibility.Visible : Visibility.Collapsed,
                ToolTip = "Tıklayıp klavye tuşlarına basın; basış ve bırakış süreleri milisaniye hassasiyetinde kaydedilir."
            };
            Grid.SetColumn(recBtn, 3);
            grid.Children.Add(recBtn);

            bool isRecTimed = false;
            Stopwatch? recSw = null;

            recBtn.Click += (_, _) =>
            {
                if (!isRecTimed)
                {
                    isRecTimed = true;
                    item.TimedSteps.Clear();
                    recSw = Stopwatch.StartNew();
                    recBtn.Content = "■ Durdur";
                    paramBox.Text = "Tuşlara basın (Bitirmek için Durdur)...";
                    recBtn.Focus();
                }
                else
                {
                    isRecTimed = false;
                    recSw?.Stop();
                    recBtn.Content = "● Kayıt";
                    paramBox.Text = FormatMacroParamText(item);
                }
            };

            recBtn.PreviewKeyDown += (_, ke) =>
            {
                if (!isRecTimed || recSw is null) return;
                ke.Handled = true;
                Key realKey = ke.Key == Key.System ? ke.SystemKey : ke.Key;
                if (realKey == Key.Escape)
                {
                    isRecTimed = false;
                    recSw.Stop();
                    recBtn.Content = "● Kayıt";
                    paramBox.Text = FormatMacroParamText(item);
                    return;
                }
                if (ke.IsRepeat || item.TimedSteps.Count >= 24) return;
                ushort vk = (ushort)KeyInterop.VirtualKeyFromKey(realKey);
                int delay = item.TimedSteps.Count == 0 ? 0 : (int)Math.Clamp(recSw.ElapsedMilliseconds, 5, 5000);
                recSw.Restart();
                item.TimedSteps.Add(new TimedMacroStep
                {
                    VkCode = vk,
                    KeyLabel = realKey.ToString().ToUpperInvariant(),
                    IsKeyUp = false,
                    DelayMs = delay
                });
                paramBox.Text = FormatMacroParamText(item);
            };

            recBtn.PreviewKeyUp += (_, ke) =>
            {
                if (!isRecTimed || recSw is null) return;
                ke.Handled = true;
                Key realKey = ke.Key == Key.System ? ke.SystemKey : ke.Key;
                if (item.TimedSteps.Count >= 24) return;
                ushort vk = (ushort)KeyInterop.VirtualKeyFromKey(realKey);
                int delay = (int)Math.Clamp(recSw.ElapsedMilliseconds, 5, 5000);
                recSw.Restart();
                item.TimedSteps.Add(new TimedMacroStep
                {
                    VkCode = vk,
                    KeyLabel = realKey.ToString().ToUpperInvariant(),
                    IsKeyUp = true,
                    DelayMs = delay
                });
                paramBox.Text = FormatMacroParamText(item);
            };

            modeCombo.SelectionChanged += (_, _) =>
            {
                if (modeCombo.SelectedItem is ComboBoxItem { Tag: string m })
                {
                    item.Mode = m;
                    recBtn.Visibility = m == "timed" ? Visibility.Visible : Visibility.Collapsed;
                    paramBox.Text = FormatMacroParamText(item);
                }
            };

            paramBox.TextChanged += (_, _) =>
            {
                if (!isRecTimed)
                {
                    ParseMacroParamText(item, paramBox.Text);
                }
            };

            card.Child = grid;
            MacroItemsPanel.Children.Add(card);
        }

        _suppressUiEvents = true;
        AutoSwitchToggle.IsChecked = _automation.AutoProfileSwitchEnabled;
        OsdToggle.IsChecked = _automation.OsdNotificationsEnabled;
        StartupToggle.IsChecked = HostAutomationService.GetRunAtStartup();
        DefaultSlotCombo.SelectedIndex = Math.Clamp(_automation.DefaultProfileIndex, 0, 2);
        _suppressUiEvents = false;

        RefreshAppRulesListUi();
    }

    private static string FormatMacroParamText(MacroBindingItem item) => item.Mode switch
    {
        "rapid_fire" => $"{item.Cps} CPS ({(string.Equals(item.MouseButton, "right", StringComparison.OrdinalIgnoreCase) ? "Sağ Tık" : "Sol Tık")})",
        "recoil" => $"{item.Cps} CPS · Dikey: +{item.RecoilVerticalPx}px, Yatay: {item.RecoilHorizontalPx}px",
        "gshift" => $"Basılı Tut → Profil {item.GShiftProfileIndex + 1}",
        "timed" => item.TimedSteps.Count == 0
            ? "Adım yok ('● Kayıt' ile kaydedin)"
            : string.Join(" → ", item.TimedSteps.Take(6).Select(s => $"{s.KeyLabel}{(s.IsKeyUp ? "↑" : "↓")}({s.DelayMs}ms)")),
        "shortcut" => item.ShortcutKeys,
        _ => item.TextPayload
    };

    private static void ParseMacroParamText(MacroBindingItem item, string raw)
    {
        if (item.Mode == "rapid_fire")
        {
            string digits = new(raw.Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, out int cps))
            {
                item.Cps = Math.Clamp(cps, 1, 50);
            }
            bool isRight = raw.Contains("right", StringComparison.OrdinalIgnoreCase)
                || raw.Contains("sağ", StringComparison.OrdinalIgnoreCase)
                || raw.Contains("sag", StringComparison.OrdinalIgnoreCase);
            item.MouseButton = isRight ? "right" : "left";
        }
        else if (item.Mode == "recoil")
        {
            var nums = System.Text.RegularExpressions.Regex.Matches(raw, @"-?\d+")
                .Select(m => int.TryParse(m.Value, out int v) ? v : 0)
                .ToArray();
            if (nums.Length >= 1) item.Cps = Math.Clamp(Math.Abs(nums[0]), 1, 50);
            if (nums.Length >= 2) item.RecoilVerticalPx = Math.Clamp(nums[1], -30, 40);
            if (nums.Length >= 3) item.RecoilHorizontalPx = Math.Clamp(nums[2], -30, 30);
        }
        else if (item.Mode == "gshift")
        {
            string digits = new(raw.Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, out int pNum))
            {
                item.GShiftProfileIndex = Math.Clamp(pNum - 1, 0, 2);
            }
        }
        else if (item.Mode == "shortcut")
        {
            item.ShortcutKeys = raw.Trim();
        }
        else if (item.Mode == "text")
        {
            item.TextPayload = raw;
        }
    }

    private void RefreshAppRulesListUi()
    {
        AppRulesPanel.Children.Clear();

        for (int i = 0; i < _automation.AppRules.Count; i++)
        {
            int ruleIdx = i;
            var rule = _automation.AppRules[i];

            var row = new Border
            {
                Background = (Brush)FindResource("BrushBgSurface2"),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 0, 0, 6)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var exeText = new TextBlock
            {
                Text = rule.ExecutableName,
                FontSize = 13,
                FontWeight = FontWeights.Medium,
                Foreground = (Brush)FindResource("BrushTextPrimary"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(exeText, 0);
            grid.Children.Add(exeText);

            var slotBadge = new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = (Brush)FindResource("BrushBgElevated"),
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(8, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = $"Profil {rule.ProfileIndex + 1}",
                    FontSize = 11.5,
                    FontWeight = FontWeights.Medium,
                    Foreground = (Brush)FindResource("BrushTextSecondary")
                }
            };
            Grid.SetColumn(slotBadge, 1);
            grid.Children.Add(slotBadge);

            var delBtn = new Button
            {
                Content = "Kaldır",
                Style = (Style)FindResource("ToolbarActionButton"),
                Height = 26,
                Padding = new Thickness(10, 0, 10, 0),
                FontSize = 11.5
            };
            delBtn.Click += (_, _) =>
            {
                _automation.AppRules.RemoveAt(ruleIdx);
                _automation.SaveConfig();
                RefreshAppRulesListUi();
            };
            Grid.SetColumn(delBtn, 2);
            grid.Children.Add(delBtn);

            row.Child = grid;
            AppRulesPanel.Children.Add(row);
        }
    }

    private void ConnectAndReadHardware()
    {
        try
        {
            bool connected = _device.Connect();
            LgsWarningBadge.Visibility = G300sHidDevice.IsLgsRunning() ? Visibility.Visible : Visibility.Collapsed;

            if (connected)
            {
                var state = _device.GetActiveState();
                _activeProfileIndex = state.ProfileIndex;
                _activeDpiStageIndex = state.DpiStageIndex;

                for (int i = 0; i < 3; i++)
                {
                    _profiles[i] = _device.GetProfile(i);
                }

                SetConnectionStatusUi(true, "Bağlı");
                StatusFooterText.Text = $"Logitech G300s bağlı · Aktif Profil {_activeProfileIndex + 1}, Kademe {_activeDpiStageIndex + 1}";
            }
            else
            {
                SetConnectionStatusUi(false, "Çevrimdışı");
                StatusFooterText.Text = "Logitech G300s algılanmadı · Çevrimdışı düzenleme modu";
            }
        }
        catch (Exception ex)
        {
            SetConnectionStatusUi(false, "Bağlantı Hatası");
            StatusFooterText.Text = $"Donanım okuma hatası: {ex.Message}";
        }

        _hasUnsavedChanges = false;
        UpdateDirtyStateVisuals();
        SelectProfileSlot(_activeProfileIndex, sendToHardware: false);
    }

    private void SetConnectionStatusUi(bool online, string label)
    {
        ConnectionStatusText.Text = label;
        ConnectionDot.Fill = online ? (Brush)FindResource("BrushSuccess") : (Brush)FindResource("BrushWarning");
    }

    private void SelectProfileSlot(int slotIndex, bool sendToHardware)
    {
        _activeProfileIndex = Math.Clamp(slotIndex, 0, 2);
        var prof = _profiles[_activeProfileIndex];

        if (sendToHardware && _device.IsConnected)
        {
            try
            {
                _device.SetActiveProfile(_activeProfileIndex);
                if (_automation.ActiveRgbEffect == "none")
                {
                    _device.SetLiveLed(prof.LedR, prof.LedG, prof.LedB);
                }
                _device.SetLiveRate(prof.PollingRateHz);
                StatusFooterText.Text = $"Profil {_activeProfileIndex + 1} ({prof.ColorName}) etkinleştirildi.";
            }
            catch (Exception ex)
            {
                StatusFooterText.Text = $"Profil geçiş hatası: {ex.Message}";
            }
        }

        _automation.UpdateRgbParameters((int)BreathSpeedSlider.Value, (prof.LedR, prof.LedG, prof.LedB), _secondaryBreathColor);
        RefreshSlotHeaderVisuals();
        RefreshCurrentProfileToUi();
    }

    private void RefreshSlotHeaderVisuals()
    {
        Button[] slotBtns = [SlotBtn0, SlotBtn1, SlotBtn2];
        Ellipse[] slotDots = [SlotDot0, SlotDot1, SlotDot2];

        for (int i = 0; i < 3; i++)
        {
            var p = _profiles[i];
            slotDots[i].Fill = (SolidColorBrush)new BrushConverter().ConvertFromString(p.ColorHex)!;

            if (i == _activeProfileIndex)
            {
                slotBtns[i].Background = (Brush)FindResource("BrushBgElevated");
                slotBtns[i].Foreground = (Brush)FindResource("BrushTextPrimary");
            }
            else
            {
                slotBtns[i].Background = Brushes.Transparent;
                slotBtns[i].Foreground = (Brush)FindResource("BrushTextMuted");
            }
        }
    }

    private void RefreshCurrentProfileToUi()
    {
        _suppressUiEvents = true;
        var prof = _profiles[_activeProfileIndex];

        StageActiveSlotLabel.Text = $"Profil {_activeProfileIndex + 1} · {prof.ColorName}";
        ActiveColorLabel.Text = prof.ColorName;

        if (_automation.ActiveRgbEffect == "none")
        {
            var col = (Color)ColorConverter.ConvertFromString(prof.ColorHex);
            UpdateTintedSwoosh24Bit(col.R, col.G, col.B);
            LiveColorPreviewOrb.Background = new SolidColorBrush(col);
            LiveTelemetryProgressFill.Background = new SolidColorBrush(col);
            LiveTelemetryProgressFill.Width = 380;
            LiveTelemetryTitleText.Text = $"Sabit Donanım Rengi ({prof.ColorName})";
        }

        for (int i = 0; i < 9; i++)
        {
            string spec = prof.Buttons[i];
            _calloutActionTexts[i].Text = G300sProtocol.GetFriendlyActionTitle(spec);
        }

        Button[] rateBtns = [RateBtn125, RateBtn250, RateBtn500, RateBtn1000];
        int[] rates = [125, 250, 500, 1000];
        for (int i = 0; i < 4; i++)
        {
            bool active = prof.PollingRateHz == rates[i];
            rateBtns[i].Background = active ? (Brush)FindResource("BrushBgElevated") : Brushes.Transparent;
            rateBtns[i].Foreground = active ? (Brush)FindResource("BrushTextPrimary") : (Brush)FindResource("BrushTextMuted");
        }

        for (int i = 0; i < 4; i++)
        {
            int dpiVal = prof.DpiStages[i];
            _dpiSliders[i].Value = dpiVal;
            _dpiValueLabels[i].Text = $"{dpiVal} DPI";

            bool isCurrentStage = i == _activeDpiStageIndex;
            _dpiActivateButtons[i].Background = isCurrentStage ? (Brush)FindResource("BrushActiveWhite") : (Brush)FindResource("BrushBgSurface2");
            _dpiActivateButtons[i].Foreground = isCurrentStage ? new SolidColorBrush(Color.FromRgb(9, 9, 11)) : (Brush)FindResource("BrushTextSecondary");

            bool isDefault = i == prof.DefaultDpiStage;
            _dpiDefaultButtons[i].Background = isDefault ? (Brush)FindResource("BrushBgElevated") : Brushes.Transparent;
            _dpiDefaultButtons[i].Foreground = isDefault ? (Brush)FindResource("BrushTextPrimary") : (Brush)FindResource("BrushTextMuted");
        }

        for (int i = 0; i < PaletteSwatches.Length; i++)
        {
            var sw = PaletteSwatches[i];
            bool selected = prof.LedR == sw.R && prof.LedG == sw.G && prof.LedB == sw.B;
            _ledSwatchBorders[i].BorderBrush = selected ? (Brush)FindResource("BrushActiveWhite") : Brushes.Transparent;
        }

        int activeDpi = prof.DpiStages[Math.Clamp(_activeDpiStageIndex, 0, 3)];
        TelemetryDpiText.Text = $"{activeDpi} DPI";
        TelemetryRateText.Text = $"{prof.PollingRateHz} Hz";

        if (_selectedButtonIndex >= 0)
        {
            RefreshSelectedButtonInspectorUi();
        }
        if (_activeViewIndex == 1)
        {
            RenderDpiGraph();
        }
        _suppressUiEvents = false;
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
            Canvas.SetLeft(SelectedButtonGlowImage, MouseOffsetX + meta.RelX);
            Canvas.SetTop(SelectedButtonGlowImage, MouseOffsetY + meta.RelY);
            SelectedButtonGlowImage.Width = glowBmp.PixelWidth;
            SelectedButtonGlowImage.Height = glowBmp.PixelHeight;
            SelectedButtonGlowImage.Source = glowBmp;
            SelectedButtonGlowImage.Visibility = Visibility.Visible;
        }

        if (hiBmp is not null)
        {
            Canvas.SetLeft(SelectedButtonHighlightImage, MouseOffsetX + meta.RelX);
            Canvas.SetTop(SelectedButtonHighlightImage, MouseOffsetY + meta.RelY);
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
                Canvas.SetLeft(HoveredButtonGlowImage, MouseOffsetX + meta.RelX);
                Canvas.SetTop(HoveredButtonGlowImage, MouseOffsetY + meta.RelY);
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
        for (int i = 0; i < 9; i++)
        {
            bool isSelected = i == _selectedButtonIndex;
            bool isHovered = i == _hoveredButtonIndex;

            if (isSelected)
            {
                _calloutBorders[i].Background = (Brush)FindResource("BrushBgSurface2");
                _calloutAccentBars[i].Background = (Brush)FindResource("BrushActiveWhite");
                _leaderLines[i].Stroke = (Brush)FindResource("BrushActiveWhite");
                _leaderLines[i].StrokeThickness = 1.8;
                _anchorDots[i].Fill = (Brush)FindResource("BrushActiveWhite");
            }
            else if (isHovered)
            {
                _calloutBorders[i].Background = (Brush)FindResource("BrushBgSurface1");
                _calloutAccentBars[i].Background = (Brush)FindResource("BrushTextSecondary");
                _leaderLines[i].Stroke = new SolidColorBrush(Color.FromArgb(180, 212, 212, 216));
                _leaderLines[i].StrokeThickness = 1.5;
                _anchorDots[i].Fill = (Brush)FindResource("BrushActiveWhite");
            }
            else
            {
                _calloutBorders[i].Background = Brushes.Transparent;
                _calloutAccentBars[i].Background = new SolidColorBrush(Color.FromArgb(90, 161, 161, 170));
                _leaderLines[i].Stroke = new SolidColorBrush(Color.FromArgb(75, 161, 161, 170));
                _leaderLines[i].StrokeThickness = 1.2;
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
        int hit = HitTestMouseButton(pt.X - MouseOffsetX, pt.Y - MouseOffsetY);
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
        int hit = HitTestMouseButton(pt.X - MouseOffsetX, pt.Y - MouseOffsetY);
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

    private void NavRail_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string t } && int.TryParse(t, out int viewIdx))
        {
            SwitchMainView(viewIdx);
        }
    }

    private void SwitchMainView(int viewIdx)
    {
        _activeViewIndex = Math.Clamp(viewIdx, 0, 3);
        ViewAssignmentsGrid.Visibility = _activeViewIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        ViewSensitivityGrid.Visibility = _activeViewIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        ViewLightingGrid.Visibility = _activeViewIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        ViewAutomationGrid.Visibility = _activeViewIndex == 3 ? Visibility.Visible : Visibility.Collapsed;

        Button[] navBtns = [NavBtnAssignments, NavBtnSensitivity, NavBtnLighting, NavBtnAutomation];
        for (int i = 0; i < navBtns.Length; i++)
        {
            bool active = i == _activeViewIndex;
            navBtns[i].Background = active ? (Brush)FindResource("BrushBgSurface2") : Brushes.Transparent;
            navBtns[i].Foreground = active ? (Brush)FindResource("BrushTextPrimary") : (Brush)FindResource("BrushTextMuted");
        }

        if (_activeViewIndex == 1)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, RenderDpiGraph);
        }
    }

    private void DrawerCategoryTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string t } && int.TryParse(t, out int catIdx))
        {
            SwitchDrawerCategory(catIdx);
        }
    }

    private void SwitchDrawerCategory(int catIdx)
    {
        _activeDrawerCategory = Math.Clamp(catIdx, 0, 2);
        DrawerPanelMouse.Visibility = _activeDrawerCategory == 0 ? Visibility.Visible : Visibility.Collapsed;
        DrawerPanelDpi.Visibility = _activeDrawerCategory == 1 ? Visibility.Visible : Visibility.Collapsed;
        DrawerPanelKey.Visibility = _activeDrawerCategory == 2 ? Visibility.Visible : Visibility.Collapsed;

        Button[] tabs = [DrawerTabMouseBtn, DrawerTabDpiBtn, DrawerTabKeyBtn];
        for (int i = 0; i < tabs.Length; i++)
        {
            bool active = i == _activeDrawerCategory;
            tabs[i].Background = active ? (Brush)FindResource("BrushBgElevated") : Brushes.Transparent;
            tabs[i].Foreground = active ? (Brush)FindResource("BrushTextPrimary") : (Brush)FindResource("BrushTextMuted");
        }
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
            StatusFooterText.Text = $"Desteklenmeyen tuş '{key}'. Standart harf/rakam veya F1-F24 deneyin.";
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
        if (_device.IsConnected)
        {
            try
            {
                _device.SetActiveDpiStage(_activeDpiStageIndex);
                StatusFooterText.Text = $"Donanım hassasiyeti Kademe {_activeDpiStageIndex + 1} ({_profiles[_activeProfileIndex].DpiStages[_activeDpiStageIndex]} DPI) yapıldı.";
            }
            catch (Exception ex)
            {
                StatusFooterText.Text = $"DPI değiştirilemedi: {ex.Message}";
            }
        }
        RefreshCurrentProfileToUi();
    }

    private void SetDefaultDpiStage(int stageIdx)
    {
        var prof = _profiles[_activeProfileIndex];
        prof.DefaultDpiStage = stageIdx;
        MarkModified($"Kademe {stageIdx + 1} ({prof.DpiStages[stageIdx]} DPI) başlangıç varsayılanı yapıldı.");
        RefreshCurrentProfileToUi();
    }

    private void SelectLedColor(int r, int g, int b)
    {
        var prof = _profiles[_activeProfileIndex];
        prof.SetLed(r, g, b);

        _automation.UpdateRgbParameters((int)BreathSpeedSlider.Value, (r, g, b), _secondaryBreathColor);

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
        _automation.UpdateRgbParameters((int)BreathSpeedSlider.Value, (prof.LedR, prof.LedG, prof.LedB), _secondaryBreathColor);
        UpdateSecondarySwatchVisuals();
        StatusFooterText.Text = "İkincil geçiş rengi güncellendi.";
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
        int speedMs = (int)BreathSpeedSlider.Value;
        _automation.SetRgbEffect(effectKey, speedMs, (prof.LedR, prof.LedG, prof.LedB), _secondaryBreathColor);

        if (effectKey == "none" && _device.IsConnected)
        {
            try { _device.SetLiveLed(prof.LedR, prof.LedG, prof.LedB); } catch { /* ignore */ }
        }

        UpdateLightingModeSelectionVisuals(effectKey);
        RefreshCurrentProfileToUi();
        StatusFooterText.Text = "Aydınlatma modu güncellendi.";
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

    private void BreathSpeedSlider_ValueChanged(object sender, RoutedEventArgs e)
    {
        if (BreathSpeedValueText is null) return;
        double ms = BreathSpeedSlider.Value;
        BreathSpeedValueText.Text = $"{ms / 1000.0:0.0} sn";

        if (_suppressUiEvents || _automation is null) return;
        var prof = _profiles[_activeProfileIndex];
        _automation.UpdateRgbParameters((int)ms, (prof.LedR, prof.LedG, prof.LedB), _secondaryBreathColor);
    }

    private void MarkModified(string message)
    {
        _hasUnsavedChanges = true;
        StatusFooterText.Text = $"{message} · Kalıcı olması için 'Belleğe Kaydet' butonuna tıklayın.";
        UpdateDirtyStateVisuals();
    }

    private void UpdateDirtyStateVisuals()
    {
        FlashSaveBtnText.Text = _hasUnsavedChanges ? "Belleğe Kaydet ●" : "Belleğe Kaydet";
    }

    private async void SaveToHardware_Click(object sender, RoutedEventArgs e)
    {
        if (!_device.IsConnected)
        {
            MessageBox.Show(
                this,
                "Logitech G300s USB üzerinden bağlı değil.",
                "Donanım Çevrimdışı",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        FlashSaveBtn.IsEnabled = false;
        FlashSaveBtnText.Text = "Kaydediliyor...";
        StatusFooterText.Text = "Profiller yerleşik EEPROM belleğe yazılıyor...";

        try
        {
            await Task.Run(() =>
            {
                for (int i = 0; i < 3; i++)
                {
                    _device.SaveProfile(_profiles[i], verify: true);
                }
                _device.SetActiveProfile(_activeProfileIndex);
                var p = _profiles[_activeProfileIndex];
                if (_automation.ActiveRgbEffect == "none")
                {
                    _device.SetLiveLed(p.LedR, p.LedG, p.LedB);
                }
                _device.SetLiveRate(p.PollingRateHz);
            });

            _hasUnsavedChanges = false;
            UpdateDirtyStateVisuals();
            StatusFooterText.Text = "Tüm 3 profil Logitech G300s belleğine kaydedildi.";
        }
        catch (Exception ex)
        {
            StatusFooterText.Text = $"Belleğe yazma hatası: {ex.Message}";
            MessageBox.Show(this, $"Donanım belleğine yazılamadı:\n{ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            FlashSaveBtn.IsEnabled = true;
            UpdateDirtyStateVisuals();
        }
    }

    private void ReloadFromMouse_Click(object sender, RoutedEventArgs e) => ConnectAndReadHardware();

    private void ResetProfileDefaults_Click(object sender, RoutedEventArgs e)
    {
        var prof = _profiles[_activeProfileIndex];
        prof.PollingRateHz = 1000;
        prof.DefaultDpiStage = 1;
        prof.SetDpiStage(0, 500);
        prof.SetDpiStage(1, 1000);
        prof.SetDpiStage(2, 1500);
        prof.SetDpiStage(3, 2500);

        if (_activeProfileIndex == 0) prof.SetLed(1, 0, 1);
        else if (_activeProfileIndex == 1) prof.SetLed(0, 1, 0);
        else prof.SetLed(0, 1, 1);

        prof.SetButtonAction(0, "mouse:1");
        prof.SetButtonAction(1, "mouse:2");
        prof.SetButtonAction(2, "mouse:3");
        prof.SetButtonAction(3, "mouse:4");
        prof.SetButtonAction(4, "mouse:5");
        prof.SetButtonAction(5, "dpi-down");
        prof.SetButtonAction(6, "dpi-up");
        prof.SetButtonAction(7, "profile-cycle");
        prof.SetButtonAction(8, "dpi-cycle");

        MarkModified($"Profil {_activeProfileIndex + 1} fabrika varsayılanlarına sıfırlandı.");
        RefreshSlotHeaderVisuals();
        RefreshCurrentProfileToUi();
    }

    private void SlotButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string t } && int.TryParse(t, out int idx))
        {
            SelectProfileSlot(idx, sendToHardware: true);
        }
    }

    private void ImportJson_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Logitech G300s Profil Yedeğini İçe Aktar",
            Filter = "JSON Profil Yedeği (*.json)|*.json|Tüm Dosyalar (*.*)|*.*"
        };

        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(dlg.FileName)) as JsonObject;
            if (root?["profiles"] is JsonArray arr)
            {
                for (int i = 0; i < Math.Min(3, arr.Count); i++)
                {
                    if (arr[i] is not JsonObject pObj) continue;
                    string? rawHex = pObj["raw_hex"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(rawHex) && rawHex.Length == 70)
                    {
                        byte[] raw = Convert.FromHexString(rawHex);
                        _profiles[i] = G300sProfile.FromRawReport(raw, i);
                    }
                }
                MarkModified($"Profiller '{Path.GetFileName(dlg.FileName)}' dosyasından yüklendi.");
                RefreshSlotHeaderVisuals();
                RefreshCurrentProfileToUi();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"JSON içe aktarma hatası:\n{ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportJson_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Logitech G300s Profil Yedeğini Dışa Aktar",
            FileName = "g300s_profiles_backup.json",
            Filter = "JSON Profil Yedeği (*.json)|*.json"
        };

        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var arr = new JsonArray();
            for (int i = 0; i < 3; i++)
            {
                var p = _profiles[i];
                var dpiArr = new JsonArray();
                for (int d = 0; d < 4; d++)
                {
                    dpiArr.Add(new JsonObject
                    {
                        ["dpi"] = p.DpiStages[d],
                        ["is_default"] = d == p.DefaultDpiStage
                    });
                }

                var btnArr = new JsonArray();
                for (int b = 0; b < 9; b++)
                {
                    btnArr.Add(p.Buttons[b]);
                }

                arr.Add(new JsonObject
                {
                    ["index"] = i,
                    ["report_id"] = $"0x{0xF3 + i:X2}",
                    ["led"] = new JsonObject
                    {
                        ["r"] = p.LedR,
                        ["g"] = p.LedG,
                        ["b"] = p.LedB,
                        ["name"] = p.ColorName
                    },
                    ["report_rate_hz"] = p.PollingRateHz,
                    ["dpi_levels"] = dpiArr,
                    ["buttons"] = btnArr,
                    ["raw_hex"] = Convert.ToHexString(p.ToRawReport()).ToLowerInvariant()
                });
            }

            var root = new JsonObject
            {
                ["active_profile"] = _activeProfileIndex,
                ["active_dpi_level"] = _activeDpiStageIndex,
                ["profiles"] = arr
            };

            File.WriteAllText(dlg.FileName, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            StatusFooterText.Text = $"Profiller '{Path.GetFileName(dlg.FileName)}' dosyasına dışa aktarıldı.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"JSON dışa aktarma hatası:\n{ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveDaemonConfig_Click(object sender, RoutedEventArgs e)
    {
        _automation.SaveConfig();
        StatusFooterText.Text = "Makro ve otomatik profil kuralları kaydedildi.";
    }

    private void AutoSwitchToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || _automation is null) return;
        _automation.AutoProfileSwitchEnabled = AutoSwitchToggle.IsChecked == true;
        _automation.SaveConfig();
    }

    private void OsdToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || _automation is null) return;
        bool enabled = OsdToggle.IsChecked == true;
        _automation.OsdNotificationsEnabled = enabled;
        _osd.IsEnabled = enabled;
        _automation.SaveConfig();
        if (enabled)
        {
            var prof = _profiles[_activeProfileIndex];
            int dpi = prof.DpiStages[Math.Clamp(_activeDpiStageIndex, 0, 3)];
            _osd.ShowNotification($"Profil {_activeProfileIndex + 1} · {prof.ColorName}", $"{dpi} DPI · {prof.PollingRateHz} Hz", prof.ColorHex);
        }
    }

    private void StartupToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents) return;
        bool enabled = StartupToggle.IsChecked == true;
        HostAutomationService.SetRunAtStartup(enabled);
        StatusFooterText.Text = enabled
            ? "Windows başlangıcında tepsi modunda otomatik çalışma etkinleştirildi."
            : "Windows başlangıcında otomatik çalışma devre dışı bırakıldı.";
    }

    private void AssignGShiftBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedButtonIndex < 0) return;
        if (_automation.Macros.TryGetValue("f18", out var f18Macro))
        {
            f18Macro.Mode = "gshift";
            _automation.SaveConfig();
            BuildDaemonWorkspaceUi();
        }
        AssignActionToSelectedButton("key:f18");
    }

    [StructLayout(LayoutKind.Sequential)]
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

    private void DefaultSlotCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || _automation is null) return;
        _automation.DefaultProfileIndex = Math.Clamp(DefaultSlotCombo.SelectedIndex, 0, 2);
        _automation.SaveConfig();
    }

    private void NewRuleExeCombo_DropDownOpened(object sender, EventArgs e)
    {
        string current = NewRuleExeCombo.Text;
        NewRuleExeCombo.Items.Clear();

        try
        {
            var names = Process.GetProcesses()
                .Where(p => p.MainWindowHandle != IntPtr.Zero)
                .Select(p =>
                {
                    try { return p.ProcessName + ".exe"; }
                    catch { return null; }
                })
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n)
                .ToList();

            foreach (string? n in names)
            {
                NewRuleExeCombo.Items.Add(n!);
            }
        }
        catch
        {
            // Ignore process enumeration errors
        }

        NewRuleExeCombo.Text = current;
    }

    private void AddAppRule_Click(object sender, RoutedEventArgs e)
    {
        string exe = NewRuleExeCombo.Text.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(exe)) return;
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            exe += ".exe";
        }

        int slot = Math.Clamp(NewRuleSlotCombo.SelectedIndex, 0, 2);
        var existing = _automation.AppRules.FirstOrDefault(r => string.Equals(r.ExecutableName, exe, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.ProfileIndex = slot;
        }
        else
        {
            _automation.AppRules.Add(new AppProfileRule { ExecutableName = exe, ProfileIndex = slot });
        }

        _automation.SaveConfig();
        RefreshAppRulesListUi();
        StatusFooterText.Text = $"Otomatik geçiş kuralı eklendi: {exe} → Profil {slot + 1}";
    }

    private void OnHardwareEventReceived(HardwareButtonEvent ev)
    {
        Dispatcher.BeginInvoke(() =>
        {
            string stateLabel = ev.IsPressed ? "Basıldı" : "Bırakıldı";
            string friendlyName = G300sProtocol.GetFriendlyActionTitle(ev.ActionName);
            TelemetryEventText.Text = $"{friendlyName} ({stateLabel})";

            if (_device.IsConnected)
            {
                try
                {
                    var state = _device.GetActiveState();
                    if (state.ProfileIndex != _activeProfileIndex || state.DpiStageIndex != _activeDpiStageIndex)
                    {
                        _activeProfileIndex = state.ProfileIndex;
                        _activeDpiStageIndex = state.DpiStageIndex;
                        RefreshSlotHeaderVisuals();
                        RefreshCurrentProfileToUi();

                        var prof = _profiles[_activeProfileIndex];
                        int dpi = prof.DpiStages[Math.Clamp(_activeDpiStageIndex, 0, 3)];
                        _osd.ShowNotification(
                            $"Profil {_activeProfileIndex + 1} · {prof.ColorName}",
                            $"Kademe {_activeDpiStageIndex + 1}: {dpi} DPI · {prof.PollingRateHz} Hz",
                            prof.ColorHex);
                    }
                }
                catch
                {
                    // Ignore transient state poll error
                }
            }
        });
    }

    private void OnAutoProfileSwitched(int newSlot, string exeName)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _activeProfileIndex = newSlot;
            RefreshSlotHeaderVisuals();
            RefreshCurrentProfileToUi();
            StatusFooterText.Text = $"Otomatik geçiş: {exeName} → Profil {newSlot + 1}";

            var prof = _profiles[_activeProfileIndex];
            int dpi = prof.DpiStages[Math.Clamp(_activeDpiStageIndex, 0, 3)];
            _osd.ShowNotification(
                $"Otomatik Profil · {exeName}",
                $"Profil {_activeProfileIndex + 1} ({prof.ColorName}) · {dpi} DPI",
                prof.ColorHex);
        });
    }

    private void OnGShiftStateChanged(bool isActive, int targetSlot)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _activeProfileIndex = Math.Clamp(targetSlot, 0, 2);
            RefreshSlotHeaderVisuals();
            RefreshCurrentProfileToUi();

            var prof = _profiles[_activeProfileIndex];
            int dpi = prof.DpiStages[Math.Clamp(_activeDpiStageIndex, 0, 3)];
            _osd.ShowNotification(
                isActive ? $"G-Shift Katmanı Aktif (Profil {targetSlot + 1})" : $"Normal Katmana Dönüldü (Profil {targetSlot + 1})",
                $"{dpi} DPI · {prof.ColorName}",
                prof.ColorHex);
        });
    }

    private void OnMacroKeyTriggered(string desc)
    {
        Dispatcher.BeginInvoke(() =>
        {
            TelemetryEventText.Text = $"Makro: {desc}";
        });
    }

    private void MinimizeToTray_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        StatusFooterText.Text = "Sistem tepsisinde çalışıyor.";
    }

    private void CaptionMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CaptionMaximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CaptionClose_Click(object sender, RoutedEventArgs e)
    {
        _isExiting = true;
        Close();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isExiting)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _rateTestCts?.Cancel();
        _rateTestCts?.Dispose();
        _livePreviewTimer.Stop();
        _osd.Dispose();
        _tray.Dispose();
        _automation.Dispose();
        _device.Dispose();
    }

    private void CaptureWindowScreenshot(string outputPath)
    {
        try
        {
            UpdateLayout();
            int w = (int)Math.Max(1, ActualWidth);
            int h = (int)Math.Max(1, ActualHeight);

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(this);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using var fs = File.Create(outputPath);
            encoder.Save(fs);
        }
        catch
        {
            // Ignore screenshot error
        }
    }
}
