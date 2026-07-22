# Scrolls the AER Flow window by sending mouse-wheel notches at a window-relative point,
# then captures by handle. Saves/restores the user's cursor so driving the app does not
# steal their pointer. Mirrors Click-Capture.ps1's DPI and capture handling.
param(
    [Parameter(Mandatory = $true)][int]$X,          # window-relative, in captured-bitmap pixels
    [Parameter(Mandatory = $true)][int]$Y,
    [Parameter(Mandatory = $true)][int]$Notches,    # negative scrolls down
    [Parameter(Mandatory = $true)][string]$OutPath,
    [int]$SettleMs = 1200
)

Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Scr {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, int d, UIntPtr e);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT val, int size);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
}
"@

[void][Scr]::SetProcessDPIAware()

$proc = Get-Process | Where-Object { $_.MainWindowTitle -like "*AER*" } | Select-Object -First 1
if (-not $proc) { Write-Output "NO WINDOW"; exit 1 }
$h = $proc.MainWindowHandle

if ([Scr]::IsIconic($h)) { [void][Scr]::ShowWindow($h, 9) }
[void][Scr]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 350

$r = New-Object Scr+RECT
[void][Scr]::DwmGetWindowAttribute($h, 9, [ref]$r, 16)

$saved = New-Object Scr+POINT
[void][Scr]::GetCursorPos([ref]$saved)

[void][Scr]::SetCursorPos($r.Left + $X, $r.Top + $Y)
Start-Sleep -Milliseconds 150
# 0x0800 = MOUSEEVENTF_WHEEL; one notch = 120
for ($i = 0; $i -lt [Math]::Abs($Notches); $i++) {
    [Scr]::mouse_event(0x0800, 0, 0, $(if ($Notches -lt 0) { -120 } else { 120 }), [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
}
[void][Scr]::SetCursorPos($saved.X, $saved.Y)
Start-Sleep -Milliseconds $SettleMs

$w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
$bmp = New-Object System.Drawing.Bitmap($w, $ht)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
[void][Scr]::PrintWindow($h, $hdc, 2)
$g.ReleaseHdc($hdc); $g.Dispose()
$bmp.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "OK scroll($X,$Y,$Notches) -> $OutPath (${w}x${ht})"
