# test-gui-smoke.ps1 — Tier 2 E2E: launch the GUI and verify it via the OS-CLI
# window-control verbs (read-only), capturing a screenshot artifact.
#
# Reuses the launch-self-smoke pattern: list-windows → get-window-info →
# screenshot → element-tree. Adds a check that the new W3 "Diff Review" entry is
# discoverable in the UI Automation tree (warn-only — glyph buttons may expose
# only the tooltip).
#
# Run:  pwsh Test/e2e/gui/test-gui-smoke.ps1 [-Configuration Debug] [-KeepOpen]

[CmdletBinding()]
param([string]$Configuration = "Debug", [switch]$KeepOpen)

. "$PSScriptRoot\..\lib\_common.ps1"

$exe = Get-Exe -Configuration $Configuration
$runDir = New-RunDir -Tier "gui"
Write-Host "Tier 2 — GUI smoke + visual" -ForegroundColor Cyan
Write-Host "  exe: $exe"
Write-Host "  artifacts: $runDir"
Write-Host ""

$script:Hwnd = $null

Test-Case "GUI launches and window is enumerable" {
    Ensure-Gui -Exe $exe -TimeoutSec 25 | Out-Null
    # Let the WPF surface finish first paint before we probe/screenshot it —
    # capturing immediately after the window appears yields a half-rendered frame.
    Start-Sleep -Seconds 5
    $script:Hwnd = Get-AppWindowHwnd -Exe $exe
    Assert-True ($script:Hwnd -gt 0) "no hwnd resolved"
}

Test-Case "get-window-info returns a real rect" {
    $r = Invoke-Cli -Exe $exe -CliArgs @("os","get-window-info","$script:Hwnd")
    Assert-Exit0 $r
    $info = $r.Output | ConvertFrom-Json
    Assert-True ($info.window.rect.w -gt 0 -and $info.window.rect.h -gt 0) "window has zero size"
}

Test-Case "screenshot captures a non-empty PNG" {
    $shot = Save-AppScreenshot -Exe $exe -Hwnd $script:Hwnd -RunDir $runDir
    Assert-True ($shot.width -gt 0 -and $shot.height -gt 0) "screenshot has zero size"
    Assert-True (Test-Path $shot.path) "screenshot file missing"
    Write-Host "      png: $($shot.path) ($($shot.width)x$($shot.height))"
}

# element-tree on the full WPF surface is expensive; keep depth shallow (3) and
# treat it as warn-only (the original launch-self-smoke does the same — some
# shell states minimize the window mid-run). The screenshot is the hard gate.
Test-Case "UI Automation tree is reachable (warn-only)" {
    $r = Invoke-Cli -Exe $exe -CliArgs @("os","element-tree","$script:Hwnd","--depth","3") -TimeoutSec 60
    if ($r.ExitCode -ne 0) { Write-Host "      WARN: element-tree exit=$($r.ExitCode)"; return }
    $tree = $r.Output | ConvertFrom-Json
    if ($tree.nodeCount -ge 1) {
        $r.Output | Set-Content -Path (Join-Path $runDir "element-tree.json") -Encoding UTF8
        Write-Host "      nodeCount: $($tree.nodeCount)"
        if ($r.Output -match "Diff") { Write-Host "      found 'Diff' node in UIA tree (W3 button)" }
    } else { Write-Host "      WARN: empty UIA tree" }
}

if (-not $KeepOpen) {
    Write-Host ""
    Write-Host "  closing GUI (pass -KeepOpen to leave it running)"
    Get-Process AgentZeroLite -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

$fail = Write-Summary -Suite "Tier 2 (GUI smoke)" -RunDir $runDir
exit $fail
