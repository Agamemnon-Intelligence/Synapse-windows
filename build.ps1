<#
.SYNOPSIS
  Builds Synapse for Windows: the app, the synapse CLI, a portable zip and (with Inno Setup) an installer.
.EXAMPLE
  ./build.ps1                 # x64
  ./build.ps1 -Arch arm64
#>
param(
    [ValidateSet('x64', 'arm64')] [string] $Arch = 'x64',
    [string] $Configuration = 'Release',
    [switch] $SkipTests,
    [switch] $SkipInstaller
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version | Select-Object -First 1
$rid = "win-$Arch"
$out = Join-Path $PSScriptRoot "dist/Synapse-$rid"
Write-Host "Synapse $version ($rid)" -ForegroundColor Magenta

if (-not $SkipTests) { dotnet test tests/Synapse.Core.Tests -c $Configuration; if ($LASTEXITCODE) { exit $LASTEXITCODE } }

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish src/Synapse.App -c $Configuration -r $rid --self-contained -o $out -p:PublishReadyToRun=true
if ($LASTEXITCODE) { exit $LASTEXITCODE }
# The CLI lives in cli\ because Windows file names ignore case (synapse.exe would replace Synapse.exe).
dotnet publish src/Synapse.Cli -c $Configuration -r $rid --self-contained -o (Join-Path $out 'cli')
if ($LASTEXITCODE) { exit $LASTEXITCODE }
Get-ChildItem $out -Include *.pdb, *.xml -Recurse | Remove-Item -Force

$zip = Join-Path $PSScriptRoot "dist/Synapse-$version-$rid-portable.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "$out/*" -DestinationPath $zip
Write-Host "Portable: $zip" -ForegroundColor Green

if ($SkipInstaller) { return }
$iscc = $null
$found = Get-Command iscc.exe -ErrorAction SilentlyContinue
if ($found) { $iscc = $found.Source }
if (-not $iscc) { $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1 }
if (-not $iscc) { Write-Warning 'Inno Setup 6 not found; skipping the installer (winget install JRSoftware.InnoSetup).'; return }
& $iscc "/DAppVersion=$version" "/DArch=$Arch" "/DSourceDir=$out" "/DOutputDir=$(Join-Path $PSScriptRoot 'dist')" installer/Synapse.iss
if ($LASTEXITCODE) { exit $LASTEXITCODE }
Write-Host "Installer: dist/Synapse-$version-$rid-setup.exe" -ForegroundColor Green
