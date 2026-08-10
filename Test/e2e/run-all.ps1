# run-all.ps1 — AgentZero Lite E2E orchestrator.
#
# Runs the E2E tiers and aggregates pass/fail. Tier 1 (CLI in-process) is always
# run; Tier 2 (GUI smoke) needs a desktop session and can be skipped in headless
# CI with -SkipGui.
#
# Run:  pwsh Test/e2e/run-all.ps1 [-Configuration Debug] [-SkipGui]

[CmdletBinding()]
param([string]$Configuration = "Debug", [switch]$SkipGui)

$ErrorActionPreference = "Continue"
$here = $PSScriptRoot
$fails = 0

Write-Host "########## AgentZero Lite E2E ##########" -ForegroundColor Magenta
Write-Host "Configuration=$Configuration  SkipGui=$SkipGui"
Write-Host ""

Write-Host ">>> Tier 1: CLI in-process" -ForegroundColor Magenta
& pwsh -NoProfile -File (Join-Path $here "cli\test-cli-inproc.ps1") -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { $fails += $LASTEXITCODE }

if (-not $SkipGui) {
    Write-Host ""
    Write-Host ">>> Tier 2: GUI smoke + visual" -ForegroundColor Magenta
    & pwsh -NoProfile -File (Join-Path $here "gui\test-gui-smoke.ps1") -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { $fails += $LASTEXITCODE }
} else {
    Write-Host ""
    Write-Host ">>> Tier 2 skipped (-SkipGui)" -ForegroundColor Yellow
}

Write-Host ""
if ($fails -eq 0) {
    Write-Host "########## E2E PASSED ##########" -ForegroundColor Green
} else {
    Write-Host "########## E2E FAILED ($fails) ##########" -ForegroundColor Red
}
exit $fails
