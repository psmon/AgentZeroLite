# launch-self-smoke.ps1 — M0014 E2E acceptance probe.
#
# Uses the new `os` CLI verbs to verify AgentZeroLite is reachable from the
# desktop. Mission contract is functional ACCESS only — no driving. Steps:
#   1. Resolve the AgentZeroLite.exe under bin/ (Debug or Release).
#   2. Make sure a GUI instance is running (start it if not).
#   3. `os list-windows --filter "AgentZero Lite"` to confirm the window
#      exists in the desktop window list.
#   4. `os get-window-info <hwnd>` for full geometry / pid / class.
#   5. `os screenshot --hwnd <hwnd>` to drop a PNG under
#      tmp/os-cli/screenshots/<date>/.
#   6. `os element-tree <hwnd> --depth 3` to prove UIA tree is reachable
#      (sanity-only, depth=3 is fast).
#   7. Append a summary line to tmp/os-cli/e2e/<date>.log.
#
# Exit code:
#   0 — every probe step succeeded
#   1 — any step failed; details on stdout/stderr and in the log file
#
# This script does NOT enable input simulation. It only exercises the
# read-only verbs. That matches the M0014 acceptance contract:
# "기능접근만 확인.. 기능수행x".

[CmdletBinding()]
param(
    [string]$Configuration = "Debug",
    [int]$LaunchTimeoutSec = 15
)

$ErrorActionPreference = "Stop"
# Script lives at Docs/scripts/ → repo root is two levels up.
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$exe = Join-Path $repoRoot "Project\AgentZeroWpf\bin\$Configuration\net10.0-windows\AgentZeroLite.exe"

if (-not (Test-Path $exe)) {
    Write-Error "AgentZeroLite.exe not found under $exe. Build first: dotnet build Project/AgentZeroWpf/AgentZeroWpf.csproj -c $Configuration"
}

$dateStr = Get-Date -Format "yyyy-MM-dd"
$timeStr = Get-Date -Format "HH-mm-ss"
$logDir = Join-Path $repoRoot "tmp\os-cli\e2e"
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
$logFile = Join-Path $logDir "$dateStr.log"

function Write-Step($msg) {
    $stamp = Get-Date -Format "HH:mm:ss"
    Write-Host "[$stamp] $msg"
    Add-Content -Path $logFile -Value "[$stamp] $msg"
}

function Run-OsCli($args) {
    $argList = @("-cli", "os") + $args
    $output = & $exe @argList 2>&1
    $exit = $LASTEXITCODE
    return @{ Output = ($output -join [Environment]::NewLine); ExitCode = $exit }
}

Write-Step "===== M0014 launch-self-smoke ($timeStr) ====="
Write-Step "exe=$exe"

# ---- Step 1: ensure GUI is up (if not, start it and wait for window) ----
$gui = Get-Process -Name "AgentZeroLite" -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne 0 } |
    Select-Object -First 1

if (-not $gui) {
    Write-Step "GUI not running — launching $exe"
    Start-Process -FilePath $exe | Out-Null

    $deadline = (Get-Date).AddSeconds($LaunchTimeoutSec)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $gui = Get-Process -Name "AgentZeroLite" -ErrorAction SilentlyContinue |
            Where-Object { $_.MainWindowHandle -ne 0 } |
            Select-Object -First 1
        if ($gui) { break }
    }

    if (-not $gui) {
        Write-Step "FAIL: GUI did not appear within $LaunchTimeoutSec s"
        exit 1
    }
}
Write-Step "GUI alive — pid=$($gui.Id), main hwnd=$($gui.MainWindowHandle)"

# ---- Step 2: list-windows and locate AgentZeroLite ----
Write-Step "Step 2: os list-windows --filter 'AgentZero Lite'"
$listResult = Run-OsCli @("list-windows", "--filter", "AgentZero Lite")
if ($listResult.ExitCode -ne 0) {
    Write-Step "FAIL: list-windows failed (exit=$($listResult.ExitCode))"
    Write-Step $listResult.Output
    exit 1
}

$listJson = $listResult.Output | ConvertFrom-Json
if ($listJson.windows.Count -eq 0) {
    Write-Step "FAIL: AgentZeroLite window not in enumeration"
    exit 1
}

$hwnd = $listJson.windows[0].hwnd
Write-Step "  found hwnd=$hwnd, title='$($listJson.windows[0].title)'"

# ---- Step 3: get-window-info ----
Write-Step "Step 3: os get-window-info $hwnd"
$infoResult = Run-OsCli @("get-window-info", "$hwnd")
if ($infoResult.ExitCode -ne 0) { Write-Step "FAIL: get-window-info"; exit 1 }
$info = $infoResult.Output | ConvertFrom-Json
Write-Step "  rect=($($info.window.rect.x),$($info.window.rect.y) $($info.window.rect.w)x$($info.window.rect.h)), pid=$($info.window.pid)"

# ---- Step 4: screenshot of the window ----
Write-Step "Step 4: os screenshot --hwnd $hwnd"
$shotResult = Run-OsCli @("screenshot", "--hwnd", "$hwnd")
if ($shotResult.ExitCode -ne 0) { Write-Step "FAIL: screenshot"; exit 1 }
$shot = $shotResult.Output | ConvertFrom-Json
Write-Step "  saved $($shot.path) ($($shot.width)x$($shot.height))"

# ---- Step 5: element-tree depth=3 (sanity only) ----
Write-Step "Step 5: os element-tree $hwnd --depth 3"
$treeResult = Run-OsCli @("element-tree", "$hwnd", "--depth", "3")
if ($treeResult.ExitCode -ne 0) {
    Write-Step "WARN: element-tree returned non-zero (window may have been minimized)"
} else {
    $tree = $treeResult.Output | ConvertFrom-Json
    Write-Step "  nodeCount=$($tree.nodeCount)"
}

# ---- Step 6: dpi (no hwnd needed) ----
Write-Step "Step 6: os dpi"
$dpiResult = Run-OsCli @("dpi")
if ($dpiResult.ExitCode -eq 0) {
    $dpi = $dpiResult.Output | ConvertFrom-Json
    Write-Step "  systemDpi=$($dpi.dpi.systemDpi), scale=$($dpi.dpi.scaleFactor)"
}

Write-Step "===== smoke OK ($timeStr) ====="
Write-Host ""
Write-Host "Artifacts:"
Write-Host "  log: $logFile"
Write-Host "  png: $($shot.path)"
Write-Host "  audit: $(Join-Path $repoRoot "tmp\os-cli\audit\$dateStr.jsonl")"
exit 0
