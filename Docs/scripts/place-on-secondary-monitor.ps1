<#
.SYNOPSIS
  Move a window to the secondary monitor and maximize it — how a Debug GUI under e2e test is
  kept off the operator's working screen (harness/knowledge/test-runner/gui-e2e-display.md).

.DESCRIPTION
  "Secondary" = the first non-primary monitor, left to right. With one monitor the window is
  left where it is and the script says so (exit 0 — a single-screen machine is not an error).
  Per-process DPI aware, so the coordinates are physical pixels on mixed-DPI setups.

.EXAMPLE
  $p = Start-Process $exe -PassThru
  & Docs/scripts/place-on-secondary-monitor.ps1 -ProcessId $p.Id
#>
param(
    [Parameter(Mandatory)] [int] $ProcessId,
    [int] $TimeoutSeconds = 30
)

Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class AzPlace {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
"@
[AzPlace]::SetProcessDPIAware() | Out-Null

# The main window appears a few seconds after the process starts.
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$hwnd = [IntPtr]::Zero
while ((Get-Date) -lt $deadline) {
    $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (-not $proc) { Write-Error "process $ProcessId is gone"; exit 1 }
    $proc.Refresh()
    if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { $hwnd = $proc.MainWindowHandle; break }
    Start-Sleep -Milliseconds 300
}
if ($hwnd -eq [IntPtr]::Zero) { Write-Error "no main window for $ProcessId within $TimeoutSeconds s"; exit 1 }

$secondary = [System.Windows.Forms.Screen]::AllScreens |
    Where-Object { -not $_.Primary } | Sort-Object { $_.Bounds.X } | Select-Object -First 1
if (-not $secondary) { Write-Output "single monitor — window left in place"; exit 0 }

$wa = $secondary.WorkingArea
[AzPlace]::ShowWindow($hwnd, 9) | Out-Null                       # SW_RESTORE: a maximized window ignores moves
[AzPlace]::SetWindowPos($hwnd, [IntPtr]::Zero, $wa.X + 40, $wa.Y + 40, [Math]::Max(800, $wa.Width - 80), [Math]::Max(600, $wa.Height - 80), 0x0014) | Out-Null   # NOZORDER|NOACTIVATE
[AzPlace]::ShowWindow($hwnd, 3) | Out-Null                       # SW_MAXIMIZE on the monitor it now sits on
Write-Output "maximized on $($secondary.DeviceName) at $($wa.X),$($wa.Y) $($wa.Width)x$($wa.Height)"
