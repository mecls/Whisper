# Publishes Spit.App self-contained for win-x64 and packs it with Velopack into windows/Releases/Spit-Setup.exe
# (prd-spit-mac-windows.md rules 8, 44). Run from anywhere: pwsh windows/scripts/pack.ps1
param(
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
$publish = Join-Path $root 'windows/publish'
$out = Join-Path $root 'windows/Releases'

Remove-Item -Recurse -Force $publish, $out -ErrorAction SilentlyContinue

dotnet publish (Join-Path $root 'windows/Spit.App/Spit.App.csproj') -c $Configuration -r win-x64 --self-contained -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
if (-not (Test-Path (Join-Path $publish 'Spit.exe'))) { throw 'publish produced no Spit.exe' }

# Pinned to the Velopack library version in Spit.App.csproj; `update` installs it when missing.
dotnet tool update --global vpk --version 1.2.0
if ($LASTEXITCODE -ne 0) { throw "installing vpk failed ($LASTEXITCODE)" }

vpk pack `
    --packId Spit `
    --packVersion $version `
    --packTitle Spit `
    --packAuthors Miraside `
    --packDir $publish `
    --mainExe Spit.exe `
    --icon (Join-Path $root 'windows/Spit.App/Assets/Spit.ico') `
    --outputDir $out
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed ($LASTEXITCODE)" }

# Release assets carry no version and one fixed name, so latest/download URLs keep working (rule 8).
# Velopack may include the channel in the file name (Spit-win-Setup.exe); normalise it.
$setup = Get-ChildItem -Path $out -Filter '*Setup.exe' | Select-Object -First 1
if ($null -eq $setup) { throw 'vpk produced no Setup.exe' }
$target = Join-Path $out 'Spit-Setup.exe'
if ($setup.FullName -ne $target) { Move-Item -Force $setup.FullName $target }

Write-Output $target
