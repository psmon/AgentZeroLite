# agent-one.ps1
# Run the Debug build of agent-one straight from the source tree: builds it if
# it is missing or out of date, then hands every argument to the binary and
# returns its exit code untouched.
#
# Usage:
#   .\agent-one.ps1 run "hello" --provider echo
#   .\agent-one.ps1 tui
#   .\agent-one.ps1 -Rebuild tools list      # force a build first
#   .\agent-one.ps1 -NoBuild --version       # never build, fail if missing
#
# There are no declared parameters on purpose. agent-one's own flags include
# -r, -p, -m and -v, and PowerShell would bind those to any parameter whose
# name starts with the same letter (-r -> -Rebuild) before the binary ever saw
# them. So the script takes $args raw and pulls its own two switches out by hand.

$ErrorActionPreference = 'Stop'

$projectDir  = $PSScriptRoot
$projectFile = Join-Path $projectDir 'AgentOne.csproj'
$exeName     = if ($IsWindows -eq $false) { 'agent-one' } else { 'agent-one.exe' }
$exePath     = Join-Path $projectDir "bin/Debug/net10.0/$exeName"

# --- pull our switches out of the pass-through arguments -------------------

$cliArgs = @()
$forceBuild = $false
$skipBuild  = $false

foreach ($arg in $args) {
    switch -Regex ($arg) {
        '^(-Rebuild|--rebuild)$' { $forceBuild = $true; continue }
        '^(-NoBuild|--no-build)$' { $skipBuild = $true; continue }
        default { $cliArgs += $arg }
    }
}

# --- decide whether to build ------------------------------------------------

function Get-NewestSourceTime {
    # Only the inputs that actually change the binary. bin/ and obj/ are skipped
    # or the build output would always look newer than itself.
    Get-ChildItem -Path $projectDir -Recurse -File -Include *.cs, *.csproj, version.txt |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj|packaging)[\\/]' } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1 -ExpandProperty LastWriteTimeUtc
}

$exists = Test-Path $exePath
$reason = $null

if ($forceBuild) {
    $reason = 'rebuild requested'
}
elseif (-not $exists) {
    $reason = 'no Debug build yet'
}
else {
    $newestSource = Get-NewestSourceTime
    $builtAt = (Get-Item $exePath).LastWriteTimeUtc
    if ($newestSource -gt $builtAt) {
        $reason = 'sources changed since the last build'
    }
}

if ($reason -and $skipBuild) {
    if (-not $exists) {
        Write-Host "agent-one.ps1: $reason, and -NoBuild was given." -ForegroundColor Red
        Write-Host "agent-one.ps1: build it with  dotnet build `"$projectFile`" -c Debug" -ForegroundColor Red
        exit 1
    }
    Write-Host "agent-one.ps1: $reason - running the existing binary anyway (-NoBuild)." -ForegroundColor DarkYellow
    $reason = $null
}

if ($reason) {
    Write-Host "agent-one.ps1: building ($reason)..." -ForegroundColor DarkCyan

    # Pin the version instead of letting the MSBuild target bump version.txt:
    # a convenience wrapper should not dirty a tracked file every time it runs.
    $version = (Get-Content (Join-Path $projectDir 'version.txt') -Raw).Trim()

    dotnet build $projectFile -c Debug --nologo -v quiet `
        -p:SkipAutoBumpVersion=true -p:Version=$version

    if ($LASTEXITCODE -ne 0) {
        Write-Host "agent-one.ps1: build failed ($LASTEXITCODE)." -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

if (-not (Test-Path $exePath)) {
    Write-Host "agent-one.ps1: the build reported success but $exePath is not there." -ForegroundColor Red
    exit 1
}

# --- run --------------------------------------------------------------------

# Called directly, not through Start-Process: the TUI needs the real console
# (Start-Process -NoNewWindow still hands the child a fresh stdio pair, and a
# full-screen page then has no terminal to draw on).
& $exePath @cliArgs
exit $LASTEXITCODE
