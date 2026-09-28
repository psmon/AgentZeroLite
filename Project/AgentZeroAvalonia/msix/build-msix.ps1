<#
.SYNOPSIS
  Builds the Microsoft Store MSIX of the Avalonia host (win-x64).

.DESCRIPTION
  dotnet publish (self-contained win-x64) → stage next to the manifest and tile assets →
  fill the identity tokens → makepri → makeappx pack. The .msix it writes is what gets
  uploaded to Partner Center; the Store re-signs it, so a Store build needs no certificate.

  Two local-test modes, because an unsigned package cannot be installed by double-click:
    -Register   registers the staged folder in place (Add-AppxPackage -Register). Needs
                Windows "Developer Mode" on, installs nothing permanent — Remove-AppxPackage
                undoes it. The quickest way to see the packaged app, its alias and the
                unvirtualized AppData behave.
    -Sign       signs the .msix with a self-signed certificate whose subject is -Publisher,
                for installing the file itself on a test machine. That machine must trust
                the certificate (Cert:\LocalMachine\TrustedPeople — admin).

  Identity values come from Partner Center → the app → Product management → Product
  identity. Until the name is reserved, the defaults build a dev package that can be
  registered locally but not uploaded.

.EXAMPLE
  # Store upload build — values from .secret/msstore-identity.json, or passed explicitly
  ./build-msix.ps1 -IdentityName "12345Webnori.AgentZeroLite" -Publisher "CN=ABCD1234-..." -PublisherDisplayName "webnori"

.EXAMPLE
  # Local smoke test in Developer Mode
  ./build-msix.ps1 -Register
#>
[CmdletBinding()]
param(
    [string]$IdentityName = "AgentZeroLite.Dev",
    [string]$Publisher = "CN=AgentZeroLite-Dev",
    [string]$PublisherDisplayName = "AgentZero Lite (dev)",
    [string]$DisplayName = "AgentZero Lite",
    # Four-part MSIX version. Empty: derived from Project/AgentZeroWpf/version.txt (see below).
    [string]$Version = "",
    [string]$OutDir = "",
    [switch]$Register,
    [switch]$Sign
)

$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$project = Join-Path $here "..\AgentZeroAvalonia.csproj" | Resolve-Path
$repo = Join-Path $here "..\..\.." | Resolve-Path
if (-not $OutDir) { $OutDir = Join-Path $repo "publish-msix" }

# ── identity ─────────────────────────────────────────────────────────────────
# Partner Center's values live in .secret/msstore-identity.json (git-ignored) so the Store
# build needs no arguments. Values passed on the command line win; an empty field in the
# file keeps the dev default. -Register always builds the dev identity: the real one is for
# the upload, and registering it locally would collide with a Store install of the app.
$identityFile = Join-Path $repo ".secret\msstore-identity.json"
if (-not $Register -and (Test-Path $identityFile)) {
    $id = Get-Content $identityFile -Raw | ConvertFrom-Json
    if ($id.IdentityName -and -not $PSBoundParameters.ContainsKey('IdentityName')) { $IdentityName = $id.IdentityName }
    if ($id.Publisher -and -not $PSBoundParameters.ContainsKey('Publisher')) { $Publisher = $id.Publisher }
    if ($id.PublisherDisplayName -and -not $PSBoundParameters.ContainsKey('PublisherDisplayName')) { $PublisherDisplayName = $id.PublisherDisplayName }
    if ($id.IdentityName) { Write-Host "== identity from .secret\msstore-identity.json" }
}
if ($Publisher -notmatch '^CN=') { throw "Publisher must be the full 'CN=…' string from Partner Center, got '$Publisher'" }
# A -Register build is installed *in place*: its stage folder becomes the package's install
# location, locked while the app runs. The upload build gets its own folder so rebuilding
# it never has to delete a registered, running copy.
$stage = Join-Path $OutDir ($(if ($Register) { "stage-dev" } else { "stage" }))

