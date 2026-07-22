# Clicks a window-relative point in a window identified by HWND, then types literal text.
# Text goes through SendKeys, so SendKeys metacharacters are escaped here rather than by callers.
param(
    [Parameter(Mandatory = $true)][string]$Handle,
    [Parameter(Mandatory = $true)][int]$X,
    [Parameter(Mandatory = $true)][int]$Y,
    [Parameter(Mandatory = $true)][string]$Text,
    [int]$SettleMs = 500
)

Add-Type -AssemblyName System.Windows.Forms

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class CT {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT v, int s);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
}
"@

[void][CT]::SetProcessDPIAware()
$h = [IntPtr][Convert]::ToInt64($Handle, 16)
$r = New-Object CT+RECT
if ([CT]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) -ne 0) { [void][CT]::GetWindowRect($h, [ref]$r) }

[void][CT]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 300

# This script is the most dangerous of the three: the click can land in another application (see
# Capture-Handle.ps1), and SendKeys then types into whatever holds real focus -- so text can be
# entered into an unrelated window while this still reports success. Refuse unless the target
# genuinely owns the pixel.
$probe = New-Object CT+POINT
$probe.X = $r.Left + $X; $probe.Y = $r.Top + $Y
$rootAtPoint = [CT]::GetAncestor([CT]::WindowFromPoint($probe), 2)   # GA_ROOT
if ($rootAtPoint -ne $h) {
    $sb = New-Object System.Text.StringBuilder 256
    [void][CT]::GetWindowText($rootAtPoint, $sb, 256)
    Write-Output "REFUSED: ($X,$Y) belongs to 0x$($rootAtPoint.ToString('X')) '$($sb.ToString())', not $Handle. Nothing was clicked and nothing was typed."
    exit 2
}

$saved = New-Object CT+POINT
[void][CT]::GetCursorPos([ref]$saved)
[void][CT]::SetCursorPos($r.Left + $X, $r.Top + $Y)
Start-Sleep -Milliseconds 150
[CT]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 60
[CT]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
[void][CT]::SetCursorPos($saved.X, $saved.Y)
Start-Sleep -Milliseconds 250

# SendKeys treats + ^ % ~ ( ) { } [ ] as control characters; brace-escape them.
$escaped = [regex]::Replace($Text, '[+^%~(){}\[\]]', { '{' + $args[0].Value + '}' })
[System.Windows.Forms.SendKeys]::SendWait($escaped)
Start-Sleep -Milliseconds $SettleMs
Write-Output "OK typed $($Text.Length) chars at ($X,$Y)"
