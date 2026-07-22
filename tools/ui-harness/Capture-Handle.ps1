# Captures any top-level window by HWND, and optionally clicks a window-relative point first.
# Needed for Avalonia modal dialogs, which are separate HWNDs invisible to Process.MainWindowTitle.
param(
    [Parameter(Mandatory = $true)][string]$Handle,   # e.g. 0x68105A
    [Parameter(Mandatory = $true)][string]$OutPath,
    [int]$ClickX = -1,
    [int]$ClickY = -1,
    [int]$SettleMs = 1500
)

Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Cap2 {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT v, int s);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
}
"@

[void][Cap2]::SetProcessDPIAware()
$h = [IntPtr][Convert]::ToInt64($Handle, 16)

$r = New-Object Cap2+RECT
if ([Cap2]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) -ne 0) { [void][Cap2]::GetWindowRect($h, [ref]$r) }

if ($ClickX -ge 0 -and $ClickY -ge 0) {
    [void][Cap2]::SetForegroundWindow($h)
    Start-Sleep -Milliseconds 300

    # A click is a real screen event: it goes to whatever window is topmost at that pixel, NOT to
    # $Handle. SetForegroundWindow is refused outright when the calling process isn't already
    # foreground (it fails silently and returns), so if anything else is covering the target the
    # click lands in THAT application instead -- and PrintWindow below still renders the target
    # correctly, so the screenshot looks like a UI that simply ignored the click. This has already
    # manufactured false product defects, and in one session sent clicks into an unrelated browser
    # window. Verify the target actually owns the pixel before pressing anything.
    $probe = New-Object Cap2+POINT
    $probe.X = $r.Left + $ClickX; $probe.Y = $r.Top + $ClickY
    $atPoint = [Cap2]::WindowFromPoint($probe)
    $rootAtPoint = [Cap2]::GetAncestor($atPoint, 2)   # GA_ROOT
    if ($rootAtPoint -ne $h) {
        $sb = New-Object System.Text.StringBuilder 256
        [void][Cap2]::GetWindowText($rootAtPoint, $sb, 256)
        Write-Output "REFUSED: ($ClickX,$ClickY) belongs to 0x$($rootAtPoint.ToString('X')) '$($sb.ToString())', not $Handle. Bring the target window to the front and retry; no click was sent."
        exit 2
    }

    $saved = New-Object Cap2+POINT
    [void][Cap2]::GetCursorPos([ref]$saved)
    [void][Cap2]::SetCursorPos($r.Left + $ClickX, $r.Top + $ClickY)
    Start-Sleep -Milliseconds 150
    [Cap2]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)   # LEFTDOWN
    Start-Sleep -Milliseconds 60
    [Cap2]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)   # LEFTUP
    [void][Cap2]::SetCursorPos($saved.X, $saved.Y)
    Start-Sleep -Milliseconds $SettleMs
    # geometry may change after the click
    if ([Cap2]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) -ne 0) { [void][Cap2]::GetWindowRect($h, [ref]$r) }
}

$w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
if ($w -le 0 -or $ht -le 0) { Write-Output "BAD RECT ${w}x${ht}"; exit 1 }
$bmp = New-Object System.Drawing.Bitmap($w, $ht)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
[void][Cap2]::PrintWindow($h, $hdc, 2)
$g.ReleaseHdc($hdc); $g.Dispose()
$bmp.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "OK $Handle -> $OutPath (${w}x${ht})"
