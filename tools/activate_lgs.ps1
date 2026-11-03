Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
  public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
$p = Get-Process LCore -ErrorAction SilentlyContinue
if (-not $p) { "LCore not running"; exit 1 }
$h = $p.MainWindowHandle
if ($h -eq 0) { "no main window handle"; exit 1 }
if ([Win]::IsIconic($h)) { [Win]::ShowWindow($h, 9) | Out-Null }
[Win]::SetWindowPos($h, [IntPtr]::Zero, 40, 20, 1280, 900, 0x0040) | Out-Null
[Win]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 600
$r = New-Object Win+RECT
[Win]::GetWindowRect($h, [ref]$r) | Out-Null
"handle=$h rect=$($r.Left),$($r.Top),$($r.Right),$($r.Bottom) title=$($p.MainWindowTitle)"
