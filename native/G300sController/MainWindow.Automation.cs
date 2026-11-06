using System;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using G300sController.Services;

namespace G300sController;

public partial class MainWindow
{
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
            var nums = Regex.Matches(raw, @"-?\d+")
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
            _osd.ShowNotification($"{prof.DisplayName} · {prof.ColorName}", $"{dpi} DPI · {prof.PollingRateHz} Hz", prof.ColorHex);
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
                $"{prof.DisplayName} ({prof.ColorName}) · {dpi} DPI",
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
}
