using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace G300sController.Models;

public sealed class ActiveHardwareState
{
    public int ProfileIndex { get; init; }
    public int DpiStageIndex { get; init; }
    public bool IsFlashBusy { get; init; }
    public bool ConfigMode { get; init; }
    public byte RawFlags { get; init; }
}

public sealed class HardwareButtonEvent
{
    public byte ActionCode { get; init; }
    public string ActionName { get; init; } = string.Empty;
    public bool IsPressed { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
}

public sealed class G300sProfile : INotifyPropertyChanged
{
    private int _index;
    private int _ledR = 0;
    private int _ledG = 1;
    private int _ledB = 1;
    private int _pollingRateHz = 1000;
    private int _defaultDpiStage = 1;
    private byte _raw7 = 0x40;
    private readonly int[] _dpiStages = [500, 1000, 1500, 2500];
    private readonly string[] _buttons =
    [
        "mouse:1",
        "mouse:2",
        "mouse:3",
        "mouse:4",
        "mouse:5",
        "dpi-down",
        "dpi-up",
        "profile-cycle",
        "dpi-cycle"
    ];

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index
    {
        get => _index;
        set { _index = value; OnPropertyChanged(); }
    }

    public int LedR
    {
        get => _ledR;
        set { _ledR = value != 0 ? 1 : 0; OnPropertyChanged(); OnPropertyChanged(nameof(ColorName)); OnPropertyChanged(nameof(ColorHex)); }
    }

    public int LedG
    {
        get => _ledG;
        set { _ledG = value != 0 ? 1 : 0; OnPropertyChanged(); OnPropertyChanged(nameof(ColorName)); OnPropertyChanged(nameof(ColorHex)); }
    }

    public int LedB
    {
        get => _ledB;
        set { _ledB = value != 0 ? 1 : 0; OnPropertyChanged(); OnPropertyChanged(nameof(ColorName)); OnPropertyChanged(nameof(ColorHex)); }
    }

    public int PollingRateHz
    {
        get => _pollingRateHz;
        set { _pollingRateHz = value; OnPropertyChanged(); }
    }

    public int DefaultDpiStage
    {
        get => _defaultDpiStage;
        set { _defaultDpiStage = Math.Clamp(value, 0, 3); OnPropertyChanged(); }
    }

    public byte Raw7
    {
        get => _raw7;
        set { _raw7 = value; OnPropertyChanged(); }
    }

    public int[] DpiStages => _dpiStages;
    public string[] Buttons => _buttons;

    [JsonIgnore]
    public string ColorName => G300sProtocol.GetLedColorName(_ledR, _ledG, _ledB);

    [JsonIgnore]
    public string ColorHex => G300sProtocol.GetLedColorHex(_ledR, _ledG, _ledB);

    public void SetLed(int r, int g, int b)
    {
        _ledR = r != 0 ? 1 : 0;
        _ledG = g != 0 ? 1 : 0;
        _ledB = b != 0 ? 1 : 0;
        OnPropertyChanged(nameof(LedR));
        OnPropertyChanged(nameof(LedG));
        OnPropertyChanged(nameof(LedB));
        OnPropertyChanged(nameof(ColorName));
        OnPropertyChanged(nameof(ColorHex));
    }

    public void SetDpiStage(int stageIndex, int dpiValue)
    {
        if (stageIndex is < 0 or > 3) return;
        int snapped = Math.Clamp((int)Math.Round(dpiValue / 250.0) * 250, 250, 2500);
        _dpiStages[stageIndex] = snapped;
        OnPropertyChanged(nameof(DpiStages));
    }

    public void SetButtonAction(int buttonIndex, string actionSpec)
    {
        if (buttonIndex is < 0 or > 8) return;
        _buttons[buttonIndex] = actionSpec;
        OnPropertyChanged(nameof(Buttons));
    }

