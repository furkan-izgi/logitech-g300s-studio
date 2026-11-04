using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace G300sController.Services;

/// <summary>
/// Immutable snapshot representing the analytical heatmap state of a single G300s button.
/// </summary>
public sealed record ButtonHeatmapSnapshot(
    int Index,
    string ButtonName,
    long ClickCount,
    double NormalizedIntensity,
    Color HeatmapColor,
    string HexColor,
    double Cpm);

/// <summary>
/// Visualization target mode for screen heatmap rendering.
/// </summary>
public enum HeatmapVisualizationMode
{
    ScreenClicks = 0,
    CursorDwell = 1
}

/// <summary>
/// Thread-safe service for tracking button click telemetry, calculating real-time activity metrics (CPM, intensity),
/// tracking screen display click coordinates (X, Y) and cursor dwell times, generating thermal color maps,
/// and persisting analytics data for Logitech G300s Studio.
/// </summary>
public sealed class ClickHeatmapService : IDisposable
{
    public const int ButtonCount = 9;
    public const int ScreenGridColumns = 96;
    public const int ScreenGridRows = 54;

    // Heatmap color gradient stops:
    public static readonly Color IdleSlateColor = Color.FromRgb(0x18, 0x18, 0x1B);
    public static readonly Color CoolCyanColor  = Color.FromRgb(0x00, 0xD4, 0xFF);
    public static readonly Color AmberColor     = Color.FromRgb(0xFF, 0xC4, 0x00);
    public static readonly Color HotRedColor    = Color.FromRgb(0xFF, 0x2D, 0x46);

