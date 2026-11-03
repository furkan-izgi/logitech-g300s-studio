using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace G300sController.Services;

/// <summary>
/// Non-activating, click-through (WS_EX_TRANSPARENT | WS_EX_NOACTIVATE) on-screen HUD notification
/// for displaying profile switches, DPI stage changes, and G-Shift state without interrupting fullscreen games.
/// </summary>
public sealed class OsdNotificationService : IDisposable
{
    private Window? _osdWindow;
    private Border? _colorDot;
    private TextBlock? _titleBlock;
    private TextBlock? _subtitleBlock;
    private DispatcherTimer? _hideTimer;

    public bool IsEnabled { get; set; } = true;

    public void ShowNotification(string title, string subtitle, string accentHex = "#FAFAFA")
    {
        if (!IsEnabled) return;

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            EnsureWindowCreated();
            if (_osdWindow is null || _colorDot is null || _titleBlock is null || _subtitleBlock is null)
                return;

            _titleBlock.Text = title;
            _subtitleBlock.Text = subtitle;

            try
            {
                var col = (Color)ColorConverter.ConvertFromString(accentHex);
                _colorDot.Background = new SolidColorBrush(col);
            }
            catch
            {
                _colorDot.Background = Brushes.White;
            }

            PositionTopRight();

            if (!_osdWindow.IsVisible)
            {
                _osdWindow.Opacity = 0;
                _osdWindow.Show();
            }

            var fadeIn = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(120))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            _osdWindow.BeginAnimation(UIElement.OpacityProperty, fadeIn);

            _hideTimer?.Stop();
            _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1550) };
            _hideTimer.Tick += (_, _) =>
            {
                _hideTimer?.Stop();
                if (_osdWindow is null) return;

                var fadeOut = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                };
                fadeOut.Completed += (_, _) =>
                {
                    if (_osdWindow is not null && _osdWindow.Opacity < 0.05)
                    {
                        _osdWindow.Hide();
                    }
                };
                _osdWindow.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            };
            _hideTimer.Start();
        });
    }

    private void EnsureWindowCreated()
    {
        if (_osdWindow is not null) return;

        _osdWindow = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            Focusable = false,
            IsHitTestVisible = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize
        };

        var outerBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(242, 17, 17, 21)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(42, 42, 50)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 11, 18, 11),
            Margin = new Thickness(12),
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 18,
                ShadowDepth = 4,
                Opacity = 0.65
            }
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _colorDot = new Border
        {
            Width = 10,
            Height = 28,
            CornerRadius = new CornerRadius(3),
            Background = Brushes.White,
            Margin = new Thickness(0, 0, 13, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_colorDot, 0);
        grid.Children.Add(_colorDot);

        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _titleBlock = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(250, 250, 250))
        };
        _subtitleBlock = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromRgb(161, 161, 170)),
            Margin = new Thickness(0, 1, 0, 0)
        };
        textStack.Children.Add(_titleBlock);
        textStack.Children.Add(_subtitleBlock);
        Grid.SetColumn(textStack, 1);
        grid.Children.Add(textStack);

        outerBorder.Child = grid;
        _osdWindow.Content = outerBorder;

        _osdWindow.SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(_osdWindow).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        };
    }

    private void PositionTopRight()
    {
        if (_osdWindow is null) return;
        _osdWindow.UpdateLayout();
        var workArea = SystemParameters.WorkArea;
        double w = _osdWindow.ActualWidth > 10 ? _osdWindow.ActualWidth : 260;
        _osdWindow.Left = workArea.Right - w - 16;
        _osdWindow.Top = workArea.Top + 16;
    }

    public void Dispose()
    {
        _hideTimer?.Stop();
        _osdWindow?.Close();
        _osdWindow = null;
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
}