    public G300sProfile Clone()
    {
        var copy = new G300sProfile
        {
            Index = _index,
            LedR = _ledR,
            LedG = _ledG,
            LedB = _ledB,
            PollingRateHz = _pollingRateHz,
            DefaultDpiStage = _defaultDpiStage,
            Raw7 = _raw7
        };
        for (int i = 0; i < 4; i++) copy._dpiStages[i] = _dpiStages[i];
        for (int i = 0; i < 9; i++) copy._buttons[i] = _buttons[i];
        return copy;
    }

    public byte[] ToRawReport() => G300sProtocol.SerializeProfile(this);

    public static G300sProfile FromRawReport(byte[] raw, int profileIndex) => G300sProtocol.ParseProfile(raw, profileIndex);

    private void OnPropertyChanged([CallerMemberName] string? propName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
}

public static class G300sProtocol
{
    public const ushort VendorId = 0x046D;
    public const ushort ProductId = 0xC246;
    public const ushort VendorUsagePage = 0xFF80;
    public const ushort VendorUsage = 0x0080;

    public const byte ReportInputEvent = 0x80;
    public const byte ReportActiveState = 0xF0;
    public const byte ReportLiveLed = 0xF1;
    public const byte ReportLiveRate = 0xF2;
    public const byte ReportProfile0 = 0xF3;
    public const byte ReportProfile1 = 0xF4;
    public const byte ReportProfile2 = 0xF5;

    public static readonly byte[] ProfileReportIds = [ReportProfile0, ReportProfile1, ReportProfile2];

    public static readonly IReadOnlyDictionary<byte, int> CodeToRateHz = new Dictionary<byte, int>
    {
        [0] = 1000,
        [1] = 125,
        [2] = 250,
        [3] = 500
    };

    public static readonly IReadOnlyDictionary<int, byte> RateHzToCode = new Dictionary<int, byte>
    {
        [1000] = 0,
        [125] = 1,
        [250] = 2,
        [500] = 3
    };

    public static readonly IReadOnlyDictionary<byte, string> SpecialActions = new Dictionary<byte, string>
    {
        [0x0A] = "dpi-up",
        [0x0B] = "dpi-down",
        [0x0C] = "dpi-cycle",
        [0x0D] = "profile-cycle",
        [0x0E] = "dpi-alternate",
        [0x0F] = "dpi-default"
    };

