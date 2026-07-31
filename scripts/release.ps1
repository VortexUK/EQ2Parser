# Build, pack, and (optionally) publish an EQ2Parser release.
#
#   ./scripts/release.ps1 -Version 0.1.0            # build + pack only
#   ./scripts/release.ps1 -Version 0.1.0 -Publish   # + create the GitHub release
#
# Publishing needs an authenticated gh CLI (the token is borrowed from it).
# Releases are marked pre-release; the in-app updater follows them.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [switch]$Publish
)
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    Write-Host 'Installing the vpk tool…'
    dotnet tool install -g vpk
    if ($LASTEXITCODE -ne 0) { throw 'vpk install failed' }
}

Write-Host "Publishing v$Version (self-contained win-x64)…"
dotnet publish src/EQ2Parser.App -c Release -r win-x64 --self-contained true `
    -o publish /p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Write-Host 'Packing with Velopack…'
vpk pack --packId EQ2Parser --packVersion $Version --packDir publish `
    --mainExe EQ2Parser.App.exe --packTitle EQ2Parser --outputDir Releases
if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed' }

if ($Publish) {
    Write-Host 'Uploading to GitHub Releases (pre-release)…'
    vpk upload github --repoUrl https://github.com/VortexUK/EQ2Parser `
        --publish --pre --releaseName "EQ2Parser $Version" --tag "v$Version" `
        --token (gh auth token) --outputDir Releases
    if ($LASTEXITCODE -ne 0) { throw 'vpk upload failed' }
    Write-Host "Released v$Version — installer: EQ2Parser-win-Setup.exe on the release page."
}
else {
    Write-Host "Packed. Local installer: Releases/EQ2Parser-win-Setup.exe (use -Publish to release)."
}
