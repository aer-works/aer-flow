# Captures a single top-level window by handle, independent of which monitor it is on,
# whether it is occluded, or how the desktop is arranged. Uses PrintWindow with
# PW_RENDERFULLCONTENT (0x2), which asks the compositor to re-render the window into a
# bitmap rather than copying pixels off the screen.
param(
    [Parameter(Mandatory = $true)][string]$TitleLike,
    [Parameter(Mandatory = $true)][string]$OutPath
)

Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class WinCap {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT val, int size);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

$proc = Get-Process | Where-Object { $_.MainWindowTitle -like "*$TitleLike*" } | Select-Object -First 1
if (-not $proc) {
    Write-Output "NO WINDOW matching '$TitleLike'"
    Get-Process | Where-Object { $_.MainWindowTitle } | ForEach-Object { "  open: [$($_.ProcessName)] $($_.MainWindowTitle)" }
    exit 1
}

$h = $proc.MainWindowHandle
if ([WinCap]::IsIconic($h)) { [WinCap]::ShowWindow($h, 9) | Out-Null; Start-Sleep -Milliseconds 700 }

# DWMWA_EXTENDED_FRAME_BOUNDS = 9 — the true visible bounds, excluding the invisible
# resize border GetWindowRect reports on DWM-composited windows.
$r = New-Object WinCap+RECT
$null = [WinCap]::DwmGetWindowAttribute($h, 9, [ref]$r, 16)
$w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
if ($w -le 0 -or $ht -le 0) { Write-Output "BAD BOUNDS ${w}x${ht}"; exit 1 }

$bmp = New-Object System.Drawing.Bitmap($w, $ht)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [WinCap]::PrintWindow($h, $hdc, 2)   # 2 = PW_RENDERFULLCONTENT
$g.ReleaseHdc($hdc); $g.Dispose()

if (-not $ok) { Write-Output "PrintWindow FAILED"; $bmp.Dispose(); exit 1 }

$bmp.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "OK ${w}x${ht} -> $OutPath  [$($proc.ProcessName)] $($proc.MainWindowTitle)"
