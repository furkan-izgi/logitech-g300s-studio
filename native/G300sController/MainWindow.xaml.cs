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
using System.Windows.Media.Animation;
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
        new("none",      "Sabit Donanım Rengi",              "Seçili profil rengini sıfır işlemci yüküyle doğrudan fare belleğinde tutar."),
        new("spectrum",  "Pürüzsüz Spektrum Dalgası (Rainbow)", "Tüm renk tayfı arasında 60 FPS ultra akıcı gökkuşağı akışı."),
        new("breathing", "Nefes Alma (Breathing / Pulse)",   "Seçili profil rengini pürüzsüz sinüs dalgasıyla açıp karartan ritmik nefes alma efekti.")
    ];

    private readonly G300sHidDevice _device = new();
    private readonly HostAutomationService _automation;
    private readonly SystemTrayService _tray = new();
    private readonly OsdNotificationService _osd = new();
    private readonly CrosshairOverlayService _crosshair = new();
    private readonly ClickHeatmapService _heatmap = new();
    private readonly DebounceFilterService _debounce = new();
    private readonly SniperModeService _sniper = new();
    private readonly SwitchHealthService _switchHealth = new();
    private readonly AngleSnappingService _angleSnapping = new();
    private bool _fluidRgbEnabled;
    private bool _microInteractionsEnabled = true;
    private WindowBackdropMode _activeBackdropMode = WindowBackdropMode.Mica;
    private bool _gShiftVisualFeedbackEnabled = true;
    private bool _gShiftAudioFeedbackEnabled = true;
    private readonly SmartAdvisorService _advisor;
    private DispatcherTimer? _toastTimer;
    private readonly DispatcherTimer _livePreviewTimer;
    private CancellationTokenSource? _rateTestCts;
    private CancellationTokenSource? _hotplugDebounceCts;
    private bool _heatmapModeEnabled;
    private int _selectedSniperDpi = 250;

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
    private readonly Border[] _dpiStageCards = new Border[4];
    private bool _isMicaBackdropEnabled = false;
    private (byte R, byte G, byte B)? _activeCustomColor24Bit;
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
        _suppressUiEvents = true;
        _automation = new HostAutomationService(_device);
        _advisor = new SmartAdvisorService(_switchHealth, _debounce, _heatmap, _automation);
        _advisor.RecommendationsUpdated += () => Dispatcher.BeginInvoke(UpdateSmartAdvisorUi);
        InitializeComponent();
        _suppressUiEvents = false;

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
        var helper = new WindowInteropHelper(this);
        IntPtr hwnd = helper.Handle;

        int dark = 1;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        int round = 2;
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));

        // Strip WS_SYSMENU from Win32 window style to prevent DWM from rendering duplicate native caption buttons
        const int GWL_STYLE = -16;
        const int WS_SYSMENU = 0x00080000;
        int style = GetWindowLong(hwnd, GWL_STYLE);
        SetWindowLong(hwnd, GWL_STYLE, style & ~WS_SYSMENU);

        // Hook WndProc to suppress any OS non-client painting of caption buttons
        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(WndProc);

        // Apply backdrop according to user preference (defaults to Solid 100% matte carbon)
        if (_isMicaBackdropEnabled)
        {
            WindowBackdropService.ApplyBackdrop(this, _activeBackdropMode);
            ApplyBackdropVisualStyles(_activeBackdropMode);
        }
        else
        {
            WindowBackdropService.ApplyBackdrop(this, WindowBackdropMode.Solid);
            ApplyBackdropVisualStyles(WindowBackdropMode.Solid);
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_NCPAINT = 0x0085;
        const int WM_NCACTIVATE = 0x0086;

        if (msg == WM_NCPAINT)
        {
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == WM_NCACTIVATE)
        {
            handled = true;
            return new IntPtr(1);
        }

        return IntPtr.Zero;
    }

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
        SetupAllCardMicroInteractions();
        if (MicaBackdropToggle is not null)
        {
            _suppressUiEvents = true;
            MicaBackdropToggle.IsChecked = _isMicaBackdropEnabled;
            _suppressUiEvents = false;
        }

        string iconPath = Path.Combine(assetsDir, "g300s.ico");
        _tray.Initialize(this, iconPath);
        _tray.RestoreRequested += () => Dispatcher.Invoke(RestoreWindowFromExternalLaunch);
        _tray.ProfileSwitchRequested += idx => Dispatcher.Invoke(() => SelectProfileSlot(idx, sendToHardware: true));
        _tray.DpiStageSwitchRequested += st => Dispatcher.Invoke(() => ActivateDpiStage(st));
        _tray.LightingModeSwitchRequested += mode => Dispatcher.Invoke(() => SelectLightingMode(mode));
        _tray.OsdToggleRequested += () => Dispatcher.Invoke(() =>
        {
            OsdToggle.IsChecked = !(OsdToggle.IsChecked == true);
        });
        _tray.CrosshairToggleRequested += () => Dispatcher.Invoke(() =>
        {
            CrosshairToggle.IsChecked = !(CrosshairToggle.IsChecked == true);
        });
        _tray.DebounceToggleRequested += () => Dispatcher.Invoke(() =>
        {
            DebounceToggle.IsChecked = !(DebounceToggle.IsChecked == true);
        });
        _tray.SniperToggleRequested += () => Dispatcher.Invoke(() =>
        {
            SniperToggle.IsChecked = !(SniperToggle.IsChecked == true);
        });
        _tray.DeviceChangeDetected += OnUsbDeviceChangeDetected;
        _tray.ExitRequested += () =>
        {
            _isExiting = true;
            Dispatcher.Invoke(Close);
        };

        // Initialize Debounce Filter Service
        _debounce.DoubleClickFiltered += (btn, ms) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                _osd.ShowNotification("Çift Tıklama Engellendi", $"{btn} tuşunda {ms}ms kontak sıçraması engellendi", "#FFC400");
                if (DebounceStatusText is not null)
                {
                    DebounceStatusText.Text = $"Switch sekme koruması aktif · Engellenen: {_debounce.FilteredClicksCount} tık";
                }
            });
        };
        _debounce.ValidClickWithPointOccurred += (btn, ptX, ptY) =>
        {
            int bIdx = btn switch
            {
                "Left" => 0,
                "Right" => 1,
                "Middle" => 2,
                "XButton1" => 3, // G4 (Sol Üst)
                "XButton2" => 4, // G5 (Sol Alt)
                _ => -1
            };
            if (bIdx >= 0)
            {
                _heatmap.RecordScreenClick(ptX, ptY, bIdx);
                _switchHealth.RecordClick(bIdx + 1);
            }
        };
        _debounce.Start();

        // Hook physical hardware G-Key events (dpi-up, dpi-down, profile-cycle, dpi-cycle)
        _device.HardwareEventReceived += evt =>
        {
            if (evt.IsPressed)
            {
                int btnIndex = evt.ActionName switch
                {
                    "dpi-down" => 6,      // G6 (Sağ Üst)
                    "dpi-up" => 7,        // G7 (Sağ Alt)
                    "profile-cycle" => 8, // G8 (Profil)
                    "dpi-cycle" => 9,     // G9 (Mod)
                    _ => -1
                };
                if (btnIndex > 0)
                {
                    Dispatcher.BeginInvoke(() => _switchHealth.RecordClick(btnIndex));
                }
            }
        };

        // Hook macro keystroke triggers (F13..F18)
        _automation.PhysicalMacroButtonClicked += physicalBtn =>
        {
            Dispatcher.BeginInvoke(() => _switchHealth.RecordClick(physicalBtn));
        };

        // Initialize Click Heatmap Analytics Service
        _heatmap.LoadStats();
        _heatmap.ClickRecorded += (_, _) =>
        {
            Dispatcher.BeginInvoke(UpdateHeatmapHeaderStats);
        };
        UpdateHeatmapHeaderStats();

        // Initialize Switch Health Analytics & Live Mini-HUD Click Highlights
        _switchHealth.HealthUpdated += () => Dispatcher.BeginInvoke(UpdateSwitchHealthUi);
        UpdateSwitchHealthUi();

        // Hook Adaptive Chatter Protection stats
        _debounce.ChatterStatsUpdated += () => Dispatcher.BeginInvoke(UpdateChatterStatsUi);
        UpdateChatterStatsUi();

        // Hook G-Shift layer audio/visual feedback
        _automation.GShiftStateChanged += (isActive, slot) => Dispatcher.BeginInvoke(() => HandleGShiftFeedback(isActive, slot));

