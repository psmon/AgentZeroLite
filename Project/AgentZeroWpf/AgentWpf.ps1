# AgentWpf.ps1
# AgentZeroWpf GUI 실행 래퍼
# Usage: AgentWpf.ps1

$ExePath = Join-Path $PSScriptRoot "AgentZeroWpf.exe"

# Prefer Release build to avoid file-lock conflicts with Debug builds
if (-not (Test-Path $ExePath)) {
    $ExePath = Join-Path $PSScriptRoot "bin\Release\net10.0-windows\AgentZeroWpf.exe"
}
if (-not (Test-Path $ExePath)) {
    $ExePath = Join-Path $PSScriptRoot "bin\Debug\net10.0-windows\AgentZeroWpf.exe"
}

if (-not (Test-Path $ExePath)) {
    Write-Host "[ERROR] 실행 파일을 찾을 수 없습니다. dotnet build를 먼저 실행하세요." -ForegroundColor Red
    exit 1
}

Write-Host "[AgentWpf] 실행: $ExePath" -ForegroundColor Cyan
Start-Process -FilePath $ExePath
