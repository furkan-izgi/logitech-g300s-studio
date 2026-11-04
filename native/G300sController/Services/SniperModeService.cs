using System;
using System.Diagnostics;
using G300sController.Models;

namespace G300sController.Services;

/// <summary>
/// Configuration mode used to determine which DPI stage or value is used when activating Sniper Mode.
/// </summary>
public enum SniperTargetMode
{
    /// <summary>
    /// Matches the closest DPI value (e.g. 250 DPI or 500 DPI) available in the active profile's DPI stages.
    /// </summary>
    DpiValue = 0,

    /// <summary>
    /// Uses a fixed stage index (0..3).
    /// </summary>
    StageIndex = 1
}

/// <summary>
/// Thread-safe Dynamic G-Shift Sniper DPI (Sniper Mode) Service.
/// Manages temporary switching to a precision/low DPI stage while a designated
/// sniper/G-Shift button is held, and immediately restores the user's prior DPI stage upon release.
/// Seamlessly cooperates with the Logitech G300s onboard hardware state without racing.
/// </summary>
public sealed class SniperModeService
{
    private readonly object _syncLock = new();

    private bool _isSniperActive;
    private int _previousDpiStage = -1;
    private int _sniperDpiStage = -1;
    private int _previousDpi = 1000;
    private int _currentDpi = 250;
    private G300sProfile? _activeProfile;

    private int _targetSniperDpi = 250;
    private int _configuredSniperStage;
    private SniperTargetMode _targetMode = SniperTargetMode.DpiValue;

    /// <summary>
    /// Event fired whenever Sniper Mode is activated or deactivated.
    /// Arguments: (bool isActive, int dpiValue).
    /// When active: dpiValue is the active sniper DPI.
    /// When inactive: dpiValue is the restored original DPI.
    /// </summary>
    public event Action<bool, int>? SniperStateChanged;

    /// <summary>
    /// Event fired when a hardware communication error occurs during DPI switching.
    /// </summary>
    public event Action<Exception>? ErrorOccurred;

    /// <summary>
    /// Gets whether Sniper DPI mode is currently active.
    /// </summary>
    public bool IsSniperActive
    {
        get
        {
            lock (_syncLock)
            {
                return _isSniperActive;
            }
        }
    }

    /// <summary>
    /// Gets the DPI stage index (0..3) in effect prior to activating Sniper Mode.
    /// </summary>
    public int PreviousDpiStage
    {
        get
        {
            lock (_syncLock)
            {
                return _previousDpiStage;
            }
        }
    }

    /// <summary>
    /// Gets the current or target Sniper DPI stage index (0..3).
    /// </summary>
    public int SniperDpiStage
    {
        get
        {
            lock (_syncLock)
            {
                return _sniperDpiStage >= 0 ? _sniperDpiStage : _configuredSniperStage;
            }
        }
    }

    /// <summary>
    /// Gets the DPI value (in DPI units) in effect prior to sniper activation.
    /// </summary>
    public int PreviousDpi
    {
        get
        {
            lock (_syncLock)
            {
                return _previousDpi;
            }
        }
    }

    /// <summary>
    /// Gets the currently active DPI value (in DPI units).
    /// </summary>
    public int CurrentDpi
    {
        get
        {
            lock (_syncLock)
            {
                return _currentDpi;
            }
        }
    }

    /// <summary>
    /// Gets the profile associated with the current sniper session, if provided.
    /// </summary>
    public G300sProfile? ActiveProfile
    {
        get
        {
            lock (_syncLock)
            {
                return _activeProfile;
            }
        }
    }

    /// <summary>
    /// Gets or sets the target sniper DPI (250..2500, snapped to 250 steps). Default is 250 DPI.
    /// </summary>
    public int TargetSniperDpi
    {
        get
        {
            lock (_syncLock)
            {
                return _targetSniperDpi;
            }
        }
        set
        {
            lock (_syncLock)
            {
                _targetSniperDpi = Math.Clamp((int)Math.Round(value / 250.0) * 250, 250, 2500);
            }
        }
    }

