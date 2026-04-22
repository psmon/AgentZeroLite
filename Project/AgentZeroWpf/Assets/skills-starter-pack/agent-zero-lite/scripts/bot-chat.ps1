# bot-chat.ps1 — Display a chat message in the AgentBot panel (no LLM trigger)
# Usage: bot-chat.ps1 [-From Name] <message...>
param(
    [string]$From = "CLI",
    [Parameter(Mandatory, ValueFromRemainingArguments)][string[]]$Message
)
. "$PSScriptRoot\_common.ps1"

$joined = $Message -join " "
Invoke-AgentZeroLite @("bot-chat", $joined, "--from", $From)
exit $LASTEXITCODE
