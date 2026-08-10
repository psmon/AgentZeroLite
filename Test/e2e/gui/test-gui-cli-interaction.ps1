# test-gui-cli-interaction.ps1 — Tier 3 E2E: live GUI ↔ CLI IPC round-trips.
#
# Verifies the WM_COPYDATA CLI path against a running GUI: status, terminal-list,
# terminal-read (read-only), terminal-wait (W4 TUI-idle, against a real idle
# terminal), and agent-hook (W1 fire-and-forget). A terminal-send round-trip
# runs ONLY against a shell-titled tab so we never inject text into a live agent
# CLI (Claude/Codex) or SSH session.
#
# Run:  pwsh Test/e2e/gui/test-gui-cli-interaction.ps1 [-Configuration Debug] [-KeepOpen]

[CmdletBinding()]
param([string]$Configuration = "Debug", [switch]$KeepOpen)

. "$PSScriptRoot\..\lib\_common.ps1"

$exe = Get-Exe -Configuration $Configuration
$runDir = New-RunDir -Tier "gui-cli"
Write-Host "Tier 3 — GUI <-> CLI interaction" -ForegroundColor Cyan
Write-Host "  exe: $exe"
Write-Host ""

Test-Case "GUI is up" {
    Ensure-Gui -Exe $exe -TimeoutSec 25 | Out-Null
    Start-Sleep -Seconds 4
}

Test-Case "status responds over IPC" {
    $r = Invoke-Cli -Exe $exe -CliArgs @("status") -TimeoutSec 20
    Assert-Exit0 $r
    Assert-True ($r.Output.Trim().Length -gt 0) "empty status output"
}

# Parse terminal-list, locate a running tab (and, separately, a shell-titled one).
$script:RunTab = $null      # @{ g; t; title }
$script:ShellTab = $null
Test-Case "terminal-list enumerates over IPC" {
    $r = Invoke-Cli -Exe $exe -CliArgs @("terminal-list") -TimeoutSec 20
    Assert-Exit0 $r
    $r.Output | Set-Content -Path (Join-Path $runDir "terminal-list.txt") -Encoding UTF8
    # terminal-list prints a human table, then a "--- JSON ---" marker, then JSON.
    $marker = "--- JSON ---"
    $idx = $r.Output.IndexOf($marker)
    $jsonPart = if ($idx -ge 0) { $r.Output.Substring($idx + $marker.Length).Trim() } else { $r.Output.Trim() }
    $list = $jsonPart | ConvertFrom-Json
    foreach ($g in @($list.groups)) {
        foreach ($t in @($g.tabs)) {
            if ($t.running) {
                if (-not $script:RunTab) { $script:RunTab = @{ g = $g.group_index; t = $t.tab_index; title = $t.title } }
                if (-not $script:ShellTab -and ($t.title -match '(?i)cmd|pw5|pw7|powershell|pwsh|bash|zsh|terminal|console')) {
                    $script:ShellTab = @{ g = $g.group_index; t = $t.tab_index; title = $t.title }
                }
            }
        }
    }
    Write-Host "      running tab: $(if($script:RunTab){"[$($script:RunTab.g):$($script:RunTab.t)] '$($script:RunTab.title)'"}else{'none'})"
    Write-Host "      shell tab:   $(if($script:ShellTab){"[$($script:ShellTab.g):$($script:ShellTab.t)] '$($script:ShellTab.title)'"}else{'none'})"
}

Test-Case "terminal-read (read-only) against a live terminal" {
    if (-not $script:RunTab) { Write-Host "      (no running terminal — skipped)"; return }
    $r = Invoke-Cli -Exe $exe -CliArgs @("terminal-read","$($script:RunTab.g)","$($script:RunTab.t)","--last","400") -TimeoutSec 20
    Assert-Exit0 $r
    # read-only: any output (including empty console) is acceptable; must not error.
}

Test-Case "W4 terminal-wait detects idle on a live terminal" {
    if (-not $script:RunTab) { Write-Host "      (no running terminal — skipped)"; return }
    # A restored terminal sitting at a prompt is idle → should return promptly.
    $r = Invoke-Cli -Exe $exe -CliArgs @("terminal-wait","$($script:RunTab.g)","$($script:RunTab.t)","--idle-ms","800","--timeout-ms","10000") -TimeoutSec 20
    Assert-True ($r.ExitCode -eq 0) "terminal-wait did not report idle (exit=$($r.ExitCode)): $($r.Output)"
    Assert-Contains $r.Output "idle"
}

Test-Case "terminal-send round-trip (shell tab only)" {
    if (-not $script:ShellTab) { Write-Host "      (no shell-titled terminal — send skipped to avoid disrupting agent/SSH tabs)"; return }
    $token = "AZ-E2E-" + ([guid]::NewGuid().ToString('n').Substring(0,8))
    $g = $script:ShellTab.g; $t = $script:ShellTab.t
    $s = Invoke-Cli -Exe $exe -CliArgs @("terminal-send","$g","$t","echo $token") -TimeoutSec 20
    Assert-Exit0 $s
    Invoke-Cli -Exe $exe -CliArgs @("terminal-wait","$g","$t","--idle-ms","1000","--timeout-ms","12000") -TimeoutSec 20 | Out-Null
    $r = Invoke-Cli -Exe $exe -CliArgs @("terminal-read","$g","$t","--last","1000") -TimeoutSec 20
    Assert-Exit0 $r
    Assert-Contains $r.Output $token
    Write-Host "      echoed token observed: $token"
}

Test-Case "W1 agent-hook fire-and-forget accepted" {
    $r = Invoke-Cli -Exe $exe -CliArgs @("agent-hook","--event","PreToolUse","--detail","e2e-probe","--no-wait") -TimeoutSec 15
    Assert-Exit0 $r
    # Best-effort: confirm the GUI logged the hook (warn-only — log surface varies).
    $log = Invoke-Cli -Exe $exe -CliArgs @("log","--last","40") -TimeoutSec 15
    if ($log.ExitCode -eq 0 -and $log.Output -match "agent-hook") { Write-Host "      GUI logged the hook" }
    else { Write-Host "      (hook log not surfaced via -cli log — fire-and-forget still accepted)" }
}

if (-not $KeepOpen) {
    Write-Host ""
    Write-Host "  closing GUI (pass -KeepOpen to leave it running)"
    Get-Process AgentZeroLite -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

$fail = Write-Summary -Suite "Tier 3 (GUI<->CLI)" -RunDir $runDir
exit $fail