# ── version ──────────────────────────────────────────────────────────────────
# The Store rules: four parts, the first cannot be 0, the last must be 0 (reserved for the
# Store). The app is 0.x, so the MSIX major is the app's major + 1: 0.25.0 → 1.25.0.0, and a
# later 1.0.0 → 2.0.0.0. The mapping is monotonic, which is the property the Store needs —
# it always serves the highest version, and a lower one would never reach anyone.
if (-not $Version) {
    $app = (Get-Content (Join-Path $repo "Project\AgentZeroWpf\version.txt") -Raw).Trim()
    $p = $app.Split('.')
    if ($p.Count -ne 3) { throw "version.txt is '$app' — expected major.minor.patch" }
    $Version = "{0}.{1}.{2}.0" -f ([int]$p[0] + 1), [int]$p[1], [int]$p[2]
}
if ($Version -notmatch '^[1-9]\d{0,4}\.\d{1,5}\.\d{1,5}\.0$') { throw "MSIX version '$Version' breaks the Store rules (n.n.n.0, first part not 0)" }

# ── tools ────────────────────────────────────────────────────────────────────
function Find-SdkTool([string]$name) {
    $hit = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\$name" -ErrorAction SilentlyContinue |
        Sort-Object { [version]($_.Directory.Parent.Name) } -Descending | Select-Object -First 1
    if (-not $hit) { throw "$name not found — install the Windows 10/11 SDK (Visual Studio Installer → Individual components → Windows SDK)" }
    $hit.FullName
}
$makeappx = Find-SdkTool "makeappx.exe"
$makepri = Find-SdkTool "makepri.exe"

Write-Host "== AgentZero Lite MSIX  $Version  ($IdentityName / $Publisher)"

# ── publish + stage ──────────────────────────────────────────────────────────
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null

dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $stage
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Copy-Item (Join-Path $here "Assets") (Join-Path $stage "Assets") -Recurse -Force
Remove-Item (Join-Path $stage "Assets\StoreListing*.png") -ErrorAction SilentlyContinue   # listing art, not package content

$manifest = Get-Content (Join-Path $here "Package.appxmanifest") -Raw
$manifest = $manifest.Replace("__IDENTITY_NAME__", $IdentityName).
    Replace("__PUBLISHER__", $Publisher).
    Replace("__PUBLISHER_DISPLAY_NAME__", [Security.SecurityElement]::Escape($PublisherDisplayName)).
    Replace("__DISPLAY_NAME__", [Security.SecurityElement]::Escape($DisplayName)).
    Replace("__VERSION__", $Version)
if ($manifest -match "__[A-Z_]+__") { throw "unfilled token in the manifest: $($Matches[0])" }
[IO.File]::WriteAllText((Join-Path $stage "AppxManifest.xml"), $manifest, [Text.UTF8Encoding]::new($false))

# resources.pri: lets the shell pick the targetsize/scale variants of the tile assets.
$priConfig = Join-Path $OutDir "priconfig.xml"
& $makepri createconfig /cf $priConfig /dq en-US /o | Out-Null
& $makepri new /pr $stage /cf $priConfig /of (Join-Path $stage "resources.pri") /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makepri failed" }

# ── pack ─────────────────────────────────────────────────────────────────────
$msix = Join-Path $OutDir ("AgentZeroLite_{0}_x64.msix" -f $Version)
& $makeappx pack /d $stage /p $msix /o /h SHA256 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed" }
$size = [math]::Round((Get-Item $msix).Length / 1MB, 1)
Write-Host "== packed  $msix  ($size MB)"

if ($Sign) {
    $signtool = Find-SdkTool "signtool.exe"
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $Publisher -and $_.HasPrivateKey } | Select-Object -First 1
    if (-not $cert) {
        $cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher -KeyUsage DigitalSignature `
            -FriendlyName "AgentZero Lite MSIX test" -CertStoreLocation Cert:\CurrentUser\My `
            -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
        Write-Host "== created test certificate $($cert.Thumbprint) ($Publisher)"
    }
    & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint $msix
    if ($LASTEXITCODE -ne 0) { throw "signtool failed" }
    $cer = Join-Path $OutDir "AgentZeroLite-test.cer"
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    Write-Host "== signed. On the test machine (admin): Import-Certificate '$cer' -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
}

if ($Register) {
    # In-place registration of the staged layout: no signature needed, Developer Mode is.
    Get-AppxPackage -Name $IdentityName | Remove-AppxPackage
    Add-AppxPackage -Register (Join-Path $stage "AppxManifest.xml")
    Write-Host "== registered. Start menu: '$DisplayName'; shell: AgentZeroLite.exe -cli status"
    Write-Host "   undo: Get-AppxPackage -Name $IdentityName | Remove-AppxPackage"
}
