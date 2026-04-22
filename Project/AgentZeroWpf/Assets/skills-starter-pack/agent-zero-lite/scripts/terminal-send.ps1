# terminal-send.ps1 — Send text + Enter to a specific terminal tab
# Usage: terminal-send.ps1 -GroupIndex 0 -TabIndex 0 <text...>
param(
    [Parameter(Mandatory)][int]$GroupIndex,
    [Parameter(Mandatory)][int]$TabIndex,
    [Parameter(Mandatory, ValueFromRemainingArguments)][string[]]$Text
)
. "$PSScriptRoot\_common.ps1"

$joined = $Text -join " "
Invoke-AgentZeroLite @("terminal-send", $GroupIndex, $TabIndex, $joined)
exit $LASTEXITCODE