    /// <summary>
    /// Gets or sets the default sniper stage index (0..3) when stage index mode is used.
    /// </summary>
    public int ConfiguredSniperStage
    {
        get
        {
            lock (_syncLock)
            {
                return _configuredSniperStage;
            }
        }
        set
        {
            lock (_syncLock)
            {
                _configuredSniperStage = Math.Clamp(value, 0, 3);
            }
        }
    }

    /// <summary>
    /// Gets or sets whether sniper stage selection uses closest DPI value or explicit stage index.
    /// </summary>
    public SniperTargetMode TargetMode
    {
        get
        {
            lock (_syncLock)
            {
                return _targetMode;
            }
        }
        set
        {
            lock (_syncLock)
            {
                _targetMode = value;
            }
        }
    }

    /// <summary>
    /// Activates Sniper Mode, switching the hardware to the specified DPI stage.
    /// Records the previous DPI stage from the device so it can be restored on release.
    /// If Sniper Mode is already active (e.g. repeated key-down events), this call is a safe no-op.
    /// </summary>
    /// <param name="device">Connected G300s HID device.</param>
    /// <param name="activeProfile">Active profile containing DPI stage tables.</param>
    /// <param name="sniperDpiStage">Target DPI stage index (0..3).</param>
    public void ActivateSniper(G300sHidDevice device, G300sProfile activeProfile, int sniperDpiStage)
    {
        ArgumentNullException.ThrowIfNull(device);

        int targetStage = Math.Clamp(sniperDpiStage, 0, 3);
        int activeDpi;

        lock (_syncLock)
        {
            if (_isSniperActive)
            {
                // Already engaged: prevent repeated key-down signals from overwriting PreviousDpiStage
                return;
            }

            _activeProfile = activeProfile;
            _sniperDpiStage = targetStage;

            int initialStage = -1;

            if (device.IsConnected)
            {
                try
                {
                    ActiveHardwareState state = device.GetActiveState();
                    initialStage = state.DpiStageIndex;
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"[SniperModeService] Could not read hardware active state: {ex.Message}");
                    ErrorOccurred?.Invoke(ex);
                }
            }

            if (initialStage is < 0 or > 3)
            {
                initialStage = activeProfile?.DefaultDpiStage ?? 1;
            }

            _previousDpiStage = initialStage;
            _previousDpi = ResolveDpiForStage(activeProfile, _previousDpiStage);
            _currentDpi = ResolveDpiForStage(activeProfile, _sniperDpiStage);
            activeDpi = _currentDpi;

            if (device.IsConnected && _previousDpiStage != _sniperDpiStage)
            {
                try
                {
                    device.SetActiveDpiStage(_sniperDpiStage);
                }
                catch (Exception ex)
                {
                    Trace.TraceError($"[SniperModeService] Failed to set hardware sniper DPI stage {_sniperDpiStage}: {ex.Message}");
                    ErrorOccurred?.Invoke(ex);
                    throw;
                }
            }

            _isSniperActive = true;
        }

