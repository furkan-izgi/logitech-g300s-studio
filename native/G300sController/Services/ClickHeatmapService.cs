using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

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
/// Thread-safe service for tracking button click telemetry, calculating real-time activity metrics (CPM, intensity),
/// generating 4-stop thermal color gradients, and persisting analytics data for all 9 buttons (G1–G9) of the Logitech G300s.
/// </summary>
public sealed class ClickHeatmapService
{
    public const int ButtonCount = 9;

    // Heatmap color gradient stops:
    // 0.00: Idle Slate  (#18181B)
    // 0.33: Cool Cyan   (#00D4FF)
    // 0.67: Amber       (#FFC400)
    // 1.00: Hot Red     (#FF2D46)
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

    private TimeSpan _accumulatedDuration = TimeSpan.Zero;
    private DateTime _sessionStartTimeUtc;
    private DateTime? _lastClickTimeUtc;
    private long _totalClicks;

    /// <summary>
    /// Event triggered whenever a button click is recorded. (buttonIndex, newCount)
    /// </summary>
    public event Action<int, long>? ClickRecorded;

    /// <summary>
    /// Event triggered when statistics are reset.
    /// </summary>
    public event Action? StatsReset;

    /// <summary>
    /// Event triggered when statistics are saved to disk.
    /// </summary>
    public event Action? StatsSaved;

    /// <summary>
    /// Event triggered when statistics are loaded from disk.
    /// </summary>
    public event Action? StatsLoaded;

    /// <summary>
    /// Initializes a new instance of <see cref="ClickHeatmapService"/>.
    /// </summary>
    /// <param name="customStatsPath">Optional custom path for persisting JSON statistics. Defaults to AppData/Local/LogitechG300sStudio/heatmap_stats.json.</param>
    public ClickHeatmapService(string? customStatsPath = null)
    {
        _statsFilePath = string.IsNullOrWhiteSpace(customStatsPath)
            ? DefaultStatsFilePath
            : customStatsPath;

        _sessionStartTimeUtc = DateTime.UtcNow;
        _sessionStopwatch.Start();
    }

    /// <summary>
    /// Gets the absolute file path where heatmap statistics are stored.
    /// </summary>
    public string StatsFilePath => _statsFilePath;

    /// <summary>
    /// Total duration of the active tracking session including previous persisted sessions.
    /// </summary>
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

    /// <summary>
    /// UTC timestamp indicating when the current session tracking started.
    /// </summary>
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

    /// <summary>
    /// UTC timestamp of the most recent click recorded, or null if no clicks have occurred.
    /// </summary>
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

    /// <summary>
    /// Total number of clicks recorded across all 9 buttons.
    /// </summary>
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

    /// <summary>
    /// Highest click count recorded on any single button.
    /// </summary>
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

    /// <summary>
    /// Button index (0 to 8) that has received the most clicks, or 0 if all are 0.
    /// </summary>
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

    /// <summary>
    /// Overall session clicks-per-minute (total clicks / total session minutes).
    /// </summary>
    public double SessionCpm => GetSessionCpm();

    /// <summary>
    /// Real-time rolling clicks-per-minute over the last 60 seconds.
    /// </summary>
    public double CurrentCpm => GetRecentCpm(TimeSpan.FromSeconds(60));

    /// <summary>
    /// Records a single click for the specified button index (0 for G1 to 8 for G9).
    /// </summary>
    /// <param name="buttonIndex">0-based button index (0 = G1, 1 = G2, ..., 8 = G9).</param>
    public void RecordClick(int buttonIndex)
    {
        ValidateButtonIndex(buttonIndex);

        long newCount;
        lock (_syncLock)
        {
            if (!_sessionStopwatch.IsRunning)
            {
                _sessionStopwatch.Start();
            }

            newCount = ++_clickCounts[buttonIndex];
            _totalClicks++;

            DateTime now = DateTime.UtcNow;
            _lastClickTimeUtc = now;
            _recentClicks.Enqueue(now);

            PruneRecentClicksInternal(now - TimeSpan.FromMinutes(2));
        }

        ClickRecorded?.Invoke(buttonIndex, newCount);
    }

