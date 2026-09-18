# AgentZeroLite.ps1
# AgentZero Lite (Avalonia host) CLI wrapper - routes console output from the WinExe binary.
# Usage: AgentZeroLite.ps1 <command> [options]
#   e.g. AgentZeroLite.ps1 status
#        AgentZeroLite.ps1 terminal-list
#        AgentZeroLite.ps1 terminal-send 0 0 "dir"

$ExePath = Join-Path $PSScriptRoot "AgentZeroLite.exe"

if (-not (Test-Path $ExePath)) {
    Write-Host "[ERROR] executable not found: $ExePath" -ForegroundColor Red
    exit 1
}

$proc = Start-Process -FilePath $ExePath -ArgumentList (@("-cli") + $args) -NoNewWindow -Wait -PassThru
exit $proc.ExitCode
