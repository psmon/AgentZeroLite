# terminal-read.ps1 — Read ANSI-stripped text buffer from a terminal tab
# Usage: terminal-read.ps1 -GroupIndex 0 -TabIndex 0 [-Last 2000]
param(
    [Parameter(Mandatory)][int]$GroupIndex,
    [Parameter(Mandatory)][int]$TabIndex,
    [int]$Last = 0
)
. "$PSScriptRoot\_common.ps1"

$cliArgs = @("terminal-read", $GroupIndex, $TabIndex)
if ($Last -gt 0) { $cliArgs += @("--last", $Last) }

Invoke-AgentZeroLite $cliArgs
exit $LASTEXITCODE
