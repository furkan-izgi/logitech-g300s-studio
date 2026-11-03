using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        new(0, "G1", "Primary Left Click",       59,  33, 323, 141,   8, 232, true),
        new(1, "G2", "Primary Right Click",     213,   6, 441,  78, 842, 142, false),
        new(2, "G3", "Middle Wheel Click",      241,  39, 313,  78,   8,  34, true),
        new(3, "G4", "Left Index Rear (G4)",    140, 117, 216, 161,   8, 166, true),
        new(4, "G5", "Left Index Front (G5)",    66,  97, 128, 134,   8,  98, true),
        new(5, "G6", "Right Ring Rear (G6)",    425,  14, 498,  53, 842,  80, false),
        new(6, "G7", "Right Ring Front (G7)",   344, -10, 405,  30, 842,  18, false),
        new(7, "G8", "Center Rear / Mode (G8)", 390,  59, 452, 111, 842, 266, false),
        new(8, "G9", "Center Front / DPI (G9)", 353,  59, 406,  97, 842, 204, false)
    ];

    private static readonly (string Name, int R, int G, int B, string Hex)[] PaletteSwatches =
    [
        ("Stealth Off",   0, 0, 0, "#262C36"),
        ("Crimson Red",   1, 0, 0, "#FF334B"),
        ("Solar Amber",   1, 1, 0, "#FFB800"),
        ("Venom Green",   0, 1, 0, "#00E676"),
        ("Logitech Cyan", 0, 1, 1, "#00D4FF"),
        ("Cobalt Blue",   0, 0, 1, "#2979FF"),
        ("Hyper Magenta", 1, 0, 1, "#E040FB"),
        ("Pure White",    1, 1, 1, "#F0F4F8")
    ];

    private readonly G300sHidDevice _device = new();
    private readonly HostAutomationService _automation;
    private readonly SystemTrayService _tray = new();

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
    private readonly TextBlock[] _calloutActionTexts = new TextBlock[9];
    private readonly Polyline[] _leaderLines = new Polyline[9];
    private readonly Ellipse[] _anchorDots = new Ellipse[9];

    private readonly Border[] _ledSwatchBorders = new Border[8];
    private readonly Slider[] _dpiSliders = new Slider[4];
    private readonly TextBlock[] _dpiValueLabels = new TextBlock[4];
    private readonly Button[] _dpiActivateButtons = new Button[4];
    private readonly Button[] _dpiDefaultButtons = new Button[4];

    private int _activeProfileIndex;
    private int _activeDpiStageIndex = 1;
    private int _selectedButtonIndex;
    private int _hoveredButtonIndex = -1;
    private bool _isRecordingKey;
    private bool _suppressUiEvents;
    private bool _hasUnsavedChanges;
    private bool _isExiting;

    public MainWindow()
    {
        InitializeComponent();

        _automation = new HostAutomationService(_device);
        _device.HardwareEventReceived += OnHardwareEventReceived;
        _automation.AutoProfileSwitched += OnAutoProfileSwitched;
        _automation.MacroKeyTriggered += OnMacroKeyTriggered;

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        string assetsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets");
        LoadVisualAssets(assetsDir);
        BuildOverlayCallouts();
        BuildDpiStagesUi();
        BuildLedSwatchesUi();
        BuildDaemonWorkspaceUi();

        string iconPath = Path.Combine(assetsDir, "g300s.ico");
        _tray.Initialize(this, iconPath);
        _tray.RestoreRequested += RestoreFromTray;
        _tray.ProfileSwitchRequested += idx => Dispatcher.Invoke(() => SelectProfileSlot(idx, sendToHardware: true));
        _tray.ExitRequested += () =>
        {
            _isExiting = true;
            Dispatcher.Invoke(Close);
        };

        _automation.StartHookAndWatcher();
        ConnectAndReadHardware();
        UpdateWorkspaceTabVisuals(0);

        // Support automated visual verification screenshot if requested via CLI
        string[] args = Environment.GetCommandLineArgs();
        int ssIdx = Array.IndexOf(args, "--verify-screenshot");
        if (ssIdx >= 0 && ssIdx + 1 < args.Length)
        {
            string outPath = args[ssIdx + 1];
            await Task.Delay(650);
            CaptureWindowScreenshot(outPath);

            UpdateWorkspaceTabVisuals(1);
            await Task.Delay(300);
            string ext = Path.GetExtension(outPath);
            string tab2Path = Path.Combine(
                Path.GetDirectoryName(outPath) ?? "",
                $"{Path.GetFileNameWithoutExtension(outPath)}_tab2{ext}");
            CaptureWindowScreenshot(tab2Path);

            _isExiting = true;
            Application.Current.Shutdown(0);
        }
    }

    private void LoadVisualAssets(string assetsDir)
    {
        MouseBaseImage.Source = LoadPng(Path.Combine(assetsDir, "G300sDevice_logitech.png"));
        _rawSwooshBitmap = LoadPng(Path.Combine(assetsDir, "G300sDevice_swoosh.png"));
        MouseSwooshTextureImage.Source = LoadPng(Path.Combine(assetsDir, "G300sDevice_swoosh_texture.png"));
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

    private void UpdateTintedSwoosh(int rBit, int gBit, int bBit)
    {
        if (_rawSwooshBitmap is null) return;

        Color targetColor = (rBit != 0 ? 1 : 0, gBit != 0 ? 1 : 0, bBit != 0 ? 1 : 0) switch
        {
            (0, 0, 0) => Color.FromRgb(22, 26, 32),
            (1, 0, 0) => Color.FromRgb(255, 45, 65),
            (1, 1, 0) => Color.FromRgb(255, 195, 0),
            (0, 1, 0) => Color.FromRgb(0, 235, 110),
            (0, 1, 1) => Color.FromRgb(0, 215, 255),
            (0, 0, 1) => Color.FromRgb(45, 125, 255),
            (1, 0, 1) => Color.FromRgb(230, 60, 255),
            (1, 1, 1) => Color.FromRgb(245, 250, 255),
            _ => Color.FromRgb(0, 215, 255)
        };

        var bgra = new FormatConvertedBitmap(_rawSwooshBitmap, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth;
        int h = bgra.PixelHeight;
        byte[] pixels = new byte[w * h * 4];
        bgra.CopyPixels(pixels, w * 4, 0);

        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte lum = pixels[i + 2]; // Red channel intensity
            pixels[i + 0] = (byte)(lum * targetColor.B / 255);
            pixels[i + 1] = (byte)(lum * targetColor.G / 255);
            pixels[i + 2] = (byte)(lum * targetColor.R / 255);
        }

        var wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, w, h), pixels, w * 4, 0);
        wb.Freeze();
        MouseSwooshImage.Source = wb;
    }

    private void BuildOverlayCallouts()
    {
        OverlayCalloutsCanvas.Children.Clear();

        foreach (var meta in ButtonMetaList)
        {
            int idx = meta.Index;
            double anchorX = MouseOffsetX + meta.CenterX;
            double anchorY = MouseOffsetY + meta.CenterY;

            double cardW = 190.0;
            double cardH = 48.0;
            double attachX = meta.IsLeftSide ? meta.CardX + cardW : meta.CardX;
            double attachY = meta.CardY + cardH / 2.0;
            double elbowX = meta.IsLeftSide ? attachX + 26.0 : attachX - 26.0;

            var line = new Polyline
            {
                Points =
                [
                    new Point(anchorX, anchorY),
                    new Point(elbowX, attachY),
                    new Point(attachX, attachY)
                ],
                Stroke = new SolidColorBrush(Color.FromArgb(140, 100, 116, 139)),
                StrokeThickness = 1.5,
                IsHitTestVisible = false
            };
            _leaderLines[idx] = line;
            OverlayCalloutsCanvas.Children.Add(line);

            var dot = new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = new SolidColorBrush(Color.FromRgb(0, 200, 255)),
                Stroke = new SolidColorBrush(Color.FromRgb(11, 13, 16)),
                StrokeThickness = 1.5,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(dot, anchorX - 3.5);
            Canvas.SetTop(dot, anchorY - 3.5);
            _anchorDots[idx] = dot;
            OverlayCalloutsCanvas.Children.Add(dot);

            // Callout Pill Card
            var card = new Border
            {
                Width = cardW,
                Height = cardH,
                CornerRadius = new CornerRadius(5),
                Background = (Brush)FindResource("BrushBgSurface2"),
                BorderBrush = (Brush)FindResource("BrushBorderSubtle"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 6, 10, 6),
                Cursor = Cursors.Hand,
                Tag = idx
            };
            Canvas.SetLeft(card, meta.CardX);
            Canvas.SetTop(card, meta.CardY);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var badge = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(4),
                Background = (Brush)FindResource("BrushBgElevated"),
                BorderBrush = (Brush)FindResource("BrushBorderStrong"),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 9, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = meta.CodeLabel,
                    FontFamily = (FontFamily)FindResource("FontMono"),
                    FontSize = 11.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = (Brush)FindResource("BrushAccentCyan"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            Grid.SetColumn(badge, 0);
            grid.Children.Add(badge);

            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var nameLbl = new TextBlock
            {
                Text = meta.FullTitle,
                FontSize = 10.5,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var actionLbl = new TextBlock
            {
                Text = "Left Click",
                FontFamily = (FontFamily)FindResource("FontMono"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("BrushTextPrimary"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 1, 0, 0)
            };
            _calloutActionTexts[idx] = actionLbl;
            textStack.Children.Add(nameLbl);
            textStack.Children.Add(actionLbl);
            Grid.SetColumn(textStack, 1);
            grid.Children.Add(textStack);

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
            var rowBorder = new Border
            {
                Background = (Brush)FindResource("BrushBgSurface2"),
                BorderBrush = (Brush)FindResource("BrushBorderSubtle"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 4, 7, 4),
                Margin = new Thickness(0, 0, 0, i < 3 ? 5 : 0)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });

            // Activate Stage Button
            var actBtn = new Button
            {
                Content = $"S{stageIdx + 1}",
                Style = (Style)FindResource("ToolbarActionButton"),
                Width = 32,
                Height = 24,
                Padding = new Thickness(0),
                FontFamily = (FontFamily)FindResource("FontMono"),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 5, 0),
                ToolTip = $"Switch active hardware DPI to Stage {stageIdx + 1} immediately (0xF0)"
            };
            actBtn.Click += (_, _) => ActivateDpiStage(stageIdx);
            _dpiActivateButtons[stageIdx] = actBtn;
            Grid.SetColumn(actBtn, 0);
            grid.Children.Add(actBtn);

            // Default Boot Star Button
            var defBtn = new Button
            {
                Content = "★",
                Style = (Style)FindResource("ToolbarActionButton"),
                Width = 24,
                Height = 24,
                Padding = new Thickness(0),
                FontSize = 11.5,
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = $"Set Stage {stageIdx + 1} as the profile boot default DPI"
            };
            defBtn.Click += (_, _) => SetDefaultDpiStage(stageIdx);
            _dpiDefaultButtons[stageIdx] = defBtn;
            Grid.SetColumn(defBtn, 1);
            grid.Children.Add(defBtn);

            // Minus Step Button
            var minusBtn = new Button
            {
                Content = "−",
                Style = (Style)FindResource("StepIconButton"),
                Margin = new Thickness(0, 0, 6, 0)
            };
            minusBtn.Click += (_, _) => StepDpiStage(stageIdx, -250);
            Grid.SetColumn(minusBtn, 2);
            grid.Children.Add(minusBtn);

            // Slider
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
            Grid.SetColumn(slider, 3);
            grid.Children.Add(slider);

            // Plus Step Button
            var plusBtn = new Button
            {
                Content = "+",
                Style = (Style)FindResource("StepIconButton"),
                Margin = new Thickness(6, 0, 6, 0)
            };
            plusBtn.Click += (_, _) => StepDpiStage(stageIdx, 250);
            Grid.SetColumn(plusBtn, 4);
            grid.Children.Add(plusBtn);

            // Numeric Readout
            var valText = new TextBlock
            {
                Text = "1000 DPI",
                FontFamily = (FontFamily)FindResource("FontMono"),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("BrushTextPrimary"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            _dpiValueLabels[stageIdx] = valText;
            Grid.SetColumn(valText, 5);
            grid.Children.Add(valText);

            rowBorder.Child = grid;
            DpiStagesContainer.Children.Add(rowBorder);
        }
    }

    private void BuildLedSwatchesUi()
    {
        LedSwatchesGrid.Children.Clear();

        for (int i = 0; i < PaletteSwatches.Length; i++)
        {
            var sw = PaletteSwatches[i];
            int idx = i;

            var outer = new Border
            {
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Padding = new Thickness(2),
                Margin = new Thickness(2, 0, 2, 0),
                Cursor = Cursors.Hand,
                ToolTip = $"{sw.Name} (R={sw.R}, G={sw.G}, B={sw.B})"
            };

            var inner = new Border
            {
                CornerRadius = new CornerRadius(3),
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString(sw.Hex)!,
                BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
                BorderThickness = new Thickness(1)
            };

            outer.Child = inner;
            outer.MouseLeftButtonDown += (_, _) => SelectLedColor(sw.R, sw.G, sw.B);
            _ledSwatchBorders[idx] = outer;
            LedSwatchesGrid.Children.Add(outer);
        }
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
                BorderBrush = (Brush)FindResource("BrushBorderSubtle"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 8)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(95) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(178) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var badge = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            badge.Children.Add(new TextBlock
            {
                Text = $"MACRO {i + 1}",
                FontFamily = (FontFamily)FindResource("FontDisplay"),
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("BrushAccentCyan")
            });
            badge.Children.Add(new TextBlock
            {
                Text = $"Trigger: {k.ToUpperInvariant()}",
                FontFamily = (FontFamily)FindResource("FontMono"),
                FontSize = 10,
                Foreground = (Brush)FindResource("BrushTextMuted")
            });
            Grid.SetColumn(badge, 0);
            grid.Children.Add(badge);

            var modeCombo = new ComboBox { Height = 32, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            modeCombo.Items.Add(new ComboBoxItem { Content = "Text String", Tag = "text" });
            modeCombo.Items.Add(new ComboBoxItem { Content = "Rapid-Fire Click", Tag = "rapid_fire" });
            modeCombo.Items.Add(new ComboBoxItem { Content = "Key Shortcut", Tag = "shortcut" });
            modeCombo.SelectedIndex = item.Mode switch { "rapid_fire" => 1, "shortcut" => 2, _ => 0 };
            Grid.SetColumn(modeCombo, 1);
            grid.Children.Add(modeCombo);

            var paramBox = new TextBox
            {
                Height = 32,
                VerticalContentAlignment = VerticalAlignment.Center,
                FontFamily = (FontFamily)FindResource("FontMono"),
                FontSize = 11.5,
                Padding = new Thickness(8, 0, 8, 0),
                Text = item.Mode == "rapid_fire" ? $"{item.Cps} CPS ({item.MouseButton})" : item.TextPayload
            };
            Grid.SetColumn(paramBox, 2);
            grid.Children.Add(paramBox);

            modeCombo.SelectionChanged += (_, _) =>
            {
                if (modeCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string m)
                {
                    item.Mode = m;
                    paramBox.Text = m == "rapid_fire" ? $"{item.Cps}" : item.TextPayload;
                }
            };

            paramBox.TextChanged += (_, _) =>
            {
                if (item.Mode == "rapid_fire")
                {
                    string digits = new string(paramBox.Text.Where(char.IsDigit).ToArray());
                    if (int.TryParse(digits, out int cps)) item.Cps = Math.Clamp(cps, 1, 50);
                }
                else
                {
                    item.TextPayload = paramBox.Text;
                }
            };

            card.Child = grid;
            MacroItemsPanel.Children.Add(card);
        }

        RefreshAppRulesListUi();
    }

    private void RefreshAppRulesListUi()
    {
        AppRulesPanel.Children.Clear();
        foreach (var rule in _automation.AppRules.ToList())
        {
            var row = new Border
            {
                Background = (Brush)FindResource("BrushBgSurface2"),
                BorderBrush = (Brush)FindResource("BrushBorderSubtle"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 6)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var exeText = new TextBlock
            {
                Text = rule.ExecutableName,
                FontFamily = (FontFamily)FindResource("FontMono"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("BrushTextPrimary"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(exeText, 0);
            grid.Children.Add(exeText);

            var slotBadge = new Border
            {
                Background = (Brush)FindResource("BrushAccentCyanSubtle"),
                BorderBrush = (Brush)FindResource("BrushAccentCyanBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = $"SLOT {rule.ProfileIndex + 1}",
                    FontFamily = (FontFamily)FindResource("FontMono"),
                    FontSize = 10.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = (Brush)FindResource("BrushAccentCyan")
                }
            };
            Grid.SetColumn(slotBadge, 1);
            grid.Children.Add(slotBadge);

            var delBtn = new Button
            {
                Content = "Remove",
                Style = (Style)FindResource("ToolbarActionButton"),
                Height = 24,
                Padding = new Thickness(8, 0, 8, 0),
                FontSize = 11
            };
            delBtn.Click += (_, _) =>
            {
                _automation.AppRules.Remove(rule);
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

                SetConnectionStatusUi(true, "CONNECTED");
                StatusFooterText.Text = $"Synchronized with Logitech G300s hardware · Active Slot {_activeProfileIndex + 1}, Stage {_activeDpiStageIndex + 1}.";
            }
            else
            {
                SetConnectionStatusUi(false, "OFFLINE PREVIEW");
                StatusFooterText.Text = "Logitech G300s not detected on USB · Running in offline studio editor mode.";
            }
        }
        catch (Exception ex)
        {
            SetConnectionStatusUi(false, "IO ERROR");
            StatusFooterText.Text = $"Hardware read error: {ex.Message}";
        }

        _hasUnsavedChanges = false;
        UpdateDirtyStateVisuals();
        SelectProfileSlot(_activeProfileIndex, sendToHardware: false);
        SelectButtonForEditing(0);
    }

    private void SetConnectionStatusUi(bool online, string label)
    {
        ConnectionStatusText.Text = label;
        if (online)
        {
            ConnectionBadge.Background = (Brush)FindResource("BrushSuccessSubtle");
            ConnectionBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(80, 16, 185, 129));
            ConnectionDot.Fill = (Brush)FindResource("BrushSuccess");
            ConnectionStatusText.Foreground = (Brush)FindResource("BrushSuccess");
        }
        else
        {
            ConnectionBadge.Background = (Brush)FindResource("BrushWarningSubtle");
            ConnectionBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(80, 245, 158, 11));
            ConnectionDot.Fill = (Brush)FindResource("BrushWarning");
            ConnectionStatusText.Foreground = (Brush)FindResource("BrushWarning");
        }
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
                StatusFooterText.Text = $"Switched active hardware profile to Slot {_activeProfileIndex + 1}.";
            }
            catch (Exception ex)
            {
                StatusFooterText.Text = $"Failed to switch profile: {ex.Message}";
            }
        }

        RefreshSlotHeaderVisuals();
        RefreshCurrentProfileToUi();
    }

    private void RefreshSlotHeaderVisuals()
    {
        SlotDot0.Fill = (SolidColorBrush)new BrushConverter().ConvertFromString(_profiles[0].ColorHex)!;
        SlotDot1.Fill = (SolidColorBrush)new BrushConverter().ConvertFromString(_profiles[1].ColorHex)!;
        SlotDot2.Fill = (SolidColorBrush)new BrushConverter().ConvertFromString(_profiles[2].ColorHex)!;

        Button[] slotBtns = [SlotBtn0, SlotBtn1, SlotBtn2];
        for (int i = 0; i < 3; i++)
        {
            bool active = i == _activeProfileIndex;
            slotBtns[i].Background = active ? (Brush)FindResource("BrushBgElevated") : Brushes.Transparent;
            slotBtns[i].Foreground = active ? (Brush)FindResource("BrushAccentCyan") : (Brush)FindResource("BrushTextSecondary");
        }

        StageActiveSlotLabel.Text = $"SLOT {_activeProfileIndex + 1} · {_profiles[_activeProfileIndex].ColorName.ToUpperInvariant()}";
    }

    private void RefreshCurrentProfileToUi()
    {
        _suppressUiEvents = true;
        try
        {
            var prof = _profiles[_activeProfileIndex];

            // 1. Update 3D Mouse LED Swoosh & Swatches
            UpdateTintedSwoosh(prof.LedR, prof.LedG, prof.LedB);
            ActiveColorLabel.Text = prof.ColorName;
            ActiveColorLabel.Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString(prof.ColorHex)!;

            for (int i = 0; i < PaletteSwatches.Length; i++)
            {
                var sw = PaletteSwatches[i];
                bool match = sw.R == prof.LedR && sw.G == prof.LedG && sw.B == prof.LedB;
                _ledSwatchBorders[i].BorderBrush = match ? (Brush)FindResource("BrushAccentCyan") : Brushes.Transparent;
            }

            // 2. Update Callout Cards
            for (int i = 0; i < 9; i++)
            {
                _calloutActionTexts[i].Text = G300sProtocol.GetFriendlyActionTitle(prof.Buttons[i]);
            }

            // 3. Update Polling Rate Segmented Buttons
            Button[] rateBtns = [RateBtn125, RateBtn250, RateBtn500, RateBtn1000];
            int[] rates = [125, 250, 500, 1000];
            for (int i = 0; i < 4; i++)
            {
                bool activeRate = prof.PollingRateHz == rates[i];
                rateBtns[i].Background = activeRate ? (Brush)FindResource("BrushAccentCyanSubtle") : (Brush)FindResource("BrushBgSurface2");
                rateBtns[i].BorderBrush = activeRate ? (Brush)FindResource("BrushAccentCyan") : (Brush)FindResource("BrushBorderSubtle");
                rateBtns[i].Foreground = activeRate ? (Brush)FindResource("BrushAccentCyan") : (Brush)FindResource("BrushTextSecondary");
            }

            // 4. Update 4 DPI Stages
            for (int i = 0; i < 4; i++)
            {
                int dpi = prof.DpiStages[i];
                _dpiSliders[i].Value = dpi;
                _dpiValueLabels[i].Text = $"{dpi} DPI";

                bool isDefault = i == prof.DefaultDpiStage;
                _dpiDefaultButtons[i].Foreground = isDefault ? (Brush)FindResource("BrushWarning") : (Brush)FindResource("BrushTextMuted");
                _dpiDefaultButtons[i].BorderBrush = isDefault ? (Brush)FindResource("BrushWarning") : (Brush)FindResource("BrushBorderSubtle");

                bool isActiveStage = i == _activeDpiStageIndex;
                _dpiActivateButtons[i].Background = isActiveStage ? (Brush)FindResource("BrushAccentCyanSubtle") : (Brush)FindResource("BrushBgSurface2");
                _dpiActivateButtons[i].BorderBrush = isActiveStage ? (Brush)FindResource("BrushAccentCyan") : (Brush)FindResource("BrushBorderSubtle");
                _dpiActivateButtons[i].Foreground = isActiveStage ? (Brush)FindResource("BrushAccentCyan") : (Brush)FindResource("BrushTextSecondary");
            }

            // 5. Update Telemetry Strip & Hex Preview
            int activeDpiVal = prof.DpiStages[Math.Clamp(_activeDpiStageIndex, 0, 3)];
            TelemetryDpiText.Text = $"{activeDpiVal} DPI (STAGE {_activeDpiStageIndex + 1})";
            double intervalMs = 1000.0 / prof.PollingRateHz;
            TelemetryRateText.Text = $"{prof.PollingRateHz} Hz ({intervalMs:0.0} ms)";

            byte[] raw = prof.ToRawReport();
            string hexHead = BitConverter.ToString(raw, 0, 14).Replace("-", " ");
            RawHexPreviewText.Text = $"0x{raw[0]:X2}: {hexHead} ... (35B)";

            _tray.UpdateActiveProfile(_activeProfileIndex, activeDpiVal, prof.PollingRateHz);
            RefreshInspectorCard();
        }
        finally
        {
            _suppressUiEvents = false;
        }
    }

    private void SelectButtonForEditing(int buttonIndex)
    {
        _selectedButtonIndex = Math.Clamp(buttonIndex, 0, 8);
        StopKeyRecording();

        var meta = ButtonMetaList[_selectedButtonIndex];
        if (_pulseGlowBitmaps[_selectedButtonIndex] is { } pgBmp)
        {
            SelectedButtonGlowImage.Source = pgBmp;
            SelectedButtonGlowImage.Width = pgBmp.PixelWidth;
            SelectedButtonGlowImage.Height = pgBmp.PixelHeight;
            Canvas.SetLeft(SelectedButtonGlowImage, MouseOffsetX + meta.RelX);
            Canvas.SetTop(SelectedButtonGlowImage, MouseOffsetY + meta.RelY);
            SelectedButtonGlowImage.Visibility = Visibility.Visible;
        }

        if (_highlightBitmaps[_selectedButtonIndex] is { } hlBmp)
        {
            SelectedButtonHighlightImage.Source = hlBmp;
            SelectedButtonHighlightImage.Width = hlBmp.PixelWidth;
            SelectedButtonHighlightImage.Height = hlBmp.PixelHeight;
            Canvas.SetLeft(SelectedButtonHighlightImage, MouseOffsetX + meta.RelX);
            Canvas.SetTop(SelectedButtonHighlightImage, MouseOffsetY + meta.RelY);
            SelectedButtonHighlightImage.Visibility = Visibility.Visible;
        }

        UpdateCalloutHighlights();
        RefreshInspectorCard();
    }

    private void SetHoveredButton(int buttonIndex)
    {
        if (_hoveredButtonIndex == buttonIndex) return;
        _hoveredButtonIndex = buttonIndex;

        if (buttonIndex is >= 0 and < 9 && buttonIndex != _selectedButtonIndex)
        {
            var meta = ButtonMetaList[buttonIndex];
            if (_pulseGlowBitmaps[buttonIndex] is { } pgBmp)
            {
                HoveredButtonGlowImage.Source = pgBmp;
                HoveredButtonGlowImage.Width = pgBmp.PixelWidth;
                HoveredButtonGlowImage.Height = pgBmp.PixelHeight;
                Canvas.SetLeft(HoveredButtonGlowImage, MouseOffsetX + meta.RelX);
                Canvas.SetTop(HoveredButtonGlowImage, MouseOffsetY + meta.RelY);
                HoveredButtonGlowImage.Visibility = Visibility.Visible;
            }
        }
        else
        {
            HoveredButtonGlowImage.Visibility = Visibility.Collapsed;
        }

        UpdateCalloutHighlights();
    }

    private void UpdateCalloutHighlights()
    {
        for (int i = 0; i < 9; i++)
        {
            bool isSelected = i == _selectedButtonIndex;
            bool isHovered = i == _hoveredButtonIndex;

            if (isSelected)
            {
                _calloutBorders[i].Background = (Brush)FindResource("BrushAccentCyanSubtle");
                _calloutBorders[i].BorderBrush = (Brush)FindResource("BrushAccentCyan");
                _leaderLines[i].Stroke = (Brush)FindResource("BrushAccentCyan");
                _leaderLines[i].StrokeThickness = 2.0;
                _anchorDots[i].Width = 9;
                _anchorDots[i].Height = 9;
            }
            else if (isHovered)
            {
                _calloutBorders[i].Background = (Brush)FindResource("BrushBgElevated");
                _calloutBorders[i].BorderBrush = (Brush)FindResource("BrushBorderStrong");
                _leaderLines[i].Stroke = new SolidColorBrush(Color.FromArgb(220, 0, 200, 255));
                _leaderLines[i].StrokeThickness = 1.8;
                _anchorDots[i].Width = 7;
                _anchorDots[i].Height = 7;
            }
            else
            {
                _calloutBorders[i].Background = (Brush)FindResource("BrushBgSurface2");
                _calloutBorders[i].BorderBrush = (Brush)FindResource("BrushBorderSubtle");
                _leaderLines[i].Stroke = new SolidColorBrush(Color.FromArgb(120, 100, 116, 139));
                _leaderLines[i].StrokeThickness = 1.4;
                _anchorDots[i].Width = 7;
                _anchorDots[i].Height = 7;
            }
        }
    }

    private void RefreshInspectorCard()
    {
        var meta = ButtonMetaList[_selectedButtonIndex];
        var prof = _profiles[_activeProfileIndex];
        string actionSpec = prof.Buttons[_selectedButtonIndex];

        InspectorBtnNumberText.Text = meta.CodeLabel;
        InspectorBtnTitleText.Text = meta.FullTitle;
        var (code, mod, key) = G300sProtocol.EncodeButtonAction(actionSpec);
        InspectorBtnSubText.Text = $"Button Index {_selectedButtonIndex} · Raw Bytes [{code:X2} {mod:X2} {key:X2}]";
        InspectorCurrentActionText.Text = G300sProtocol.GetFriendlyActionTitle(actionSpec);
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
    }

    private int HitTestMouseButton(double mouseX, double mouseY)
    {
        // Check smaller auxiliary buttons (G3..G9) before large primary buttons (G1, G2)
        int[] checkOrder = [2, 3, 4, 5, 6, 7, 8, 0, 1];
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
        var prof = _profiles[_activeProfileIndex];
        prof.SetButtonAction(_selectedButtonIndex, actionSpec);
        MarkModified($"Assigned '{G300sProtocol.GetFriendlyActionTitle(actionSpec)}' to {ButtonMetaList[_selectedButtonIndex].CodeLabel}.");
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
        RecordKeyBtnText.Text = "Listening... Press any key or combo (Esc to cancel)";
        RecordKeyBtn.BorderBrush = (Brush)FindResource("BrushAccentCyan");
        RecordKeyBtn.Focus();
    }

    private void RecordKeyBtn_LostFocus(object sender, RoutedEventArgs e)
    {
        StopKeyRecording();
    }

    private void StopKeyRecording()
    {
        if (!_isRecordingKey) return;
        _isRecordingKey = false;
        RecordKeyBtnText.Text = "Record Keypress (Ctrl+C, F13...)";
        RecordKeyBtn.BorderBrush = (Brush)FindResource("BrushBorderSubtle");
    }

    private void RecordKeyBtn_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isRecordingKey) return;
        e.Handled = true;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Ignore lone modifier presses while waiting for main key
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
            StatusFooterText.Text = $"Unsupported HID key '{key}'. Try standard alphanumeric, F1-F24, or navigation keys.";
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
            try
            {
                _device.SetLiveRate(hz);
            }
            catch
            {
                // Ignore live rate error
            }
        }

        MarkModified($"Set Slot {_activeProfileIndex + 1} polling rate to {hz} Hz (live 0xF2 applied).");
        RefreshCurrentProfileToUi();
    }

    private void OnDpiSliderValueChanged(int stageIdx, int newValue)
    {
        if (_suppressUiEvents) return;
        var prof = _profiles[_activeProfileIndex];
        prof.SetDpiStage(stageIdx, newValue);
        MarkModified($"Updated Stage {stageIdx + 1} sensitivity to {prof.DpiStages[stageIdx]} DPI.");
        RefreshCurrentProfileToUi();
    }

    private void StepDpiStage(int stageIdx, int delta)
    {
        var prof = _profiles[_activeProfileIndex];
        int next = Math.Clamp(prof.DpiStages[stageIdx] + delta, 250, 2500);
        prof.SetDpiStage(stageIdx, next);
        MarkModified($"Adjusted Stage {stageIdx + 1} sensitivity to {next} DPI.");
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
                StatusFooterText.Text = $"Switched active hardware sensitivity to Stage {_activeDpiStageIndex + 1} ({_profiles[_activeProfileIndex].DpiStages[_activeDpiStageIndex]} DPI).";
            }
            catch (Exception ex)
            {
                StatusFooterText.Text = $"Failed to switch DPI stage: {ex.Message}";
            }
        }
        RefreshCurrentProfileToUi();
    }

    private void SetDefaultDpiStage(int stageIdx)
    {
        var prof = _profiles[_activeProfileIndex];
        prof.DefaultDpiStage = stageIdx;
        MarkModified($"Set Stage {stageIdx + 1} ({prof.DpiStages[stageIdx]} DPI) as boot default for Slot {_activeProfileIndex + 1}.");
        RefreshCurrentProfileToUi();
    }

    private void SelectLedColor(int r, int g, int b)
    {
        var prof = _profiles[_activeProfileIndex];
        prof.SetLed(r, g, b);

        if (_device.IsConnected && _automation.ActiveRgbEffect == "none")
        {
            try
            {
                _device.SetLiveLed(r, g, b);
            }
            catch
            {
                // Ignore transient error
            }
        }

        MarkModified($"Set Slot {_activeProfileIndex + 1} illumination to {prof.ColorName} (live 0xF1 preview applied).");
        RefreshSlotHeaderVisuals();
        RefreshCurrentProfileToUi();
    }

    private void RgbEffectCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || _automation is null) return;
        if (RgbEffectCombo.SelectedItem is ComboBoxItem { Tag: string effect })
        {
            var prof = _profiles[_activeProfileIndex];
            _automation.SetRgbEffect(effect, 350, (prof.LedR, prof.LedG, prof.LedB));

            if (effect == "none" && _device.IsConnected)
            {
                try { _device.SetLiveLed(prof.LedR, prof.LedG, prof.LedB); } catch { /* ignore */ }
            }
            StatusFooterText.Text = $"Live RAM lighting mode set to '{effect}'.";
        }
    }

    private void MarkModified(string message)
    {
        _hasUnsavedChanges = true;
        StatusFooterText.Text = $"{message} (Unsaved changes in editor — click 'Flash to Memory' to persist)";
        UpdateDirtyStateVisuals();
    }

    private void UpdateDirtyStateVisuals()
    {
        FlashSaveBtnText.Text = _hasUnsavedChanges ? "Flash to Memory ●" : "Flash to Memory";
    }

    private async void SaveToHardware_Click(object sender, RoutedEventArgs e)
    {
        if (!_device.IsConnected)
        {
            MessageBox.Show(
                this,
                "Logitech G300s is not connected via USB. Connect the mouse and click 'Read Device'.",
                "Hardware Offline",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        FlashSaveBtn.IsEnabled = false;
        FlashSaveBtnText.Text = "Flashing EEPROM...";
        StatusFooterText.Text = "Checking 0xF0 busy lock and writing 35-byte profile blocks to onboard flash...";

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
                _device.SetLiveLed(p.LedR, p.LedG, p.LedB);
                _device.SetLiveRate(p.PollingRateHz);
            });

            _hasUnsavedChanges = false;
            UpdateDirtyStateVisuals();
            StatusFooterText.Text = "All 3 onboard memory slots flashed and verified on Logitech G300s hardware.";
        }
        catch (Exception ex)
        {
            StatusFooterText.Text = $"EEPROM flash failed: {ex.Message}";
            MessageBox.Show(this, $"Failed to write profiles to onboard flash:\n{ex.Message}", "Flash Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            FlashSaveBtn.IsEnabled = true;
            UpdateDirtyStateVisuals();
        }
    }

    private void ReloadFromMouse_Click(object sender, RoutedEventArgs e)
    {
        ConnectAndReadHardware();
    }

    private void ResetProfileDefaults_Click(object sender, RoutedEventArgs e)
    {
        var prof = _profiles[_activeProfileIndex];
        prof.PollingRateHz = 1000;
        prof.DefaultDpiStage = 1;
        prof.SetDpiStage(0, 500);
        prof.SetDpiStage(1, 1000);
        prof.SetDpiStage(2, 1500);
        prof.SetDpiStage(3, 2500);

        if (_activeProfileIndex == 0) prof.SetLed(0, 1, 1);
        else if (_activeProfileIndex == 1) prof.SetLed(0, 1, 0);
        else prof.SetLed(0, 0, 1);

        prof.SetButtonAction(0, "mouse:1");
        prof.SetButtonAction(1, "mouse:2");
        prof.SetButtonAction(2, "mouse:3");
        prof.SetButtonAction(3, "mouse:4");
        prof.SetButtonAction(4, "mouse:5");
        prof.SetButtonAction(5, "dpi-down");
        prof.SetButtonAction(6, "dpi-up");
        prof.SetButtonAction(7, "profile-cycle");
        prof.SetButtonAction(8, "dpi-cycle");

        MarkModified($"Loaded factory default layout into Slot {_activeProfileIndex + 1}.");
        RefreshSlotHeaderVisuals();
        RefreshCurrentProfileToUi();
    }

    private void ExportJson_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Export Logitech G300s Onboard Profiles",
            Filter = "JSON Profile Snapshot (*.json)|*.json",
            FileName = "g300s_profiles_backup.json"
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var root = new JsonObject
            {
                ["active_profile"] = _activeProfileIndex,
                ["active_dpi_level"] = _activeDpiStageIndex
            };
            var arr = new JsonArray();
            for (int i = 0; i < 3; i++)
            {
                var p = _profiles[i];
                arr.Add(new JsonObject
                {
                    ["index"] = p.Index,
                    ["led"] = new JsonArray(p.LedR, p.LedG, p.LedB),
                    ["rate"] = p.PollingRateHz,
                    ["dpis"] = new JsonArray(p.DpiStages[0], p.DpiStages[1], p.DpiStages[2], p.DpiStages[3]),
                    ["default_dpi"] = p.DefaultDpiStage,
                    ["raw7"] = p.Raw7,
                    ["buttons"] = new JsonArray(p.Buttons.Select(b => (JsonNode)b).ToArray())
                });
            }
            root["profiles"] = arr;

            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(dlg.FileName, root.ToJsonString(opts), Encoding.UTF8);
            StatusFooterText.Text = $"Exported 3 onboard profiles to '{Path.GetFileName(dlg.FileName)}'.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Export failed:\n{ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportJson_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Import Logitech G300s Onboard Profiles",
            Filter = "JSON Profile Snapshot (*.json)|*.json"
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            string json = File.ReadAllText(dlg.FileName, Encoding.UTF8);
            var root = JsonNode.Parse(json)?.AsObject();
            if (root?["profiles"] is not JsonArray arr)
                throw new InvalidDataException("JSON does not contain a 'profiles' array.");

            for (int i = 0; i < Math.Min(3, arr.Count); i++)
            {
                if (arr[i] is not JsonObject pObj) continue;
                var p = _profiles[i];
                p.PollingRateHz = pObj["rate"]?.GetValue<int>() ?? 1000;
                p.DefaultDpiStage = Math.Clamp(pObj["default_dpi"]?.GetValue<int>() ?? 1, 0, 3);
                p.Raw7 = (byte)(pObj["raw7"]?.GetValue<int>() ?? 0x40);

                if (pObj["led"] is JsonArray ledArr && ledArr.Count >= 3)
                {
                    p.SetLed(ledArr[0]!.GetValue<int>(), ledArr[1]!.GetValue<int>(), ledArr[2]!.GetValue<int>());
                }
                if (pObj["dpis"] is JsonArray dpiArr && dpiArr.Count >= 4)
                {
                    for (int s = 0; s < 4; s++) p.SetDpiStage(s, dpiArr[s]!.GetValue<int>());
                }
                if (pObj["buttons"] is JsonArray btnArr)
                {
                    for (int b = 0; b < Math.Min(9, btnArr.Count); b++)
                        p.SetButtonAction(b, btnArr[b]?.GetValue<string>() ?? "none");
                }
            }

            MarkModified($"Imported profiles from '{Path.GetFileName(dlg.FileName)}'.");
            RefreshSlotHeaderVisuals();
            RefreshCurrentProfileToUi();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Import failed:\n{ex.Message}", "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SlotButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out int idx))
        {
            SelectProfileSlot(idx, sendToHardware: true);
        }
    }

    private void WorkspaceTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out int tabIdx))
        {
            UpdateWorkspaceTabVisuals(tabIdx);
        }
    }

    private void UpdateWorkspaceTabVisuals(int tabIdx)
    {
        bool isHw = tabIdx == 0;
        HardwareWorkspaceGrid.Visibility = isHw ? Visibility.Visible : Visibility.Collapsed;
        DaemonWorkspaceGrid.Visibility = isHw ? Visibility.Collapsed : Visibility.Visible;

        TabHardwareBtn.Background = isHw ? (Brush)FindResource("BrushBgElevated") : Brushes.Transparent;
        TabHardwareBtn.Foreground = isHw ? (Brush)FindResource("BrushAccentCyan") : (Brush)FindResource("BrushTextSecondary");

        TabDaemonBtn.Background = !isHw ? (Brush)FindResource("BrushBgElevated") : Brushes.Transparent;
        TabDaemonBtn.Foreground = !isHw ? (Brush)FindResource("BrushAccentCyan") : (Brush)FindResource("BrushTextSecondary");
    }

    private void SaveDaemonConfig_Click(object sender, RoutedEventArgs e)
    {
        _automation.SaveConfig();
        StatusFooterText.Text = $"Saved host macro & auto-switch rules to '{_automation.ConfigFilePath}'.";
    }

    private void AutoSwitchToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_automation is null) return;
        _automation.AutoProfileSwitchEnabled = AutoSwitchToggle.IsChecked == true;
        StatusFooterText.Text = _automation.AutoProfileSwitchEnabled
            ? "Foreground application auto-profile switching enabled."
            : "Foreground application auto-profile switching paused.";
    }

    private void DefaultSlotCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_automation is null) return;
        if (DefaultSlotCombo.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out int slot))
        {
            _automation.DefaultProfileIndex = slot;
            _automation.SaveConfig();
        }
    }

    private void NewRuleExeCombo_DropDownOpened(object sender, EventArgs e)
    {
        try
        {
            var running = Process.GetProcesses()
                .Where(p => p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(p.ProcessName))
                .Select(p => $"{p.ProcessName.ToLowerInvariant()}.exe")
                .Distinct()
                .OrderBy(n => n)
                .ToList();

            NewRuleExeCombo.ItemsSource = running;
        }
        catch
        {
            // Ignore process enumeration error
        }
    }

    private void AddAppRule_Click(object sender, RoutedEventArgs e)
    {
        string exe = NewRuleExeCombo.Text.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(exe)) return;
        if (!exe.EndsWith(".exe")) exe += ".exe";

        int slot = 1;
        if (NewRuleSlotCombo.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out int parsed))
            slot = parsed;

        _automation.AppRules.RemoveAll(r => string.Equals(r.ExecutableName, exe, StringComparison.OrdinalIgnoreCase));
        _automation.AppRules.Add(new AppProfileRule { ExecutableName = exe, ProfileIndex = slot });
        _automation.SaveConfig();
        RefreshAppRulesListUi();
        StatusFooterText.Text = $"Added auto-switch rule: '{exe}' -> Slot {slot + 1}.";
    }

    private void OnHardwareEventReceived(HardwareButtonEvent evt)
    {
        Dispatcher.BeginInvoke(() =>
        {
            string stateStr = evt.IsPressed ? "PRESSED" : "RELEASED";
            TelemetryEventText.Text = $"[0x80] {evt.ActionName.ToUpperInvariant()} -> {stateStr}";

            // Refresh active state if hardware cycled profile or DPI
            if (!evt.IsPressed && _device.IsConnected)
            {
                try
                {
                    var st = _device.GetActiveState();
                    _activeDpiStageIndex = st.DpiStageIndex;
                    if (st.ProfileIndex != _activeProfileIndex)
                    {
                        SelectProfileSlot(st.ProfileIndex, sendToHardware: false);
                    }
                    else
                    {
                        RefreshCurrentProfileToUi();
                    }
                }
                catch
                {
                    // Ignore
                }
            }
        });
    }

    private void OnAutoProfileSwitched(int targetProfile, string exeName)
    {
        Dispatcher.BeginInvoke(() =>
        {
            SelectProfileSlot(targetProfile, sendToHardware: false);
            StatusFooterText.Text = $"Auto-switched to Slot {targetProfile + 1} for active process '{exeName}'.";
        });
    }

    private void OnMacroKeyTriggered(string keyName, bool isDown)
    {
        Dispatcher.BeginInvoke(() =>
        {
            string stateStr = isDown ? "TRIGGERED" : "RELEASED";
            TelemetryEventText.Text = $"[HOOK] {keyName.ToUpperInvariant()} -> {stateStr}";
        });
    }

    private void MinimizeToTray_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isExiting)
        {
            // Close button exits cleanly and disposes native hooks/tray
            _isExiting = true;
        }
        _tray.Dispose();
        _automation.Dispose();
        _device.Dispose();
    }

    private void CaptureWindowScreenshot(string outputPath)
    {
        try
        {
            UpdateLayout();
            double dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            int width = (int)(ActualWidth * dpiScale);
            int height = (int)(ActualHeight * dpiScale);

            var rtb = new RenderTargetBitmap(width, height, 96 * dpiScale, 96 * dpiScale, PixelFormats.Pbgra32);
            rtb.Render(this);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            using var fs = File.Create(outputPath);
            encoder.Save(fs);
        }
        catch
        {
            // Ignore screenshot error
        }
    }
}