    public static readonly IReadOnlyDictionary<string, byte> ActionToSpecialCode =
        SpecialActions.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    public static readonly IReadOnlyDictionary<string, byte> HidKeyCodes = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = 0x04, ["b"] = 0x05, ["c"] = 0x06, ["d"] = 0x07, ["e"] = 0x08, ["f"] = 0x09,
        ["g"] = 0x0A, ["h"] = 0x0B, ["i"] = 0x0C, ["j"] = 0x0D, ["k"] = 0x0E, ["l"] = 0x0F,
        ["m"] = 0x10, ["n"] = 0x11, ["o"] = 0x12, ["p"] = 0x13, ["q"] = 0x14, ["r"] = 0x15,
        ["s"] = 0x16, ["t"] = 0x17, ["u"] = 0x18, ["v"] = 0x19, ["w"] = 0x1A, ["x"] = 0x1B,
        ["y"] = 0x1C, ["z"] = 0x1D,
        ["1"] = 0x1E, ["2"] = 0x1F, ["3"] = 0x20, ["4"] = 0x21, ["5"] = 0x22,
        ["6"] = 0x23, ["7"] = 0x24, ["8"] = 0x25, ["9"] = 0x26, ["0"] = 0x27,
        ["enter"] = 0x28, ["esc"] = 0x29, ["backspace"] = 0x2A, ["tab"] = 0x2B, ["space"] = 0x2C,
        ["minus"] = 0x2D, ["equal"] = 0x2E, ["lbracket"] = 0x2F, ["rbracket"] = 0x30,
        ["backslash"] = 0x31, ["semicolon"] = 0x33, ["quote"] = 0x34, ["grave"] = 0x35,
        ["comma"] = 0x36, ["period"] = 0x37, ["slash"] = 0x38, ["capslock"] = 0x39,
        ["f1"] = 0x3A, ["f2"] = 0x3B, ["f3"] = 0x3C, ["f4"] = 0x3D, ["f5"] = 0x3E, ["f6"] = 0x3F,
        ["f7"] = 0x40, ["f8"] = 0x41, ["f9"] = 0x42, ["f10"] = 0x43, ["f11"] = 0x44, ["f12"] = 0x45,
        ["prtsc"] = 0x46, ["scrolllock"] = 0x47, ["pause"] = 0x48, ["ins"] = 0x49, ["home"] = 0x4A,
        ["pgup"] = 0x4B, ["del"] = 0x4C, ["end"] = 0x4D, ["pgdn"] = 0x4E,
        ["right"] = 0x4F, ["left"] = 0x50, ["down"] = 0x51, ["up"] = 0x52,
        ["numlock"] = 0x53, ["kp_slash"] = 0x54, ["kp_star"] = 0x55, ["kp_minus"] = 0x56,
        ["kp_plus"] = 0x57, ["kp_enter"] = 0x58,
        ["kp_1"] = 0x59, ["kp_2"] = 0x5A, ["kp_3"] = 0x5B, ["kp_4"] = 0x5C, ["kp_5"] = 0x5D,
        ["kp_6"] = 0x5E, ["kp_7"] = 0x5F, ["kp_8"] = 0x60, ["kp_9"] = 0x61, ["kp_0"] = 0x62,
        ["kp_dot"] = 0x63, ["menu"] = 0x65,
        ["f13"] = 0x68, ["f14"] = 0x69, ["f15"] = 0x6A, ["f16"] = 0x6B,
        ["f17"] = 0x6C, ["f18"] = 0x6D, ["f19"] = 0x6E, ["f20"] = 0x6F,
        ["f21"] = 0x70, ["f22"] = 0x71, ["f23"] = 0x72, ["f24"] = 0x73,
        ["mute"] = 0x7F, ["volup"] = 0x80, ["voldown"] = 0x81
    };

    public static readonly IReadOnlyDictionary<byte, string> HidCodeToName =
        HidKeyCodes.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);

    public static ActiveHardwareState ParseF0State(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < 4 || buf[0] != ReportActiveState)
            throw new ArgumentException("Invalid 0xF0 state buffer.");