    public static readonly string DefaultStatsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LogitechG300sStudio",
        "heatmap_stats.json");

    private readonly object _syncLock = new();
    private readonly string _statsFilePath;
    private readonly long[] _clickCounts = new long[ButtonCount];
    private readonly Queue<DateTime> _recentClicks = new();
    private readonly Stopwatch _sessionStopwatch = new();

    private readonly long[,] _screenClickDensity = new long[ScreenGridColumns, ScreenGridRows];
    private readonly double[,] _screenDwellSeconds = new double[ScreenGridColumns, ScreenGridRows];
    private readonly List<(double X, double Y, int Button)> _recentScreenClicks = new();

    private System.Threading.Timer? _dwellSampleTimer;
    private TimeSpan _accumulatedDuration = TimeSpan.Zero;
    private DateTime _sessionStartTimeUtc;
    private DateTime? _lastClickTimeUtc;
    private long _totalClicks;
    private long _totalScreenClicks;
    private double _totalDwellSeconds;
    private bool _isDwellTrackingActive;

    public event Action<int, long>? ClickRecorded;
    public event Action<int, int, int>? ScreenClickRecorded;
    public event Action? StatsReset;
    public event Action? StatsSaved;
    public event Action? StatsLoaded;
    public event Action? HeatmapUpdated;

    public ClickHeatmapService(string? customStatsPath = null)
    {
        _statsFilePath = string.IsNullOrWhiteSpace(customStatsPath)
            ? DefaultStatsFilePath
            : customStatsPath;

        _sessionStartTimeUtc = DateTime.UtcNow;
        _sessionStopwatch.Start();

        StartDwellTracking();
    }

    public string StatsFilePath => _statsFilePath;

    public TimeSpan SessionDuration
    {
        get
        {
            lock (_syncLock)
            {
                return _sessionStopwatch.Elapsed + _accumulatedDuration;
            }
        }
    }

    public DateTime SessionStartTimeUtc
    {
        get
        {
            lock (_syncLock)
            {
                return _sessionStartTimeUtc;
            }
        }
    }

    public DateTime? LastClickTimeUtc
    {
        get
        {
            lock (_syncLock)
            {
                return _lastClickTimeUtc;
            }
        }
    }

    public long TotalClicks
    {
        get
        {
            lock (_syncLock)
            {
                return _totalClicks;
            }
        }
    }

    public long TotalScreenClicks
    {
        get
        {
            lock (_syncLock)
            {
                return _totalScreenClicks > 0 ? _totalScreenClicks : _totalClicks;
            }
        }
    }

    public double TotalDwellSeconds
    {
        get
        {
            lock (_syncLock)
            {
                return _totalDwellSeconds;
            }
        }
    }

    public long PeakClicks
    {
        get
        {
            lock (_syncLock)
            {
                return CalculatePeakClicksInternal();
            }
        }
    }

    public int MostActiveButtonIndex
    {
        get
        {
            lock (_syncLock)
            {
                long highest = 0;
                int bestIdx = 0;
                for (int i = 0; i < ButtonCount; i++)
                {
                    if (_clickCounts[i] > highest)
                    {
                        highest = _clickCounts[i];
                        bestIdx = i;
                    }
                }
                return bestIdx;
            }
        }
    }

    public double SessionCpm => GetSessionCpm();
    public double CurrentCpm => GetRecentCpm(TimeSpan.FromSeconds(60));

    public void StartDwellTracking()
    {
        lock (_syncLock)
        {
            if (_isDwellTrackingActive) return;
            _isDwellTrackingActive = true;

            _dwellSampleTimer = new System.Threading.Timer(_ =>
            {
                try
                {
                    if (Win32.GetCursorPos(out var pt))
                    {
                        int w = (int)SystemParameters.PrimaryScreenWidth;
                        int h = (int)SystemParameters.PrimaryScreenHeight;
                        if (w > 0 && h > 0)
                        {
                            AccumulateDwell(pt.x, pt.y, w, h, 0.1);
                        }
                    }
                }
                catch
                {
                    // Ignore transient Win32 coordinate query error
                }
            }, null, 100, 100);
        }
    }

    public void StopDwellTracking()
    {
        lock (_syncLock)
        {
            _isDwellTrackingActive = false;
            _dwellSampleTimer?.Dispose();
            _dwellSampleTimer = null;
        }
    }

    public void RecordClick(int buttonIndex)
    {
        ValidateButtonIndex(buttonIndex);
        DateTime nowUtc = DateTime.UtcNow;
        long newCount;

        lock (_syncLock)
        {
            _clickCounts[buttonIndex]++;
            _totalClicks++;
            _lastClickTimeUtc = nowUtc;
            newCount = _clickCounts[buttonIndex];

            _recentClicks.Enqueue(nowUtc);
            PruneRecentClicksInternal(nowUtc.AddMinutes(-5));
        }

        ClickRecorded?.Invoke(buttonIndex, newCount);
    }

    public void RecordScreenClick(int screenX, int screenY, int buttonIndex = 0)
    {
        if (buttonIndex < 0 || buttonIndex >= ButtonCount) buttonIndex = 0;

        int screenW = (int)SystemParameters.PrimaryScreenWidth;
        int screenH = (int)SystemParameters.PrimaryScreenHeight;
        if (screenW <= 0) screenW = 1920;
        if (screenH <= 0) screenH = 1080;

        double normX = Math.Clamp((double)screenX / screenW, 0.0, 0.9999);
        double normY = Math.Clamp((double)screenY / screenH, 0.0, 0.9999);

        int col = (int)(normX * ScreenGridColumns);
        int row = (int)(normY * ScreenGridRows);

        lock (_syncLock)
        {
            _screenClickDensity[col, row]++;
            _totalScreenClicks++;
            _recentScreenClicks.Add((normX, normY, buttonIndex));
            if (_recentScreenClicks.Count > 1200)
            {
                _recentScreenClicks.RemoveAt(0);
            }
        }

        RecordClick(buttonIndex);
        ScreenClickRecorded?.Invoke(screenX, screenY, buttonIndex);
        HeatmapUpdated?.Invoke();
    }

    public void AccumulateDwell(int screenX, int screenY, int screenW, int screenH, double seconds)
    {
        if (screenX < 0 || screenY < 0 || screenX >= screenW || screenY >= screenH) return;

        double normX = Math.Clamp((double)screenX / screenW, 0.0, 0.9999);
        double normY = Math.Clamp((double)screenY / screenH, 0.0, 0.9999);

        int col = (int)(normX * ScreenGridColumns);
        int row = (int)(normY * ScreenGridRows);

        lock (_syncLock)
        {
            _screenDwellSeconds[col, row] += seconds;
            _totalDwellSeconds += seconds;
        }
    }

    public WriteableBitmap RenderHeatmapBitmap(int width = 960, int height = 540, bool showDwell = false)
    {
        width = Math.Max(320, width);
        height = Math.Max(180, height);

        var wb = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        byte[] pixels = new byte[width * height * 4];

        double[,] gridValues = new double[ScreenGridColumns, ScreenGridRows];
        double peak = 0.0001;

        lock (_syncLock)
        {
            for (int c = 0; c < ScreenGridColumns; c++)
            {
                for (int r = 0; r < ScreenGridRows; r++)
                {
                    double v = showDwell ? _screenDwellSeconds[c, r] : _screenClickDensity[c, r];
                    gridValues[c, r] = v;
                    if (v > peak) peak = v;
                }
            }
        }

        // Render thermal density field
        for (int y = 0; y < height; y++)
        {
            double vY = (double)y / height * (ScreenGridRows - 1);
            int r0 = (int)vY;
            int r1 = Math.Min(r0 + 1, ScreenGridRows - 1);
            double fY = vY - r0;

            for (int x = 0; x < width; x++)
            {
                double vX = (double)x / width * (ScreenGridColumns - 1);
                int c0 = (int)vX;
                int c1 = Math.Min(c0 + 1, ScreenGridColumns - 1);
                double fX = vX - c0;

                // Bilinear interpolation of intensity
                double top = gridValues[c0, r0] * (1.0 - fX) + gridValues[c1, r0] * fX;
                double bot = gridValues[c0, r1] * (1.0 - fX) + gridValues[c1, r1] * fX;
                double val = top * (1.0 - fY) + bot * fY;

                double norm = Math.Clamp(val / peak, 0.0, 1.0);
                var (b, g, r, a) = ThermalMapColor(norm);

                int idx = (y * width + x) * 4;
                pixels[idx + 0] = b;
                pixels[idx + 1] = g;
                pixels[idx + 2] = r;
                pixels[idx + 3] = a;
            }
        }

        // Draw crisp radiant points for recent clicks if in click mode
        if (!showDwell)
        {
            lock (_syncLock)
            {
                foreach (var click in _recentScreenClicks)
                {
                    int cx = (int)(click.X * width);
                    int cy = (int)(click.Y * height);

                    int radius = 4;
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        int py = cy + dy;
                        if (py < 0 || py >= height) continue;

                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            int px = cx + dx;
                            if (px < 0 || px >= width) continue;

                            double distSq = dx * dx + dy * dy;
                            if (distSq <= radius * radius)
                            {
                                int pIdx = (py * width + px) * 4;
                                pixels[pIdx + 0] = 0xFF; // Cyan/White core
                                pixels[pIdx + 1] = 0xF0;
                                pixels[pIdx + 2] = 0x00;
                                pixels[pIdx + 3] = 0xE0;
                            }
                        }
                    }
                }
            }
        }

        wb.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        return wb;
    }

    private static (byte B, byte G, byte R, byte A) ThermalMapColor(double t)
    {
        if (t <= 0.02)
        {
            // Dark elegant display glass base
            return (0x17, 0x14, 0x12, 0xD0);
        }

        if (t < 0.25)
        {
            // Deep blue to cyan
            double frac = t / 0.25;
            byte r = (byte)(0 * (1 - frac) + 0 * frac);
            byte g = (byte)(60 * (1 - frac) + 212 * frac);
            byte b = (byte)(180 * (1 - frac) + 255 * frac);
            return (b, g, r, 0xF0);
        }
        else if (t < 0.55)
        {
            // Cyan to bright emerald
            double frac = (t - 0.25) / 0.30;
            byte r = (byte)(0 * (1 - frac) + 34 * frac);
            byte g = (byte)(212 * (1 - frac) + 225 * frac);
            byte b = (byte)(255 * (1 - frac) + 94 * frac);
            return (b, g, r, 0xF5);
        }
        else if (t < 0.82)
        {
            // Emerald to amber yellow
            double frac = (t - 0.55) / 0.27;
            byte r = (byte)(34 * (1 - frac) + 255 * frac);
            byte g = (byte)(225 * (1 - frac) + 196 * frac);
            byte b = (byte)(94 * (1 - frac) + 0 * frac);
            return (b, g, r, 0xFA);
        }
        else
        {
            // Amber yellow to plasma hot red
            double frac = (t - 0.82) / 0.18;
            byte r = (byte)(255 * (1 - frac) + 255 * frac);
            byte g = (byte)(196 * (1 - frac) + 45 * frac);
            byte b = (byte)(0 * (1 - frac) + 70 * frac);
            return (b, g, r, 0xFF);
        }
    }

    public (long TotalClicks, double TotalDwellSec, string TopZoneName) GetScreenSummary()
    {
        lock (_syncLock)
        {
            long leftHalf = 0;
            long rightHalf = 0;
            long topHalf = 0;
            long bottomHalf = 0;
            long centerBox = 0;

            int midCol = ScreenGridColumns / 2;
            int midRow = ScreenGridRows / 2;

            for (int c = 0; c < ScreenGridColumns; c++)
            {
                for (int r = 0; r < ScreenGridRows; r++)
                {
                    long v = _screenClickDensity[c, r];
                    if (c < midCol) leftHalf += v; else rightHalf += v;
                    if (r < midRow) topHalf += v; else bottomHalf += v;

                    if (c >= ScreenGridColumns / 4 && c <= ScreenGridColumns * 3 / 4 &&
                        r >= ScreenGridRows / 4 && r <= ScreenGridRows * 3 / 4)
                    {
                        centerBox += v;
                    }
                }
            }

            long total = leftHalf + rightHalf;
            string topZone = "Merkez / Çalışma Alanı";
            if (total > 0)
            {
                double centerPct = (double)centerBox / total;
                if (centerPct > 0.40) topZone = $"Ekran Merkezi (%{centerPct * 100:0})";
                else if (leftHalf > rightHalf * 1.4) topZone = "Sol Çalışma / Menü Bölgesi";
                else if (rightHalf > leftHalf * 1.4) topZone = "Sağ Araç Paneli Bölgesi";
                else if (bottomHalf > topHalf * 1.5) topZone = "Alt Görev Çubuğu & Alanı";
                else topZone = "Dengeli Ekran Dağılımı";
            }

            return (_totalScreenClicks > 0 ? _totalScreenClicks : _totalClicks, _totalDwellSeconds, topZone);
        }
    }

    public long GetClickCount(int buttonIndex)
    {
        ValidateButtonIndex(buttonIndex);
        lock (_syncLock)
        {
            return _clickCounts[buttonIndex];
        }
    }

    public double GetNormalizedIntensity(int buttonIndex)
    {
        ValidateButtonIndex(buttonIndex);
        lock (_syncLock)
        {
            long peak = CalculatePeakClicksInternal();
            if (peak == 0) return 0.0;
            return Math.Clamp((double)_clickCounts[buttonIndex] / peak, 0.0, 1.0);
        }
    }

    public Color GetHeatmapColor(int buttonIndex)
    {
        double intensity = GetNormalizedIntensity(buttonIndex);
        return InterpolateThermalColor(intensity);
    }

    public static Color InterpolateThermalColor(double intensity)
    {
        intensity = Math.Clamp(intensity, 0.0, 1.0);

        if (intensity < 0.33)
        {
            double f = intensity / 0.33;
            return LerpColor(IdleSlateColor, CoolCyanColor, f);
        }
        else if (intensity < 0.67)
        {
            double f = (intensity - 0.33) / 0.34;
            return LerpColor(CoolCyanColor, AmberColor, f);
        }
        else
        {
            double f = (intensity - 0.67) / 0.33;
            return LerpColor(AmberColor, HotRedColor, f);
        }
    }

    public double GetSessionCpm()
    {
        lock (_syncLock)
        {
            double minutes = (_sessionStopwatch.Elapsed + _accumulatedDuration).TotalMinutes;
            if (minutes <= 0.001) return 0.0;
            return Math.Round(_totalClicks / minutes, 1);
        }
    }

    public double GetRecentCpm(TimeSpan window)
    {
        DateTime nowUtc = DateTime.UtcNow;
        DateTime cutoffUtc = nowUtc - window;

        lock (_syncLock)
        {
            PruneRecentClicksInternal(cutoffUtc);
            double windowMinutes = window.TotalMinutes;
            if (windowMinutes <= 0.001) return 0.0;
            return Math.Round(_recentClicks.Count / windowMinutes, 1);
        }
    }

    public void ResetStats()
    {
        lock (_syncLock)
        {
            Array.Clear(_clickCounts, 0, _clickCounts.Length);
            Array.Clear(_screenClickDensity, 0, _screenClickDensity.Length);
            Array.Clear(_screenDwellSeconds, 0, _screenDwellSeconds.Length);
            _recentScreenClicks.Clear();
            _recentClicks.Clear();
            _totalClicks = 0;
            _totalScreenClicks = 0;
            _totalDwellSeconds = 0;
            _lastClickTimeUtc = null;
            _accumulatedDuration = TimeSpan.Zero;
            _sessionStartTimeUtc = DateTime.UtcNow;
            _sessionStopwatch.Restart();
        }

        SaveStats();
        StatsReset?.Invoke();
        HeatmapUpdated?.Invoke();
    }

    public void SaveStats()
    {
        try
        {
            HeatmapPersistenceDto dto;
            lock (_syncLock)
            {
                long[] copy = new long[ButtonCount];
                Array.Copy(_clickCounts, copy, ButtonCount);

                dto = new HeatmapPersistenceDto
                {
                    Version = 2,
                    SavedAtUtc = DateTime.UtcNow,
                    SessionStartTimeUtc = _sessionStartTimeUtc,
                    AccumulatedSessionSeconds = (_sessionStopwatch.Elapsed + _accumulatedDuration).TotalSeconds,
                    TotalClicks = _totalClicks,
                    ButtonClicks = copy,
                    TotalScreenClicks = _totalScreenClicks,
                    TotalDwellSeconds = _totalDwellSeconds
                };
            }

            string? dir = Path.GetDirectoryName(_statsFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_statsFilePath, json, Encoding.UTF8);
            StatsSaved?.Invoke();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClickHeatmapService] Failed to save stats to '{_statsFilePath}': {ex}");
        }
    }

    public void LoadStats()
    {
        try
        {
            if (!File.Exists(_statsFilePath)) return;

            string json = File.ReadAllText(_statsFilePath, Encoding.UTF8);
            var dto = JsonSerializer.Deserialize<HeatmapPersistenceDto>(json);
            if (dto is null) return;

            lock (_syncLock)
            {
                if (dto.ButtonClicks is not null)
                {
                    for (int i = 0; i < Math.Min(ButtonCount, dto.ButtonClicks.Length); i++)
                    {
                        _clickCounts[i] = dto.ButtonClicks[i];
                    }
                }

                _totalClicks = dto.TotalClicks;
                _totalScreenClicks = dto.TotalScreenClicks;
                _totalDwellSeconds = dto.TotalDwellSeconds;
                _accumulatedDuration = TimeSpan.FromSeconds(dto.AccumulatedSessionSeconds);
            }

            StatsLoaded?.Invoke();
            HeatmapUpdated?.Invoke();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClickHeatmapService] Failed to load stats from '{_statsFilePath}': {ex}");
        }
    }

    public void Dispose()
    {
        StopDwellTracking();
    }

    public static string GetButtonName(int buttonIndex)
    {
        ValidateButtonIndex(buttonIndex);
        return $"G{buttonIndex + 1}";
    }

    private static Color LerpColor(Color from, Color to, double factor)
    {
        factor = Math.Clamp(factor, 0.0, 1.0);
        byte r = (byte)Math.Round(from.R + ((to.R - from.R) * factor));
        byte g = (byte)Math.Round(from.G + ((to.G - from.G) * factor));
        byte b = (byte)Math.Round(from.B + ((to.B - from.B) * factor));
        return Color.FromArgb(0xFF, r, g, b);
    }

    private static void ValidateButtonIndex(int buttonIndex)
    {
        if (buttonIndex < 0 || buttonIndex >= ButtonCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(buttonIndex),
                buttonIndex,
                $"Button index must be between 0 (G1) and {ButtonCount - 1} (G9).");
        }
    }

    private long CalculatePeakClicksInternal()
    {
        long peak = 0;
        for (int i = 0; i < ButtonCount; i++)
        {
            if (_clickCounts[i] > peak)
            {
                peak = _clickCounts[i];
            }
        }
        return peak;
    }

    private void PruneRecentClicksInternal(DateTime cutoffUtc)
    {
        while (_recentClicks.Count > 0 && _recentClicks.Peek() < cutoffUtc)
        {
            _recentClicks.Dequeue();
        }
    }

    private static class Win32
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int x;
            public int y;
        }
    }

    private sealed class HeatmapPersistenceDto
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 2;

        [JsonPropertyName("saved_at_utc")]
        public DateTime SavedAtUtc { get; set; }

        [JsonPropertyName("session_start_time_utc")]
        public DateTime SessionStartTimeUtc { get; set; }

        [JsonPropertyName("accumulated_session_seconds")]
        public double AccumulatedSessionSeconds { get; set; }

        [JsonPropertyName("total_clicks")]
        public long TotalClicks { get; set; }

        [JsonPropertyName("button_clicks")]
        public long[] ButtonClicks { get; set; } = [];

        [JsonPropertyName("total_screen_clicks")]
        public long TotalScreenClicks { get; set; }

        [JsonPropertyName("total_dwell_seconds")]
        public double TotalDwellSeconds { get; set; }
    }
}
