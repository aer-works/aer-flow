# Enumerates every visible top-level window belonging to a process, not just MainWindow.
# Avalonia modal dialogs are separate HWNDs and never appear in Process.MainWindowTitle.
param([Parameter(Mandatory = $true)][int]$ProcessId)

Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class Enu {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

$found = @()
$cb = [Enu+EnumProc]{
    param($h, $l)
    [uint32]$pid2 = 0
    [void][Enu]::GetWindowThreadProcessId($h, [ref]$pid2)
    if ($pid2 -eq $ProcessId -and [Enu]::IsWindowVisible($h)) {
        $len = [Enu]::GetWindowTextLength($h)
        $sb = New-Object System.Text.StringBuilder ($len + 2)
        [void][Enu]::GetWindowText($h, $sb, $sb.Capacity)
        $r = New-Object Enu+RECT
        [void][Enu]::GetWindowRect($h, [ref]$r)
        $script:found += [pscustomobject]@{
            Handle = "0x{0:X}" -f [int64]$h
            Title  = $sb.ToString()
            Rect   = "$($r.Left),$($r.Top) $($r.Right - $r.Left)x$($r.Bottom - $r.Top)"
        }
    }
    return $true
}
[void][Enu]::EnumWindows($cb, [IntPtr]::Zero)
$found | Format-Table -AutoSize
