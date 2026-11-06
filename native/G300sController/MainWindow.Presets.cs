using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using G300sController.Models;

namespace G300sController;

public partial class MainWindow
{
    private static readonly string ProfileNamesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LogitechG300sStudio",
        "profile_names.json");

    private int _targetRenameSlotIndex = 0;

    public void ApplyPresetTemplate(string presetKey)
    {
        var prof = _profiles[_activeProfileIndex];

        if (_presetService.ApplyPreset(presetKey, prof, out string presetTitle))
        {
            if (_device.IsConnected && _automation.ActiveRgbEffect == "none")
            {
                try { _device.SetLiveLed(prof.LedR, prof.LedG, prof.LedB); } catch { /* ignore */ }
            }

            MarkModified($"Profil {_activeProfileIndex + 1} üzerine '{presetTitle}' şablonu yüklendi.");
            RefreshSlotHeaderVisuals();
            RefreshCurrentProfileToUi();
            ShowToast($"'{presetTitle}' şablonu uygulandı!");
        }
        else
        {
            ShowToast($"Şablon '{presetKey}' bulunamadı.");
        }
    }

    private void PopulatePresetCombo()
    {
        if (PresetTemplateCombo is null) return;

        _suppressUiEvents = true;
        PresetTemplateCombo.Items.Clear();

        // 0. Placeholder Header
        PresetTemplateCombo.Items.Add(new ComboBoxItem
        {
            Content = "🎮 Şablon Yükle...",
            Tag = string.Empty,
            IsEnabled = false,
            IsSelected = true
        });

        var all = _presetService.Presets;

        // 1. Game Presets
        var games = all.Where(p => p.Category.Equals("Oyun", StringComparison.OrdinalIgnoreCase)).ToList();
        if (games.Count > 0)
        {
            foreach (var g in games)
            {
                PresetTemplateCombo.Items.Add(new ComboBoxItem
                {
                    Content = g.DisplayTitle,
                    Tag = g.Id,
                    ToolTip = g.Description
                });
            }
        }

        // 2. Non-game Presets (General / Productivity)
        var others = all.Where(p => !p.Category.Equals("Oyun", StringComparison.OrdinalIgnoreCase)).ToList();
        if (others.Count > 0)
        {
            PresetTemplateCombo.Items.Add(new Separator());
            foreach (var o in others)
            {
                PresetTemplateCombo.Items.Add(new ComboBoxItem
                {
                    Content = o.DisplayTitle,
                    Tag = o.Id,
                    ToolTip = o.Description
                });
            }
        }

        // 3. Preset Folder Management Actions
        PresetTemplateCombo.Items.Add(new Separator());
        PresetTemplateCombo.Items.Add(new ComboBoxItem
        {
            Content = "📁 Şablon Klasörünü Aç (Presets/)...",
            Tag = "action:open_folder",
            ToolTip = "Disk üzerindeki hazır şablon JSON dosyalarını Dosya Gezgini'nde aç"
        });
        PresetTemplateCombo.Items.Add(new ComboBoxItem
        {
            Content = "🔄 Şablonları Yenile (Diskten Oku)",
            Tag = "action:reload",
            ToolTip = "Presets klasörüne yeni eklediğiniz veya düzenlediğiniz JSON şablonlarını anında yeniden yükle"
        });
        PresetTemplateCombo.Items.Add(new ComboBoxItem
        {
            Content = "💾 Aktif Profili Yeni Şablon Olarak Kaydet...",
            Tag = "action:save_as",
            ToolTip = "Şu anki aktif profil ayarlarını Presets/ klasörüne yeni bir JSON oyun şablonu olarak kaydet"
        });

        PresetTemplateCombo.SelectedIndex = 0;
        _suppressUiEvents = false;
    }

    private void PresetTemplateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || PresetTemplateCombo is null) return;
        if (PresetTemplateCombo.SelectedItem is not ComboBoxItem { Tag: string tag } || string.IsNullOrWhiteSpace(tag)) return;

        if (tag == "action:open_folder")
        {
            _presetService.OpenFolderInExplorer();
            ShowToast("Presets klasörü açıldı.");
        }
        else if (tag == "action:reload")
        {
            _presetService.ReloadPresets();
            ShowToast("Tüm şablonlar diskten başarıyla yenilendi!");
        }
        else if (tag == "action:save_as")
        {
            SaveCurrentProfileAsPreset();
        }
        else
        {
            ApplyPresetTemplate(tag);
        }

        _suppressUiEvents = true;
        PresetTemplateCombo.SelectedIndex = 0;
        _suppressUiEvents = false;
    }

    private void SaveCurrentProfileAsPreset()
    {
        var prof = _profiles[_activeProfileIndex];
        string defaultName = string.IsNullOrWhiteSpace(prof.CustomName) ? $"Özel Profil {_activeProfileIndex + 1}" : prof.CustomName;

        var sfd = new Microsoft.Win32.SaveFileDialog
        {
            InitialDirectory = _presetService.GetPresetsDirectory(),
            FileName = $"{defaultName.ToLowerInvariant().Replace(' ', '_')}.json",
            DefaultExt = ".json",
            Filter = "JSON Şablon Dosyaları (*.json)|*.json",
            Title = "Aktif Profili Yeni Şablon Olarak Kaydet"
        };

        if (sfd.ShowDialog(this) == true)
        {
            try
            {
                string fileTitle = Path.GetFileNameWithoutExtension(sfd.FileName);
                var template = PresetTemplate.FromProfile(
                    prof,
                    id: fileTitle.ToLowerInvariant(),
                    name: defaultName,
                    category: "Özel",
                    icon: "⭐",
                    description: $"{DateTime.Now:yyyy-MM-dd HH:mm} tarihinde oluşturulan özel fare profili."
                );

                _presetService.SaveCustomPreset(template, sfd.FileName);
                ShowToast($"'{template.Name}' şablonu Presets klasörüne kaydedildi!");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Şablon kaydedilirken bir hata oluştu:\n{ex.Message}", "Kayıt Hatası", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
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

    private void LoadProfileNames()
    {
        try
        {
            if (File.Exists(ProfileNamesPath))
            {
                var json = File.ReadAllText(ProfileNamesPath);
                var names = JsonSerializer.Deserialize<string[]>(json);
                if (names != null)
                {
                    for (int i = 0; i < Math.Min(names.Length, _profiles.Length); i++)
                    {
                        if (!string.IsNullOrWhiteSpace(names[i]))
                            _profiles[i].CustomName = names[i].Trim();
                    }
                }
            }
        }
        catch { /* ignore */ }
    }

    private void SaveProfileNames()
    {
        try
        {
            var dir = Path.GetDirectoryName(ProfileNamesPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var names = _profiles.Select(p => p.CustomName ?? "").ToArray();
            File.WriteAllText(ProfileNamesPath, JsonSerializer.Serialize(names, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* ignore */ }
    }

    private void RenameActiveProfileBtn_Click(object sender, RoutedEventArgs e)
    {
        OpenRenameModal(_activeProfileIndex);
    }

    private void RenameSlotContext_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is string tagStr && int.TryParse(tagStr, out int slot))
        {
            OpenRenameModal(slot);
        }
        else
        {
            OpenRenameModal(_activeProfileIndex);
        }
    }

    private void SlotButton_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tagStr && int.TryParse(tagStr, out int slot))
        {
            OpenRenameModal(slot);
        }
    }

    private void OpenRenameModal(int slotIndex)
    {
        _targetRenameSlotIndex = Math.Clamp(slotIndex, 0, 2);
        var prof = _profiles[_targetRenameSlotIndex];
        if (RenameProfileModalTitle is not null)
            RenameProfileModalTitle.Text = $"Yuva {_targetRenameSlotIndex + 1} Profilini Yeniden Adlandır";
        if (RenameProfileModalSubtitle is not null)
            RenameProfileModalSubtitle.Text = $"Mevcut Renk: {prof.ColorName} · Özel bir isim belirleyin";
        if (RenameProfileTextBox is not null)
        {
            RenameProfileTextBox.Text = prof.CustomName ?? $"Profil {_targetRenameSlotIndex + 1}";
            RenameProfileTextBox.Focus();
            RenameProfileTextBox.SelectAll();
        }
        if (RenameProfileOverlayCard is not null)
            RenameProfileOverlayCard.Visibility = Visibility.Visible;
    }

    private void CloseRenameProfile_Click(object sender, RoutedEventArgs e)
    {
        if (RenameProfileOverlayCard is not null)
            RenameProfileOverlayCard.Visibility = Visibility.Collapsed;
    }

    private void SaveRenameProfile_Click(object sender, RoutedEventArgs e)
    {
        CommitRenameProfile();
    }

    private void RenameProfileTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitRenameProfile();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseRenameProfile_Click(sender, e);
            e.Handled = true;
        }
    }

    private void CommitRenameProfile()
    {
        if (RenameProfileTextBox is null) return;
        string newName = RenameProfileTextBox.Text.Trim();
        var prof = _profiles[_targetRenameSlotIndex];
        prof.CustomName = string.IsNullOrWhiteSpace(newName) ? null : newName;
        SaveProfileNames();
        RefreshSlotHeaderVisuals();
        RefreshCurrentProfileToUi();
        if (RenameProfileOverlayCard is not null)
            RenameProfileOverlayCard.Visibility = Visibility.Collapsed;
        ShowToast($"Profil {_targetRenameSlotIndex + 1} adı '{prof.DisplayName}' olarak güncellendi.");
    }
}
