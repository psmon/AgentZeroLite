# _common.ps1 — Shared helper for agent-zero-lite skill scripts
# Source at the top of each script: . "$PSScriptRoot\_common.ps1"

$ErrorActionPreference = "Stop"

function Invoke-AgentZeroLite {
    param([string[]]$Arguments)
    $result = & AgentZeroLite.ps1 @Arguments 2>&1
    $result | Write-Output
    return $LASTEXITCODE
}
