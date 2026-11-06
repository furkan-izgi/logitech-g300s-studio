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
    private const double MouseScale = 0.72;
    private const double MouseStageX = 215.0;
    private const double MouseStageY = 70.0;

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
        new(0, "G1", "Sol Tık (G1)",             59,  33, 323, 141,  14, 282, true),
        new(1, "G2", "Sağ Tık (G2)",            213,   6, 441,  78, 800, 168, false),
        new(2, "G3", "Orta Tekerlek (G3)",      241,  39, 313,  78,  14,  48, true),
        new(3, "G4", "Sol Arka (G4)",           140, 117, 216, 161,  14, 204, true),
        new(4, "G5", "Sol Ön (G5)",              66,  97, 128, 134,  14, 126, true),
        new(5, "G6", "Sağ Arka (G6)",           425,  14, 498,  53, 800, 100, false),
        new(6, "G7", "Sağ Ön (G7)",             344, -10, 405,  30, 800,  32, false),
        new(7, "G8", "Orta Arka / Mod (G8)",    390,  59, 452, 111, 800, 304, false),
        new(8, "G9", "Orta Ön / DPI (G9)",      353,  59, 406,  97, 800, 236, false)
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
    private readonly PresetTemplateService _presetService = PresetTemplateService.Instance;
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

    private System.Drawing.Icon? _wndIconBig;
    private System.Drawing.Icon? _wndIconSmall;

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
    private bool _isMicaBackdropEnabled = true;
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
    private bool _isAppInitialized;

    public MainWindow() : this(false) { }

    public MainWindow(bool startMinimized)
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

        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();

        InitializeApp(startMinimized);
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        IntPtr hwnd = helper.Handle;

        int dark = 1;
        DwmSetWindowAttribute(hwnd, 19, ref dark, sizeof(int));
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        int round = 2;
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));

        // Start with solid dark canvas during window construction for reliable layout initialization
        WindowBackdropService.ApplyBackdrop(this, WindowBackdropMode.Solid);
        ApplyBackdropVisualStyles(WindowBackdropMode.Solid);

        ApplyWindowIcons(hwnd);
    }

    private void ApplyWindowIcons(IntPtr hwnd)
    {
        try
        {
            var sri = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/g300s.ico", UriKind.Absolute));
            if (sri != null)
            {
                using var stream = sri.Stream;
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                ms.Position = 0;
                _wndIconBig = new System.Drawing.Icon(ms, 32, 32);
                ms.Position = 0;
                _wndIconSmall = new System.Drawing.Icon(ms, 16, 16);
            }
        }
        catch { }

        if (_wndIconBig == null)
        {
            string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "g300s.ico");
            if (File.Exists(iconPath))
            {
                try
                {
                    _wndIconBig = new System.Drawing.Icon(iconPath, 32, 32);
                    _wndIconSmall = new System.Drawing.Icon(iconPath, 16, 16);
                }
                catch { }
            }
        }

        if (_wndIconBig == null)
        {
            try
            {
                string? proc = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(proc) && File.Exists(proc))
                {
                    _wndIconBig = System.Drawing.Icon.ExtractAssociatedIcon(proc);
                    _wndIconSmall = _wndIconBig;
                }
            }
            catch { }
        }

        if (_wndIconBig != null)
        {
            SendMessage(hwnd, WM_SETICON, new IntPtr(ICON_BIG), _wndIconBig.Handle);
            SetClassLong(hwnd, GCLP_HICON, _wndIconBig.Handle);
        }
        if (_wndIconSmall != null)
        {
            SendMessage(hwnd, WM_SETICON, new IntPtr(ICON_SMALL), _wndIconSmall.Handle);
            SetClassLong(hwnd, GCLP_HICONSM, _wndIconSmall.Handle);
        }
    }

    private const int WM_SETICON = 0x0080;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;
    private const int GCLP_HICON = -14;
    private const int GCLP_HICONSM = -34;

    private static IntPtr SetClassLong(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
    {
        if (IntPtr.Size == 8)
            return SetClassLongPtr64(hWnd, nIndex, dwNewLong);
        return new IntPtr(SetClassLong32(hWnd, nIndex, unchecked((uint)dwNewLong.ToInt64())));
    }

    [DllImport("user32.dll", EntryPoint = "SetClassLong")]
    private static extern uint SetClassLong32(IntPtr hWnd, int nIndex, uint dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetClassLongPtr")]
    private static extern IntPtr SetClassLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

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

    private void InitializeApp(bool startMinimized)
    {
        if (_isAppInitialized) return;
        _isAppInitialized = true;

        string assetsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets");
        LoadVisualAssets(assetsDir);
        BuildOverlayCallouts();
        BuildDpiStagesUi();
        BuildLightingStudioUi();
        BuildDaemonWorkspaceUi();
        PopulatePresetCombo();
        _presetService.PresetsReloaded += () => Dispatcher.Invoke(PopulatePresetCombo);
        SetupAllCardMicroInteractions();

        if (MicaBackdropToggle is not null)
        {
            _suppressUiEvents = true;
            MicaBackdropToggle.IsChecked = _isMicaBackdropEnabled;
            _suppressUiEvents = false;

            if (_isMicaBackdropEnabled)
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
            Dispatcher.Invoke(() =>
            {
                Close();
                Application.Current.Shutdown(0);
            });
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

        // Initialize Angle Snapping Service (Hook installed dynamically only when enabled)
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
        _ = ConnectAndReadHardwareAsync();
        DeselectButton(); // Ensure nothing is selected on startup!
        SwitchMainView(0);
        SwitchDrawerCategory(0);
        _livePreviewTimer.Start();

        if (startMinimized)
        {
            Hide();
            if (StatusFooterText is not null) StatusFooterText.Text = "Windows başlangıcında sistem tepsisinde başlatıldı.";
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_isAppInitialized)
        {
            InitializeApp(false);
        }

        string[] args = Environment.GetCommandLineArgs();

        // Automated multi-view visual verification screenshot mode
        int ssIdx = Array.IndexOf(args, "--verify-screenshot");
        if (ssIdx >= 0 && ssIdx + 1 < args.Length)
        {
            string outPath = args[ssIdx + 1];
            string dir = Path.GetDirectoryName(outPath) ?? "";
            string baseName = Path.GetFileNameWithoutExtension(outPath);
            string ext = Path.GetExtension(outPath);

            // Ensure solid visual state during verification screenshot capture for pristine PNG output
            ApplyBackdropVisualStyles(WindowBackdropMode.Solid);
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

    private void ConnectAndReadHardware()
    {
        _ = ConnectAndReadHardwareAsync();
    }

    private async Task ConnectAndReadHardwareAsync()
    {
        try
        {
            var result = await Task.Run(() =>
            {
                bool connected = _device.Connect();
                bool isLgsRunning = G300sHidDevice.IsLgsRunning();
                ActiveHardwareState? state = null;
                G300sProfile[] profiles = new G300sProfile[3];

                if (connected)
                {
                    try
                    {
                        state = _device.GetActiveState();
                        for (int i = 0; i < 3; i++)
                        {
                            profiles[i] = _device.GetProfile(i);
                        }
                    }
                    catch
                    {
                        // Device might be in transition or contention
                    }
                }

                return (connected, isLgsRunning, state, profiles);
            });

            LgsWarningBadge.Visibility = result.isLgsRunning ? Visibility.Visible : Visibility.Collapsed;

            if (result.connected && result.state is not null)
            {
                _activeProfileIndex = result.state.ProfileIndex;
                _activeDpiStageIndex = result.state.DpiStageIndex;

                for (int i = 0; i < 3; i++)
                {
                    if (result.profiles[i] is not null)
                    {
                        _profiles[i] = result.profiles[i];
                    }
                }
                LoadProfileNames();

                SetConnectionStatusUi(true, "Bağlı");
                if (StatusFooterText is not null) StatusFooterText.Text = $"Logitech G300s bağlı · Aktif Profil {_profiles[_activeProfileIndex].DisplayName}, Kademe {_activeDpiStageIndex + 1}";
            }
            else
            {
                LoadProfileNames();
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
        TextBlock[] slotTexts = [SlotText0, SlotText1, SlotText2];

        for (int i = 0; i < 3; i++)
        {
            var p = _profiles[i];
            slotDots[i].Fill = (SolidColorBrush)new BrushConverter().ConvertFromString(p.ColorHex)!;
            slotTexts[i].Text = p.DisplayName;

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

        StageActiveSlotLabel.Text = $"{prof.DisplayName} · {prof.ColorName}";
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
        }

        for (int i = 0; i < PaletteSwatches.Length; i++)
        {
            var sw = PaletteSwatches[i];
            bool selected = prof.LedR == sw.R && prof.LedG == sw.G && prof.LedB == sw.B;
            if (_ledSwatchBorders[i] is not null)
            {
                _ledSwatchBorders[i].BorderBrush = selected ? (Brush)FindResource("BrushActiveWhite") : Brushes.Transparent;
            }
        }

        if (CurrentHardwareColorText is not null)
        {
            CurrentHardwareColorText.Text = prof.ColorName;
            try
            {
                CurrentHardwareColorText.Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString(prof.ColorHex)!;
            }
            catch
            {
                CurrentHardwareColorText.Foreground = (Brush)FindResource("BrushTextPrimary");
            }
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

            _ = Dispatcher.BeginInvoke(async () =>
            {
                bool wasConnected = _device.IsConnected;
                if (wasConnected)
                {
                    try
                    {
                        _ = await Task.Run(() => _device.GetActiveState());
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
                await ConnectAndReadHardwareAsync();
                if (_device.IsConnected)
                {
                    var prof = _profiles[_activeProfileIndex];
                    int dpi = prof.DpiStages[Math.Clamp(_activeDpiStageIndex, 0, 3)];
                    _osd.ShowNotification(
                        "Logitech G300s Bağlandı",
                        $"{prof.DisplayName} ({prof.ColorName}) · {dpi} DPI",
                        prof.ColorHex);
                }
            });
        }, token);
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
            shortcut.IconLocation = $"{exePath},0";
            shortcut.Save();

            if (StatusFooterText is not null) StatusFooterText.Text = "Masaüstüne 'Logitech G300s Studio' kısayolu oluşturuldu.";
            _osd.ShowNotification("Masaüstü Kısayolu Oluşturuldu", "Logitech G300s Studio masaüstüne eklendi", "#00E676");
        }
        catch (Exception ex)
        {
            if (StatusFooterText is not null) StatusFooterText.Text = $"Kısayol oluşturma hatası: {ex.Message}";
        }
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
                            $"{prof.DisplayName} · {prof.ColorName}",
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
        _wndIconBig?.Dispose();
        _wndIconSmall?.Dispose();
    }

    private void CaptureWindowScreenshot(string outputPath)
    {
        try
        {
            UpdateLayout();
            int w = (int)Math.Max(1, ActualWidth > 0 ? ActualWidth : Width);
            int h = (int)Math.Max(1, ActualHeight > 0 ? ActualHeight : Height);

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(this);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using var fs = File.Create(outputPath);
            encoder.Save(fs);
        }
        catch (Exception ex)
        {
            try { File.AppendAllText("screenshot_error.txt", $"{outputPath}: {ex}\n"); } catch { }
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

}