    /// <summary>
    /// Records a single click using a button name label (e.g. "G1" through "G9").
    /// </summary>
    /// <param name="buttonName">Button label, e.g. "G1", "g2", "G9".</param>
    public void RecordClick(string buttonName)
    {
        int index = ParseButtonNameToIndex(buttonName);
        RecordClick(index);
    }

    /// <summary>
    /// Returns the total click count for the given button index.
    /// </summary>
    /// <param name="buttonIndex">0-based button index (0 to 8).</param>
    public long GetClickCount(int buttonIndex)
    {
        ValidateButtonIndex(buttonIndex);
        lock (_syncLock)
        {
            return _clickCounts[buttonIndex];
        }
    }

    /// <summary>
    /// Returns the total click count using a button name label.
    /// </summary>
    /// <param name="buttonName">Button label, e.g. "G1".</param>
    public long GetClickCount(string buttonName)
    {
        int index = ParseButtonNameToIndex(buttonName);
        return GetClickCount(index);
    }

    /// <summary>
    /// Returns a normalized activity intensity score between 0.0 and 1.0 relative to the peak clicks across all buttons.
    /// If no clicks have been recorded anywhere, returns 0.0.
    /// </summary>
    /// <param name="buttonIndex">0-based button index (0 to 8).</param>
    public double GetNormalizedIntensity(int buttonIndex)
    {
        ValidateButtonIndex(buttonIndex);
        lock (_syncLock)
        {
            long peak = CalculatePeakClicksInternal();
            if (peak <= 0)
            {
                return 0.0;
            }

            double intensity = (double)_clickCounts[buttonIndex] / peak;
            return Math.Clamp(intensity, 0.0, 1.0);
        }
    }

    /// <summary>
    /// Returns a normalized activity intensity score between 0.0 and 1.0 using a button name label.
    /// </summary>
    /// <param name="buttonName">Button label, e.g. "G1".</param>
    public double GetNormalizedIntensity(string buttonName)
    {
        int index = ParseButtonNameToIndex(buttonName);
        return GetNormalizedIntensity(index);
    }

    /// <summary>
    /// Calculates the heatmap color for the specified button index by interpolating across the 4-stop thermal palette:
    /// Idle Slate (#18181B) -> Cool Cyan (#00D4FF) -> Amber (#FFC400) -> Hot Red (#FF2D46).
    /// </summary>
    /// <param name="buttonIndex">0-based button index (0 to 8).</param>
    public Color GetHeatmapColor(int buttonIndex)
    {
        double intensity = GetNormalizedIntensity(buttonIndex);
        return InterpolateHeatmapColor(intensity);
    }

    /// <summary>
    /// Calculates the heatmap color using a button name label.
    /// </summary>
    /// <param name="buttonName">Button label, e.g. "G1".</param>
    public Color GetHeatmapColor(string buttonName)
    {
        int index = ParseButtonNameToIndex(buttonName);
        return GetHeatmapColor(index);
    }

