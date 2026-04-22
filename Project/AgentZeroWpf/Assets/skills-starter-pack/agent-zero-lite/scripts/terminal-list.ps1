# terminal-list.ps1 — Enumerate active terminal sessions in AgentZero Lite
. "$PSScriptRoot\_common.ps1"
Invoke-AgentZeroLite @("terminal-list")
exit $LASTEXITCODE
