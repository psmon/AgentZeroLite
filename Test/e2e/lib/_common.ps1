# _common.ps1 — shared helpers for the AgentZero Lite E2E suite (Test/e2e).
#
# Reusable across tiers: exe resolution, CLI invocation, a tiny assert/test
# framework, artifact/logging, and GUI launch + os-cli screenshot helpers.
# Dot-source this from a tier script:  . "$PSScriptRoot\..\lib\_common.ps1"

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ── Paths ────────────────────────────────────────────────────────────────────
# _common.ps1 lives at Test/e2e/lib → repo root is three levels up.
$script:RepoRoot   = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path
$script:E2eRoot    = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$script:ArtifactRoot = Join-Path $script:E2eRoot "_artifacts"

function Get-Exe {
    param([string]$Configuration = "Debug")
    $exe = Join-Path $script:RepoRoot "Project\AgentZeroWpf\bin\$Configuration\net10.0-windows\AgentZeroLite.exe"
    if (-not (Test-Path $exe)) {
        throw "AgentZeroLite.exe not found ($exe). Build: dotnet build Project/AgentZeroWpf/AgentZeroWpf.csproj -c $Configuration"
    }
    return $exe
}

function New-RunDir {
    param([string]$Tier)
    $stamp = Get-Date -Format "yyyy-MM-dd_HH-mm-ss"
    $dir = Join-Path $script:ArtifactRoot "$Tier-$stamp"
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    return $dir
}

# ── CLI invocation ───────────────────────────────────────────────────────────
# AgentZeroLite.exe is a WinExe (GUI subsystem): `& $exe` neither captures its
# console output nor sets $LASTEXITCODE. Mirror AgentZeroLite.ps1 — Start-Process
# -Wait -PassThru — but redirect stdout/stderr to temp files so we can capture
# them (the CLI's AttachOrAllocConsole picks up the redirected handles).
function Invoke-Cli {
    param(
        [Parameter(Mandatory)] [string]$Exe,
        [Parameter(Mandatory)] [string[]]$CliArgs,
        [int]$TimeoutSec = 30
    )
    $all = @("-cli") + $CliArgs
    # Start-Process does NOT auto-quote array elements containing spaces, so
    # build a single properly-quoted command line ourselves (else e.g.
    # "every 30m" splits into two tokens).
    $argString = ($all | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' '
    $outFile = [System.IO.Path]::GetTempFileName()
    $errFile = [System.IO.Path]::GetTempFileName()
    try {
        $p = Start-Process -FilePath $Exe -ArgumentList $argString -PassThru `
                -RedirectStandardOutput $outFile -RedirectStandardError $errFile
        if (-not $p.WaitForExit($TimeoutSec * 1000)) {
            try { $p.Kill() } catch {}
            return [pscustomobject]@{ Output = "TIMEOUT after ${TimeoutSec}s"; ExitCode = 124 }
        }
        $out = (Get-Content -Path $outFile -Raw -ErrorAction SilentlyContinue)
        $err = (Get-Content -Path $errFile -Raw -ErrorAction SilentlyContinue)
        $combined = (@($out, $err) | Where-Object { $_ }) -join "`n"
        return [pscustomobject]@{
            Output   = ($combined ?? "").Trim()
            ExitCode = $p.ExitCode
        }
    } finally {
        Remove-Item -Path $outFile, $errFile -Force -ErrorAction SilentlyContinue
    }
}

# ── Tiny test framework ──────────────────────────────────────────────────────
$script:Results = New-Object System.Collections.ArrayList

function Test-Case {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][scriptblock]$Body)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        & $Body
        $sw.Stop()
        [void]$script:Results.Add([pscustomobject]@{ Name = $Name; Ok = $true; Ms = $sw.ElapsedMilliseconds; Error = $null })
        Write-Host ("  [PASS] {0}  ({1} ms)" -f $Name, $sw.ElapsedMilliseconds) -ForegroundColor Green
    } catch {
        $sw.Stop()
        [void]$script:Results.Add([pscustomobject]@{ Name = $Name; Ok = $false; Ms = $sw.ElapsedMilliseconds; Error = "$_" })
        Write-Host ("  [FAIL] {0}  — {1}" -f $Name, $_) -ForegroundColor Red
    }
}

function Assert-Exit0   { param($Result) if ($Result.ExitCode -ne 0) { throw "exit=$($Result.ExitCode); output: $($Result.Output)" } }
function Assert-Contains { param([string]$Haystack, [string]$Needle) if ($Haystack -notmatch [regex]::Escape($Needle)) { throw "expected to contain '$Needle'; got: $Haystack" } }
function Assert-True     { param([bool]$Cond, [string]$Msg = "assertion failed") if (-not $Cond) { throw $Msg } }

function Write-Summary {
    param([string]$Suite, [string]$RunDir)
    $pass = @($script:Results | Where-Object Ok).Count
    $fail = @($script:Results | Where-Object { -not $_.Ok }).Count
    Write-Host ""
    Write-Host ("===== {0}: {1} passed, {2} failed =====" -f $Suite, $pass, $fail) -ForegroundColor Cyan
    if ($RunDir) {
        $reportPath = Join-Path $RunDir "report.json"
        $script:Results | ConvertTo-Json -Depth 4 | Set-Content -Path $reportPath -Encoding UTF8
        Write-Host "  report: $reportPath"
    }
    return $fail
}

# ── GUI helpers ──────────────────────────────────────────────────────────────
function Ensure-Gui {
    param([Parameter(Mandatory)][string]$Exe, [int]$TimeoutSec = 20)
    $gui = Get-Process -Name "AgentZeroLite" -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if ($gui) { return $gui }

    Start-Process -FilePath $Exe | Out-Null
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $gui = Get-Process -Name "AgentZeroLite" -ErrorAction SilentlyContinue |
            Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
        if ($gui) { return $gui }
    }
    throw "GUI did not appear within ${TimeoutSec}s"
}

function Get-AppWindowHwnd {
    param([Parameter(Mandatory)][string]$Exe)
    $r = Invoke-Cli -Exe $Exe -CliArgs @("os","list-windows","--filter","AgentZero Lite")
    Assert-Exit0 $r
    $json = $r.Output | ConvertFrom-Json
    if (-not $json.windows -or @($json.windows).Count -eq 0) { throw "AgentZeroLite window not enumerated" }
    return @($json.windows)[0].hwnd
}

function Save-AppScreenshot {
    param([Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][int]$Hwnd, [string]$RunDir)
    $r = Invoke-Cli -Exe $Exe -CliArgs @("os","screenshot","--hwnd","$Hwnd")
    Assert-Exit0 $r
    $shot = $r.Output | ConvertFrom-Json
    if ($RunDir -and (Test-Path $shot.path)) {
        Copy-Item $shot.path (Join-Path $RunDir (Split-Path $shot.path -Leaf)) -Force
    }
    return $shot
}