        byte flags = buf[1];
        return new ActiveHardwareState
        {
            ProfileIndex = Math.Clamp((flags >> 4) & 0x0F, 0, 2),
            DpiStageIndex = (flags >> 1) & 0x03,
            IsFlashBusy = (flags & 0x08) != 0,
            ConfigMode = (flags & 0x01) != 0,
            RawFlags = flags
        };
    }

    public static byte[] MakeSetActiveProfileCmd(int profileIndex)
    {
        if (profileIndex is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(profileIndex));
        return [ReportActiveState, (byte)(0x80 | ((profileIndex & 0x0F) << 4)), 0x00, 0x00];
    }

    public static byte[] MakeSetDpiStageCmd(int dpiStageIndex)
    {
        if (dpiStageIndex is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(dpiStageIndex));
        return [ReportActiveState, (byte)(0x40 | ((dpiStageIndex & 0x03) << 1)), 0x00, 0x00];
    }

    public static (int R, int G, int B) ParseF1Led(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < 2 || buf[0] != ReportLiveLed)
            throw new ArgumentException("Invalid 0xF1 buffer.");
        byte v = buf[1];
        return (v & 1, (v >> 1) & 1, (v >> 2) & 1);
    }

    public static byte[] MakeF1LedCmd(int r, int g, int b)
    {
        byte mask = (byte)((r != 0 ? 1 : 0) | ((g != 0 ? 1 : 0) << 1) | ((b != 0 ? 1 : 0) << 2));
        return [ReportLiveLed, mask];
    }

    public static int ParseF2Rate(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < 2 || buf[0] != ReportLiveRate)
            throw new ArgumentException("Invalid 0xF2 buffer.");
        byte code = (byte)(buf[1] & 0x03);
        return CodeToRateHz.GetValueOrDefault(code, 500);
    }

    public static byte[] MakeF2RateCmd(int hz)
    {
        if (!RateHzToCode.TryGetValue(hz, out byte code))
            throw new ArgumentOutOfRangeException(nameof(hz), $"Unsupported polling rate: {hz} Hz.");
        return [ReportLiveRate, (byte)(0x04 | (code & 0x03))];
    }

    public static HardwareButtonEvent? ParseInputEvent(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < 5 || buf[0] != ReportInputEvent) return null;
        byte actionCode = buf[4];
        string name = SpecialActions.GetValueOrDefault(actionCode, $"special:0x{actionCode:X2}");
        return new HardwareButtonEvent
        {
            ActionCode = actionCode,
            ActionName = name,
            IsPressed = buf[1] != 0,
            Timestamp = DateTime.Now
        };
    }

    public static G300sProfile ParseProfile(ReadOnlySpan<byte> raw, int profileIndex)
    {
        if (raw.Length < 35)
            throw new ArgumentException($"Profile report must be 35 bytes (got {raw.Length}).");

        var profile = new G300sProfile
        {
            Index = profileIndex,
            LedR = raw[1] & 1,
            LedG = (raw[1] >> 1) & 1,
            LedB = (raw[1] >> 2) & 1,
            PollingRateHz = CodeToRateHz.GetValueOrDefault(raw[2], 500),
            Raw7 = raw[7]
        };

        int defaultStage = 0;
        for (int i = 0; i < 4; i++)
        {
            byte b = raw[3 + i];
            int dpi = (b & 0x7F) * 250;
            profile.DpiStages[i] = Math.Clamp(dpi, 250, 2500);
            if ((b & 0x80) != 0) defaultStage = i;
        }
        profile.DefaultDpiStage = defaultStage;

        for (int i = 0; i < 9; i++)
        {
            byte code = raw[8 + 3 * i];
            byte mod = raw[9 + 3 * i];
            byte key = raw[10 + 3 * i];

            if (SpecialActions.TryGetValue(code, out string? special))
            {
                profile.Buttons[i] = special;
            }
            else if (code is >= 1 and <= 9)
            {
                profile.Buttons[i] = $"mouse:{code}";
            }
            else if (mod != 0 || key != 0)
            {
                profile.Buttons[i] = $"key:{FormatKeystroke(mod, key)}";
            }
            else
            {
                profile.Buttons[i] = "none";
            }
        }

        return profile;
    }

    public static byte[] SerializeProfile(G300sProfile profile)
    {
        byte[] buf = new byte[35];
        buf[0] = ProfileReportIds[Math.Clamp(profile.Index, 0, 2)];
        buf[1] = (byte)((profile.LedR & 1) | ((profile.LedG & 1) << 1) | ((profile.LedB & 1) << 2));
        buf[2] = RateHzToCode.GetValueOrDefault(profile.PollingRateHz, (byte)3);

        for (int i = 0; i < 4; i++)
        {
            int dpi = Math.Clamp((int)Math.Round(profile.DpiStages[i] / 250.0) * 250, 250, 2500);
            byte step = (byte)(dpi / 250);
            if (i == profile.DefaultDpiStage) step |= 0x80;
            buf[3 + i] = step;
        }

        buf[7] = profile.Raw7;

        for (int i = 0; i < 9; i++)
        {
            var (code, mod, key) = EncodeButtonAction(profile.Buttons[i]);
            buf[8 + 3 * i] = code;
            buf[9 + 3 * i] = mod;
            buf[10 + 3 * i] = key;
        }

        return buf;
    }

    public static (byte Code, byte Mod, byte Key) EncodeButtonAction(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return (0, 0, 0);
        string s = spec.Trim().ToLowerInvariant();

        if (s is "none" or "off" or "disabled") return (0, 0, 0);
        if (ActionToSpecialCode.TryGetValue(s, out byte specialCode)) return (specialCode, 0, 0);

        if (s.StartsWith("mouse:"))
        {
            if (byte.TryParse(s.AsSpan(6), out byte btn) && btn is >= 1 and <= 9)
                return (btn, 0, 0);
        }

        if (s.StartsWith("key:"))
        {
            var (mod, key) = ParseKeystroke(s[4..]);
            return (0x00, mod, key);
        }

        return (0, 0, 0);
    }

    public static (byte Mod, byte Key) ParseKeystroke(string expr)
    {
        if (string.IsNullOrWhiteSpace(expr)) return (0, 0);
        var parts = expr.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        byte mod = 0;
        byte key = 0;

        foreach (string rawPart in parts)
        {
            string p = rawPart.ToLowerInvariant();
            switch (p)
            {
                case "ctrl" or "lctrl": mod |= 0x01; break;
                case "shift" or "lshift": mod |= 0x02; break;
                case "alt" or "lalt": mod |= 0x04; break;
                case "win" or "lwin": mod |= 0x08; break;
                case "rctrl": mod |= 0x10; break;
                case "rshift": mod |= 0x20; break;
                case "ralt": mod |= 0x40; break;
                case "rwin": mod |= 0x80; break;
                default:
                    if (HidKeyCodes.TryGetValue(p, out byte k))
                        key = k;
                    else if (p.StartsWith("0x") && byte.TryParse(p[2..], System.Globalization.NumberStyles.HexNumber, null, out byte hexKey))
                        key = hexKey;
                    break;
            }
        }
        return (mod, key);
    }

    public static string FormatKeystroke(byte mod, byte key)
    {
        var parts = new List<string>();
        if ((mod & 0x01) != 0) parts.Add("ctrl");
        if ((mod & 0x02) != 0) parts.Add("shift");
        if ((mod & 0x04) != 0) parts.Add("alt");
        if ((mod & 0x08) != 0) parts.Add("win");
        if ((mod & 0x10) != 0) parts.Add("rctrl");
        if ((mod & 0x20) != 0) parts.Add("rshift");
        if ((mod & 0x40) != 0) parts.Add("ralt");
        if ((mod & 0x80) != 0) parts.Add("rwin");

        if (key != 0)
        {
            parts.Add(HidCodeToName.TryGetValue(key, out string? kName) ? kName : $"0x{key:x2}");
        }
        return parts.Count > 0 ? string.Join("+", parts) : "none";
    }

    public static string? MapWpfKeyToHidName(Key wpfKey)
    {
        if (wpfKey is >= Key.A and <= Key.Z)
            return ((char)('a' + (wpfKey - Key.A))).ToString();
        if (wpfKey is >= Key.D0 and <= Key.D9)
            return ((char)('0' + (wpfKey - Key.D0))).ToString();
        if (wpfKey is >= Key.F1 and <= Key.F24)
            return $"f{1 + (wpfKey - Key.F1)}";
        if (wpfKey is >= Key.NumPad0 and <= Key.NumPad9)
            return $"kp_{wpfKey - Key.NumPad0}";

        return wpfKey switch
        {
            Key.Return => "enter",
            Key.Escape => "esc",
            Key.Back => "backspace",
            Key.Tab => "tab",
            Key.Space => "space",
            Key.OemMinus => "minus",
            Key.OemPlus => "equal",
            Key.OemOpenBrackets => "lbracket",
            Key.OemCloseBrackets => "rbracket",
            Key.OemPipe => "backslash",
            Key.OemSemicolon => "semicolon",
            Key.OemQuotes => "quote",
            Key.OemTilde => "grave",
            Key.OemComma => "comma",
            Key.OemPeriod => "period",
            Key.OemQuestion => "slash",
            Key.Capital => "capslock",
            Key.Snapshot => "prtsc",
            Key.Scroll => "scrolllock",
            Key.Pause => "pause",
            Key.Insert => "ins",
            Key.Home => "home",
            Key.PageUp => "pgup",
            Key.Delete => "del",
            Key.End => "end",
            Key.PageDown => "pgdn",
            Key.Right => "right",
            Key.Left => "left",
            Key.Down => "down",
            Key.Up => "up",
            Key.NumLock => "numlock",
            Key.Divide => "kp_slash",
            Key.Multiply => "kp_star",
            Key.Subtract => "kp_minus",
            Key.Add => "kp_plus",
            Key.Decimal => "kp_dot",
            Key.Apps => "menu",
            Key.VolumeMute => "mute",
            Key.VolumeUp => "volup",
            Key.VolumeDown => "voldown",
            _ => null
        };
    }

    public static string GetFriendlyActionTitle(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return "Devre Dışı";
        string s = spec.Trim().ToLowerInvariant();
        return s switch
        {
            "none" or "off" or "disabled" => "Devre Dışı",
            "mouse:1" => "Sol Tık",
            "mouse:2" => "Sağ Tık",
            "mouse:3" => "Orta Tık",
            "mouse:4" => "Geri (Fare 4)",
            "mouse:5" => "İleri (Fare 5)",
            "dpi-up" => "DPI Artır",
            "dpi-down" => "DPI Azalt",
            "dpi-cycle" => "DPI Döngüsü",
            "profile-cycle" => "Profil Değiştir",
            "dpi-alternate" => "DPI Shift (Nişan)",
            "dpi-default" => "Varsayılan DPI",
            _ when s.StartsWith("key:") => FormatKeyDisplay(s[4..]),
            _ => spec
        };
    }

    public static string FormatKeyDisplay(string keyExpr)
    {
        var parts = keyExpr.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var formatted = parts.Select(p => p.ToUpperInvariant() switch
        {
            "CTRL" or "LCTRL" => "Ctrl",
            "SHIFT" or "LSHIFT" => "Shift",
            "ALT" or "LALT" => "Alt",
            "WIN" or "LWIN" => "Win",
            "F13" => "F13 (Makro 1)",
            "F14" => "F14 (Makro 2)",
            "F15" => "F15 (Makro 3)",
            "F16" => "F16 (Makro 4)",
            "F17" => "F17 (Makro 5)",
            "F18" => "F18 (Makro 6)",
            var other => other
        });
        return string.Join(" + ", formatted);
    }

    public static string GetLedColorName(int r, int g, int b) => (r != 0 ? 1 : 0, g != 0 ? 1 : 0, b != 0 ? 1 : 0) switch
    {
        (0, 0, 0) => "Kapalı",
        (1, 0, 0) => "Kırmızı",
        (1, 1, 0) => "Kehribar Sarı",
        (0, 1, 0) => "Zümrüt Yeşil",
        (0, 1, 1) => "Turkuaz (Cyan)",
        (0, 0, 1) => "Kobalt Mavi",
        (1, 0, 1) => "Macenta",
        (1, 1, 1) => "Saf Beyaz",
        _ => "Turkuaz (Cyan)"
    };

    public static string GetLedColorHex(int r, int g, int b) => (r != 0 ? 1 : 0, g != 0 ? 1 : 0, b != 0 ? 1 : 0) switch
    {
        (0, 0, 0) => "#3A414C",
        (1, 0, 0) => "#FF334B",
        (1, 1, 0) => "#FFB800",
        (0, 1, 0) => "#00E676",
        (0, 1, 1) => "#00D4FF",
        (0, 0, 1) => "#2979FF",
        (1, 0, 1) => "#E040FB",
        (1, 1, 1) => "#F0F4F8",
        _ => "#00D4FF"
    };
}
