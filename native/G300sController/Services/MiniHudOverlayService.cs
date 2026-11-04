using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace G300sController.Services;

public sealed class MiniHudOverlayService : IDisposable
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private Window? _window;
    private TextBlock? _profileText;
    private TextBlock? _dpiText;
    private TextBlock? _rateText;
    private Ellipse? _profileDot;
    private readonly Dictionary<int, Border> _buttonIndicators = new();

    public bool IsEnabled { get; set; } = false;
    public bool AutoShowOnMinimize { get; set; } = true;
    public bool IsClickThrough { get; set; } = true;

    public void Show()
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            EnsureWindowCreated();
            if (_window is null) return;
            UpdateWindowExStyle();
            _window.Show();
        });
    }

    public void Hide()
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _window?.Hide();
        });
    }

    public void UpdateTelemetry(string profileName, string hexColor, int dpi, int hz)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_profileText != null) _profileText.Text = profileName;
            if (_dpiText != null) _dpiText.Text = $"{dpi} DPI";
            if (_rateText != null) _rateText.Text = $"{hz} Hz";
            if (_profileDot != null)
            {
                try
                {
                    _profileDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hexColor));
                }
                catch
                {
                    _profileDot.Fill = new SolidColorBrush(Color.FromRgb(0, 212, 255));
                }
            }
        });
    }

    public void HighlightButton(int buttonIndex, bool isPressed)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_buttonIndicators.TryGetValue(buttonIndex, out var badge))
            {
                badge.Background = isPressed
                    ? new SolidColorBrush(Color.FromRgb(0, 240, 255))
                    : new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));

                if (badge.Child is TextBlock tb)
                {
                    tb.Foreground = isPressed
                        ? new SolidColorBrush(Color.FromRgb(10, 11, 14))
                        : new SolidColorBrush(Color.FromArgb(180, 255, 255, 255));
                    tb.FontWeight = isPressed ? FontWeights.Bold : FontWeights.Normal;
                }
            }
        });
    }

    private void EnsureWindowCreated()
    {
        if (_window != null) return;

        _window = new Window
        {
            Title = "G300s Mini HUD",
            Width = 270,
            Height = 84,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            ResizeMode = ResizeMode.NoResize
        };

        // Default position: top right of screen
        double screenW = SystemParameters.PrimaryScreenWidth;
        _window.Left = screenW - 295;
        _window.Top = 25;

        // Container Card
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(220, 14, 15, 20)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(80, 0, 240, 255)),
            BorderThickness = new Thickness(1.2),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 8, 12, 8),
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 14,
                ShadowDepth = 4,
                Opacity = 0.75
            }
        };

        card.MouseLeftButtonDown += (_, e) =>
        {
            if (!IsClickThrough && e.LeftButton == MouseButtonState.Pressed)
            {
                _window.DragMove();
            }
        };

        var mainStack = new StackPanel();

        // Top Row: Profile Dot, Profile Name, DPI & Hz
        var topGrid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var profPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _profileDot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = new SolidColorBrush(Color.FromRgb(0, 212, 255)),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        _profileText = new TextBlock
        {
            Text = "Profil 1",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(240, 240, 245)),
            VerticalAlignment = VerticalAlignment.Center
        };
        profPanel.Children.Add(_profileDot);
        profPanel.Children.Add(_profileText);
        Grid.SetColumn(profPanel, 0);
        topGrid.Children.Add(profPanel);

        var telPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _dpiText = new TextBlock
        {
            Text = "2000 DPI",
            FontFamily = new FontFamily("Consolas, Segoe UI Variable Display"),
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0, 240, 255)),
            Margin = new Thickness(0, 0, 8, 0)
        };
        _rateText = new TextBlock
        {
            Text = "1000 Hz",
            FontFamily = new FontFamily("Consolas, Segoe UI Variable Display"),
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255))
        };
        telPanel.Children.Add(_dpiText);
        telPanel.Children.Add(_rateText);
        Grid.SetColumn(telPanel, 2);
        topGrid.Children.Add(telPanel);

        mainStack.Children.Add(topGrid);

        // Bottom Row: 9-Button Activity Visualizer
        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        string[] labels = ["L", "R", "M", "G4", "G5", "G6", "G7", "G8", "G9"];

        for (int i = 0; i < 9; i++)
        {
            int btnIndex = i + 1;
            var badge = new Border
            {
                Width = 23,
                Height = 18,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
                Margin = new Thickness(1.5, 0, 1.5, 0)
            };

            var tb = new TextBlock
            {
                Text = labels[i],
                FontSize = 9.5,
                Foreground = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            badge.Child = tb;
            _buttonIndicators[btnIndex] = badge;
            btnPanel.Children.Add(badge);
        }

        mainStack.Children.Add(btnPanel);
        card.Child = mainStack;
        _window.Content = card;

        _window.SourceInitialized += (_, _) =>
        {
            UpdateWindowExStyle();
        };
    }

    private void UpdateWindowExStyle()
    {
        if (_window is null) return;
        IntPtr hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero) return;

        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        if (IsClickThrough)
        {
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        }
        else
        {
            SetWindowLong(hwnd, GWL_EXSTYLE, (exStyle & ~WS_EX_TRANSPARENT) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        }
    }

    public void Dispose()
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _window?.Close();
            _window = null;
        });
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
}
