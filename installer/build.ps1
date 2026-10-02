# Publishes TermHost and wraps it in an MSI: artifacts\TermHost-<version>-x64.msi
# Needs the WiX toolset:  dotnet tool install --global wix
param([string]$Version = '1.0.0')

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root 'artifacts\publish'
$msi = Join-Path $root "artifacts\TermHost-$Version-x64.msi"

# Self-contained: the installed app needs neither .NET nor the Windows App SDK on the machine.
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish (Join-Path $root 'TermHost.csproj') -c Release -f net10.0-windows10.0.19041.0 -r win-x64 `
    --self-contained -p:WindowsAppSDKSelfContained=true -p:ApplicationDisplayVersion=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

wix build (Join-Path $PSScriptRoot 'TermHost.wxs') -arch x64 -d "PublishDir=$publish" -d "Version=$Version" -o $msi
if ($LASTEXITCODE -ne 0) { throw 'wix build failed' }

Get-Item $msi | Select-Object FullName, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
