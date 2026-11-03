using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace G300sController.Services;

/// <summary>
/// Non-activating, click-through (WS_EX_TRANSPARENT | WS_EX_NOACTIVATE) precision crosshair overlay
/// centered on the primary display for competitive FPS/TPS games.
/// </summary>
public sealed class CrosshairOverlayService : IDisposable
{
    private Window? _window;
    private Canvas? _canvas;

    public bool IsEnabled { get; private set; }
    // "cross", "dot", "circle", "tcross"
    public string StyleKey { get; private set; } = "cross";
    public string ColorHex { get; private set; } = "#00E676";

    public void Configure(bool enabled, string? styleKey = null, string? colorHex = null)
    {
        IsEnabled = enabled;
        if (!string.IsNullOrWhiteSpace(styleKey)) StyleKey = styleKey;
        if (!string.IsNullOrWhiteSpace(colorHex)) ColorHex = colorHex;

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (!IsEnabled)
            {
                _window?.Hide();
                return;
            }

            EnsureWindowCreated();
            if (_window is null || _canvas is null) return;

            RenderCrosshairGeometry();
            CenterOnPrimaryMonitor();

            if (!_window.IsVisible)
            {
                _window.Show();
            }
        });
    }

    private void EnsureWindowCreated()
    {
        if (_window is not null) return;

        _window = new Window
        {
            Width = 64,
            Height = 64,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            Focusable = false,
            IsHitTestVisible = false,
            ResizeMode = ResizeMode.NoResize
        };

        _canvas = new Canvas
        {
            Width = 64,
            Height = 64,
            Background = Brushes.Transparent,
            SnapsToDevicePixels = true
        };
        RenderOptions.SetEdgeMode(_canvas, EdgeMode.Aliased);
        _window.Content = _canvas;

        _window.SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(_window).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        };
    }

    private void CenterOnPrimaryMonitor()
    {
        if (_window is null) return;
        double screenW = SystemParameters.PrimaryScreenWidth;
        double screenH = SystemParameters.PrimaryScreenHeight;
        _window.Left = Math.Round((screenW - 64.0) * 0.5);
        _window.Top = Math.Round((screenH - 64.0) * 0.5);
    }

    private void RenderCrosshairGeometry()
    {
        if (_canvas is null) return;
        _canvas.Children.Clear();

        Brush mainBrush;
        try
        {
            mainBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(ColorHex));
        }
        catch
        {
            mainBrush = new SolidColorBrush(Color.FromRgb(0, 230, 118));
        }
        var outlineBrush = new SolidColorBrush(Color.FromArgb(210, 0, 0, 0));

        const double cx = 32.0;
        const double cy = 32.0;

        switch (StyleKey)
        {
            case "dot":
            {
                var dot = new Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = mainBrush,
                    Stroke = outlineBrush,
                    StrokeThickness = 1
                };
                Canvas.SetLeft(dot, cx - 3);
                Canvas.SetTop(dot, cy - 3);
                _canvas.Children.Add(dot);
                break;
            }

            case "circle":
            {
                var ring = new Ellipse
                {
                    Width = 20,
                    Height = 20,
                    Stroke = mainBrush,
                    StrokeThickness = 1.5
                };
                Canvas.SetLeft(ring, cx - 10);
                Canvas.SetTop(ring, cy - 10);
                _canvas.Children.Add(ring);

                var dot = new Ellipse
                {
                    Width = 4,
                    Height = 4,
                    Fill = mainBrush
                };
                Canvas.SetLeft(dot, cx - 2);
                Canvas.SetTop(dot, cy - 2);
                _canvas.Children.Add(dot);
                break;
            }

            case "tcross":
            {
                AddArm(cx - 10, cy - 1, 7, 2, mainBrush, outlineBrush); // Left
                AddArm(cx + 3, cy - 1, 7, 2, mainBrush, outlineBrush);  // Right
                AddArm(cx - 1, cy + 3, 2, 7, mainBrush, outlineBrush);  // Bottom
                break;
            }

            default: // "cross"
            {
                AddArm(cx - 10, cy - 1, 7, 2, mainBrush, outlineBrush); // Left
                AddArm(cx + 3, cy - 1, 7, 2, mainBrush, outlineBrush);  // Right
                AddArm(cx - 1, cy - 10, 2, 7, mainBrush, outlineBrush); // Top
                AddArm(cx - 1, cy + 3, 2, 7, mainBrush, outlineBrush);  // Bottom
                break;
            }
        }
    }

    private void AddArm(double x, double y, double w, double h, Brush fill, Brush stroke)
    {
        if (_canvas is null) return;
        var rect = new Rectangle
        {
            Width = w + 2,
            Height = h + 2,
            Fill = fill,
            Stroke = stroke,
            StrokeThickness = 1
        };
        Canvas.SetLeft(rect, x - 1);
        Canvas.SetTop(rect, y - 1);
        _canvas.Children.Add(rect);
    }

    public void Dispose()
    {
        _window?.Close();
        _window = null;
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