    /// <summary>
    /// Returns the hex color string (e.g. "#00D4FF") representing the button's heatmap intensity.
    /// </summary>
    /// <param name="buttonIndex">0-based button index (0 to 8).</param>
    public string GetHeatmapColorHex(int buttonIndex)
    {
        Color color = GetHeatmapColor(buttonIndex);
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    /// <summary>
    /// Returns the overall session clicks-per-minute (total clicks / total session minutes).
    /// </summary>
    public double GetSessionCpm()
    {
        lock (_syncLock)
        {
            TimeSpan duration = _sessionStopwatch.Elapsed + _accumulatedDuration;
            double minutes = duration.TotalMinutes;
            if (minutes <= 0.0 || _totalClicks <= 0)
            {
                return 0.0;
            }

            return Math.Round(_totalClicks / minutes, 2);
        }
    }

    /// <summary>
    /// Calculates the rolling clicks-per-minute over the specified recent time window.
    /// </summary>
    /// <param name="window">Rolling window span (defaults to 60 seconds if null).</param>
    public double GetRecentCpm(TimeSpan? window = null)
    {
        TimeSpan effectiveWindow = window ?? TimeSpan.FromSeconds(60);
        if (effectiveWindow <= TimeSpan.Zero)
        {
            return 0.0;
        }

        DateTime cutoff = DateTime.UtcNow - effectiveWindow;
        lock (_syncLock)
        {
            PruneRecentClicksInternal(cutoff);
            if (_recentClicks.Count == 0)
            {
                return 0.0;
            }

            double minutes = effectiveWindow.TotalMinutes;
            if (minutes <= 0.0)
            {
                return 0.0;
            }

            return Math.Round(_recentClicks.Count / minutes, 2);
        }
    }

    /// <summary>
    /// Returns the average clicks-per-minute for a specific button over the session duration.
    /// </summary>
    /// <param name="buttonIndex">0-based button index (0 to 8).</param>
    public double GetButtonCpm(int buttonIndex)
    {
        ValidateButtonIndex(buttonIndex);
        lock (_syncLock)
        {
            TimeSpan duration = _sessionStopwatch.Elapsed + _accumulatedDuration;
            double minutes = duration.TotalMinutes;
            if (minutes <= 0.0 || _clickCounts[buttonIndex] <= 0)
            {
                return 0.0;
            }

            return Math.Round(_clickCounts[buttonIndex] / minutes, 2);
        }
    }

    /// <summary>
    /// Returns a copy of all 9 button click counts.
    /// </summary>
    public long[] GetAllClickCounts()
    {
        lock (_syncLock)
        {
            return (long[])_clickCounts.Clone();
        }
    }

    /// <summary>
    /// Captures a complete analytical snapshot of all 9 buttons.
    /// </summary>
    public IReadOnlyList<ButtonHeatmapSnapshot> GetAllSnapshots()
    {
        var list = new List<ButtonHeatmapSnapshot>(ButtonCount);
        for (int i = 0; i < ButtonCount; i++)
        {
            list.Add(GetSnapshot(i));
        }
        return list.AsReadOnly();
    }

    /// <summary>
    /// Captures a complete analytical snapshot of a specific button.
    /// </summary>
    /// <param name="buttonIndex">0-based button index (0 to 8).</param>
    public ButtonHeatmapSnapshot GetSnapshot(int buttonIndex)
    {
        ValidateButtonIndex(buttonIndex);
        long count = GetClickCount(buttonIndex);
        double intensity = GetNormalizedIntensity(buttonIndex);
        Color color = GetHeatmapColor(buttonIndex);
        string hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        double cpm = GetButtonCpm(buttonIndex);
        string name = GetButtonName(buttonIndex);

        return new ButtonHeatmapSnapshot(buttonIndex, name, count, intensity, color, hex, cpm);
    }

    /// <summary>
    /// Resets all button activity counters, recent click queues, and session duration to zero.
    /// </summary>
    public void ResetStats()
    {
        lock (_syncLock)
        {
            Array.Clear(_clickCounts, 0, _clickCounts.Length);
            _recentClicks.Clear();
            _totalClicks = 0;
            _lastClickTimeUtc = null;
            _accumulatedDuration = TimeSpan.Zero;
            _sessionStartTimeUtc = DateTime.UtcNow;
            _sessionStopwatch.Restart();
        }

        StatsReset?.Invoke();
    }

    /// <summary>
    /// Persists current analytics state into the JSON storage file atomically.
    /// </summary>
    public void SaveStats()
    {
        string? directory = Path.GetDirectoryName(_statsFilePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        HeatmapPersistenceDto dto;
        lock (_syncLock)
        {
            TimeSpan currentDuration = _sessionStopwatch.Elapsed + _accumulatedDuration;
            dto = new HeatmapPersistenceDto
            {
                Version = 1,
                SavedAtUtc = DateTime.UtcNow,
                SessionStartTimeUtc = _sessionStartTimeUtc,
                AccumulatedSessionSeconds = Math.Round(currentDuration.TotalSeconds, 2),
                TotalClicks = _totalClicks,
                ButtonClicks = (long[])_clickCounts.Clone()
            };
        }

        string tempFilePath = $"{_statsFilePath}.tmp.{Guid.NewGuid():N}";
        try
        {
            string json = JsonSerializer.Serialize(dto, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(tempFilePath, json, Encoding.UTF8);
            File.Move(tempFilePath, _statsFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClickHeatmapService] Failed to save stats to '{_statsFilePath}': {ex}");
            if (File.Exists(tempFilePath))
            {
                try { File.Delete(tempFilePath); } catch { /* best effort cleanup */ }
            }
            throw;
        }

        StatsSaved?.Invoke();
    }

    /// <summary>
    /// Loads persisted analytics from the JSON storage file, restoring button click counts and session duration.
    /// If the file does not exist, the operation completes without error.
    /// </summary>
    public void LoadStats()
    {
        if (!File.Exists(_statsFilePath))
        {
            return;
        }

        try
        {
            string json = File.ReadAllText(_statsFilePath, Encoding.UTF8);
            var dto = JsonSerializer.Deserialize<HeatmapPersistenceDto>(json);
            if (dto is null)
            {
                return;
            }

            lock (_syncLock)
            {
                if (dto.ButtonClicks is { Length: > 0 })
                {
                    int maxButtons = Math.Min(dto.ButtonClicks.Length, ButtonCount);
                    for (int i = 0; i < maxButtons; i++)
                    {
                        _clickCounts[i] = Math.Max(0, dto.ButtonClicks[i]);
                    }
                }

                _accumulatedDuration = TimeSpan.FromSeconds(Math.Max(0.0, dto.AccumulatedSessionSeconds));
                _sessionStartTimeUtc = dto.SessionStartTimeUtc != default ? dto.SessionStartTimeUtc : DateTime.UtcNow;
                _sessionStopwatch.Restart();

                long sum = 0;
                for (int i = 0; i < ButtonCount; i++)
                {
                    sum += _clickCounts[i];
                }
                _totalClicks = sum;
                _recentClicks.Clear();
            }

            StatsLoaded?.Invoke();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClickHeatmapService] Failed to load stats from '{_statsFilePath}': {ex}");
            throw;
        }
    }

    /// <summary>
    /// Translates a 0.0 to 1.0 normalized intensity into a 4-stop thermal color gradient.
    /// 0.00 -> Idle Slate  (#18181B)
    /// 0.33 -> Cool Cyan   (#00D4FF)
    /// 0.67 -> Amber       (#FFC400)
    /// 1.00 -> Hot Red     (#FF2D46)
    /// </summary>
    /// <param name="intensity">Normalized intensity factor (0.0 to 1.0).</param>
    public static Color InterpolateHeatmapColor(double intensity)
    {
        intensity = Math.Clamp(intensity, 0.0, 1.0);
        if (intensity <= 0.0)
        {
            return IdleSlateColor;
        }
        if (intensity >= 1.0)
        {
            return HotRedColor;
        }

        const double stop1 = 1.0 / 3.0; // ~0.3333
        const double stop2 = 2.0 / 3.0; // ~0.6667

        if (intensity < stop1)
        {
            double factor = intensity / stop1;
            return LerpColor(IdleSlateColor, CoolCyanColor, factor);
        }

        if (intensity < stop2)
        {
            double factor = (intensity - stop1) / (stop2 - stop1);
            return LerpColor(CoolCyanColor, AmberColor, factor);
        }

        {
            double factor = (intensity - stop2) / (1.0 - stop2);
            return LerpColor(AmberColor, HotRedColor, factor);
        }
    }

    /// <summary>
    /// Converts a button index (0–8) to its canonical hardware label ("G1"–"G9").
    /// </summary>
    public static string GetButtonName(int buttonIndex)
    {
        ValidateButtonIndex(buttonIndex);
        return $"G{buttonIndex + 1}";
    }

    /// <summary>
    /// Parses a button label ("G1"–"G9") to its 0-based button index (0–8).
    /// </summary>
    public static int ParseButtonNameToIndex(string buttonName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buttonName);
        string trimmed = buttonName.Trim().ToUpperInvariant();
        if (trimmed.StartsWith('G') && int.TryParse(trimmed.AsSpan(1), out int number))
        {
            int index = number - 1;
            if (index >= 0 && index < ButtonCount)
            {
                return index;
            }
        }

        throw new ArgumentException($"Invalid button name '{buttonName}'. Expected 'G1' through 'G9'.", nameof(buttonName));
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

    private sealed class HeatmapPersistenceDto
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

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
    }
}