        SniperStateChanged?.Invoke(true, activeDpi);
    }

    /// <summary>
    /// Activates Sniper Mode using the active profile and either the configured TargetSniperDpi or ConfiguredSniperStage.
    /// </summary>
    public void ActivateSniper(G300sHidDevice device, G300sProfile activeProfile)
    {
        int resolvedStage = ResolveSniperDpiStage(activeProfile);
        ActivateSniper(device, activeProfile, resolvedStage);
    }

    /// <summary>
    /// Activates Sniper Mode using an explicit stage index without a profile instance.
    /// </summary>
    public void ActivateSniper(G300sHidDevice device, int sniperDpiStage)
    {
        ActivateSniper(device, _activeProfile ?? new G300sProfile(), sniperDpiStage);
    }

    /// <summary>
    /// Activates Sniper Mode using configured settings.
    /// </summary>
    public void ActivateSniper(G300sHidDevice device)
    {
        int stage = _targetMode == SniperTargetMode.StageIndex
            ? _configuredSniperStage
            : ResolveSniperDpiStage(_activeProfile);

        ActivateSniper(device, _activeProfile ?? new G300sProfile(), stage);
    }

    /// <summary>
    /// Deactivates Sniper Mode immediately and restores the original DPI stage on the hardware.
    /// If Sniper Mode is not active, this call is a safe no-op.
    /// </summary>
    /// <param name="device">Connected G300s HID device.</param>
    public void DeactivateSniper(G300sHidDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        int restoreStage;
        int restoredDpi;

        lock (_syncLock)
        {
            if (!_isSniperActive)
            {
                return;
            }

            restoreStage = _previousDpiStage >= 0
                ? _previousDpiStage
                : (_activeProfile?.DefaultDpiStage ?? 1);

            restoreStage = Math.Clamp(restoreStage, 0, 3);
            restoredDpi = ResolveDpiForStage(_activeProfile, restoreStage);

            _currentDpi = restoredDpi;
            _isSniperActive = false;

            if (device.IsConnected && restoreStage != _sniperDpiStage)
            {
                try
                {
                    device.SetActiveDpiStage(restoreStage);
                }
                catch (Exception ex)
                {
                    Trace.TraceError($"[SniperModeService] Failed to restore hardware DPI stage {restoreStage}: {ex.Message}");
                    ErrorOccurred?.Invoke(ex);
                    throw;
                }
            }
        }

        SniperStateChanged?.Invoke(false, restoredDpi);
    }

    /// <summary>
    /// Toggles Sniper Mode on or off. Useful for push-to-toggle bindings.
    /// </summary>
    public void ToggleSniper(G300sHidDevice device, G300sProfile activeProfile, int sniperDpiStage)
    {
        if (IsSniperActive)
        {
            DeactivateSniper(device);
        }
        else
        {
            ActivateSniper(device, activeProfile, sniperDpiStage);
        }
    }

    /// <summary>
    /// Resolves the optimal DPI stage index (0..3) for the given profile according to the configured TargetMode.
    /// </summary>
    public int ResolveSniperDpiStage(G300sProfile? profile)
    {
        lock (_syncLock)
        {
            if (profile is null || profile.DpiStages.Length == 0)
            {
                return _configuredSniperStage;
            }

            if (_targetMode == SniperTargetMode.StageIndex)
            {
                return Math.Clamp(_configuredSniperStage, 0, profile.DpiStages.Length - 1);
            }

            int bestStage = 0;
            int minDifference = int.MaxValue;

            for (int i = 0; i < profile.DpiStages.Length; i++)
            {
                int diff = Math.Abs(profile.DpiStages[i] - _targetSniperDpi);
                if (diff < minDifference)
                {
                    minDifference = diff;
                    bestStage = i;
                }
            }

            return bestStage;
        }
    }

    /// <summary>
    /// Resolves the DPI value (in DPI units) for the given stage index in the profile.
    /// Falls back to default step scaling if profile or stages are absent.
    /// </summary>
    public static int ResolveDpiForStage(G300sProfile? profile, int stageIndex)
    {
        if (profile is not null && stageIndex >= 0 && stageIndex < profile.DpiStages.Length)
        {
            return profile.DpiStages[stageIndex];
        }

        return Math.Clamp((stageIndex + 1) * 250, 250, 2500);
    }

    /// <summary>
    /// Resets the internal sniper state without sending hardware commands.
    /// Recommended when device is disconnected or active profile is replaced.
    /// </summary>
    public void Reset()
    {
        lock (_syncLock)
        {
            _isSniperActive = false;
            _previousDpiStage = -1;
            _sniperDpiStage = -1;
            _activeProfile = null;
        }
    }
}
