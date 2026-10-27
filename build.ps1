# Builds PDM in one go (Windows):
#   powershell -ExecutionPolicy Bypass -File build.ps1
# Output:
#   dist\PDM.exe              the app (self-contained, single file)
#   dist\PDM-Setup.exe        the same file; its name makes it start in setup mode
#   dist\extension\opera      unpacked Opera/Chrome/Edge extension (also embedded in PDM.exe)
#   dist\extension\firefox    pdm-firefox.xpi (also embedded; signed by Mozilla when AMO_JWT_ISSUER/AMO_JWT_SECRET are set)
#   dist\PDM.exe.sha256       checksum used by the automatic updater
# Steps: icons (if missing) -> native host -> extension zip -> tests -> app.
# -Version 1.1.42 stamps the app and the extension (CI passes the run number); local builds are 1.0.0.
param(
    [switch]$SkipTests,
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
Set-Location $Root

function Step([string]$text) { Write-Host ""; Write-Host "==> $text" -ForegroundColor Cyan }

# ---- dotnet ----
$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $dotnet = $cmd.Source } else { throw ".NET 10 SDK not found (looked in %USERPROFILE%\.dotnet and PATH). See BUILD.md." }
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
Write-Host "dotnet: $dotnet ($(& $dotnet --version))  -  PDM version $Version"

function Invoke-Dotnet {
    & $dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($args -join ' ') failed (exit $LASTEXITCODE)" }
}

$build = Join-Path $Root 'build'
$dist = Join-Path $Root 'dist'
New-Item -ItemType Directory -Force $build | Out-Null

# ---- icons ----
$icons = @('src\PDM\Assets\pdm.ico', 'src\PDM\Assets\pdm-active.ico', 'extension\opera\icons\icon128.png', 'extension\opera\icons\icon16-off.png')
if ($icons | Where-Object { -not (Test-Path (Join-Path $Root $_)) }) {
    Step 'Drawing icons (tools\make-icons.ps1)'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Root 'tools\make-icons.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'make-icons.ps1 failed' }
}

# ---- native host ----
Step 'Publishing native host -> build\host'
$hostOut = Join-Path $build 'host'
if (Test-Path $hostOut) { Remove-Item -Recurse -Force $hostOut }
Invoke-Dotnet publish (Join-Path $Root 'src\PDM.NativeHost\PDM.NativeHost.csproj') -c Release -o $hostOut "-p:Version=$Version"
$hostExe = Join-Path $hostOut 'PDM.NativeHost.exe'
if (-not (Test-Path $hostExe)) { throw "Native host was not produced: $hostExe" }

