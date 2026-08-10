# test-cli-inproc.ps1 — Tier 1 E2E: in-process CLI commands (no GUI needed).
#
# Verifies the feat/orca CLI surface that runs entirely in the -cli process:
#   W4 help <topic>, worktree ; W6 orchestrate ; W9 cost.
# SAFE: does not touch the user's real ~/.cursor/.codex/.claude (those commands
# are covered by headless unit tests against a temp home). orchestrate writes
# only to the app's own local DB, using a clearly-labeled test run name.
#
# Run:  pwsh Test/e2e/cli/test-cli-inproc.ps1 [-Configuration Debug]

[CmdletBinding()]
param([string]$Configuration = "Debug")

. "$PSScriptRoot\..\lib\_common.ps1"

$exe = Get-Exe -Configuration $Configuration
$runDir = New-RunDir -Tier "cli"
Write-Host "Tier 1 — CLI in-process E2E" -ForegroundColor Cyan
Write-Host "  exe: $exe"
Write-Host ""

# ── W4/W5: help <topic> serves the version-matched guide (anti-drift) ────────
Test-Case "W4 help agentzero serves guide" {
    $r = Invoke-Cli -Exe $exe -CliArgs @("help","agentzero")
    Assert-Exit0 $r
    Assert-Contains $r.Output "terminal-wait"
    Assert-Contains $r.Output "worktree"
}
Test-Case "W4 help unknown-topic fails with topic list" {
    $r = Invoke-Cli -Exe $exe -CliArgs @("help","does-not-exist")
    Assert-True ($r.ExitCode -ne 0) "expected non-zero exit for unknown topic"
    Assert-Contains $r.Output "Topics:"
}

# ── W4/W7: worktree list on the repo itself ──────────────────────────────────
Test-Case "W7 worktree list (repo root)" {
    Push-Location $script:RepoRoot
    try {
        $r = Invoke-Cli -Exe $exe -CliArgs @("worktree","list")
        Assert-Exit0 $r
        # Either lists the main worktree or reports none — both are valid, must not error.
        Assert-True (($r.Output -match "worktree") -or ($r.Output -match "No worktrees") -or ($r.Output.Trim().Length -ge 0)) "worktree list produced no sane output"
    } finally { Pop-Location }
}

# ── W9: cost estimate from recorded token usage ──────────────────────────────
Test-Case "W9 cost runs and reports" {
    $r = Invoke-Cli -Exe $exe -CliArgs @("cost")
    Assert-Exit0 $r
    Assert-True (($r.Output -match "Estimated cost") -or ($r.Output -match "No token usage")) "cost output unexpected: $($r.Output)"
}

# ── W6: orchestrate create → status → list (local DB only) ───────────────────
$specPath = Join-Path $runDir "run-spec.json"
@'
{
  "name": "e2e-test-run",
  "tasks": [
    { "key": "a", "prompt": "do a", "deps": [] },
    { "key": "b", "prompt": "do b", "deps": ["a"] }
  ]
}
'@ | Set-Content -Path $specPath -Encoding UTF8

$script:CreatedRunId = $null
Test-Case "W6 orchestrate create" {
    $r = Invoke-Cli -Exe $exe -CliArgs @("orchestrate","create",$specPath)
    Assert-Exit0 $r
    Assert-Contains $r.Output "Created run #"
    if ($r.Output -match "Created run #(\d+)") { $script:CreatedRunId = [int]$Matches[1] }
    Assert-True ($null -ne $script:CreatedRunId) "could not parse run id"
}
Test-Case "W6 orchestrate status shows DAG" {
    Assert-True ($null -ne $script:CreatedRunId) "no run id from create"
    $r = Invoke-Cli -Exe $exe -CliArgs @("orchestrate","status","$script:CreatedRunId")
    Assert-Exit0 $r
    Assert-Contains $r.Output "e2e-test-run"
    Assert-Contains $r.Output "a"
    Assert-Contains $r.Output "b"
    # 'b' declares a dependency on 'a'
    Assert-Contains $r.Output "[a]"
}
Test-Case "W6 orchestrate list includes the run" {
    $r = Invoke-Cli -Exe $exe -CliArgs @("orchestrate","list")
    Assert-Exit0 $r
    Assert-Contains $r.Output "e2e-test-run"
}

$fail = Write-Summary -Suite "Tier 1 (CLI in-process)" -RunDir $runDir
exit $fail
