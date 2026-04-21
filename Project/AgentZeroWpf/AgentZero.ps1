# AgentZero.ps1
# AgentZeroWpf CLI 래퍼 - WinExe 콘솔 출력 문제를 해결합니다.
# Usage: AgentZero.ps1 <command> [options]

$ExePath = Join-Path $PSScriptRoot "AgentZeroWpf.exe"

if (-not (Test-Path $ExePath)) {
    Write-Host "[ERROR] 실행 파일을 찾을 수 없습니다: $ExePath" -ForegroundColor Red
    exit 1
}

$proc = Start-Process -FilePath $ExePath -ArgumentList (@("-cli") + $args) -NoNewWindow -Wait -PassThru
exit $proc.ExitCode
