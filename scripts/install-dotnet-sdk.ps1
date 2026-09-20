[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$installDirectory = Join-Path $env:LOCALAPPDATA 'WeChatJevHud\dotnet'
$installer = Join-Path $env:TEMP 'dotnet-install.ps1'

Invoke-WebRequest -UseBasicParsing 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
& $installer -Channel 8.0 -Quality GA -InstallDir $installDirectory -NoPath
Write-Host "Installed the local .NET 8 SDK at $installDirectory"
