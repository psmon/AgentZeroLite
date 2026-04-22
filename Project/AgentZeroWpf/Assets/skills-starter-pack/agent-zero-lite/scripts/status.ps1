# status.ps1 — Query AgentZero Lite app state
. "$PSScriptRoot\_common.ps1"
Invoke-AgentZeroLite @("status")
exit $LASTEXITCODE
