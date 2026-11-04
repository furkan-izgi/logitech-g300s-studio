using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace G300sController.Services;

public enum AmbilightSamplingMode
{
    FullScreenAverage = 0,
    LeftRightSplit = 1
}

public sealed class AmbilightService : IDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _samplingTask;
    private int _targetFps = 20;

    public bool IsRunning => _cts is not null && !_cts.IsCancellationRequested;
    public AmbilightSamplingMode Mode { get; set; } = AmbilightSamplingMode.LeftRightSplit;

    public int TargetFps
    {
        get => _targetFps;
        set => _targetFps = Math.Max(5, Math.Min(60, value));
    }

    public event Action<Color, Color, int>? ColorsSampled;

    // G300s Hardware Color Mapping (Index: 1=Red, 2=Green, 3=Blue, 4=Yellow, 5=Magenta, 6=Cyan, 7=White)
    private static readonly (int Index, byte R, byte G, byte B)[] HardwarePalette =
    [
        (1, 255, 0, 0),      // Red
        (2, 0, 255, 0),      // Green
        (3, 0, 0, 255),      // Blue
        (4, 255, 230, 0),    // Yellow
        (5, 220, 0, 255),    // Magenta
        (6, 0, 220, 255),    // Cyan
        (7, 240, 240, 255)   // White
    ];

    public void Start()
    {
        if (IsRunning) return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _samplingTask = Task.Run(() => SamplingLoop(token), token);
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _samplingTask?.Wait(500);
        }
        catch
        {
            // Ignore cancellation wait timeout
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _samplingTask = null;
        }
    }

    private void SamplingLoop(CancellationToken token)
    {
        const int sampleW = 16;
        const int sampleH = 16;

        using var bmp = new Bitmap(sampleW, sampleH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;

        while (!token.IsCancellationRequested)
        {
            var frameStart = Environment.TickCount;

            try
            {
                int screenW = (int)System.Windows.SystemParameters.PrimaryScreenWidth;
                int screenH = (int)System.Windows.SystemParameters.PrimaryScreenHeight;

                if (screenW > 0 && screenH > 0)
                {
                    IntPtr hdcScreen = GetDC(IntPtr.Zero);
                    IntPtr hdcBmp = g.GetHdc();

                    SetStretchBltMode(hdcBmp, 3); // COLORONCOLOR
                    StretchBlt(hdcBmp, 0, 0, sampleW, sampleH, hdcScreen, 0, 0, screenW, screenH, 0x00CC0020); // SRCCOPY

                    g.ReleaseHdc(hdcBmp);
                    ReleaseDC(IntPtr.Zero, hdcScreen);

                    var bmpData = bmp.LockBits(
                        new Rectangle(0, 0, sampleW, sampleH),
                        ImageLockMode.ReadOnly,
                        System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                    int halfPixels = (sampleW / 2) * sampleH;

                    long leftR = 0, leftG = 0, leftB = 0;
                    long rightR = 0, rightG = 0, rightB = 0;

                    unsafe
                    {
                        byte* ptr = (byte*)bmpData.Scan0;
                        int stride = bmpData.Stride;

                        for (int y = 0; y < sampleH; y++)
                        {
                            byte* row = ptr + (y * stride);
                            for (int x = 0; x < sampleW; x++)
                            {
                                int idx = x * 4;
                                byte b = row[idx];
                                byte gVal = row[idx + 1];
                                byte r = row[idx + 2];

                                if (x < sampleW / 2)
                                {
                                    leftR += r; leftG += gVal; leftB += b;
                                }
                                else
                                {
                                    rightR += r; rightG += gVal; rightB += b;
                                }
                            }
                        }
                    }

                    bmp.UnlockBits(bmpData);

                    byte avgLeftR = (byte)(leftR / halfPixels);
                    byte avgLeftG = (byte)(leftG / halfPixels);
                    byte avgLeftB = (byte)(leftB / halfPixels);

                    byte avgRightR = (byte)(rightR / halfPixels);
                    byte avgRightG = (byte)(rightG / halfPixels);
                    byte avgRightB = (byte)(rightB / halfPixels);

                    Color leftColor = Color.FromRgb(avgLeftR, avgLeftG, avgLeftB);
                    Color rightColor = Color.FromRgb(avgRightR, avgRightG, avgRightB);

                    // Combine for primary hardware color match
                    byte dominantR = (byte)((avgLeftR + avgRightR) / 2);
                    byte dominantG = (byte)((avgLeftG + avgRightG) / 2);
                    byte dominantB = (byte)((avgLeftB + avgRightB) / 2);

                    int matchedHwIndex = FindClosestHardwareColor(dominantR, dominantG, dominantB);

                    ColorsSampled?.Invoke(leftColor, rightColor, matchedHwIndex);
                }
            }
            catch
            {
                // Screen lock / UAC switch guard
            }

            int elapsed = Environment.TickCount - frameStart;
            int delay = Math.Max(10, (1000 / _targetFps) - elapsed);

            try
            {
                Task.Delay(delay, token).Wait(token);
            }
            catch
            {
                break;
            }
        }
    }

    public static int FindClosestHardwareColor(byte r, byte g, byte b)
    {
        // If screen is nearly pitch black, turn LED off (0)
        int luminance = (int)(0.299 * r + 0.587 * g + 0.114 * b);
        if (luminance < 18)
        {
            return 0; // Black / Off
        }

        int closestIdx = 6; // Default Cyan
        double minDistance = double.MaxValue;

        foreach (var (idx, pr, pg, pb) in HardwarePalette)
        {
            // Weighted Euclidean distance for human perception
            double dr = r - pr;
            double dg = g - pg;
            double db = b - pb;
            double dist = (dr * dr * 0.3) + (dg * dg * 0.59) + (db * db * 0.11);

            if (dist < minDistance)
            {
                minDistance = dist;
                closestIdx = idx;
            }
        }

        return closestIdx;
    }

    public void Dispose()
    {
        Stop();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
                                          IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, int rop);
}
