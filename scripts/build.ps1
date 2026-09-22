[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$localDotNet = Join-Path $env:LOCALAPPDATA 'Yanwai\dotnet\dotnet.exe'
$dotnet = if (Test-Path $localDotNet) { $localDotNet } else { (Get-Command dotnet -ErrorAction Stop).Source }

Push-Location $repositoryRoot
try {
    & $dotnet build 'Yanwai.sln' --nologo --verbosity minimal
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    Pop-Location
}
