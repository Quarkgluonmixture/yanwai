[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$localDotNet = Join-Path $env:LOCALAPPDATA 'Yanwai\dotnet\dotnet.exe'
$dotnet = if (Test-Path $localDotNet) { $localDotNet } else { (Get-Command dotnet -ErrorAction Stop).Source }

if ([string]::IsNullOrWhiteSpace($env:TYPESAFE_API_KEY)) {
    throw 'TYPESAFE_API_KEY is not set. Set it for this user, then start a new shell.'
}

& $dotnet run --project (Join-Path $repositoryRoot 'src\Yanwai.Panel\Yanwai.Panel.csproj')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
