# AgentZeroLite.ps1
# AgentZeroWpf CLI wrapper - routes console output from the WinExe binary.
# Usage: AgentZeroLite.ps1 <command> [options]

$ExePath = Join-Path $PSScriptRoot "AgentZeroLite.exe"

if (-not (Test-Path $ExePath)) {
    Write-Host "[ERROR] 실행 파일을 찾을 수 없습니다: $ExePath" -ForegroundColor Red
    exit 1
}

$proc = Start-Process -FilePath $ExePath -ArgumentList (@("-cli") + $args) -NoNewWindow -Wait -PassThru
exit $proc.ExitCode
