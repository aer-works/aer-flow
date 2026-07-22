# Brings a window genuinely to the front so synthesized clicks actually reach it.
#
# Windows refuses SetForegroundWindow from a process that is not already foreground -- it fails
# silently. That is why the click helpers refuse (#356): a click is a real screen event and would
# otherwise land in whichever application happens to be on top.
#
# The supported way to get the right is to attach to the current foreground window's input queue
# with AttachThreadInput, which puts this process in the same input context; SetForegroundWindow is
# then honoured. -Topmost additionally pins the window above everything else, which is what makes a
# multi-step drive reliable -- otherwise anything that steals focus mid-sequence silently starts
# eating clicks again. Always release it with -Release when finished.
param(
    [Parameter(Mandatory = $true)][string]$Handle,
    [switch]$Topmost,
    [switch]$Release
)

Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class FgWin {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
}
"@

[void][FgWin]::SetProcessDPIAware()
$h = [IntPtr][Convert]::ToInt64($Handle, 16)

$HWND_TOPMOST = [IntPtr](-1)
$HWND_NOTOPMOST = [IntPtr](-2)
$SWP_NOMOVE = 0x0002; $SWP_NOSIZE = 0x0001; $SWP_SHOWWINDOW = 0x0040
$flags = $SWP_NOMOVE -bor $SWP_NOSIZE -bor $SWP_SHOWWINDOW

if ($Release) {
    [void][FgWin]::SetWindowPos($h, $HWND_NOTOPMOST, 0, 0, 0, 0, $flags)
    Write-Output "RELEASED $Handle (no longer topmost)"
    exit 0
}

if ([FgWin]::IsIconic($h)) { [void][FgWin]::ShowWindow($h, 9) }   # SW_RESTORE

$fg = [FgWin]::GetForegroundWindow()
$fgThread = [FgWin]::GetWindowThreadProcessId($fg, [IntPtr]::Zero)
$thisThread = [FgWin]::GetCurrentThreadId()

# Attaching to the foreground thread's input queue is what makes SetForegroundWindow legal here.
$attached = $false
if ($fgThread -ne 0 -and $fgThread -ne $thisThread) {
    $attached = [FgWin]::AttachThreadInput($thisThread, $fgThread, $true)
}
try {
    [void][FgWin]::BringWindowToTop($h)
    [void][FgWin]::SetForegroundWindow($h)
    if ($Topmost) { [void][FgWin]::SetWindowPos($h, $HWND_TOPMOST, 0, 0, 0, 0, $flags) }
}
finally {
    if ($attached) { [void][FgWin]::AttachThreadInput($thisThread, $fgThread, $false) }
}

Start-Sleep -Milliseconds 400

$now = [FgWin]::GetForegroundWindow()
$sb = New-Object System.Text.StringBuilder 256
[void][FgWin]::GetWindowText($now, $sb, 256)
if ($now -eq $h) {
    Write-Output "OK foreground = $Handle$(if ($Topmost) { ' (topmost)' })"
    exit 0
}

# Report the truth rather than a hopeful success: the click helpers' own guard is the backstop, but
# a caller that assumes this worked would drive a blind sequence.
Write-Output "FAILED: foreground is 0x$($now.ToString('X')) '$($sb.ToString())', not $Handle"
exit 1
