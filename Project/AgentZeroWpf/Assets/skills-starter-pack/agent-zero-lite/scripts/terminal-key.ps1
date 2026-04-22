# terminal-key.ps1 — Send a control key (cr/esc/tab/ctrlc/arrows/hex:XX) to a terminal tab
# Usage: terminal-key.ps1 -GroupIndex 0 -TabIndex 0 -Key cr
param(
    [Parameter(Mandatory)][int]$GroupIndex,
    [Parameter(Mandatory)][int]$TabIndex,
    [Parameter(Mandatory)][string]$Key
)
. "$PSScriptRoot\_common.ps1"

Invoke-AgentZeroLite @("terminal-key", $GroupIndex, $TabIndex, $Key)
exit $LASTEXITCODE
