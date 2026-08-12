# find-cli.ps1 — Locate AgentZeroLite.exe by priority, verify build identity.
#
# Priority (internal test build first):
#   1. Debug build   2. Release build   3. Installed (%ProgramFiles%)   4. PATH
#
# Prints the resolved exe path to stdout (last line) so callers can capture it:
#   $Exe = (& .claude/skills/agentzero-cli/scripts/find-cli.ps1 -Quiet)
#
# Options:
#   -RepoRoot <path>  Repo root to search under (default: git top-level, else CWD)
#   -Quiet            Print only the exe path (no identity banner)

[CmdletBinding()]
param(
    [string]$RepoRoot,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

if (-not $RepoRoot) {
    $RepoRoot = (git rev-parse --show-toplevel 2>$null)
    if (-not $RepoRoot) { $RepoRoot = (Get-Location).Path }
}

$candidates = @(
    (Join-Path $RepoRoot 'Project\AgentZeroWpf\bin\Debug\net10.0-windows\AgentZeroLite.exe'),
    (Join-Path $RepoRoot 'Project\AgentZeroWpf\bin\Release\net10.0-windows\AgentZeroLite.exe'),
    (Join-Path $env:ProgramFiles 'AgentZeroLite\AgentZeroLite.exe')
)

$exe = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $exe) {
    $onPath = Get-Command AgentZeroLite.exe -ErrorAction SilentlyContinue
    if ($onPath) { $exe = $onPath.Source }
}

if (-not $exe) {
    Write-Error "AgentZeroLite.exe not found. Build it (dotnet build Project/AgentZeroWpf) or install it."
    exit 1
}

$exe = (Resolve-Path $exe).Path

if (-not $Quiet) {
    $which = switch -Wildcard ($exe) {
        '*\bin\Debug\*'   { 'DEBUG build (internal test)'; break }
        '*\bin\Release\*' { 'RELEASE build'; break }
        "*$env:ProgramFiles*" { 'INSTALLED build'; break }
        default { 'PATH / other' }
    }
    Write-Host "AgentZeroLite.exe → $which" -ForegroundColor Cyan
    Write-Host "  $exe" -ForegroundColor DarkGray
    # Verify identity (needs no GUI). Start-Process because it is a WinExe.
    Start-Process $exe -ArgumentList '-cli','version' -NoNewWindow -Wait
}

# Last line = the path, for capture.
$exe