// Smart Advisor hooked

        // Initialize Angle Snapping Service
        _angleSnapping.Start();
        _angleSnapping.MovementProcessed += (dx, dy, snapped) =>
        {
            if (snapped)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (AngleSnappingStatusText is not null)
                        AngleSnappingStatusText.Text = $"Eksen sabitlendi: dx={dx}, dy={dy} (Düz Çizgi)";
                });
            }
        };

        StateChanged += MainWindow_StateChanged;

        // Initialize Sniper Mode Service
        _sniper.TargetSniperDpi = _selectedSniperDpi;
        _sniper.SniperStateChanged += (active, dpi) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (SniperStatusText is not null)
                {
                    SniperStatusText.Text = active
                        ? $"Sniper Modu Aktif: {dpi} DPI (Nişan Alınıyor)"
                        : $"G-Shift basıldığında hedef hassasiyeti: {_selectedSniperDpi} DPI (Hazır)";
                }
                if (active)
                {
                    _osd.ShowNotification("Sniper DPI Aktif", $"{dpi} DPI hassasiyeti", "#00D4FF");
                }
            });
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
            if (StatusFooterText is not null) StatusFooterText.Text = "Windows başlangıcında sistem tepsisinde başlatıldı.";
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

            // Capture Heatmap Analytics view
            HeatmapToggle.IsChecked = true;
            await Task.Delay(200);
            CaptureWindowScreenshot(Path.Combine(dir, $"{baseName}_heatmap{ext}"));
            HeatmapToggle.IsChecked = false;

            // Capture Switch Health view
            SwitchHealthBtn_Click(this, new RoutedEventArgs());
            await Task.Delay(200);
            CaptureWindowScreenshot(Path.Combine(dir, $"{baseName}_switchhealth{ext}"));
            CloseSwitchHealth_Click(this, new RoutedEventArgs());

            // Capture Screen Click & Dwell Heatmap view
            ScreenHeatmapBtn_Click(this, new RoutedEventArgs());
            await Task.Delay(250);
            CaptureWindowScreenshot(Path.Combine(dir, $"{baseName}_screen_heatmap{ext}"));
            CloseScreenHeatmap_Click(this, new RoutedEventArgs());

            // Capture Smart Advisor Recommendations view
            SmartAdvisorHeaderBtn_Click(this, new RoutedEventArgs());
            await Task.Delay(250);
            CaptureWindowScreenshot(Path.Combine(dir, $"{baseName}_smartadvisor{ext}"));
            CloseSmartAdvisor_Click(this, new RoutedEventArgs());

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

            SwitchMainView(4);
            await Task.Delay(250);
            CaptureWindowScreenshot(Path.Combine(dir, $"{baseName}_settings{ext}"));

            // Also copy screenshots directly into project screenshots/ directory so user can open them easily
            try
            {
                string projectScreenshotsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "screenshots");
                string fullProjDir = Path.GetFullPath(projectScreenshotsDir);
                if (!Directory.Exists(fullProjDir)) Directory.CreateDirectory(fullProjDir);
                foreach (string f in Directory.GetFiles(dir, $"{baseName}*{ext}"))
                {
                    File.Copy(f, Path.Combine(fullProjDir, Path.GetFileName(f)), true);
                }
            }
            catch {}

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

        for (int i = 0; i < PaletteSwatches.Length; i++)
        {
            var sw = PaletteSwatches[i];
            int idx = i;

            var primaryOrb = CreateColorOrb(sw.Hex, $"{sw.Name} (Ana Renk)", () => SelectLedColor(sw.R, sw.G, sw.B));
            _ledSwatchBorders[idx] = primaryOrb;
            LedSwatchesGrid.Children.Add(primaryOrb);


        }

        UpdateLightingModeSelectionVisuals("none");
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
                if (StatusFooterText is not null) StatusFooterText.Text = $"Logitech G300s bağlı · Aktif Profil {_activeProfileIndex + 1}, Kademe {_activeDpiStageIndex + 1}";
            }
            else
            {
                SetConnectionStatusUi(false, "Çevrimdışı");
                if (StatusFooterText is not null) StatusFooterText.Text = "Logitech G300s algılanmadı · Çevrimdışı düzenleme modu";
            }
        }
        catch (Exception ex)
        {
            SetConnectionStatusUi(false, "Bağlantı Hatası");
            if (StatusFooterText is not null) StatusFooterText.Text = $"Donanım okuma hatası: {ex.Message}";
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
                if (StatusFooterText is not null) StatusFooterText.Text = $"Profil {_activeProfileIndex + 1} ({prof.ColorName}) etkinleştirildi.";
            }
            catch (Exception ex)
            {
                if (StatusFooterText is not null) StatusFooterText.Text = $"Profil geçiş hatası: {ex.Message}";
            }
        }

        _automation.UpdateRgbParameters(2400, (prof.LedR, prof.LedG, prof.LedB), _secondaryBreathColor);
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
            if (_dpiStageCards[i] is not null)
            {
                _dpiStageCards[i].BorderBrush = isCurrentStage ? (Brush)FindResource("BrushAccentCyanBorder") : (Brush)FindResource("BrushBorderSubtle");
                _dpiStageCards[i].BorderThickness = new Thickness(isCurrentStage ? 2 : 1);
                _dpiStageCards[i].Background = isCurrentStage ? new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0xF0, 0xFF)) : (Brush)FindResource("BrushBgSurface1");
            }
            _dpiActivateButtons[i].Background = isCurrentStage ? (Brush)FindResource("BrushActiveWhite") : (Brush)FindResource("BrushBgSurface2");
            _dpiActivateButtons[i].Foreground = isCurrentStage ? new SolidColorBrush(Color.FromRgb(9, 9, 11)) : (Brush)FindResource("BrushTextSecondary");
            _dpiActivateButtons[i].Content = isCurrentStage ? $"Kademe {i + 1} (AKTİF)" : $"Kademe {i + 1}";

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

        _tray.UpdateTrayState(
            _activeProfileIndex,
            _activeDpiStageIndex,
            prof.DpiStages,
            prof.PollingRateHz,
            _automation.ActiveRgbEffect,
            _automation.OsdNotificationsEnabled,
            _crosshair.IsEnabled,
            _debounce.IsEnabled,
            SniperToggle?.IsChecked == true);

        if (_activeCustomColor24Bit.HasValue && _automation.ActiveRgbEffect == "none")
        {
            UpdateTintedSwoosh24Bit(_activeCustomColor24Bit.Value.R, _activeCustomColor24Bit.Value.G, _activeCustomColor24Bit.Value.B);
            if (CustomColorOrb is not null) CustomColorOrb.Background = new SolidColorBrush(Color.FromRgb(_activeCustomColor24Bit.Value.R, _activeCustomColor24Bit.Value.G, _activeCustomColor24Bit.Value.B));
            if (CustomColorHexText is not null) CustomColorHexText.Text = $"#{_activeCustomColor24Bit.Value.R:X2}{_activeCustomColor24Bit.Value.G:X2}{_activeCustomColor24Bit.Value.B:X2}";
        }
        RefreshHexInspectorUi(prof);
        if (_heatmapModeEnabled)
        {
            UpdateCalloutHeatmapVisuals();
        }

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

    private void RefreshHexInspectorUi(G300sProfile prof)
    {
        if (HexInspectorTitleText is null) return;
        byte[] raw = prof.ToRawReport();
        if (raw.Length < 35) return;

        HexInspectorTitleText.Text = $"Donanım & EEPROM Hex Müfettişi (0x{raw[0]:X2} · Profil {_activeProfileIndex + 1} · 35 Bayt)";

        var hdrSb = new StringBuilder("HDR [00..07]: ");
        for (int i = 0; i < 8; i++)
        {
            hdrSb.Append($"{raw[i]:X2} ");
        }
        hdrSb.Append($"(LED:{prof.ColorName}, {prof.PollingRateHz}Hz, Def:K{prof.DefaultDpiStage + 1})");
        HexDumpHeaderBytesText.Text = hdrSb.ToString();

        var btnSb = new StringBuilder("BTN [08..34]: ");
        for (int b = 0; b < 9; b++)
        {
            int off = 8 + b * 3;
            btnSb.Append($"G{b + 1}:{raw[off]:X2} {raw[off + 1]:X2} {raw[off + 2]:X2}");
            if (b < 8) btnSb.Append(" | ");
        }
        HexDumpButtonBytesText.Text = btnSb.ToString();
    }

    private void CopyHexDump_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 3; i++)
            {
                byte[] raw = _profiles[i].ToRawReport();
                string hex = string.Join(" ", raw.Select(b => $"{b:X2}"));
                sb.AppendLine($"Profil {i + 1} (0x{raw[0]:X2}): {hex}");
            }
            Clipboard.SetText(sb.ToString().TrimEnd());
            if (StatusFooterText is not null) StatusFooterText.Text = "Tüm 3 profilin (0xF3–0xF5) 35-bayt EEPROM hex dökümü panoya kopyalandı.";
            _osd.ShowNotification("EEPROM Hex Kopyalandı", "0xF3, 0xF4, 0xF5 (3×35 Bayt) panoda", "#00D4FF");
        }
        catch (Exception ex)
        {
            if (StatusFooterText is not null) StatusFooterText.Text = $"Pano kopyalama hatası: {ex.Message}";
        }
    }

    private void CrosshairToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents) return;
        UpdateCrosshairState();
    }

    private void CrosshairStyleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents) return;
        UpdateCrosshairState();
    }

    private void CrosshairColorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents) return;
        UpdateCrosshairState();
    }

    private void UpdateCrosshairState()
    {
        if (_suppressUiEvents || CrosshairToggle is null || _crosshair is null || _profiles is null || _tray is null || _automation is null || StatusFooterText is null) return;
        bool visible = CrosshairToggle.IsChecked == true;
        string style = (CrosshairStyleCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "cross";
        string colorHex = (CrosshairColorCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "#00E676";

        _crosshair.Configure(visible, style, colorHex);
        var prof = _profiles[_activeProfileIndex];
        _tray.UpdateTrayState(
            _activeProfileIndex,
            _activeDpiStageIndex,
            prof.DpiStages,
            prof.PollingRateHz,
            _automation.ActiveRgbEffect,
            _automation.OsdNotificationsEnabled,
            _crosshair.IsEnabled,
            _debounce.IsEnabled,
            SniperToggle?.IsChecked == true);

        if (StatusFooterText is not null) StatusFooterText.Text = visible
            ? "Oyun içi ekran nişangahı (Crosshair) etkinleştirildi."
            : "Oyun içi ekran nişangahı kapatıldı.";
    }

    private void HeatmapToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents) return;
        _heatmapModeEnabled = HeatmapToggle.IsChecked == true;
        UpdateCalloutHeatmapVisuals();
        if (StatusFooterText is not null) StatusFooterText.Text = _heatmapModeEnabled
            ? "Tuş aktivite ve tıklama ısı haritası görünümü açıldı."
            : "Standart tuş atama görünümüne dönüldü.";
    }

    private void ResetHeatmapStats_Click(object sender, RoutedEventArgs e)
    {
        _heatmap.ResetStats();
        UpdateHeatmapHeaderStats();
        UpdateCalloutHeatmapVisuals();
        if (StatusFooterText is not null) StatusFooterText.Text = "Tıklama istatistikleri ve ısı haritası sıfırlandı.";
        _osd.ShowNotification("İstatistikler Sıfırlandı", "Isı haritası ve sayaçlar temizlendi", "#00D4FF");
    }

    private void UpdateHeatmapHeaderStats()
    {
        if (HeatmapStatsText is null) return;
        long total = _heatmap.TotalClicks;
        double cpm = _heatmap.CurrentCpm;
        HeatmapStatsText.Text = $"{cpm:0} CPM · {total:N0} Tık";

        if (_heatmapModeEnabled)
        {
            UpdateCalloutHeatmapVisuals();
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
        _activeViewIndex = Math.Clamp(viewIdx, 0, 4);

        FrameworkElement[] views = [ViewAssignmentsGrid, ViewSensitivityGrid, ViewLightingGrid, ViewAutomationGrid, ViewSettingsGrid];
        for (int i = 0; i < views.Length; i++)
        {
            if (i == _activeViewIndex)
            {
                AnimateViewIn(views[i]);
            }
            else
            {
                views[i].Visibility = Visibility.Collapsed;
            }
        }

        Button[] navBtns = [NavBtnAssignments, NavBtnSensitivity, NavBtnLighting, NavBtnAutomation, NavBtnSettings];
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
        else if (_activeViewIndex == 4)
        {
            UpdateSwitchHealthUi();
        }
    }

    private void AnimateViewIn(FrameworkElement view)
    {
        view.Visibility = Visibility.Visible;
        if (!_microInteractionsEnabled)
        {
            view.Opacity = 1.0;
            return;
        }

        var tt = new TranslateTransform(0, 12);
        view.RenderTransform = tt;
        view.Opacity = 0.0;

        var animOpacity = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(160)) { DecelerationRatio = 0.8 };
        var animSlide = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(160)) { DecelerationRatio = 0.8 };
        view.BeginAnimation(UIElement.OpacityProperty, animOpacity);
        tt.BeginAnimation(TranslateTransform.YProperty, animSlide);
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
                System.Diagnostics.Debug.WriteLine($"SetActiveDpiStage: {ex.Message}");
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


    private void MarkModified(string message)
    {
        _hasUnsavedChanges = true;
        if (StatusFooterText is not null) StatusFooterText.Text = $"{message} · Kalıcı olması için 'Belleğe Kaydet' butonuna tıklayın.";
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
        if (StatusFooterText is not null) StatusFooterText.Text = "Profiller yerleşik EEPROM belleğe yazılıyor...";

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
            if (StatusFooterText is not null) StatusFooterText.Text = "Tüm 3 profil Logitech G300s belleğine kaydedildi.";
        }
        catch (Exception ex)
        {
            if (StatusFooterText is not null) StatusFooterText.Text = $"Belleğe yazma hatası: {ex.Message}";
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
            if (StatusFooterText is not null) StatusFooterText.Text = $"Profiller '{Path.GetFileName(dlg.FileName)}' dosyasına dışa aktarıldı.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"JSON dışa aktarma hatası:\n{ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveDaemonConfig_Click(object sender, RoutedEventArgs e)
    {
        _automation.SaveConfig();
        if (StatusFooterText is not null) StatusFooterText.Text = "Makro ve otomatik profil kuralları kaydedildi.";
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
        if (StatusFooterText is not null) StatusFooterText.Text = enabled
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

    private void OnUsbDeviceChangeDetected()
    {
        _hotplugDebounceCts?.Cancel();
        _hotplugDebounceCts?.Dispose();
        _hotplugDebounceCts = new CancellationTokenSource();
        CancellationToken token = _hotplugDebounceCts.Token;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(650, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _ = Dispatcher.BeginInvoke(() =>
            {
                bool wasConnected = _device.IsConnected;
                if (wasConnected)
                {
                    try
                    {
                        _ = _device.GetActiveState();
                        return; // Still connected and responsive
                    }
                    catch
                    {
                        _device.Dispose();
                        SetConnectionStatusUi(false, "Çevrimdışı");
                        if (StatusFooterText is not null) StatusFooterText.Text = "Logitech G300s USB bağlantısı kesildi · Tak-Çıkar bekleniyor...";
                        _osd.ShowNotification("Logitech G300s Çıkarıldı", "USB bağlantısı kesildi", "#FF2D46");
                        return;
                    }
                }

                // Was disconnected; attempt automatic hot-plug reconnection
                if (_device.Connect())
                {
                    ConnectAndReadHardware();
                    var prof = _profiles[_activeProfileIndex];
                    int dpi = prof.DpiStages[Math.Clamp(_activeDpiStageIndex, 0, 3)];
                    _osd.ShowNotification(
                        "Logitech G300s Bağlandı",
                        $"Profil {_activeProfileIndex + 1} ({prof.ColorName}) · {dpi} DPI",
                        prof.ColorHex);
                }
            });
        }, token);
    }

        public void ApplyPresetTemplate(string presetKey)
    {
        var prof = _profiles[_activeProfileIndex];
        string presetTitle;

        switch (presetKey.ToLowerInvariant())
        {
            case "web":
                presetTitle = "Web & Tarayıcı Gezinme";
                prof.PollingRateHz = 1000;
                prof.DefaultDpiStage = 1;
                prof.SetDpiStage(0, 750);
                prof.SetDpiStage(1, 1250);
                prof.SetDpiStage(2, 1750);
                prof.SetDpiStage(3, 2250);
                prof.SetLed(0, 1, 1); // Turkuaz Camgöbeği
                prof.SetButtonAction(0, "mouse:1"); // Sol Tık
                prof.SetButtonAction(1, "mouse:2"); // Sağ Tık
                prof.SetButtonAction(2, "mouse:3"); // Orta Tık (Yeni sekmede aç)
                prof.SetButtonAction(3, "mouse:5"); // G4: İleri (Forward)
                prof.SetButtonAction(4, "mouse:4"); // G5: Geri (Back)
                prof.SetButtonAction(5, "key:f5");  // G6: Sayfayı Yenile (Refresh F5)
                prof.SetButtonAction(6, "key:lctrl+t"); // G7: Yeni Sekme Aç (Ctrl+T)
                prof.SetButtonAction(7, "key:lctrl+w"); // G8: Sekmeyi Kapat (Ctrl+W)
                prof.SetButtonAction(8, "key:lctrl+lshift+t"); // G9: Kapatılan Sekmeyi Aç (Ctrl+Shift+T)
                break;

            case "dev":
                presetTitle = "Yazılım Geliştirme (VS Code / Terminal)";
                prof.PollingRateHz = 1000;
                prof.DefaultDpiStage = 1;
                prof.SetDpiStage(0, 800);
                prof.SetDpiStage(1, 1250);
                prof.SetDpiStage(2, 1750);
                prof.SetDpiStage(3, 2500);
                prof.SetLed(0, 0, 1); // Saf Mavi
                prof.SetButtonAction(0, "mouse:1");
                prof.SetButtonAction(1, "mouse:2");
                prof.SetButtonAction(2, "mouse:3");
                prof.SetButtonAction(3, "key:lctrl+c"); // G4: Kopyala
                prof.SetButtonAction(4, "key:lctrl+v"); // G5: Yapıştır
                prof.SetButtonAction(5, "key:lctrl+z"); // G6: Geri Al
                prof.SetButtonAction(6, "key:lctrl+y"); // G7: İleri Al
                prof.SetButtonAction(7, "key:lctrl+s"); // G8: Hızlı Kaydet
                prof.SetButtonAction(8, "key:lctrl+p"); // G9: Dosya / Komut Gezgini
                break;

            case "office":
                presetTitle = "Ofis & Üretkenlik";
                prof.PollingRateHz = 500;
                prof.DefaultDpiStage = 1;
                prof.SetDpiStage(0, 750);
                prof.SetDpiStage(1, 1250);
                prof.SetDpiStage(2, 1750);
                prof.SetDpiStage(3, 2250);
                prof.SetLed(0, 1, 0); // Zümrüt Yeşili
                prof.SetButtonAction(0, "mouse:1");
                prof.SetButtonAction(1, "mouse:2");
                prof.SetButtonAction(2, "mouse:3");
                prof.SetButtonAction(3, "mouse:4"); // G4: Geri
                prof.SetButtonAction(4, "mouse:5"); // G5: İleri
                prof.SetButtonAction(5, "key:lctrl+c"); // G6: Kopyala
                prof.SetButtonAction(6, "key:lctrl+v"); // G7: Yapıştır
                prof.SetButtonAction(7, "key:lctrl+z"); // G8: Geri Al
                prof.SetButtonAction(8, "dpi-cycle");   // G9: DPI Döngüsü
                break;

            case "fps":
                presetTitle = "FPS Rekabetçi (CS2 / Valorant)";
                prof.PollingRateHz = 1000;
                prof.DefaultDpiStage = 1;
                prof.SetDpiStage(0, 500);
                prof.SetDpiStage(1, 750);
                prof.SetDpiStage(2, 1250);
                prof.SetDpiStage(3, 1750);
                prof.SetLed(1, 0, 0); // Kırmızı
                prof.SetButtonAction(0, "mouse:1");
                prof.SetButtonAction(1, "mouse:2");
                prof.SetButtonAction(2, "mouse:3");
                prof.SetButtonAction(3, "mouse:5");
                prof.SetButtonAction(4, "mouse:4");
                prof.SetButtonAction(5, "key:f18"); // G-Shift Katmanı
                prof.SetButtonAction(6, "key:f17"); // Recoil Dengeleme
                prof.SetButtonAction(7, "dpi-cycle");
                prof.SetButtonAction(8, "dpi-alternate"); // DPI Shift (Nişan)
                break;

            case "moba":
                presetTitle = "MOBA & MMO (LoL / Dota 2)";
                prof.PollingRateHz = 1000;
                prof.DefaultDpiStage = 1;
                prof.SetDpiStage(0, 1000);
                prof.SetDpiStage(1, 1500);
                prof.SetDpiStage(2, 2000);
                prof.SetDpiStage(3, 2500);
                prof.SetLed(1, 0, 1); // Eflatun
                prof.SetButtonAction(0, "mouse:1");
                prof.SetButtonAction(1, "mouse:2");
                prof.SetButtonAction(2, "mouse:3");
                prof.SetButtonAction(3, "key:1");
                prof.SetButtonAction(4, "key:2");
                prof.SetButtonAction(5, "key:3");
                prof.SetButtonAction(6, "key:4");
                prof.SetButtonAction(7, "dpi-cycle");
                prof.SetButtonAction(8, "key:f15"); // Zamanlamalı Kombo
                break;

            case "media":
                presetTitle = "Medya & Müzik Kontrolü";
                prof.PollingRateHz = 500;
                prof.DefaultDpiStage = 1;
                prof.SetDpiStage(0, 750);
                prof.SetDpiStage(1, 1250);
                prof.SetDpiStage(2, 1750);
                prof.SetDpiStage(3, 2250);
                prof.SetLed(1, 1, 0); // Sarı
                prof.SetButtonAction(0, "mouse:1");
                prof.SetButtonAction(1, "mouse:2");
                prof.SetButtonAction(2, "mouse:3");
                prof.SetButtonAction(3, "key:right"); // İleri Sar
                prof.SetButtonAction(4, "key:left");  // Geri Sar
                prof.SetButtonAction(5, "key:space"); // Oynat / Durdur
                prof.SetButtonAction(6, "key:m");     // Sesi Kapat (Mute)
                prof.SetButtonAction(7, "key:up");    // Ses Artır
                prof.SetButtonAction(8, "key:down");  // Ses Azalt
                break;

            default:
                presetTitle = "Standart Masaüstü";
                prof.PollingRateHz = 500;
                prof.DefaultDpiStage = 1;
                prof.SetDpiStage(0, 750);
                prof.SetDpiStage(1, 1250);
                prof.SetDpiStage(2, 1750);
                prof.SetDpiStage(3, 2250);
                prof.SetLed(0, 1, 1); // Turkuaz
                prof.SetButtonAction(0, "mouse:1");
                prof.SetButtonAction(1, "mouse:2");
                prof.SetButtonAction(2, "mouse:3");
                prof.SetButtonAction(3, "mouse:5");
                prof.SetButtonAction(4, "mouse:4");
                prof.SetButtonAction(5, "key:lctrl+c");
                prof.SetButtonAction(6, "key:lctrl+v");
                prof.SetButtonAction(7, "profile-cycle");
                prof.SetButtonAction(8, "dpi-cycle");
                break;
        }

        if (_device.IsConnected && _automation.ActiveRgbEffect == "none")
        {
            try { _device.SetLiveLed(prof.LedR, prof.LedG, prof.LedB); } catch { /* ignore */ }
        }

        MarkModified($"Profil {_activeProfileIndex + 1} üzerine '{presetTitle}' şablonu yüklendi.");
        RefreshSlotHeaderVisuals();
        RefreshCurrentProfileToUi();
        ShowToast($"'{presetTitle}' şablonu uygulandı!");
    }

    private void PresetTemplateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || PresetTemplateCombo is null) return;
        if (PresetTemplateCombo.SelectedItem is not ComboBoxItem { Tag: string presetKey }) return;

        ApplyPresetTemplate(presetKey);

        _suppressUiEvents = true;
        PresetTemplateCombo.SelectedIndex = 0;
        _suppressUiEvents = false;
    }

    private void CopyProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || CopyProfileCombo is null) return;
        if (CopyProfileCombo.SelectedItem is not ComboBoxItem { Tag: string tag } || !int.TryParse(tag, out int dstSlot))
            return;

        dstSlot = Math.Clamp(dstSlot, 0, 2);
        _suppressUiEvents = true;
        CopyProfileCombo.SelectedIndex = 0;
        _suppressUiEvents = false;

        if (dstSlot == _activeProfileIndex)
        {
            if (StatusFooterText is not null) StatusFooterText.Text = $"Profil {dstSlot + 1} zaten aktif profil.";
            return;
        }

        var src = _profiles[_activeProfileIndex];
        var dst = _profiles[dstSlot];

        dst.PollingRateHz = src.PollingRateHz;
        dst.DefaultDpiStage = src.DefaultDpiStage;
        for (int i = 0; i < 4; i++)
        {
            dst.SetDpiStage(i, src.DpiStages[i]);
        }
        for (int b = 0; b < 9; b++)
        {
            dst.SetButtonAction(b, src.Buttons[b]);
        }

        MarkModified($"Profil {_activeProfileIndex + 1} ayarları → Profil {dstSlot + 1} üzerine kopyalandı.");
        RefreshSlotHeaderVisuals();
        RefreshCurrentProfileToUi();
    }

    private void CreateDesktopShortcut_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath)) return;

            string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string lnkPath = Path.Combine(desktopDir, "Logitech G300s Studio.lnk");
            string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "g300s.ico");

            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return;

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            shortcut.TargetPath = exePath;
            shortcut.WorkingDirectory = Path.GetDirectoryName(exePath);
            shortcut.Description = "Logitech G300s Studio — Donanım ve Otomasyon Kontrol Merkezi";
            if (File.Exists(iconPath))
            {
                shortcut.IconLocation = iconPath;
            }
            shortcut.Save();

            if (StatusFooterText is not null) StatusFooterText.Text = "Masaüstüne 'Logitech G300s Studio' kısayolu oluşturuldu.";
            _osd.ShowNotification("Masaüstü Kısayolu Oluşturuldu", "Logitech G300s Studio masaüstüne eklendi", "#00E676");
        }
        catch (Exception ex)
        {
            if (StatusFooterText is not null) StatusFooterText.Text = $"Kısayol oluşturma hatası: {ex.Message}";
        }
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
        if (StatusFooterText is not null) StatusFooterText.Text = $"Otomatik geçiş kuralı eklendi: {exe} → Profil {slot + 1}";
    }

    private void OnHardwareEventReceived(HardwareButtonEvent ev)
    {
        if (ev.IsPressed)
        {
            var prof = _profiles[_activeProfileIndex];
            for (int b = 0; b < 9; b++)
            {
                if (string.Equals(prof.Buttons[b], ev.ActionName, StringComparison.OrdinalIgnoreCase))
                {
                    _heatmap.RecordClick(b);
                    break;
                }
            }
        }

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
            if (StatusFooterText is not null) StatusFooterText.Text = $"Otomatik geçiş: {exeName} → Profil {newSlot + 1}";

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
        if (SniperToggle?.IsChecked == true && _device.IsConnected)
        {
            if (isActive)
            {
                _sniper.TargetSniperDpi = _selectedSniperDpi;
                _sniper.ActivateSniper(_device, _profiles[_activeProfileIndex]);
            }
            else
            {
                _sniper.DeactivateSniper(_device);
            }
        }

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
        if (StatusFooterText is not null) StatusFooterText.Text = "Sistem tepsisinde çalışıyor.";
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

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            if (e.OriginalSource is DependencyObject depObj)
            {
                var parentButton = FindVisualParent<Button>(depObj);
                if (parentButton is not null) return;
            }

            if (e.ClickCount == 2)
            {
                CaptionMaximize_Click(sender, e);
            }
            else
            {
                if (WindowState == WindowState.Maximized)
                {
                    Point mousePos = PointToScreen(e.GetPosition(this));
                    WindowState = WindowState.Normal;
                    Left = mousePos.X - (RestoreBounds.Width / 2);
                    Top = mousePos.Y - 15;
                }
                DragMove();
            }
        }
    }

    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        DependencyObject? parentObject = VisualTreeHelper.GetParent(child);
        if (parentObject == null) return null;
        if (parentObject is T parent) return parent;
        return FindVisualParent<T>(parentObject);
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
        _angleSnapping.Dispose();
        _switchHealth.SaveStats();
        _debounce.Dispose();
        _heatmap.SaveStats();
        _crosshair.Dispose();
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

    // =========================================================================
    // FEATURES: SCREEN HEATMAP, SWITCH HEALTH, ANGLE SNAPPING, DYNAMIC ACRYLIC
    // =========================================================================

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (CaptionMaximizeIcon is not null)
        {
            CaptionMaximizeIcon.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
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

            // Row for popup
            if (SwitchHealthBarsPanel is not null)
            {
                SwitchHealthBarsPanel.Children.Add(CreateSwitchHealthRow(stats, 130.0));
            }

// Switch health full view removed from Settings tab
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
        // Recursively find and attach interactions to all StudioCardBorder panels
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

        // Header always maintains a rich, solid dark matte carbon surface so native DWM buttons behind it can never show through
        var headerBrush = new SolidColorBrush(Color.FromRgb(0x0E, 0x0E, 0x11));
        Application.Current.Resources["BrushBgHeader"] = headerBrush;

        if (isTranslucent)
        {
            this.Background = Brushes.Transparent;
            if (WindowOuterBorder is not null)
            {
                WindowOuterBorder.Background = new SolidColorBrush(Color.FromArgb(0x28, 0x09, 0x09, 0x0B));
                WindowOuterBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF));
            }
            Application.Current.Resources["BrushBgCanvas"] = new SolidColorBrush(Color.FromArgb(0x28, 0x09, 0x09, 0x0B));
            Application.Current.Resources["BrushBgRail"] = new SolidColorBrush(Color.FromArgb(0x38, 0x0E, 0x0E, 0x11));
            Application.Current.Resources["BrushBgSurface1"] = new SolidColorBrush(Color.FromArgb(0x4D, 0x14, 0x14, 0x18));
            Application.Current.Resources["BrushBgSurface2"] = new SolidColorBrush(Color.FromArgb(0x60, 0x1C, 0x1C, 0x22));
            Application.Current.Resources["BrushBgElevated"] = new SolidColorBrush(Color.FromArgb(0x75, 0x26, 0x26, 0x2E));
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
            WindowBackdropService.ApplyBackdrop(this, _activeBackdropMode);
            ApplyBackdropVisualStyles(_activeBackdropMode);
            if (StatusFooterText is not null) StatusFooterText.Text = $"Windows 11 {_activeBackdropMode} yarı saydam akrilik cam uygulandı.";
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
            WindowBackdropService.ApplyBackdrop(this, _activeBackdropMode);
            ApplyBackdropVisualStyles(_activeBackdropMode);
        }
    }

    private void MicroInteractionsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents) return;
        if (MicroInteractionsToggle is null) return;
        _microInteractionsEnabled = MicroInteractionsToggle.IsChecked == true;
        if (StatusFooterText is not null) StatusFooterText.Text = _microInteractionsEnabled ? "Akıcı hover animasyonları aktif." : "Hover animasyonları devre dışı.";
    }

    private void CustomHueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressUiEvents || CustomColorOrb is null || CustomColorHexText is null) return;
        double hue = CustomHueSlider.Value;
        var (r, g, b) = HostAutomationService.HsvToRgb(hue, 1.0, 1.0);

        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        CustomColorOrb.Background = brush;
        string hex = $"#{r:X2}{g:X2}{b:X2}";
        CustomColorHexText.Text = hex;

        UpdateTintedSwoosh24Bit(r, g, b);
        if (_activeViewIndex == 2 && LiveColorPreviewOrb is not null && LiveTelemetryTitleText is not null)
        {
            LiveColorPreviewOrb.Background = brush;
            LiveTelemetryProgressFill.Background = brush;
            LiveTelemetryTitleText.Text = $"Özel Renk ({hex})";
            ActiveColorLabel.Text = hex;
        }

        var prof = _profiles[_activeProfileIndex];
        int hwR = r >= 100 ? 1 : 0;
        int hwG = g >= 100 ? 1 : 0;
        int hwB = b >= 100 ? 1 : 0;
        if (hwR == 0 && hwG == 0 && hwB == 0) { hwR = 1; hwG = 1; hwB = 1; }
        prof.SetLed(hwR, hwG, hwB);

        _automation.UpdateRgbParameters(2400, (hwR, hwG, hwB), _secondaryBreathColor, (r, g, b));
        if (_device.IsConnected && _automation.ActiveRgbEffect == "none")
        {
            try { _device.SetLiveLed(hwR, hwG, hwB); } catch { }
        }
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

        // Header: Category Badge & Title
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

        // Description
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

        // Actions: Apply and Dismiss
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
