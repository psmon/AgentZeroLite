# log.ps1 — Show recent CLI action history
# Usage: log.ps1 [-Last 20] [-Clear]
param(
    [int]$Last = 50,
    [switch]$Clear
)
. "$PSScriptRoot\_common.ps1"

if ($Clear) {
    Invoke-AgentZeroLite @("log", "--clear")
} else {
    Invoke-AgentZeroLite @("log", "--last", $Last)
}
exit $LASTEXITCODE