# ---- extension zip ----
Step 'Packing extension -> build\extension.zip'
$extSrc = Join-Path $Root 'extension\opera'
$extZip = Join-Path $build 'extension.zip'
if (Test-Path $extZip) { Remove-Item -Force $extZip }
# Work on a copy whose manifest carries this build's version: PDM re-extracts the extension when the version
# changes, and the extension reloads itself when PDM reports a newer one.
$extStage = Join-Path $build 'extension'
if (Test-Path $extStage) { Remove-Item -Recurse -Force $extStage }
Copy-Item -Recurse $extSrc $extStage
$manifestPath = Join-Path $extStage 'manifest.json'
$manifest = [System.IO.File]::ReadAllText($manifestPath)
$manifest = [regex]::new('"version"\s*:\s*"[^"]*"').Replace($manifest, '"version": "' + $Version + '"', 1)
[System.IO.File]::WriteAllText($manifestPath, $manifest, (New-Object System.Text.UTF8Encoding $false))
$extSrc = $extStage
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
function New-ZipFromFolder([string]$folder, [string]$target) {
    if (Test-Path $target) { Remove-Item -Force $target }
    # Entries are written one by one with '/' separators (works on every PowerShell/.NET version).
    $zip = [System.IO.Compression.ZipFile]::Open($target, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        Get-ChildItem -Path $folder -Recurse -File | ForEach-Object {
            $rel = $_.FullName.Substring($folder.Length + 1).Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $rel, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $zip.Dispose() }
}
New-ZipFromFolder $extSrc $extZip

# ---- Firefox add-on ----
# Same files, Firefox's own manifest (event page instead of service worker, add-on ID instead of key).
Step 'Packing Firefox add-on -> build\firefox.xpi'
$ffStage = Join-Path $build 'firefox'
if (Test-Path $ffStage) { Remove-Item -Recurse -Force $ffStage }
Copy-Item -Recurse $extStage $ffStage
$ffManifest = [System.IO.File]::ReadAllText((Join-Path $Root 'extension\firefox\manifest.json'))
$ffManifest = [regex]::new('"version"\s*:\s*"[^"]*"').Replace($ffManifest, '"version": "' + $Version + '"', 1)
[System.IO.File]::WriteAllText((Join-Path $ffStage 'manifest.json'), $ffManifest, (New-Object System.Text.UTF8Encoding $false))
$ffXpi = Join-Path $build 'firefox.xpi'
New-ZipFromFolder $ffStage $ffXpi
# Release Firefox keeps only add-ons signed by Mozilla installed. With an addons.mozilla.org API key (unlisted:
# not published, only signed) the build signs it; without one it stays unsigned (temporary loading only).
if ($env:AMO_JWT_ISSUER -and $env:AMO_JWT_SECRET) {
    Step 'Signing the Firefox add-on (addons.mozilla.org, unlisted)'
    $signedDir = Join-Path $build 'firefox-signed'
    if (Test-Path $signedDir) { Remove-Item -Recurse -Force $signedDir }
    try {
        & npx --yes web-ext@10.7.0 sign --channel unlisted --source-dir $ffStage --artifacts-dir $signedDir `
            --api-key $env:AMO_JWT_ISSUER --api-secret $env:AMO_JWT_SECRET --approval-timeout 900000
        $signedXpi = Get-ChildItem $signedDir -Filter *.xpi -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($LASTEXITCODE -eq 0 -and $signedXpi) {
            Copy-Item -Force $signedXpi.FullName $ffXpi
            Write-Host "Signed by Mozilla: $($signedXpi.Name)"
        } else {
            Write-Warning "Signing did not produce a file (exit $LASTEXITCODE); the unsigned add-on is used."
        }
    } catch {
        Write-Warning "Signing failed: $($_.Exception.Message); the unsigned add-on is used."
    }
} else {
    Write-Host 'AMO_JWT_ISSUER / AMO_JWT_SECRET not set: the Firefox add-on stays unsigned.'
}

# ---- tests ----
if (-not $SkipTests) {
    Step 'Running tests'
    Invoke-Dotnet build (Join-Path $Root 'tests\PDM.Tests\PDM.Tests.csproj') -c Release
    Invoke-Dotnet run --no-build --project (Join-Path $Root 'tests\PDM.Tests\PDM.Tests.csproj') -c Release -- $hostExe
} else {
    Write-Host 'Tests skipped (-SkipTests).' -ForegroundColor Yellow
}

# ---- app ----
Step 'Publishing PDM -> dist'
if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
$appOut = Join-Path $build 'app'
if (Test-Path $appOut) { Remove-Item -Recurse -Force $appOut }
Invoke-Dotnet publish (Join-Path $Root 'src\PDM\PDM.csproj') -c Release -o $appOut "-p:Version=$Version"
New-Item -ItemType Directory -Force $dist | Out-Null
Copy-Item (Join-Path $appOut 'PDM.exe') (Join-Path $dist 'PDM.exe')
Copy-Item (Join-Path $appOut 'PDM.exe') (Join-Path $dist 'PDM-Setup.exe')
$extDist = Join-Path $dist 'extension\opera'
New-Item -ItemType Directory -Force $extDist | Out-Null
Copy-Item -Recurse -Force (Join-Path $extSrc '*') $extDist
$ffDist = Join-Path $dist 'extension\firefox'
New-Item -ItemType Directory -Force $ffDist | Out-Null
Copy-Item -Force $ffXpi (Join-Path $ffDist 'pdm-firefox.xpi')
# sha256sum format; the updater refuses a download that does not match.
$exeHash = (Get-FileHash (Join-Path $dist 'PDM.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText((Join-Path $dist 'PDM.exe.sha256'), "$exeHash  PDM.exe`n", (New-Object System.Text.UTF8Encoding $false))

Step 'Done'
Get-ChildItem $dist -File | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    '{0,-18} {1,8:N1} MB  sha256 {2}' -f $_.Name, ($_.Length / 1MB), $hash
}
Write-Host ""
Write-Host "Install: run dist\PDM-Setup.exe. Browser extension: see README.md." -ForegroundColor Green
