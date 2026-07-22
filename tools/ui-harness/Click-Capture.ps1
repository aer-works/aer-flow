# Clicks a window-relative point in the AER Flow window, then captures by handle.
# Saves and restores the user's cursor position so driving the app does not steal
# their pointer. DPI-aware so bitmap pixels and screen coordinates agree.
param(
    [Parameter(Mandatory = $true)][int]$X,        # window-relative, in captured-bitmap pixels
    [Parameter(Mandatory = $true)][int]$Y,
    [Parameter(Mandatory = $true)][string]$OutPath,
    [int]$SettleMs = 1500
)

Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Clk {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT val, int size);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    public const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004;
}
"@

[Clk]::SetProcessDPIAware() | Out-Null

$proc = Get-Process | Where-Object { $_.MainWindowTitle -like '*AER Flow*' } | Select-Object -First 1
if (-not $proc) { Write-Output "NO AER WINDOW"; exit 1 }
$h = $proc.MainWindowHandle

$r = New-Object Clk+RECT
$null = [Clk]::DwmGetWindowAttribute($h, 9, [ref]$r, 16)

[Clk]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 350

$saved = New-Object Clk+POINT
[Clk]::GetCursorPos([ref]$saved) | Out-Null

[Clk]::SetCursorPos($r.Left + $X, $r.Top + $Y) | Out-Null
Start-Sleep -Milliseconds 120
[Clk]::mouse_event([Clk]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 60
[Clk]::mouse_event([Clk]::LEFTUP, 0, 0, 0, [IntPtr]::Zero)

[Clk]::SetCursorPos($saved.X, $saved.Y) | Out-Null
Start-Sleep -Milliseconds $SettleMs

$null = [Clk]::DwmGetWindowAttribute($h, 9, [ref]$r, 16)
$w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
$bmp = New-Object System.Drawing.Bitmap($w, $ht)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [Clk]::PrintWindow($h, $hdc, 2)
$g.ReleaseHdc($hdc); $g.Dispose()
if (-not $ok) { Write-Output "PrintWindow FAILED"; $bmp.Dispose(); exit 1 }
$bmp.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "OK click($X,$Y) -> $OutPath (${w}x${ht})"
